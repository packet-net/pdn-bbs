using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>
/// The Core receive path for messages delivered by an Fbb session: proposal-time policy
/// (BID dedup via <see cref="BbsStore.CheckInboundBid"/>, size caps), delivery-time
/// storage (received body verbatim — the sender's R: chain included; our own R: line is
/// stamped at transmit time by <see cref="OutboundBuilder"/>), the §3.14 loop/age holds,
/// and the routing re-enqueue for onward forwarding.
/// </summary>
public sealed class InboundMessageReceiver
{
    private readonly BbsStore _store;
    private readonly RoutingService _routing;
    private readonly RoutingEngine _engine;
    private readonly string _ownBaseCall;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Maximum tolerated R:-chain age — the BID-lifetime default (compat spec §3.14/§6).</summary>
    public static readonly TimeSpan MaxChainAge = TimeSpan.FromDays(60);

    /// <summary>Creates the receiver.</summary>
    public InboundMessageReceiver(
        BbsStore store,
        RoutingService routing,
        RoutingEngine engine,
        string ownCallsign,
        TimeProvider time,
        ILogger<InboundMessageReceiver> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownCallsign);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _routing = routing;
        _engine = engine;
        _ownBaseCall = Callsigns.StripSsid(Callsigns.Normalize(ownCallsign));
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Decides one proposal block (spec §3.4, receive order per §4.3): oversize → '-',
    /// duplicate BID → '-', B2F FC (never advertised, so never legitimately offered) → '-',
    /// else '+'. The session itself downgrades the §3.3 polite-reject class.
    /// </summary>
    public IReadOnlyList<FsAnswer> Decide(IReadOnlyList<Proposal> proposals, Partner? partner)
    {
        ArgumentNullException.ThrowIfNull(proposals);

        // Unknown peers (SID-shaped caller without a partner record) get the global
        // default cap (compat spec §4.1 MaxRXSize default 99999).
        int maxRx = partner?.MaxRxSize ?? 99999;
        var answers = new FsAnswer[proposals.Count];
        for (int i = 0; i < proposals.Count; i++)
        {
            answers[i] = proposals[i] is FaProposal fa ? DecideFa(fa, maxRx) : FsAnswer.AlreadyHave;
        }

        return answers;
    }

    private FsAnswer DecideFa(FaProposal fa, int maxRx)
    {
        if (fa.Size > maxRx)
        {
            LogRefusedSize(_logger, fa.Bid, fa.Size, maxRx, null);
            return FsAnswer.AlreadyHave; // "size > MaxRXSize → '-'" (spec §4.3)
        }

        MessageType type;
        try
        {
            type = MessageTypeExtensions.MessageTypeFromCode(fa.MessageType);
        }
        catch (ArgumentOutOfRangeException)
        {
            LogRefusedType(_logger, fa.Bid, fa.MessageType, null);
            return FsAnswer.AlreadyHave;
        }

        if (_store.CheckInboundBid(fa.Bid, type, fa.To) == BidDisposition.RejectDuplicate)
        {
            LogRefusedBid(_logger, fa.Bid, null);
            return FsAnswer.AlreadyHave; // "known BID → '-'" (spec §4.3/§2.3)
        }

        return FsAnswer.Accept;
    }

    /// <summary>
    /// Stores one delivered message: body verbatim (R: chain intact), BID recorded with
    /// its arrival direction (feeds the routing BID loop guard), §3.14 loop/age failures →
    /// stored Held, then the routing re-enqueue for onward forwarding.
    /// </summary>
    public Message? Deliver(FbbMessageDelivered delivered, string fromPartnerCall)
    {
        ArgumentNullException.ThrowIfNull(delivered);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromPartnerCall);

        if (delivered.Proposal is not FaProposal fa)
        {
            LogUnsupportedDelivery(_logger, delivered.Proposal.GetType().Name, null);
            return null;
        }

        string? holdReason = CheckRChain(delivered.Body);
        var stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageTypeExtensions.MessageTypeFromCode(fa.MessageType),
            From = fa.From,
            Recipients = [fa.To],
            At = fa.AtBbs,
            Bid = fa.Bid,
            Subject = delivered.Title,
            Body = delivered.Body,
            ReceivedFrom = fromPartnerCall,
            Hold = holdReason is not null,
        });

        if (holdReason is not null)
        {
            LogHeld(_logger, stored.Number, holdReason, null);
        }

        LogStored(_logger, stored.Number, stored.Bid, fromPartnerCall, null);
        AutoCreateHomedUser(fa);
        _routing.RouteMessage(stored);
        return stored;
    }

    /// <summary>
    /// Auto-creates the recipient's user record on the first inbound personal homed here
    /// (design.md "The home-BBS requirement" rule #2). Trigger — only this case:
    /// <list type="bullet">
    /// <item>the delivery is a <b>Personal</b> (never a bulletin or NTS/traffic), and</item>
    /// <item>its <b>AT resolves to us</b> — <see cref="RoutingEngine.AtResolvesToLocal(string?)"/>,
    /// the same own-call/own-HA signal rule #1's local-delivery pre-empt uses; this is the homed
    /// mailbox case, distinct from a no-AT personal that is only local because its TO is already
    /// a known user, and</item>
    /// <item>the TO is <b>not</b> already a known local user (<see cref="BbsStore.UserExists"/>).</item>
    /// </list>
    /// then a skeletal user is created (<see cref="BbsStore.EnsureUser"/>, idempotent) so the
    /// mail is listable on the owner's first connect. Runs only on this inbound path — webmail
    /// compose / console sends never reach it, so they never auto-create. An explicit remote AT
    /// fails the AT-is-us test (and forwards onward per rule #1), so it never auto-creates either.
    /// </summary>
    private void AutoCreateHomedUser(FaProposal fa)
    {
        MessageType type;
        try
        {
            type = MessageTypeExtensions.MessageTypeFromCode(fa.MessageType);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        if (type != MessageType.Personal || !_engine.AtResolvesToLocal(fa.AtBbs))
        {
            return;
        }

        if (_store.UserExists(fa.To))
        {
            return; // already a user — nothing to create (rule #2 fires only for unknown TOs)
        }

        if (_store.EnsureUser(fa.To))
        {
            LogAutoCreatedUser(_logger, Callsigns.NormalizeAddressee(fa.To), fa.Bid, null);
        }
    }

    /// <summary>The §3.14 R:-chain checks: own-call loop, future-dated/expired/corrupt oldest hop.</summary>
    private string? CheckRChain(ReadOnlyMemory<byte> body)
    {
        string text = Encoding.Latin1.GetString(body.Span);
        IReadOnlyList<RLine> chain = RLine.ExtractLeadingRLines(text.ReplaceLineEndings("\n").Split('\n'));
        if (chain.Count == 0)
        {
            return null; // no chain to check — a first-hop message from a non-stamping peer
        }

        if (RLine.IsLikelyLooping(chain, _ownBaseCall))
        {
            return "Message may be looping (own call appears twice in the R: chain)";
        }

        return RLine.CheckAge(chain, _time.GetUtcNow(), MaxChainAge) switch
        {
            RLineAgeStatus.Ok => null,
            RLineAgeStatus.FutureDated => "R: chain is future-dated",
            RLineAgeStatus.TooOld => "R: chain is older than the BID lifetime",
            RLineAgeStatus.Unparseable => "Corrupt R: line - can't determine age",
            _ => null,
        };
    }

    private static readonly Action<ILogger, string, int, int, Exception?> LogRefusedSize =
        LoggerMessage.Define<string, int, int>(LogLevel.Information, new EventId(1, "RefusedSize"),
            "Refused proposal {Bid}: {Size} bytes exceeds MaxRxSize {MaxRx}");

    private static readonly Action<ILogger, string, char, Exception?> LogRefusedType =
        LoggerMessage.Define<string, char>(LogLevel.Warning, new EventId(2, "RefusedType"),
            "Refused proposal {Bid}: unknown message type '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogRefusedBid =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(3, "RefusedBid"),
            "Refused proposal {Bid}: duplicate BID");

    private static readonly Action<ILogger, string, Exception?> LogUnsupportedDelivery =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "UnsupportedDelivery"),
            "Discarded delivered message with unsupported proposal shape {Shape}");

    private static readonly Action<ILogger, long, string, Exception?> LogHeld =
        LoggerMessage.Define<long, string>(LogLevel.Warning, new EventId(5, "Held"),
            "Message {Number} stored Held: {Reason}");

    private static readonly Action<ILogger, long, string, string, Exception?> LogStored =
        LoggerMessage.Define<long, string, string>(LogLevel.Information, new EventId(6, "Stored"),
            "Stored message {Number} (BID {Bid}) from {Partner}");

    private static readonly Action<ILogger, string, string, Exception?> LogAutoCreatedUser =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(7, "AutoCreatedUser"),
            "Auto-created skeletal user {Call} on first inbound personal homed here (BID {Bid})");
}
