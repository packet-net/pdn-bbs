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
