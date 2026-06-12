using System.Text;
using Microsoft.Data.Sqlite;

namespace Bbs.Core.Tests;

/// <summary>Store fundamentals: schema/migration, WAL, round-trips, numbering, field limits.</summary>
public sealed class BbsStoreTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    [Fact]
    public void Open_CreatesSchemaAtCurrentVersion()
    {
        Assert.Equal(BbsStore.CurrentSchemaVersion, _ts.Store.SchemaVersion);
    }

    [Fact]
    public void Open_IsIdempotent_DataSurvivesReopen()
    {
        Message stored = _ts.Store.AddMessage(Drafts.Personal(subject: "survives"));

        BbsStore reopened = _ts.Reopen();

        Assert.Equal(BbsStore.CurrentSchemaVersion, reopened.SchemaVersion);
        Message? loaded = reopened.GetMessage(stored.Number);
        Assert.NotNull(loaded);
        Assert.Equal("survives", loaded.Subject);
    }

    [Fact]
    public void Open_UsesWalJournalMode()
    {
        using var connection = new SqliteConnection($"Data Source={_ts.DbPath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Wal_AllowsConcurrentReaderInstance()
    {
        Message stored = _ts.Store.AddMessage(Drafts.Personal(subject: "concurrent"));

        using BbsStore reader = _ts.OpenSecond();
        Message? seen = reader.GetMessage(stored.Number);

        Assert.NotNull(seen);
        Assert.Equal("concurrent", seen.Subject);

        // And the writer can keep writing while the second connection is open.
        Message second = _ts.Store.AddMessage(Drafts.Personal(subject: "concurrent-2"));
        Assert.Equal("concurrent-2", reader.GetMessage(second.Number)!.Subject);
    }

    [Fact]
    public void AddMessage_RoundTripsAllFields()
    {
        DateTimeOffset now = _ts.Time.GetUtcNow();
        Message stored = _ts.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8BPQ"],
            At = "GB7BPQ.#23.GBR.EURO",
            Bid = "123_GB7PDN",
            Subject = "round trip",
            Body = "body line\r"u8.ToArray(),
            ReceivedFrom = "GB7BPQ-1",
        });

        Message? loaded = _ts.Store.GetMessage(stored.Number);

        Assert.NotNull(loaded);
        Assert.Equal(MessageType.Personal, loaded.Type);
        Assert.Equal(MessageStatus.Unread, loaded.Status);
        Assert.Equal("M0LTE", loaded.From);
        Assert.Equal("GB7BPQ.#23.GBR.EURO", loaded.At);
        Assert.Equal("123_GB7PDN", loaded.Bid);
        Assert.Equal("round trip", loaded.Subject);
        Assert.Equal("body line\r", loaded.GetBodyText());
        Assert.Equal("GB7BPQ-1", loaded.ReceivedFrom);
        Assert.Equal(now.ToUnixTimeSeconds(), loaded.CreatedAt.ToUnixTimeSeconds());
        Assert.Null(loaded.KilledAt);
        Assert.Equal(["G8BPQ"], loaded.Recipients.Select(r => r.ToCall));
    }

    [Fact]
    public void AddMessage_BodyIsLatin1ByteTransparent()
    {
        // £ (0xA3) and other 8-bit Latin-1 bytes must survive exactly.
        byte[] body = [0xA3, 0xE9, 0x0D, 0x41];
        Message stored = _ts.Store.AddMessage(Drafts.Personal() with { Body = body });

        Message loaded = _ts.Store.GetMessage(stored.Number)!;

        Assert.Equal(body, loaded.Body.ToArray());
        Assert.Equal("£é\rA", loaded.GetBodyText());
    }

    [Fact]
    public void AddMessage_AppliesSpecFieldLimits()
    {
        // §1.5: TO/FROM ≤6 + SSID stripped; subject ≤60; §2.3 BID ≤12; §2.4 AT ≤40.
        string longSubject = new('x', 80);
        string longAt = string.Join('.', "GB7BPQ", "#23", "GBR", "EURO", new string('Z', 30));

        Message stored = _ts.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "2E0ABC-15",
            Recipients = ["G8BPQXYZ-7"],
            At = longAt,
            Bid = "1234567890ABCDEF",
            Subject = longSubject,
            Body = ReadOnlyMemory<byte>.Empty,
        });

        Assert.Equal("2E0ABC", stored.From);
        Assert.Equal("G8BPQX", Assert.Single(stored.Recipients).ToCall);
        Assert.Equal(Message.MaxSubjectLength, stored.Subject.Length);
        Assert.Equal("1234567890AB", stored.Bid);
        Assert.Equal(Message.MaxAtLength, stored.At!.Length);
    }

    [Fact]
    public void AddMessage_HoldFlagStoresStatusH()
    {
        Message stored = _ts.Store.AddMessage(Drafts.Personal(hold: true));
        Assert.Equal(MessageStatus.Held, stored.Status);
    }

    [Fact]
    public void AddMessage_RequiresARecipient()
    {
        Assert.Throws<ArgumentException>(() => _ts.Store.AddMessage(Drafts.Personal() with { Recipients = [] }));
    }

    [Fact]
    public void MessageNumbers_AreMonotonic_EvenAfterPhysicalDeletion()
    {
        Message first = _ts.Store.AddMessage(Drafts.Personal());
        Message second = _ts.Store.AddMessage(Drafts.Personal());
        Assert.True(second.Number > first.Number);

        // Kill + purge the highest-numbered message, then add another: the number must not be reused
        // (BID identity depends on it — §2.3).
        _ts.Store.Kill(second.Number);
        Housekeeping.Run(_ts.Store, new HousekeepingPolicy());
        Assert.Null(_ts.Store.GetMessage(second.Number));

        Message third = _ts.Store.AddMessage(Drafts.Personal());
        Assert.True(third.Number > second.Number);
    }

    [Fact]
    public void GetLatestMessageNumber_TracksHighest()
    {
        Assert.Equal(0, _ts.Store.GetLatestMessageNumber());
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        Assert.Equal(m.Number, _ts.Store.GetLatestMessageNumber());
    }

    [Fact]
    public void MultiRecipientMessage_StoresOneRowPerRecipient()
    {
        Message stored = _ts.Store.AddMessage(Drafts.Personal() with { Recipients = ["G8BPQ", "M0LTE-2", "g8bpq"] });

        // Duplicate (case/SSID-collapsed) recipients fold; both targets present.
        Assert.Equal(["G8BPQ", "M0LTE"], stored.Recipients.Select(r => r.ToCall).Order());
    }

    // ------------------------------------------------ B2 completeness: To/Cc + attachments (§3.9)

    [Fact]
    public void AddMessage_StoresMultipleToAndCc_AndAttachments_RoundTrips()
    {
        byte[] a1 = [0x01, 0x02, 0x03, 0xFF, 0x00, 0x80];
        byte[] a2 = Encoding.Latin1.GetBytes("attachment two\r\n");
        Message stored = _ts.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8BPQ", "G4XYZ"],   // two To
            CcRecipients = ["M0ABC"],            // one Cc
            Subject = "with files",
            Body = Encoding.Latin1.GetBytes("body\r"),
            Attachments =
            [
                new MessageAttachment("DATA.BIN", a1),
                new MessageAttachment("notes.txt", a2),
            ],
        });

        // Reopen so we assert the persisted rows, not an in-memory echo.
        BbsStore reopened = _ts.Reopen();
        Message loaded = reopened.GetMessage(stored.Number)!;

        // Two To (cc=false) and one Cc (cc=true), To-first then Cc.
        Assert.Equal(["G4XYZ", "G8BPQ"], loaded.Recipients.Where(r => !r.Cc).Select(r => r.ToCall).Order());
        MessageRecipient cc = Assert.Single(loaded.Recipients, r => r.Cc);
        Assert.Equal("M0ABC", cc.ToCall);

        // Attachments present, in wire order, byte-exact.
        Assert.Equal(2, loaded.Attachments.Count);
        Assert.Equal("DATA.BIN", loaded.Attachments[0].Name);
        Assert.Equal(a1, loaded.Attachments[0].Content.ToArray());
        Assert.Equal("notes.txt", loaded.Attachments[1].Name);
        Assert.Equal(a2, loaded.Attachments[1].Content.ToArray());

        // The download accessor returns the bytes by exact stored name, and null for a name that
        // does not match a stored row — including a traversal-shaped name (it can only ever match a
        // row whose name is that exact string, and no stored name contains a path separator).
        Assert.Equal(a1, reopened.GetAttachment(stored.Number, "DATA.BIN")!.Value.ToArray());
        Assert.Null(reopened.GetAttachment(stored.Number, "../DATA.BIN"));
        Assert.Null(reopened.GetAttachment(stored.Number, "DATA"));
        Assert.Null(reopened.GetAttachment(stored.Number, "nope.bin"));
    }

    [Fact]
    public void AddMessage_CcThatIsAlsoTo_FoldsToTheToRow()
    {
        // A callsign appearing in both To and Cc is one row, kept as a To (one row per callsign).
        Message stored = _ts.Store.AddMessage(Drafts.Personal() with
        {
            Recipients = ["G8BPQ"],
            CcRecipients = ["G8BPQ", "M0ABC"],
        });

        Assert.Equal("G8BPQ", Assert.Single(stored.Recipients, r => !r.Cc).ToCall);
        Assert.Equal("M0ABC", Assert.Single(stored.Recipients, r => r.Cc).ToCall);
    }

    [Fact]
    public void NoAttachmentsNoCc_LeavesCollectionsEmpty()
    {
        // The common path (B1/console/webmail compose) is unchanged: empty Cc + attachments.
        Message stored = _ts.Store.AddMessage(Drafts.Personal());
        Message loaded = _ts.Reopen().GetMessage(stored.Number)!;
        Assert.Empty(loaded.Attachments);
        Assert.All(loaded.Recipients, r => Assert.False(r.Cc));
    }

    [Fact]
    public void Migration_FromV1Database_AddsCcColumnAndAttachmentsTable_DataSurvives()
    {
        // A v1 database (no `cc` column, no attachments table) with real data must upgrade on open
        // — the live lab bbs.db predates this change and the change is additive. Produce a genuine
        // v1 db: open at the current version (which has the full v1 surface), store a real message,
        // then strip the v2 additions and reset the version stamp to 1 — exactly the on-disk shape a
        // pre-v2 build left behind. Reopening must migrate it forward without touching the data.
        string path = Path.Combine(Directory.CreateTempSubdirectory("bbs-migrate-").FullName, "v1.db");
        long legacyNumber;
        using (BbsStore seed = BbsStore.Open(path, "GB7PDN", _ts.Time))
        {
            legacyNumber = seed.AddMessage(Drafts.Personal(from: "G4XYZ", to: "M0LTE", subject: "legacy subject")).Number;
        }

        DowngradeToV1(path);

        using BbsStore upgraded = BbsStore.Open(path, "GB7PDN", _ts.Time);
        Assert.Equal(BbsStore.CurrentSchemaVersion, upgraded.SchemaVersion); // upgraded to current (v1 → … → v3)

        // The pre-existing row survived and now reads through the v2 code (cc=false, no attachments).
        Message loaded = upgraded.GetMessage(legacyNumber)!;
        Assert.Equal("legacy subject", loaded.Subject);
        MessageRecipient r = Assert.Single(loaded.Recipients);
        Assert.Equal("M0LTE", r.ToCall);
        Assert.False(r.Cc);
        Assert.Empty(loaded.Attachments);

        // And the new columns/table are writable on the upgraded db.
        Message withFiles = upgraded.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G4XYZ",
            Recipients = ["M0LTE"],
            CcRecipients = ["G8BPQ"],
            Subject = "post-upgrade",
            Body = Encoding.Latin1.GetBytes("x\r"),
            Attachments = [new MessageAttachment("F.BIN", new byte[] { 9, 8, 7 })],
        });
        Message back = upgraded.GetMessage(withFiles.Number)!;
        Assert.Contains(back.Recipients, x => x.Cc && x.ToCall == "G8BPQ");
        Assert.Equal(new byte[] { 9, 8, 7 }, Assert.Single(back.Attachments).Content.ToArray());
    }

    /// <summary>Strips the v2 schema additions and resets the version stamp, leaving a genuine v1 db on disk.</summary>
    private static void DowngradeToV1(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
        connection.Open();
        // Strip the v3/v4 additions too (the seed was created at the current version, which has
        // them): a genuine v1 messages table has no local_only column, and the 7plus tracking tables
        // and the mail_auth table don't exist yet. Without this the v3 ALTER / v4 CREATE on reopen
        // fails ("duplicate column name" / "table mail_auth already exists").
        Exec(connection, "DROP TABLE IF EXISTS mail_auth;");
        Exec(connection, "DROP TABLE IF EXISTS sevenplus_parts;");
        Exec(connection, "DROP TABLE IF EXISTS sevenplus_files;");
        Exec(connection, "ALTER TABLE messages DROP COLUMN local_only;");
        Exec(connection, "DROP TABLE IF EXISTS attachments;");
        // SQLite can't DROP COLUMN on a WITHOUT ROWID legacy table cleanly across versions; rebuild
        // recipients without `cc` to reproduce the v1 shape exactly.
        Exec(connection, "ALTER TABLE recipients RENAME TO recipients_v2;");
        Exec(connection,
            """
            CREATE TABLE recipients(
                message_number INTEGER NOT NULL REFERENCES messages(number) ON DELETE CASCADE,
                to_call TEXT NOT NULL, read_utc INTEGER,
                PRIMARY KEY(message_number, to_call)) WITHOUT ROWID;
            """);
        Exec(connection, "INSERT INTO recipients(message_number,to_call,read_utc) SELECT message_number,to_call,read_utc FROM recipients_v2;");
        Exec(connection, "DROP TABLE recipients_v2;");
        Exec(connection, "CREATE INDEX IF NOT EXISTS idx_recipients_to ON recipients(to_call);");
        Exec(connection, "UPDATE meta SET value='1' WHERE key='schema_version';");
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Users_RoundTripAndTouchLogin()
    {
        _ts.Store.UpsertUser(new User
        {
            Callsign = "m0lte",
            Name = "Tom",
            HomeBbs = "gb7pdn.#23.gbr.euro",
            PdnUsername = "tom",
            LastListedNumber = 7,
        });

        User loaded = _ts.Store.GetUser("M0LTE")!;
        Assert.Equal("M0LTE", loaded.Callsign);
        Assert.Equal("Tom", loaded.Name);
        Assert.Equal("GB7PDN.#23.GBR.EURO", loaded.HomeBbs);
        Assert.Equal("tom", loaded.PdnUsername);
        Assert.Equal(7, loaded.LastListedNumber);
        Assert.Null(loaded.LastLogin);

        _ts.Store.TouchLastLogin("M0LTE");
        Assert.Equal(_ts.Time.GetUtcNow().ToUnixTimeSeconds(), _ts.Store.GetUser("M0LTE")!.LastLogin!.Value.ToUnixTimeSeconds());

        // Auto-creates on first connect (§1.1).
        _ts.Store.TouchLastLogin("2E0XYZ");
        Assert.NotNull(_ts.Store.GetUser("2E0XYZ"));

        _ts.Store.SetLastListed("M0LTE", 42);
        Assert.Equal(42, _ts.Store.GetUser("M0LTE")!.LastListedNumber);
    }

    [Fact]
    public void UserExists_MatchesOnBaseCallsign_CaseAndSsidInsensitive()
    {
        Assert.False(_ts.Store.UserExists("M0LTE"));

        _ts.Store.UpsertUser(new User { Callsign = "m0lte", Name = "Tom" });

        // Case-insensitive and SSID-agnostic on the lookup side (the routing TO is base-call).
        Assert.True(_ts.Store.UserExists("M0LTE"));
        Assert.True(_ts.Store.UserExists("m0lte"));
        Assert.True(_ts.Store.UserExists("M0LTE-7"));
        Assert.False(_ts.Store.UserExists("M0XYZ"));

        // A stored record carrying an SSID is still found by its base call.
        _ts.Store.UpsertUser(new User { Callsign = "G4ABC-2", Name = "Ann" });
        Assert.True(_ts.Store.UserExists("G4ABC"));
        Assert.True(_ts.Store.UserExists("G4ABC-9"));

        // A base call must not match an unrelated call that merely shares a prefix.
        Assert.False(_ts.Store.UserExists("G4AB"));
        Assert.False(_ts.Store.UserExists("G4ABCD"));
    }

    [Fact]
    public void EnsureUser_CreatesSkeletalRecord_IdempotentOnBaseCall()
    {
        // First inbound personal homed here for an unknown TO → a skeletal record (callsign
        // only; name/QTH/Home left null) so the mail is listable on first connect (rule #2).
        Assert.False(_ts.Store.UserExists("G0UNK"));
        Assert.True(_ts.Store.EnsureUser("G0UNK"));
        Assert.True(_ts.Store.UserExists("G0UNK"));

        User created = _ts.Store.GetUser("G0UNK")!;
        Assert.Equal("G0UNK", created.Callsign);
        Assert.Null(created.Name);
        Assert.Null(created.HomeBbs);
        Assert.Equal(0, created.LastListedNumber);

        // Idempotent: a second inbound to the same TO (with or without SSID) creates nothing
        // more and does not error.
        Assert.False(_ts.Store.EnsureUser("G0UNK"));
        Assert.False(_ts.Store.EnsureUser("G0UNK-7"));
        Assert.Single(_ts.Store.ListUsers());

        // Keyed by the base call: a record stored with an SSID is matched, never duplicated.
        _ts.Store.UpsertUser(new User { Callsign = "G4ABC-2", Name = "Ann" });
        Assert.False(_ts.Store.EnsureUser("G4ABC"));
        Assert.Equal("Ann", _ts.Store.GetUser("G4ABC-2")!.Name); // existing data untouched
        Assert.Equal(2, _ts.Store.ListUsers().Count); // G0UNK + G4ABC-2, no skeletal G4ABC added
    }

    [Fact]
    public void Partners_RoundTrip_AndListOrderedByCall()
    {
        var partner = new Partner
        {
            Call = "gb7bpq-1",
            Enabled = false,
            ForwardIntervalSeconds = 120,
            ForwardNewImmediately = true,
            ConnectScript = ["NETROM", "C GB7BPQ-1"],
            ToCalls = ["WANT", "!SALE*"],
            AtCalls = ["GB7BPQ", "*"],
            HRoutes = ["WW"],
            HRoutesP = ["GBR.EURO", "#23.GBR.EURO"],
            BbsHa = "GB7BPQ.#23.GBR.EURO",
            MaxRxSize = 20000,
            MaxTxSize = 30000,
            AllowB2F = true,
        };

        _ts.Store.UpsertPartner(partner);
        _ts.Store.UpsertPartner(new Partner { Call = "GB7AAA" });

        Partner loaded = _ts.Store.GetPartner("GB7BPQ-1")!;
        Assert.Equal("GB7BPQ-1", loaded.Call);
        Assert.False(loaded.Enabled);
        Assert.Equal(120, loaded.ForwardIntervalSeconds);
        Assert.True(loaded.ForwardNewImmediately);
        Assert.Equal(["NETROM", "C GB7BPQ-1"], loaded.ConnectScript);
        Assert.Equal(["WANT", "!SALE*"], loaded.ToCalls);
        Assert.Equal(["GB7BPQ", "*"], loaded.AtCalls);
        Assert.Equal(["WW"], loaded.HRoutes);
        Assert.Equal(["GBR.EURO", "#23.GBR.EURO"], loaded.HRoutesP);
        Assert.Equal("GB7BPQ.#23.GBR.EURO", loaded.BbsHa);
        Assert.Equal(20000, loaded.MaxRxSize);
        Assert.Equal(30000, loaded.MaxTxSize);
        Assert.True(loaded.AllowB2F);

        // Ordered by call — the deterministic routing tie-break order.
        Assert.Equal(["GB7AAA", "GB7BPQ-1"], _ts.Store.ListPartners().Select(p => p.Call));

        Assert.True(_ts.Store.DeletePartner("gb7aaa"));
        Assert.Null(_ts.Store.GetPartner("GB7AAA"));
    }
}
