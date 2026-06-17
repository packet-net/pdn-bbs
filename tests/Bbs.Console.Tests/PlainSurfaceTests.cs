using Bbs.Core;

namespace Bbs.Console.Tests;

/// <summary>
/// The plain-language surface (the plain-language mandate, design.md): plain is the default;
/// canonical words + any unambiguous prefix drive the same store operations as classic; sentence
/// listings + plain paging + plain help; the classic/plain mode toggle and its persistence.
/// </summary>
public sealed class PlainSurfaceTests : IDisposable
{
    private readonly SessionHarness _h = new(InterfaceMode.Plain);

    public PlainSurfaceTests() => _h.KnownUser("M0LTE");

    public void Dispose() => _h.Dispose();

    private Task<(BbsSessionEndReason End, ScriptedTerminal Terminal)> RunAsync(params string[] lines) =>
        _h.RunAsync("M0LTE", lines);

    // ---------------------------------------------------------------- default surface (§ the mandate)

    [Fact]
    public async Task NeverSetUser_GetsThePlainSurfaceByDefault()
    {
        // No InterfaceMode saved, config default Plain → plain greeting, not the W0RLI banner.
        (_, ScriptedTerminal t) = await RunAsync("quit");
        Assert.Contains("welcome to the GB7PDN mailbox", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("de GB7PDN>", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainPrompt_IsFriendlyAndNeverEndsInPromptCharacter()
    {
        (_, ScriptedTerminal t) = await RunAsync("quit");
        Assert.Contains("GB7PDN ready, what next?", t.Output, StringComparison.Ordinal);
        // The prompt-faking guard (§1.2 spirit): no '>' at the prompt.
        Assert.DoesNotContain(">", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassicSetUser_GetsTheByteExactClassicSurface()
    {
        _h.Settings.Save("M0LTE", new UserSettings { InterfaceMode = InterfaceMode.Classic });
        (_, ScriptedTerminal t) = await RunAsync("B");
        Assert.Contains("de GB7PDN>\r\n", t.Output, StringComparison.Ordinal);
        Assert.Contains("Hello Tom. Latest Message is 0, Last listed is 0\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- canonical words → operations

    [Fact]
    public async Task Quit_SignsOffAndReturnsBye()
    {
        (BbsSessionEndReason end, ScriptedTerminal t) = await RunAsync("quit");
        Assert.Equal(BbsSessionEndReason.Bye, end);
        Assert.Contains("73 - thanks for calling GB7PDN", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Node_SignsOffAndReturnsNode()
    {
        (BbsSessionEndReason end, _) = await RunAsync("node");
        Assert.Equal(BbsSessionEndReason.Node, end);
    }

    [Fact]
    public async Task List_RendersNewMailAsSentences()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "Antenna party this weekend"));
        (_, ScriptedTerminal t) = await RunAsync("list", "quit");
        Assert.Contains("You have 1 new message:", t.Output, StringComparison.Ordinal);
        Assert.Contains("1) from G4ABC, 11 Jun: Antenna party this weekend\r", t.Output, StringComparison.Ordinal);
        // No status letters / column dump.
        Assert.DoesNotContain("PN", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_NoNewMail_PlainSentence()
    {
        (_, ScriptedTerminal t) = await RunAsync("list", "quit");
        Assert.Contains("No new mail right now.\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_UnreadMessagesToMe_AreMarked()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "hi"));
        (_, ScriptedTerminal t) = await RunAsync("list", "quit");
        Assert.Contains("* 1) from G4ABC", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_ShowsHeaderSentenceAndBody_AndMarksRead()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "hello", body: "first line\rsecond line\r"));
        (_, ScriptedTerminal t) = await RunAsync("read 1", "quit");
        Assert.Contains("Message 1, from G4ABC to M0LTE,", t.Output, StringComparison.Ordinal);
        Assert.Contains("Subject: hello\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("first line\rsecond line\r", t.Output, StringComparison.Ordinal);
        Assert.Equal(MessageStatus.Read, _h.Store.GetMessage(1)!.Status);
    }

    [Fact]
    public async Task Read_OthersPersonal_FriendlyRefusal()
    {
        _h.KnownUser("2E0XYZ");
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "G8BPQ", subject: "private"));
        (_, ScriptedTerminal t) = await _h.RunAsync("2E0XYZ", "read 1", "quit");
        Assert.Contains("private to someone else", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Subject: private", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_NoNumber_ReadsUnreadMail()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "for me", body: "body\r"));
        (_, ScriptedTerminal t) = await RunAsync("read", "quit");
        Assert.Contains("Subject: for me\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_FullFlow_StoresAndConfirmsInPlain()
    {
        (_, ScriptedTerminal t) = await RunAsync("send G4ABC", "Hello there", "this is the body", "/ex", "quit");
        Assert.Contains("New message to G4ABC. What's it about?", t.Output, StringComparison.Ordinal);
        Assert.Contains("Sent - message 1 is on its way to G4ABC.", t.Output, StringComparison.Ordinal);
        Message m = _h.Store.GetMessage(1)!;
        Assert.Equal("M0LTE", m.From);
        Assert.Equal("G4ABC", Assert.Single(m.Recipients).ToCall);
        Assert.Equal("Hello there", m.Subject);
        Assert.Equal("this is the body\r", m.GetBodyText());
    }

    [Fact]
    public async Task Send_NoCallsign_AsksFriendly()
    {
        (_, ScriptedTerminal t) = await RunAsync("send", "quit");
        Assert.Contains("Who's it for?", t.Output, StringComparison.Ordinal);
        Assert.Null(_h.Store.GetMessage(1));
    }

    [Fact]
    public async Task Send_EmptySubject_ThrownAway()
    {
        (_, ScriptedTerminal t) = await RunAsync("send G4ABC", "", "quit");
        Assert.Contains("thrown that one away", t.Output, StringComparison.Ordinal);
        Assert.Null(_h.Store.GetMessage(1));
    }

    [Fact]
    public async Task Send_PowerForm_CallAtBbs_SetsAt()
    {
        (_, ScriptedTerminal t) = await RunAsync("send G4ABC@GB7BSK", "subject", "body", "/ex", "quit");
        Assert.Equal("GB7BSK", _h.Store.GetMessage(1)!.At);
    }

    [Fact]
    public async Task Send_AutoCompletesAtFromRecipientHome()
    {
        _h.KnownUser("G4ABC", name: "Bob", homeBbs: "GB7BSK.GBR.EURO");
        (_, ScriptedTerminal t) = await RunAsync("send G4ABC", "subject", "body", "/ex", "quit");
        Assert.Contains("route that via their home mailbox, GB7BSK.GBR.EURO", t.Output, StringComparison.Ordinal);
        Assert.Equal("GB7BSK.GBR.EURO", _h.Store.GetMessage(1)!.At);
    }

    [Fact]
    public async Task Reply_ToSenderWithAutoSubject()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "question"));
        (_, ScriptedTerminal t) = await RunAsync("reply 1", "my answer", "/ex", "quit");
        Message reply = _h.Store.GetMessage(2)!;
        Assert.Equal("Re:question", reply.Subject);
        Assert.Equal("G4ABC", Assert.Single(reply.Recipients).ToCall);
        Assert.Equal("M0LTE", reply.From);
        Assert.Contains("Sent - message 2 is on its way to G4ABC.", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forward_CopiesToAnotherStation()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "interesting", body: "the body\r"));
        (_, ScriptedTerminal t) = await RunAsync("forward 1 to 2E0XYZ", "quit");
        Message copy = _h.Store.GetMessage(2)!;
        Assert.Equal("Fwd:interesting", copy.Subject);
        Assert.Equal("the body\r", copy.GetBodyText());
        Assert.Equal("2E0XYZ", Assert.Single(copy.Recipients).ToCall);
        Assert.Contains("on its way to 2E0XYZ", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forward_AcceptsBareRecipientWithoutTo()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "x"));
        await RunAsync("forward 1 2E0XYZ", "quit");
        Assert.Equal("2E0XYZ", Assert.Single(_h.Store.GetMessage(2)!.Recipients).ToCall);
    }

    [Fact]
    public async Task Delete_RemovesOwnMessage()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "M0LTE", to: "G4ABC", subject: "mine"));
        (_, ScriptedTerminal t) = await RunAsync("delete 1", "quit");
        Assert.Contains("Deleted message 1.\r", t.Output, StringComparison.Ordinal);
        Assert.Equal(MessageStatus.Killed, _h.Store.GetMessage(1)!.Status);
    }

    [Fact]
    public async Task Delete_OthersMessage_Refused()
    {
        _h.KnownUser("2E0XYZ");
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "G8BPQ", subject: "theirs"));
        (_, ScriptedTerminal t) = await _h.RunAsync("2E0XYZ", "delete 1", "quit");
        Assert.Contains("isn't yours to delete", t.Output, StringComparison.Ordinal);
        Assert.Equal(MessageStatus.Unread, _h.Store.GetMessage(1)!.Status);
    }

    [Fact]
    public async Task Bulletins_ListsBulletinsAsSentences()
    {
        _h.Store.AddMessage(Drafts.Bulletin(from: "G4ABC", to: "SALE", subject: "FT-817 for sale"));
        (_, ScriptedTerminal t) = await RunAsync("bulletins", "quit");
        Assert.Contains("There is 1 bulletin:", t.Output, StringComparison.Ordinal);
        Assert.Contains("from G4ABC", t.Output, StringComparison.Ordinal);
        Assert.Contains("FT-817 for sale", t.Output, StringComparison.Ordinal);
    }

    // ------------------------------------------------ #49: bare `b` is BYE, not bulletins
    // Every classic BBS (and this BBS's own classic surface) treats `B` as the sign-off reflex.
    // The plain surface used to map `b` to bulletins, so an operator reaching for disconnect opened
    // the bulletin list instead. `b` now signs off (same as quit/bye); bulletins keep `bu`/`bul`.

    [Fact]
    public async Task Shortcut_b_SignsOff_DoesNotOpenBulletins()
    {
        // A bulletin is present so a mistaken open would be obvious in the output.
        _h.Store.AddMessage(Drafts.Bulletin(from: "G4ABC", to: "SALE", subject: "should-not-appear"));
        (BbsSessionEndReason end, ScriptedTerminal t) = await RunAsync("b");
        Assert.Equal(BbsSessionEndReason.Bye, end);
        Assert.Contains("73 - thanks for calling GB7PDN", t.Output, StringComparison.Ordinal);
        // The bulletin list never rendered.
        Assert.DoesNotContain("There is 1 bulletin:", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-appear", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Word_bye_SignsOff_LikeQuit()
    {
        (BbsSessionEndReason end, ScriptedTerminal t) = await RunAsync("bye");
        Assert.Equal(BbsSessionEndReason.Bye, end);
        Assert.Contains("73 - thanks for calling GB7PDN", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prefix_bu_StillOpensBulletins()
    {
        // Bulletins keep an obvious, slightly longer prefix; only "bulletins" starts with "bu",
        // so the natural unambiguous-prefix rule resolves it (no shortcut needed).
        _h.Store.AddMessage(Drafts.Bulletin(from: "G4ABC", to: "SALE", subject: "FT-817 for sale"));
        (_, ScriptedTerminal t) = await RunAsync("bu", "quit");
        Assert.Contains("There is 1 bulletin:", t.Output, StringComparison.Ordinal);
        Assert.Contains("FT-817 for sale", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ListsBye_AndNotesBIsTheShortcut()
    {
        (_, ScriptedTerminal t) = await RunAsync("help", "quit");
        Assert.Contains("bye - sign off and disconnect", t.Output, StringComparison.Ordinal);
        Assert.Contains("b is the shortcut", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Topics_ShowsCategoriesWithCounts()
    {
        _h.Store.AddMessage(Drafts.Bulletin(to: "SALE", subject: "one"));
        _h.Store.AddMessage(Drafts.Bulletin(to: "SALE", subject: "two"));
        _h.Store.AddMessage(Drafts.Bulletin(to: "ARRL", subject: "three"));
        (_, ScriptedTerminal t) = await RunAsync("topics", "quit");
        Assert.Contains("Bulletin topics in use", t.Output, StringComparison.Ordinal);
        Assert.Contains("SALE - 2 bulletins", t.Output, StringComparison.Ordinal);
        Assert.Contains("ARRL - 1 bulletin\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- prefix matching (the mandate's rule)

    [Fact]
    public async Task UnambiguousPrefix_l_RunsList()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "hi"));
        (_, ScriptedTerminal t) = await RunAsync("l", "quit");
        Assert.Contains("You have 1 new message:", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnambiguousPrefix_r3_RunsRead()
    {
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "hi", body: "x\r"));
        (_, ScriptedTerminal t) = await RunAsync("r 1", "quit");
        Assert.Contains("Subject: hi\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnambiguousPrefix_q_RunsQuit()
    {
        (BbsSessionEndReason end, _) = await RunAsync("q");
        Assert.Equal(BbsSessionEndReason.Bye, end);
    }

    [Fact]
    public async Task UnambiguousPrefix_se_RunsSend()
    {
        await RunAsync("se G4ABC", "subj", "body", "/ex", "quit");
        Assert.Equal("G4ABC", Assert.Single(_h.Store.GetMessage(1)!.Recipients).ToCall);
    }

    [Fact]
    public async Task ExactWord_Read_WinsOverItsPrefixSiblings()
    {
        // "read" is a full word and also shares the "re" prefix with "reply"; the exact match wins.
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "hi", body: "x\r"));
        (_, ScriptedTerminal t) = await RunAsync("read 1", "quit");
        Assert.Contains("Subject: hi\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shortcut_re_ResolvesToReply()
    {
        // The mandate lists `re…` as a die-hard's reply shortcut: it wins over the bare
        // ambiguous-prefix rule (read/reply) and runs reply.
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "question"));
        (_, ScriptedTerminal t) = await RunAsync("re 1", "my answer", "/ex", "quit");
        Assert.Equal("Re:question", _h.Store.GetMessage(2)!.Subject);
        Assert.DoesNotContain("Did you mean", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousPrefix_rea_StillUnambiguous_IsRead()
    {
        // A longer prefix that only "read" starts with resolves straight to read.
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "hi", body: "x\r"));
        (_, ScriptedTerminal t) = await RunAsync("rea 1", "quit");
        Assert.Contains("Subject: hi\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousPrefix_del_AsksDidYouMean()
    {
        // "del" is a prefix of "delete" and "delivered".
        (_, ScriptedTerminal t) = await RunAsync("del 1", "quit");
        Assert.Contains("Did you mean delete or delivered?", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownWord_FriendlySentence_NoInvalidCommand()
    {
        (_, ScriptedTerminal t) = await RunAsync("wibble", "quit");
        Assert.Contains("I don't know \"wibble\"", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid Command", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyLine_JustReprompts()
    {
        (_, ScriptedTerminal t) = await RunAsync("", "quit");
        Assert.DoesNotContain("I don't know", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- help (full sentences)

    [Fact]
    public async Task Help_ExplainsEverythingInSentences()
    {
        (_, ScriptedTerminal t) = await RunAsync("help", "quit");
        Assert.Contains("list - show your new mail", t.Output, StringComparison.Ordinal);
        Assert.Contains("read <n> - read message number n", t.Output, StringComparison.Ordinal);
        Assert.Contains("send <call> - write a new message", t.Output, StringComparison.Ordinal);
        Assert.Contains("classic - switch to the old-style terse command surface", t.Output, StringComparison.Ordinal);
        Assert.Contains("only need the first few letters", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_PrefixH_Works()
    {
        (_, ScriptedTerminal t) = await RunAsync("h", "quit");
        Assert.Contains("list - show your new mail", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- plain paging (more? (yes/no))

    [Fact]
    public async Task Paging_AsksMoreYesNo_AndContinuesOnYes()
    {
        _h.Settings.Save("M0LTE", new UserSettings { PageLength = 3, InterfaceMode = InterfaceMode.Plain });
        for (int i = 0; i < 8; i++)
        {
            _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: $"msg {i + 1}"));
        }

        (_, ScriptedTerminal t) = await RunAsync("list", "yes", "yes", "yes", "quit");
        Assert.Contains("more? (yes/no) ", t.Output, StringComparison.Ordinal);
        Assert.Contains("from G4ABC, 11 Jun: msg 1\r", t.Output, StringComparison.Ordinal); // oldest reached
    }

    [Fact]
    public async Task Paging_No_StopsOutput()
    {
        _h.Settings.Save("M0LTE", new UserSettings { PageLength = 3, InterfaceMode = InterfaceMode.Plain });
        for (int i = 0; i < 8; i++)
        {
            _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: $"msg {i + 1}"));
        }

        (_, ScriptedTerminal t) = await RunAsync("list", "no", "quit");
        Assert.Contains("OK, stopping there.\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("msg 1\r", t.Output, StringComparison.Ordinal); // oldest never reached
    }

    // ---------------------------------------------------------------- mode toggle + persistence

    [Fact]
    public async Task Classic_Command_FlipsAndPersists()
    {
        (_, ScriptedTerminal t) = await RunAsync("classic", "B");
        Assert.Contains("Switched to the classic terse surface.", t.Output, StringComparison.Ordinal);
        // Persisted: Load returns Classic after Save.
        Assert.Equal(InterfaceMode.Classic, _h.Settings.Load("M0LTE").InterfaceMode);
    }

    [Fact]
    public async Task Classic_TakesEffectFromNextPrompt()
    {
        // After `classic`, the next prompt is the classic `de CALL>` and classic verbs work.
        (_, ScriptedTerminal t) = await RunAsync("classic", "V", "B");
        Assert.Contains("de GB7PDN>\r\n", t.Output, StringComparison.Ordinal);
        Assert.Contains("BBS Version 0.1.0\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Classic_PersistsAcrossReconnect()
    {
        await RunAsync("classic", "B");
        // A fresh session for the same user now opens in classic.
        (_, ScriptedTerminal t) = await RunAsync("B");
        Assert.Contains("de GB7PDN>\r\n", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_Command_FromClassic_FlipsBackAndPersists()
    {
        _h.Settings.Save("M0LTE", new UserSettings { InterfaceMode = InterfaceMode.Classic });
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "PLAIN", "B");
        Assert.Contains("Switched to the plain-language surface.", t.Output, StringComparison.Ordinal);
        Assert.Equal(InterfaceMode.Plain, _h.Settings.Load("M0LTE").InterfaceMode);
    }

    [Fact]
    public async Task Plain_Command_WhenAlreadyPlain_SaysSo()
    {
        (_, ScriptedTerminal t) = await RunAsync("plain", "quit");
        Assert.Contains("already using the plain-language surface", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- profile setters in plain

    [Fact]
    public async Task Name_SetsAndPersists()
    {
        (_, ScriptedTerminal t) = await RunAsync("name John", "quit");
        Assert.Contains("I'll call you John", t.Output, StringComparison.Ordinal);
        Assert.Equal("John", _h.Store.GetUser("M0LTE")!.Name);
    }

    [Fact]
    public async Task Home_SetsHomeMailbox()
    {
        (_, ScriptedTerminal t) = await RunAsync("home GB7PDN.GBR.EURO", "quit");
        Assert.Contains("Your home mailbox is now GB7PDN.GBR.EURO.", t.Output, StringComparison.Ordinal);
        Assert.Equal("GB7PDN.GBR.EURO", _h.Store.GetUser("M0LTE")!.HomeBbs);
    }

    [Fact]
    public async Task Info_LooksUpAStation()
    {
        _h.KnownUser("G4ABC", name: "Bob", homeBbs: "GB7BSK.GBR.EURO");
        (_, ScriptedTerminal t) = await RunAsync("info G4ABC", "quit");
        Assert.Contains("G4ABC, is Bob", t.Output, StringComparison.Ordinal);
        Assert.Contains("home mailbox GB7BSK.GBR.EURO", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pagelength_SetsAndPersists()
    {
        await RunAsync("pagelength 20", "quit");
        Assert.Equal(20, _h.Settings.Load("M0LTE").PageLength);
    }

    // ---------------------------------------------------------------- greeting onboarding (sentences)

    [Fact]
    public async Task NewUser_OnboardingAsksNameInASentence()
    {
        var h = new SessionHarness(InterfaceMode.Plain);
        try
        {
            (_, ScriptedTerminal t) = await h.RunAsync("M0NEW", "Alice", "quit");
            Assert.Contains("what's your name?", t.Output, StringComparison.Ordinal);
            Assert.Contains("Good to meet you", t.Output, StringComparison.Ordinal);
            Assert.Equal("Alice", h.Store.GetUser("M0NEW")!.Name);
        }
        finally
        {
            h.Dispose();
        }
    }

    [Fact]
    public async Task NewUser_NoHome_OffersHomeMailboxInSentences()
    {
        var h = new SessionHarness(InterfaceMode.Plain);
        try
        {
            (_, ScriptedTerminal t) = await h.RunAsync("M0NEW", "Alice", "quit");
            Assert.Contains("set this as your home", t.Output, StringComparison.Ordinal);
            Assert.Contains("home GB7PDN.", t.Output, StringComparison.Ordinal);
        }
        finally
        {
            h.Dispose();
        }
    }

    [Fact]
    public async Task BbsFlaggedCaller_AlwaysGetsClassic_EvenWithPlainDefault()
    {
        // A forwarding/automated peer pattern-matches the legacy prompts: the plain default must
        // never apply to it. (Forwarding proper is SID-triggered in the demux; this guards the
        // fall-through case.)
        _h.Partner();
        (_, ScriptedTerminal t) = await _h.RunAsync(SessionHarness.PartnerCall, "B");
        Assert.StartsWith("de GB7PDN>\r\n", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("welcome to the", t.Output, StringComparison.Ordinal);
    }
}
