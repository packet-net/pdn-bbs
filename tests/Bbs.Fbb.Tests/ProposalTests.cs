namespace Bbs.Fbb.Tests;

public class ProposalTests
{
    [Fact]
    public void Fa_EmitsSevenFieldLine()
    {
        var fa = new FaProposal('A', 'P', "M0LTE", "GB7BPQ.#23.GBR.EURO", "G8BPQ", "123_GB7PDN", 456);
        Assert.Equal("FA P M0LTE GB7BPQ.#23.GBR.EURO G8BPQ 123_GB7PDN 456", fa.ToWireLine());
    }

    [Fact]
    public void Fa_ParsesTheSpecExample()
    {
        // Spec §3.3's annotated FB example.
        var p = Assert.IsType<FaProposal>(Proposal.Parse("FB P F6FBB FC1GHV FC1MVP 24657_F6FBB 1345"));
        Assert.Equal('B', p.Verb);
        Assert.Equal('P', p.MessageType);
        Assert.Equal("F6FBB", p.From);
        Assert.Equal("FC1GHV", p.AtBbs);
        Assert.Equal("FC1MVP", p.To);
        Assert.Equal("24657_F6FBB", p.Bid);
        Assert.Equal(1345, p.Size);
        Assert.False(p.RequiresPoliteReject);
    }

    [Fact]
    public void Fa_RoundTrips()
    {
        const string line = "FA T W1AW NTS.NY.USA.NOAM 12345 99_GB7PDN 2048";
        Assert.Equal(line, Proposal.Parse(line).ToWireLine());
    }

    [Theory]
    [InlineData("FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN")] // 6 fields
    [InlineData("FA P M0LTE")]
    [InlineData("FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN notanumber")]
    [InlineData("FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN -5")]
    public void Fa_MissingOrBadFields_AreSessionFatal(string line)
    {
        // "If a field is missing upon receipt, an error message will be sent
        // immediately followed by a disconnection" [FBB-PROTO, spec §3.3].
        Assert.Throws<FbbProtocolException>(() => Proposal.Parse(line));
    }

    [Fact]
    public void Fa_OversizeTo_IsPerProposalRejectNotFatal()
    {
        // "A TO >6 chars in a received FA gets a polite '-', not a protocol
        // error" [BPQ-SRC, spec §3.3].
        var p = Assert.IsType<FaProposal>(Proposal.Parse("FA P M0LTE GB7BPQ TOOLONGCALL 1_X 10"));
        Assert.True(p.RequiresPoliteReject);
    }

    [Theory]
    [InlineData("VERYLONGCALL", "From")]
    [InlineData("", "From")]
    public void Fa_EmitLimits_AreEnforced(string from, string field)
    {
        var fa = new FaProposal('A', 'P', from, "GB7BPQ", "G8BPQ", "1_X", 1);
        var ex = Assert.Throws<FbbProtocolException>(() => fa.ToWireLine());
        Assert.Contains(field, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fa_BidOver12Chars_IsRejectedOnEmit()
    {
        var fa = new FaProposal('A', 'P', "M0LTE", "GB7BPQ", "G8BPQ", "1234567890123", 1);
        Assert.Throws<FbbProtocolException>(() => fa.ToWireLine());
    }

    [Theory]
    [InlineData("M0LTE-15", "M0LTE")]
    [InlineData("g8bpq", "G8BPQ")]
    [InlineData("LONGCALLSIGN", "LONGCA")]
    public void NormalizeCallsign_StripsSsidAndTruncates(string input, string expected)
    {
        Assert.Equal(expected, FaProposal.NormalizeCallsign(input));
    }

    [Fact]
    public void Fc_EmitsWithTrailingZero()
    {
        var fc = new FcProposal("EM", "12345_K4CJX", 1306, 281);
        Assert.Equal("FC EM 12345_K4CJX 1306 281 0", fc.ToWireLine());
    }

    [Theory]
    [InlineData("FC EM 12345_K4CJX 1306 281 0")]
    [InlineData("FC EM 12345_K4CJX 1306 281")] // F4HOF ABNF omits the 0 — accept both (spec §3.3)
    [InlineData("FC EM 12345_K4CJX 1306 281 0 M0LTE GB7BPQ G8BPQ P")] // BPQ extension fields tolerated
    public void Fc_ParsesWithAndWithoutTrailingZero(string line)
    {
        var p = Assert.IsType<FcProposal>(Proposal.Parse(line));
        Assert.Equal("EM", p.ControlType);
        Assert.Equal("12345_K4CJX", p.Mid);
        Assert.Equal(1306, p.UncompressedSize);
        Assert.Equal(281, p.CompressedSize);
    }

    [Fact]
    public void Fc_TypeMustBeTwoChars()
    {
        // "Inbound FC type field must be 2 chars (EM/CM)" [BPQ-SRC, spec §3.9].
        Assert.Throws<FbbProtocolException>(() => Proposal.Parse("FC EMX 12345 10 5 0"));
    }

    [Fact]
    public void Checksum_MatchesTheSpecWorkedExample()
    {
        // Spec §3.3: the proposal line sums to 0x69 ⇒ "F> 97".
        var checksum = ProposalBlock.ComputeChecksum(
            ["FA P M0LTE GB7BPQ.#23.GBR.EURO G8BPQ 123_GB7PDN 456"]);
        Assert.Equal(0x97, checksum);
        Assert.Equal("F> 97", ProposalBlock.BuildTerminator(checksum));
    }

    [Fact]
    public void Checksum_SumsEveryLineIncludingItsCr()
    {
        // Two empty-ish lines would each contribute their CR.
        var one = ProposalBlock.ComputeChecksum(["A"]);
        var two = ProposalBlock.ComputeChecksum(["A", "A"]);
        Assert.Equal(unchecked((byte)(-('A' + 0x0D))), one);
        Assert.Equal(unchecked((byte)(2 * -('A' + 0x0D))), two);
    }

    [Theory]
    [InlineData("F>", null)]
    [InlineData("F> 97", (byte)0x97)]
    [InlineData("F> 5", (byte)0x05)] // any-width hex
    [InlineData("F>097", (byte)0x97)]
    [InlineData("F> 0197", (byte)0x97)] // wider than 2 digits, taken mod 256
    public void Terminator_Parses(string line, byte? expected)
    {
        Assert.True(ProposalBlock.TryParseTerminator(line, out var checksum));
        Assert.Equal(expected, checksum);
    }

    [Theory]
    [InlineData("F> ZZ")]
    [InlineData("FS +")]
    [InlineData("FF")]
    public void Terminator_RejectsNonTerminators(string line)
    {
        Assert.False(ProposalBlock.TryParseTerminator(line, out _));
    }
}
