namespace Bbs.Fbb.Tests;

public class RLineTests
{
    [Fact]
    public void Format_MatchesTheBpqWriterShape()
    {
        // Spec §3.14's documented example: R:120218/1023Z 8277@G8BPQ.#23.GBR.EU BPQ1.4.48
        var line = RLine.Format(
            new DateTimeOffset(2012, 2, 18, 10, 23, 0, TimeSpan.Zero),
            8277,
            "G8BPQ",
            "#23.GBR.EU",
            "BPQ1.4.48");
        Assert.Equal("R:120218/1023Z 8277@G8BPQ.#23.GBR.EU BPQ1.4.48", line);
    }

    [Fact]
    public void Format_OurOwnShape()
    {
        // Spec §3.16(c)'s PDN example.
        var line = RLine.Format(
            new DateTimeOffset(2026, 6, 11, 9, 30, 0, TimeSpan.Zero),
            123,
            "GB7PDN",
            "#23.GBR.EURO",
            "PDN0.1");
        Assert.Equal("R:260611/0930Z 123@GB7PDN.#23.GBR.EURO PDN0.1", line);
    }

    [Fact]
    public void Parse_BpqForm()
    {
        var r = RLine.TryParse("R:120218/1023Z 8277@G8BPQ.#23.GBR.EU BPQ1.4.48");
        Assert.NotNull(r);
        Assert.Equal(new DateTimeOffset(2012, 2, 18, 10, 23, 0, TimeSpan.Zero), r.Timestamp);
        Assert.Equal("G8BPQ", r.Callsign);
        Assert.Equal("G8BPQ.#23.GBR.EU", r.HierarchicalAddress);
        Assert.Equal(8277, r.MessageNumber);
        Assert.Equal("BPQ1.4.48", r.Version);
        Assert.Null(r.Bid);
    }

    [Fact]
    public void Parse_ClassicFbbForm_ExtractsBidAndQth()
    {
        // The richer FBB shape (spec §3.14): @:CALL.HIER [QTH] #:num $:BID.
        var r = RLine.TryParse("R:260611/0930Z @:GB7BPQ.#23.GBR.EURO [Bordeaux] #:1042 $:1042_GB7BPQ");
        Assert.NotNull(r);
        Assert.Equal("GB7BPQ", r.Callsign);
        Assert.Equal("GB7BPQ.#23.GBR.EURO", r.HierarchicalAddress);
        Assert.Equal(1042, r.MessageNumber);
        Assert.Equal("1042_GB7BPQ", r.Bid);
        Assert.Equal("Bordeaux", r.Qth);
    }

    [Fact]
    public void Parse_QthWithSpaces()
    {
        var r = RLine.TryParse("R:260611/0930Z @:F6FBB.FRA.EU [La Rochelle] $:9_F6FBB");
        Assert.NotNull(r);
        Assert.Equal("La Rochelle", r.Qth);
        Assert.Equal("9_F6FBB", r.Bid);
    }

    [Fact]
    public void Parse_NonRLine_ReturnsNull()
    {
        Assert.Null(RLine.TryParse("Hello world"));
    }

    [Fact]
    public void Parse_CorruptRLine_StillCountsAsRLine()
    {
        // Needed so loop counting sees the hop even when the date is junk —
        // the age check reports Unparseable (spec §3.14 "Corrupt R: Line").
        var r = RLine.TryParse("R:garbage here");
        Assert.NotNull(r);
        Assert.Null(r.Timestamp);
    }

    [Fact]
    public void ExtractLeadingRLines_StopsAtTheFirstNonRLine()
    {
        var chain = RLine.ExtractLeadingRLines(
        [
            "R:260611/0930Z 123@GB7PDN.#23.GBR.EURO PDN0.1",
            "R:260610/2100Z 99@G8BPQ.#23.GBR.EU BPQ6.0.25",
            "",
            "R:260609/0000Z 1@IGNORED.EU X1", // below the blank line: body text
        ]);
        Assert.Equal(2, chain.Count);
        Assert.Equal("GB7PDN", chain[0].Callsign);
        Assert.Equal("G8BPQ", chain[1].Callsign);
    }

    [Fact]
    public void LoopCheck_OnePriorTransitIsTolerated()
    {
        // Spec §3.14(b): hold only when OurCount > 1.
        var once = RLine.ExtractLeadingRLines(
        [
            "R:260611/0930Z 5@GB7PDN.#23.GBR.EURO PDN0.1",
            "R:260610/0930Z 4@G8BPQ.#23.GBR.EU BPQ6.0.25",
        ]);
        Assert.False(RLine.IsLikelyLooping(once, "GB7PDN"));
        Assert.Equal(1, RLine.CountCallsignOccurrences(once, "gb7pdn")); // case-insensitive

        var twice = RLine.ExtractLeadingRLines(
        [
            "R:260611/1130Z 7@GB7PDN.#23.GBR.EURO PDN0.1",
            "R:260611/1030Z 6@G8BPQ.#23.GBR.EU BPQ6.0.25",
            "R:260611/0930Z 5@GB7PDN.#23.GBR.EURO PDN0.1",
        ]);
        Assert.True(RLine.IsLikelyLooping(twice, "GB7PDN"));
    }

    [Fact]
    public void AgeCheck_UsesTheOldestLine()
    {
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var chain = RLine.ExtractLeadingRLines(
        [
            "R:260611/0930Z 5@GB7PDN.#23.GBR.EURO PDN0.1",
            "R:260520/0930Z 4@G8BPQ.#23.GBR.EU BPQ6.0.25", // 22 days old
        ]);
        Assert.Equal(RLineAgeStatus.Ok, RLine.CheckAge(chain, now, TimeSpan.FromDays(30)));
        Assert.Equal(RLineAgeStatus.TooOld, RLine.CheckAge(chain, now, TimeSpan.FromDays(10)));
    }

    [Fact]
    public void AgeCheck_FutureDatedBeyondSevenDays()
    {
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var nearFuture = RLine.ExtractLeadingRLines(["R:260615/0930Z 5@GB7PDN.GBR.EURO PDN0.1"]);
        var farFuture = RLine.ExtractLeadingRLines(["R:260711/0930Z 5@GB7PDN.GBR.EURO PDN0.1"]);
        Assert.Equal(RLineAgeStatus.Ok, RLine.CheckAge(nearFuture, now, TimeSpan.FromDays(30)));
        Assert.Equal(RLineAgeStatus.FutureDated, RLine.CheckAge(farFuture, now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void AgeCheck_UnparseableDateOrEmptyChain()
    {
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var corrupt = RLine.ExtractLeadingRLines(["R:not-a-date 5@GB7PDN.GBR.EURO PDN0.1"]);
        Assert.Equal(RLineAgeStatus.Unparseable, RLine.CheckAge(corrupt, now, TimeSpan.FromDays(30)));
        Assert.Equal(RLineAgeStatus.Unparseable, RLine.CheckAge([], now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Parse_CenturyPivot()
    {
        var nineties = RLine.TryParse("R:991231/2359Z 1@G8BPQ.GBR.EU BPQ4");
        var twenties = RLine.TryParse("R:260611/0930Z 1@G8BPQ.GBR.EU BPQ6");
        Assert.Equal(1999, nineties!.Timestamp!.Value.Year);
        Assert.Equal(2026, twenties!.Timestamp!.Value.Year);
    }

    [Fact]
    public void Parse_InvalidCalendarDate_YieldsNullTimestamp()
    {
        var r = RLine.TryParse("R:261311/0930Z 1@G8BPQ.GBR.EU BPQ6"); // month 13
        Assert.NotNull(r);
        Assert.Null(r.Timestamp);
    }
}
