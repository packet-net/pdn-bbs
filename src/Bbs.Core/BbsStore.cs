using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Bbs.Core;

/// <summary>
/// The SQLite-backed BBS store: messages with per-recipient rows, the BID dedup store, users,
/// partners and the per-partner forward queue. One database file (path supplied by the Host;
/// design.md: the package state dir), WAL journal mode, schema-versioned with idempotent
/// migration on open.
///
/// Status transitions (compat spec §2.2) are enforced here; permission checks
/// (<see cref="MessageRules"/>) are the caller's job. Instances are safe for use from multiple
/// threads (a single connection guarded by a lock); open multiple instances on the same path
/// for concurrent readers — WAL allows them.
/// </summary>
public sealed class BbsStore : IDisposable
{
    /// <summary>The schema version this build writes and expects.</summary>
    public const int CurrentSchemaVersion = 2;

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private BbsStore(SqliteConnection connection, string bbsCallsign, TimeProvider time, int schemaVersion)
    {
        _connection = connection;
        _time = time;
        BbsCallsign = bbsCallsign;
        SchemaVersion = schemaVersion;
    }

    /// <summary>The BBS callsign used as the auto-BID suffix (compat spec §2.3).</summary>
    public string BbsCallsign { get; }

    /// <summary>The schema version found/created on open.</summary>
    public int SchemaVersion { get; }

    internal TimeProvider Time => _time;

    /// <summary>
    /// Opens (creating and/or migrating as needed) the store at <paramref name="path"/>.
    /// Migration is idempotent: re-opening an up-to-date database is a no-op.
    /// </summary>
    public static BbsStore Open(string path, string bbsCallsign, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(bbsCallsign);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            ExecuteRaw(connection, "PRAGMA journal_mode=WAL;");
            ExecuteRaw(connection, "PRAGMA foreign_keys=ON;");
            ExecuteRaw(connection, "PRAGMA synchronous=NORMAL;");
            int version = Migrate(connection);
            return new BbsStore(connection, Callsigns.Normalize(bbsCallsign), timeProvider, version);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _connection.Dispose();

    // ---------------------------------------------------------------- messages

    /// <summary>
    /// Stores a message: assigns the next monotonic number, applies the compat-spec field
    /// limits (§1.5/§2.3/§2.4), auto-allocates the BID when absent (§2.3
    /// <c>&lt;msgno&gt;_&lt;BBSCALL&gt;</c>), writes one recipient row per addressee, and
    /// records the BID in the dedup store. Initial status N, or H when
    /// <see cref="MessageDraft.Hold"/> (§2.2).
    /// </summary>
    public Message AddMessage(MessageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        string from = Callsigns.NormalizeAddressee(draft.From);
        ArgumentException.ThrowIfNullOrWhiteSpace(from, nameof(draft));

        var recipients = new List<string>();
        foreach (string recipient in draft.Recipients)
        {
            string normalized = Callsigns.NormalizeAddressee(recipient);
            if (normalized.Length > 0 && !recipients.Contains(normalized))
            {
                recipients.Add(normalized);
            }
        }

        if (recipients.Count == 0)
        {
            throw new ArgumentException("At least one recipient is required (compat spec §1.5: TO is mandatory).", nameof(draft));
        }

        // Cc recipients (spec §3.9), deduped against the To set — a callsign that is both To and Cc
        // is kept as a To (one row per callsign; the recipients PK is (message_number,to_call)).
        var ccRecipients = new List<string>();
        foreach (string cc in draft.CcRecipients)
        {
            string normalized = Callsigns.NormalizeAddressee(cc);
            if (normalized.Length > 0 && !recipients.Contains(normalized) && !ccRecipients.Contains(normalized))
            {
                ccRecipients.Add(normalized);
            }
        }

        string? at = NormalizeAt(draft.At);
        string? bid = NormalizeBid(draft.Bid);
        string subject = draft.Subject.Length <= Message.MaxSubjectLength ? draft.Subject : draft.Subject[..Message.MaxSubjectLength];
        string? receivedFrom = draft.ReceivedFrom is null ? null : Callsigns.Normalize(draft.ReceivedFrom);
        char status = draft.Hold ? 'H' : 'N';
        long now = NowSeconds();

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();

            using (SqliteCommand insert = Command(tx,
                "INSERT INTO messages(type,status,from_call,at_bbs,bid,subject,body,received_from,created_utc) " +
                "VALUES($type,$status,$from,$at,$bid,$subject,$body,$rxfrom,$created);"))
            {
                insert.Parameters.AddWithValue("$type", draft.Type.ToCode().ToString());
                insert.Parameters.AddWithValue("$status", status.ToString());
                insert.Parameters.AddWithValue("$from", from);
                insert.Parameters.AddWithValue("$at", (object?)at ?? DBNull.Value);
                insert.Parameters.AddWithValue("$bid", bid ?? "");
                insert.Parameters.AddWithValue("$subject", subject);
                insert.Parameters.AddWithValue("$body", draft.Body.ToArray());
                insert.Parameters.AddWithValue("$rxfrom", (object?)receivedFrom ?? DBNull.Value);
                insert.Parameters.AddWithValue("$created", now);
                insert.ExecuteNonQuery();
            }

            long number;
            using (SqliteCommand rowId = Command(tx, "SELECT last_insert_rowid();"))
            {
                number = (long)rowId.ExecuteScalar()!;
            }

            if (bid is null)
            {
                bid = BidGenerator.Generate(number, BbsCallsign);
                using SqliteCommand setBid = Command(tx, "UPDATE messages SET bid=$bid WHERE number=$n;");
                setBid.Parameters.AddWithValue("$bid", bid);
                setBid.Parameters.AddWithValue("$n", number);
                setBid.ExecuteNonQuery();
            }

            foreach (string recipient in recipients)
            {
                InsertRecipient(tx, number, recipient, cc: false);
            }

            foreach (string cc in ccRecipients)
            {
                InsertRecipient(tx, number, cc, cc: true);
            }

            foreach (MessageAttachment attachment in draft.Attachments)
            {
                using SqliteCommand insertAttachment = Command(tx,
                    "INSERT INTO attachments(message_number,name,content) VALUES($n,$name,$content);");
                insertAttachment.Parameters.AddWithValue("$n", number);
                insertAttachment.Parameters.AddWithValue("$name", attachment.Name);
                insertAttachment.Parameters.AddWithValue("$content", attachment.Content.ToArray());
                insertAttachment.ExecuteNonQuery();
            }

            // BID dedup row. First-seen time and direction are preserved on conflict (the
            // lifetime anchors at first sight); the message link follows the newest live copy
            // so the personal live-copy check (§2.3) finds it.
            using (SqliteCommand insertBid = Command(tx,
                "INSERT INTO bids(bid,first_seen_utc,first_seen_from,message_number) VALUES($bid,$seen,$from,$n) " +
                "ON CONFLICT(bid) DO UPDATE SET message_number=excluded.message_number;"))
            {
                insertBid.Parameters.AddWithValue("$bid", bid);
                insertBid.Parameters.AddWithValue("$seen", now);
                insertBid.Parameters.AddWithValue("$from", (object?)receivedFrom ?? DBNull.Value);
                insertBid.Parameters.AddWithValue("$n", number);
                insertBid.ExecuteNonQuery();
            }

            tx.Commit();
            return GetMessageCore(number)!;
        }
    }

    /// <summary>Fetches one message (any status — list visibility rules live in queries/<see cref="MessageRules"/>).</summary>
    public Message? GetMessage(long number)
    {
        lock (_gate)
        {
            return GetMessageCore(number);
        }
    }

    /// <summary>
    /// Fetches one attachment's bytes by message number and exact stored name (the webmail download
    /// path), or null when no attachment with that exact name exists on that message. Matching is on
    /// the exact stored name only — no path interpretation — so a traversal-shaped name can never
    /// resolve to anything but a row whose <c>name</c> is byte-for-byte that string.
    /// </summary>
    public ReadOnlyMemory<byte>? GetAttachment(long number, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT content FROM attachments WHERE message_number=$n AND name=$name LIMIT 1;");
            cmd.Parameters.AddWithValue("$n", number);
            cmd.Parameters.AddWithValue("$name", name);
            using SqliteDataReader reader = cmd.ExecuteReader();

            // NB: an explicit null — NOT a `byte[]?`-via-ternary — because converting a null byte[]
            // to ReadOnlyMemory<byte>? yields an empty-but-present value, not a null nullable.
            if (!reader.Read())
            {
                return null;
            }

            return reader.GetFieldValue<byte[]>(0);
        }
    }

    /// <summary>Highest message number in the store ($L in the welcome banner, compat spec §1.1), or 0.</summary>
    public long GetLatestMessageNumber()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, "SELECT COALESCE(MAX(number),0) FROM messages;");
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// The L-family listing query (compat spec §1.3). Newest-first by message number unless
    /// <see cref="MessageQuery.OldestFirst"/>; H and K rows excluded unless the sysop include
    /// flags are set (held-invisible rule, §2.2).
    /// </summary>
    public IReadOnlyList<Message> ListMessages(MessageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql = new System.Text.StringBuilder(
            "SELECT m.number,m.type,m.status,m.from_call,m.at_bbs,m.bid,m.subject,m.body,m.received_from,m.created_utc,m.killed_utc FROM messages m");
        var parameters = new List<(string Name, object Value)>();

        if (query.ToCall is not null)
        {
            sql.Append(" JOIN recipients r ON r.message_number=m.number AND r.to_call=$qto");
            parameters.Add(("$qto", Callsigns.NormalizeAddressee(query.ToCall)));
        }

        sql.Append(" WHERE 1=1");

        if (!query.IncludeHeld)
        {
            sql.Append(" AND m.status<>'H'");
        }

        if (!query.IncludeKilled)
        {
            sql.Append(" AND m.status<>'K'");
        }

        if (query.Type is { } type)
        {
            sql.Append(" AND m.type=$qtype");
            parameters.Add(("$qtype", type.ToCode().ToString()));
        }

        if (query.Status is { } status)
        {
            sql.Append(" AND m.status=$qstatus");
            parameters.Add(("$qstatus", status.ToCode().ToString()));
        }

        if (query.MinNumber is { } min)
        {
            sql.Append(" AND m.number>=$qmin");
            parameters.Add(("$qmin", min));
        }

        if (query.MaxNumber is { } max)
        {
            sql.Append(" AND m.number<=$qmax");
            parameters.Add(("$qmax", max));
        }

        if (query.Since is { } since)
        {
            sql.Append(" AND m.created_utc>=$qsince");
            parameters.Add(("$qsince", since.ToUnixTimeSeconds()));
        }

        if (query.FromCall is not null)
        {
            sql.Append(" AND m.from_call=$qfrom");
            parameters.Add(("$qfrom", Callsigns.NormalizeAddressee(query.FromCall)));
        }

        if (query.AtPrefix is not null)
        {
            // "L@ matches up to the length of the input string" (compat spec §1.3).
            sql.Append(" AND m.at_bbs IS NOT NULL AND substr(m.at_bbs,1,length($qat))=$qat");
            parameters.Add(("$qat", query.AtPrefix.Trim().ToUpperInvariant()));
        }

        sql.Append(query.OldestFirst ? " ORDER BY m.number ASC" : " ORDER BY m.number DESC");

        if (query.Limit is { } limit)
        {
            sql.Append(" LIMIT $qlimit");
            parameters.Add(("$qlimit", limit));
        }

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, sql.ToString());
            foreach ((string name, object value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            var messages = new List<Message>();
            using (SqliteDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    messages.Add(ReadMessage(reader));
                }
            }

            return AttachRecipients(messages);
        }
    }

    /// <summary>
    /// Marks a message read by <paramref name="byCall"/>: stamps that recipient's read time and
    /// applies the §2.2 transition — N→Y, never overwriting K/H/F/D, and never for T messages
    /// ("T messages are not set Y on read"). Returns false when the message doesn't exist or
    /// <paramref name="byCall"/> is not an addressee.
    /// </summary>
    public bool MarkRead(long number, string byCall)
    {
        ArgumentNullException.ThrowIfNull(byCall);
        string call = Callsigns.NormalizeAddressee(byCall);
        long now = NowSeconds();

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();

            (char Type, char Status)? header = GetHeader(tx, number);
            if (header is null)
            {
                return false;
            }

            using (SqliteCommand stamp = Command(tx,
                "UPDATE recipients SET read_utc=COALESCE(read_utc,$now) WHERE message_number=$n AND to_call=$call;"))
            {
                stamp.Parameters.AddWithValue("$now", now);
                stamp.Parameters.AddWithValue("$n", number);
                stamp.Parameters.AddWithValue("$call", call);
                if (stamp.ExecuteNonQuery() == 0)
                {
                    return false; // not an addressee
                }
            }

            if (header.Value.Status == 'N' && header.Value.Type != 'T')
            {
                SetStatus(tx, number, 'Y', stampKilled: false);
            }

            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// Kills a message: status → K with the kill time stamped, so housekeeping physically
    /// removes it after the grace (compat spec §2.2/§6 "remains on disk until housekeeping
    /// removes it"). The BID dedup row is untouched — dedup survives the kill (§2.3).
    /// Returns false when missing or already K.
    /// </summary>
    public bool Kill(long number)
    {
        long now = NowSeconds();
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "UPDATE messages SET status='K', killed_utc=$now WHERE number=$n AND status<>'K';");
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$n", number);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Holds a live message (status → H). Returns false when missing, killed or already held.</summary>
    public bool HoldMessage(long number)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "UPDATE messages SET status='H' WHERE number=$n AND status NOT IN ('K','H');");
            cmd.Parameters.AddWithValue("$n", number);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Unholds a held message per the sysop UH command: "status reverts to $ if forwarding
    /// queued else N" (compat spec §1.4) — pinned by [BPQ-SRC BBSUtilities.c:3586]: $ only for
    /// a bulletin with pending forwards, N otherwise. Returns false when not held.
    /// </summary>
    public bool Unhold(long number)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();

            (char Type, char Status)? header = GetHeader(tx, number);
            if (header is null || header.Value.Status != 'H')
            {
                return false;
            }

            char next = header.Value.Type == 'B' && CountPendingForwards(tx, number) > 0 ? '$' : 'N';
            SetStatus(tx, number, next, stampKilled: false);
            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// Flags an NTS message delivered (the D command): T only, status → D (compat spec §1.3
    /// "non-T → Message %d not an NTS Message"; §2.2). K/H are not overwritten. Returns false
    /// when missing, not T, or K/H.
    /// </summary>
    public bool MarkDelivered(long number)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "UPDATE messages SET status='D' WHERE number=$n AND type='T' AND status NOT IN ('K','H');");
            cmd.Parameters.AddWithValue("$n", number);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ---------------------------------------------------------------- forwarding state

    /// <summary>
    /// Persists the routing decision: one queue row per partner. A bulletin with queued
    /// forwarding moves N → $ immediately (compat spec §2.2). Re-enqueueing an existing pair
    /// is a no-op.
    /// </summary>
    public void EnqueueForwards(long number, IEnumerable<string> partnerCalls)
    {
        ArgumentNullException.ThrowIfNull(partnerCalls);
        long now = NowSeconds();

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();

            int added = 0;
            foreach (string partnerCall in partnerCalls)
            {
                using SqliteCommand cmd = Command(tx,
                    "INSERT OR IGNORE INTO forwards(message_number,partner_call,queued_utc) VALUES($n,$p,$now);");
                cmd.Parameters.AddWithValue("$n", number);
                cmd.Parameters.AddWithValue("$p", Callsigns.Normalize(partnerCall));
                cmd.Parameters.AddWithValue("$now", now);
                added += cmd.ExecuteNonQuery();
            }

            if (added > 0)
            {
                using SqliteCommand bull = Command(tx,
                    "UPDATE messages SET status='$' WHERE number=$n AND type='B' AND status='N';");
                bull.Parameters.AddWithValue("$n", number);
                bull.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Messages pending forwarding to a partner, in forwarding priority order T, P, B (compat
    /// spec §2.1) then by number. K and H messages are excluded (H "can't be forwarded" §2.2;
    /// their queue rows survive and resume on unhold).
    /// </summary>
    public IReadOnlyList<Message> GetForwardQueue(string partnerCall)
    {
        ArgumentNullException.ThrowIfNull(partnerCall);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT m.number,m.type,m.status,m.from_call,m.at_bbs,m.bid,m.subject,m.body,m.received_from,m.created_utc,m.killed_utc " +
                "FROM forwards f JOIN messages m ON m.number=f.message_number " +
                "WHERE f.partner_call=$p AND f.forwarded_utc IS NULL AND m.status NOT IN ('K','H') " +
                "ORDER BY CASE m.type WHEN 'T' THEN 0 WHEN 'P' THEN 1 ELSE 2 END, m.number;");
            cmd.Parameters.AddWithValue("$p", Callsigns.Normalize(partnerCall));

            var messages = new List<Message>();
            using (SqliteDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    messages.Add(ReadMessage(reader));
                }
            }

            return AttachRecipients(messages);
        }
    }

    /// <summary>
    /// Clears one partner's queue entry (FS '+' completed — or '-'/'R': the partner already has
    /// it, which also counts as cleared, compat spec §3.4). When the last pending entry clears,
    /// the message moves to F ("Forwarded-to-all: →F; per-partner bits cleared one at a time;
    /// F only when all clear" — §2.2). Returns false when no pending entry existed.
    /// </summary>
    public bool MarkForwarded(long number, string partnerCall)
    {
        ArgumentNullException.ThrowIfNull(partnerCall);
        long now = NowSeconds();

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();

            using (SqliteCommand cmd = Command(tx,
                "UPDATE forwards SET forwarded_utc=$now WHERE message_number=$n AND partner_call=$p AND forwarded_utc IS NULL;"))
            {
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$n", number);
                cmd.Parameters.AddWithValue("$p", Callsigns.Normalize(partnerCall));
                if (cmd.ExecuteNonQuery() == 0)
                {
                    return false;
                }
            }

            if (CountPendingForwards(tx, number) == 0)
            {
                using SqliteCommand done = Command(tx,
                    "UPDATE messages SET status='F' WHERE number=$n AND status IN ('N','Y','$');");
                done.Parameters.AddWithValue("$n", number);
                done.ExecuteNonQuery();
            }

            tx.Commit();
            return true;
        }
    }

    // ---------------------------------------------------------------- BID dedup store

    /// <summary>Looks up a BID case-insensitively (compat spec §2.3 [BPQ-SRC LookupBID _stricmp]).</summary>
    public BidRecord? LookupBid(string bid)
    {
        ArgumentNullException.ThrowIfNull(bid);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT bid,first_seen_utc,first_seen_from,message_number FROM bids WHERE bid=$bid;");
            cmd.Parameters.AddWithValue("$bid", NormalizeBid(bid) ?? "");
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new BidRecord(
                reader.GetString(0),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3));
        }
    }

    /// <summary>
    /// Records a BID without a message (e.g. one declined in a proposal block we still never
    /// want again). Returns true when newly recorded, false when already known — the first-seen
    /// time and direction are never overwritten.
    /// </summary>
    public bool RecordBid(string bid, string? seenFrom = null)
    {
        ArgumentNullException.ThrowIfNull(bid);
        string normalized = NormalizeBid(bid) ?? throw new ArgumentException("BID must be non-empty.", nameof(bid));
        long now = NowSeconds();

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "INSERT OR IGNORE INTO bids(bid,first_seen_utc,first_seen_from,message_number) VALUES($bid,$now,$from,NULL);");
            cmd.Parameters.AddWithValue("$bid", normalized);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$from", (object?)(seenFrom is null ? null : Callsigns.Normalize(seenFrom)) ?? DBNull.Value);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// The inbound duplicate-BID check, pinned by [BPQ-SRC DoWeWantIt / BBSUtilities.c:5577]
    /// (compat spec §2.3): bulletins — any known BID rejects; personals (and T, which the
    /// source treats the same way) reject only when a live copy (status N/Y/H) with the same
    /// TO still exists — forwarded or killed copies are accepted again.
    /// </summary>
    public BidDisposition CheckInboundBid(string bid, MessageType type, string toCall)
    {
        ArgumentNullException.ThrowIfNull(bid);
        ArgumentNullException.ThrowIfNull(toCall);

        lock (_gate)
        {
            BidRecord? record = LookupBid(bid);
            if (record is null)
            {
                return BidDisposition.Accept;
            }

            if (type == MessageType.Bulletin)
            {
                return BidDisposition.RejectDuplicate;
            }

            if (record.MessageNumber is { } messageNumber)
            {
                Message? copy = GetMessageCore(messageNumber);
                if (copy is not null
                    && copy.Status is MessageStatus.Unread or MessageStatus.Read or MessageStatus.Held)
                {
                    string to = Callsigns.NormalizeAddressee(toCall);
                    foreach (MessageRecipient recipient in copy.Recipients)
                    {
                        if (string.Equals(recipient.ToCall, to, StringComparison.OrdinalIgnoreCase))
                        {
                            return BidDisposition.RejectDuplicate;
                        }
                    }
                }
            }

            return BidDisposition.Accept;
        }
    }

    // ---------------------------------------------------------------- users

    /// <summary>Fetches a user by callsign (case-insensitive).</summary>
    public User? GetUser(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT callsign,name,home_bbs,last_login_utc,last_listed_number,pdn_username FROM users WHERE callsign=$c;");
            cmd.Parameters.AddWithValue("$c", Callsigns.Normalize(callsign));
            using SqliteDataReader reader = cmd.ExecuteReader();
            return reader.Read() ? ReadUser(reader) : null;
        }
    }

    /// <summary>
    /// True when <paramref name="callsign"/> is a known local user, compared on the base
    /// (SSID-stripped) callsign — a personal mailbox is owned by the base call, while a stored
    /// record may or may not carry an SSID (<see cref="Callsigns.BaseEquals"/> semantics). Used
    /// by the host's routing to keep local users' mail local rather than letting a wildcard-AT
    /// partner swallow it (design.md "The home-BBS requirement" rule #1).
    /// </summary>
    public bool UserExists(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        string baseCall = Callsigns.StripSsid(Callsigns.Normalize(callsign));
        if (baseCall.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return UserExistsCore(baseCall);
        }
    }

    /// <summary>Base-call membership test (caller holds <see cref="_gate"/>); <paramref name="baseCall"/> already normalised + SSID-stripped.</summary>
    private bool UserExistsCore(string baseCall)
    {
        // Match a stored bare base call ($base) or one carrying an SSID ($base-…).
        using SqliteCommand cmd = Command(null,
            "SELECT 1 FROM users WHERE callsign=$base OR callsign LIKE $prefix ESCAPE '\\' LIMIT 1;");
        cmd.Parameters.AddWithValue("$base", baseCall);
        cmd.Parameters.AddWithValue("$prefix", EscapeLike(baseCall) + "-%");
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read();
    }

    /// <summary>Escapes SQL LIKE metacharacters in a literal prefix (used with <c>ESCAPE '\'</c>).</summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>All users, ordered by callsign.</summary>
    public IReadOnlyList<User> ListUsers()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT callsign,name,home_bbs,last_login_utc,last_listed_number,pdn_username FROM users ORDER BY callsign;");
            using SqliteDataReader reader = cmd.ExecuteReader();
            var users = new List<User>();
            while (reader.Read())
            {
                users.Add(ReadUser(reader));
            }

            return users;
        }
    }

    /// <summary>Inserts or fully updates a user record.</summary>
    public void UpsertUser(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "INSERT INTO users(callsign,name,home_bbs,last_login_utc,last_listed_number,pdn_username) " +
                "VALUES($c,$name,$home,$login,$listed,$pdn) " +
                "ON CONFLICT(callsign) DO UPDATE SET name=excluded.name, home_bbs=excluded.home_bbs, " +
                "last_login_utc=excluded.last_login_utc, last_listed_number=excluded.last_listed_number, " +
                "pdn_username=excluded.pdn_username;");
            cmd.Parameters.AddWithValue("$c", Callsigns.Normalize(user.Callsign));
            cmd.Parameters.AddWithValue("$name", (object?)user.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$home", (object?)(user.HomeBbs?.Trim().ToUpperInvariant()) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$login", (object?)user.LastLogin?.ToUnixTimeSeconds() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$listed", user.LastListedNumber);
            cmd.Parameters.AddWithValue("$pdn", (object?)user.PdnUsername ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Ensures a skeletal user record exists for <paramref name="callsign"/>, keyed by its base
    /// (SSID-stripped) callsign so it is found by <see cref="UserExists"/> and joins the same
    /// recipient rows (<see cref="Callsigns.NormalizeAddressee"/> semantics). Idempotent: a no-op
    /// when the user already exists (matched on the base call), creating nothing and overwriting
    /// nothing — name/QTH/Home are left for the console's first-connect persistence to fill.
    ///
    /// This is the store half of the home-BBS auto-create (design.md "The home-BBS requirement"
    /// rule #2): mail homed here can arrive before its owner ever connects, so a record must be
    /// created on first inbound delivery to make that mail listable on their first <c>L</c>.
    /// Returns true when a new record was created, false when one already existed.
    /// </summary>
    public bool EnsureUser(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        string baseCall = Callsigns.StripSsid(Callsigns.Normalize(callsign));
        if (baseCall.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            // Idempotent on the same base-call membership UserExists uses — including a stored
            // record that happens to carry an SSID — so we never insert a duplicate skeletal row.
            if (UserExistsCore(baseCall))
            {
                return false;
            }

            using SqliteCommand cmd = Command(null,
                "INSERT OR IGNORE INTO users(callsign,last_listed_number) VALUES($c,0);");
            cmd.Parameters.AddWithValue("$c", baseCall);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Stamps a user's last-login time, auto-creating the record on first connect
    /// (compat spec §1.1 "First-ever connect: BPQMail auto-creates a user record").
    /// </summary>
    public void TouchLastLogin(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        long now = NowSeconds();

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "INSERT INTO users(callsign,last_login_utc,last_listed_number) VALUES($c,$now,0) " +
                "ON CONFLICT(callsign) DO UPDATE SET last_login_utc=excluded.last_login_utc;");
            cmd.Parameters.AddWithValue("$c", Callsigns.Normalize(callsign));
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Records the highest message number a user has listed ("L = new since last L", compat spec §1.3).</summary>
    public void SetLastListed(string callsign, long number)
    {
        ArgumentNullException.ThrowIfNull(callsign);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "INSERT INTO users(callsign,last_listed_number) VALUES($c,$n) " +
                "ON CONFLICT(callsign) DO UPDATE SET last_listed_number=excluded.last_listed_number;");
            cmd.Parameters.AddWithValue("$c", Callsigns.Normalize(callsign));
            cmd.Parameters.AddWithValue("$n", number);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- partners

    /// <summary>Fetches a partner by its exact configured call (case-insensitive).</summary>
    public Partner? GetPartner(string call)
    {
        ArgumentNullException.ThrowIfNull(call);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, PartnerSelect + " WHERE call=$c;");
            cmd.Parameters.AddWithValue("$c", Callsigns.Normalize(call));
            using SqliteDataReader reader = cmd.ExecuteReader();
            return reader.Read() ? ReadPartner(reader) : null;
        }
    }

    /// <summary>
    /// All partners ordered by callsign — the order fed to <see cref="RoutingEngine.Route"/>,
    /// making its first-wins tie-breaking deterministic.
    /// </summary>
    public IReadOnlyList<Partner> ListPartners()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, PartnerSelect + " ORDER BY call;");
            using SqliteDataReader reader = cmd.ExecuteReader();
            var partners = new List<Partner>();
            while (reader.Read())
            {
                partners.Add(ReadPartner(reader));
            }

            return partners;
        }
    }

    /// <summary>Inserts or fully updates a partner record (keyed by exact call incl. SSID, compat spec §2.5).</summary>
    public void UpsertPartner(Partner partner)
    {
        ArgumentNullException.ThrowIfNull(partner);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "INSERT INTO partners(call,enabled,forward_interval_seconds,forward_new_immediately,connect_script," +
                "to_calls,at_calls,h_routes,h_routes_p,bbs_ha,max_rx_size,max_tx_size,allow_b2f) " +
                "VALUES($call,$en,$int,$imm,$script,$to,$at,$hr,$hrp,$ha,$rx,$tx,$b2) " +
                "ON CONFLICT(call) DO UPDATE SET enabled=excluded.enabled, " +
                "forward_interval_seconds=excluded.forward_interval_seconds, " +
                "forward_new_immediately=excluded.forward_new_immediately, connect_script=excluded.connect_script, " +
                "to_calls=excluded.to_calls, at_calls=excluded.at_calls, h_routes=excluded.h_routes, " +
                "h_routes_p=excluded.h_routes_p, bbs_ha=excluded.bbs_ha, max_rx_size=excluded.max_rx_size, " +
                "max_tx_size=excluded.max_tx_size, allow_b2f=excluded.allow_b2f;");
            cmd.Parameters.AddWithValue("$call", Callsigns.Normalize(partner.Call));
            cmd.Parameters.AddWithValue("$en", partner.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$int", partner.ForwardIntervalSeconds);
            cmd.Parameters.AddWithValue("$imm", partner.ForwardNewImmediately ? 1 : 0);
            cmd.Parameters.AddWithValue("$script", string.Join('\n', partner.ConnectScript));
            cmd.Parameters.AddWithValue("$to", string.Join(' ', partner.ToCalls));
            cmd.Parameters.AddWithValue("$at", string.Join(' ', partner.AtCalls));
            cmd.Parameters.AddWithValue("$hr", string.Join(' ', partner.HRoutes));
            cmd.Parameters.AddWithValue("$hrp", string.Join(' ', partner.HRoutesP));
            cmd.Parameters.AddWithValue("$ha", (object?)(partner.BbsHa?.Trim().ToUpperInvariant()) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rx", partner.MaxRxSize);
            cmd.Parameters.AddWithValue("$tx", partner.MaxTxSize);
            cmd.Parameters.AddWithValue("$b2", partner.AllowB2F ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes a partner. Pending forward-queue rows for it remain until their messages purge.</summary>
    public bool DeletePartner(string call)
    {
        ArgumentNullException.ThrowIfNull(call);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, "DELETE FROM partners WHERE call=$c;");
            cmd.Parameters.AddWithValue("$c", Callsigns.Normalize(call));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ---------------------------------------------------------------- housekeeping internals

    /// <summary>Physically deletes K messages whose kill time is at or before <paramref name="cutoffUtcSeconds"/>.</summary>
    internal int PurgeKilledMessages(long cutoffUtcSeconds)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "DELETE FROM messages WHERE status='K' AND killed_utc IS NOT NULL AND killed_utc<=$cut;");
            cmd.Parameters.AddWithValue("$cut", cutoffUtcSeconds);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Kills (status → K) messages of <paramref name="typeCode"/> in <paramref name="statusCodes"/> created strictly before the cutoff.</summary>
    internal int KillByAge(char typeCode, string statusCodes, long cutoffUtcSeconds, bool? hasPendingForwards = null)
    {
        var sql = new System.Text.StringBuilder(
            "UPDATE messages SET status='K', killed_utc=$now WHERE type=$t AND created_utc<$cut AND instr($statuses, status)>0");

        if (hasPendingForwards is { } pending)
        {
            sql.Append(pending
                ? " AND EXISTS(SELECT 1 FROM forwards f WHERE f.message_number=messages.number AND f.forwarded_utc IS NULL)"
                : " AND NOT EXISTS(SELECT 1 FROM forwards f WHERE f.message_number=messages.number AND f.forwarded_utc IS NULL)");
        }

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, sql.ToString() + ";");
            cmd.Parameters.AddWithValue("$now", NowSeconds());
            cmd.Parameters.AddWithValue("$t", typeCode.ToString());
            cmd.Parameters.AddWithValue("$cut", cutoffUtcSeconds);
            cmd.Parameters.AddWithValue("$statuses", statusCodes);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Purges BID records first seen strictly before the cutoff (BID lifetime, compat spec §6).</summary>
    internal int PurgeExpiredBids(long cutoffUtcSeconds)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, "DELETE FROM bids WHERE first_seen_utc<$cut;");
            cmd.Parameters.AddWithValue("$cut", cutoffUtcSeconds);
            return cmd.ExecuteNonQuery();
        }
    }

    internal long NowSeconds() => _time.GetUtcNow().ToUnixTimeSeconds();

    // ---------------------------------------------------------------- plumbing

    private const string PartnerSelect =
        "SELECT call,enabled,forward_interval_seconds,forward_new_immediately,connect_script," +
        "to_calls,at_calls,h_routes,h_routes_p,bbs_ha,max_rx_size,max_tx_size,allow_b2f FROM partners";

    private static void ExecuteRaw(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int Migrate(SqliteConnection connection)
    {
        ExecuteRaw(connection, "CREATE TABLE IF NOT EXISTS meta(key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;");

        int version;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
            object? value = read.ExecuteScalar();
            version = value is null ? 0 : int.Parse((string)value, CultureInfo.InvariantCulture);
        }

        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {version} is newer than this build supports ({CurrentSchemaVersion}).");
        }

        if (version < 1)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            using (var ddl = connection.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV1;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "INSERT INTO meta(key,value) VALUES('schema_version','1');";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 1;
        }

        // v2 — B2 completeness (multi-recipient To/Cc + File: attachments). PURELY ADDITIVE so it
        // is safe to apply to an existing populated database (the live lab bbs.db): the recipients
        // table gains a `cc` column defaulting 0 (every existing row stays a To-recipient), and a
        // new attachments table is created. No existing column/row is touched or rewritten.
        if (version < 2)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            using (var ddl = connection.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV2;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='2' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 2;
        }

        return version;
    }

    private const string SchemaV1 = """
        CREATE TABLE messages(
            number        INTEGER PRIMARY KEY AUTOINCREMENT,
            type          TEXT NOT NULL CHECK(type IN ('P','B','T')),
            status        TEXT NOT NULL CHECK(status IN ('N','Y','$','F','K','H','D')),
            from_call     TEXT NOT NULL,
            at_bbs        TEXT,
            bid           TEXT NOT NULL,
            subject       TEXT NOT NULL,
            body          BLOB NOT NULL,
            received_from TEXT,
            created_utc   INTEGER NOT NULL,
            killed_utc    INTEGER
        );
        CREATE INDEX idx_messages_status ON messages(status);
        CREATE INDEX idx_messages_type ON messages(type);

        CREATE TABLE recipients(
            message_number INTEGER NOT NULL REFERENCES messages(number) ON DELETE CASCADE,
            to_call        TEXT NOT NULL,
            read_utc       INTEGER,
            PRIMARY KEY(message_number, to_call)
        ) WITHOUT ROWID;
        CREATE INDEX idx_recipients_to ON recipients(to_call);

        CREATE TABLE bids(
            bid             TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
            first_seen_utc  INTEGER NOT NULL,
            first_seen_from TEXT,
            message_number  INTEGER
        ) WITHOUT ROWID;

        CREATE TABLE users(
            callsign           TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
            name               TEXT,
            home_bbs           TEXT,
            last_login_utc     INTEGER,
            last_listed_number INTEGER NOT NULL DEFAULT 0,
            pdn_username       TEXT
        ) WITHOUT ROWID;

        CREATE TABLE partners(
            call                     TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
            enabled                  INTEGER NOT NULL DEFAULT 1,
            forward_interval_seconds INTEGER NOT NULL DEFAULT 3600,
            forward_new_immediately  INTEGER NOT NULL DEFAULT 0,
            connect_script           TEXT NOT NULL DEFAULT '',
            to_calls                 TEXT NOT NULL DEFAULT '',
            at_calls                 TEXT NOT NULL DEFAULT '',
            h_routes                 TEXT NOT NULL DEFAULT '',
            h_routes_p               TEXT NOT NULL DEFAULT '',
            bbs_ha                   TEXT,
            max_rx_size              INTEGER NOT NULL DEFAULT 99999,
            max_tx_size              INTEGER NOT NULL DEFAULT 99999,
            allow_b2f                INTEGER NOT NULL DEFAULT 0
        ) WITHOUT ROWID;

        CREATE TABLE forwards(
            message_number INTEGER NOT NULL REFERENCES messages(number) ON DELETE CASCADE,
            partner_call   TEXT NOT NULL COLLATE NOCASE,
            queued_utc     INTEGER NOT NULL,
            forwarded_utc  INTEGER,
            PRIMARY KEY(message_number, partner_call)
        ) WITHOUT ROWID;
        CREATE INDEX idx_forwards_partner ON forwards(partner_call) WHERE forwarded_utc IS NULL;
        """;

    // v2 — additive only (see Migrate): a cc flag on recipients (To vs Cc, spec §3.9) and an
    // attachments table for B2F File: parts. Applied on top of a populated v1 db without rewriting
    // any existing data: the ALTER defaults cc=0 so every pre-existing recipient stays a To.
    private const string SchemaV2 = """
        ALTER TABLE recipients ADD COLUMN cc INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE attachments(
            message_number INTEGER NOT NULL REFERENCES messages(number) ON DELETE CASCADE,
            name           TEXT NOT NULL,
            content        BLOB NOT NULL
        );
        CREATE INDEX idx_attachments_message ON attachments(message_number);
        """;

    private void InsertRecipient(SqliteTransaction tx, long number, string toCall, bool cc)
    {
        using SqliteCommand cmd = Command(tx,
            "INSERT OR IGNORE INTO recipients(message_number,to_call,cc) VALUES($n,$to,$cc);");
        cmd.Parameters.AddWithValue("$n", number);
        cmd.Parameters.AddWithValue("$to", toCall);
        cmd.Parameters.AddWithValue("$cc", cc ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(SqliteTransaction? tx, string sql)
    {
        SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private Message? GetMessageCore(long number)
    {
        using SqliteCommand cmd = Command(null,
            "SELECT m.number,m.type,m.status,m.from_call,m.at_bbs,m.bid,m.subject,m.body,m.received_from,m.created_utc,m.killed_utc " +
            "FROM messages m WHERE m.number=$n;");
        cmd.Parameters.AddWithValue("$n", number);

        Message? message = null;
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                message = ReadMessage(reader);
            }
        }

        if (message is null)
        {
            return null;
        }

        return AttachRecipients([message])[0];
    }

    private (char Type, char Status)? GetHeader(SqliteTransaction tx, long number)
    {
        using SqliteCommand cmd = Command(tx, "SELECT type,status FROM messages WHERE number=$n;");
        cmd.Parameters.AddWithValue("$n", number);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0)[0], reader.GetString(1)[0]);
    }

    private void SetStatus(SqliteTransaction tx, long number, char status, bool stampKilled)
    {
        using SqliteCommand cmd = Command(tx, stampKilled
            ? "UPDATE messages SET status=$s, killed_utc=$now WHERE number=$n;"
            : "UPDATE messages SET status=$s WHERE number=$n;");
        cmd.Parameters.AddWithValue("$s", status.ToString());
        cmd.Parameters.AddWithValue("$n", number);
        if (stampKilled)
        {
            cmd.Parameters.AddWithValue("$now", NowSeconds());
        }

        cmd.ExecuteNonQuery();
    }

    private long CountPendingForwards(SqliteTransaction tx, long number)
    {
        using SqliteCommand cmd = Command(tx,
            "SELECT COUNT(*) FROM forwards WHERE message_number=$n AND forwarded_utc IS NULL;");
        cmd.Parameters.AddWithValue("$n", number);
        return (long)cmd.ExecuteScalar()!;
    }

    private static Message ReadMessage(SqliteDataReader reader)
    {
        return new Message
        {
            Number = reader.GetInt64(0),
            Type = MessageTypeExtensions.MessageTypeFromCode(reader.GetString(1)[0]),
            Status = MessageStatusExtensions.MessageStatusFromCode(reader.GetString(2)[0]),
            From = reader.GetString(3),
            At = reader.IsDBNull(4) ? null : reader.GetString(4),
            Bid = reader.GetString(5),
            Subject = reader.GetString(6),
            Body = reader.GetFieldValue<byte[]>(7),
            ReceivedFrom = reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(9)),
            KilledAt = reader.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(10)),
        };
    }

    private static User ReadUser(SqliteDataReader reader)
    {
        return new User
        {
            Callsign = reader.GetString(0),
            Name = reader.IsDBNull(1) ? null : reader.GetString(1),
            HomeBbs = reader.IsDBNull(2) ? null : reader.GetString(2),
            LastLogin = reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
            LastListedNumber = reader.GetInt64(4),
            PdnUsername = reader.IsDBNull(5) ? null : reader.GetString(5),
        };
    }

    private static Partner ReadPartner(SqliteDataReader reader)
    {
        return new Partner
        {
            Call = reader.GetString(0),
            Enabled = reader.GetInt64(1) != 0,
            ForwardIntervalSeconds = (int)reader.GetInt64(2),
            ForwardNewImmediately = reader.GetInt64(3) != 0,
            ConnectScript = SplitLines(reader.GetString(4)),
            ToCalls = SplitList(reader.GetString(5)),
            AtCalls = SplitList(reader.GetString(6)),
            HRoutes = SplitList(reader.GetString(7)),
            HRoutesP = SplitList(reader.GetString(8)),
            BbsHa = reader.IsDBNull(9) ? null : reader.GetString(9),
            MaxRxSize = (int)reader.GetInt64(10),
            MaxTxSize = (int)reader.GetInt64(11),
            AllowB2F = reader.GetInt64(12) != 0,
        };
    }

    private static string[] SplitLines(string joined) =>
        joined.Length == 0 ? [] : joined.Split('\n');

    private static string[] SplitList(string joined) =>
        joined.Length == 0 ? [] : joined.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Loads recipient and attachment rows for a batch of messages and returns new records with
    /// <see cref="Message.Recipients"/> (ordered To-first then Cc, by callsign) and
    /// <see cref="Message.Attachments"/> (insert/wire order) populated. Both use a single query
    /// keyed by the message-number set — no N+1 — like the recipient load always has.
    /// </summary>
    private List<Message> AttachRecipients(List<Message> messages)
    {
        if (messages.Count == 0)
        {
            return messages;
        }

        var recipientsByNumber = new Dictionary<long, List<MessageRecipient>>();
        var attachmentsByNumber = new Dictionary<long, List<MessageAttachment>>();
        var inClause = new System.Text.StringBuilder();
        var numberParameters = new List<(string Name, long Number)>(messages.Count);

        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0)
            {
                inClause.Append(',');
            }

            string name = "$m" + i.ToString(CultureInfo.InvariantCulture);
            inClause.Append(name);
            numberParameters.Add((name, messages[i].Number));
            recipientsByNumber[messages[i].Number] = [];
            attachmentsByNumber[messages[i].Number] = [];
        }

        string set = inClause.ToString();

        // To-recipients sort before Cc (cc ASC), then by callsign — a stable, To-first order.
        using (SqliteCommand cmd = Command(null,
            "SELECT message_number,to_call,read_utc,cc FROM recipients WHERE message_number IN (" +
            set + ") ORDER BY message_number,cc,to_call;"))
        {
            foreach ((string name, long n) in numberParameters)
            {
                cmd.Parameters.AddWithValue(name, n);
            }

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long number = reader.GetInt64(0);
                recipientsByNumber[number].Add(new MessageRecipient(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                    reader.GetInt64(3) != 0));
            }
        }

        // Attachments in stored (= wire) order: rowid is monotonic with insert order.
        using (SqliteCommand cmd = Command(null,
            "SELECT message_number,name,content FROM attachments WHERE message_number IN (" +
            set + ") ORDER BY message_number,rowid;"))
        {
            foreach ((string name, long n) in numberParameters)
            {
                cmd.Parameters.AddWithValue(name, n);
            }

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long number = reader.GetInt64(0);
                attachmentsByNumber[number].Add(new MessageAttachment(
                    reader.GetString(1),
                    reader.GetFieldValue<byte[]>(2)));
            }
        }

        var result = new List<Message>(messages.Count);
        foreach (Message message in messages)
        {
            result.Add(message with
            {
                Recipients = recipientsByNumber[message.Number],
                Attachments = attachmentsByNumber[message.Number],
            });
        }

        return result;
    }

    private static string? NormalizeAt(string? at)
    {
        if (string.IsNullOrWhiteSpace(at))
        {
            return null;
        }

        string normalized = at.Trim().ToUpperInvariant();
        return normalized.Length <= Message.MaxAtLength ? normalized : normalized[..Message.MaxAtLength];
    }

    private static string? NormalizeBid(string? bid)
    {
        if (string.IsNullOrWhiteSpace(bid))
        {
            return null;
        }

        // ≤12 chars, truncated [BPQ-SRC BBSUtilities.c:5630] (compat spec §2.3).
        string normalized = bid.Trim().ToUpperInvariant();
        return normalized.Length <= Message.MaxBidLength ? normalized : normalized[..Message.MaxBidLength];
    }
}
