namespace Bbs.Fbb.Tests;

public class SidTests
{
    [Fact]
    public void Build_ProducesPinnedShape()
    {
        Assert.Equal("[PDN-0.1.0-B1FHM$]", Sid.Build("0.1.0"));
    }

    [Fact]
    public void Build_WithB2_AddsTheDigit()
    {
        Assert.Equal("[PDN-0.1.0-B12FHM$]", Sid.Build("0.1.0", offerB2: true));
    }

    [Theory]
    [InlineData("1.0-BPQ")]
    [InlineData("bpq1")]
    public void Build_RejectsVersionsThatWouldEmbedBpq(string version)
    {
        // Spec §3.2: the substring "BPQ" anywhere flips LinBPQ into
        // BPQ↔BPQ extension mode — our SID must never contain it.
        Assert.Throws<ArgumentException>(() => Sid.Build(version));
    }

    [Fact]
    public void Build_RejectsShapeBreakingVersions()
    {
        Assert.Throws<ArgumentException>(() => Sid.Build("1.0]x"));
    }

    [Fact]
    public void Parse_LinBpqAnsweringSid()
    {
        // The stock-partner answering SID from spec §3.2.
        var sid = Sid.Parse("[BPQ-6.0.25.30-B12FWIHJM$]");
        Assert.Equal("BPQ", sid.Author);
        Assert.Equal("6.0.25.30", sid.Version);
        Assert.Equal("B12FWIHJM$", sid.Features);
        Assert.True(sid.SupportsCompression);
        Assert.True(sid.SupportsB1);
        Assert.True(sid.SupportsB2);
        Assert.True(sid.SupportsBlockedFbb);
        Assert.True(sid.SupportsHierarchical);
        Assert.True(sid.SupportsMid);
        Assert.True(sid.SupportsBid);
        Assert.True(sid.MentionsBpq);
    }

    [Fact]
    public void Parse_B1OnlySid()
    {
        var sid = Sid.Parse("[PDN-0.1.0-B1FHM$]");
        Assert.True(sid.SupportsB1);
        Assert.False(sid.SupportsB2);
        Assert.True(sid.SupportsBlockedFbb);
        Assert.False(sid.MentionsBpq);
    }

    [Fact]
    public void Parse_B2ImpliesB1()
    {
        // "B2 uses B1 mode (crc on front of file)" [BPQ-SRC, spec §3.2].
        var sid = Sid.Parse("[RMS-1.0-B2FHM$]");
        Assert.True(sid.SupportsB2);
        Assert.True(sid.SupportsB1);
    }

    [Fact]
    public void Parse_PlainBIsVersionZeroOnly()
    {
        var sid = Sid.Parse("[FBB-5.15-BFHM$]");
        Assert.True(sid.SupportsCompression);
        Assert.False(sid.SupportsB1);
        Assert.False(sid.SupportsB2);
    }

    [Fact]
    public void Parse_FbbClassicSid()
    {
        // FBB emits e.g. B1FHLM$ — L is undocumented and must be inert
        // (spec §3.2 table).
        var sid = Sid.Parse("[FBB-7.00-B1FHLM$]");
        Assert.True(sid.SupportsB1);
        Assert.True(sid.SupportsMid);
        Assert.True(sid.SupportsBid);
    }

    [Fact]
    public void Parse_ToleratesUnknownLetters()
    {
        var sid = Sid.Parse("[XYZ-9.9-QB1FZK$]");
        Assert.True(sid.SupportsB1);
        Assert.True(sid.SupportsBlockedFbb);
        Assert.True(sid.SupportsBid);
    }

    [Fact]
    public void Parse_MblOnlySid_HasNoFbbFeatures()
    {
        // Spec §3.16(c): "[BPQ-6.0.25.30-IHM$] ; no B/F usable ⇒ MBL".
        var sid = Sid.Parse("[BPQ-6.0.25.30-IHM$]");
        Assert.False(sid.SupportsCompression);
        Assert.False(sid.SupportsBlockedFbb);
        Assert.True(sid.SupportsBid);
    }

    [Fact]
    public void Parse_TwoFieldSid_HasNullVersion()
    {
        // "at least two, maximum three" fields [FBB-SID, spec §3.2].
        var sid = Sid.Parse("[TNC-B1FHM$]");
        Assert.Equal("TNC", sid.Author);
        Assert.Null(sid.Version);
        Assert.True(sid.SupportsB1);
    }

    [Fact]
    public void Parse_FeaturesComeAfterTheLastHyphen()
    {
        // A hyphenated version must not pollute the feature field.
        var sid = Sid.Parse("[XR-1.0-rc1-B1FHM$]");
        Assert.Equal("XR", sid.Author);
        Assert.Equal("1.0-rc1", sid.Version);
        Assert.Equal("B1FHM$", sid.Features);
        Assert.True(sid.SupportsB1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("[nodash]")]
    [InlineData("[a-b")]
    public void TryParse_RejectsNonSids(string line)
    {
        Assert.False(Sid.TryParse(line, out _));
    }

    [Theory]
    [InlineData("[BPQ-6.0.25.30-B12FWIHJM$]", true)]
    [InlineData("de GB7BPQ>", false)]
    [InlineData("FA P A B C D 1", false)]
    [InlineData("[brackets but no dash]", false)]
    public void IsSidShaped_IsTheSessionDemuxTest(string line, bool expected)
    {
        Assert.Equal(expected, Sid.IsSidShaped(line));
    }
}
