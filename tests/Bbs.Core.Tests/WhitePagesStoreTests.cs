namespace Bbs.Core.Tests;

/// <summary>
/// The White Pages directory store (schema v12, issue #36): the date-wins upsert, the authoritative
/// (<c>/U</c>) override, null-never-overwrites-known, the aging sweep, and reopen-durability. All
/// callsigns are synthetic placeholders. The directory is kept entirely OUT of the mail store.
/// </summary>
public sealed class WhitePagesStoreTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    private static WhitePagesRecord Record(
        string call = "AA1AA",
        WhitePagesType type = WhitePagesType.Guessed,
        int year = 2026, int month = 6, int day = 1,
        string? homeBbs = "AA1AA.EURO",
        string? name = "Alice",
        string? qth = "Placeville",
        string? zip = "ZZ99")
        => new(call, type, new DateOnly(year, month, day), homeBbs, name, qth, zip);

    [Fact]
    public void Schema_IsAtVersion12_WhitePagesTablePresent()
    {
        Assert.Equal(12, BbsStore.CurrentSchemaVersion);
        Assert.Equal(BbsStore.CurrentSchemaVersion, _ts.Store.SchemaVersion);
        // A round-trip proves the table exists and is queryable.
        Assert.Equal(0, _ts.Store.CountWhitePages());
        Assert.Null(_ts.Store.GetWhitePages("AA1AA"));
    }

    [Fact]
    public void Upsert_NewCallsign_Inserts_AndReadsBack()
    {
        Assert.True(_ts.Store.UpsertWhitePages(Record()));

        WhitePagesEntry e = _ts.Store.GetWhitePages("AA1AA")!;
        Assert.Equal("AA1AA", e.Callsign);
        Assert.Equal(WhitePagesType.Guessed, e.Type);
        Assert.Equal(new DateOnly(2026, 6, 1), e.RecordDate);
        Assert.Equal("AA1AA.EURO", e.HomeBbs);
        Assert.Equal("Alice", e.Name);
        Assert.Equal("Placeville", e.Qth);
        Assert.Equal("ZZ99", e.Zip);
        Assert.Equal("wp", e.Source);
        Assert.Equal(1, _ts.Store.CountWhitePages());
    }

    [Fact]
    public void Upsert_LookupIsCaseInsensitive_AndSsidStripped()
    {
        _ts.Store.UpsertWhitePages(Record(call: "AA1AA"));
        Assert.NotNull(_ts.Store.GetWhitePages("aa1aa"));
        Assert.NotNull(_ts.Store.GetWhitePages("AA1AA-7")); // SSID stripped
    }

    [Fact]
    public void Upsert_NewerDate_WinsPerField()
    {
        _ts.Store.UpsertWhitePages(Record(day: 1, name: "Alice", qth: "OldTown"));
        Assert.True(_ts.Store.UpsertWhitePages(Record(day: 5, name: "Alicia", qth: "NewTown")));

        WhitePagesEntry e = _ts.Store.GetWhitePages("AA1AA")!;
        Assert.Equal(new DateOnly(2026, 6, 5), e.RecordDate);
        Assert.Equal("Alicia", e.Name);
        Assert.Equal("NewTown", e.Qth);
    }

    [Fact]
    public void Upsert_StaleDate_DoesNotOverwrite_ReturnsFalse()
    {
        _ts.Store.UpsertWhitePages(Record(day: 10, name: "Current"));
        Assert.False(_ts.Store.UpsertWhitePages(Record(day: 3, name: "Stale"))); // older ⇒ dropped

        WhitePagesEntry e = _ts.Store.GetWhitePages("AA1AA")!;
        Assert.Equal(new DateOnly(2026, 6, 10), e.RecordDate);
        Assert.Equal("Current", e.Name);
    }

    [Fact]
    public void Upsert_SameDate_IsIdempotentNoOp_ReturnsFalse()
    {
        Assert.True(_ts.Store.UpsertWhitePages(Record(day: 7)));
        Assert.False(_ts.Store.UpsertWhitePages(Record(day: 7))); // identical re-ingest ⇒ no field change
        Assert.Equal(1, _ts.Store.CountWhitePages());
    }

    [Fact]
    public void Upsert_AuthoritativeUser_OverwritesOlderStaleData_Unconditionally()
    {
        _ts.Store.UpsertWhitePages(Record(day: 20, type: WhitePagesType.Guessed, name: "Guessed"));

        // A /U record dated EARLIER still overwrites — user-supplied is authoritative.
        Assert.True(_ts.Store.UpsertWhitePages(Record(day: 1, type: WhitePagesType.User, name: "Authoritative")));

        WhitePagesEntry e = _ts.Store.GetWhitePages("AA1AA")!;
        Assert.Equal(WhitePagesType.User, e.Type);
        Assert.Equal("Authoritative", e.Name); // content overwritten unconditionally
        // The freshness key keeps the NEWER of the two dates (see
        // Upsert_AuthoritativeOlderDate_DoesNotRollFreshnessBackward) — content overwrites, date doesn't roll back.
        Assert.Equal(new DateOnly(2026, 6, 20), e.RecordDate);
    }

    [Fact]
    public void Upsert_NullIncomingField_NeverOverwritesKnownStoredValue()
    {
        _ts.Store.UpsertWhitePages(Record(day: 1, name: "Alice", qth: "Placeville", zip: "ZZ99"));

        // A newer record whose name/qth are unknown (null from '?') must keep the stored values.
        _ts.Store.UpsertWhitePages(Record(day: 5, name: null, qth: null, zip: "AB12"));

        WhitePagesEntry e = _ts.Store.GetWhitePages("AA1AA")!;
        Assert.Equal("Alice", e.Name);       // preserved
        Assert.Equal("Placeville", e.Qth);   // preserved
        Assert.Equal("AB12", e.Zip);         // freshened (non-null incoming)
        Assert.Equal(new DateOnly(2026, 6, 5), e.RecordDate);
    }

    [Fact]
    public void Sweep_PrunesEntriesNotSeenSinceCutoff_KeepsRecentlySeen()
    {
        // The sweep keys on LAST-SEEN, not record_date: a station last ingested long ago is pruned; a
        // recently-re-seen one survives even if its record content (record_date) is old.
        _ts.Store.UpsertWhitePages(Record(call: "AA1AA")); // seen at the clock's start (the "old" sighting)
        _ts.Time.Advance(TimeSpan.FromDays(200));
        DateTimeOffset cutoff = _ts.Store.Now - TimeSpan.FromDays(30);
        _ts.Store.UpsertWhitePages(Record(call: "BB2BB", day: 1)); // seen just now (recent)

        int pruned = _ts.Store.SweepWhitePages(cutoff);

        Assert.Equal(1, pruned);
        Assert.Null(_ts.Store.GetWhitePages("AA1AA"));    // not seen since cutoff → pruned
        Assert.NotNull(_ts.Store.GetWhitePages("BB2BB")); // recently seen → survives
    }

    [Fact]
    public void Sweep_KeepsActiveStation_WithOldRecordDateButRecentSighting()
    {
        // The crux of keying on last_seen: BPQ re-announces only CHANGED records, so an active station
        // keeps an old record_date but is re-seen each cycle. A stale re-ingest still bumps last_seen,
        // so the sweep must NOT prune it.
        _ts.Store.UpsertWhitePages(Record(call: "AA1AA", year: 2025, month: 1, day: 1)); // old record_date
        _ts.Time.Advance(TimeSpan.FromDays(200));
        _ts.Store.UpsertWhitePages(Record(call: "AA1AA", year: 2025, month: 1, day: 1)); // re-seen (stale no-op upsert)

        int pruned = _ts.Store.SweepWhitePages(_ts.Store.Now - TimeSpan.FromDays(30));

        Assert.Equal(0, pruned);
        Assert.NotNull(_ts.Store.GetWhitePages("AA1AA")); // old content, but recently seen → kept
    }

    [Fact]
    public void Upsert_AuthoritativeOlderDate_DoesNotRollFreshnessBackward()
    {
        // A /U record dated EARLIER overwrites CONTENT unconditionally, but the freshness key must not
        // roll backward — otherwise a later guess predating the /U could slip past the staleness guard.
        _ts.Store.UpsertWhitePages(Record(day: 20, type: WhitePagesType.Guessed, name: "Guessed"));
        _ts.Store.UpsertWhitePages(Record(day: 1, type: WhitePagesType.User, name: "Authoritative"));

        WhitePagesEntry e = _ts.Store.GetWhitePages("AA1AA")!;
        Assert.Equal("Authoritative", e.Name);                 // content overwritten
        Assert.Equal(new DateOnly(2026, 6, 20), e.RecordDate); // freshness key kept (not rolled back to day 1)

        // A guess dated between the two (day 10) is now correctly rejected as stale.
        Assert.False(_ts.Store.UpsertWhitePages(Record(day: 10, type: WhitePagesType.Guessed, name: "Slips")));
        Assert.Equal("Authoritative", _ts.Store.GetWhitePages("AA1AA")!.Name);
    }

    [Fact]
    public void Entries_SurviveReopen_DurablyPersisted()
    {
        _ts.Store.UpsertWhitePages(Record(call: "AA1AA", name: "Alice", qth: "Placeville"));

        BbsStore reopened = _ts.Reopen();

        Assert.Equal(12, reopened.SchemaVersion);
        WhitePagesEntry e = reopened.GetWhitePages("AA1AA")!;
        Assert.Equal("Alice", e.Name);
        Assert.Equal("Placeville", e.Qth);
        Assert.Equal(1, reopened.CountWhitePages());
    }
}
