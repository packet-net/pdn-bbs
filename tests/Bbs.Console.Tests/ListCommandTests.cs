using System.Globalization;
using Bbs.Core;

namespace Bbs.Console.Tests;

/// <summary>The L family filter/range/status matrix and the §1.6 column shapes.</summary>
public sealed class ListCommandTests : IDisposable
{
    private readonly SessionHarness _h = new();

    public ListCommandTests()
    {
        _h.KnownUser("M0LTE");
        // 1: P M0LTE→G8BPQ @GB7BPQ.#23.GBR.EURO
        _h.Store.AddMessage(Drafts.Personal(from: "M0LTE", to: "G8BPQ", at: "GB7BPQ.#23.GBR.EURO", subject: "one"));
        // 2: B M0LTE→ALL @GBR.EURO
        _h.Store.AddMessage(Drafts.Bulletin(from: "M0LTE", to: "ALL", at: "GBR.EURO", subject: "two"));
        // 3: T K4CJX→32118
        _h.Store.AddMessage(Drafts.Traffic(from: "K4CJX", to: "32118", subject: "three"));
        // 4: P G8BPQ→M0LTE (held)
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "four", hold: true));
        // 5: P G8BPQ→M0LTE
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "five"));
    }

    public void Dispose() => _h.Dispose();

    /// <summary>Message numbers in listing-line order (listing lines = leading number + the @ column).</summary>
    private static long[] ListedNumbers(ScriptedTerminal t) =>
        [.. t.OutputLines
            .Where(l => l.Length > 0 && char.IsAsciiDigit(l[0]) && l.Contains('@', StringComparison.Ordinal))
            .Select(l => long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture))];

    private async Task<ScriptedTerminal> ListAsync(params string[] lines)
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", lines);
        return t;
    }

    // ---------------------------------------------------------------- bare L / LR (§1.3)

    [Fact]
    public async Task BareL_ListsNewSinceLastList_NewestFirst_HidingHeld()
    {
        ScriptedTerminal t = await ListAsync("L", "B");
        Assert.Equal([5, 3, 2, 1], ListedNumbers(t));
    }

    [Fact]
    public async Task BareL_SecondTimeInSession_NoNewMessages()
    {
        ScriptedTerminal t = await ListAsync("L", "L", "B");
        Assert.Contains("No New Messages\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareL_OnlyNewMessagesAfterPointer()
    {
        await ListAsync("L", "B");
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "six"));
        ScriptedTerminal t = await ListAsync("L", "B");
        Assert.Equal([6], ListedNumbers(t));
    }

    [Fact]
    public async Task LR_IsSinceLastOldestFirst()
    {
        ScriptedTerminal t = await ListAsync("LR", "B");
        Assert.Equal([1, 2, 3, 5], ListedNumbers(t));
    }

    [Fact]
    public async Task FilteredLists_DoNotAdvanceThePointer()
    {
        ScriptedTerminal t = await ListAsync("LM", "L", "B");
        Assert.Equal([5, 5, 3, 2, 1], ListedNumbers(t)); // LM shows 5; bare L still shows everything
    }

    // ---------------------------------------------------------------- type/status/mine filters (§1.3)

    [Fact]
    public async Task LM_ListsMessagesToMe()
    {
        ScriptedTerminal t = await ListAsync("LM", "B");
        Assert.Equal([5], ListedNumbers(t)); // 4 is held → invisible (§2.2)
    }

    [Fact]
    public async Task LB_Bulletins()
    {
        ScriptedTerminal t = await ListAsync("LB", "B");
        Assert.Equal([2], ListedNumbers(t));
    }

    [Fact]
    public async Task LP_Personals()
    {
        ScriptedTerminal t = await ListAsync("LP", "B");
        Assert.Equal([5, 1], ListedNumbers(t));
    }

    [Fact]
    public async Task LT_Traffic()
    {
        ScriptedTerminal t = await ListAsync("LT", "B");
        Assert.Equal([3], ListedNumbers(t));
    }

    [Fact]
    public async Task LN_UnreadStatus()
    {
        _h.Store.MarkRead(1, "G8BPQ");
        ScriptedTerminal t = await ListAsync("LN", "B");
        Assert.Equal([5, 3, 2], ListedNumbers(t));
    }

    [Fact]
    public async Task LY_ReadStatus()
    {
        _h.Store.MarkRead(1, "G8BPQ");
        ScriptedTerminal t = await ListAsync("LY", "B");
        Assert.Equal([1], ListedNumbers(t));
    }

    [Fact]
    public async Task CombinedOptions_LMP()
    {
        ScriptedTerminal t = await ListAsync("LMP", "B");
        Assert.Equal([5], ListedNumbers(t));
    }

    [Fact]
    public async Task CombinedOptions_LBFromCall()
    {
        ScriptedTerminal t = await ListAsync("LB< M0LTE", "B");
        Assert.Equal([2], ListedNumbers(t));
    }

    // ---------------------------------------------------------------- LH/LK sysop gate (§1.3/§2.2)

    [Fact]
    public async Task LH_NonSysop_RefusedVerbatim()
    {
        ScriptedTerminal t = await ListAsync("LH", "B");
        Assert.Contains("LH or LK can only be used by SYSOP\r", t.Output, StringComparison.Ordinal);
        Assert.Empty(ListedNumbers(t));
    }

    [Fact]
    public async Task LK_NonSysop_RefusedVerbatim()
    {
        ScriptedTerminal t = await ListAsync("LK", "B");
        Assert.Contains("LH or LK can only be used by SYSOP\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LH_Sysop_SeesHeldMessages()
    {
        _h.KnownUser(SessionHarness.SysopCall);
        (_, ScriptedTerminal t) = await _h.RunAsync(SessionHarness.SysopCall, "LH", "B");
        Assert.Equal([4], ListedNumbers(t));
    }

    [Fact]
    public async Task LK_Sysop_SeesKilledMessages()
    {
        _h.Store.Kill(5);
        _h.KnownUser(SessionHarness.SysopCall);
        (_, ScriptedTerminal t) = await _h.RunAsync(SessionHarness.SysopCall, "LK", "B");
        Assert.Equal([5], ListedNumbers(t));
    }

    // ---------------------------------------------------------------- LL n and ranges (§1.3)

    [Fact]
    public async Task LL_LastN()
    {
        ScriptedTerminal t = await ListAsync("LL 2", "B");
        Assert.Equal([5, 3], ListedNumbers(t));
    }

    [Fact]
    public async Task LL_CountAttached()
    {
        ScriptedTerminal t = await ListAsync("LL2", "B");
        Assert.Equal([5, 3], ListedNumbers(t));
    }

    [Fact]
    public async Task LL_MissingCount_InvalidFormat()
    {
        ScriptedTerminal t = await ListAsync("LL", "B");
        Assert.Contains("*** Error: Invalid Format\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task L_Range()
    {
        ScriptedTerminal t = await ListAsync("L 1-3", "B");
        Assert.Equal([3, 2, 1], ListedNumbers(t));
    }

    [Fact]
    public async Task L_RangeAttached()
    {
        ScriptedTerminal t = await ListAsync("L1-3", "B");
        Assert.Equal([3, 2, 1], ListedNumbers(t));
    }

    [Fact]
    public async Task L_OpenRange()
    {
        ScriptedTerminal t = await ListAsync("L 3-", "B");
        Assert.Equal([5, 3], ListedNumbers(t));
    }

    [Fact]
    public async Task L_SingleNumber()
    {
        ScriptedTerminal t = await ListAsync("L 2", "B");
        Assert.Equal([2], ListedNumbers(t));
    }

    // ---------------------------------------------------------------- L< / L> / L@ (§1.3)

    [Fact]
    public async Task LFrom_FiltersBySender()
    {
        ScriptedTerminal t = await ListAsync("L< M0LTE", "B");
        Assert.Equal([2, 1], ListedNumbers(t));
    }

    [Fact]
    public async Task LFrom_AttachedValue()
    {
        ScriptedTerminal t = await ListAsync("L<M0LTE", "B");
        Assert.Equal([2, 1], ListedNumbers(t));
    }

    [Fact]
    public async Task LTo_FiltersByAddressee()
    {
        ScriptedTerminal t = await ListAsync("L> G8BPQ", "B");
        Assert.Equal([1], ListedNumbers(t));
    }

    [Fact]
    public async Task LAt_MatchesUpToTheLengthOfTheInput()
    {
        // §1.3: "L@ matches up to the length of the input string".
        ScriptedTerminal gbr = await ListAsync("L@ GBR", "B");
        Assert.Equal([2], ListedNumbers(gbr));

        ScriptedTerminal gb7 = await ListAsync("L@ GB7", "B");
        Assert.Equal([1], ListedNumbers(gb7));
    }

    // ---------------------------------------------------------------- errors + empties

    [Fact]
    public async Task BadOptionLetter_VerbatimShape()
    {
        ScriptedTerminal t = await ListAsync("LX", "B");
        Assert.Contains("*** Error: Invalid List option X\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyFilteredList_NoMessagesFound()
    {
        ScriptedTerminal t = await ListAsync("L> NOBODY", "B");
        Assert.Contains("No Messages found\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- §1.6 column shapes

    [Fact]
    public async Task ListLine_ExactBpqColumnShape()
    {
        // [BPQ-SRC ListMessage]: "%-6d %s %c%c   %5d %-7s@%-6s %-6s %-s" — body "hello\r" = 6 bytes,
        // store opened at 2026-06-11 → "11-Jun", VIA = first dotted element of the AT.
        ScriptedTerminal t = await ListAsync("L 1", "B");
        Assert.Contains("1      11-Jun PN       6 G8BPQ  @GB7BPQ M0LTE  one\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListLine_NoAt_EmptyViaColumn()
    {
        ScriptedTerminal t = await ListAsync("L 3", "B");
        Assert.Contains("3      11-Jun TN       6 32118  @       K4CJX  three\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListLine_StatusLetter_BulletinQueued()
    {
        // §2.2: a bulletin with queued forwarding shows $.
        _h.Store.EnqueueForwards(2, ["GB7BPQ-1"]);
        ScriptedTerminal t = await ListAsync("L 2", "B");
        Assert.Contains(" B$   ", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListLine_StatusLetter_Forwarded()
    {
        _h.Store.EnqueueForwards(1, ["GB7BPQ-1"]);
        _h.Store.MarkForwarded(1, "GB7BPQ-1");
        ScriptedTerminal t = await ListAsync("L 1", "B");
        Assert.Contains(" PF   ", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListLine_StatusLetter_Read()
    {
        _h.Store.MarkRead(5, "M0LTE");
        ScriptedTerminal t = await ListAsync("L 5", "B");
        Assert.Contains(" PY   ", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_HasNoHeaderRow()
    {
        // [VERIFY-ORACLE #9]: no header row emitted (safer default for pattern-matching clients).
        ScriptedTerminal t = await ListAsync("L", "B");
        string[] lines = t.OutputLines;
        int prompt = Array.FindIndex(lines, l => l.StartsWith("de GB7PDN>", StringComparison.Ordinal));
        Assert.StartsWith("5 ", lines[prompt + 1], StringComparison.Ordinal);
    }
}
