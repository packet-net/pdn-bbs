using Microsoft.Data.Sqlite;

namespace Bbs.Core.Tests;

/// <summary>
/// Per-user read state (schema v5): the <c>message_read</c> table and
/// <see cref="BbsStore.SetReadByUser"/>/<see cref="BbsStore.IsReadByUser"/> that back per-user unread
/// for messages a user is not a named recipient of (bulletins, in the IMAP server).
/// </summary>
public sealed class ReadStateTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    [Fact]
    public void SetThenIs_RoundTrips_AndDefaultsUnread()
    {
        Message b = _ts.Store.AddMessage(Drafts.Bulletin(to: "ALL", subject: "net tonight"));
        Assert.False(_ts.Store.IsReadByUser("M0LTE", b.Number)); // nobody has read it yet

        _ts.Store.SetReadByUser("M0LTE", b.Number);
        Assert.True(_ts.Store.IsReadByUser("M0LTE", b.Number));
    }

    [Fact]
    public void ReadState_IsPerUser()
    {
        Message b = _ts.Store.AddMessage(Drafts.Bulletin(to: "ALL"));
        _ts.Store.SetReadByUser("M0LTE", b.Number);

        Assert.True(_ts.Store.IsReadByUser("M0LTE", b.Number));
        Assert.False(_ts.Store.IsReadByUser("G4ABC", b.Number)); // another user is unaffected
    }

    [Fact]
    public void ReadState_IsCaseInsensitive_AndSsidStripped()
    {
        Message b = _ts.Store.AddMessage(Drafts.Bulletin(to: "ALL"));
        _ts.Store.SetReadByUser("m0lte", b.Number);

        Assert.True(_ts.Store.IsReadByUser("M0LTE", b.Number));
        Assert.True(_ts.Store.IsReadByUser("M0LTE-7", b.Number)); // SSID shares the operator's read-state
    }

    [Fact]
    public void SetReadByUser_IsIdempotent()
    {
        Message b = _ts.Store.AddMessage(Drafts.Bulletin(to: "ALL"));
        _ts.Store.SetReadByUser("M0LTE", b.Number);
        _ts.Store.SetReadByUser("M0LTE", b.Number); // no throw, still read
        Assert.True(_ts.Store.IsReadByUser("M0LTE", b.Number));
    }

    [Fact]
    public void ReadState_SurvivesReopen()
    {
        Message b = _ts.Store.AddMessage(Drafts.Bulletin(to: "ALL"));
        _ts.Store.SetReadByUser("M0LTE", b.Number);
        _ts.Reopen();
        Assert.True(_ts.Store.IsReadByUser("M0LTE", b.Number));
    }

    [Fact]
    public void Migration_FromV4Database_AddsMessageRead_DataSurvives()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("bbs-migrate-v5-").FullName, "v4.db");
        long legacyNumber;
        using (BbsStore seed = BbsStore.Open(path, "GB7PDN", _ts.Time))
        {
            legacyNumber = seed.AddMessage(Drafts.Bulletin(to: "ALL", subject: "legacy v4")).Number;
        }

        DowngradeToV4(path);

        using BbsStore upgraded = BbsStore.Open(path, "GB7PDN", _ts.Time);
        Assert.Equal(BbsStore.CurrentSchemaVersion, upgraded.SchemaVersion);
        Assert.Equal("legacy v4", upgraded.GetMessage(legacyNumber)!.Subject); // data intact

        // The new message_read table is writable + queryable through the v5 code.
        upgraded.SetReadByUser("M0LTE", legacyNumber);
        Assert.True(upgraded.IsReadByUser("M0LTE", legacyNumber));
    }

    /// <summary>Strips the v5 schema addition and resets the version stamp, leaving a genuine v4 db on disk.</summary>
    private static void DowngradeToV4(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
        connection.Open();
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE IF EXISTS message_read;";
            drop.ExecuteNonQuery();
        }

        using var stamp = connection.CreateCommand();
        stamp.CommandText = "UPDATE meta SET value='4' WHERE key='schema_version';";
        stamp.ExecuteNonQuery();
    }
}
