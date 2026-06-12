using System.Text;
using Microsoft.Data.Sqlite;

namespace Bbs.Core.Tests;

/// <summary>
/// The schema-v3 store surface for the inbound 7plus integration: the <c>local_only</c> message flag
/// (round-trip + forward-safety via dedup exclusion), the part-tracking tables (record / progress /
/// part bodies / mark-assembled / hide-set), and the additive v3 migration from a populated v2 db.
/// </summary>
public sealed class SevenPlusStoreTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    // ------------------------------------------------ local_only flag

    [Fact]
    public void LocalOnly_RoundTrips_DefaultsFalse()
    {
        Message normal = _ts.Store.AddMessage(Drafts.Personal(subject: "normal"));
        Assert.False(normal.LocalOnly);
        Assert.False(_ts.Store.GetMessage(normal.Number)!.LocalOnly);

        Message local = _ts.Store.AddMessage(Drafts.Personal(subject: "local") with { LocalOnly = true });
        Assert.True(local.LocalOnly);

        // Survives a reopen (read back through the schema, not the in-memory return value).
        Assert.True(_ts.Reopen().GetMessage(local.Number)!.LocalOnly);
    }

    [Fact]
    public void LocalOnly_IsExcludedFromBidDedupStore_SoItNeverCollidesOnTheNetwork()
    {
        // A local_only message must NOT collide on BID with the network: its BID is never recorded in
        // the dedup store, so a genuine inbound message that happens to share it is still accepted.
        Message local = _ts.Store.AddMessage(Drafts.Personal(bid: "SHARED_BID") with { LocalOnly = true });
        Assert.Equal("SHARED_BID", local.Bid);

        // No dedup row was written for it.
        Assert.Null(_ts.Store.LookupBid("SHARED_BID"));

        // So an inbound personal carrying that same BID is NOT rejected as a duplicate.
        Assert.Equal(BidDisposition.Accept, _ts.Store.CheckInboundBid("SHARED_BID", MessageType.Personal, "G8BPQ"));

        // Whereas a normal message with a BID does write the dedup row (control).
        _ts.Store.AddMessage(Drafts.Bulletin(bid: "NORMAL_BID"));
        Assert.NotNull(_ts.Store.LookupBid("NORMAL_BID"));
    }

    // ------------------------------------------------ part tracking

    private const string Key = "7p|10|FIELDS.JPG  |145173|17|138";

    private long RecordPart(int partNumber, int total = 17, string body = "raw 7plus body\r")
    {
        Message src = _ts.Store.AddMessage(Drafts.Personal(subject: "FIELDS.P01", body: body) with { Type = MessageType.Bulletin });
        _ts.Store.RecordSevenPlusPart(Key, "FIELDS.JPG  ", 145173, total, 138, partNumber, src.Number);
        return src.Number;
    }

    [Fact]
    public void RecordSevenPlusPart_TracksProgress_AndIsIdempotentPerPart()
    {
        Assert.Null(_ts.Store.GetSevenPlusProgress(Key));

        RecordPart(1, total: 3, body: "original part one\r");
        SevenPlusProgress p1 = _ts.Store.GetSevenPlusProgress(Key)!;
        Assert.Equal("FIELDS.JPG  ", p1.HeaderName);
        Assert.Equal(1, p1.ReceivedParts);
        Assert.Equal(3, p1.TotalParts);
        Assert.False(p1.IsComplete);
        Assert.Null(p1.AssembledMessageNumber);

        RecordPart(2, total: 3);
        Assert.Equal(2, _ts.Store.GetSevenPlusProgress(Key)!.ReceivedParts);

        // Re-recording part 1 from a different source message is a no-op (first source wins): the
        // count is unchanged and the body still resolves to the original part-1 source.
        Message dup = _ts.Store.AddMessage(Drafts.Personal(subject: "dup p1", body: "second part one\r") with { Type = MessageType.Bulletin });
        Assert.False(_ts.Store.RecordSevenPlusPart(Key, "FIELDS.JPG  ", 145173, 3, 138, 1, dup.Number));
        Assert.Equal(2, _ts.Store.GetSevenPlusProgress(Key)!.ReceivedParts);
        Assert.Equal("original part one\r", Encoding.Latin1.GetString(_ts.Store.GetSevenPlusPartBodies(Key)[0].Span));
    }

    [Fact]
    public void Progress_BecomesComplete_WhenAllPartsRecorded()
    {
        RecordPart(1, total: 3);
        RecordPart(2, total: 3);
        Assert.False(_ts.Store.GetSevenPlusProgress(Key)!.IsComplete);
        RecordPart(3, total: 3);
        Assert.True(_ts.Store.GetSevenPlusProgress(Key)!.IsComplete);
    }

    [Fact]
    public void GetSevenPlusPartBodies_ReturnsSourceBodies_InPartOrder()
    {
        // Record out of order; the bodies come back in part-number order.
        Message s2 = _ts.Store.AddMessage(Drafts.Personal(body: "part two body\r") with { Type = MessageType.Bulletin });
        _ts.Store.RecordSevenPlusPart(Key, "FIELDS.JPG  ", 145173, 3, 138, 2, s2.Number);
        Message s1 = _ts.Store.AddMessage(Drafts.Personal(body: "part one body\r") with { Type = MessageType.Bulletin });
        _ts.Store.RecordSevenPlusPart(Key, "FIELDS.JPG  ", 145173, 3, 138, 1, s1.Number);

        var bodies = _ts.Store.GetSevenPlusPartBodies(Key);
        Assert.Equal(2, bodies.Count);
        Assert.Equal("part one body\r", Encoding.Latin1.GetString(bodies[0].Span));
        Assert.Equal("part two body\r", Encoding.Latin1.GetString(bodies[1].Span));
    }

    [Fact]
    public void MarkAssembled_LinksMessage_AndIsGuardedAgainstDoubleAssembly()
    {
        RecordPart(1, total: 2);
        RecordPart(2, total: 2);

        Message synthesized = _ts.Store.AddMessage(Drafts.Personal(subject: "FIELDS.JPG") with { LocalOnly = true });
        Assert.True(_ts.Store.MarkSevenPlusAssembled(Key, synthesized.Number));
        Assert.Equal(synthesized.Number, _ts.Store.GetSevenPlusProgress(Key)!.AssembledMessageNumber);

        // A second attempt (a racing completer) does NOT overwrite the link — the loser is told no.
        Message other = _ts.Store.AddMessage(Drafts.Personal(subject: "dup") with { LocalOnly = true });
        Assert.False(_ts.Store.MarkSevenPlusAssembled(Key, other.Number));
        Assert.Equal(synthesized.Number, _ts.Store.GetSevenPlusProgress(Key)!.AssembledMessageNumber);
    }

    [Fact]
    public void IncompleteList_DropsAssembledFiles_AndCarriesSourceType()
    {
        RecordPart(1, total: 3); // bulletin source
        SevenPlusProgress pending = Assert.Single(_ts.Store.ListIncompleteSevenPlusFiles());
        Assert.Equal(Key, pending.IdentityKey);
        Assert.Equal(1, pending.ReceivedParts);
        Assert.Equal(3, pending.TotalParts);
        Assert.Equal(MessageType.Bulletin, pending.SourceType);

        // Once assembled it drops out of the incomplete list (it now lists as its synthesized message).
        RecordPart(2, total: 3);
        RecordPart(3, total: 3);
        Message synthesized = _ts.Store.AddMessage(Drafts.Bulletin() with { LocalOnly = true });
        _ts.Store.MarkSevenPlusAssembled(Key, synthesized.Number);
        Assert.Empty(_ts.Store.ListIncompleteSevenPlusFiles());
    }

    [Fact]
    public void IncompleteList_RecipientScope_OnlyReturnsFilesAddressedToThatCall()
    {
        // A personal 7plus file's placeholder must scope to its addressee. Record a part-bulletin
        // addressed to M0AAA only.
        Message src = _ts.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0AAA"],
            Subject = "FIELDS.P01",
            Body = System.Text.Encoding.Latin1.GetBytes("go_7+ part\r"),
        });
        _ts.Store.RecordSevenPlusPart(Key, "FIELDS.JPG  ", 145173, 5, 138, 1, src.Number);

        // The addressee sees it; another user does not; unscoped (bulletins) sees it.
        Assert.Single(_ts.Store.ListIncompleteSevenPlusFiles("M0AAA"));
        Assert.Empty(_ts.Store.ListIncompleteSevenPlusFiles("M0BBB"));
        Assert.Single(_ts.Store.ListIncompleteSevenPlusFiles());
    }

    [Fact]
    public void IncompleteList_DropsOrphanedFiles_WhenAllSourcePartsPurged()
    {
        // If every source part-message is physically removed (housekeeping purge → ON DELETE CASCADE
        // on sevenplus_parts), the file has zero remaining parts and must NOT show as a stale
        // "0/N parts received" ghost placeholder.
        long src = RecordPart(1, total: 3);
        Assert.Single(_ts.Store.ListIncompleteSevenPlusFiles());

        // Simulate the purge: delete the source message (cascades the part row away).
        using (var connection = new SqliteConnection($"Data Source={_ts.DbPath};Mode=ReadWrite;Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys=ON; DELETE FROM messages WHERE number=$n;";
            cmd.Parameters.AddWithValue("$n", src);
            cmd.ExecuteNonQuery();
        }

        BbsStore reopened = _ts.Reopen();
        Assert.Empty(reopened.ListIncompleteSevenPlusFiles()); // orphan dropped, no ghost
    }

    [Fact]
    public void PartMessageHideSet_IdentifiesRawPartMessages_NotOthers()
    {
        long part = RecordPart(1, total: 2);
        Message normal = _ts.Store.AddMessage(Drafts.Bulletin(subject: "unrelated"));

        Assert.True(_ts.Store.IsSevenPlusPartMessage(part));
        Assert.False(_ts.Store.IsSevenPlusPartMessage(normal.Number));

        var hidden = _ts.Store.GetSevenPlusPartMessageNumbers();
        Assert.Contains(part, hidden);
        Assert.DoesNotContain(normal.Number, hidden);
    }

    // ------------------------------------------------ additive v3 migration

    [Fact]
    public void Migration_FromV2Database_AddsLocalOnlyAndSevenPlusTables_DataSurvives()
    {
        // The live lab bbs.db is at v2. The v3 step must be additive: a populated v2 db upgrades on
        // open with every existing row intact (local_only defaulting 0) and the new tables writable.
        string path = Path.Combine(Directory.CreateTempSubdirectory("bbs-migrate-v3-").FullName, "v2.db");
        long legacyNumber;
        using (BbsStore seed = BbsStore.Open(path, "GB7PDN", _ts.Time))
        {
            legacyNumber = seed.AddMessage(Drafts.Personal(from: "G4XYZ", to: "M0LTE", subject: "legacy v2")).Number;
        }

        DowngradeToV2(path);

        using BbsStore upgraded = BbsStore.Open(path, "GB7PDN", _ts.Time);
        // Reopening always migrates to the current version (v2 → v3 → … → current); this test asserts
        // the v3 additions in particular survive the upgrade.
        Assert.Equal(BbsStore.CurrentSchemaVersion, upgraded.SchemaVersion);

        // The pre-existing row survived and now reads local_only=false through the v3 code.
        Message loaded = upgraded.GetMessage(legacyNumber)!;
        Assert.Equal("legacy v2", loaded.Subject);
        Assert.False(loaded.LocalOnly);

        // The new local_only column is writable; the new tracking tables are writable + queryable.
        Message local = upgraded.AddMessage(Drafts.Personal(subject: "post-v3") with { LocalOnly = true });
        Assert.True(upgraded.GetMessage(local.Number)!.LocalOnly);

        upgraded.RecordSevenPlusPart(Key, "FIELDS.JPG  ", 145173, 2, 138, 1, legacyNumber);
        SevenPlusProgress p = upgraded.GetSevenPlusProgress(Key)!;
        Assert.Equal(1, p.ReceivedParts);
        Assert.Equal(2, p.TotalParts);
        Assert.True(upgraded.IsSevenPlusPartMessage(legacyNumber));
    }

    /// <summary>Strips the v3+ schema additions and resets the version stamp, leaving a genuine v2 db on disk.</summary>
    private static void DowngradeToV2(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
        connection.Open();
        Exec(connection, "DROP TABLE IF EXISTS mail_auth;"); // v4 — also a later addition the seed carries
        Exec(connection, "DROP TABLE IF EXISTS sevenplus_parts;");
        Exec(connection, "DROP TABLE IF EXISTS sevenplus_files;");
        // messages is a rowid table here, so DROP COLUMN works on this SQLite — but rebuild to be
        // version-robust and reproduce the exact v2 messages shape (no local_only column).
        Exec(connection, "ALTER TABLE messages DROP COLUMN local_only;");
        Exec(connection, "UPDATE meta SET value='2' WHERE key='schema_version';");
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
