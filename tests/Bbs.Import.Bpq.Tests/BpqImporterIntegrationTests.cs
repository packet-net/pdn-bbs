using System.Globalization;
using Bbs.Import.Bpq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Import.Bpq.Tests;

/// <summary>
/// End-to-end importer tests: build a consistent BPQ dump, rebuild a temp bbs.db, and assert the
/// three no-duplicate-transfer rules hold — verbatim BIDs, a complete dedup store (incl. orphan
/// BIDs), pre-marked forwarding legs, and the message-number high-water mark.
/// </summary>
public sealed class BpqImporterIntegrationTests : IDisposable
{
    private readonly BpqDumpFixture _dump = BpqDumpFixture.BuildReference();
    private readonly DirectoryInfo _outDir = Directory.CreateTempSubdirectory("bpq-import-out-");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _dump.Dispose();
        _outDir.Delete(recursive: true);
    }

    private string TargetDb => Path.Combine(_outDir.FullName, "bbs.db");

    private ImportReport Import(bool force = false) => BpqImporter.Run(
        new ImportOptions { SourceDirectory = _dump.Dir, TargetDatabase = TargetDb, Force = force },
        _time);

    private SqliteConnection OpenTarget()
    {
        var c = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = TargetDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    [Fact]
    public void Import_MessageCount_MatchesHeadersNotBodies()
    {
        ImportReport report = Import();

        // 4 headers; one is an orphan header (#102, no body) but still imported; the orphan body
        // (#777, no header) must NOT be imported.
        Assert.Equal(4, report.ImportedMessages);
        using SqliteConnection c = OpenTarget();
        Assert.Equal(4, ScalarLong(c, "SELECT COUNT(*) FROM messages;"));
        Assert.Equal(0, ScalarLong(c, "SELECT COUNT(*) FROM messages WHERE number=777;"));
        Assert.Equal(1, ScalarLong(c, "SELECT COUNT(*) FROM messages WHERE number=102;")); // orphan header kept
    }

    [Fact]
    public void Import_MessagesByTypeAndStatus_AreCounted()
    {
        ImportReport report = Import();
        Assert.Equal(2, report.MessagesByType['B']);
        Assert.Equal(1, report.MessagesByType['P']);
        Assert.Equal(1, report.MessagesByType['T']);
        Assert.Equal(1, report.MessagesByStatus['$']);
        Assert.Equal(1, report.MessagesByStatus['Y']);
        Assert.Equal(1, report.MessagesByStatus['K']);
        Assert.Equal(1, report.MessagesByStatus['D']);
    }

    [Fact]
    public void Import_Rule1_BidsAreVerbatim_AndOriginalNumbersPreserved()
    {
        Import();
        using SqliteConnection c = OpenTarget();

        // Original message numbers are preserved (load-bearing: they feed the BID suffix).
        Assert.Equal("14986_LU9DCE", ScalarString(c, "SELECT bid FROM messages WHERE number=100;"));
        Assert.Equal("101_GB7RDG", ScalarString(c, "SELECT bid FROM messages WHERE number=101;"));
        Assert.Equal("6323_KC2NJV", ScalarString(c, "SELECT bid FROM messages WHERE number=102;"));
        Assert.Equal("103_GB7RDG", ScalarString(c, "SELECT bid FROM messages WHERE number=103;"));
    }

    [Fact]
    public void Import_Rule1_DedupStore_IncludesOrphanBids()
    {
        Import();
        using SqliteConnection c = OpenTarget();

        // Every WFBID record is in the bids table — including the orphan whose message is gone.
        Assert.Equal(1, ScalarLong(c, "SELECT COUNT(*) FROM bids WHERE bid='9999_OLDBID';"));
        Assert.Equal(1, ScalarLong(c, "SELECT COUNT(*) FROM bids WHERE bid='14986_LU9DCE';"));
        Assert.Equal(1, ScalarLong(c, "SELECT COUNT(*) FROM bids WHERE bid='6323_KC2NJV';"));

        // The orphan BID has no message back-link; the live-message BIDs do.
        Assert.Equal(DBNull.Value, ScalarRaw(c, "SELECT message_number FROM bids WHERE bid='9999_OLDBID';"));
        Assert.Equal(100L, ScalarRaw(c, "SELECT message_number FROM bids WHERE bid='14986_LU9DCE';"));

        // 5 distinct BIDs total (4 message BIDs + 1 orphan), none lost.
        Assert.Equal(5, ScalarLong(c, "SELECT COUNT(*) FROM bids;"));
    }

    [Fact]
    public void Import_Rule1_BidDedupIsCaseInsensitive()
    {
        Import();
        using SqliteConnection c = OpenTarget();
        // bids PK is COLLATE NOCASE, mirroring BPQ's _stricmp dedup.
        Assert.Equal(1, ScalarLong(c, "SELECT COUNT(*) FROM bids WHERE bid='14986_lu9dce' COLLATE NOCASE;"));
    }

    [Fact]
    public void Import_Rule2_ForwardLegs_PreMarkedSentVsQueued()
    {
        ImportReport report = Import();
        using SqliteConnection c = OpenTarget();

        // #100: forw bit 1 (GB7BSK) = SENT; fbbs bit 5 (GB7CIP) = QUEUED.
        Assert.Equal(1, ScalarLong(c,
            "SELECT COUNT(*) FROM forwards WHERE message_number=100 AND partner_call='GB7BSK' AND forwarded_utc IS NOT NULL;"));
        Assert.Equal(1, ScalarLong(c,
            "SELECT COUNT(*) FROM forwards WHERE message_number=100 AND partner_call='GB7CIP' AND forwarded_utc IS NULL;"));

        // #101: forw bit 1 (GB7BSK) = SENT.
        Assert.Equal(1, ScalarLong(c,
            "SELECT COUNT(*) FROM forwards WHERE message_number=101 AND partner_call='GB7BSK' AND forwarded_utc IS NOT NULL;"));

        // Report per-partner legs.
        Assert.Equal((Queued: 0, Sent: 2), report.PartnerLegs["GB7BSK"]);
        Assert.Equal((Queued: 1, Sent: 0), report.PartnerLegs["GB7CIP"]);
    }

    [Fact]
    public void Import_Rule2_NeverQueuesToSelf()
    {
        Import();
        using SqliteConnection c = OpenTarget();
        // No forward row should ever target our own callsign (BBSNumber 2 = GB7RDG self-entry).
        Assert.Equal(0, ScalarLong(c, "SELECT COUNT(*) FROM forwards WHERE partner_call='GB7RDG';"));
    }

    [Fact]
    public void Import_Rule3_HighWaterMark_SetAboveLatest()
    {
        ImportReport report = Import();
        using SqliteConnection c = OpenTarget();

        // BPQ latest = 1119; the sqlite_sequence high-water must be >= that, so the next local
        // message gets 1120 (never reusing a number already on the network).
        Assert.Equal(1119, report.HighWaterMark);
        long seq = ScalarLong(c, "SELECT seq FROM sqlite_sequence WHERE name='messages';");
        Assert.True(seq >= 1119, $"sqlite_sequence seq was {seq}, expected >= 1119.");
    }

    [Fact]
    public void Import_BodyPreservedVerbatim_RLinesIntact()
    {
        Import();
        using SqliteConnection c = OpenTarget();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT body FROM messages WHERE number=100;";
        byte[] body = (byte[])cmd.ExecuteScalar()!;
        string text = System.Text.Encoding.UTF8.GetString(body);
        Assert.StartsWith("R:260601/0000Z 100@GB7RDG", text, StringComparison.Ordinal);
        Assert.Contains("World news body.", text, StringComparison.Ordinal);

        // Orphan header #102 imported with an empty body (no resurrection of a body file).
        cmd.CommandText = "SELECT length(body) FROM messages WHERE number=102;";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Import_Partners_ImportedExceptSelf()
    {
        ImportReport report = Import();
        using SqliteConnection c = OpenTarget();

        Assert.Equal(2, report.ImportedPartners); // GB7BSK + GB7CIP, not the GB7RDG self-entry
        Assert.Equal(0, ScalarLong(c, "SELECT COUNT(*) FROM partners WHERE call='GB7RDG';"));
        Assert.Equal(1, ScalarLong(c, "SELECT enabled FROM partners WHERE call='GB7CIP';"));
        Assert.Equal(1, ScalarLong(c, "SELECT allow_b2f FROM partners WHERE call='GB7CIP';"));
        Assert.Equal(0, ScalarLong(c, "SELECT allow_b2f FROM partners WHERE call='GB7BSK';"));
        Assert.Equal("C 3 GB7BSK", ScalarString(c, "SELECT connect_script FROM partners WHERE call='GB7BSK';"));
        // The connect script is BPQ-normalised at import (BpqConnectScript.Translate): the "!"
        // direct-flag on the via target is stripped (NC->C too, though this fixture uses C).
        Assert.Equal(
            "INTERLOCK 3\nC 3 GB7WEM-7\nC uhf gb7cip",
            ScalarString(c, "SELECT connect_script FROM partners WHERE call='GB7CIP';"));
    }

    [Fact]
    public void Import_Users_HumanOnly_NoPasswordRow()
    {
        ImportReport report = Import();
        using SqliteConnection c = OpenTarget();

        Assert.Equal(1, report.ImportedUsers); // only M0LTE (BBS records + self excluded)
        Assert.Equal(1, ScalarLong(c, "SELECT COUNT(*) FROM users WHERE callsign='M0LTE';"));
        Assert.Equal("Tom", ScalarString(c, "SELECT name FROM users WHERE callsign='M0LTE';"));
        Assert.Equal("GB7RDG", ScalarString(c, "SELECT home_bbs FROM users WHERE callsign='M0LTE';"));
        // The "last listed" pointer (BPQ field 7 lastmsg=29) is carried over verbatim, so the migrated
        // user is not shown the whole back-catalogue as "new" on first connect (NOT hardcoded 0).
        Assert.Equal(29, ScalarLong(c, "SELECT last_listed_number FROM users WHERE callsign='M0LTE';"));

        // Password caveat: BPQ has no Argon2id hashes, so NO mail_auth row is written (= disabled).
        Assert.Equal(0, ScalarLong(c, "SELECT COUNT(*) FROM mail_auth;"));

        // The user's directory info is surfaced as a white-pages record.
        Assert.Equal(1, ScalarLong(c, "SELECT COUNT(*) FROM whitepages WHERE callsign='M0LTE';"));
    }

    [Fact]
    public void Import_IsDeterministic_TwoRunsProduceIdenticalContent()
    {
        Import();
        byte[] first = ReadAllRowsDigest();

        // Rebuild over the top with --force; the content must be identical (deterministic rebuild).
        Import(force: true);
        byte[] second = ReadAllRowsDigest();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Import_RefusesToClobber_WithoutForce()
    {
        Import();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Import(force: false));
        Assert.Contains("refuses to clobber", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_WritesNothing_ButProjectsCounts()
    {
        ImportReport report = BpqImporter.Run(
            new ImportOptions { SourceDirectory = _dump.Dir, TargetDatabase = TargetDb, DryRun = true },
            _time);

        Assert.False(File.Exists(TargetDb));
        Assert.Equal(4, report.ImportedMessages);
        Assert.Equal(5, report.ImportedBids);
        Assert.Equal(1119, report.HighWaterMark);
        Assert.Equal((Queued: 0, Sent: 2), report.PartnerLegs["GB7BSK"]);
    }

    [Fact]
    public void Import_OracleFixture_SmokeTest()
    {
        if (!Fixtures.HasOracleState)
        {
            return; // oracle fixture is a gitignored docker-runtime artifact; present locally/in docker, absent in CI
        }

        // The shipped consistent oracle fixture (degenerate: 2 housekeeping-results messages).
        string outDb = Path.Combine(_outDir.FullName, "oracle.db");
        ImportReport report = BpqImporter.Run(
            new ImportOptions { SourceDirectory = Fixtures.OracleStateDir, TargetDatabase = outDb },
            _time);

        Assert.Equal("GB7BPQ-1", report.BbsName);
        Assert.Equal(2, report.ImportedMessages);
        Assert.True(File.Exists(outDb));
    }

    private byte[] ReadAllRowsDigest()
    {
        using SqliteConnection c = OpenTarget();
        var sb = new System.Text.StringBuilder();
        foreach (string sql in new[]
        {
            "SELECT number||'|'||type||'|'||status||'|'||from_call||'|'||IFNULL(at_bbs,'')||'|'||bid||'|'||subject FROM messages ORDER BY number;",
            "SELECT bid||'|'||IFNULL(message_number,'') FROM bids ORDER BY bid;",
            "SELECT message_number||'|'||partner_call||'|'||(forwarded_utc IS NULL) FROM forwards ORDER BY message_number, partner_call;",
            "SELECT call||'|'||enabled||'|'||connect_script FROM partners ORDER BY call;",
        })
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                sb.Append(r.GetString(0)).Append('\n');
            }

            sb.Append("---\n");
        }

        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static long ScalarLong(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ScalarString(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string ?? string.Empty;
    }

    private static object ScalarRaw(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() ?? DBNull.Value;
    }
}
