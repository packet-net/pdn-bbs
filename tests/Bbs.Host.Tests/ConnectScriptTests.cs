using Bbs.Core;
using Bbs.Host.Forwarding;

namespace Bbs.Host.Tests;

public class ConnectScriptTests
{
    private static Partner PartnerWith(params string[] script) => new() { Call = "GB7BPQ", ConnectScript = script };

    [Fact]
    public void EmptyScript_DialsThePartnerCall()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith());
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void SingleCLine_NamesTheOpenTarget()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C GB7BPQ-1"));
        Assert.Equal("GB7BPQ-1", plan.Target);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void LastCLine_WinsAndPriorHopsWarn()
    {
        // Spec §4.4 scripts can hop through nodes; v1 dials only the final target.
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("C MB7NXX", "C GB7BPQ"));
        Assert.Equal("GB7BPQ", plan.Target);
        string warning = Assert.Single(plan.Warnings);
        Assert.Contains("superseded", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void NonCLines_AreWarnedNotSilentlyDropped()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("PAUSE 5", "C GB7BPQ-1", "TIMES 0900 1700"));
        Assert.Equal("GB7BPQ-1", plan.Target);
        Assert.Equal(2, plan.Warnings.Count);
        Assert.All(plan.Warnings, w => Assert.Contains("unsupported", w, StringComparison.Ordinal));
    }

    [Fact]
    public void LowercaseC_IsAccepted()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("c gb7bpq-1"));
        Assert.Equal("gb7bpq-1", plan.Target);
    }

    [Fact]
    public void BlankLines_AreIgnored()
    {
        ConnectPlan plan = ConnectScript.Resolve(PartnerWith("", "  ", "C GB7BPQ"));
        Assert.Equal("GB7BPQ", plan.Target);
        Assert.Empty(plan.Warnings);
    }
}
