namespace Bbs.Console.Tests;

/// <summary>Connect greeting (compat spec §1.1), prompt/sign-off shapes (§1.2) and session end reasons.</summary>
public sealed class GreetingAndLifetimeTests : IDisposable
{
    private readonly SessionHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ---------------------------------------------------------------- §1.2 prompt

    [Fact]
    public async Task Prompt_IsExactDeCallShape_WithCrLf()
    {
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "B");
        Assert.Contains("de GB7PDN>\r\n", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_SentAfterEveryCommandCompletion()
    {
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "V", "V", "B");
        int count = t.Output.Split("de GB7PDN>\r\n").Length - 1;
        Assert.Equal(3, count); // initial + after each V; none after B (session over)
    }

    [Fact]
    public async Task WelcomeText_DoesNotEndWithPromptCharacter()
    {
        // §1.2: welcome text MUST NOT end in '>' (prompt-faking guard for automated peers).
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "B");
        string beforePrompt = t.Output[..t.Output.IndexOf("de GB7PDN>", StringComparison.Ordinal)].TrimEnd('\r', '\n');
        Assert.False(beforePrompt.EndsWith('>'));
    }

    // ---------------------------------------------------------------- §1.1 greeting

    [Fact]
    public async Task KnownUser_WelcomeShowsNameLatestAndLastListed()
    {
        _h.KnownUser("M0LTE", name: "Tom");
        _h.Store.AddMessage(Drafts.Personal());
        _h.Store.AddMessage(Drafts.Bulletin());
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "B");
        Assert.Contains("Hello Tom. Latest Message is 2, Last listed is 0\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownUser_LastListedReflectsPriorSession()
    {
        _h.KnownUser("M0LTE");
        _h.Store.AddMessage(Drafts.Personal());
        _h.Store.AddMessage(Drafts.Bulletin());
        await _h.RunAsync("M0LTE", "L", "B");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "B");
        Assert.Contains("Latest Message is 2, Last listed is 2\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewUser_GetsExactNamePrompt()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "Tom", "B");
        Assert.StartsWith("Please enter your Name\r>\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewUser_NameLineIsStoredOnTheUserRecord()
    {
        await _h.RunAsync("M0LTE", "Tom", "B");
        Assert.Equal("Tom", _h.Store.GetUser("M0LTE")!.Name);
    }

    [Fact]
    public async Task NewUser_NameTruncatedAtSeventeen()
    {
        // §1.3: source truncates the name at 17 (the doc's 12 is the known doc error).
        await _h.RunAsync("M0LTE", "ABCDEFGHIJKLMNOPQRSTU", "B");
        Assert.Equal("ABCDEFGHIJKLMNOPQ", _h.Store.GetUser("M0LTE")!.Name);
    }

    [Fact]
    public async Task NewUser_EmptyNameLine_LeavesNameUnset()
    {
        await _h.RunAsync("M0LTE", "", "B");
        Assert.Null(_h.Store.GetUser("M0LTE")!.Name);
    }

    [Fact]
    public async Task NewUser_WelcomeUsesCallsignWhenNoName()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "", "B");
        Assert.Contains("Hello M0LTE. Latest Message is 0", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserWithoutHomeBbs_GetsTheVerbatimNag()
    {
        _h.KnownUser("M0LTE", homeBbs: null);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "B");
        Assert.Contains("Please enter your Home BBS using the Home command.\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("You may also enter your QTH and ZIP/Postcode using qth and zip commands.\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserWithHomeBbs_GetsNoNag()
    {
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "B");
        Assert.DoesNotContain("Please enter your Home BBS", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_StampsLastLogin()
    {
        _h.KnownUser("M0LTE");
        await _h.RunAsync("M0LTE", "B");
        Assert.Equal(_h.Time.GetUtcNow(), _h.Store.GetUser("M0LTE")!.LastLogin);
    }

    [Fact]
    public async Task BbsFlaggedCaller_SkipsBannerAndNamePrompt()
    {
        // §1.1: a BBS-flagged caller doesn't get the chatty banner path.
        _h.Partner();
        (_, ScriptedTerminal t) = await _h.RunAsync(SessionHarness.PartnerCall, "B");
        Assert.StartsWith("de GB7PDN>\r\n", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Please enter your Name", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SsidCaller_IsTheBaseCallUser()
    {
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE-9", "B");
        Assert.Contains("Hello Tom.", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- §1.2/§1.3 sign-off + end reasons

    [Fact]
    public async Task B_SignsOffAndReturnsBye()
    {
        _h.KnownUser("M0LTE");
        (BbsSessionEndReason end, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "B");
        Assert.Equal(BbsSessionEndReason.Bye, end);
        Assert.EndsWith("73 de GB7PDN\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bye_Word_AlsoSignsOff()
    {
        _h.KnownUser("M0LTE");
        (BbsSessionEndReason end, _) = await _h.RunAsync("M0LTE", "BYE");
        Assert.Equal(BbsSessionEndReason.Bye, end);
    }

    [Fact]
    public async Task Commands_AreCaseInsensitive()
    {
        _h.KnownUser("M0LTE");
        (BbsSessionEndReason end, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "bye");
        Assert.Equal(BbsSessionEndReason.Bye, end);
        Assert.DoesNotContain("Invalid Command", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Node_SignsOffAndReturnsNode()
    {
        // §1.3 NODE: exit back to the node — Host keeps the link, so it needs the distinct reason.
        _h.KnownUser("M0LTE");
        (BbsSessionEndReason end, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "NODE");
        Assert.Equal(BbsSessionEndReason.Node, end);
        Assert.EndsWith("73 de GB7PDN\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptExhausted_ReturnsDrop()
    {
        _h.KnownUser("M0LTE");
        (BbsSessionEndReason end, _) = await _h.RunAsync("M0LTE", "V");
        Assert.Equal(BbsSessionEndReason.Drop, end);
    }

    [Fact]
    public async Task DropMidTextEntry_ReturnsDrop()
    {
        _h.KnownUser("M0LTE");
        (BbsSessionEndReason end, _) = await _h.RunAsync("M0LTE", "SP G8BPQ", "title", "body without terminator");
        Assert.Equal(BbsSessionEndReason.Drop, end);
    }

    // ---------------------------------------------------------------- §1.3 errors

    [Fact]
    public async Task UnknownCommand_InvalidCommandShape()
    {
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "WIBBLE", "B");
        Assert.Contains("Invalid Command\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FourErrors_SessionStaysOpen()
    {
        _h.KnownUser("M0LTE");
        (BbsSessionEndReason end, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "W1", "W2", "W3", "W4", "B");
        Assert.Equal(BbsSessionEndReason.Bye, end);
        Assert.DoesNotContain("Too many errors", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FifthError_TooManyErrorsClosing()
    {
        // §1.3: ">4 in a session → Too many errors - closing + disconnect".
        _h.KnownUser("M0LTE");
        (BbsSessionEndReason end, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "W1", "W2", "W3", "W4", "W5", "B");
        Assert.Equal(BbsSessionEndReason.Bye, end);
        Assert.Contains("Too many errors - closing\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyLine_JustRepromptsWithoutError()
    {
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "", "B");
        Assert.DoesNotContain("Invalid Command", t.Output, StringComparison.Ordinal);
        Assert.Equal(2, t.Output.Split("de GB7PDN>\r\n").Length - 1);
    }

    // ---------------------------------------------------------------- ?/H/A/V (§1.3)

    [Fact]
    public async Task Help_QuestionMark_SendsBuiltInSummary()
    {
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "?", "B");
        Assert.Contains("R n Read", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_H_IsTheSameCommand()
    {
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "H", "B");
        Assert.Contains("R n Read", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ConfigTextReplacesBuiltIn()
    {
        // §1.3: "If help.txt exists it replaces the built-in text".
        _h.Config = _h.Config with { HelpText = "custom help line\r" };
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "?", "B");
        Assert.Contains("custom help line\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("R n Read", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Abort_OutsidePaging_StillSendsTheShape()
    {
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "A", "B");
        Assert.Contains("\rOutput aborted\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_ShowsBbsAndNodeLines()
    {
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "V", "B");
        Assert.Contains("BBS Version 0.1.0\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("Node Version pdn 1.2.3\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_OmitsNodeLineWhenUnconfigured()
    {
        _h.Config = _h.Config with { NodeVersion = null };
        _h.KnownUser("M0LTE");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "V", "B");
        Assert.Contains("BBS Version 0.1.0\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Node Version", t.Output, StringComparison.Ordinal);
    }
}
