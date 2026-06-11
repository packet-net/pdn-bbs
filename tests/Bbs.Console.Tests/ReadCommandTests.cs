using Bbs.Core;

namespace Bbs.Console.Tests;

/// <summary>R/RM/RMR (compat spec §1.3): output, the N→Y transition, visibility and rights.</summary>
public sealed class ReadCommandTests : IDisposable
{
    private readonly SessionHarness _h = new();

    public ReadCommandTests()
    {
        _h.KnownUser("M0LTE");
        // 1: P M0LTE→G8BPQ; 2: B; 3: T; 4: P G8BPQ→M0LTE held; 5: P G8BPQ→M0LTE.
        _h.Store.AddMessage(Drafts.Personal(from: "M0LTE", to: "G8BPQ", at: "GB7BPQ.#23.GBR.EURO", subject: "one", body: "first line\rsecond line\r"));
        _h.Store.AddMessage(Drafts.Bulletin(subject: "two"));
        _h.Store.AddMessage(Drafts.Traffic(subject: "three"));
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "four", hold: true));
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "five", body: "for me\r"));
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task R_OutputsHeaderAndBody()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 1", "B");
        Assert.Contains("From: M0LTE\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("To: G8BPQ @ GB7BPQ.#23.GBR.EURO\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("Type/Status: PN\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("Bid: 1_GB7PDN\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("Title: one\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("first line\rsecond line\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_HeaderOmitsAtWhenNone()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 5", "B");
        Assert.Contains("To: M0LTE\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_ByAddressee_MarksRead()
    {
        await _h.RunAsync("M0LTE", "R 5", "B");
        Assert.Equal(MessageStatus.Read, _h.Store.GetMessage(5)!.Status);
    }

    [Fact]
    public async Task R_Traffic_NeverGoesRead()
    {
        // §2.2: "T messages are not set Y on read".
        await _h.RunAsync("M0LTE", "R 3", "B");
        Assert.Equal(MessageStatus.Unread, _h.Store.GetMessage(3)!.Status);
    }

    [Fact]
    public async Task R_Bulletin_ByNonAddressee_DoesNotChangeStatus()
    {
        await _h.RunAsync("M0LTE", "R 2", "B");
        Assert.Equal(MessageStatus.Unread, _h.Store.GetMessage(2)!.Status);
    }

    [Fact]
    public async Task R_MultipleNumbers_ReadsEach()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 1 5", "B");
        Assert.Contains("Title: one\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("Title: five\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_NotFound_VerbatimShape()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 99", "B");
        Assert.Contains("Message 99 not found\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_OthersPersonal_NotForYou()
    {
        // §1.3: "Message %d not for you" — 2E0XYZ is neither sender nor addressee of 1.
        _h.KnownUser("2E0XYZ");
        (_, ScriptedTerminal t) = await _h.RunAsync("2E0XYZ", "R 1", "B");
        Assert.Contains("Message 1 not for you\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("first line", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_Sender_MayReadOwnPersonal()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 1", "B");
        Assert.Contains("Title: one\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_BulletinAndTraffic_ReadableByAnyone()
    {
        // §2.1: T readable by any user; bulletins likewise.
        _h.KnownUser("2E0XYZ");
        (_, ScriptedTerminal t) = await _h.RunAsync("2E0XYZ", "R 2 3", "B");
        Assert.Contains("Title: two\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("Title: three\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_Held_NonSysop_ReadsAsNotFound()
    {
        // §2.2 held-invisible: even the addressee can't see message 4.
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 4", "B");
        Assert.Contains("Message 4 not found\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_Held_Sysop_CanRead()
    {
        _h.KnownUser(SessionHarness.SysopCall);
        (_, ScriptedTerminal t) = await _h.RunAsync(SessionHarness.SysopCall, "R 4", "B");
        Assert.Contains("Title: four\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_Killed_NonSysop_NotFound()
    {
        _h.Store.Kill(5);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 5", "B");
        Assert.Contains("Message 5 not found\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_NoArguments_InvalidFormat()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R", "B");
        Assert.Contains("*** Error: Invalid Format\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_NonNumeric_InvalidFormat()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R x", "B");
        Assert.Contains("*** Error: Invalid Format\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_BadOptionLetter_VerbatimShape()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "RX", "B");
        Assert.Contains("*** Error: Invalid Read option X\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R_Latin1Body_SurvivesByteForByte()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "latin", body: "café üñ\r"));
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 6", "B");
        Assert.Contains("café üñ\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- RM / RMR (§1.3)

    [Fact]
    public async Task RM_ReadsUnreadMessagesToMe_NewestFirst()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "six"));
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "RM", "B");
        int five = t.Output.IndexOf("Title: five", StringComparison.Ordinal);
        int six = t.Output.IndexOf("Title: six", StringComparison.Ordinal);
        Assert.True(six >= 0 && five > six, "expected newest (six) before five");
        Assert.Equal(MessageStatus.Read, _h.Store.GetMessage(5)!.Status);
        Assert.Equal(MessageStatus.Read, _h.Store.GetMessage(6)!.Status);
    }

    [Fact]
    public async Task RMR_ReadsOldestFirst()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "six"));
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "RMR", "B");
        int five = t.Output.IndexOf("Title: five", StringComparison.Ordinal);
        int six = t.Output.IndexOf("Title: six", StringComparison.Ordinal);
        Assert.True(five >= 0 && six > five, "expected oldest (five) before six");
    }

    [Fact]
    public async Task RM_SkipsMessagesAlreadyReadByMe()
    {
        _h.Store.MarkRead(5, "M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "RM", "B");
        Assert.Contains("No New Messages\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RM_SkipsHeldMessagesToMe()
    {
        _h.Store.MarkRead(5, "M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "RM", "B");
        Assert.DoesNotContain("Title: four", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RM_NothingNew_NoNewMessages()
    {
        _h.KnownUser("2E0XYZ");
        (_, ScriptedTerminal t) = await _h.RunAsync("2E0XYZ", "RM", "B");
        Assert.Contains("No New Messages\r", t.Output, StringComparison.Ordinal);
    }
}
