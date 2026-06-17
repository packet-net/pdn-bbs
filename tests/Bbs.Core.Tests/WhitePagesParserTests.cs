namespace Bbs.Core.Tests;

/// <summary>
/// The White Pages wire-format parser + recognition discriminator (issue #36). All callsigns/QTH are
/// synthetic placeholders. The format under test (BPQ <c>WPRoutines.c</c> / FBB <c>docwp.htm</c>):
/// <code>On &lt;YYMMDD&gt; &lt;CALL&gt;/&lt;TYPE&gt; @ &lt;HOME-HA&gt; zip &lt;ZIP&gt; &lt;NAME&gt; &lt;QTH...&gt;</code>
/// </summary>
public sealed class WhitePagesParserTests
{
    // ------------------------------------------------------------------ multi-record parse

    [Fact]
    public void Parse_MultiRecordBody_YieldsOneRecordPerLine_FieldsMapped()
    {
        const string body =
            "On 260615 AA1AA/I @ AA1AA.#99.ZZZ.EURO zip ? Alice Placeville\r\n" +
            "On 260614 BB2BB/G @ BB2BB.#88.ZZZ.EURO zip ZZ99 ? ?\r\n" +
            "On 260613 CC3CC/U @ CC3CC.AAA.BB.ZZZ.NOAM zip 00000 Carlos Other Town\r\n";

        IReadOnlyList<WhitePagesRecord> records = WhitePagesParser.Parse(body);

        Assert.Equal(3, records.Count);

        WhitePagesRecord a = records[0];
        Assert.Equal("AA1AA", a.Callsign);
        Assert.Equal(WhitePagesType.RLine, a.Type);
        Assert.Equal(new DateOnly(2026, 6, 15), a.RecordDate);
        Assert.Equal("AA1AA.#99.ZZZ.EURO", a.HomeBbs);
        Assert.Null(a.Zip); // '?' ⇒ null
        Assert.Equal("Alice", a.Name);
        Assert.Equal("Placeville", a.Qth);

        WhitePagesRecord b = records[1];
        Assert.Equal("BB2BB", b.Callsign);
        Assert.Equal(WhitePagesType.Guessed, b.Type);
        Assert.Equal("ZZ99", b.Zip);
        Assert.Null(b.Name); // '?' ⇒ null
        Assert.Null(b.Qth);  // '?' ⇒ null

        WhitePagesRecord c = records[2];
        Assert.Equal("CC3CC", c.Callsign);
        Assert.Equal(WhitePagesType.User, c.Type);
        Assert.Equal("00000", c.Zip);
        Assert.Equal("Carlos", c.Name);
        Assert.Equal("Other Town", c.Qth); // QTH takes the rest of the line (incl. spaces)
    }

    [Fact]
    public void Parse_ZipKeywordQuirk_LiteralZipIsSkipped_NextTokenIsTheValue()
    {
        // The emitter writes the literal word `zip`; the parser reads it into a throwaway token and
        // the NEXT token is the real value. Prove the literal `zip` is NOT mistaken for data.
        IReadOnlyList<WhitePagesRecord> records =
            WhitePagesParser.Parse("On 260601 AA1AA/U @ AA1AA.EURO zip AB12CD Alice Townsville\r\n");

        WhitePagesRecord r = Assert.Single(records);
        Assert.Equal("AB12CD", r.Zip);   // the token AFTER the literal `zip`
        Assert.NotEqual("zip", r.Zip);
        Assert.Equal("Alice", r.Name);
        Assert.Equal("Townsville", r.Qth);
    }

    [Fact]
    public void Parse_AbsentSlashType_DefaultsToGuessed()
    {
        WhitePagesRecord r = Assert.Single(
            WhitePagesParser.Parse("On 260601 AA1AA @ AA1AA.EURO zip ? Alice Place\r\n"));
        Assert.Equal(WhitePagesType.Guessed, r.Type);
    }

    [Fact]
    public void Parse_UnknownSlashType_FallsBackToGuessed()
    {
        WhitePagesRecord r = Assert.Single(
            WhitePagesParser.Parse("On 260601 AA1AA/Z @ AA1AA.EURO zip ? Alice Place\r\n"));
        Assert.Equal(WhitePagesType.Guessed, r.Type);
    }

    [Fact]
    public void Parse_BareCallsignHomeBbs_NoHierarchy_IsAccepted()
    {
        // FBB example: the HA may be a bare callsign with no hierarchy.
        WhitePagesRecord r = Assert.Single(
            WhitePagesParser.Parse("On 260601 AA1AA/U @ BB2BB zip ? Alice Place\r\n"));
        Assert.Equal("BB2BB", r.HomeBbs);
    }

    [Theory]
    [InlineData("\r\n")] // CRLF
    [InlineData("\n")]   // LF
    [InlineData("\r")]   // CR
    public void Parse_ToleratesAllLineEndings(string eol)
    {
        string body = $"On 260601 AA1AA/U @ AA1AA.EURO zip ? Alice Place{eol}On 260602 BB2BB/U @ BB2BB.EURO zip ? Bob Burgh{eol}";
        Assert.Equal(2, WhitePagesParser.Parse(body).Count);
    }

    // ------------------------------------------------------------------ malformed / partial tolerance

    [Fact]
    public void Parse_NonRecordLines_AreSkippedSilently()
    {
        // The message's own R: lines + blank lines + free text are skipped, not errors.
        const string body =
            "R:260615/1200Z @:AA1AA.#99.ZZZ.EURO\r\n" +
            "\r\n" +
            "Here are some WP updates:\r\n" +
            "On 260615 AA1AA/U @ AA1AA.EURO zip ? Alice Place\r\n";

        WhitePagesRecord r = Assert.Single(WhitePagesParser.Parse(body));
        Assert.Equal("AA1AA", r.Callsign);
    }

    [Fact]
    public void Parse_MalformedLineInTheMiddle_IsSkipped_RemainingRecordsStillParsed()
    {
        // The key BPQ-bug fix: a bad line must NOT truncate the rest of the body (BPQ `return`s).
        const string body =
            "On 260601 AA1AA/U @ AA1AA.EURO zip ? Alice One\r\n" +
            "On garbage line with too few tokens\r\n" +
            "On 260603 CC3CC/U @ CC3CC.EURO zip ? Carlos Three\r\n";

        IReadOnlyList<WhitePagesRecord> records = WhitePagesParser.Parse(body);
        Assert.Equal(2, records.Count);
        Assert.Equal("AA1AA", records[0].Callsign);
        Assert.Equal("CC3CC", records[1].Callsign); // proves parsing continued past the bad line
    }

    [Fact]
    public void Parse_UnparseableDate_SkipsThatLine()
    {
        Assert.Empty(WhitePagesParser.Parse("On 26XX01 AA1AA/U @ AA1AA.EURO zip ? Alice Place\r\n"));
        Assert.Empty(WhitePagesParser.Parse("On 261301 AA1AA/U @ AA1AA.EURO zip ? Alice Place\r\n")); // month 13
        Assert.Empty(WhitePagesParser.Parse("On 260631 AA1AA/U @ AA1AA.EURO zip ? Alice Place\r\n")); // 31 June
    }

    [Theory]
    [InlineData("WP")]      // 2 chars — too short (and the reserved pseudo-call: must never be a record)
    [InlineData("A")]       // 1 char
    [InlineData("TOOLONG")] // 7 chars — too long
    [InlineData("AB:CD")]   // contains ':'
    [InlineData("NODIGIT")] // not callsign-shaped
    public void Parse_BadCallsign_SkipsThatLine(string call)
    {
        Assert.Empty(WhitePagesParser.Parse($"On 260601 {call}/U @ AA1AA.EURO zip ? Alice Place\r\n"));
    }

    [Fact]
    public void Parse_MissingAtMarker_SkipsThatLine()
    {
        // tokens[3] must be the literal '@'.
        Assert.Empty(WhitePagesParser.Parse("On 260601 AA1AA/U X AA1AA.EURO zip ? Alice Place\r\n"));
    }

    [Fact]
    public void Parse_TooFewTokens_SkipsThatLine()
    {
        // Six tokens (no zip VALUE) — below the required Date/Call/AT/HA/zip/ZIP minimum.
        Assert.Empty(WhitePagesParser.Parse("On 260601 AA1AA/U @ AA1AA.EURO zip\r\n"));
    }

    [Fact]
    public void Parse_RecordEndingAtZip_NoNameOrQth_IsAccepted()
    {
        // Seven tokens: the required fields through the zip VALUE, NAME and QTH absent (optional).
        WhitePagesRecord r = Assert.Single(
            WhitePagesParser.Parse("On 260601 AA1AA/U @ AA1AA.EURO zip AB12\r\n"));
        Assert.Equal("AA1AA", r.Callsign);
        Assert.Equal("AB12", r.Zip);
        Assert.Null(r.Name);
        Assert.Null(r.Qth);
    }

    [Fact]
    public void Parse_RecordWithNameButNoQth_IsAccepted()
    {
        // Eight tokens: name present, QTH absent.
        WhitePagesRecord r = Assert.Single(
            WhitePagesParser.Parse("On 260601 AA1AA/U @ AA1AA.EURO zip AB12 Alice\r\n"));
        Assert.Equal("AB12", r.Zip);
        Assert.Equal("Alice", r.Name);
        Assert.Null(r.Qth);
    }

    [Fact]
    public void Parse_OverLongLine_IsRejected()
    {
        string longQth = new('X', 200);
        Assert.Empty(WhitePagesParser.Parse($"On 260601 AA1AA/U @ AA1AA.EURO zip ? Alice {longQth}\r\n"));
    }

    [Fact]
    public void Parse_OverCapHomeBbs_SkipsThatLine()
    {
        string longHa = new('A', 50); // > 40 cap
        Assert.Empty(WhitePagesParser.Parse($"On 260601 AA1AA/U @ {longHa} zip ? Alice Place\r\n"));
    }

    [Fact]
    public void Parse_OverCapOptionalField_BecomesNull_NotTruncated()
    {
        // An over-cap optional field is treated as unknown (null), not truncated to junk.
        string longName = new('N', 20); // > 12 cap
        WhitePagesRecord r = Assert.Single(
            WhitePagesParser.Parse($"On 260601 AA1AA/U @ AA1AA.EURO zip ? {longName} Place\r\n"));
        Assert.Null(r.Name);
        Assert.Equal("Place", r.Qth); // the rest of the line still parses
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_EmptyOrNullBody_YieldsNoRecords(string? body)
    {
        Assert.Empty(WhitePagesParser.Parse(body));
    }

    // ------------------------------------------------------------------ recognition discriminator

    [Theory]
    [InlineData("WP")]
    [InlineData("wp")]
    [InlineData("WP@GB7RDG")]
    [InlineData("WP@M9YYY")]
    [InlineData("WP-1")]
    public void IsDirectoryRecipient_MatchesReservedPseudoCall(string to)
    {
        Assert.True(WhitePagesParser.IsDirectoryRecipient(to));
    }

    [Theory]
    [InlineData("AA1AA")]      // a real-shaped call
    [InlineData("AA1AA@WP")]   // WP only in the AT — not the recipient identity
    [InlineData("WPX")]        // a different base call that merely starts WP
    [InlineData("ALL")]        // a bulletin category
    [InlineData("")]
    [InlineData(null)]
    public void IsDirectoryRecipient_RejectsEverythingElse(string? to)
    {
        Assert.False(WhitePagesParser.IsDirectoryRecipient(to));
    }
}
