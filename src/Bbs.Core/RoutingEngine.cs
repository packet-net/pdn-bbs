namespace Bbs.Core;

/// <summary>Which rule selected a partner (mirrors LinBPQ's "Routing Trace" lines).</summary>
public enum RouteReason
{
    /// <summary>TO-list match (compat spec §4.2 "first TO-list match").</summary>
    ToMatch,

    /// <summary>Implied AT — the message AT is the partner's own call (compat spec §4.1).</summary>
    ImpliedAt,

    /// <summary>Exact ATCalls match (compat spec §4.2).</summary>
    AtMatch,

    /// <summary>Hierarchical-route match; <see cref="RouteTarget.Depth"/> is the matched element count.</summary>
    Hierarchical,

    /// <summary>Wildcarded ATCalls match — the last-resort default route [BPQ-SRC CheckBBSATListWildCarded].</summary>
    WildcardAt,

    /// <summary>
    /// Local delivery — the message resolves to a mailbox on this BBS, so it is never forwarded
    /// ("already here" [BPQ-SRC CheckAndSend]). A <see cref="RouteReason.Local"/> decision carries
    /// <b>no</b> <see cref="RouteTarget"/>s; see <see cref="RoutingDecision.IsLocal"/>.
    /// </summary>
    Local,
}

/// <summary>One partner queue selected for a message.</summary>
/// <param name="PartnerCall">The partner's configured call (exact, including SSID).</param>
/// <param name="Reason">The rule that matched.</param>
/// <param name="Depth">
/// Matched element/prefix count for <see cref="RouteReason.Hierarchical"/> and
/// <see cref="RouteReason.WildcardAt"/>; 0 otherwise.
/// </param>
public sealed record RouteTarget(string PartnerCall, RouteReason Reason, int Depth);

/// <summary>Routing input: the addressing of one message copy (route per recipient for multi-recipient messages).</summary>
public sealed record RoutingRequest
{
    /// <summary>P/B/T (compat spec §2.1).</summary>
    public required MessageType Type { get; init; }

    /// <summary>The TO callsign.</summary>
    public required string ToCall { get; init; }

    /// <summary>The AT (@BBS) designator, or null/empty.</summary>
    public string? At { get; init; }

    /// <summary>
    /// Partner this copy arrived from, or null if local. Loop guard: never route back to the
    /// partner it came from (compat spec §4.2 [BPQ-SRC CheckAndSend]).
    /// </summary>
    public string? ReceivedFrom { get; init; }

    /// <summary>
    /// Partner the message's BID was first seen from per the dedup store
    /// (<see cref="BidRecord.FirstSeenFrom"/>), or null. Loop guard: never route a BID back in
    /// the direction the dedup store saw it arrive from.
    /// </summary>
    public string? BidSeenFrom { get; init; }

    /// <summary>
    /// BBS calls extracted from the message's R: lines (the Fbb layer owns the R:-line codec).
    /// Loop guard: never route to a partner whose call appears in the R: chain (compat spec
    /// §3.14 loop prevention, applied here on the send side).
    /// </summary>
    public IReadOnlyList<string> RouteChainCalls { get; init; } = [];

    /// <summary>
    /// True when <see cref="ToCall"/> is a known local user of this BBS (the host layer answers
    /// this from the user store). Used by the "local delivery beats forwarding" pre-empt: a
    /// personal with no AT addressed to a local user stays here rather than matching a partner's
    /// wildcard-AT default route (design.md "The home-BBS requirement" rule #1). Default false.
    /// </summary>
    public bool ToIsLocalUser { get; init; }
}

/// <summary>The per-partner queues a message should join.</summary>
public sealed record RoutingDecision
{
    /// <summary>
    /// Selected partners. Exactly zero or one entry for P, T and directed bulletins (the
    /// single-copy rule, compat spec §4.2); zero or more for flood bulletins.
    /// </summary>
    public required IReadOnlyList<RouteTarget> Targets { get; init; }

    /// <summary>True when the message was routed as a flood bulletin (compat spec §4.2).</summary>
    public required bool IsFlood { get; init; }

    /// <summary>
    /// True when the message resolves to a local mailbox here and so was deliberately given
    /// <b>zero</b> forward targets ("already here" [BPQ-SRC CheckAndSend]; design.md rule #1).
    /// Distinguishes an intended local delivery from a no-partner-matched miss — both have empty
    /// <see cref="Targets"/>, but only this one is a positive "stays here" decision.
    /// </summary>
    public bool IsLocal { get; init; }
}

/// <summary>
/// Decides which partner queues a message joins. Faithful port of
/// [BPQ-SRC MailRouting.c MatchMessagetoBBSList] per compat spec §4.2:
///
/// <list type="bullet">
/// <item><b>P + directed bulletins → exactly one partner</b>: first TO-list match, else
/// implied-AT, else exact AT-list, else the partner with the most matching HRoutesP
/// elements, else wildcard AT. Never duplicated.</item>
/// <item><b>Flood bulletins → every partner</b> that matches TO/AT, or that is inside the
/// message's target area (its BBSHA under the message AT) and has an HRoutes entry fully
/// matching the AT root-first; wildcard AT as a collective fallback when nothing matched.</item>
/// <item><b>T (NTS)</b>: longest wildcarded-TO match wins; AT only if no TO match; then
/// wildcard AT. No implied-AT or HR matching for T, mirroring the source.</item>
/// </list>
///
/// Tie-breaking: where LinBPQ keeps the first BBS in its chain on equal depth (strict
/// <c>&gt;</c> comparisons), we keep the <b>first partner in the supplied list</b>.
/// <see cref="BbsStore.ListPartners"/> returns partners ordered by callsign, so ties resolve
/// deterministically to the lexicographically-lowest call.
///
/// Named deferrals (compat spec §4.2 features not implemented): SendPtoMultiple, ForwardToMe,
/// FWDAliases/NTS alias files, <c>to@route!BBSCALL</c> source ("bang") routing, RMS/AMPR/
/// winlink.org special destinations, FWDPersonalsOnly.
/// </summary>
public sealed class RoutingEngine
{
    private readonly string _ownCall;
    private readonly HierarchicalAddress _ownHa;

    /// <summary>
    /// Creates a routing engine for this BBS.
    /// </summary>
    /// <param name="ownCall">Our BBS callsign (never route to self — compat spec §4.2).</param>
    /// <param name="hierarchicalRoute">
    /// Our H-Route <b>without</b> the callsign, e.g. <c>#23.GBR.EURO</c> (linmail.cfg H-Route
    /// shape). The own call is appended as the leaf element, mirroring [BPQ-SRC SetupMyHA].
    /// </param>
    public RoutingEngine(string ownCall, string? hierarchicalRoute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownCall);

        _ownCall = Callsigns.Normalize(ownCall);

        // MyElements = [WW] + split(H-Route) + own call as leaf [BPQ-SRC SetupMyHA].
        string route = (hierarchicalRoute ?? "").Trim();
        _ownHa = HierarchicalAddress.ParseRoutePattern(route.Length == 0 ? _ownCall : _ownCall + "." + route);
    }

    /// <summary>Routes one message copy against the supplied partner list (order-significant for ties).</summary>
    public RoutingDecision Route(RoutingRequest request, IReadOnlyList<Partner> partners)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(partners);

        string to = Callsigns.NormalizeAddressee(request.ToCall);
        HierarchicalAddress at = HierarchicalAddress.Parse(request.At);
        string atBbs = at.AtBbs;

        // Local delivery beats forwarding (design.md "The home-BBS requirement" rule #1). This
        // restores LinBPQ's own local-first behaviour — "Don't forward to ourself - already
        // here!" [BPQ-SRC CheckAndSend] — which the faithful single-copy/flood port otherwise
        // lacks once a wildcard-AT partner is configured. A personal whose AT resolves to us, or
        // whose TO is a known local user when there is no AT, is for a mailbox on this BBS: it
        // stays here with zero forward targets, never matching any partner's default route. The
        // message itself is already stored against its recipient; this only suppresses the
        // forward fan-out, so the dangerous silent leak (a wildcard default swallowing local
        // mail) cannot happen.
        if (request.Type == MessageType.Personal && ResolvesToLocal(at, atBbs, request.ToIsLocalUser))
        {
            return new RoutingDecision { Targets = [], IsFlood = false, IsLocal = true };
        }

        List<Partner> eligible = [];
        foreach (Partner partner in partners)
        {
            if (IsEligible(partner, request))
            {
                eligible.Add(partner);
            }
        }

        // Flood determination [BPQ-SRC MatchMessagetoBBSList]: a bulletin floods when it has
        // no AT, an unconvertible AT, or has reached its target area (our own HA lies under
        // the message AT). Otherwise it is a "directed bull", routed like a personal.
        bool flood = request.Type == MessageType.Bulletin && (!at.IsWwRooted || at.AreaContains(_ownHa));

        if (request.Type == MessageType.Traffic)
        {
            return RouteTraffic(to, atBbs, eligible);
        }

        if (!flood)
        {
            RouteTarget? single = RouteSingleCopy(to, at, atBbs, eligible);
            return new RoutingDecision { Targets = single is null ? [] : [single], IsFlood = false };
        }

        return RouteFlood(to, at, atBbs, eligible);
    }

    /// <summary>
    /// True when a personal resolves to a mailbox on this BBS (design.md rule #1). Two cases,
    /// mirroring the flood test's <c>at.AreaContains(_ownHa)</c> / <see cref="HierarchicalAddress.IsWwRooted"/>
    /// style:
    /// <list type="number">
    /// <item>The AT names us — its leaf element is our own call (same base-call comparison as
    /// implied-AT, <see cref="ImpliedAtMatches"/>), or it is a WW-rooted address sitting under
    /// our own HA (<c>_ownHa.AreaContains(at)</c>: all of our HA elements, including our call,
    /// match the AT root-first).</item>
    /// <item>There is no AT and the TO is a known local user — the caller resolved that against
    /// the user store. This is the case a wildcard-AT partner would otherwise swallow.</item>
    /// </list>
    /// </summary>
    private bool ResolvesToLocal(HierarchicalAddress at, string atBbs, bool toIsLocalUser)
    {
        if (atBbs.Length == 0)
        {
            // No AT: stays here only when the recipient is one of our own users.
            return toIsLocalUser;
        }

        return Callsigns.BaseEquals(atBbs, _ownCall)
            || (at.IsWwRooted && _ownHa.AreaContains(at));
    }

    private bool IsEligible(Partner partner, RoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(partner);

        // Never to self ("Don't forward to ourself - already here!" [BPQ-SRC CheckAndSend];
        // ForwardToMe not implemented).
        if (Callsigns.BaseEquals(partner.Call, _ownCall))
        {
            return false;
        }

        // Never back to the partner it came from (compat spec §4.2).
        if (request.ReceivedFrom is not null && Callsigns.BaseEquals(partner.Call, request.ReceivedFrom))
        {
            return false;
        }

        // Never a BID back in the direction the dedup store first saw it from.
        if (request.BidSeenFrom is not null && Callsigns.BaseEquals(partner.Call, request.BidSeenFrom))
        {
            return false;
        }

        // Never to a partner already in the R: chain (compat spec §3.14 loop prevention).
        foreach (string hop in request.RouteChainCalls)
        {
            if (Callsigns.BaseEquals(partner.Call, hop))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The P / directed-bulletin single-copy chain (compat spec §4.2).</summary>
    private static RouteTarget? RouteSingleCopy(string to, HierarchicalAddress at, string atBbs, List<Partner> eligible)
    {
        // 1. First TO-list match (exact).
        foreach (Partner partner in eligible)
        {
            if (ToListMatches(partner, to))
            {
                return new RouteTarget(partner.Call, RouteReason.ToMatch, 0);
            }
        }

        // 2. Implied AT: the message AT is the partner's own call (compat spec §4.1).
        foreach (Partner partner in eligible)
        {
            if (ImpliedAtMatches(partner, atBbs))
            {
                return new RouteTarget(partner.Call, RouteReason.ImpliedAt, 0);
            }
        }

        // 3. Exact AT-list match.
        foreach (Partner partner in eligible)
        {
            if (AtListMatches(partner, atBbs))
            {
                return new RouteTarget(partner.Call, RouteReason.AtMatch, 0);
            }
        }

        // 4. Best HRoutesP depth — the single-copy best-match rule (compat spec §4.2: HR GBR.EU
        //    beats HR EU; "There is no need for an exclusion rule"). Strict '>' keeps the first
        //    partner on ties, like LinBPQ's chain scan.
        Partner? best = null;
        int bestDepth = 0;
        foreach (Partner partner in eligible)
        {
            int depth = BestPatternDepth(partner.HRoutesP, at);
            if (depth > bestDepth)
            {
                bestDepth = depth;
                best = partner;
            }
        }

        if (best is not null)
        {
            return new RouteTarget(best.Call, RouteReason.Hierarchical, bestDepth);
        }

        // 5. Wildcarded AT — default route of last resort.
        return BestWildcardAt(eligible, atBbs);
    }

    /// <summary>The NTS T chain [BPQ-SRC MatchMessagetoBBSList 'T' path] (compat spec §4.2).</summary>
    private static RoutingDecision RouteTraffic(string to, string atBbs, List<Partner> eligible)
    {
        // 1. Longest wildcarded-TO match across all partners ("It will only route on the AT
        //    field if there are no matches on TO" — compat spec §4.2 [BPQ-DOC NTSFacilities]).
        Partner? best = null;
        int bestLen = -1;
        foreach (Partner partner in eligible)
        {
            int len = NtsToMatchLength(partner.ToCalls, to);
            if (len > bestLen)
            {
                bestLen = len;
                best = partner;
            }
        }

        if (best is not null && bestLen >= 0)
        {
            return new RoutingDecision { Targets = [new RouteTarget(best.Call, RouteReason.ToMatch, bestLen)], IsFlood = false };
        }

        // 2. Exact AT-list match (first wins). Note: no implied-AT and no HR matching for T,
        //    mirroring the source.
        foreach (Partner partner in eligible)
        {
            if (AtListMatches(partner, atBbs))
            {
                return new RoutingDecision { Targets = [new RouteTarget(partner.Call, RouteReason.AtMatch, 0)], IsFlood = false };
            }
        }

        // 3. Wildcarded AT.
        RouteTarget? wildcard = BestWildcardAt(eligible, atBbs);
        return new RoutingDecision { Targets = wildcard is null ? [] : [wildcard], IsFlood = false };
    }

    /// <summary>Flood-bulletin fan-out (compat spec §4.2) [BPQ-SRC MatchMessagetoBBSList flood loop].</summary>
    private static RoutingDecision RouteFlood(string to, HierarchicalAddress at, string atBbs, List<Partner> eligible)
    {
        List<RouteTarget> targets = [];

        foreach (Partner partner in eligible)
        {
            if (ToListMatches(partner, to))
            {
                targets.Add(new RouteTarget(partner.Call, RouteReason.ToMatch, 0));
                continue;
            }

            if (ImpliedAtMatches(partner, atBbs) || AtListMatches(partner, atBbs))
            {
                targets.Add(new RouteTarget(partner.Call, RouteReason.AtMatch, 0));
                continue;
            }

            int depth = FloodHierarchicalDepth(partner, at);
            if (depth > 0)
            {
                targets.Add(new RouteTarget(partner.Call, RouteReason.Hierarchical, depth));
            }
        }

        // Nothing matched → wildcard AT, single best (the source jumps to CheckWildCardedAT).
        if (targets.Count == 0)
        {
            RouteTarget? wildcard = BestWildcardAt(eligible, atBbs);
            if (wildcard is not null)
            {
                targets.Add(wildcard);
            }
        }

        return new RoutingDecision { Targets = targets, IsFlood = true };
    }

    /// <summary>Exact TO-list match for P/B [BPQ-SRC CheckBBSToList — plain strcmp, no wildcards].</summary>
    private static bool ToListMatches(Partner partner, string to)
    {
        foreach (string entry in partner.ToCalls)
        {
            if (string.Equals(entry.Trim(), to, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Implied AT (compat spec §4.1): the AT's leaf element equals the partner's call. ATs never
    /// carry SSIDs (§1.5), so the partner's base call is also accepted (documented judgment —
    /// LinBPQ compares the configured call verbatim).
    /// </summary>
    private static bool ImpliedAtMatches(Partner partner, string atBbs)
    {
        if (atBbs.Length == 0)
        {
            return false;
        }

        return string.Equals(atBbs, partner.Call.Trim(), StringComparison.OrdinalIgnoreCase)
            || Callsigns.BaseEquals(atBbs, partner.Call);
    }

    /// <summary>Exact ATCalls match against the AT's first element [BPQ-SRC CheckBBSAtList].</summary>
    private static bool AtListMatches(Partner partner, string atBbs)
    {
        if (atBbs.Length == 0)
        {
            return false;
        }

        foreach (string entry in partner.AtCalls)
        {
            if (string.Equals(entry.Trim(), atBbs, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Best match depth of <paramref name="at"/> across a pattern list [BPQ-SRC CheckBBSHElements].</summary>
    private static int BestPatternDepth(IReadOnlyList<string> patterns, HierarchicalAddress at)
    {
        int best = 0;
        foreach (string pattern in patterns)
        {
            int depth = at.MatchDepth(HierarchicalAddress.ParseRoutePattern(pattern));
            if (depth > best)
            {
                best = depth;
            }
        }

        return best;
    }

    /// <summary>
    /// Flood HR match [BPQ-SRC CheckBBSHElementsFlood]: the partner must be inside the message's
    /// target area (its BBSHA under the message AT), and an HRoutes entry must fully match the
    /// AT root-first ("eg GBR.EU wouldn't get @EU or @WW messages" — compat spec §4.2).
    /// </summary>
    private static int FloodHierarchicalDepth(Partner partner, HierarchicalAddress at)
    {
        if (string.IsNullOrWhiteSpace(partner.BbsHa))
        {
            return 0; // "Not safe to flood"
        }

        HierarchicalAddress bbsHa = HierarchicalAddress.ParseRoutePattern(partner.BbsHa);
        if (!at.AreaContains(bbsHa))
        {
            return 0; // "Message is not for BBS's area"
        }

        return BestPatternDepth(partner.HRoutes, at);
    }

    /// <summary>
    /// Longest wildcarded-TO match for NTS [BPQ-SRC CheckBBSToForNTS]: <c>*</c> wildcards match
    /// by literal prefix, exact entries count their full length, and a matching <c>!</c>/<c>-</c>
    /// entry excludes the partner outright. Returns -1 for no match / excluded.
    /// </summary>
    private static int NtsToMatchLength(IReadOnlyList<string> entries, string to)
    {
        int best = -1;
        foreach (string raw in entries)
        {
            string entry = raw.Trim();
            bool invert = false;

            if (entry.StartsWith('!') || entry.StartsWith('-'))
            {
                invert = true;
                entry = entry[1..];
            }

            int star = entry.IndexOf('*', StringComparison.Ordinal);
            if (star >= 0)
            {
                string prefix = entry[..star];
                if (to.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (invert)
                    {
                        return -1;
                    }

                    if (prefix.Length > best)
                    {
                        best = prefix.Length;
                    }
                }
            }
            else if (string.Equals(to, entry, StringComparison.OrdinalIgnoreCase))
            {
                if (invert)
                {
                    return -1;
                }

                if (entry.Length > best)
                {
                    best = entry.Length;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Best wildcarded-AT match across partners [BPQ-SRC CheckBBSATListWildCarded]: only entries
    /// containing <c>*</c> participate (the full AT was already tried); longest literal prefix
    /// wins; a bare <c>*</c> matches everything (including an empty AT) at depth 0.
    /// </summary>
    private static RouteTarget? BestWildcardAt(List<Partner> eligible, string atBbs)
    {
        Partner? best = null;
        int bestLen = -1;

        foreach (Partner partner in eligible)
        {
            foreach (string raw in partner.AtCalls)
            {
                string entry = raw.Trim();
                int star = entry.IndexOf('*', StringComparison.Ordinal);
                if (star < 0)
                {
                    continue;
                }

                string prefix = entry[..star];
                if (atBbs.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length > bestLen)
                {
                    bestLen = prefix.Length;
                    best = partner;
                }
            }
        }

        return best is null ? null : new RouteTarget(best.Call, RouteReason.WildcardAt, bestLen);
    }
}
