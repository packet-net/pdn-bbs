using Bbs.Core;
using Bbs.Host.Forwarding;

namespace Bbs.Host.Tests;

/// <summary>
/// Connect-script resolution (compat spec §4.4): the first <c>C [port] &lt;target&gt;</c>
/// names the RHP open; every later line is an expect/send post-connect step
/// (<c>EXPECT=SEND</c> split on the first <c>=</c>; a bare line is send-only — the legacy
/// verbatim form); <c>PAUSE n</c> delays; the directives BPQ interprets locally are
/// recognised, warned and kept off the wire.
/// </summary>
public class ConnectScriptTests
{
    private static Partner PartnerWith(params string[] script) => new() { Call = "GB7BPQ", ConnectScript = script };

    /// <summary>The (Expect, Send) pair of a step, for terse equality assertions.</summary>
    private static (string Expect, string Send) Pair(ConnectScriptStep step)
    {
        ExpectSendStep es = Assert.IsType<ExpectSendStep>(step);
        return (es.Expect, es.Send);
    }

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
    public void BareLinesAfterTheConnectStep_BecomeSendOnlySteps()
    {
        // The classic navigation: dial the node, then enter its BBS (spec §4.4). A bare
        // line (no '=') is send-only — Expect empty — preserving the legacy verbatim form.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ", "BBS"));
        Assert.Equal("GB7BPQ", plan.Target);
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal("", step.Expect);
        Assert.Equal("BBS", step.Send);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ExpectSendLine_BecomesAnExpectThenSendStep()
    {
        // EXPECT=SEND (split on the first '='): wait for the node prompt, then send.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7RDG", "GB7RDG}=BBS"));
        Assert.Equal("GB7RDG", plan.Target);
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal("GB7RDG}", step.Expect);
        Assert.Equal("BBS", step.Send);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ExpectSend_SplitsOnTheFirstEqualsAndTrimsBothSides()
    {
        // Only the FIRST '=' splits; a later '=' belongs to the SEND. Whitespace around
        // EXPECT and SEND is trimmed.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ", "  prompt> = SET X=Y  "));
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal("prompt>", step.Expect);
        Assert.Equal("SET X=Y", step.Send);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ExpectSend_AllowsAnEmptySend_PureWait()
    {
        // "EXPECT=" with nothing after the '=' is a wait-only step (Expect set, Send empty).
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ", "GB7BPQ>="));
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal("GB7BPQ>", step.Expect);
        Assert.Equal("", step.Send);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void MultiHopExpectSend_WalksNodeByNode()
    {
        // The explicit multi-hop form from pdn-bpqchat's LAB.md, ported: open the first hop,
        // then expect each node prompt before sending the next connect.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            "C GB7BBB", "GB7BBB>=C GB7CCC", "GB7CCC>=C GB7DDD", "GB7DDD>=C GB7DDD-4"));
        Assert.Equal("GB7BBB", plan.Target);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal(("GB7BBB>", "C GB7CCC"), Pair(plan.Steps[0]));
        Assert.Equal(("GB7CCC>", "C GB7DDD"), Pair(plan.Steps[1]));
        Assert.Equal(("GB7DDD>", "C GB7DDD-4"), Pair(plan.Steps[2]));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void SecondBareCLine_IsASendOnlyNodeCommandNotANewTarget()
    {
        // Spec §4.4's verbatim model: only the FIRST C names the open; a later bare C is a
        // node-level connect command typed at the connected node's prompt (send-only).
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ", "C GB7RDG", "BBS"));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(("", "C GB7RDG"), Pair(plan.Steps[0]));
        Assert.Equal(("", "BBS"), Pair(plan.Steps[1]));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void NoLeadingC_DialsThePartnerAndEveryLineIsAStep()
    {
        // Without a leading C the RHP open dials the partner call itself, and even a C
        // appearing after another step stays a send-only step.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("NODES", "C GB7RDG"));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(("", "NODES"), Pair(plan.Steps[0]));
        Assert.Equal(("", "C GB7RDG"), Pair(plan.Steps[1]));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Pause_BecomesAPauseStepInOrder()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ", "PAUSE 5", "BBS"));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.IsType<PauseStep>(plan.Steps[0]).Delay);
        Assert.Equal(("", "BBS"), Pair(plan.Steps[1]));
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
        // wire) and warned — named deviations, never silent drops. INTERLOCK is NOT here:
        // it is recognised as superseded (a note, not a warning) — see Interlock_IsNoted...
        string[] directives =
        [
            "TIMES 0900 1700", "ELSE", "MSGTYPE B", "SKIPPROMPT",
            "SKIPCON", "TEXTFORWARDING", "SETCALLTOSENDER", "ATTACH 2", "RADIO FREQ",
            "FILE x", "IMPORT x", "RMS", "SendWL2KFW",
        ];
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(["C GB7BPQ", .. directives, "BBS"]));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Equal(("", "BBS"), Pair(Assert.Single(plan.Steps)));
        Assert.Equal(directives.Length, plan.Warnings.Count);
        Assert.All(plan.Warnings, w => Assert.Contains("unsupported", w, StringComparison.Ordinal));
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void Interlock_IsNotedNotWarned_AndKeptOffTheWire()
    {
        // INTERLOCK is recognised as SUPERSEDED, not unsupported: its job (one forwarding session
        // per shared radio) belongs to the node/port layer (kiss.ackMode), so it is a by-design
        // no-op here — recorded as a Note (Debug, surfaced by test-connect), NOT a warning. This is
        // the GB7CIP/GB7LOX/GB7OXF shape: a leading INTERLOCK then the real connect.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("INTERLOCK 3", "C 3 GB7WEM-7", "C uhf gb7cip"));

        Assert.Equal("GB7WEM-7", plan.Target);   // the INTERLOCK line did not become the open
        Assert.Equal("3", plan.Port);
        Assert.Empty(plan.Warnings);             // no warning spam every cycle
        string note = Assert.Single(plan.Notes); // but named, never silently dropped
        Assert.Contains("INTERLOCK 3", note, StringComparison.Ordinal);
        Assert.Contains("serialization", note, StringComparison.Ordinal);
        // The trailing "C uhf gb7cip" is the multi-hop send step, not a second open.
        Assert.Equal(("", "C uhf gb7cip"), Pair(Assert.Single(plan.Steps)));
    }

    [Fact]
    public void Interlock_RecognitionIsCaseInsensitive()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("interlock 3", "C 3 GB7LOX-2", "bbs"));
        Assert.Empty(plan.Warnings);
        Assert.Single(plan.Notes);
        Assert.Equal("GB7LOX-2", plan.Target);
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
        Assert.Equal(("", "BBS"), Pair(Assert.Single(plan.Steps)));
        Assert.Empty(plan.Warnings);
    }

    // NOTE: BPQ node-command syntax (the "NC" verb, the "!" direct flag) is normalised to plain "C"
    // at IMPORT time by the migrator (tools/Bbs.Import.Bpq), NOT tolerated by this parser — pdn's "C"
    // already negotiates. The translation is covered by the migrator's tests; here "C" stays strict.
}
