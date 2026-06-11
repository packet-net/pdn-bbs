using Bbs.Core;

namespace Bbs.Console.Tests;

/// <summary>K/KM kill rights (compat spec §1.3, §2.2) and D delivered (§1.3).</summary>
public sealed class KillAndDeliveredTests : IDisposable
{
    private readonly SessionHarness _h = new();

    public KillAndDeliveredTests()
    {
        _h.KnownUser("M0LTE");
        // 1: P M0LTE→G8BPQ; 2: B M0LTE→ALL; 3: T K4CJX→32118; 4: P G8BPQ→M0LTE held.
        _h.Store.AddMessage(Drafts.Personal(from: "M0LTE", to: "G8BPQ", subject: "one"));
        _h.Store.AddMessage(Drafts.Bulletin(from: "M0LTE", subject: "two"));
        _h.Store.AddMessage(Drafts.Traffic(subject: "three"));
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "four", hold: true));
    }

    public void Dispose() => _h.Dispose();

    // ---------------------------------------------------------------- K n (§1.3, rights §2.2)

    [Fact]
    public async Task K_BySender_KilledVerbatim()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "K 1", "B");
        Assert.Contains("Message #1 Killed\r", t.Output, StringComparison.Ordinal);
        Assert.Equal(MessageStatus.Killed, _h.Store.GetMessage(1)!.Status);
    }

    [Fact]
    public async Task K_ByAddressee_Allowed()
    {
        _h.KnownUser("G8BPQ");
        (_, ScriptedTerminal t) = await _h.RunAsync("G8BPQ", "K 1", "B");
        Assert.Contains("Message #1 Killed\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K_PersonalByThirdParty_NotYourMessage()
    {
        _h.KnownUser("2E0XYZ");
        (_, ScriptedTerminal t) = await _h.RunAsync("2E0XYZ", "K 1", "B");
        Assert.Contains("Not your message\r", t.Output, StringComparison.Ordinal);
        Assert.Equal(MessageStatus.Unread, _h.Store.GetMessage(1)!.Status);
    }

    [Fact]
    public async Task K_BulletinBySender_Allowed()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "K 2", "B");
        Assert.Contains("Message #2 Killed\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K_BulletinByOther_NotYourMessage()
    {
        // §2.2: B killable by sender only.
        _h.KnownUser("2E0XYZ");
        (_, ScriptedTerminal t) = await _h.RunAsync("2E0XYZ", "K 2", "B");
        Assert.Contains("Not your message\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K_TrafficByAnyone_Allowed()
    {
        // §2.2: T killable by anyone.
        _h.KnownUser("2E0XYZ");
        (_, ScriptedTerminal t) = await _h.RunAsync("2E0XYZ", "K 3", "B");
        Assert.Contains("Message #3 Killed\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K_NotFound_VerbatimShape()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "K 99", "B");
        Assert.Contains("Message 99 not found\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K_Held_NonSysop_ReadsAsNotFound()
    {
        // §2.2: held can't be killed except by sysop, and is invisible.
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "K 4", "B");
        Assert.Contains("Message 4 not found\r", t.Output, StringComparison.Ordinal);
        Assert.Equal(MessageStatus.Held, _h.Store.GetMessage(4)!.Status);
    }

    [Fact]
    public async Task K_Held_Sysop_Kills()
    {
        _h.KnownUser(SessionHarness.SysopCall);
        (_, ScriptedTerminal t) = await _h.RunAsync(SessionHarness.SysopCall, "K 4", "B");
        Assert.Contains("Message #4 Killed\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K_Sysop_MayKillAnything()
    {
        _h.KnownUser(SessionHarness.SysopCall);
        (_, ScriptedTerminal t) = await _h.RunAsync(SessionHarness.SysopCall, "K 1 2 3", "B");
        Assert.Contains("Message #1 Killed\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("Message #2 Killed\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("Message #3 Killed\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K_AlreadyKilled_NotFound()
    {
        _h.Store.Kill(1);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "K 1", "B");
        Assert.Contains("Message 1 not found\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K_NoArguments_InvalidFormat()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "K", "B");
        Assert.Contains("*** Error: Invalid Format\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task K_BadOptionLetter_VerbatimShape()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "KX", "B");
        Assert.Contains("*** Error: Invalid Kill option X\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- KM (§1.3, [VERIFY-ORACLE #7])

    [Fact]
    public async Task KM_KillsMyReadPersonals_LeavesUnread()
    {
        // The source kills status Y (the doc's "haven't yet read" is the known typo).
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "read one"));   // 5
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "unread one")); // 6
        _h.Store.MarkRead(5, "M0LTE");

        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "KM", "B");
        Assert.Contains("Message #5 Killed\r", t.Output, StringComparison.Ordinal);
        Assert.Equal(MessageStatus.Killed, _h.Store.GetMessage(5)!.Status);
        Assert.Equal(MessageStatus.Unread, _h.Store.GetMessage(6)!.Status);
    }

    [Fact]
    public async Task KM_DoesNotTouchMessagesToOthers()
    {
        _h.Store.MarkRead(1, "G8BPQ"); // 1 is M0LTE→G8BPQ, now Y
        await _h.RunAsync("M0LTE", "KM", "B");
        Assert.Equal(MessageStatus.Read, _h.Store.GetMessage(1)!.Status);
    }

    [Fact]
    public async Task KM_NothingToKill_NoMessagesFound()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "KM", "B");
        Assert.Contains("No Messages found\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- D n (§1.3)

    [Fact]
    public async Task D_Traffic_FlaggedDeliveredVerbatim()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "D 3", "B");
        Assert.Contains("Message #3 Flagged as Delivered\r", t.Output, StringComparison.Ordinal);
        Assert.Equal(MessageStatus.Delivered, _h.Store.GetMessage(3)!.Status);
    }

    [Fact]
    public async Task D_NonTraffic_VerbatimRefusal()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "D 1", "B");
        Assert.Contains("Message 1 not an NTS Message\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D_NotFound()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "D 99", "B");
        Assert.Contains("Message 99 not found\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delivered_Word_IsTheSameCommand()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "DELIVERED 3", "B");
        Assert.Contains("Message #3 Flagged as Delivered\r", t.Output, StringComparison.Ordinal);
    }
}
