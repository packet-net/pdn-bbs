using System.Globalization;
using Bbs.Core;
using Microsoft.Data.Sqlite;

namespace Bbs.Import.Bpq;

/// <summary>Options controlling an import run.</summary>
internal sealed record ImportOptions
{
    /// <summary>The BPQ dump directory to read.</summary>
    public required string SourceDirectory { get; init; }

    /// <summary>The bbs.db path to (re)build.</summary>
    public required string TargetDatabase { get; init; }

    /// <summary>If false (default), refuse to overwrite an existing target. The deterministic rebuild model.</summary>
    public bool Force { get; init; }

    /// <summary>Parse + validate + print the summary, but write no database.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The BBS callsign to stamp into the store (the BID-suffix identity). Defaults to linmail.cfg BBSName.
    /// </summary>
    public string? OwnCallOverride { get; init; }
}

/// <summary>
/// The deterministic-rebuild importer: <c>bbs.db</c> is a pure function of the BPQ dump. Each run
/// produces a fresh database from scratch (the source is never modified), so a run is idempotent,
/// re-runnable and rollback-safe by construction.
///
/// <para>
/// It honours the three no-duplicate-transfer rules:
/// <list type="number">
/// <item>Every BID is imported VERBATIM — each message keeps its original BID, and the <c>bids</c>
/// table is seeded from EVERY WFBID.SYS record (including orphan BIDs whose message is gone but is
/// still within the dedup lifetime). No BID is auto-generated for an imported message.</item>
/// <item>Already-forwarded / still-queued legs are pre-marked: for each message the <c>forw</c> and
/// <c>fbbs</c> bitmaps are decoded (bit n → byte (n-1)/8, mask 1&lt;&lt;((n-1)%8); n = a partner's
/// BBSNumber) and written into <c>forwards</c> — <c>forw</c> bit → <c>forwarded_utc</c> stamped (sent);
/// <c>fbbs</c> bit → <c>forwarded_utc</c> NULL (queued).</item>
/// <item>The message-number high-water (<c>sqlite_sequence</c>) is set to BPQ's latest number so new
/// GB7RDG BIDs never reuse a number already on the network.</item>
/// </list>
/// </para>
///
/// <para>
/// Because <see cref="BbsStore.AddMessage"/> always stamps <c>created_utc=now</c>, AUTOINCREMENTs the
/// number, and only ever sets status N/H, the importer does NOT use it for messages. It opens the
/// freshly-migrated schema-v12 database and writes the load-bearing rows via raw SQL inside a single
/// transaction, preserving the original number, dates, status and BID exactly.
/// </para>
/// </summary>
internal static class BpqImporter
{
    /// <summary>Runs an import (or a dry run) and returns the validation summary.</summary>
    public static ImportReport Run(ImportOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        BpqSource source = BpqSource.Load(options.SourceDirectory);
        var report = new ImportReport { BbsName = source.Config.BbsName };
        report.Warnings.AddRange(source.Warnings);

        string ownCall = ResolveOwnCall(options, source, report);

        PopulateSourceCounts(report, source);

        if (options.DryRun)
        {
            // Compute the would-be target counts without writing anything.
            ComputeProjectedCounts(report, source, ownCall);
            return report;
        }

        PrepareTargetFile(options, report);

        // Open (create + migrate to schema v12). We then write the load-bearing rows via raw SQL.
        using (BbsStore store = BbsStore.Open(options.TargetDatabase, ownCall, timeProvider))
        {
            // store is opened to create + migrate the schema; we use a separate connection for raw writes.
        }

        long nowUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = options.TargetDatabase,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        using SqliteTransaction tx = connection.BeginTransaction();

        ImportMessagesAndBids(connection, tx, source, ownCall, nowUnix, report);
        ImportPartners(connection, tx, source, report);
        ImportUsers(connection, tx, source, nowUnix, report);
        SetHighWaterMark(connection, tx, source, report);

        tx.Commit();

        VerifyTargetCounts(connection, source, report);
        return report;
    }

    private static string ResolveOwnCall(ImportOptions options, BpqSource source, ImportReport report)
    {
        string call = options.OwnCallOverride ?? source.Config.BbsName;
        if (string.IsNullOrWhiteSpace(call))
        {
            call = source.Node?.BbsCall ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(call))
        {
            throw new InvalidOperationException(
                "Could not determine the BBS callsign (linmail.cfg BBSName is empty and no bpq32.cfg BBS application " +
                "was found). Pass --own-call explicitly.");
        }

        return Callsigns.Normalize(call);
    }

    private static void PopulateSourceCounts(ImportReport report, BpqSource source)
    {
        report.SourceLatestMessageNumber = source.Dirmes.LatestMessageNumber;
        report.SourceMessageHeaders = source.Dirmes.Messages.Count;
        report.SourceBids = source.Wfbid.Bids.Count;
        report.SourceBodiesOnDisk = source.Dirmes.Messages.Count - source.OrphanHeaders.Count + source.OrphanBodies.Count;
        report.OrphanHeaders = source.OrphanHeaders.Count;
        report.OrphanBodies = source.OrphanBodies.Count;
        report.SourcePartners = source.Config.Partners.Count;
        report.SourceUsers = source.Config.Users.Count;
        report.SourceBbsPartnersWithNumber = source.BbsNumberToPartner.Count;
    }

    // ---- The write path (raw SQL, one transaction) ----

    private static void ImportMessagesAndBids(
        SqliteConnection connection,
        SqliteTransaction tx,
        BpqSource source,
        string ownCall,
        long nowUnix,
        ImportReport report)
    {
        using SqliteCommand insertMsg = connection.CreateCommand();
        insertMsg.Transaction = tx;
        insertMsg.CommandText =
            """
            INSERT INTO messages (number, type, status, from_call, at_bbs, bid, subject, body, received_from, created_utc, killed_utc, local_only)
            VALUES ($number, $type, $status, $from, $at, $bid, $subject, $body, $rxfrom, $created, $killed, 0);
            """;
        SqliteParameter pNum = insertMsg.Parameters.Add("$number", SqliteType.Integer);
        SqliteParameter pType = insertMsg.Parameters.Add("$type", SqliteType.Text);
        SqliteParameter pStatus = insertMsg.Parameters.Add("$status", SqliteType.Text);
        SqliteParameter pFrom = insertMsg.Parameters.Add("$from", SqliteType.Text);
        SqliteParameter pAt = insertMsg.Parameters.Add("$at", SqliteType.Text);
        SqliteParameter pBid = insertMsg.Parameters.Add("$bid", SqliteType.Text);
        SqliteParameter pSubject = insertMsg.Parameters.Add("$subject", SqliteType.Text);
        SqliteParameter pBody = insertMsg.Parameters.Add("$body", SqliteType.Blob);
        SqliteParameter pRxFrom = insertMsg.Parameters.Add("$rxfrom", SqliteType.Text);
        SqliteParameter pCreated = insertMsg.Parameters.Add("$created", SqliteType.Integer);
        SqliteParameter pKilled = insertMsg.Parameters.Add("$killed", SqliteType.Integer);

        using SqliteCommand insertRcpt = connection.CreateCommand();
        insertRcpt.Transaction = tx;
        insertRcpt.CommandText =
            "INSERT OR IGNORE INTO recipients (message_number, to_call, read_utc, cc) VALUES ($n, $to, $read, 0);";
        SqliteParameter rN = insertRcpt.Parameters.Add("$n", SqliteType.Integer);
        SqliteParameter rTo = insertRcpt.Parameters.Add("$to", SqliteType.Text);
        SqliteParameter rRead = insertRcpt.Parameters.Add("$read", SqliteType.Integer);

        using SqliteCommand insertFwd = connection.CreateCommand();
        insertFwd.Transaction = tx;
        insertFwd.CommandText =
            "INSERT OR IGNORE INTO forwards (message_number, partner_call, queued_utc, forwarded_utc) VALUES ($n, $p, $q, $f);";
        SqliteParameter fN = insertFwd.Parameters.Add("$n", SqliteType.Integer);
        SqliteParameter fP = insertFwd.Parameters.Add("$p", SqliteType.Text);
        SqliteParameter fQ = insertFwd.Parameters.Add("$q", SqliteType.Integer);
        SqliteParameter fF = insertFwd.Parameters.Add("$f", SqliteType.Integer);

        // bids back-link from live messages: the message-keyed BID row (mirrors AddMessage's upsert).
        using SqliteCommand upsertBidForMsg = connection.CreateCommand();
        upsertBidForMsg.Transaction = tx;
        upsertBidForMsg.CommandText =
            """
            INSERT INTO bids (bid, first_seen_utc, first_seen_from, message_number)
            VALUES ($bid, $seen, $from, $num)
            ON CONFLICT(bid) DO UPDATE SET message_number = excluded.message_number;
            """;
        SqliteParameter mbBid = upsertBidForMsg.Parameters.Add("$bid", SqliteType.Text);
        SqliteParameter mbSeen = upsertBidForMsg.Parameters.Add("$seen", SqliteType.Integer);
        SqliteParameter mbFrom = upsertBidForMsg.Parameters.Add("$from", SqliteType.Text);
        SqliteParameter mbNum = upsertBidForMsg.Parameters.Add("$num", SqliteType.Integer);

        var seenMessageNumbers = new HashSet<int>();

        foreach (BpqMessageHeader h in source.Dirmes.Messages)
        {
            if (!seenMessageNumbers.Add(h.Number))
            {
                report.Warnings.Add($"Duplicate message number {h.Number} in DIRMES.SYS; keeping the first, skipping the rest.");
                continue;
            }

            char type = MapType(h.Type, report, h.Number);
            char status = MapStatus(h.Status, report, h.Number);
            byte[] body = source.ReadBody(h.Number);
            string normalizedBid = NormalizeBid(h.Bid, out bool truncated);

            if (truncated)
            {
                report.TruncatedBids++;
                report.Warnings.Add(
                    $"Message {h.Number}: BID '{h.Bid}' exceeds 12 chars; truncated to '{normalizedBid}' (matches BPQ/pdn behaviour). Verify dedup.");
            }

            if (normalizedBid.Length == 0)
            {
                // BPQ messages always carry a BID; a blank one would break dedup. Synthesise the BPQ
                // scheme so the row is valid, and flag it loudly.
                normalizedBid = BidGenerator.Generate(h.Number, ownCall);
                report.Warnings.Add(
                    $"Message {h.Number}: empty BID in DIRMES.SYS; synthesised '{normalizedBid}'. This message had no dedup identity — verify it is not a live network message.");
            }

            pNum.Value = h.Number;
            pType.Value = type.ToString();
            pStatus.Value = status.ToString();
            pFrom.Value = Callsigns.NormalizeAddressee(h.From);
            pAt.Value = NullableAt(h.Via);
            pBid.Value = normalizedBid;
            pSubject.Value = Truncate(h.Title, 60);
            pBody.Value = body;
            pRxFrom.Value = string.IsNullOrWhiteSpace(h.BbsFrom) ? DBNull.Value : Callsigns.Normalize(h.BbsFrom);
            pCreated.Value = PickDate(h.DateCreated, h.DateReceived, nowUnix);
            pKilled.Value = status == 'K' ? PickDate(h.DateChanged, h.DateReceived, nowUnix) : DBNull.Value;
            insertMsg.ExecuteNonQuery();

            report.ImportedMessages++;
            report.VerbatimBidMessages++;
            Bump(report.MessagesByType, type);
            Bump(report.MessagesByStatus, status);

            // recipient (BPQ DIRMES holds a single TO per record).
            string to = Callsigns.NormalizeAddressee(h.To);
            if (to.Length > 0)
            {
                rN.Value = h.Number;
                rTo.Value = to;
                rRead.Value = status is 'Y' or 'D' ? PickDate(h.DateChanged, h.DateReceived, nowUnix) : DBNull.Value;
                insertRcpt.ExecuteNonQuery();
            }

            // message-keyed BID row (the verbatim BID — Rule 1, live-message half).
            mbBid.Value = normalizedBid;
            mbSeen.Value = PickDate(h.DateReceived, h.DateCreated, nowUnix);
            mbFrom.Value = string.IsNullOrWhiteSpace(h.BbsFrom) ? DBNull.Value : Callsigns.Normalize(h.BbsFrom);
            mbNum.Value = h.Number;
            upsertBidForMsg.ExecuteNonQuery();

            // Pre-mark forwarding legs from the fbbs/forw bitmaps (Rule 2).
            PreMarkLegs(h, source, ownCall, nowUnix, report, fN, fP, fQ, fF, insertFwd);
        }

        // Seed the dedup store from EVERY WFBID.SYS record (Rule 1, orphan-BID half).
        SeedBidStore(connection, tx, source, nowUnix, report);
    }

    private static void PreMarkLegs(
        BpqMessageHeader h,
        BpqSource source,
        string ownCall,
        long nowUnix,
        ImportReport report,
        SqliteParameter fN,
        SqliteParameter fP,
        SqliteParameter fQ,
        SqliteParameter fF,
        SqliteCommand insertFwd)
    {
        // forw[] bit set -> already sent to that partner (forwarded_utc stamped).
        foreach (int bbsNumber in h.AlreadyForwardedBbsNumbers)
        {
            if (!TryResolvePartner(bbsNumber, source, ownCall, h.Number, "forw", report, out string partner))
            {
                continue;
            }

            fN.Value = h.Number;
            fP.Value = partner;
            fQ.Value = PickDate(h.DateReceived, h.DateCreated, nowUnix);
            fF.Value = PickDate(h.DateChanged, h.DateReceived, nowUnix);
            insertFwd.ExecuteNonQuery();
            AddLeg(report, partner, sent: true);
        }

        // fbbs[] bit set -> still queued to that partner (forwarded_utc NULL).
        foreach (int bbsNumber in h.StillToForwardBbsNumbers)
        {
            // If both bits are set (shouldn't happen — BPQ clears one as it sets the other), the
            // already-sent row above wins via INSERT OR IGNORE; the queued attempt is a no-op.
            if (!TryResolvePartner(bbsNumber, source, ownCall, h.Number, "fbbs", report, out string partner))
            {
                continue;
            }

            fN.Value = h.Number;
            fP.Value = partner;
            fQ.Value = PickDate(h.DateReceived, h.DateCreated, nowUnix);
            fF.Value = DBNull.Value; // NULL = queued
            insertFwd.ExecuteNonQuery();
            AddLeg(report, partner, sent: false);
        }
    }

    private static bool TryResolvePartner(
        int bbsNumber,
        BpqSource source,
        string ownCall,
        int messageNumber,
        string bitmap,
        ImportReport report,
        out string partner)
    {
        partner = string.Empty;
        if (!source.BbsNumberToPartner.TryGetValue(bbsNumber, out string? call))
        {
            report.Warnings.Add(
                $"Message {messageNumber}: {bitmap} bit {bbsNumber} has no partner with that BBSNumber in BBSUsers; " +
                $"forwarding leg dropped. Verify the partner mapping (a wrong map = wrong/duplicate sends).");
            return false;
        }

        if (Callsigns.BaseEquals(call, ownCall))
        {
            // A forward bit pointing at ourselves (BPQ keeps a self-entry). Never queue mail to self.
            return false;
        }

        partner = Callsigns.Normalize(call);
        return true;
    }

    private static void SeedBidStore(
        SqliteConnection connection,
        SqliteTransaction tx,
        BpqSource source,
        long nowUnix,
        ImportReport report)
    {
        // INSERT OR IGNORE: a BID already linked to a live message (above) is kept with its message
        // back-link; orphan BIDs are added with message_number NULL. Either way the dedup key exists.
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT OR IGNORE INTO bids (bid, first_seen_utc, first_seen_from, message_number) VALUES ($bid, $seen, NULL, NULL);";
        SqliteParameter pBid = cmd.Parameters.Add("$bid", SqliteType.Text);
        SqliteParameter pSeen = cmd.Parameters.Add("$seen", SqliteType.Integer);

        foreach (BpqBidRecord b in source.Wfbid.Bids)
        {
            string bid = NormalizeBid(b.Bid, out _);
            if (bid.Length == 0)
            {
                continue;
            }

            pBid.Value = bid;
            // WFBID timestamp is in days since the epoch; convert to seconds for first_seen_utc.
            pSeen.Value = b.TimestampDays > 0 ? (long)b.TimestampDays * 86400L : nowUnix;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The forwarding-partner filter, shared by the write path (<see cref="ImportPartners"/>) and the
    /// dry-run projection (<see cref="ComputeProjectedCounts"/>) so the two can never disagree. Returns
    /// the partners to IMPORT plus a human-readable list of the SKIPPED ones.
    ///
    /// Rule (Tom's call): import ONLY forwarding partners that have a BBS-checked (F_BBS) user record —
    /// "you only need to take forwarding partners whose user record has BBS checked". This drops two
    /// distinct classes of BBSForwarding entry: disabled stubs with no real BBS user, AND BBSNumber-slot
    /// collisions where a non-F_BBS partner reuses a slot already owned by the real (F_BBS) BBS (e.g.
    /// GB7MNK/GB7BRK both reuse slot 7 owned by the F_BBS GB7BPQ — keeping them would re-flood those
    /// BBSes, since the bitmap decode attributes slot 7 to GB7BPQ alone, so they would import with zero
    /// pre-marked legs). Membership is by the F_BBS user flag only (not BBSNumber): the flag is the
    /// authority. The BPQ self-entry in BBSForwarding is dropped silently (it is us, not a partner).
    /// </summary>
    private static (List<BpqPartner> Kept, List<string> Skipped) PartitionPartners(BpqSource source)
    {
        var bbsUserCalls = source.Config.Users
            .Where(u => u.IsBbs)
            .Select(u => Callsigns.StripSsid(Callsigns.Normalize(u.Call)))
            .ToHashSet(StringComparer.Ordinal);

        var kept = new List<BpqPartner>();
        var skipped = new List<string>();
        foreach (BpqPartner p in source.Config.Partners)
        {
            string call = Callsigns.Normalize(p.Call);
            if (Callsigns.BaseEquals(call, source.Config.BbsName))
            {
                continue; // BPQ keeps a self-entry in BBSForwarding; that is us, not a partner.
            }

            if (!bbsUserCalls.Contains(Callsigns.StripSsid(call)))
            {
                skipped.Add($"{call} ({(p.Enabled ? "enabled" : "disabled")})");
                continue;
            }

            kept.Add(p);
        }

        return (kept, skipped);
    }

    private static void ImportPartners(SqliteConnection connection, SqliteTransaction tx, BpqSource source, ImportReport report)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO partners
              (call, enabled, forward_interval_seconds, forward_new_immediately, connect_script,
               to_calls, at_calls, h_routes, h_routes_p, bbs_ha, max_rx_size, max_tx_size, allow_b2f, collect, con_timeout_seconds)
            VALUES
              ($call, $enabled, $interval, $newimm, $script, $to, $at, $hr, $hrp, $ha, 99999, 99999, $b2f, 0, $contimeout)
            ON CONFLICT(call) DO UPDATE SET
              enabled=excluded.enabled, forward_interval_seconds=excluded.forward_interval_seconds,
              forward_new_immediately=excluded.forward_new_immediately, connect_script=excluded.connect_script,
              to_calls=excluded.to_calls, at_calls=excluded.at_calls, h_routes=excluded.h_routes,
              h_routes_p=excluded.h_routes_p, bbs_ha=excluded.bbs_ha, allow_b2f=excluded.allow_b2f,
              con_timeout_seconds=excluded.con_timeout_seconds;
            """;
        SqliteParameter pCall = cmd.Parameters.Add("$call", SqliteType.Text);
        SqliteParameter pEnabled = cmd.Parameters.Add("$enabled", SqliteType.Integer);
        SqliteParameter pInterval = cmd.Parameters.Add("$interval", SqliteType.Integer);
        SqliteParameter pNewImm = cmd.Parameters.Add("$newimm", SqliteType.Integer);
        SqliteParameter pScript = cmd.Parameters.Add("$script", SqliteType.Text);
        SqliteParameter pTo = cmd.Parameters.Add("$to", SqliteType.Text);
        SqliteParameter pAt = cmd.Parameters.Add("$at", SqliteType.Text);
        SqliteParameter pHr = cmd.Parameters.Add("$hr", SqliteType.Text);
        SqliteParameter pHrp = cmd.Parameters.Add("$hrp", SqliteType.Text);
        SqliteParameter pHa = cmd.Parameters.Add("$ha", SqliteType.Text);
        SqliteParameter pB2f = cmd.Parameters.Add("$b2f", SqliteType.Integer);
        SqliteParameter pConTimeout = cmd.Parameters.Add("$contimeout", SqliteType.Integer);

        (List<BpqPartner> partners, List<string> skipped) = PartitionPartners(source);
        report.SkippedPartners.AddRange(skipped);

        foreach (BpqPartner p in partners)
        {
            string call = Callsigns.Normalize(p.Call);
            pCall.Value = call;
            // Controlled cutover: import EVERY partner DISABLED, regardless of BPQ's enabled flag, so
            // the migrated node forwards to no one and accepts no inbound until the sysop test-connects
            // and enables each partner one at a time ("enable per partner with confidence"). The toggle
            // gates both directions. A re-import re-disables — deterministic-rebuild, re-enable after.
            pEnabled.Value = 0;
            pInterval.Value = Math.Max(1, p.FwdInterval);
            pNewImm.Value = p.FwdNewImmediately ? 1 : 0;
            pScript.Value = string.Join('\n', p.ConnectScript.Select(BpqConnectScript.Translate));
            pTo.Value = string.Join(' ', p.ToCalls);
            pAt.Value = string.Join(' ', p.AtCalls);
            pHr.Value = string.Join(' ', p.HRoutes);
            pHrp.Value = string.Join(' ', p.HRoutesP);
            pHa.Value = p.BbsHa is null ? DBNull.Value : p.BbsHa;
            pB2f.Value = p.UseB2 ? 1 : 0;
            pConTimeout.Value = Math.Max(1, p.ConTimeout);
            cmd.ExecuteNonQuery();
            report.ImportedPartners++;
        }
    }

    private static void ImportUsers(SqliteConnection connection, SqliteTransaction tx, BpqSource source, long nowUnix, ImportReport report)
    {
        using SqliteCommand userCmd = connection.CreateCommand();
        userCmd.Transaction = tx;
        userCmd.CommandText =
            """
            INSERT INTO users (callsign, name, home_bbs, last_login_utc, last_listed_number, pdn_username)
            VALUES ($call, $name, $home, $login, $listed, NULL)
            ON CONFLICT(callsign) DO UPDATE SET
              name=excluded.name, home_bbs=excluded.home_bbs, last_login_utc=excluded.last_login_utc,
              last_listed_number=excluded.last_listed_number;
            """;
        SqliteParameter uCall = userCmd.Parameters.Add("$call", SqliteType.Text);
        SqliteParameter uName = userCmd.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter uHome = userCmd.Parameters.Add("$home", SqliteType.Text);
        SqliteParameter uLogin = userCmd.Parameters.Add("$login", SqliteType.Integer);
        SqliteParameter uListed = userCmd.Parameters.Add("$listed", SqliteType.Integer);

        using SqliteCommand wpCmd = connection.CreateCommand();
        wpCmd.Transaction = tx;
        wpCmd.CommandText =
            """
            INSERT OR IGNORE INTO whitepages (callsign, type, home_bbs, name, qth, zip, record_date, last_seen_utc, source)
            VALUES ($call, 'U', $home, $name, $qth, $zip, $date, $seen, 'wp');
            """;
        SqliteParameter wCall = wpCmd.Parameters.Add("$call", SqliteType.Text);
        SqliteParameter wHome = wpCmd.Parameters.Add("$home", SqliteType.Text);
        SqliteParameter wName = wpCmd.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter wQth = wpCmd.Parameters.Add("$qth", SqliteType.Text);
        SqliteParameter wZip = wpCmd.Parameters.Add("$zip", SqliteType.Text);
        SqliteParameter wDate = wpCmd.Parameters.Add("$date", SqliteType.Integer);
        SqliteParameter wSeen = wpCmd.Parameters.Add("$seen", SqliteType.Integer);

        long midnight = nowUnix - (nowUnix % 86400L);

        foreach (BpqUser u in source.Config.Users)
        {
            // Skip the partner BBS records and our own record — they are not human users.
            if (u.IsBbs || Callsigns.BaseEquals(u.Call, source.Config.BbsName))
            {
                continue;
            }

            string call = Callsigns.StripSsid(Callsigns.Normalize(u.Call));
            if (call.Length == 0)
            {
                continue;
            }

            uCall.Value = call;
            uName.Value = NullIfBlank(u.Name);
            uHome.Value = NullIfBlank(u.HomeBbs);
            uLogin.Value = u.TimeLastConnected > 0 ? u.TimeLastConnected : DBNull.Value;
            uListed.Value = Math.Max(0, u.LastListed);
            userCmd.ExecuteNonQuery();
            report.ImportedUsers++;

            // Surface user directory info as a white-pages record (no password — see the caveat).
            if (!string.IsNullOrWhiteSpace(u.HomeBbs) || !string.IsNullOrWhiteSpace(u.Name) ||
                !string.IsNullOrWhiteSpace(u.Qra) || !string.IsNullOrWhiteSpace(u.Zip))
            {
                wCall.Value = call;
                wHome.Value = NullIfBlank(u.HomeBbs);
                wName.Value = NullIfBlank(Truncate(u.Name, 12));
                wQth.Value = NullIfBlank(Truncate(u.Qra, 30));
                wZip.Value = NullIfBlank(Truncate(u.Zip, 8));
                wDate.Value = midnight;
                wSeen.Value = u.TimeLastConnected > 0 ? u.TimeLastConnected : nowUnix;
                wpCmd.ExecuteNonQuery();
                report.ImportedWhitePages++;
            }
        }
    }

    private static void SetHighWaterMark(SqliteConnection connection, SqliteTransaction tx, BpqSource source, ImportReport report)
    {
        // The next pdn message must get a number ABOVE BPQ's latest, so a new GB7RDG BID
        // (<n>_GB7RDG) never reuses a number already on the network (Rule 3).
        int maxImported = source.Dirmes.Messages.Count == 0 ? 0 : source.Dirmes.Messages.Max(m => m.Number);
        int highWater = Math.Max(source.Dirmes.LatestMessageNumber, maxImported);
        report.HighWaterMark = highWater;

        // sqlite_sequence drives AUTOINCREMENT: the next INSERT gets seq+1. It is a special internal
        // table with no PRIMARY KEY/UNIQUE constraint, so an UPSERT is not allowed. Inserting messages
        // with explicit numbers already creates the row and tracks the max number used; we then bump it
        // up to the high-water in case BPQ's latest > the max live number (e.g. the top message was
        // killed and purged). UPDATE if present, else INSERT.
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText =
                "UPDATE sqlite_sequence SET seq = MAX(seq, $seq) WHERE name = 'messages';";
            update.Parameters.AddWithValue("$seq", highWater);
            int rows = update.ExecuteNonQuery();
            if (rows == 0)
            {
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO sqlite_sequence (name, seq) VALUES ('messages', $seq);";
                insert.Parameters.AddWithValue("$seq", highWater);
                insert.ExecuteNonQuery();
            }
        }
    }

    // ---- Verification / projection ----

    private static void VerifyTargetCounts(SqliteConnection connection, BpqSource source, ImportReport report)
    {
        report.ImportedBids = (int)ScalarLong(connection, "SELECT COUNT(*) FROM bids;");
        int messages = (int)ScalarLong(connection, "SELECT COUNT(*) FROM messages;");
        if (messages != report.ImportedMessages)
        {
            report.Warnings.Add($"Post-import message count {messages} != expected {report.ImportedMessages}.");
        }

        // Rule-1 invariant: every WFBID BID and every live-message BID must be present in the dedup
        // store. (The store may hold FEWER rows than the raw WFBID count because the case-insensitive
        // key collapses exact duplicates and message BIDs not in WFBID still get a row — so a count
        // comparison is the wrong test; presence is the right one.)
        using SqliteCommand exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM bids WHERE bid = $bid COLLATE NOCASE LIMIT 1;";
        SqliteParameter pBid = exists.Parameters.Add("$bid", SqliteType.Text);

        int missingWfbid = 0;
        foreach (BpqBidRecord b in source.Wfbid.Bids)
        {
            string bid = NormalizeBid(b.Bid, out _);
            if (bid.Length == 0)
            {
                continue;
            }

            pBid.Value = bid;
            if (exists.ExecuteScalar() is null)
            {
                missingWfbid++;
            }
        }

        if (missingWfbid > 0)
        {
            report.AllWfbidBidsPresent = false;
            report.Warnings.Add($"{missingWfbid} BID(s) from WFBID.SYS are MISSING from the dedup store — network re-flood risk.");
        }

        int missingMsg = 0;
        foreach (BpqMessageHeader h in source.Dirmes.Messages)
        {
            string bid = NormalizeBid(h.Bid, out _);
            if (bid.Length == 0)
            {
                continue;
            }

            pBid.Value = bid;
            if (exists.ExecuteScalar() is null)
            {
                missingMsg++;
            }
        }

        if (missingMsg > 0)
        {
            report.AllMessageBidsPresent = false;
            report.Warnings.Add($"{missingMsg} message BID(s) are MISSING from the dedup store — network re-flood risk.");
        }

        long seq = ScalarLong(connection, "SELECT IFNULL(MAX(seq),0) FROM sqlite_sequence WHERE name='messages';");
        if (seq < report.HighWaterMark)
        {
            report.Warnings.Add($"sqlite_sequence seq {seq} is below the intended high-water {report.HighWaterMark}.");
        }

        // Per-user "last listed" reconciliation (fail-loud guard for the field-7 regression class): the
        // importer once hardcoded last_listed_number=0, silently resetting every user's "new mail"
        // watermark so they re-listed the whole back-catalogue on first connect. That was found by a
        // human reading source — exactly the kind of hole this assertion now makes self-reporting. The
        // count of users carrying a non-zero pointer in the import MUST match BPQ's. Mirror the EXACT
        // import filter (ImportUsers skips IsBbs partner records AND the own-BBS record), or the guard
        // false-positives on an own-call that happens to carry a pointer.
        int srcUsersWithPointer = source.Config.Users.Count(u =>
            !u.IsBbs && !Callsigns.BaseEquals(u.Call, source.Config.BbsName) && u.LastListed > 0);
        int dbUsersWithPointer = (int)ScalarLong(connection, "SELECT COUNT(*) FROM users WHERE last_listed_number > 0;");
        if (dbUsersWithPointer != srcUsersWithPointer)
        {
            report.Warnings.Add(
                $"Last-listed pointer mismatch: {srcUsersWithPointer} user(s) carry a non-zero pointer in BPQ " +
                $"but {dbUsersWithPointer} in the import — the per-user \"new mail\" watermark may be wrong " +
                "(users would re-list old mail as new). Check the BpqImporter field-7 mapping.");
        }
    }

    private static void ComputeProjectedCounts(ImportReport report, BpqSource source, string ownCall)
    {
        var bidSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BpqMessageHeader h in source.Dirmes.Messages)
        {
            char type = MapType(h.Type, report, h.Number);
            char status = MapStatus(h.Status, report, h.Number);
            Bump(report.MessagesByType, type);
            Bump(report.MessagesByStatus, status);
            report.ImportedMessages++;
            report.VerbatimBidMessages++;

            string bid = NormalizeBid(h.Bid, out bool truncated);
            if (truncated)
            {
                report.TruncatedBids++;
            }

            if (bid.Length > 0)
            {
                bidSet.Add(bid);
            }

            foreach (int n in h.AlreadyForwardedBbsNumbers)
            {
                if (source.BbsNumberToPartner.TryGetValue(n, out string? c) && !Callsigns.BaseEquals(c, ownCall))
                {
                    AddLeg(report, Callsigns.Normalize(c), sent: true);
                }
            }

            foreach (int n in h.StillToForwardBbsNumbers)
            {
                if (source.BbsNumberToPartner.TryGetValue(n, out string? c) && !Callsigns.BaseEquals(c, ownCall))
                {
                    AddLeg(report, Callsigns.Normalize(c), sent: false);
                }
            }
        }

        foreach (BpqBidRecord b in source.Wfbid.Bids)
        {
            string bid = NormalizeBid(b.Bid, out _);
            if (bid.Length > 0)
            {
                bidSet.Add(bid);
            }
        }

        report.ImportedBids = bidSet.Count;
        (List<BpqPartner> keptPartners, List<string> skippedPartners) = PartitionPartners(source);
        report.ImportedPartners = keptPartners.Count;
        report.SkippedPartners.AddRange(skippedPartners);
        report.ImportedUsers = source.Config.Users.Count(u => !u.IsBbs && !Callsigns.BaseEquals(u.Call, source.Config.BbsName));
        int maxImported = source.Dirmes.Messages.Count == 0 ? 0 : source.Dirmes.Messages.Max(m => m.Number);
        report.HighWaterMark = Math.Max(source.Dirmes.LatestMessageNumber, maxImported);
    }

    private static void PrepareTargetFile(ImportOptions options, ImportReport report)
    {
        if (File.Exists(options.TargetDatabase))
        {
            if (!options.Force)
            {
                throw new InvalidOperationException(
                    $"Target '{options.TargetDatabase}' already exists. The importer is a deterministic rebuild and " +
                    $"refuses to clobber an existing database. Delete it, choose another path, or pass --force.");
            }

            DeleteDatabaseFiles(options.TargetDatabase);
            report.Warnings.Add($"--force: deleted the existing target '{options.TargetDatabase}' and its WAL/SHM sidecars before rebuild.");
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(options.TargetDatabase));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            string p = path + suffix;
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }
    }

    // ---- Field mapping helpers ----

    private static char MapType(char bpqType, ImportReport report, int number)
    {
        char c = char.ToUpperInvariant(bpqType);
        return c switch
        {
            'B' => 'B',
            'P' => 'P',
            'T' => 'T',
            'N' => 'T', // BPQ stores corrupt NTS as 'N' and fixes to 'T' on load (BBSUtilities.c:1486).
            _ => Fallback(),
        };

        char Fallback()
        {
            report.Warnings.Add($"Message {number}: unknown type '{bpqType}'; importing as Bulletin (B).");
            return 'B';
        }
    }

    private static char MapStatus(char bpqStatus, ImportReport report, int number)
    {
        char c = bpqStatus == '$' ? '$' : char.ToUpperInvariant(bpqStatus);
        return c switch
        {
            'N' => 'N',
            'Y' => 'Y',
            '$' => '$',
            'F' => 'F',
            'K' => 'K',
            'H' => 'H',
            'D' => 'D',
            _ => Fallback(),
        };

        char Fallback()
        {
            report.Warnings.Add($"Message {number}: unknown status '{bpqStatus}'; importing as Held (H) for safety.");
            return 'H';
        }
    }

    private static string NormalizeBid(string bid, out bool truncated)
    {
        string b = bid.Trim().ToUpperInvariant();
        truncated = b.Length > Message.MaxBidLength;
        return truncated ? b[..Message.MaxBidLength] : b;
    }

    private static object NullableAt(string via)
    {
        string at = via.Trim().ToUpperInvariant();
        if (at.Length == 0)
        {
            return DBNull.Value;
        }

        return at.Length <= 40 ? at : at[..40];
    }

    private static long PickDate(long primary, long secondary, long now)
    {
        if (IsReasonable(primary))
        {
            return primary;
        }

        if (IsReasonable(secondary))
        {
            return secondary;
        }

        return now;
    }

    private static bool IsReasonable(long unix) => unix is > 315532800 and < 4102444800; // 1980..2100

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static object NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();

    private static void Bump(SortedDictionary<char, int> counts, char key)
        => counts[key] = counts.TryGetValue(key, out int v) ? v + 1 : 1;

    private static void AddLeg(ImportReport report, string partner, bool sent)
    {
        (int q, int s) = report.PartnerLegs.TryGetValue(partner, out (int Queued, int Sent) cur) ? cur : (0, 0);
        report.PartnerLegs[partner] = sent ? (q, s + 1) : (q + 1, s);
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        object? result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
