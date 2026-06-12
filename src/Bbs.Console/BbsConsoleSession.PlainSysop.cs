using System.Globalization;
using Bbs.Core;

namespace Bbs.Console;

/// <summary>
/// The plain-language sysop diagnostics (forwarding.md F-2 ops layer, "the jargon goes away"
/// vocabulary table §58 and the route-explain phrasing §77). All read-only: <c>forwarding</c>
/// (per-partner status), <c>queue</c> (the pending forward queue) and <c>route &lt;call&gt;</c>
/// (the routing-explain trace). Built jargon-free from the start — these are PLAIN-surface
/// words only (the classic surface stays byte-exact), gated to sysops at dispatch.
///
/// Named deferrals (forwarding.md "Out of scope"): the forwarding <b>action</b> verbs
/// (<c>forward &lt;partner&gt; now</c> / re-route) trigger real dials and belong to a later
/// slice with the scheduler nudge; and the live per-partner <b>health</b> (last cycle / last
/// failure + reason / consecutive failures / next retry — wart 3) is not surfaced because the
/// <c>ForwardingScheduler</c> keeps that state in a private loop variable, not anywhere a
/// read-only session can query cheaply — so <c>forwarding</c> shows the status that IS
/// available (enabled/disabled, dial cadence, queue depth) and names the rest as deferred.
/// </summary>
public sealed partial class BbsConsoleSession
{
    // ---------------------------------------------------------------- forwarding (per-partner status)

    /// <summary>
    /// <c>forwarding</c> — per-partner status in sentences (vocabulary §58: plain words, no
    /// HR/flood jargon): each enabled/disabled partner, its dial cadence, and how many messages
    /// are waiting for it right now (<see cref="BbsStore.GetForwardQueue"/> count). Paged.
    /// </summary>
    private async Task HandlePlainForwardingAsync()
    {
        IReadOnlyList<Partner> partners = _store.ListPartners();
        if (partners.Count == 0)
        {
            await WritePlainLineAsync("There are no forwarding partners set up.").ConfigureAwait(false);
            return;
        }

        var lines = new List<string> { Inv($"You have {Plural(partners.Count, "forwarding partner")}:") };
        foreach (Partner partner in partners)
        {
            int waiting = _store.GetForwardQueue(partner.Call).Count;
            string state = partner.Enabled ? "dialled" : "not dialled (turned off)";
            string cadence = DescribeCadence(partner);
            string waitingText = waiting == 0
                ? "nothing waiting"
                : Inv($"{Plural(waiting, "message")} waiting");
            lines.Add(Inv($"  {partner.Call} - {state}, {cadence}; {waitingText}."));
        }

        // Live health (last cycle / last failure + reason / next retry — forwarding.md wart 3) is
        // a named deferral: the scheduler doesn't expose it for a read-only session to query yet.
        lines.Add("(Last-cycle and failure detail isn't available yet - that's coming later.)");
        await WritePlainPagedAsync(lines).ConfigureAwait(false);
    }

    /// <summary>The dial cadence in plain words ("dials as soon as mail arrives" / "tries every N minutes").</summary>
    private static string DescribeCadence(Partner partner)
    {
        if (partner.ForwardNewImmediately)
        {
            return "dials as soon as mail arrives";
        }

        int minutes = Math.Max(1, partner.ForwardIntervalSeconds) / 60;
        return minutes <= 1
            ? "tries about once a minute"
            : Inv($"tries every {minutes} minutes");
    }

    // ---------------------------------------------------------------- queue (the pending forward queue)

    /// <summary>
    /// <c>queue</c> — the forward queue in sentences: which messages are waiting, to which partner
    /// (number, from, subject, partner). Empty → "Nothing is waiting to forward." Paged with the
    /// plain <c>more? (yes/no)</c> pager.
    /// </summary>
    private async Task HandlePlainQueueAsync()
    {
        IReadOnlyList<Partner> partners = _store.ListPartners();
        var lines = new List<string>();
        int total = 0;

        foreach (Partner partner in partners)
        {
            IReadOnlyList<Message> waiting = _store.GetForwardQueue(partner.Call);
            foreach (Message message in waiting)
            {
                total++;
                string subject = TrimForPaclen(message.Subject, 40);
                lines.Add(Inv($"  {message.Number}) from {message.From}: {subject} - waiting for {partner.Call}"));
            }
        }

        if (total == 0)
        {
            await WritePlainLineAsync("Nothing is waiting to forward.").ConfigureAwait(false);
            return;
        }

        lines.Insert(0, Inv($"{Plural(total, "message")} waiting to forward:"));
        await WritePlainPagedAsync(lines).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- route (the explain trace)

    /// <summary>
    /// <c>route &lt;call&gt;</c> (also <c>route &lt;call&gt;@&lt;region&gt;</c>) — the routing-explain
    /// trace (forwarding.md wart 2): builds a hypothetical <b>personal</b> to the address, asks the
    /// same <see cref="RoutingEngine"/> the live host uses where it would go, and renders the
    /// decision in plain words (§77) — never "depth 3 HR match".
    /// </summary>
    private async Task HandlePlainRouteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            await WritePlainLineAsync(
                "Which station? Try route and a callsign, like route g4abc.").ConfigureAwait(false);
            return;
        }

        (string call, string? at) = SplitCallAndAt(args);
        if (call.Length == 0)
        {
            await WritePlainLineAsync(
                "I didn't catch a callsign. Try route and a callsign, like route g4abc.").ConfigureAwait(false);
            return;
        }

        string to = Callsigns.NormalizeAddressee(call);
        string? region = string.IsNullOrWhiteSpace(at) ? null : at.ToUpperInvariant();

        var request = new RoutingRequest
        {
            Type = MessageType.Personal,
            ToCall = to,
            At = region,
            // Same local-delivery signal the live RoutingService feeds (design.md rule #1): a
            // personal with no region addressed to one of our own users stays here.
            ToIsLocalUser = _store.UserExists(to),
        };

        RoutingDecision decision = RouteExplainEngine().Route(request, _store.ListPartners());
        await WritePlainLineAsync(ExplainRoute(to, region, decision)).ConfigureAwait(false);
    }

    /// <summary>
    /// The <see cref="RoutingEngine"/> for the <c>route</c> explain — our own call (the BBS
    /// callsign) plus the configured H-Route, exactly as <c>HostComposition</c> builds the live
    /// engine. Built per call (the explain is rare and read-only; no need to cache).
    /// </summary>
    private RoutingEngine RouteExplainEngine() => new(_config.BbsCallsign, _config.HRoute);

    /// <summary>
    /// Renders a <see cref="RoutingDecision"/> for a personal as one plain sentence (forwarding.md
    /// §77), mapping each <see cref="RouteReason"/> to plain words and never leaking depth/HR jargon:
    /// <list type="bullet">
    /// <item><b>Local</b> → "would stay here — &lt;call&gt; is one of our users."</item>
    /// <item><b>Hierarchical</b> → "would go to &lt;partner&gt; — it carries mail for stations in
    /// region &lt;region&gt; (closest match)."</item>
    /// <item><b>WildcardAt</b> → "would go to &lt;partner&gt; — it's our catch-all route for mail
    /// with nowhere more specific to go."</item>
    /// <item><b>direct</b> (TO-list / implied / exact AT) → "would go to &lt;partner&gt; — it
    /// carries mail for that address."</item>
    /// <item><b>none</b> (no partner matched, not local) → "has nowhere to go — no partner covers
    /// that address."</item>
    /// </list>
    /// </summary>
    private static string ExplainRoute(string to, string? region, RoutingDecision decision)
    {
        string lead = Inv($"A message to {to} ");

        if (decision.IsLocal)
        {
            return lead + Inv($"would stay here - {to} is one of our users.");
        }

        if (decision.Targets.Count == 0)
        {
            return lead + "has nowhere to go - no partner covers that address.";
        }

        RouteTarget target = decision.Targets[0];
        // Regions read as plain lower-case place words (gbr.euro), never the wire's upper-case.
        string regionWord = (region ?? to).ToLowerInvariant();
        return target.Reason switch
        {
            RouteReason.Hierarchical => lead + Inv(
                $"would go to {target.PartnerCall} - it carries mail for stations in region {regionWord} (closest match)."),
            RouteReason.WildcardAt => lead + Inv(
                $"would go to {target.PartnerCall} - it's our catch-all route for mail with nowhere more specific to go."),
            _ => lead + Inv(
                $"would go to {target.PartnerCall} - it carries mail for that address."),
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>"1 partner" / "3 partners" — plain singular/plural for the diagnostics.</summary>
    private static string Plural(int count, string noun) =>
        count == 1 ? Inv($"1 {noun}") : Inv($"{count} {noun}s");
}
