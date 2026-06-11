using Bbs.Core;
using Bbs.Host.Forwarding;

namespace Bbs.Host.Tests;

/// <summary>
/// Connect-script resolution (compat spec §4.4): the first <c>C [port] &lt;target&gt;</c>
/// names the RHP open; every later line is a post-connect step sent verbatim;
/// <c>PAUSE n</c> delays; the directives BPQ interprets locally are recognised, warned
/// and kept off the wire.
/// </summary>
public class ConnectScriptTests
{
    private static Partner PartnerWith(params string[] script) => new() { Call = "GB7BPQ", ConnectScript = script };

    [Fact]
    public void EmptyScript_DialsThePartnerCallWithNoSteps()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith());
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Null(plan.Port);
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void SingleCLine_NamesTheOpenTarget()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ-1"));
        Assert.Equal("GB7BPQ-1", plan.Target);
        Assert.Null(plan.Port);
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void CWithDigitsToken_PinsThePortOnTheOpen()
    {
        // BPQ's C <port> <call> shape (spec §4.4): the port rides the RHP open.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C 2 GB7BPQ-1"));
        Assert.Equal("GB7BPQ-1", plan.Target);
        Assert.Equal("2", plan.Port);
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void LinesAfterTheConnectStep_BecomeVerbatimSendSteps()
    {
        // The classic navigation: dial the node, then enter its BBS (spec §4.4).
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ", "BBS"));
        Assert.Equal("GB7BPQ", plan.Target);
        SendLineStep step = Assert.IsType<SendLineStep>(Assert.Single(plan.Steps));
        Assert.Equal("BBS", step.Line);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void SecondCLine_IsAVerbatimNodeCommandNotANewTarget()
    {
        // Spec §4.4's verbatim model: only the FIRST C names the open; a later C is a
        // node-level connect command typed at the connected node's prompt.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ", "C GB7RDG", "BBS"));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("C GB7RDG", Assert.IsType<SendLineStep>(plan.Steps[0]).Line);
        Assert.Equal("BBS", Assert.IsType<SendLineStep>(plan.Steps[1]).Line);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void NoLeadingC_DialsThePartnerAndEveryLineIsAStep()
    {
        // Without a leading C the RHP open dials the partner call itself, and even a C
        // appearing after another step stays a verbatim step.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("NODES", "C GB7RDG"));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("NODES", Assert.IsType<SendLineStep>(plan.Steps[0]).Line);
        Assert.Equal("C GB7RDG", Assert.IsType<SendLineStep>(plan.Steps[1]).Line);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Pause_BecomesAPauseStepInOrder()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ", "PAUSE 5", "BBS"));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.IsType<PauseStep>(plan.Steps[0]).Delay);
        Assert.Equal("BBS", Assert.IsType<SendLineStep>(plan.Steps[1]).Line);
        Assert.Empty(plan.Warnings);
    }

    [Theory]
    [InlineData("PAUSE")]
    [InlineData("PAUSE five")]
    [InlineData("PAUSE 0")]
    [InlineData("PAUSE -3")]
    [InlineData("PAUSE 5 9")]
    public void MalformedPause_IsWarnedAndKeptOffTheWire(string line)
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ", line));
        Assert.Empty(plan.Steps);
        string warning = Assert.Single(plan.Warnings);
        Assert.Contains("PAUSE", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedDirectives_AreWarnedNotSentToTheNode()
    {
        // The spec §4.4 directives BPQ interprets locally: recognised (kept off the
        // wire) and warned — named deviations, never silent drops.
        string[] directives =
        [
            "TIMES 0900 1700", "ELSE", "MSGTYPE B", "INTERLOCK 1", "SKIPPROMPT",
            "SKIPCON", "TEXTFORWARDING", "SETCALLTOSENDER", "ATTACH 2", "RADIO FREQ",
            "FILE x", "IMPORT x", "RMS", "SendWL2KFW",
        ];
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(["C GB7BPQ", .. directives, "BBS"]));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Equal("BBS", Assert.IsType<SendLineStep>(Assert.Single(plan.Steps)).Line);
        Assert.Equal(directives.Length, plan.Warnings.Count);
        Assert.All(plan.Warnings, w => Assert.Contains("unsupported", w, StringComparison.Ordinal));
    }

    [Fact]
    public void DirectiveRecognition_IsCaseInsensitive()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ", "times 0900 1700"));
        Assert.Empty(plan.Steps);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void TokensAfterTheConnectTarget_AreWarned()
    {
        // Digipeater paths are not supported over an RHP open (spec §4.4 deviation).
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ-1 MB7NXX"));
        Assert.Equal("GB7BPQ-1", plan.Target);
        Assert.Empty(plan.Steps);
        string warning = Assert.Single(plan.Warnings);
        Assert.Contains("digipeater", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void LowercaseC_IsAccepted()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("c gb7bpq-1"));
        Assert.Equal("gb7bpq-1", plan.Target);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void BlankLines_AreIgnored()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("", "  ", "C GB7BPQ", "", "BBS"));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Equal("BBS", Assert.IsType<SendLineStep>(Assert.Single(plan.Steps)).Line);
        Assert.Empty(plan.Warnings);
    }
}
