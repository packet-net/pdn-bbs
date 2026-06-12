namespace Bbs.Core.Tests;

/// <summary>
/// Routing decisions (compat spec §4.2), pinned by [BPQ-SRC MailRouting.c MatchMessagetoBBSList].
/// Our BBS: GB7PDN at #23.GBR.EURO.
/// </summary>
public sealed class RoutingEngineTests
{
    private static readonly RoutingEngine Engine = new("GB7PDN", "#23.GBR.EURO");

    private static Partner P(
        string call,
        string[]? to = null,
        string[]? at = null,
        string[]? hr = null,
        string[]? hrp = null,
        string? bbsHa = null)
        => new()
        {
            Call = call,
            ToCalls = to ?? [],
            AtCalls = at ?? [],
            HRoutes = hr ?? [],
            HRoutesP = hrp ?? [],
            BbsHa = bbsHa,
        };

    private static RoutingRequest Personal(string to, string? at, params string[] chain) => new()
    {
        Type = MessageType.Personal,
        ToCall = to,
        At = at,
        RouteChainCalls = chain,
    };

    private static RoutingRequest LocalUserPersonal(string to, string? at) => new()
    {
        Type = MessageType.Personal,
        ToCall = to,
        At = at,
        ToIsLocalUser = true,
    };

    // ------------------------------------------- local delivery beats forwarding (design.md #1)

    [Fact]
    public void LocalDelivery_NoAt_ToLocalUser_NeverLeaksToWildcardPartner()
    {
        // THE leak: a wildcard-AT default route (GB7RDG's @WW/* — already live on the lab)
        // must never swallow a local user's personal mail with no AT. design.md "The home-BBS
        // requirement" rule #1: this stays local — zero forward targets. Restores LinBPQ's own
        // "already here" local-first behaviour [BPQ-SRC CheckAndSend].
        var wildcard = P("GB7RDG", at: ["WW", "*"]);

        RoutingDecision decision = Engine.Route(LocalUserPersonal("M0LTE", null), [wildcard]);

        Assert.Empty(decision.Targets);
        Assert.False(decision.IsFlood);
    }

    [Fact]
    public void LocalDelivery_AtIsOurOwnCall_ZeroTargets()
    {
        // AT names our own callsign → for a local mailbox here, never forwarded.
        var wildcard = P("GB7RDG", at: ["*"]);
        RoutingDecision decision = Engine.Route(Personal("M0LTE", "GB7PDN"), [wildcard]);
        Assert.Empty(decision.Targets);
    }

    [Fact]
    public void LocalDelivery_AtUnderOurHa_ZeroTargets()
    {
        // AT is a full address under our own H-Route (GB7PDN.#23.GBR.EURO) → resolves to us.
        var wildcard = P("GB7RDG", at: ["*"]);
        RoutingDecision decision = Engine.Route(Personal("M0LTE", "GB7PDN.#23.GBR.EURO"), [wildcard]);
        Assert.Empty(decision.Targets);
    }

    [Fact]
    public void LocalDelivery_NoAt_NonLocalUser_StillRoutesToWildcard()
    {
        // The rule must not over-suppress: a no-AT personal whose TO is NOT a local user still
        // falls through to the wildcard default route, exactly as before.
        var wildcard = P("GB7RDG", at: ["*"]);
        RoutingDecision decision = Engine.Route(Personal("G8ABC", null), [wildcard]);
        Assert.Equal("GB7RDG", Assert.Single(decision.Targets).PartnerCall);
        Assert.Equal(RouteReason.WildcardAt, decision.Targets[0].Reason);
    }

    [Fact]
    public void LocalDelivery_ExplicitRemoteAt_StillForwards_EvenIfToIsLocalUser()
    {
        // Explicit AT naming a REMOTE bbs wins: even when TO happens to be a known local user,
        // the message is addressed elsewhere and must still forward there.
        var remote = P("GB7BSK", at: ["GB7BSK"]);

        RoutingDecision decision = Engine.Route(
            new RoutingRequest
            {
                Type = MessageType.Personal,
                ToCall = "M0LTE",
                At = "GB7BSK",
                ToIsLocalUser = true,
            },
            [remote]);

        RouteTarget target = Assert.Single(decision.Targets);
        Assert.Equal("GB7BSK", target.PartnerCall);
    }

    [Fact]
    public void LocalDelivery_DoesNotAffectBulletins()
    {
        // Regression guard: the pre-empt is P-only. A flood bulletin in our target area still
        // floods even though ToIsLocalUser is meaningless for it (and would be false anyway).
        var partner = P("GB7AAA", hr: ["WW"], bbsHa: "GB7AAA.#23.GBR.EURO");
        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Bulletin, ToCall = "ALL", At = "GBR.EU" },
            [partner]);
        Assert.True(decision.IsFlood);
        Assert.Single(decision.Targets);
    }

    [Fact]
    public void LocalDelivery_DoesNotAffectTraffic()
    {
        // Regression guard: NTS traffic routing is untouched by the personal local-first rule.
        var partner = P("W4NFL", to: ["321*"]);
        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Traffic, ToCall = "32118", At = "NTSFL" },
            [partner]);
        Assert.Equal("W4NFL", Assert.Single(decision.Targets).PartnerCall);
    }

    // ---------------------------------------------------------------- P: the single-copy chain

    [Fact]
    public void Personal_BestHrDepthWins_TheSpecExample()
    {
        // §4.2: "if you define BBS1 with HR EU and BBS2 with HR GBR.EU, a message for
        // G8BPQ@G8BPQ.#23.GBR.EU will be sent to BBS2 … no need for an exclusion rule."
        var bbs1 = P("GB7AAA", hrp: ["EU"]);
        var bbs2 = P("GB7BBB", hrp: ["GBR.EU"]);

        RoutingDecision decision = Engine.Route(Personal("G8BPQ", "G8BPQ.#23.GBR.EU"), [bbs1, bbs2]);

        RouteTarget target = Assert.Single(decision.Targets);
        Assert.Equal("GB7BBB", target.PartnerCall);
        Assert.Equal(RouteReason.Hierarchical, target.Reason);
        Assert.Equal(3, target.Depth);
        Assert.False(decision.IsFlood);
    }

    [Fact]
    public void Personal_HrDepthTie_PicksFirstInSuppliedOrder()
    {
        // Documented tie-break: LinBPQ keeps the first BBS in its chain on equal depth
        // (strict '>'); we keep the first partner in the supplied list. With store ordering
        // that is the lexicographically-lowest call.
        var first = P("GB7AAA", hrp: ["GBR.EU"]);
        var second = P("GB7BBB", hrp: ["GBR.EURO"]);

        RoutingDecision decision = Engine.Route(Personal("G8BPQ", "G8BPQ.#23.GBR.EU"), [first, second]);
        Assert.Equal("GB7AAA", Assert.Single(decision.Targets).PartnerCall);

        // Reversed supply order → the other one. The caller controls determinism via order.
        decision = Engine.Route(Personal("G8BPQ", "G8BPQ.#23.GBR.EU"), [second, first]);
        Assert.Equal("GB7BBB", Assert.Single(decision.Targets).PartnerCall);
    }

    [Fact]
    public void Personal_ToListMatch_BeatsEverythingElse()
    {
        var toPartner = P("GB7TO", to: ["G8BPQ"]);
        var hrPartner = P("GB7HR", hrp: ["#23.GBR.EU"]);

        RoutingDecision decision = Engine.Route(Personal("G8BPQ", "G8BPQ.#23.GBR.EU"), [hrPartner, toPartner]);

        RouteTarget target = Assert.Single(decision.Targets);
        Assert.Equal("GB7TO", target.PartnerCall);
        Assert.Equal(RouteReason.ToMatch, target.Reason);
    }

    [Fact]
    public void Personal_ImpliedAt_BareCallEqualsPartner()
    {
        // §4.1: "a message AT'd to a partner's own call always matches it" — implied AT beats
        // ATCalls and HR.
        var partner = P("GB7BPQ", hrp: []);
        var other = P("GB7OTH", at: ["GB7BPQ"]);

        RoutingDecision decision = Engine.Route(Personal("G8BPQ", "GB7BPQ"), [other, partner]);

        RouteTarget target = Assert.Single(decision.Targets);
        Assert.Equal("GB7BPQ", target.PartnerCall);
        Assert.Equal(RouteReason.ImpliedAt, target.Reason);
    }

    [Fact]
    public void Personal_ImpliedAt_MatchesPartnerWithSsid_AndFullHaLeaf()
    {
        // ATs never carry SSIDs (§1.5) — partner GB7BPQ-1 still matches @GB7BPQ; and the AT's
        // leaf element is what counts (ATBBS [BPQ-SRC]).
        var partner = P("GB7BPQ-1");
        RoutingDecision decision = Engine.Route(Personal("G8BPQ", "GB7BPQ.#23.GBR.EURO"), [partner]);
        Assert.Equal(RouteReason.ImpliedAt, Assert.Single(decision.Targets).Reason);
    }

    [Fact]
    public void Personal_AtListExactMatch_OnFirstElement()
    {
        var partner = P("GB7OTH", at: ["GB7XYZ"]);
        RoutingDecision decision = Engine.Route(Personal("G8BPQ", "GB7XYZ.GBR.EU"), [partner]);

        RouteTarget target = Assert.Single(decision.Targets);
        Assert.Equal(RouteReason.AtMatch, target.Reason);
    }

    [Fact]
    public void Personal_WildcardAt_IsTheLastResort_LongestPrefixWins()
    {
        var catchAll = P("GB7ALL", at: ["*"]);
        var gbOnly = P("GB7GB", at: ["GB*"]);

        RoutingDecision decision = Engine.Route(Personal("G8BPQ", "GB7XYZ.GBR.EU"), [catchAll, gbOnly]);
        RouteTarget target = Assert.Single(decision.Targets);
        Assert.Equal("GB7GB", target.PartnerCall);
        Assert.Equal(RouteReason.WildcardAt, target.Reason);

        // Bare * still catches a message with no AT at all.
        decision = Engine.Route(Personal("G8BPQ", null), [catchAll]);
        Assert.Equal("GB7ALL", Assert.Single(decision.Targets).PartnerCall);
    }

    [Fact]
    public void Personal_NoMatch_RoutesNowhere()
    {
        var partner = P("GB7AAA", hrp: ["FRA.EU"]);
        RoutingDecision decision = Engine.Route(Personal("G8BPQ", "G8BPQ.#23.GBR.EU"), [partner]);
        Assert.Empty(decision.Targets);
    }

    [Fact]
    public void Personal_NoAt_OnlyToListOrWildcardCanMatch()
    {
        var hrPartner = P("GB7HR", hrp: ["WW"]);
        Assert.Empty(Engine.Route(Personal("G8BPQ", null), [hrPartner]).Targets);

        var toPartner = P("GB7TO", to: ["G8BPQ"]);
        Assert.Equal("GB7TO", Assert.Single(Engine.Route(Personal("G8BPQ", null), [hrPartner, toPartner]).Targets).PartnerCall);
    }

    // ---------------------------------------------------------------- loop guards

    [Fact]
    public void LoopGuard_NeverRoutesBackToRouteChainPartner()
    {
        // Task spec: never route to a partner whose call appears in the message's R: chain.
        var partner = P("GB7BPQ-1", hrp: ["WW"]);

        RoutingDecision decision = Engine.Route(
            Personal("G8BPQ", "G8BPQ.#23.GBR.EU", "GB7BPQ", "GB7XXX"),
            [partner]);

        Assert.Empty(decision.Targets);
    }

    [Fact]
    public void LoopGuard_NeverRoutesBackToReceivedFromOrBidDirection()
    {
        var partner = P("GB7BPQ", hrp: ["WW"]);

        RoutingDecision viaReceived = Engine.Route(
            Personal("G8BPQ", "G8BPQ.#23.GBR.EU") with { ReceivedFrom = "GB7BPQ-1" },
            [partner]);
        Assert.Empty(viaReceived.Targets);

        RoutingDecision viaBid = Engine.Route(
            Personal("G8BPQ", "G8BPQ.#23.GBR.EU") with { BidSeenFrom = "GB7BPQ" },
            [partner]);
        Assert.Empty(viaBid.Targets);
    }

    [Fact]
    public void LoopGuard_NeverRoutesToSelf()
    {
        // §4.2: "never to self (unless ForwardToMe)" — ForwardToMe not implemented.
        var self = P("GB7PDN", hrp: ["WW"]);
        var other = P("GB7AAA", hrp: ["WW"]);

        RoutingDecision decision = Engine.Route(Personal("G8BPQ", "G8BPQ.#23.GBR.EU"), [self, other]);
        Assert.Equal("GB7AAA", Assert.Single(decision.Targets).PartnerCall);
    }

    [Fact]
    public void DisabledPartner_StillQueues()
    {
        // Enabled gates the Host dialler, not routing — matches LinBPQ (CheckAndSend never
        // consults Enabled; mail accumulates for a paused partner).
        Partner disabled = P("GB7AAA", hrp: ["WW"]) with { Enabled = false };
        RoutingDecision decision = Engine.Route(Personal("G8BPQ", "G8BPQ.#23.GBR.EU"), [disabled]);
        Assert.Single(decision.Targets);
    }

    // ---------------------------------------------------------------- bulletins

    [Fact]
    public void Bulletin_InTargetArea_FloodsToEveryMatchingPartner()
    {
        // @GBR.EU and we are in GBR.EURO → flood. Both partners are in-area with matching HRoutes.
        var p1 = P("GB7AAA", hr: ["WW"], bbsHa: "GB7AAA.#23.GBR.EURO");
        var p2 = P("GB7BBB", hr: ["GBR.EU"], bbsHa: "GB7BBB.#41.GBR.EURO");

        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Bulletin, ToCall = "ALL", At = "GBR.EU" },
            [p1, p2]);

        Assert.True(decision.IsFlood);
        Assert.Equal(["GB7AAA", "GB7BBB"], decision.Targets.Select(t => t.PartnerCall).Order());
    }

    [Fact]
    public void Bulletin_Flood_HRouteMustMatchAllItsElements()
    {
        // §4.2: "GBR.EU wouldn't get @EU or @WW messages" — the HRoutes entry must be fully
        // inside the message area.
        var partner = P("GB7AAA", hr: ["GBR.EU"], bbsHa: "GB7AAA.#23.GBR.EURO");

        RoutingDecision atEu = Engine.Route(
            new RoutingRequest { Type = MessageType.Bulletin, ToCall = "ALL", At = "EU" }, [partner]);
        Assert.True(atEu.IsFlood);
        Assert.Empty(atEu.Targets);

        RoutingDecision atGbrEu = Engine.Route(
            new RoutingRequest { Type = MessageType.Bulletin, ToCall = "ALL", At = "GBR.EU" }, [partner]);
        Assert.Single(atGbrEu.Targets);
    }

    [Fact]
    public void Bulletin_Flood_PartnerOutsideTargetArea_NotSent()
    {
        // In-area test: the partner's BBSHA must lie under the message AT.
        var ukPartner = P("GB7AAA", hr: ["WW"], bbsHa: "GB7AAA.#23.GBR.EURO");
        var usPartner = P("W4ABC", hr: ["WW"], bbsHa: "W4ABC.#NFL.FL.USA.NOAM");

        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Bulletin, ToCall = "ALL", At = "GBR.EU" },
            [ukPartner, usPartner]);

        Assert.Equal("GB7AAA", Assert.Single(decision.Targets).PartnerCall);
    }

    [Fact]
    public void Bulletin_Flood_PartnerWithoutBbsHa_NeverFloodMatched()
    {
        // [BPQ-SRC CheckBBSHElementsFlood]: "Not safe to flood".
        var partner = P("GB7AAA", hr: ["WW"], bbsHa: null);
        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Bulletin, ToCall = "ALL", At = "GBR.EU" }, [partner]);
        Assert.Empty(decision.Targets);
    }

    [Fact]
    public void Bulletin_NotYetInTargetArea_IsDirected_SingleCopyViaHRoutesP()
    {
        // @USA.NA from GBR: not our area → directed bull, routed like a personal on HRoutesP.
        var us1 = P("W4ABC", hrp: ["NA"]);
        var us2 = P("K4CJX", hrp: ["USA.NA"]);

        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Bulletin, ToCall = "ALL", At = "USA.NA" },
            [us1, us2]);

        Assert.False(decision.IsFlood);
        RouteTarget target = Assert.Single(decision.Targets);
        Assert.Equal("K4CJX", target.PartnerCall); // best depth, single copy
    }

    [Fact]
    public void Bulletin_UnconvertibleAt_TreatedAsFlood()
    {
        // §4.2: "Unconvertible addresses are treated as flood" — matched via ATCalls here.
        var partner = P("GB7AAA", at: ["PACKET"], bbsHa: "GB7AAA.#23.GBR.EURO");

        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Bulletin, ToCall = "ALL", At = "PACKET" },
            [partner]);

        Assert.True(decision.IsFlood);
        Assert.Equal(RouteReason.AtMatch, Assert.Single(decision.Targets).Reason);
    }

    [Fact]
    public void Bulletin_Flood_FallsBackToWildcardAt_WhenNothingMatched()
    {
        var partner = P("GB7AAA", at: ["*"]);
        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Bulletin, ToCall = "ALL", At = "GBR.EU" }, [partner]);
        Assert.Equal(RouteReason.WildcardAt, Assert.Single(decision.Targets).Reason);
    }

    [Fact]
    public void Bulletin_Flood_NeverDuplicatesToOnePartner()
    {
        // A partner matching both TO and HR gets exactly one queue entry.
        var partner = P("GB7AAA", to: ["ALL"], hr: ["WW"], bbsHa: "GB7AAA.#23.GBR.EURO");
        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Bulletin, ToCall = "ALL", At = "GBR.EU" }, [partner]);
        Assert.Single(decision.Targets);
    }

    // ---------------------------------------------------------------- NTS traffic

    [Fact]
    public void Traffic_LongestWildcardedToPrefixWins()
    {
        // §4.2 NTS: longest TO-prefix wildcard match wins.
        var coarse = P("W4ALL", to: ["3*"]);
        var fine = P("W4NFL", to: ["321*"]);

        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Traffic, ToCall = "32118", At = "NTSFL" },
            [coarse, fine]);

        RouteTarget target = Assert.Single(decision.Targets);
        Assert.Equal("W4NFL", target.PartnerCall);
        Assert.Equal(RouteReason.ToMatch, target.Reason);
    }

    [Fact]
    public void Traffic_ExactToEntry_CountsFullLength()
    {
        var wildcard = P("W4ALL", to: ["3211*"]);
        var exact = P("W4XCT", to: ["32118"]);

        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Traffic, ToCall = "32118" },
            [wildcard, exact]);

        Assert.Equal("W4XCT", Assert.Single(decision.Targets).PartnerCall);
    }

    [Fact]
    public void Traffic_NeverPrefixExcludesPartner()
    {
        // §4.1 TOCalls: "!/- prefix = never" — a matching exclusion knocks the partner out.
        var excluded = P("W4BAD", to: ["!321*", "3*"]);
        var fallback = P("W4OK", to: ["3*"]);

        RoutingDecision decision = Engine.Route(
            new RoutingRequest { Type = MessageType.Traffic, ToCall = "32118" },
            [excluded, fallback]);

        Assert.Equal("W4OK", Assert.Single(decision.Targets).PartnerCall);
    }

    [Fact]
    public void Traffic_RoutesOnAtOnlyWhenNoToMatch()
    {
        // §4.2 [BPQ-DOC NTSFacilities]: "It will only route on the AT field if there are no
        // matches on TO".
        var atPartner = P("W4AT", at: ["NTSFL"]);
        var toPartner = P("W4TO", to: ["321*"]);

        RoutingDecision withTo = Engine.Route(
            new RoutingRequest { Type = MessageType.Traffic, ToCall = "32118", At = "NTSFL" },
            [atPartner, toPartner]);
        Assert.Equal("W4TO", Assert.Single(withTo.Targets).PartnerCall);

        RoutingDecision withoutTo = Engine.Route(
            new RoutingRequest { Type = MessageType.Traffic, ToCall = "99999", At = "NTSFL" },
            [atPartner, toPartner]);
        Assert.Equal("W4AT", Assert.Single(withoutTo.Targets).PartnerCall);
    }
}
