using System.Globalization;

namespace Bbs.Console.Tests;

/// <summary>OP paging behaviour (compat spec §1.7): prompts, CR-continue, A-abort, mid-list R.</summary>
public sealed class PagingTests : IDisposable
{
    private const string GenericPrompt = "<A>bort, <CR> Continue..>";
    private const string ListingPrompt = "<A>bort, <R Msg(s)>, <CR> = Continue..>";

    private readonly SessionHarness _h = new();

    public PagingTests()
    {
        _h.KnownUser("M0LTE");
        _h.Settings.Save("M0LTE", new UserSettings { PageLength = 5 });
    }

    public void Dispose() => _h.Dispose();

    private void SeedLongMessage(int bodyLines)
    {
        string body = string.Concat(Enumerable.Range(1, bodyLines).Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"body line {i}\r")));
        _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "long", body: body));
    }

    private void SeedMessages(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _h.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE",
                subject: string.Create(CultureInfo.InvariantCulture, $"msg {i + 1}")));
        }
    }

    [Fact]
    public async Task Read_PagesWithTheGenericContinuePrompt()
    {
        SeedLongMessage(12);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 1", "", "", "", "B");
        Assert.Contains(GenericPrompt + "\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_CrContinues_AllLinesDelivered()
    {
        SeedLongMessage(12);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 1", "", "", "", "B");
        Assert.Contains("body line 1\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("body line 12\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_A_AbortsWithTheVerbatimShape_AndStops()
    {
        SeedLongMessage(12);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 1", "A", "B");
        Assert.Contains("\rOutput aborted\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("body line 12", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_AbortIsCaseInsensitive()
    {
        SeedLongMessage(12);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "R 1", "a", "B");
        Assert.Contains("\rOutput aborted\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_UsesTheListingContinuePrompt()
    {
        SeedMessages(8);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "L", "", "B");
        Assert.Contains(ListingPrompt + "\r", t.Output, StringComparison.Ordinal);
        Assert.Contains(" msg 1\r", t.Output, StringComparison.Ordinal); // oldest line arrived after continue
    }

    [Fact]
    public async Task Listing_RMidList_ReadsThenResumes()
    {
        // §1.7: "you can type R nnn mid-list, then the list resumes".
        SeedMessages(8);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "L", "R 8", "B");
        Assert.Contains("Title: msg 8\r", t.Output, StringComparison.Ordinal);
        Assert.Contains(" msg 1\r", t.Output, StringComparison.Ordinal); // listing resumed to the end
        int read = t.Output.IndexOf("Title: msg 8", StringComparison.Ordinal);
        int resumed = t.Output.IndexOf(" msg 1\r", StringComparison.Ordinal);
        Assert.True(read >= 0 && resumed > read, "expected the mid-list read before the resumed tail");
    }

    [Fact]
    public async Task Listing_A_AbortsRemainingLines()
    {
        SeedMessages(8);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "L", "A", "B");
        Assert.Contains("\rOutput aborted\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(" msg 1\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactlyOnePage_NoContinuePrompt()
    {
        SeedMessages(5);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "L", "B");
        Assert.DoesNotContain(ListingPrompt, t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageLengthZero_DisablesPaging()
    {
        _h.Settings.Save("M0LTE", new UserSettings { PageLength = 0 });
        SeedMessages(40);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "L", "B");
        Assert.DoesNotContain(ListingPrompt, t.Output, StringComparison.Ordinal);
        Assert.Contains(" msg 1\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigDefaultPageLength_AppliesWithoutOp()
    {
        _h.Settings.Save("M0LTE", new UserSettings());
        _h.Config = _h.Config with { DefaultPageLength = 5 };
        SeedMessages(8);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "L", "", "B");
        Assert.Contains(ListingPrompt + "\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpInSession_TakesEffectImmediately()
    {
        _h.Settings.Save("M0LTE", new UserSettings { PageLength = 0 });
        SeedMessages(12);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "OP 10", "L", "", "B");
        Assert.Contains(ListingPrompt + "\r", t.Output, StringComparison.Ordinal);
        Assert.Contains(" msg 1\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PagingPromptResponse_OtherText_Continues()
    {
        SeedMessages(8);
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "L", "yes please", "B");
        Assert.Contains(" msg 1\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DropAtThePagingPrompt_EndsAsDrop()
    {
        SeedMessages(8);
        (BbsSessionEndReason end, _) = await _h.RunAsync("M0LTE", "L");
        Assert.Equal(BbsSessionEndReason.Drop, end);
    }

    [Fact]
    public async Task InfoText_IsPaged()
    {
        _h.Config = _h.Config with
        {
            InfoText = string.Concat(Enumerable.Range(1, 12).Select(i =>
                string.Create(CultureInfo.InvariantCulture, $"info {i}\r"))),
        };
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "I", "", "", "B");
        Assert.Contains(GenericPrompt + "\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("info 12\r", t.Output, StringComparison.Ordinal);
    }
}
