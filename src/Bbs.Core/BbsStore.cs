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
    public const int CurrentSchemaVersion = 12;

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
    /// The store's current UTC instant (the injected <see cref="TimeProvider"/>) — so a UI computing
    /// "is this deferred send still within its undo window?" uses the SAME clock the store stamps
    /// <c>send_release_utc</c> with, deterministic under a fake clock in tests.
    /// </summary>
    public DateTimeOffset Now => _time.GetUtcNow();

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
                "INSERT INTO messages(type,status,from_call,at_bbs,bid,subject,body,received_from,created_utc,local_only) " +
                "VALUES($type,$status,$from,$at,$bid,$subject,$body,$rxfrom,$created,$local);"))
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
                insert.Parameters.AddWithValue("$local", draft.LocalOnly ? 1 : 0);
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
            //
            // A local_only message (a 7plus assembled-file presentation artifact, schema v3) is
            // NEVER recorded in the dedup store: it must not collide on BID with the network, so
            // its (auto-allocated, locally-unique) BID can never reject a genuine inbound message
            // that happens to share it, and it never participates in the §2.3 live-copy check.
            // This is the store half of the local_only forward-safety guarantee (the routing half
            // is RoutingService skipping local_only messages).
            if (!draft.LocalOnly)
            {
                using SqliteCommand insertBid = Command(tx,
                    "INSERT INTO bids(bid,first_seen_utc,first_seen_from,message_number) VALUES($bid,$seen,$from,$n) " +
                    "ON CONFLICT(bid) DO UPDATE SET message_number=excluded.message_number;");
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
            "SELECT m.number,m.type,m.status,m.from_call,m.at_bbs,m.bid,m.subject,m.body,m.received_from,m.created_utc,m.killed_utc,m.local_only,m.hold_reason,m.send_release_utc FROM messages m");
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

        if (query.HomedLocally)
        {
            // Mail received/held here, never routed to a partner: exclude anything with a forward
            // target (queued OR already sent — the row is durable). The "Inbox" sense — see
            // MessageQuery.HomedLocally. Outbound mail to one of our users @ a remote BBS has a
            // forwards row, so this drops it from the local inbox.
            sql.Append(" AND NOT EXISTS(SELECT 1 FROM forwards f WHERE f.message_number=m.number)");
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
    public bool HoldMessage(long number, string? reason = null)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "UPDATE messages SET status='H', hold_reason=$r WHERE number=$n AND status NOT IN ('K','H');");
            cmd.Parameters.AddWithValue("$n", number);
            cmd.Parameters.AddWithValue("$r", (object?)reason ?? DBNull.Value);
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
    /// Defers a just-composed message for the webmail "undo send" window: holds it (status → H, so it
    /// is hidden and unforwarded) AND stamps <c>send_release_utc</c> at <paramref name="windowSeconds"/>
    /// from now — the instant a release worker (<see cref="ListDueDeferredSends"/> /
    /// <see cref="ReleaseDeferredSend"/>) clears the marker and routes it. Guarded against K (killed)
    /// and an already-held message so it can't disturb either.
    /// </summary>
    public void DeferSend(long number, int windowSeconds)
    {
        long release = NowSeconds() + windowSeconds;
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "UPDATE messages SET status='H', send_release_utc=$r WHERE number=$n AND status NOT IN ('K','H');");
            cmd.Parameters.AddWithValue("$r", release);
            cmd.Parameters.AddWithValue("$n", number);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Deferred sends whose undo window has lapsed (<c>send_release_utc</c> set and at or before now) —
    /// the release worker's work list. Any status (normally H), so a marker stamped before a restart
    /// is still picked up on the first tick. Caller releases + routes each (<see cref="ReleaseDeferredSend"/>).
    /// </summary>
    public IReadOnlyList<Message> ListDueDeferredSends()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT m.number,m.type,m.status,m.from_call,m.at_bbs,m.bid,m.subject,m.body,m.received_from,m.created_utc,m.killed_utc,m.local_only,m.hold_reason,m.send_release_utc " +
                "FROM messages m WHERE m.send_release_utc IS NOT NULL AND m.send_release_utc<=$now " +
                "ORDER BY m.number;");
            cmd.Parameters.AddWithValue("$now", NowSeconds());

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
    /// Releases a deferred send whose window has lapsed: clears the <c>send_release_utc</c> marker and
    /// unholds the message (H → N, or $ for a bulletin with queued forwards — the same transition as
    /// <see cref="Unhold"/>) so it re-enters normal routing. The caller then routes it.
    /// </summary>
    public void ReleaseDeferredSend(long number)
    {
        lock (_gate)
        {
            using (SqliteCommand cmd = Command(null,
                "UPDATE messages SET send_release_utc=NULL WHERE number=$n;"))
            {
                cmd.Parameters.AddWithValue("$n", number);
                cmd.ExecuteNonQuery();
            }

            using SqliteTransaction tx = _connection.BeginTransaction();
            (char Type, char Status)? header = GetHeader(tx, number);
            if (header is { Status: 'H' })
            {
                char next = header.Value.Type == 'B' && CountPendingForwards(tx, number) > 0 ? '$' : 'N';
                SetStatus(tx, number, next, stampKilled: false);
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Cancels a deferred send — the webmail "undo": kills it (status → K, kill time stamped) and
    /// clears the marker, but ONLY while still within the window (<c>send_release_utc</c> set and
    /// strictly in the future). Returns true when it was cancelled, false when the window had already
    /// lapsed (the worker may have released + routed it) or it was not a pending send. Authorization
    /// (sender / sysop) is the caller's job.
    /// </summary>
    public bool CancelDeferredSend(long number)
    {
        long now = NowSeconds();
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "UPDATE messages SET status='K', killed_utc=$now, send_release_utc=NULL " +
                "WHERE number=$n AND send_release_utc IS NOT NULL AND send_release_utc>$now;");
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$n", number);
            return cmd.ExecuteNonQuery() > 0;
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
                "SELECT m.number,m.type,m.status,m.from_call,m.at_bbs,m.bid,m.subject,m.body,m.received_from,m.created_utc,m.killed_utc,m.local_only,m.hold_reason,m.send_release_utc " +
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
    /// Mailbox tallies for the status dashboard: <c>Total</c> live messages (every non-killed P/B/T)
    /// and how many of those are <c>Held</c>. One round-trip; killed messages are excluded from both.
    /// </summary>
    public (int Total, int Held) MessageCounts()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT " +
                "SUM(CASE WHEN status<>'K' THEN 1 ELSE 0 END), " +
                "SUM(CASE WHEN status='H' THEN 1 ELSE 0 END) FROM messages;");
            using SqliteDataReader reader = cmd.ExecuteReader();
            reader.Read();
            int total = reader.IsDBNull(0) ? 0 : (int)reader.GetInt64(0);
            int held = reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1);
            return (total, held);
        }
    }

    /// <summary>
    /// When this BBS last completed a forward to a partner (the newest <c>forwarded_utc</c> of any of
    /// its legs), or null if it never has. Drives the dashboard's per-partner "last forwarded" column.
    /// </summary>
    public DateTimeOffset? LastForwardedTo(string partnerCall)
    {
        ArgumentNullException.ThrowIfNull(partnerCall);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT MAX(forwarded_utc) FROM forwards WHERE partner_call=$p;");
            cmd.Parameters.AddWithValue("$p", Callsigns.Normalize(partnerCall));
            object? result = cmd.ExecuteScalar();
            return result is null or DBNull
                ? null
                : DateTimeOffset.FromUnixTimeSeconds((long)result);
        }
    }

    /// <summary>
    /// The age of the OLDEST still-waiting forward leg for a partner — <c>now - min(queued_utc)</c>
    /// over the same set the dashboard counts as "waiting" (a leg with <c>forwarded_utc IS NULL</c>
    /// whose message is neither killed nor held, so it matches <see cref="GetForwardQueue"/>). Null
    /// when nothing is waiting. Derived from the existing <c>forwards</c> table (no schema change) so
    /// a machine-readable health probe can alert on mail that has been stuck in the queue too long —
    /// the "queue isn't draining" signal an HTML page can't be scraped for. Never negative (a
    /// queued_utc nominally in the future from clock skew is clamped to zero).
    /// </summary>
    public TimeSpan? OldestQueuedAge(string partnerCall)
    {
        ArgumentNullException.ThrowIfNull(partnerCall);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT MIN(f.queued_utc) FROM forwards f JOIN messages m ON m.number=f.message_number " +
                "WHERE f.partner_call=$p AND f.forwarded_utc IS NULL AND m.status NOT IN ('K','H');");
            cmd.Parameters.AddWithValue("$p", Callsigns.Normalize(partnerCall));
            object? result = cmd.ExecuteScalar();
            if (result is null or DBNull)
            {
                return null;
            }

            TimeSpan age = Now - DateTimeOffset.FromUnixTimeSeconds((long)result);
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }
    }

    /// <summary>
    /// Records a forwarding dial that reached the partner and ran (the link works) — clears the
    /// failure streak and error. Persisted, so the dashboard health survives a restart. The
    /// negotiated <paramref name="mode"/> ("B2"/"B1") and the peer's raw SID (<paramref name="peerSid"/>)
    /// are persisted when the cycle got far enough to parse the peer's SID; null leaves the previously
    /// recorded values untouched (e.g. a reverse-collection poll that found nothing to dial) so the
    /// dashboard keeps showing the last mode actually negotiated.
    /// </summary>
    public void RecordForwardingSuccess(string partnerCall, string? mode = null, string? peerSid = null)
    {
        ArgumentNullException.ThrowIfNull(partnerCall);
        lock (_gate)
        {
            // COALESCE($mode, last_mode) keeps the last known mode when this cycle didn't negotiate
            // one (a no-op poll), rather than blanking the dashboard cell on the next quiet success.
            using SqliteCommand cmd = Command(null,
                "INSERT INTO forwarding_status(partner_call,last_attempt_utc,ok,error,consecutive_failures,last_mode,last_peer_sid) " +
                "VALUES($p,$now,1,NULL,0,$mode,$sid) " +
                "ON CONFLICT(partner_call) DO UPDATE SET last_attempt_utc=$now, ok=1, error=NULL, consecutive_failures=0, " +
                "last_mode=COALESCE($mode, forwarding_status.last_mode), " +
                "last_peer_sid=COALESCE($sid, forwarding_status.last_peer_sid);");
            cmd.Parameters.AddWithValue("$p", Callsigns.Normalize(partnerCall));
            cmd.Parameters.AddWithValue("$now", NowSeconds());
            cmd.Parameters.AddWithValue("$mode", (object?)mode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sid", (object?)peerSid ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records a forwarding dial that failed (could not connect / navigate) with its reason,
    /// incrementing the partner's consecutive-failure streak. Persisted. The increment is atomic
    /// (a single UPSERT): the first failure after a success is 1.
    /// </summary>
    public void RecordForwardingFailure(string partnerCall, string error)
    {
        ArgumentNullException.ThrowIfNull(partnerCall);
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "INSERT INTO forwarding_status(partner_call,last_attempt_utc,ok,error,consecutive_failures) " +
                "VALUES($p,$now,0,$err,1) " +
                "ON CONFLICT(partner_call) DO UPDATE SET last_attempt_utc=$now, ok=0, error=$err, " +
                "consecutive_failures = CASE WHEN forwarding_status.ok=0 THEN forwarding_status.consecutive_failures+1 ELSE 1 END;");
            cmd.Parameters.AddWithValue("$p", Callsigns.Normalize(partnerCall));
            cmd.Parameters.AddWithValue("$now", NowSeconds());
            cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The partner's last persisted forwarding outcome, or null if it has not been dialled.</summary>
    public PartnerForwardingState? GetForwardingStatus(string partnerCall)
    {
        ArgumentNullException.ThrowIfNull(partnerCall);
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT last_attempt_utc,ok,error,consecutive_failures,last_mode,last_peer_sid FROM forwarding_status WHERE partner_call=$p;");
            cmd.Parameters.AddWithValue("$p", Callsigns.Normalize(partnerCall));
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new PartnerForwardingState(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                reader.GetInt64(1) != 0,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                (int)reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }
    }

    /// <summary>
    /// How many messages bound for a partner are held (status H) with an unsent leg — i.e. pulled
    /// out of its forward queue (an oversize auto-hold, compat spec §4.1) rather than waiting. The
    /// forwarding card shows this beside the live queue depth so a held message isn't invisible.
    /// </summary>
    public int CountHeldForwards(string partnerCall)
    {
        ArgumentNullException.ThrowIfNull(partnerCall);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT COUNT(*) FROM forwards f JOIN messages m ON m.number=f.message_number " +
                "WHERE f.partner_call=$p AND f.forwarded_utc IS NULL AND m.status='H';");
            cmd.Parameters.AddWithValue("$p", Callsigns.Normalize(partnerCall));
            return (int)(long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// The forward targets recorded for a message — each partner it was routed to, with whether
    /// that leg has been sent (<c>forwarded_utc</c> stamped). Empty when the message was homed
    /// locally (never routed to a partner). Drives the Sent view's per-message status badge.
    /// </summary>
    public IReadOnlyList<MessageForward> GetMessageForwards(long number)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT partner_call, forwarded_utc FROM forwards WHERE message_number=$n ORDER BY partner_call;");
            cmd.Parameters.AddWithValue("$n", number);

            var forwards = new List<MessageForward>();
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                forwards.Add(new MessageForward(reader.GetString(0), !reader.IsDBNull(1)));
            }

            return forwards;
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

    // ---------------------------------------------------------------- White Pages directory (schema v12)

    /// <summary>
    /// Date-wins upsert of one parsed White Pages record into the directory (issue #36). Kept entirely
    /// out of the mail store. Semantics (BPQ <c>WPRoutines.c</c> <c>DoWPUpdate</c>):
    /// <list type="bullet">
    /// <item>A callsign not seen before is INSERTED.</item>
    /// <item>An authoritative record (<see cref="WhitePagesType.User"/>, the <c>/U</c> wire type)
    /// OVERWRITES the stored row unconditionally — regardless of staleness.</item>
    /// <item>Otherwise the incoming record only freshens fields when its
    /// <see cref="WhitePagesRecord.RecordDate"/> is at-or-after the stored <c>record_date</c>; a stale
    /// record is dropped. A null incoming optional field (<c>?</c>/unknown) NEVER overwrites a known
    /// stored value — only a non-null incoming field replaces.</item>
    /// </list>
    /// <c>last_seen_utc</c> is bumped to ingest time on every accepted-or-deduped sighting. The upsert
    /// is naturally idempotent: re-ingesting the same record (same date) leaves the row unchanged
    /// except <c>last_seen_utc</c>. Returns true when any directory FIELD changed (a new row, a
    /// freshened field, or an authoritative overwrite), false when nothing but <c>last_seen_utc</c>
    /// moved (a stale or identical re-ingest).
    /// </summary>
    public bool UpsertWhitePages(WhitePagesRecord record, string source = "wp")
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        string call = Callsigns.StripSsid(Callsigns.Normalize(record.Callsign));
        long incomingDate = ToEpochSeconds(record.RecordDate);
        long now = NowSeconds();

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();
            WhitePagesEntry? existing = GetWhitePagesCore(tx, call);

            bool authoritative = record.Type == WhitePagesType.User;
            if (existing is not null && !authoritative && incomingDate < ToEpochSeconds(existing.RecordDate))
            {
                // Stale, non-authoritative: keep stored data; only record that we saw it.
                using SqliteCommand touch = Command(tx,
                    "UPDATE whitepages SET last_seen_utc=$now WHERE callsign=$call;");
                touch.Parameters.AddWithValue("$now", now);
                touch.Parameters.AddWithValue("$call", call);
                touch.ExecuteNonQuery();
                tx.Commit();
                return false;
            }

            // Field-merge: an incoming non-null field replaces; an incoming null keeps the stored value
            // (an authoritative record still never erases known data with an unknown). A brand-new row
            // starts from the incoming record (nulls and all).
            string? homeBbs = record.HomeBbs ?? existing?.HomeBbs;
            string? name = record.Name ?? existing?.Name;
            string? qth = record.Qth ?? existing?.Qth;
            string? zip = record.Zip ?? existing?.Zip;

            // The freshness key never rolls BACKWARD: an authoritative /U record may carry an older date
            // than what we hold (it overwrites CONTENT unconditionally), but storing its older date would
            // then let a later non-authoritative update predating the /U slip past the staleness guard.
            // Keep the newer of the two dates. For the non-authoritative path the incoming date is always
            // >= stored (the stale case returned above), so the max is a no-op there.
            long storedDate = existing is null ? incomingDate : Math.Max(incomingDate, ToEpochSeconds(existing.RecordDate));
            DateOnly storedRecordDate = FromEpochSeconds(storedDate);

            using SqliteCommand cmd = Command(tx, """
                INSERT INTO whitepages(callsign,type,home_bbs,name,qth,zip,record_date,last_seen_utc,source)
                VALUES($call,$type,$home,$name,$qth,$zip,$date,$now,$source)
                ON CONFLICT(callsign) DO UPDATE SET
                    type=$type, home_bbs=$home, name=$name, qth=$qth, zip=$zip,
                    record_date=$date, last_seen_utc=$now, source=$source;
                """);
            cmd.Parameters.AddWithValue("$call", call);
            cmd.Parameters.AddWithValue("$type", record.Type.ToCode().ToString());
            cmd.Parameters.AddWithValue("$home", (object?)homeBbs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$qth", (object?)qth ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$zip", (object?)zip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$date", storedDate);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.ExecuteNonQuery();
            tx.Commit();

            // A field changed unless this was an identical re-ingest of the existing row.
            return existing is null
                || existing.Type != record.Type
                || existing.RecordDate != storedRecordDate
                || existing.HomeBbs != homeBbs
                || existing.Name != name
                || existing.Qth != qth
                || existing.Zip != zip;
        }
    }

    /// <summary>Looks up one directory entry by (base) callsign, case-insensitively. Null when unknown.</summary>
    public WhitePagesEntry? GetWhitePages(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        string call = Callsigns.StripSsid(Callsigns.Normalize(callsign));
        lock (_gate)
        {
            return GetWhitePagesCore(null, call);
        }
    }

    /// <summary>The number of entries in the directory.</summary>
    public int CountWhitePages()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, "SELECT COUNT(*) FROM whitepages;");
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Prunes directory entries we have NOT SEEN since <paramref name="cutoffUtc"/> — the housekeeping
    /// aging sweep for stale stations that have moved or retired (issue #36). Keys on
    /// <c>last_seen_utc</c>, NOT <c>record_date</c>: an active station re-announces every forwarding
    /// cycle and so is re-seen (its <c>last_seen_utc</c> is bumped on every sighting, even an unchanged
    /// one), but BPQ re-announces only records whose CONTENT changed, so a long-stable station keeps an
    /// old <c>record_date</c> while staying live — pruning on <c>record_date</c> would wrongly drop it.
    /// Returns the number of rows removed.
    /// </summary>
    public int SweepWhitePages(DateTimeOffset cutoffUtc)
    {
        long cutoffSeconds = cutoffUtc.ToUnixTimeSeconds();
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, "DELETE FROM whitepages WHERE last_seen_utc < $cutoff;");
            cmd.Parameters.AddWithValue("$cutoff", cutoffSeconds);
            return cmd.ExecuteNonQuery();
        }
    }

    private WhitePagesEntry? GetWhitePagesCore(SqliteTransaction? tx, string baseCall)
    {
        using SqliteCommand cmd = Command(tx,
            "SELECT callsign,type,home_bbs,name,qth,zip,record_date,last_seen_utc,source FROM whitepages WHERE callsign=$call;");
        cmd.Parameters.AddWithValue("$call", baseCall);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new WhitePagesEntry(
            reader.GetString(0),
            WhitePagesTypeExtensions.FromCode(reader.GetString(1)[0]),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            FromEpochSeconds(reader.GetInt64(6)),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)),
            reader.GetString(8));
    }

    /// <summary>The Unix-epoch seconds at midnight UTC of a <see cref="DateOnly"/> (the directory date key).</summary>
    private static long ToEpochSeconds(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static DateOnly FromEpochSeconds(long seconds) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime);

    // ---------------------------------------------------------------- 7plus part tracking (schema v3)

    /// <summary>
    /// Records one received 7plus part: links its file identity (<paramref name="identityKey"/> plus
    /// the geometry needed to rebuild the identity) and 1-based <paramref name="partNumber"/> to the
    /// <paramref name="sourceMessageNumber"/> it arrived in. The <c>sevenplus_files</c> row is
    /// upsert-created on first sight of the file (its geometry is fixed by the identity); the part
    /// row is inserted at-most-once per (identity, part) — a re-sent part is ignored (the first
    /// source message wins, matching the assembler's first-wins-per-part decode). Returns true when
    /// this part was newly recorded, false when that part was already known for this file.
    ///
    /// This is the store half of the inbound 7plus integration (design.md "abstract 7plus away from
    /// the user"): the host scans each inbound body, records every part here, then asks
    /// <see cref="GetSevenPlusProgress"/> whether the set is complete enough to assemble.
    /// </summary>
    public bool RecordSevenPlusPart(
        string identityKey, string headerName, int fileSize, int totalParts, int blockLines,
        int partNumber, long sourceMessageNumber)
    {
        ArgumentException.ThrowIfNullOrEmpty(identityKey);
        ArgumentNullException.ThrowIfNull(headerName);

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();

            using (SqliteCommand file = Command(tx,
                "INSERT INTO sevenplus_files(identity_key,header_name,file_size,total_parts,block_lines,assembled_message_number) " +
                "VALUES($k,$hn,$fs,$tp,$bl,NULL) ON CONFLICT(identity_key) DO NOTHING;"))
            {
                file.Parameters.AddWithValue("$k", identityKey);
                file.Parameters.AddWithValue("$hn", headerName);
                file.Parameters.AddWithValue("$fs", fileSize);
                file.Parameters.AddWithValue("$tp", totalParts);
                file.Parameters.AddWithValue("$bl", blockLines);
                file.ExecuteNonQuery();
            }

            int added;
            using (SqliteCommand part = Command(tx,
                "INSERT OR IGNORE INTO sevenplus_parts(identity_key,part_number,source_message_number) VALUES($k,$p,$n);"))
            {
                part.Parameters.AddWithValue("$k", identityKey);
                part.Parameters.AddWithValue("$p", partNumber);
                part.Parameters.AddWithValue("$n", sourceMessageNumber);
                added = part.ExecuteNonQuery();
            }

            tx.Commit();
            return added > 0;
        }
    }

    /// <summary>
    /// The progress of one 7plus file by identity: received-part count vs total, the recovered
    /// header name for display, and the synthesized message number once assembled (null while still
    /// accumulating). Returns null when the identity is unknown (no parts recorded yet).
    /// </summary>
    public SevenPlusProgress? GetSevenPlusProgress(string identityKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(identityKey);

        lock (_gate)
        {
            return GetSevenPlusProgressCore(identityKey);
        }
    }

    /// <summary>
    /// In-flight (not-yet-assembled) 7plus files, ordered by header name — the webmail placeholder
    /// source ("FIELDS.JPG — 3/5 parts received"). An assembled file drops out (it now lists as its
    /// synthesized message). A file whose source parts have all been purged (zero remaining parts) is
    /// also dropped — it is an orphan, not a real in-flight set.
    ///
    /// When <paramref name="recipientCall"/> is supplied, only files at least one of whose source
    /// part-messages is addressed to that call are returned — so a PERSONAL 7plus file's placeholder
    /// shows only in its addressee's inbox, never in another user's (matching how the raw parts +
    /// assembled message are recipient-scoped). Null returns all (the public bulletins case).
    /// </summary>
    public IReadOnlyList<SevenPlusProgress> ListIncompleteSevenPlusFiles(string? recipientCall = null)
    {
        string? scoped = recipientCall is null ? null : Callsigns.NormalizeAddressee(recipientCall);

        lock (_gate)
        {
            // SourceType is the type of any one recorded part-bulletin for the file (they share a
            // type) — picked by the lowest part number for determinism. The COUNT>0 guard drops
            // orphaned files whose parts were all purged. The optional recipient EXISTS scopes a
            // personal file's placeholder to its addressee only.
            var sql = new System.Text.StringBuilder(
                "SELECT f.identity_key,f.header_name,f.total_parts,f.assembled_message_number," +
                "(SELECT COUNT(*) FROM sevenplus_parts p WHERE p.identity_key=f.identity_key) AS rxcount," +
                "(SELECT m.type FROM sevenplus_parts p JOIN messages m ON m.number=p.source_message_number " +
                " WHERE p.identity_key=f.identity_key ORDER BY p.part_number LIMIT 1) " +
                "FROM sevenplus_files f WHERE f.assembled_message_number IS NULL AND rxcount>0");
            if (scoped is not null)
            {
                sql.Append(
                    " AND EXISTS(SELECT 1 FROM sevenplus_parts p JOIN recipients r ON r.message_number=p.source_message_number " +
                    "WHERE p.identity_key=f.identity_key AND r.to_call=$to)");
            }

            sql.Append(" ORDER BY f.header_name,f.identity_key;");

            using SqliteCommand cmd = Command(null, sql.ToString());
            if (scoped is not null)
            {
                cmd.Parameters.AddWithValue("$to", scoped);
            }

            using SqliteDataReader reader = cmd.ExecuteReader();
            var result = new List<SevenPlusProgress>();
            while (reader.Read())
            {
                result.Add(new SevenPlusProgress(
                    reader.GetString(0),
                    reader.GetString(1),
                    (int)reader.GetInt64(4),
                    (int)reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(5) ? null : MessageTypeExtensions.MessageTypeFromCode(reader.GetString(5)[0])));
            }

            return result;
        }
    }

    /// <summary>
    /// The bodies of every source part-bulletin recorded for one file, in part-number order — the
    /// raw 7plus text the assembler re-scans + reassembles once the set is complete. Each body is the
    /// stored message body verbatim (the surrounding mail text the parts arrived wrapped in is kept;
    /// <c>SevenPlusScanner</c> is tolerant of it).
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> GetSevenPlusPartBodies(string identityKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(identityKey);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT m.body FROM sevenplus_parts p JOIN messages m ON m.number=p.source_message_number " +
                "WHERE p.identity_key=$k ORDER BY p.part_number;");
            cmd.Parameters.AddWithValue("$k", identityKey);
            using SqliteDataReader reader = cmd.ExecuteReader();
            var result = new List<ReadOnlyMemory<byte>>();
            while (reader.Read())
            {
                result.Add(reader.GetFieldValue<byte[]>(0));
            }

            return result;
        }
    }

    /// <summary>
    /// Marks a 7plus file assembled, linking the synthesized <paramref name="assembledMessageNumber"/>.
    /// Idempotent and guarded: the link is set only while it is still NULL, so two racing assembly
    /// attempts can never produce two synthesized messages (the loser's UPDATE matches no row).
    /// Returns true when THIS call set the link (the caller owns the synthesized message), false when
    /// the file was already assembled.
    /// </summary>
    public bool MarkSevenPlusAssembled(string identityKey, long assembledMessageNumber)
    {
        ArgumentException.ThrowIfNullOrEmpty(identityKey);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "UPDATE sevenplus_files SET assembled_message_number=$n " +
                "WHERE identity_key=$k AND assembled_message_number IS NULL;");
            cmd.Parameters.AddWithValue("$n", assembledMessageNumber);
            cmd.Parameters.AddWithValue("$k", identityKey);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// True when <paramref name="messageNumber"/> is a raw 7plus source part-bulletin (it appears in
    /// <c>sevenplus_parts</c>). The webmail listings hide these — the user sees only the assembled file
    /// (or the in-flight placeholder) — while the message itself stays in the store and still forwards.
    /// </summary>
    public bool IsSevenPlusPartMessage(long messageNumber)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT 1 FROM sevenplus_parts WHERE source_message_number=$n LIMIT 1;");
            cmd.Parameters.AddWithValue("$n", messageNumber);
            using SqliteDataReader reader = cmd.ExecuteReader();
            return reader.Read();
        }
    }

    /// <summary>The set of message numbers that are raw 7plus source parts (the webmail listing hide-set).</summary>
    public IReadOnlySet<long> GetSevenPlusPartMessageNumbers()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, "SELECT DISTINCT source_message_number FROM sevenplus_parts;");
            using SqliteDataReader reader = cmd.ExecuteReader();
            var result = new HashSet<long>();
            while (reader.Read())
            {
                result.Add(reader.GetInt64(0));
            }

            return result;
        }
    }

    private SevenPlusProgress? GetSevenPlusProgressCore(string identityKey)
    {
        using SqliteCommand cmd = Command(null,
            "SELECT f.header_name,f.total_parts,f.assembled_message_number," +
            "(SELECT COUNT(*) FROM sevenplus_parts p WHERE p.identity_key=f.identity_key) " +
            "FROM sevenplus_files f WHERE f.identity_key=$k;");
        cmd.Parameters.AddWithValue("$k", identityKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SevenPlusProgress(
            identityKey,
            reader.GetString(0),
            (int)reader.GetInt64(3),
            (int)reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
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

    // ---------------------------------------------------------------- mail auth

    /// <summary>
    /// The shortest acceptable BBS mail-password. A mail password is typed into an external mail
    /// client (iPhone Mail) over the LAN, so a modest floor is appropriate — long enough to resist
    /// casual guessing without being onerous for a single trusted operator.
    /// </summary>
    public const int MinMailPasswordLength = 8;

    /// <summary>
    /// Sets (or replaces) the BBS mail-password for <paramref name="callsign"/> — the credential an
    /// external mail client authenticates with over IMAP, alongside the callsign. The plaintext is
    /// Argon2id-hashed (<see cref="PasswordHasher"/>) and only the PHC hash is stored, keyed by the
    /// base (SSID-stripped) callsign so M0LTE and M0LTE-7 share the one mailbox password.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="callsign"/> is not a usable callsign, or <paramref name="password"/> is shorter
    /// than <see cref="MinMailPasswordLength"/> (whitespace-only counts as empty).
    /// </exception>
    public void SetMailPassword(string callsign, string password)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(password);

        string baseCall = Callsigns.StripSsid(Callsigns.Normalize(callsign));
        if (baseCall.Length == 0)
        {
            throw new ArgumentException("Not a usable callsign.", nameof(callsign));
        }

        if (password.Trim().Length < MinMailPasswordLength)
        {
            throw new ArgumentException(
                $"Mail password must be at least {MinMailPasswordLength} characters.", nameof(password));
        }

        string hash = PasswordHasher.Hash(password);
        long now = NowSeconds();

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "INSERT INTO mail_auth(callsign,password_hash,updated_utc) VALUES($c,$h,$u) " +
                "ON CONFLICT(callsign) DO UPDATE SET password_hash=excluded.password_hash, updated_utc=excluded.updated_utc;");
            cmd.Parameters.AddWithValue("$c", baseCall);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$u", now);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Verifies a mail-client login: <c>true</c> only when <paramref name="callsign"/> has a mail
    /// password set and <paramref name="password"/> matches it (fixed-time, never throwing). A
    /// callsign with no <c>mail_auth</c> row always returns <c>false</c> — IMAP stays closed until
    /// the operator sets a password in webmail.
    /// </summary>
    public bool VerifyMailPassword(string callsign, string password)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(password);

        string baseCall = Callsigns.StripSsid(Callsigns.Normalize(callsign));
        if (baseCall.Length == 0)
        {
            return false;
        }

        string? hash;
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, "SELECT password_hash FROM mail_auth WHERE callsign=$c;");
            cmd.Parameters.AddWithValue("$c", baseCall);
            hash = cmd.ExecuteScalar() as string;
        }

        return hash is not null && PasswordHasher.Verify(password, hash);
    }

    /// <summary>True when <paramref name="callsign"/> has a BBS mail-password set (for webmail to show set-vs-change).</summary>
    public bool HasMailPassword(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);

        string baseCall = Callsigns.StripSsid(Callsigns.Normalize(callsign));
        if (baseCall.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, "SELECT 1 FROM mail_auth WHERE callsign=$c;");
            cmd.Parameters.AddWithValue("$c", baseCall);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>
    /// Removes any BBS mail-password for <paramref name="callsign"/> (disabling its IMAP login).
    /// Returns true when a row was deleted, false when none was set.
    /// </summary>
    public bool ClearMailPassword(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);

        string baseCall = Callsigns.StripSsid(Callsigns.Normalize(callsign));
        if (baseCall.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, "DELETE FROM mail_auth WHERE callsign=$c;");
            cmd.Parameters.AddWithValue("$c", baseCall);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ---------------------------------------------------------------- per-user read state

    /// <summary>
    /// Records that <paramref name="callsign"/> has read message <paramref name="number"/> (idempotent).
    /// This is the read-state for messages the user is not a named recipient of — chiefly bulletins,
    /// where the recipient is the category, not the reader. Personals keep their read-state on the
    /// recipient row (<see cref="MarkRead"/>); this table backs per-user unread for everything else.
    /// Keyed by base (SSID-stripped) callsign so a user's SSIDs share one read-state.
    /// </summary>
    public void SetReadByUser(string callsign, long number)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        string baseCall = Callsigns.StripSsid(Callsigns.Normalize(callsign));
        if (baseCall.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "INSERT INTO message_read(callsign,message_number,read_utc) VALUES($c,$n,$u) " +
                "ON CONFLICT(callsign,message_number) DO NOTHING;");
            cmd.Parameters.AddWithValue("$c", baseCall);
            cmd.Parameters.AddWithValue("$n", number);
            cmd.Parameters.AddWithValue("$u", NowSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>True when <paramref name="callsign"/> has read message <paramref name="number"/> (per <see cref="SetReadByUser"/>).</summary>
    public bool IsReadByUser(string callsign, long number)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        string baseCall = Callsigns.StripSsid(Callsigns.Normalize(callsign));
        if (baseCall.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "SELECT 1 FROM message_read WHERE callsign=$c AND message_number=$n;");
            cmd.Parameters.AddWithValue("$c", baseCall);
            cmd.Parameters.AddWithValue("$n", number);
            return cmd.ExecuteScalar() is not null;
        }
    }

    // ---------------------------------------------------------------- partners

    /// <summary>Fetches a partner by its exact configured call (case-insensitive). For looking up a
    /// KNOWN partner by its configured key (the scheduler, routing). For matching the source of an
    /// INBOUND connect, use <see cref="FindPartnerByBaseCall"/> — the source SSID is unreliable.</summary>
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
    /// Finds a forwarding partner by the BASE callsign of an inbound connect's source —
    /// SSID-agnostic. An outbound AX.25 connect grabs whatever source SSID is free at the time
    /// (in practice -15/-14/-13… next available, but it is indeterminate and not codified), so a
    /// partner CANNOT be matched on the source SSID; the node behind the link is identified by its
    /// base callsign. Returns the first partner whose configured call base-equals
    /// <paramref name="call"/> (partners are distinct nodes, so there is at most one), else null.
    /// </summary>
    public Partner? FindPartnerByBaseCall(string call)
    {
        ArgumentNullException.ThrowIfNull(call);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, PartnerSelect + " ORDER BY call;");
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                Partner partner = ReadPartner(reader);
                if (Callsigns.BaseEquals(partner.Call, call))
                {
                    return partner;
                }
            }

            return null;
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

    /// <summary>
    /// The whole-BBS forwarding MASTER switch, persisted in <c>meta</c> (so it survives a restart like
    /// the partners do, rather than reverting to the bbs.yaml seed). Read LIVE by the forwarding
    /// scheduler and the inbound FBB answerer; toggled at runtime from the sysop UI. Off = the
    /// safe-abort hold — no dialling out, no inbound accepted — regardless of any partner's Enabled.
    /// Null = never set (the host seeds it from BbsHostConfig.Forwarding.Enabled at first start).
    /// </summary>
    public bool? GetForwardingMaster()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null, "SELECT value FROM meta WHERE key='forwarding_master';");
            return cmd.ExecuteScalar() is string s ? s == "1" : null;
        }
    }

    /// <summary>Persists the whole-BBS forwarding master switch (see <see cref="GetForwardingMaster"/>).</summary>
    public void SetForwardingMaster(bool enabled)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(null,
                "INSERT INTO meta(key,value) VALUES('forwarding_master',$v) " +
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value;");
            cmd.Parameters.AddWithValue("$v", enabled ? "1" : "0");
            cmd.ExecuteNonQuery();
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
                "to_calls,at_calls,h_routes,h_routes_p,bbs_ha,max_rx_size,max_tx_size,allow_b2f,collect,con_timeout_seconds) " +
                "VALUES($call,$en,$int,$imm,$script,$to,$at,$hr,$hrp,$ha,$rx,$tx,$b2,$col,$cto) " +
                "ON CONFLICT(call) DO UPDATE SET enabled=excluded.enabled, " +
                "forward_interval_seconds=excluded.forward_interval_seconds, " +
                "forward_new_immediately=excluded.forward_new_immediately, connect_script=excluded.connect_script, " +
                "to_calls=excluded.to_calls, at_calls=excluded.at_calls, h_routes=excluded.h_routes, " +
                "h_routes_p=excluded.h_routes_p, bbs_ha=excluded.bbs_ha, max_rx_size=excluded.max_rx_size, " +
                "max_tx_size=excluded.max_tx_size, allow_b2f=excluded.allow_b2f, collect=excluded.collect, " +
                "con_timeout_seconds=excluded.con_timeout_seconds;");
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
            cmd.Parameters.AddWithValue("$col", partner.Collect ? 1 : 0);
            cmd.Parameters.AddWithValue("$cto", Math.Max(1, partner.ConTimeoutSeconds));
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

    /// <summary>
    /// The MaxMsgno compacting renumber (BPQ <c>MaxMsgno</c>, compat spec §6 / issue #39). Renumbers
    /// every live message densely from 1 in ascending old-number order (so chronological order is
    /// preserved), remapping every local row that references a message number, and resets the
    /// AUTOINCREMENT high-water mark so subsequently-allocated numbers continue from the new dense
    /// maximum. Returns the count of messages renumbered (rows whose number actually changed), or 0
    /// when the store is already dense (no row would move).
    ///
    /// <para><b>Referential integrity.</b> The network-wide BID (<c>&lt;msgno&gt;_&lt;BBSCALL&gt;</c>) is a
    /// frozen network identity and is NEVER rewritten — partners track it on the wire, in R: lines and
    /// in their own dedup stores, so renumbering a local sequence must not change it. Only the LOCAL
    /// <c>messages.number</c> and the rows keyed on it move: <c>recipients</c>, <c>forwards</c>,
    /// <c>attachments</c>, <c>message_read</c>, <c>sevenplus_parts.source_message_number</c>,
    /// <c>sevenplus_files.assembled_message_number</c>, the <c>bids.message_number</c> live-copy
    /// back-link, and each user's <c>last_listed_number</c> watermark (mapped to the new number of the
    /// highest surviving message at or below the old watermark, so "already seen" survives). The whole
    /// remap runs in ONE transaction with foreign-key checks deferred to COMMIT, so a crash mid-run
    /// rolls back to the pre-renumber state — the commit is the atomic boundary.</para>
    /// </summary>
    internal int RenumberMessages()
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();

            // Defer FK enforcement to COMMIT: we rewrite parents (messages.number) and children in the
            // same transaction, so a row will transiently reference a not-yet-moved parent. The check
            // at COMMIT proves the final graph is consistent; a crash before COMMIT rolls everything back.
            ExecuteRaw(_connection, tx, "PRAGMA defer_foreign_keys=ON;");

            // The dense old→new map: ascending old number → 1-based rank. ROW_NUMBER over the live set
            // (a killed-but-not-yet-purged 'K' message is still live until the K-purge deletes it, so it
            // keeps a number and is renumbered too).
            ExecuteRaw(_connection, tx,
                "CREATE TEMP TABLE _renumber AS " +
                "SELECT number AS old_number, ROW_NUMBER() OVER (ORDER BY number) AS new_number FROM messages;");

            int moved;
            using (SqliteCommand count = Command(tx,
                "SELECT COUNT(*) FROM _renumber WHERE old_number<>new_number;"))
            {
                moved = Convert.ToInt32(count.ExecuteScalar()!, CultureInfo.InvariantCulture);
            }

            if (moved == 0)
            {
                // Already dense — nothing to do; still reset the sequence so it tracks the true max.
                ExecuteRaw(_connection, tx, "DROP TABLE _renumber;");
                ResetMessageSequence(tx);
                tx.Commit();
                return 0;
            }

            // Each user's last-listed watermark → the new number of the highest live message whose OLD
            // number is at or below the user's old watermark (preserving "everything up to here is seen").
            // Computed BEFORE the messages move (it reads old numbers); applied after.
            ExecuteRaw(_connection, tx,
                "CREATE TEMP TABLE _watermark AS " +
                "SELECT u.callsign AS callsign, " +
                "COALESCE((SELECT MAX(r.new_number) FROM _renumber r WHERE r.old_number<=u.last_listed_number),0) AS new_listed " +
                "FROM users u WHERE u.last_listed_number>0;");

            // Remap the message parent first, then every child reference, then the non-FK back-links.
            // Order does not matter for correctness (FK deferred), but parents-first keeps it readable.
            ExecuteRaw(_connection, tx,
                "UPDATE messages SET number=(SELECT new_number FROM _renumber WHERE old_number=messages.number);");
            ExecuteRaw(_connection, tx,
                "UPDATE recipients SET message_number=(SELECT new_number FROM _renumber WHERE old_number=recipients.message_number);");
            ExecuteRaw(_connection, tx,
                "UPDATE forwards SET message_number=(SELECT new_number FROM _renumber WHERE old_number=forwards.message_number);");
            ExecuteRaw(_connection, tx,
                "UPDATE attachments SET message_number=(SELECT new_number FROM _renumber WHERE old_number=attachments.message_number);");
            ExecuteRaw(_connection, tx,
                "UPDATE message_read SET message_number=(SELECT new_number FROM _renumber WHERE old_number=message_read.message_number);");
            ExecuteRaw(_connection, tx,
                "UPDATE sevenplus_parts SET source_message_number=(SELECT new_number FROM _renumber WHERE old_number=sevenplus_parts.source_message_number);");
            // sevenplus_files.assembled_message_number + bids.message_number are nullable, non-FK
            // back-links; remap only the rows that actually point at a renumbered message (leave NULLs
            // and any dangling number — e.g. a bids back-link to a since-purged message — untouched).
            ExecuteRaw(_connection, tx,
                "UPDATE sevenplus_files SET assembled_message_number=(SELECT new_number FROM _renumber WHERE old_number=sevenplus_files.assembled_message_number) " +
                "WHERE assembled_message_number IN (SELECT old_number FROM _renumber);");
            ExecuteRaw(_connection, tx,
                "UPDATE bids SET message_number=(SELECT new_number FROM _renumber WHERE old_number=bids.message_number) " +
                "WHERE message_number IN (SELECT old_number FROM _renumber);");

            // Apply the precomputed watermarks.
            ExecuteRaw(_connection, tx,
                "UPDATE users SET last_listed_number=(SELECT new_listed FROM _watermark WHERE _watermark.callsign=users.callsign) " +
                "WHERE callsign IN (SELECT callsign FROM _watermark);");

            ExecuteRaw(_connection, tx, "DROP TABLE _watermark;");
            ExecuteRaw(_connection, tx, "DROP TABLE _renumber;");

            // The next AUTOINCREMENT must continue ABOVE the new dense max, never reissuing a number.
            ResetMessageSequence(tx);

            tx.Commit();
            return moved;
        }
    }

    /// <summary>
    /// Resets the AUTOINCREMENT sequence for <c>messages</c> to the current MAX(number) so the next
    /// inserted message gets MAX+1. SQLite's AUTOINCREMENT reads <c>sqlite_sequence</c>; after a
    /// renumber that compacts the high-water mark down we MUST lower it too, otherwise new messages
    /// keep the pre-renumber numbering and the ceiling is never actually relieved.
    /// </summary>
    private static void ResetMessageSequence(SqliteTransaction tx)
    {
        SqliteConnection connection = tx.Connection!;
        long max;
        using (SqliteCommand m = connection.CreateCommand())
        {
            m.Transaction = tx;
            m.CommandText = "SELECT COALESCE(MAX(number),0) FROM messages;";
            max = (long)m.ExecuteScalar()!;
        }

        // sqlite_sequence is a SQLite system table with NO declared PRIMARY KEY/UNIQUE constraint, so
        // ON CONFLICT can't target it. It has a row for `messages` once the AUTOINCREMENT table has
        // had any insert; UPDATE that row, and INSERT only when it is somehow absent (a never-inserted
        // / empty store — harmless to seed it at the current max, which is 0).
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE sqlite_sequence SET seq=$max WHERE name='messages';";
            update.Parameters.AddWithValue("$max", max);
            if (update.ExecuteNonQuery() > 0)
            {
                return;
            }
        }

        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO sqlite_sequence(name,seq) VALUES('messages',$max);";
        insert.Parameters.AddWithValue("$max", max);
        insert.ExecuteNonQuery();
    }

    internal long NowSeconds() => _time.GetUtcNow().ToUnixTimeSeconds();

    // ---------------------------------------------------------------- plumbing

    private const string PartnerSelect =
        "SELECT call,enabled,forward_interval_seconds,forward_new_immediately,connect_script," +
        "to_calls,at_calls,h_routes,h_routes_p,bbs_ha,max_rx_size,max_tx_size,allow_b2f,collect,con_timeout_seconds FROM partners";

    /// <summary>Whether <paramref name="table"/> already has a column named <paramref name="column"/> (PRAGMA table_info) — guards idempotent additive ALTERs.</summary>
    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction tx, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ExecuteRaw(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ExecuteRaw(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
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

        // v3 — inbound 7plus integration. PURELY ADDITIVE so it is safe to apply to the live lab
        // bbs.db: messages gains a `local_only` column defaulting 0 (every existing row stays a
        // forwardable, network-visible message), and two new tracking tables record the 7plus
        // part-bulletins accumulating toward a file. No existing column/row is touched or rewritten.
        if (version < 3)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            using (var ddl = connection.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV3;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='3' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 3;
        }

        // v4 — the BBS mail-password (IMAP / external-mail-client credential). PURELY ADDITIVE so it
        // is safe to apply to the live lab bbs.db: a new `mail_auth` table holds the Argon2id PHC
        // hash keyed by base callsign. No existing column/row is touched; users with no row simply
        // have no mail password set (IMAP login denied until they set one in webmail).
        if (version < 4)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            using (var ddl = connection.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV4;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='4' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 4;
        }

        // v5 — per-user read state. PURELY ADDITIVE: a new `message_read` table records that a given
        // callsign has read a given message. Personals already carry read-state on the recipient row
        // (recipients.read_utc); this table generalises read-state to messages a user is NOT a named
        // recipient of — chiefly BULLETINS, where the recipient is the category, not the reader — so an
        // IMAP client can show per-user unread bulletins. No existing column/row is touched.
        if (version < 5)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            using (var ddl = connection.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV5;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='5' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 5;
        }

        // v6 — reverse collection ("collect", compat spec §4.1 RequestReverse). PURELY ADDITIVE: the
        // partners table gains a `collect` column defaulting 0, so every existing partner stays
        // collect-off (a quiet link stays quiet — existing behaviour). No existing column/row is
        // touched; safe to apply to the live lab bbs.db.
        if (version < 6)
        {
            using SqliteTransaction tx = connection.BeginTransaction();

            // The ADD COLUMN is guarded on the column being absent: it is a pure no-op when the
            // column already exists (idempotent additive migration — SQLite has no ADD COLUMN IF
            // NOT EXISTS), so re-applying v6 over a partners table that already carries `collect`
            // upgrades cleanly rather than throwing "duplicate column name".
            if (!ColumnExists(connection, tx, "partners", "collect"))
            {
                using var ddl = connection.CreateCommand();
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV6;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='6' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 6;
        }

        // v7 — per-partner connect handshake timeout (compat spec §4.1 ConTimeout). PURELY ADDITIVE:
        // the partners table gains a `con_timeout_seconds` column defaulting 60, so every existing
        // partner keeps the historical 60 s default (the value the scheduler already used when the
        // column did not exist). This closes a store gap — the field was modelled on the Partner
        // record and exposed by the forwarding editor but never persisted, so an edited value silently
        // reverted to 60. No existing column/row is touched; safe to apply to the live lab bbs.db.
        if (version < 7)
        {
            using SqliteTransaction tx = connection.BeginTransaction();

            if (!ColumnExists(connection, tx, "partners", "con_timeout_seconds"))
            {
                using var ddl = connection.CreateCommand();
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV7;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='7' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 7;
        }

        // v8 — a human-readable hold reason. PURELY ADDITIVE: the messages table gains a nullable
        // `hold_reason` TEXT column (null for every existing row). The forwarding scheduler now holds
        // an oversize message (compat spec §4.1 "bigger local → held") instead of re-skipping it
        // every cycle, and records why here so the Sent view can show "too large for <partner>"
        // rather than a mute, perpetually-"queued" message. No existing column/row is touched.
        if (version < 8)
        {
            using SqliteTransaction tx = connection.BeginTransaction();

            if (!ColumnExists(connection, tx, "messages", "hold_reason"))
            {
                using var ddl = connection.CreateCommand();
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV8;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='8' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 8;
        }

        // v9 — persist per-partner forwarding health so the status dashboard survives a restart
        // (in-memory it reset to "—" on every restart). PURELY ADDITIVE: a new forwarding_status
        // table; nothing existing is touched. Created IF NOT EXISTS so re-runs are no-ops.
        if (version < 9)
        {
            using SqliteTransaction tx = connection.BeginTransaction();

            using (var ddl = connection.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV9;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='9' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 9;
        }

        // v10 — additive only (see Migrate): a nullable send_release_utc on messages. When set, the
        // message is a pending deferred "undo send" — held (so hidden + unforwarded) until a release
        // worker stamps it past the marker. Null for every existing row (not a deferred send). No
        // existing column/row is touched; safe to apply to the live lab bbs.db.
        if (version < 10)
        {
            using SqliteTransaction tx = connection.BeginTransaction();

            if (!ColumnExists(connection, tx, "messages", "send_release_utc"))
            {
                using var ddl = connection.CreateCommand();
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV10;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='10' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 10;
        }

        // v11 — additive only (see Migrate): the negotiated protocol mode + the peer's raw SID on the
        // forwarding_status row, so the status dashboard can show which protocol (B2/B1) each partner
        // last spoke. Two nullable TEXT columns (null for every existing row + any partner that has
        // not yet had a session parse its SID). The two ADDs are each guarded on the column being
        // absent (SQLite has no ADD COLUMN IF NOT EXISTS), so re-applying upgrades cleanly rather than
        // throwing "duplicate column name". No existing column/row is touched; safe on the live bbs.db.
        if (version < 11)
        {
            using SqliteTransaction tx = connection.BeginTransaction();

            if (!ColumnExists(connection, tx, "forwarding_status", "last_mode"))
            {
                using var ddl = connection.CreateCommand();
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV11Mode;
                ddl.ExecuteNonQuery();
            }

            if (!ColumnExists(connection, tx, "forwarding_status", "last_peer_sid"))
            {
                using var ddl = connection.CreateCommand();
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV11PeerSid;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='11' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 11;
        }

        // v12 — the White Pages network directory (issue #36). PURELY ADDITIVE: a new `whitepages`
        // table, kept ENTIRELY OUT of the mail store (messages/recipients) — WP directory state is
        // never a message row. One row per base callsign (the directory key), upserted date-wins on
        // each consumed WP update. No existing column/row is touched; safe to apply to the live lab
        // bbs.db. CREATE TABLE IF NOT EXISTS makes re-applying v12 a clean no-op.
        if (version < 12)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            using (var ddl = connection.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV12;
                ddl.ExecuteNonQuery();
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "UPDATE meta SET value='12' WHERE key='schema_version';";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 12;
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

    // v3 — additive only (see Migrate): the inbound 7plus integration.
    //   * messages.local_only — a local presentation artifact (the synthesized assembled-file
    //     message) that MUST never forward and is excluded from the BID dedup store. Defaults 0 so
    //     every pre-existing message stays a normal, forwardable, network-visible message.
    //   * sevenplus_files — one row per logical 7plus file (keyed by its identity string), carrying
    //     enough to rebuild SevenPlusPartIdentity and the link to the synthesized message once
    //     assembled. This is the placeholder/progress source for the webmail "N/M parts" entry.
    //   * sevenplus_parts — one row per received part of a file, linking the identity + part number
    //     to the source part-bulletin message it arrived in (so the listing can hide those raw
    //     messages and the assembler can fetch their bodies).
    private const string SchemaV3 = """
        ALTER TABLE messages ADD COLUMN local_only INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE sevenplus_files(
            identity_key             TEXT NOT NULL PRIMARY KEY,
            header_name              TEXT NOT NULL,
            file_size                INTEGER NOT NULL,
            total_parts              INTEGER NOT NULL,
            block_lines              INTEGER NOT NULL,
            -- The synthesized assembled-file message number. Deliberately NOT a foreign key: a file is
            -- 'assembled' as a one-way fact. If a sysop kills the synthesized message and housekeeping
            -- later purges it, the link must NOT revert to NULL (which would resurrect a stale
            -- 'complete' placeholder and keep the raw parts hidden with no file). A dangling number is
            -- harmless — nothing dereferences it except optional display — and keeps the file out of
            -- the incomplete-placeholder list permanently, which is the intended 'dealt with' state.
            assembled_message_number INTEGER
        ) WITHOUT ROWID;

        CREATE TABLE sevenplus_parts(
            identity_key          TEXT NOT NULL,
            part_number           INTEGER NOT NULL,
            source_message_number INTEGER NOT NULL REFERENCES messages(number) ON DELETE CASCADE,
            PRIMARY KEY(identity_key, part_number)
        ) WITHOUT ROWID;
        CREATE INDEX idx_sevenplus_parts_source ON sevenplus_parts(source_message_number);
        """;

    // v4 — additive only (see Migrate): the BBS mail-password used by IMAP / external mail clients.
    // Kept in its own table rather than a users column so the Argon2id hash never rides the User
    // record (no accidental exposure through ReadUser/UpsertUser/webmail/JSON). Keyed by base
    // callsign (SSID-stripped) to match personal-mailbox ownership: M0LTE and M0LTE-7 share one
    // mailbox and so share one mail password.
    private const string SchemaV4 = """
        CREATE TABLE mail_auth(
            callsign      TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
            password_hash TEXT NOT NULL,
            updated_utc   INTEGER NOT NULL
        ) WITHOUT ROWID;
        """;

    // v5 — additive only (see Migrate): per-user read state for messages the user is not a named
    // recipient of (bulletins). Keyed by base callsign + message number; the message FK cascades on
    // delete so housekeeping purges the read rows with the message.
    private const string SchemaV5 = """
        CREATE TABLE message_read(
            callsign       TEXT NOT NULL COLLATE NOCASE,
            message_number INTEGER NOT NULL REFERENCES messages(number) ON DELETE CASCADE,
            read_utc       INTEGER NOT NULL,
            PRIMARY KEY(callsign, message_number)
        ) WITHOUT ROWID;
        CREATE INDEX idx_message_read_msg ON message_read(message_number);
        """;

    // v6 — additive only (see Migrate): the reverse-collection ("collect") flag on partners. When
    // set, the forwarding scheduler dials the partner on its interval cadence even with an empty
    // queue (a deliberate poll for mail it holds for us — compat spec §4.1 RequestReverse).
    // Defaults 0 so every pre-existing partner stays collect-off (existing dial-on-queue behaviour).
    private const string SchemaV6 = """
        ALTER TABLE partners ADD COLUMN collect INTEGER NOT NULL DEFAULT 0;
        """;

    // v7 — additive only (see Migrate): the per-partner connect handshake timeout (compat spec §4.1
    // ConTimeout), defaulting 60 s — the value the scheduler used before the column existed. Lets the
    // forwarding editor actually persist a per-partner conTimeoutSeconds rather than silently revert it.
    private const string SchemaV7 = """
        ALTER TABLE partners ADD COLUMN con_timeout_seconds INTEGER NOT NULL DEFAULT 60;
        """;

    // v8 — additive only (see Migrate): a nullable hold reason on messages, so an auto-held oversize
    // message can explain itself in the UI ("too large for <partner>") instead of looking stuck.
    private const string SchemaV8 = """
        ALTER TABLE messages ADD COLUMN hold_reason TEXT;
        """;

    // v9 — additive only (see Migrate): persisted per-partner forwarding health, so the status
    // dashboard survives a node restart. One row per partner, upserted on each dial outcome.
    private const string SchemaV9 = """
        CREATE TABLE IF NOT EXISTS forwarding_status(
            partner_call         TEXT PRIMARY KEY,
            last_attempt_utc     INTEGER NOT NULL,
            ok                   INTEGER NOT NULL,
            error                TEXT,
            consecutive_failures INTEGER NOT NULL
        );
        """;

    // v10 — additive only (see Migrate): a nullable deferred-send release time on messages. Set by a
    // webmail compose with the undo-send window enabled — the message is held until this instant, when
    // the release worker clears the marker and routes it; null means it is not a deferred send.
    private const string SchemaV10 = """
        ALTER TABLE messages ADD COLUMN send_release_utc INTEGER;
        """;

    // v11 — additive only (see Migrate): the last-negotiated protocol mode ("B2"/"B1") + the peer's
    // raw SID on the forwarding_status row, so the dashboard can show what each partner last spoke.
    // Two nullable TEXT columns (null for every existing row). Each ADD is column-guarded and applied
    // separately (SQLite ALTER TABLE adds one column at a time) — see the v11 block in Migrate.
    private const string SchemaV11Mode = """
        ALTER TABLE forwarding_status ADD COLUMN last_mode TEXT;
        """;

    private const string SchemaV11PeerSid = """
        ALTER TABLE forwarding_status ADD COLUMN last_peer_sid TEXT;
        """;

    // v12 — the White Pages network directory (issue #36). A single table, SEPARATE from the mail
    // store: one row per base callsign, COLLATE NOCASE so the key matches regardless of case, no
    // rowid (the callsign IS the identity). `type` is the provenance/confidence (I/G/U); the optional
    // home_bbs/name/qth/zip are null when unknown. `record_date` (the On YYMMDD line date, stored as
    // a Unix-epoch day-midnight) is the freshness key for the date-wins upsert; `last_seen_utc` is
    // when we last ingested an entry for this call; `source` reserves the seam for future R:-line
    // harvest ('rline') vs a WP message ('wp').
    private const string SchemaV12 = """
        CREATE TABLE IF NOT EXISTS whitepages(
            callsign      TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
            type          TEXT NOT NULL DEFAULT 'G',
            home_bbs      TEXT,
            name          TEXT,
            qth           TEXT,
            zip           TEXT,
            record_date   INTEGER NOT NULL,
            last_seen_utc INTEGER NOT NULL,
            source        TEXT NOT NULL DEFAULT 'wp'
        ) WITHOUT ROWID;
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
            "SELECT m.number,m.type,m.status,m.from_call,m.at_bbs,m.bid,m.subject,m.body,m.received_from,m.created_utc,m.killed_utc,m.local_only,m.hold_reason,m.send_release_utc " +
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
            LocalOnly = reader.GetInt64(11) != 0,
            HoldReason = reader.IsDBNull(12) ? null : reader.GetString(12),
            SendReleaseUtc = reader.IsDBNull(13) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(13)),
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
            Collect = reader.GetInt64(13) != 0,
            ConTimeoutSeconds = (int)reader.GetInt64(14),
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
