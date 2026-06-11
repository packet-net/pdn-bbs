namespace Bbs.Fbb.Tests;

public class FsResponseTests
{
    [Fact]
    public void Emit_PlusMinusEquals()
    {
        var line = FsResponse.Emit([FsAnswer.Accept, FsAnswer.AlreadyHave, FsAnswer.Defer]);
        Assert.Equal("FS +-=", line);
    }

    [Fact]
    public void Emit_OffsetAccept_UsesBangForm()
    {
        var line = FsResponse.Emit([FsAnswer.AcceptFromOffset(345), FsAnswer.Accept]);
        Assert.Equal("FS !345+", line);
    }

    [Theory]
    [InlineData(FsAnswerKind.Reject)]
    [InlineData(FsAnswerKind.ProposalError)]
    public void Emit_NeverEmitsHRorE(FsAnswerKind kind)
    {
        // Spec §8 LATER: "FS H/R/E emission — LinBPQ never emits them".
        Assert.Throws<ArgumentException>(() => FsResponse.Emit([new FsAnswer(kind)]));
    }

    [Fact]
    public void Parse_TheFullAlphabet()
    {
        var answers = FsResponse.Parse("FS +Y-N=LHRE");
        Assert.Equal(
            [
                FsAnswerKind.Accept,
                FsAnswerKind.Accept,
                FsAnswerKind.AlreadyHave,
                FsAnswerKind.AlreadyHave,
                FsAnswerKind.Defer,
                FsAnswerKind.Defer,
                FsAnswerKind.Defer, // H = defer from a proposal-sender's POV (spec §3.4)
                FsAnswerKind.Reject,
                FsAnswerKind.ProposalError,
            ],
            answers.Select(a => a.Kind).ToArray());
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var answers = FsResponse.Parse("fs +y-nh");
        Assert.Equal(5, answers.Count);
        Assert.Equal(FsAnswerKind.Defer, answers[4].Kind);
    }

    [Theory]
    [InlineData("FS !345", 345)]
    [InlineData("FS A345", 345)] // 'An' alias (spec §3.4)
    [InlineData("FS !0", 0)]
    public void Parse_OffsetForms(string line, int offset)
    {
        var answers = FsResponse.Parse(line);
        var answer = Assert.Single(answers);
        Assert.Equal(FsAnswerKind.Accept, answer.Kind);
        Assert.Equal(offset, answer.Offset);
    }

    [Fact]
    public void Parse_MixedOffsetsAndSigns()
    {
        var answers = FsResponse.Parse("FS +!12-=");
        Assert.Equal(4, answers.Count);
        Assert.Equal(12, answers[1].Offset);
        Assert.Equal(FsAnswerKind.Defer, answers[3].Kind);
    }

    [Fact]
    public void Parse_ToleratesEmbeddedSpaces()
    {
        var answers = FsResponse.Parse("FS + - =");
        Assert.Equal(3, answers.Count);
    }

    [Theory]
    [InlineData("FS *")]
    [InlineData("FS !")] // offset digits required
    [InlineData("FS A")]
    [InlineData("XX ++")]
    public void Parse_InvalidAnswers_CarryTheExactBpqErrorLine(string line)
    {
        var ex = Assert.Throws<FbbProtocolException>(() => FsResponse.Parse(line));
        Assert.Equal("*** Protocol Error - Invalid Proposal Response'", ex.WireErrorLine);
    }

    [Fact]
    public void Parse_CountMustMatchProposals()
    {
        // "FS line MUST have as many +,-,=,R,E,H signs as lines in the
        // proposal" [FBB-APP9, spec §3.4].
        var ex = Assert.Throws<FbbProtocolException>(() => FsResponse.Parse("FS ++", 3));
        Assert.Equal(FsResponse.InvalidResponseErrorLine, ex.WireErrorLine);
        Assert.Equal(3, FsResponse.Parse("FS ++-", 3).Count);
    }
}
