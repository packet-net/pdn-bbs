using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Microsoft.Extensions.Logging;

namespace Bbs.Host;

/// <summary>
/// Connects stored messages to the per-partner forward queues: every new message — webmail
/// compose, console S-family entry, an inbound forwarding delivery — is routed once per
/// recipient through <see cref="RoutingEngine"/> and enqueued via
/// <see cref="BbsStore.EnqueueForwards"/>. Routing is idempotent (re-enqueueing an existing
/// pair is a no-op), so the startup backlog sweep and the per-event calls may overlap
/// safely. Partners with FWDNewImmediately get a scheduler nudge.
/// </summary>
public sealed class RoutingService
{
    private readonly BbsStore _store;
    private readonly RoutingEngine _engine;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private long _watermark;

    /// <summary>Creates the service.</summary>
    public RoutingService(BbsStore store, RoutingEngine engine, ILogger<RoutingService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _engine = engine;
        _logger = logger;
    }

    /// <summary>The forwarding scheduler's wake-up, keyed by partner call (set during composition).</summary>
    public Action<string>? NudgePartner { get; set; }

    /// <summary>
    /// Routes everything live in the store (startup: catches messages stored before a
    /// crash/restart that never reached a queue; harmless re-runs for the rest).
    /// </summary>
    public void RouteStartupBacklog()
    {
        lock (_gate)
        {
            _watermark = 0;
            SweepCore();
        }
    }

    /// <summary>Routes messages stored since the last sweep (called after console sessions end).</summary>
    public void RouteNewMessages()
    {
        lock (_gate)
        {
            SweepCore();
        }
    }

    /// <summary>Routes one just-stored message (webmail compose, inbound forwarding delivery).</summary>
    public void RouteMessage(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            RouteCore(message);
        }
    }

    private void SweepCore()
    {
        IReadOnlyList<Message> fresh = _store.ListMessages(new MessageQuery
        {
            MinNumber = _watermark + 1,
            OldestFirst = true,
            IncludeHeld = true, // held messages keep their queue rows and resume on unhold
        });
        foreach (Message message in fresh)
        {
            _watermark = Math.Max(_watermark, message.Number);
            RouteCore(message);
        }
    }

    private void RouteCore(Message message)
    {
        if (message.Status is MessageStatus.Killed or MessageStatus.Forwarded or MessageStatus.Delivered)
        {
            return;
        }

        IReadOnlyList<Partner> partners = _store.ListPartners();
        if (partners.Count == 0)
        {
            return;
        }

        string? bidSeenFrom = _store.LookupBid(message.Bid)?.FirstSeenFrom;
        IReadOnlyList<string> chainCalls = ExtractRouteChainCalls(message.Body);

        var targets = new List<string>();
        foreach (MessageRecipient recipient in message.Recipients)
        {
            // Cc recipients are stored + carried in the B2 envelope but never drive a forward
            // target — a Cc is informational; routing is on the To-recipients only (forwarding.md
            // "B2F negotiation": forward once on the primary route with the full To: list, the
            // F-1 per-home deferral). A real LinBPQ likewise drops Cc on receipt (oracle finding).
            if (recipient.Cc)
            {
                continue;
            }

            RoutingDecision decision = _engine.Route(
                new RoutingRequest
                {
                    Type = message.Type,
                    ToCall = recipient.ToCall,
                    At = message.At,
                    ReceivedFrom = message.ReceivedFrom,
                    BidSeenFrom = bidSeenFrom,
                    RouteChainCalls = chainCalls,
                    // Local delivery beats forwarding (design.md rule #1): a personal for one of
                    // our own users stays here rather than matching a partner's wildcard-AT route.
                    ToIsLocalUser = _store.UserExists(recipient.ToCall),
                },
                partners);
            foreach (RouteTarget target in decision.Targets)
            {
                string call = Callsigns.Normalize(target.PartnerCall);
                if (!targets.Contains(call))
                {
                    targets.Add(call);
                }
            }
        }

        if (targets.Count == 0)
        {
            LogNoRoute(_logger, message.Number, null);
            return;
        }

        _store.EnqueueForwards(message.Number, targets);
        LogRouted(_logger, message.Number, string.Join(' ', targets), null);

        foreach (string call in targets)
        {
            Partner? partner = _store.GetPartner(call);
            if (partner is { Enabled: true, ForwardNewImmediately: true })
            {
                NudgePartner?.Invoke(call);
            }
        }
    }

    /// <summary>BBS calls from the message's leading R: chain — the §3.14 send-side loop guard.</summary>
    internal static IReadOnlyList<string> ExtractRouteChainCalls(ReadOnlyMemory<byte> body)
    {
        string text = Encoding.Latin1.GetString(body.Span);

        // Normalise CR/CRLF/LF so a CRLF-terminated chain doesn't read as an empty line,
        // while a genuine blank line (the chain/body separator) still ends the chain.
        IReadOnlyList<RLine> chain = RLine.ExtractLeadingRLines(text.ReplaceLineEndings("\n").Split('\n'));
        var calls = new List<string>();
        foreach (RLine hop in chain)
        {
            if (hop.Callsign is { Length: > 0 } call && !calls.Contains(call))
            {
                calls.Add(call);
            }
        }

        return calls;
    }

    private static readonly Action<ILogger, long, Exception?> LogNoRoute =
        LoggerMessage.Define<long>(LogLevel.Debug, new EventId(1, "NoRoute"),
            "Routing trace: no match for message {Number}");

    private static readonly Action<ILogger, long, string, Exception?> LogRouted =
        LoggerMessage.Define<long, string>(LogLevel.Information, new EventId(2, "Routed"),
            "Message {Number} queued for {Partners}");
}
