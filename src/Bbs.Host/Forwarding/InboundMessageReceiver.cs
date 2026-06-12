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
    private readonly SevenPlusAssembler _sevenPlus;
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
        SevenPlusAssembler sevenPlus,
        string ownCallsign,
        TimeProvider time,
        ILogger<InboundMessageReceiver> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sevenPlus);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownCallsign);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _routing = routing;
        _engine = engine;
        _sevenPlus = sevenPlus;
        _ownBaseCall = Callsigns.StripSsid(Callsigns.Normalize(ownCallsign));
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Decides one proposal block (spec §3.4, receive order per §4.3): oversize → '-',
    /// duplicate BID → '-', else '+'. An <c>FC</c> (B2F) proposal is accepted on the SAME
    /// terms as an FA — but ONLY from a partner we enabled B2 on (<see cref="Partner.AllowB2F"/>);
    /// from anyone else we never advertised B2, so FC was never legitimately offered and is
    /// refused with '-' (the original guard, intact). The session itself downgrades the §3.3
    /// polite-reject class.
    /// </summary>
    public IReadOnlyList<FsAnswer> Decide(IReadOnlyList<Proposal> proposals, Partner? partner)
    {
        ArgumentNullException.ThrowIfNull(proposals);

        // Unknown peers (SID-shaped caller without a partner record) get the global
        // default cap (compat spec §4.1 MaxRXSize default 99999).
        int maxRx = partner?.MaxRxSize ?? 99999;
        bool allowB2 = partner?.AllowB2F ?? false;
        var answers = new FsAnswer[proposals.Count];
        for (int i = 0; i < proposals.Count; i++)
        {
            answers[i] = proposals[i] switch
            {
                FaProposal fa => DecideFa(fa, maxRx),
                FcProposal fc => DecideFc(fc, maxRx, allowB2),
                _ => FsAnswer.AlreadyHave,
            };
        }

        return answers;
    }

    /// <summary>
    /// Decides one B2F <c>FC</c> proposal (spec §3.9). Refused with '-' unless the partner is
    /// B2-enabled (we only ever advertised B2 to those). Then the same proposal-time policy as
    /// FA: oversize (the FC carries the uncompressed object size) → '-', a known MID/BID → '-'
    /// (the network-wide dedup identity), else '+'. The TO/type live in the B2 header (not the
    /// FC), so the dup check keys on the MID alone here — sufficient for B2's MID-is-unique model.
    /// </summary>
    private FsAnswer DecideFc(FcProposal fc, int maxRx, bool allowB2)
    {
        if (!allowB2)
        {
            return FsAnswer.AlreadyHave; // never advertised B2 → never legitimately offered
        }

        if (fc.UncompressedSize > maxRx)
        {
            LogRefusedSize(_logger, fc.Mid, fc.UncompressedSize, maxRx, null);
            return FsAnswer.AlreadyHave;
        }

        if (_store.LookupBid(fc.Mid) is not null)
        {
            LogRefusedBid(_logger, fc.Mid, null);
            return FsAnswer.AlreadyHave; // known MID → '-' (spec §4.3/§2.3)
        }

        return FsAnswer.Accept;
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

        return delivered.Proposal switch
        {
            FaProposal fa => DeliverFa(fa, delivered, fromPartnerCall),
            FcProposal fc => DeliverFc(fc, delivered, fromPartnerCall),
            _ => Unsupported(delivered),
        };
    }

    private Message? Unsupported(FbbMessageDelivered delivered)
    {
        LogUnsupportedDelivery(_logger, delivered.Proposal.GetType().Name, null);
        return null;
    }

    private Message DeliverFa(FaProposal fa, FbbMessageDelivered delivered, string fromPartnerCall)
    {
        MessageType type = MessageTypeExtensions.MessageTypeFromCode(fa.MessageType);
        Message stored = StoreAndRoute(
            type, fa.From, [fa.To], fa.AtBbs, fa.Bid, delivered.Title, delivered.Body, fromPartnerCall,
            ccRecipients: [], attachments: []);
        AutoCreateHomedUser(type, fa.To, fa.AtBbs, fa.Bid);
        return stored;
    }

    /// <summary>
    /// Stores one inbound B2F (FC) object (spec §3.9): the delivered body is the whole B2
    /// object, so decode it, map From/To/Cc/Subject/Body/Files/MID(=BID) onto the stored message,
    /// and run it through the SAME store + auto-create + route path as a B1 (FA) inbound — so the
    /// home-BBS rules (design.md rules #1/#2) and the loop/age R-chain guard apply identically.
    /// The routing TO/AT come from the B2 <c>To:</c> header (a bare call, or <c>call@bbs</c> —
    /// the AT split mirrors the console's send addressing).
    ///
    /// Multi-recipient + attachments (this slice): ALL <c>To:</c> are stored as To-recipients and
    /// ALL <c>Cc:</c> as Cc-recipients; ALL <c>File:</c> parts are stored as attachments. The
    /// PRIMARY recipient (first <c>To:</c>) drives <c>At</c>/routing/auto-create exactly as the
    /// single-recipient path did.
    ///
    /// NAMED DEFERRAL (F-1, per-home fan-out — forwarding.md): a recipient whose <c>@home</c>
    /// differs from the primary's is still STORED as a recipient, but the message is forwarded
    /// ONCE on the primary's route carrying the full To: list (FBB's next hop re-distributes). We
    /// do NOT split into per-home copies. This matches the single-<c>At</c> store model and the
    /// webmail compose; it is the existing F-1 per-recipient fan-out item, surfaced here, not a
    /// silent drop.
    /// </summary>
    private Message? DeliverFc(FcProposal fc, FbbMessageDelivered delivered, string fromPartnerCall)
    {
        B2Message b2;
        try
        {
            b2 = B2Message.Decode(delivered.Body.Span);
        }
        catch (FbbProtocolException ex)
        {
            // A B2 object that survived the container CRC but is structurally malformed: log and
            // drop (the session already acknowledged receipt — there is no FS to fail here).
            LogB2DecodeFailed(_logger, fc.Mid, ex.Message, null);
            return null;
        }

        // The primary recipient (first To:) drives At/routing/auto-create; the rest are stored
        // alongside (F-1 per-home fan-out deferred — see the method summary).
        (string primaryTo, string? primaryAt) = SplitToAt(b2.To.Count > 0 ? b2.To[0] : "");
        var toRecipients = new List<string>(b2.To.Count == 0 ? 1 : b2.To.Count) { primaryTo };
        for (int i = 1; i < b2.To.Count; i++)
        {
            toRecipients.Add(SplitToAt(b2.To[i]).To);
        }

        var ccRecipients = new List<string>(b2.Cc.Count);
        foreach (string cc in b2.Cc)
        {
            ccRecipients.Add(SplitToAt(cc).To);
        }

        var attachments = new List<MessageAttachment>(b2.Files.Count);
        foreach (B2Attachment file in b2.Files)
        {
            attachments.Add(new MessageAttachment(file.Name, file.Content));
        }

        MessageType type = b2.Type switch
        {
            B2MessageType.Bulletin => MessageType.Bulletin,
            _ => MessageType.Personal, // BPQ stores all B2 arrivals as P; we keep B → Bulletin, rest → Personal
        };

        Message stored = StoreAndRoute(
            type,
            from: b2.From ?? primaryTo,
            toRecipients: toRecipients,
            atBbs: primaryAt ?? "",
            bid: b2.Mid,
            subject: b2.Subject ?? delivered.Title,
            body: b2.Body,
            fromPartnerCall,
            ccRecipients: ccRecipients,
            attachments: attachments);
        AutoCreateHomedUser(type, primaryTo, primaryAt ?? "", b2.Mid);
        return stored;
    }

    /// <summary>
    /// The common inbound store + R-chain hold + route path shared by the FA and FC deliveries.
    /// The body is stored verbatim (the sender's R: chain intact); the BID is recorded with its
    /// arrival direction (the routing loop-guard input); §3.14 loop/age failures store Held. All
    /// <paramref name="toRecipients"/> (primary first), <paramref name="ccRecipients"/> and
    /// <paramref name="attachments"/> are stored; routing runs ONCE on the stored message — which
    /// routes on the primary recipient/<paramref name="atBbs"/> exactly as the single-To path did
    /// (per-home fan-out is the F-1 deferral documented on <see cref="DeliverFc"/>).
    /// </summary>
    private Message StoreAndRoute(
        MessageType type,
        string from,
        IReadOnlyList<string> toRecipients,
        string atBbs,
        string bid,
        string subject,
        ReadOnlyMemory<byte> body,
        string fromPartnerCall,
        IReadOnlyList<string> ccRecipients,
        IReadOnlyList<MessageAttachment> attachments)
    {
        string? holdReason = CheckRChain(body);
        var stored = _store.AddMessage(new MessageDraft
        {
            Type = type,
            From = from,
            Recipients = toRecipients,
            CcRecipients = ccRecipients,
            At = atBbs,
            Bid = bid,
            Subject = subject,
            Body = body,
            Attachments = attachments,
            ReceivedFrom = fromPartnerCall,
            Hold = holdReason is not null,
        });

        if (holdReason is not null)
        {
            LogHeld(_logger, stored.Number, holdReason, null);
        }

        LogStored(_logger, stored.Number, stored.Bid, fromPartnerCall, null);
        _routing.RouteMessage(stored);

        // Inbound 7plus integration (design.md): after the message is stored + routed (the raw
        // part-bulletin itself still forwards onward unchanged), scan it for 7plus parts. A body
        // without the magic is a cheap no-op (the common case); a complete set surfaces a synthesized
        // local_only message carrying the decoded file as an attachment. The synthesized message is
        // local_only → never forwarded, and is not re-scanned (it has an attachment, not 7plus text).
        _sevenPlus.ProcessInbound(stored);
        return stored;
    }

    /// <summary>
    /// Splits a B2 <c>To:</c> address into (TO, AT): <c>call@bbs.ha</c> → ("call", "bbs.ha");
    /// a bare call → (call, null). Mirrors the console's <c>send call@bbs</c> parse so a homed
    /// personal (<c>call@&lt;us&gt;</c>) is recognised local by the same AT-is-us signal.
    /// </summary>
    private static (string To, string? At) SplitToAt(string address)
    {
        string trimmed = address.Trim();
        int at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            return (trimmed, null);
        }

        string to = trimmed[..at];
        string atBbs = trimmed[(at + 1)..];
        return atBbs.Length > 0 ? (to, atBbs) : (to, null);
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
    private void AutoCreateHomedUser(MessageType type, string to, string atBbs, string bid)
    {
        if (type != MessageType.Personal || !_engine.AtResolvesToLocal(atBbs))
        {
            return;
        }

        if (_store.UserExists(to))
        {
            return; // already a user — nothing to create (rule #2 fires only for unknown TOs)
        }

        if (_store.EnsureUser(to))
        {
            LogAutoCreatedUser(_logger, Callsigns.NormalizeAddressee(to), bid, null);
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

    private static readonly Action<ILogger, string, string, Exception?> LogB2DecodeFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(8, "B2DecodeFailed"),
            "Discarded delivered B2F object {Mid}: malformed B2 message ({Detail})");

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
