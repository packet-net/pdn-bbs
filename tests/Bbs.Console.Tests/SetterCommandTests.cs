namespace Bbs.Console.Tests;

/// <summary>N/Q/Z/Home setters, X expert toggle, OP page length, I info (compat spec §1.3).</summary>
public sealed class SetterCommandTests : IDisposable
{
    private readonly SessionHarness _h = new();

    public SetterCommandTests() => _h.KnownUser("M0LTE");

    public void Dispose() => _h.Dispose();

    // ---------------------------------------------------------------- N name

    [Fact]
    public async Task Name_Sets_AndReplies()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "N John", "B");
        Assert.Contains("Name is John\r", t.Output, StringComparison.Ordinal);
        Assert.Equal("John", _h.Store.GetUser("M0LTE")!.Name);
    }

    [Fact]
    public async Task Name_WithSpaces_StoredWhole()
    {
        await _h.RunAsync("M0LTE", "N John Smith", "B");
        Assert.Equal("John Smith", _h.Store.GetUser("M0LTE")!.Name);
    }

    [Fact]
    public async Task Name_TruncatedAtSeventeen()
    {
        // §1.3: "source truncates at 17; doc says 12" — source wins.
        await _h.RunAsync("M0LTE", "N ABCDEFGHIJKLMNOPQRST", "B");
        Assert.Equal("ABCDEFGHIJKLMNOPQ", _h.Store.GetUser("M0LTE")!.Name);
    }

    [Fact]
    public async Task Name_Bare_ShowsCurrent()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "N", "B");
        Assert.Contains("Name is Tom\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Q qth

    [Fact]
    public async Task Qth_Sets_WithVerbatimReply()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "Q Ipswich", "B");
        Assert.Contains("QTH is Ipswich\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qth_TruncatedAtThirty()
    {
        // §5 WP parser caps: QTH ≤ 30.
        await _h.RunAsync("M0LTE", "Q " + new string('x', 35), "B");
        Assert.Equal(new string('x', 30), _h.Settings.Load("M0LTE").Qth);
    }

    [Fact]
    public async Task Qth_PersistsAcrossSessions()
    {
        await _h.RunAsync("M0LTE", "Q Ipswich", "B");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "Q", "B");
        Assert.Contains("QTH is Ipswich\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Z zip

    [Fact]
    public async Task Zip_Sets_WithVerbatimReply()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "Z IP1 2AB", "B");
        Assert.Contains("ZIP is IP1 2AB\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zip_TruncatedAtEight()
    {
        // §5 WP parser caps: ZIP ≤ 8.
        await _h.RunAsync("M0LTE", "Z 1234567890", "B");
        Assert.Equal("12345678", _h.Settings.Load("M0LTE").Zip);
    }

    // ---------------------------------------------------------------- Home / HOMEBBS

    [Fact]
    public async Task Home_HierarchicalAddress_SetsWithoutWarning()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "HOME GB7BPQ.#23.GBR.EURO", "B");
        Assert.Contains("HomeBBS is GB7BPQ.#23.GBR.EURO\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Please enter HA", t.Output, StringComparison.Ordinal);
        Assert.Equal("GB7BPQ.#23.GBR.EURO", _h.Store.GetUser("M0LTE")!.HomeBbs);
    }

    [Fact]
    public async Task Home_BareCall_WarnsButStillSets()
    {
        // §1.3 verbatim bare-call warning.
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "HOME GB7BPQ", "B");
        Assert.Contains(
            "Please enter HA with HomeBBS eg g8bpq.gbr.eu - this will help message routing\r",
            t.Output,
            StringComparison.Ordinal);
        Assert.Equal("GB7BPQ", _h.Store.GetUser("M0LTE")!.HomeBbs);
    }

    [Fact]
    public async Task Home_Dot_Deletes()
    {
        await _h.RunAsync("M0LTE", "HOME .", "B");
        Assert.Null(_h.Store.GetUser("M0LTE")!.HomeBbs);
    }

    [Fact]
    public async Task Home_Bare_ShowsCurrent()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "HOME", "B");
        Assert.Contains("HomeBBS is GB7PDN.#23.GBR.EURO\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HomeBbs_Alias_Works()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "HOMEBBS GB7BPQ.GBR.EURO", "B");
        Assert.Contains("HomeBBS is GB7BPQ.GBR.EURO\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_IsUpperCased()
    {
        await _h.RunAsync("M0LTE", "HOME gb7bpq.gbr.euro", "B");
        Assert.Equal("GB7BPQ.GBR.EURO", _h.Store.GetUser("M0LTE")!.HomeBbs);
    }

    // ---------------------------------------------------------------- X expert

    [Fact]
    public async Task Expert_Toggle_OnShape()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "X", "B");
        Assert.Contains("Expert Mode\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expert_ToggleTwice_OffShape()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "X", "X", "B");
        Assert.Contains("Expert Mode off\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expert_PersistsOnTheUserSettingsRecord()
    {
        await _h.RunAsync("M0LTE", "X", "B");
        Assert.True(_h.Settings.Load("M0LTE").Expert);

        // A fresh session starts from the persisted state: next toggle goes off.
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "X", "B");
        Assert.Contains("Expert Mode off\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expert_ConfigDefaultTrue_FirstToggleGoesOff()
    {
        _h.Config = _h.Config with { ExpertDefault = true };
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "X", "B");
        Assert.Contains("Expert Mode off\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expert_PromptIsUnchanged()
    {
        // [VERIFY-ORACLE #6]: source default keeps `de CALL>` for expert users too.
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "X", "V", "B");
        Assert.Equal(3, t.Output.Split("de GB7PDN>\r\n").Length - 1);
    }

    // ---------------------------------------------------------------- OP page length

    [Fact]
    public async Task PageLength_Set_EchoShape()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "OP 20", "B");
        Assert.Contains("Page Length is 20\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageLength_OneToNine_TooShort()
    {
        // §1.3: "1–9 rejected".
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "OP 9", "B");
        Assert.Contains("Page Length 9 is too short\r", t.Output, StringComparison.Ordinal);
        Assert.Null(_h.Settings.Load("M0LTE").PageLength);
    }

    [Fact]
    public async Task PageLength_Zero_TurnsPagingOff()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "OP 0", "B");
        Assert.Contains("Page Length is 0\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageLength_Bare_ShowsCurrent()
    {
        await _h.RunAsync("M0LTE", "OP 25", "B");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "OP", "B");
        Assert.Contains("Page Length is 25\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageLength_NonNumeric_InvalidFormat()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "OP lots", "B");
        Assert.Contains("*** Error: Invalid Format\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- I info

    [Fact]
    public async Task Info_NoFileConfigured_VerbatimShape()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "I", "B");
        Assert.Contains("SYSOP has not created an INFO file\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_ConfiguredText_IsSent()
    {
        _h.Config = _h.Config with { InfoText = "Welcome to GB7PDN\rRunning pdn-bbs\r" };
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "I", "B");
        Assert.Contains("Welcome to GB7PDN\rRunning pdn-bbs\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_Call_KnownUser_ShowsRecord()
    {
        _h.KnownUser("G8BPQ", name: "John", homeBbs: "GB7BPQ.#23.GBR.EURO");
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "I G8BPQ", "B");
        Assert.Contains("Callsign: G8BPQ\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("Name: John\r", t.Output, StringComparison.Ordinal);
        Assert.Contains("HomeBBS: GB7BPQ.#23.GBR.EURO\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_Call_Unknown_NoInformation()
    {
        (_, ScriptedTerminal t) = await _h.RunAsync("M0LTE", "I NOBODY", "B");
        Assert.Contains("No information on NOBODY\r", t.Output, StringComparison.Ordinal);
    }
}
