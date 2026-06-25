using Bbs.Core;
using Bbs.Host.Forwarding;

namespace Bbs.Host.Tests;

/// <summary>
/// Connect-script resolution (compat spec §4.4; v2 structured steps): the OPEN step (the one with
/// <see cref="ConnectStep.Open"/> set, which must be first) names the RHP open — no open step at all →
/// inbound-only (Target null); every later step is an expect/send. The flat <c>EXPECT=SEND</c> string
/// form is retired, so resolution is a straight mapping of <see cref="ConnectStep"/>[] →
/// <see cref="ConnectPlan"/>, including the per-step options (timeout/match/ignoreCase/eol/raw/name/expectAny).
/// </summary>
public class ConnectScriptTests
{
    private static Partner PartnerWith(params ConnectStep[] steps) => new() { Call = "GB7BPQ", ConnectScript = steps };

    [Fact]
    public void EmptyScript_IsInboundOnly_NeverDialled()
    {
        // No open step → the partner dials US and polls for its mail; we never dial it.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith());
        Assert.Null(plan.Target);
        Assert.True(plan.IsInboundOnly);
        Assert.Null(plan.Port);
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void OpenStep_NamesTheTarget()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(new ConnectStep { Open = "GB7BPQ-1" }));
        Assert.Equal("GB7BPQ-1", plan.Target);
        Assert.Null(plan.Port);
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void OpenStep_PinsThePort()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(new ConnectStep { Open = "GB7BPQ-1", Port = "2" }));
        Assert.Equal("GB7BPQ-1", plan.Target);
        Assert.Equal("2", plan.Port);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void ExpectSendStep_MapsToTheRuntimeStep()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7RDG" },
            new ConnectStep { Expect = "GB7RDG}", Send = "BBS" }));
        Assert.Equal("GB7RDG", plan.Target);
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal("GB7RDG}", step.Expect);
        Assert.Equal("BBS", step.Send);
        Assert.Equal("substring", step.Match);   // defaults preserve the historical behaviour
        Assert.True(step.IgnoreCase);
        Assert.Equal("cr", step.Eol);
        Assert.False(step.Raw);
        Assert.Null(step.Timeout);
        Assert.Null(step.Name);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ExpectStep_PreservesTrailingSpaceAndEquals()
    {
        // The whole point of v2: a node prompt of "=> " (equals, greater-than, trailing space) is just
        // a value — the flat form could not represent it (the '=' was the delimiter; the space trimmed).
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7WEM" },
            new ConnectStep { Expect = "=> ", Send = "BBS" }));
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal("=> ", step.Expect);   // three bytes, trailing space intact
    }

    [Fact]
    public void OptionFields_FlowThrough()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7BPQ" },
            new ConnectStep
            {
                Expect = "CONNECTED",
                Send = "BBS",
                TimeoutSeconds = 30,
                Match = "regex",
                IgnoreCase = false,
                Eol = "crlf",
                Raw = true,
                Name = "enter-bbs",
            }));
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal(TimeSpan.FromSeconds(30), step.Timeout);
        Assert.Equal("regex", step.Match);
        Assert.False(step.IgnoreCase);
        Assert.Equal("crlf", step.Eol);
        Assert.True(step.Raw);
        Assert.Equal("enter-bbs", step.Name);
    }

    [Fact]
    public void ExpectAny_FlowsThrough()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7BPQ" },
            new ConnectStep { ExpectAny = ["CONNECTED", "BUSY from"], Send = "BBS" }));
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal(["CONNECTED", "BUSY from"], step.ExpectAny);
    }

    [Fact]
    public void MultiHop_WalksNodeByNode()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7RDG" },
            new ConnectStep { Expect = "GB7RDG}", Send = "C 3 !GB7WEM-7" },
            new ConnectStep { Expect = "=> ", Send = "BBS" }));
        Assert.Equal("GB7RDG", plan.Target);
        Assert.Equal(2, plan.Steps.Count);
        ExpectSendStep first = Assert.IsType<ExpectSendStep>(plan.Steps[0]);
        ExpectSendStep second = Assert.IsType<ExpectSendStep>(plan.Steps[1]);
        Assert.Equal(("GB7RDG}", "C 3 !GB7WEM-7"), (first.Expect, first.Send));
        Assert.Equal(("=> ", "BBS"), (second.Expect, second.Send));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void SendOnlyStep_HasEmptyExpect()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7BPQ" },
            new ConnectStep { Send = "BBS" }));
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal("", step.Expect);
        Assert.Equal("BBS", step.Send);
    }

    [Fact]
    public void OpenAfterAStep_IsWarnedAndIgnored()
    {
        // The dial must be first; a misplaced open is ignored with a warning, not treated as a hop.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Send = "BBS" },
            new ConnectStep { Open = "GB7RDG" }));
        Assert.Null(plan.Target);
        Assert.Single(plan.Steps);
        string warning = Assert.Single(plan.Warnings);
        Assert.Contains("first step", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondOpen_IsWarnedAndIgnored()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7BPQ" },
            new ConnectStep { Open = "GB7RDG" }));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Empty(plan.Steps);
        string warning = Assert.Single(plan.Warnings);
        Assert.Contains("second dial", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenStepThatAlsoCarriesSend_IsWarned()
    {
        // The "a step is either OPEN or expect/send" invariant isn't structurally enforced, so a
        // hand-authored map carrying both must name the dropped command, not silently lose it.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7RDG", Send = "BBS" }));
        Assert.Equal("GB7RDG", plan.Target);
        Assert.Empty(plan.Steps);
        string warning = Assert.Single(plan.Warnings);
        Assert.Contains("also carries", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void NonNumericPort_IsWarnedAndDropped()
    {
        // Port is documented digits-only; a non-numeric port is dropped with a warning rather than
        // sent verbatim over the RHP open.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7BPQ-1", Port = "abc" }));
        Assert.Equal("GB7BPQ-1", plan.Target);
        Assert.Null(plan.Port);
        string warning = Assert.Single(plan.Warnings);
        Assert.Contains("not numeric", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownMatchOrEol_FallsBackToDefaultWithAWarning()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7BPQ" },
            new ConnectStep { Expect = "x", Send = "y", Match = "fuzzy", Eol = "cr-lf" }));
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal("substring", step.Match);
        Assert.Equal("cr", step.Eol);
        Assert.Equal(2, plan.Warnings.Count);
    }

    [Fact]
    public void MatchAndEol_AreNormalisedToLowercase()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith(
            new ConnectStep { Open = "GB7BPQ" },
            new ConnectStep { Expect = "x", Send = "y", Match = "Regex", Eol = "CRLF" }));
        ExpectSendStep step = Assert.IsType<ExpectSendStep>(Assert.Single(plan.Steps));
        Assert.Equal("regex", step.Match);
        Assert.Equal("crlf", step.Eol);
        Assert.Empty(plan.Warnings);
    }
}
