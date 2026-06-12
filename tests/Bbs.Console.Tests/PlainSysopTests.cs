using Bbs.Core;

namespace Bbs.Console.Tests;

/// <summary>
/// The plain-language sysop diagnostics (forwarding.md F-2 ops layer, vocabulary §58 + route
/// phrasing §77): <c>forwarding</c> (per-partner status + queue depth), <c>queue</c> (the
/// pending forward queue) and <c>route &lt;call&gt;</c> (the routing-explain trace). These are
/// PLAIN-surface words, sysop-gated; a non-sysop is refused. Assertions are on the plain
/// phrasing, never the routing internals (no "depth 3 HR match").
/// </summary>
public sealed class PlainSysopTests : IDisposable
{
    // GB7PDN sits at #23.GBR.EURO (the harness default home BBS uses this tree).
    private readonly SessionHarness _h = new(InterfaceMode.Plain);

    public PlainSysopTests()
    {
        // The route-explain engine needs our H-Route, exactly as HostComposition supplies it.
        _h.Config = _h.Config with { HRoute = "#23.GBR.EURO" };
        _h.KnownUser(SessionHarness.SysopCall);
    }

    public void Dispose() => _h.Dispose();

    private Task<(BbsSessionEndReason End, ScriptedTerminal Terminal)> AsSysop(params string[] lines) =>
        _h.RunAsync(SessionHarness.SysopCall, lines);

    private Task<(BbsSessionEndReason End, ScriptedTerminal Terminal)> AsUser(params string[] lines)
    {
        _h.KnownUser("M0LTE");
        return _h.RunAsync("M0LTE", lines);
    }

    private void AddPartner(
        string call,
        bool enabled = true,
        bool immediate = false,
        int intervalSeconds = 3600,
        string[]? toCalls = null,
        string[]? hRoutesP = null,
        string[]? atCalls = null)
        => _h.Store.UpsertPartner(new Partner
        {
            Call = call,
            Enabled = enabled,
            ForwardNewImmediately = immediate,
            ForwardIntervalSeconds = intervalSeconds,
            ToCalls = toCalls ?? [],
            HRoutesP = hRoutesP ?? [],
            AtCalls = atCalls ?? [],
        });

    // ---------------------------------------------------------------- forwarding (status)

    [Fact]
    public async Task Forwarding_NoPartners_PlainSentence()
    {
        (_, ScriptedTerminal t) = await AsSysop("forwarding", "quit");
        Assert.Contains("There are no forwarding partners set up.\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forwarding_ListsEachPartner_WithCadenceAndQueueDepth()
    {
        AddPartner("GB7RDG-2", enabled: true, immediate: true);
        AddPartner("GB7XYZ", enabled: false, intervalSeconds: 1800);

        // One message queued to GB7RDG-2 so the depth is non-zero.
        Message m = _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "G8ZZZ"));
        _h.Store.EnqueueForwards(m.Number, ["GB7RDG-2"]);

        (_, ScriptedTerminal t) = await AsSysop("forwarding", "quit");

        Assert.Contains("You have 2 forwarding partners:", t.Output, StringComparison.Ordinal);
        Assert.Contains("GB7RDG-2 - dialled, dials as soon as mail arrives; 1 message waiting.", t.Output, StringComparison.Ordinal);
        Assert.Contains("GB7XYZ - not dialled (turned off), tries every 30 minutes; nothing waiting.", t.Output, StringComparison.Ordinal);
        // Jargon is gone: no HR/flood/directed letters.
        Assert.DoesNotContain("HR", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("flood", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forwarding_NamesTheLiveHealthDetailAsDeferred()
    {
        AddPartner("GB7RDG-2");
        (_, ScriptedTerminal t) = await AsSysop("forwarding", "quit");
        // wart 3 (last cycle / failure / next retry) is named as a deferral, not faked.
        Assert.Contains("isn't available yet", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- queue (pending forward queue)

    [Fact]
    public async Task Queue_Empty_PlainSentence()
    {
        AddPartner("GB7RDG-2");
        (_, ScriptedTerminal t) = await AsSysop("queue", "quit");
        Assert.Contains("Nothing is waiting to forward.\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_ShowsPendingMessages_NumberFromSubjectAndPartner()
    {
        AddPartner("GB7RDG-2");
        Message m = _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "G8ZZZ", subject: "Antenna party"));
        _h.Store.EnqueueForwards(m.Number, ["GB7RDG-2"]);

        (_, ScriptedTerminal t) = await AsSysop("queue", "quit");

        Assert.Contains("1 message waiting to forward:", t.Output, StringComparison.Ordinal);
        Assert.Contains(
            $"{m.Number}) from G4ABC: Antenna party - waiting for GB7RDG-2\r", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- route (explain trace)

    [Fact]
    public async Task Route_LocalUser_StaysHere()
    {
        // G4LOC is one of our users → the personal stays here (design.md rule #1).
        _h.KnownUser("G4LOC");
        AddPartner("GB7RDG-2", atCalls: ["*"]); // even with a catch-all partner present
        (_, ScriptedTerminal t) = await AsSysop("route g4loc", "quit");
        Assert.Contains("A message to G4LOC would stay here - G4LOC is one of our users.\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Route_HierarchicalRegionMatch_NamesRegionAndCloseness_NoDepthJargon()
    {
        // GB7RDG-2 carries personals toward GBR.EURO; route a personal addressed @GBR.EURO.
        AddPartner("GB7RDG-2", hRoutesP: ["GBR.EURO"]);
        (_, ScriptedTerminal t) = await AsSysop("route g4abc@gbr.euro", "quit");

        Assert.Contains(
            "A message to G4ABC would go to GB7RDG-2 - it carries mail for stations in region gbr.euro (closest match).\r",
            t.Output, StringComparison.Ordinal);
        // The jargon is gone (forwarding.md §77: never "depth 3 HR match").
        Assert.DoesNotContain("depth", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("HR", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Route_WildcardCatchAll_PlainCatchAllSentence()
    {
        // A catch-all (*) AT route, an address with nothing more specific → the catch-all wins.
        AddPartner("GB7RDG-2", atCalls: ["*"]);
        (_, ScriptedTerminal t) = await AsSysop("route g4abc@somewhere.else", "quit");
        Assert.Contains(
            "A message to G4ABC would go to GB7RDG-2 - it's our catch-all route for mail with nowhere more specific to go.\r",
            t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Route_NoPartnerCovers_NowhereToGo()
    {
        // A partner that covers only GBR.EURO; an address @USA matches nothing and isn't local.
        AddPartner("GB7RDG-2", hRoutesP: ["GBR.EURO"]);
        (_, ScriptedTerminal t) = await AsSysop("route w1aw@usa", "quit");
        Assert.Contains(
            "A message to W1AW has nowhere to go - no partner covers that address.\r", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Route_NoCallsign_FriendlyPrompt()
    {
        (_, ScriptedTerminal t) = await AsSysop("route", "quit");
        Assert.Contains("Which station? Try route and a callsign", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- sysop gating (non-sysop refused)

    [Fact]
    public async Task NonSysop_Forwarding_IsRefused()
    {
        AddPartner("GB7RDG-2");
        (_, ScriptedTerminal t) = await AsUser("forwarding", "quit");
        Assert.Contains("Sorry, that's a sysop-only command.\r", t.Output, StringComparison.Ordinal);
        // The refusal leaks no partner status.
        Assert.DoesNotContain("GB7RDG-2", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonSysop_Queue_IsRefused()
    {
        AddPartner("GB7RDG-2");
        Message m = _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "G8ZZZ"));
        _h.Store.EnqueueForwards(m.Number, ["GB7RDG-2"]);
        (_, ScriptedTerminal t) = await AsUser("queue", "quit");
        Assert.Contains("Sorry, that's a sysop-only command.\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("waiting to forward", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonSysop_Route_IsRefused()
    {
        AddPartner("GB7RDG-2", atCalls: ["*"]);
        (_, ScriptedTerminal t) = await AsUser("route g4abc", "quit");
        Assert.Contains("Sorry, that's a sysop-only command.\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("would go to", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonSysop_Help_DoesNotListSysopVerbs()
    {
        (_, ScriptedTerminal t) = await AsUser("help", "quit");
        Assert.DoesNotContain("forwarding -", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("route <call> -", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sysop_Help_ListsTheSysopVerbs()
    {
        (_, ScriptedTerminal t) = await AsSysop("help", "quit");
        Assert.Contains("forwarding - (sysop)", t.Output, StringComparison.Ordinal);
        Assert.Contains("route <call> - (sysop)", t.Output, StringComparison.Ordinal);
        Assert.Contains("queue - (sysop)", t.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- prefix/shortcut still resolve

    [Fact]
    public async Task Sysop_r_StillMeansRead_NotRoute()
    {
        // The documented muscle-memory shortcut survives the new `route` word: `r` is read.
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: SessionHarness.SysopCall, subject: "hi", body: "x\r"));
        (_, ScriptedTerminal t) = await AsSysop("r 1", "quit");
        Assert.Contains("Subject: hi\r", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("would go to", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("nowhere to go", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sysop_ro_ResolvesToRoute()
    {
        AddPartner("GB7RDG-2", atCalls: ["*"]);
        (_, ScriptedTerminal t) = await AsSysop("ro g4abc", "quit");
        Assert.Contains("would go to GB7RDG-2", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", t.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonSysop_forwar_StaysAnUnambiguousForward_NoSysopLeak()
    {
        // `forwar` is a prefix of both `forward` (user verb) and `forwarding` (sysop verb); a
        // non-sysop must NOT get "did you mean forward or forwarding" (that would leak the sysop
        // verb) — it resolves straight to the user `forward` verb.
        _h.Store.AddMessage(Drafts.Personal(from: "G4ABC", to: "M0LTE", subject: "hi", body: "x\r"));
        (_, ScriptedTerminal t) = await AsUser("forwar 1 to G8ZZZ", "quit");
        Assert.DoesNotContain("Did you mean", t.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("forwarding", t.Output, StringComparison.Ordinal);
        // It ran the user forward verb (a Fwd: copy was stored).
        Assert.Equal("Fwd:hi", _h.Store.GetMessage(2)!.Subject);
    }
}
