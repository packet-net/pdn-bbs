using Bbs.Core;

namespace Bbs.Console.Tests;

/// <summary>The S family: §1.5 grammar, prompts, terminators, BID dedup and acceptance shapes.</summary>
public sealed class SendCommandTests : IDisposable
{
    private readonly SessionHarness _h = new();

    public SendCommandTests() => _h.KnownUser("M0LTE");

    public void Dispose() => _h.Dispose();

    // ---------------------------------------------------------------- happy paths

    [Fact]
    public async Task S_FullFlow_PromptsAndAcceptanceShape()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ", "hello there", "line one", "/ex", "B");
        // §1.5 steps 4-5, verbatim prompts.
        Assert.Contains("Enter Title (only):\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("Enter Message Text (end with /ex or ctrl/z)\r", t.Output, StringComparison.Ordinal);
        // §1.5 step 7, byte-exact acceptance: two spaces after "Bid:".
        Assert.Contains("Message: 1 Bid:  1_GB7PDN Size: 9\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S_DefaultsToPersonal()
    {
        await _h.RunAsync("M0LTE", "S G8BPQ", "t", "x", "/ex", "B");
        Assert.Equal(MessageType.Personal, _h.Store.GetMessage(1)!.Type);
    }

    [Fact]
    public async Task SP_StoresFromAndRecipient()
    {
        await _h.RunAsync("M0LTE", "SP G8BPQ", "t", "x", "/ex", "B");
        Message m = _h.Store.GetMessage(1)!;
        Assert.Equal("M0LTE", m.From);
        Assert.Equal("G8BPQ", Assert.Single(m.Recipients).ToCall);
    }

    [Fact]
    public async Task SB_StoresBulletin()
    {
        await _h.RunAsync("M0LTE", "SB ALL @ GBR.EURO", "t", "x", "/ex", "B");
        Message m = _h.Store.GetMessage(1)!;
        Assert.Equal(MessageType.Bulletin, m.Type);
        Assert.Equal("GBR.EURO", m.At);
    }

    [Fact]
    public async Task ST_StoresTraffic()
    {
        await _h.RunAsync("M0LTE", "ST 32118 @ NTSFL", "t", "x", "/ex", "B");
        Assert.Equal(MessageType.Traffic, _h.Store.GetMessage(1)!.Type);
    }

    [Fact]
    public async Task S_ToWithSsid_SsidStripped()
    {
        // §1.5: "TO/FROM truncated to 6 chars, SSID stripped".
        await _h.RunAsync("M0LTE", "S G8BPQ-4", "t", "x", "/ex", "B");
        Assert.Equal("G8BPQ", Assert.Single(_h.Store.GetMessage(1)!.Recipients).ToCall);
    }

    [Fact]
    public async Task S_CallAtBbsWithoutSpaces_Accepted()
    {
        // §1.5: "call@bbs without spaces accepted".
        await _h.RunAsync("M0LTE", "S G8BPQ@GB7BPQ.#23.GBR.EURO", "t", "x", "/ex", "B");
        Message m = _h.Store.GetMessage(1)!;
        Assert.Equal("G8BPQ", Assert.Single(m.Recipients).ToCall);
        Assert.Equal("GB7BPQ.#23.GBR.EURO", m.At);
    }

    [Fact]
    public async Task S_MultipleRecipients_SemicolonSeparated()
    {
        // §1.5: "multiple recipients separated by ;".
        await _h.RunAsync("M0LTE", "S G8BPQ;2E0XYZ", "t", "x", "/ex", "B");
        Assert.Equal(2, _h.Store.GetMessage(1)!.Recipients.Count);
    }

    [Fact]
    public async Task S_SubjectTruncatedAtSixty()
    {
        await _h.RunAsync("M0LTE", "S G8BPQ", new string('s', 70), "x", "/ex", "B");
        Assert.Equal(new string('s', 60), _h.Store.GetMessage(1)!.Subject);
    }

    [Fact]
    public async Task S_BodyStoredCrJoined_Latin1Safe()
    {
        await _h.RunAsync("M0LTE", "S G8BPQ", "t", "café line", "second", "/ex", "B");
        Assert.Equal("café line\rsecond\r", _h.Store.GetMessage(1)!.GetBodyText());
    }

    [Fact]
    public async Task S_SizeIsStoredBodyByteCount()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ", "t", "12345", "/ex", "B");
        Assert.Contains("Size: 6\r", t.Output, StringComparison.Ordinal); // "12345" + CR
    }

    // ---------------------------------------------------------------- terminators (§1.5 step 6)

    [Fact]
    public async Task Body_CtrlZ_Terminates()
    {
        await _h.RunAsync("M0LTE", "S G8BPQ", "t", "x", "\x1A", "B");
        Assert.Equal("x\r", _h.Store.GetMessage(1)!.GetBodyText());
    }

    [Fact]
    public async Task Body_CtrlZWithTrailingText_Terminates()
    {
        // "a line beginning Ctrl-Z (0x1A)".
        await _h.RunAsync("M0LTE", "S G8BPQ", "t", "x", "\x1A junk", "B");
        Assert.NotNull(_h.Store.GetMessage(1));
    }

    [Fact]
    public async Task Body_SlashExIsCaseInsensitive()
    {
        await _h.RunAsync("M0LTE", "S G8BPQ", "t", "x", "/EX", "B");
        Assert.Equal("x\r", _h.Store.GetMessage(1)!.GetBodyText());
    }

    [Fact]
    public async Task Body_AeaArtifact_Terminates()
    {
        // §1.5: the AEA-TNC artifact "/E<0x1A>>".
        await _h.RunAsync("M0LTE", "S G8BPQ", "t", "x", "/E\x1A>", "B");
        Assert.Equal("x\r", _h.Store.GetMessage(1)!.GetBodyText());
    }

    [Fact]
    public async Task Body_SlashExWithTrailingArgs_IsNotATerminator()
    {
        // Only the line "/ex" terminates; "/export" is body text.
        await _h.RunAsync("M0LTE", "S G8BPQ", "t", "/export", "/ex", "B");
        Assert.Equal("/export\r", _h.Store.GetMessage(1)!.GetBodyText());
    }

    // ---------------------------------------------------------------- cancel + errors (§1.5)

    [Fact]
    public async Task EmptyTitle_CancelsVerbatim_NothingStored()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ", "", "B");
        Assert.Contains("*** Message Cancelled\r", t.Output, StringComparison.Ordinal);
        Assert.Null(_h.Store.GetMessage(1));
    }

    [Fact]
    public async Task ToMissing_VerbatimShape()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S", "B");
        Assert.Contains("*** Error: The 'TO' callsign is missing\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToMissing_WhenLineStartsWithAt()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S @ GB7BPQ", "B");
        Assert.Contains("*** Error: The 'TO' callsign is missing\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadToken_InvalidFormat()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ JUNK", "B");
        Assert.Contains("*** Error: Invalid Format\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadTypeLetter_InvalidSendOption()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "SX G8BPQ", "B");
        Assert.Contains("*** Error: Invalid Send option X\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- $bid + dedup (§1.5 step 3, §2.3)

    [Fact]
    public async Task DollarBid_StoredAndEchoedInAcceptance()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ $4567_N0ARY", "t", "x", "/ex", "B");
        Assert.Contains("Bid:  4567_N0ARY ", t.Output, StringComparison.Ordinal);
        Assert.Equal("4567_N0ARY", _h.Store.GetMessage(1)!.Bid);
    }

    [Fact]
    public async Task DuplicateBid_User_VerbatimRefusal()
    {
        // A live personal with the same BID and TO → interactive "*** Error- Duplicate BID".
        _h.Store.AddMessage(Drafts.Personal(to: "G8BPQ", bid: "4567_N0ARY"));
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ $4567_N0ARY", "B");
        Assert.Contains("*** Error- Duplicate BID\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter Title", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateBid_IsCaseInsensitive()
    {
        _h.Store.AddMessage(Drafts.Personal(to: "G8BPQ", bid: "abc_x"));
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ $ABC_X", "B");
        Assert.Contains("*** Error- Duplicate BID\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateBid_BulletinKnownBid_RefusedEvenWhenKilled()
    {
        // §2.3: bulletins — any known BID rejects (dedup outlives the message).
        Message bull = _h.Store.AddMessage(Drafts.Bulletin(bid: "B1_X"));
        _h.Store.Kill(bull.Number);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "SB ALL $B1_X", "B");
        Assert.Contains("*** Error- Duplicate BID\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateBid_ForwardedPersonal_AcceptedAgain()
    {
        // §2.3: personals reject only on a live copy; forwarded copies are accepted again.
        Message m = _h.Store.AddMessage(Drafts.Personal(to: "G8BPQ", bid: "P9_X"));
        _h.Store.EnqueueForwards(m.Number, ["GB7BPQ-1"]);
        _h.Store.MarkForwarded(m.Number, "GB7BPQ-1");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ $P9_X", "t", "x", "/ex", "B");
        Assert.Contains("Message: 2 Bid:  P9_X ", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateBid_BbsPeer_NoBidShape()
    {
        // §1.5 step 3 / §3.10: "BBS peers get NO - BID".
        _h.Partner();
        _h.Store.AddMessage(Drafts.Personal(to: "G8BPQ", bid: "4567_N0ARY"));
        (_, ScriptedTerminal t) = await _h.RunAsync(SessionHarness.PartnerCall, "SP G8BPQ $4567_N0ARY", "B");
        Assert.Contains("NO - BID\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- < from (§1.5)

    [Fact]
    public async Task FromOverride_NonBbsUser_VerbatimRefusal()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ < N6ZFJ", "B");
        Assert.Contains("*** < can only be used by a BBS\r", t.Output, StringComparison.Ordinal);
        Assert.Null(_h.Store.GetMessage(1));
    }

    [Fact]
    public async Task FromOverride_BbsPeer_SetsFromAndReceivedFrom()
    {
        _h.Partner();
        await _h.RunAsync(SessionHarness.PartnerCall, "SP G8BPQ < N6ZFJ $1029_N0XYZ", "subject line", "body", "/ex", "B");
        Message m = _h.Store.GetMessage(1)!;
        Assert.Equal("N6ZFJ", m.From);
        Assert.Equal("GB7BPQ-1", m.ReceivedFrom);
        Assert.Equal("1029_N0XYZ", m.Bid);
    }

    [Fact]
    public async Task BbsPeer_SendFlow_IsOkPlusPromptWithNoUserPrompts()
    {
        // §3.10: proposal → "OK" → prompt → title line → body → /ex; no Enter Title /
        // Enter Message Text prompts and no "Message: n" acceptance line.
        _h.Partner();
        (_, ScriptedTerminal t) = await _h.RunAsync(SessionHarness.PartnerCall, "SP G8BPQ", "subject line", "body text", "/ex", "B");
        Assert.Contains("OK\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter Title", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter Message Text", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Message: 1", t.Output, StringComparison.Ordinal);
        Message m = _h.Store.GetMessage(1)!;
        Assert.Equal("subject line", m.Subject);
        Assert.Equal("body text\r", m.GetBodyText());
    }

    // ---------------------------------------------------------------- auto-@ (§1.5 step 2)

    [Fact]
    public async Task MissingAt_CompletedFromRecipientsHomeBbs()
    {
        _h.KnownUser("G8BPQ", name: "John", homeBbs: "GB7BPQ.#23.GBR.EURO");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ", "t", "x", "/ex", "B");
        Assert.Contains("Address @GB7BPQ.#23.GBR.EURO added from HomeBBS\r", t.Output, StringComparison.Ordinal);
        Assert.Equal("GB7BPQ.#23.GBR.EURO", _h.Store.GetMessage(1)!.At);
    }

    [Fact]
    public async Task ExplicitAt_NotOverridden()
    {
        _h.KnownUser("G8BPQ", name: "John", homeBbs: "GB7BPQ.#23.GBR.EURO");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "S G8BPQ @ OTHER.GBR.EURO", "t", "x", "/ex", "B");
        Assert.DoesNotContain("added from HomeBBS", t.Output, StringComparison.Ordinal);
        Assert.Equal("OTHER.GBR.EURO", _h.Store.GetMessage(1)!.At);
    }

    [Fact]
    public async Task Bulletins_GetNoHomeBbsCompletion()
    {
        _h.KnownUser("ALL", name: "x", homeBbs: "SOMEWHERE.GBR.EURO");
        await _h.RunAsync("M0LTE", "SB ALL", "t", "x", "/ex", "B");
        Assert.Null(_h.Store.GetMessage(1)!.At);
    }

    // ---------------------------------------------------------------- SR / SC (§1.3)

    [Fact]
    public async Task SR_Reply_AutoTitleNoTitlePrompt_ToOriginalSender()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "question"));
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "SR 1", "answer text", "/ex", "B");
        Assert.DoesNotContain("Enter Title", t.Output, StringComparison.Ordinal);
        Assert.Contains("Enter Message Text (end with /ex or ctrl/z)\r", t.Output, StringComparison.Ordinal);
        Message reply = _h.Store.GetMessage(2)!;
        Assert.Equal("Re:question", reply.Subject);
        Assert.Equal("G8BPQ", Assert.Single(reply.Recipients).ToCall);
        Assert.Equal("M0LTE", reply.From);
        Assert.Contains("Message: 2 Bid:  2_GB7PDN ", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SR_AtCompletedFromSendersHomeBbs()
    {
        _h.KnownUser("G8BPQ", name: "John", homeBbs: "GB7BPQ.#23.GBR.EURO");
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "question"));
        await _h.RunAsync("M0LTE", "SR 1", "answer", "/ex", "B");
        Assert.Equal("GB7BPQ.#23.GBR.EURO", _h.Store.GetMessage(2)!.At);
    }

    [Fact]
    public async Task SR_NotFound()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "SR 9", "B");
        Assert.Contains("Message 9 not found\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SR_OthersPersonal_NotForYou()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "2E0XYZ", subject: "private"));
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "SR 1", "B");
        Assert.Contains("Message 1 not for you\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SC_Copy_AutoTitleAndBodyCopiedVerbatim()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "original", body: "the original body\r"));
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "SC 1 2E0XYZ", "B");
        Message copy = _h.Store.GetMessage(2)!;
        Assert.Equal("Fwd:original", copy.Subject);
        Assert.Equal("the original body\r", copy.GetBodyText());
        Assert.Equal("2E0XYZ", Assert.Single(copy.Recipients).ToCall);
        Assert.Equal("M0LTE", copy.From);
        Assert.Contains("Message: 2 Bid:  2_GB7PDN ", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SC_WithAt_Stored()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "original"));
        await _h.RunAsync("M0LTE", "SC 1 2E0XYZ @ GB7XYZ.GBR.EURO", "B");
        Assert.Equal("GB7XYZ.GBR.EURO", _h.Store.GetMessage(2)!.At);
    }

    [Fact]
    public async Task SC_NotFound()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "SC 9 G8BPQ", "B");
        Assert.Contains("Message 9 not found\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SC_MissingRecipient_ToMissing()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "original"));
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "SC 1", "B");
        Assert.Contains("*** Error: The 'TO' callsign is missing\r", t.Output, StringComparison.Ordinal);
    }
}
