using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>How one Fbb session over RHP ended.</summary>
/// <param name="Completed">The FSM reached an end state (vs the link dying / idle timeout).</param>
/// <param name="Graceful">The FF/FQ close (spec §3.1 step 5) was reached.</param>
/// <param name="PeerSidRaw">The peer's raw SID string (<see cref="Sid.Raw"/>) once parsed, else null.</param>
/// <param name="B2Active">Whether B2F (FC) was negotiated for this session (<see cref="FbbSession.B2Active"/>).</param>
public sealed record FbbSessionResult(bool Completed, bool Graceful, string? PeerSidRaw = null, bool B2Active = false);

/// <summary>
/// Drives a sans-IO <see cref="FbbSession"/> over an RHP child connection, both roles:
/// protocol lines go out CRLF-terminated (spec §3.13.2 — bare CR is misparsed by LinBPQ
/// under load), transfers go out raw; inbound bytes are fed verbatim. Proposal decisions
/// and message deliveries are delegated to <see cref="InboundMessageReceiver"/>; FS
/// verdicts for our own proposals land in <see cref="BbsStore.MarkForwarded"/> per the
/// Core semantics ('+'/'-'/'R' clear the queue entry, '='/'E' leave it for the next cycle).
/// </summary>
public sealed class FbbSessionRunner
{
    private readonly BbsStore _store;
    private readonly InboundMessageReceiver _receiver;
    private readonly BbsIdentity _identity;
    private readonly string _sidVersion;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly FileInboundPartialStore? _partialStore;

    /// <summary>A silent peer ends the session after this long (TimeProvider-driven).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Creates the runner.</summary>
    /// <param name="partialStore">
    /// The durable scratch store for receiver-side restart granting (issue #38). When supplied, an
    /// inbound session is given a peer-scoped resume view so an interrupted transfer can be granted
    /// <c>FS !offset</c> on a re-offer. Null (the default for tests/standalone) disables resume —
    /// every accept is a from-zero receive, the pre-#38 behaviour.
    /// </param>
    public FbbSessionRunner(
        BbsStore store,
        InboundMessageReceiver receiver,
        BbsIdentity identity,
        string sidVersion,
        TimeProvider time,
        ILogger<FbbSessionRunner> logger,
        FileInboundPartialStore? partialStore = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(sidVersion);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _receiver = receiver;
        _identity = identity;
        _sidVersion = sidVersion;
        _time = time;
        _logger = logger;
        _partialStore = partialStore;
    }

    /// <summary>
    /// Runs the answerer side for an accepted inbound connection whose first line announced
    /// a forwarding partner. The session runs in continue-mode
    /// (<see cref="FbbSessionConfig.SidAlreadySent"/>): the demux's greet-immediately flow
    /// already put our SID (and the console prompt) on the wire, so the FSM starts at the
    /// SID-parse phase. <paramref name="initialData"/> is everything the demux consumed
    /// while peeking (the peer's SID line and any tail) — fed to the FSM right after start,
    /// so nothing is lost. A configured partner contributes its queue for reverse forwarding
    /// (spec §3.11) and its size caps; an unknown SID-shaped caller is still served with
    /// defaults (messages stored with its callsign as ReceivedFrom).
    /// <para>
    /// <paramref name="selfGreet"/> is for a demux-less leg — a raw AX.25 / AXUDP / direct-FBB
    /// link with no <see cref="Sessions.InboundDemux"/> ahead of it (the interop harness and the
    /// answer-real-xfbbd direction). When true the runner does NOT assume an upstream greet: the
    /// FSM emits our SID + <c>de CALL&gt;</c> prompt itself on start (symmetric with the
    /// self-greeting caller), and <paramref name="initialData"/> is normally empty (nothing was
    /// peeked). The default (false) keeps continue-mode: the demux already greeted and handed us
    /// the peeked SID line — the production node / FBBPORT path, unchanged.
    /// </para>
    /// </summary>
    public Task<FbbSessionResult> RunAnswererAsync(
        IFbbConnection child,
        byte[] initialData,
        CancellationToken cancellationToken,
        bool selfGreet = false)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(initialData);

        if (!(_store.GetForwardingMaster() ?? true))
        {
            // Inbound forwarding HELD (forwarding.enabled = false): refuse the FBB session so a
            // partner cannot push mail in either — accepting an inbound forward would advance the
            // mailbox and make this no-rollback migration irreversible (the safe-abort window holds
            // BOTH directions). We return without engaging the exchange; the caller closes the link
            // and the partner retries later. Human console sessions are unaffected (the demux only
            // reaches here for a forwarding opener). No message bodies have been exchanged yet, so
            // the partner's mail stays on the partner.
            LogInboundHeld(_logger, child.RemoteCallsign, null);
            return Task.FromResult(new FbbSessionResult(Completed: false, Graceful: false));
        }

        // Match the inbound caller on its BASE callsign — its source SSID is indeterminate
        // (an outbound connect grabs whatever SSID is free), so it can't key the partner lookup.
        Partner? partner = _store.FindPartnerByBaseCall(child.RemoteCallsign);

        // Per-partner gate: disabling a partner stops it in BOTH directions, not just outbound dials.
        // Refuse the inbound FBB session from a CONFIGURED-but-disabled partner before any mail moves —
        // the same single toggle gates dial-out and accept-in. No bodies have been exchanged, so the
        // partner keeps its mail and retries; it is accepted the instant it is re-enabled. Scoped to
        // KNOWN partners: an unknown/temporary caller keeps its existing LinBPQ "temporary BBS"
        // behaviour (the whole-BBS forwarding hold is the lever for those, not this per-partner gate).
        if (partner is not null && !partner.Enabled)
        {
            LogInboundDisabled(_logger, child.RemoteCallsign, null);
            return Task.FromResult(new FbbSessionResult(Completed: false, Graceful: false));
        }

        string partnerCall = partner?.Call ?? Callsigns.Normalize(child.RemoteCallsign);
        IReadOnlyList<OutboundItem> outbound = partner is null
            ? []
            : OutboundBuilder.Build(_store.GetForwardQueue(partner.Call), partner, _identity, _time, _logger,
                // Hold over-cap messages here too (same rule as the scheduler) so an in-session send
                // doesn't re-skip them forever — see ForwardingScheduler / compat spec §4.1.
                onOversize: (number, bytes) => _store.HoldMessage(number,
                    FormattableString.Invariant($"too large for {partner.Call} ({bytes} > {partner.MaxTxSize} bytes)")));
        return RunAsync(FbbRole.Answerer, child, partner, partnerCall, outbound, initialData, selfGreet, cancellationToken);
    }

    /// <summary>
    /// Runs the caller side of an outbound forwarding cycle (the scheduler's path).
    /// <paramref name="initialData"/> carries any bytes a connect script left unconsumed
    /// (the partner's SID line and tail when the script's SID-wait completed — spec §4.4).
    /// </summary>
    public Task<FbbSessionResult> RunCallerAsync(
        IFbbConnection child,
        Partner partner,
        IReadOnlyList<OutboundItem> outbound,
        CancellationToken cancellationToken,
        byte[]? initialData = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(partner);
        ArgumentNullException.ThrowIfNull(outbound);
        return RunAsync(FbbRole.Caller, child, partner, partner.Call, outbound, initialData, selfGreet: false, cancellationToken);
    }

    private async Task<FbbSessionResult> RunAsync(
        FbbRole role,
        IFbbConnection child,
        Partner? partner,
        string partnerCall,
        IReadOnlyList<OutboundItem> outbound,
        byte[]? initialData,
        bool selfGreet,
        CancellationToken cancellationToken)
    {
        var session = new FbbSession(
            new FbbSessionConfig
            {
                Role = role,
                OwnCallsign = _identity.Callsign,
                SidVersion = _sidVersion,

                // Answerer = continue-mode by default: the demux greeted (SID + prompt) on
                // accept. selfGreet flips this for a demux-less leg (raw AX.25 / AXUDP / FBB):
                // the FSM emits our SID + prompt itself on FbbStart, symmetric with the caller.
                SidAlreadySent = role == FbbRole.Answerer && !selfGreet,

                // B2F is opt-in per partner (default off → B1 unchanged). When set we advertise
                // '2' in our SID; the session then activates B2 only if the peer's SID also
                // advertises it (B2Active = our offer ∩ the peer SID — spec §3.2/§3.9). An
                // unknown caller (no partner record) is never B2-allowed, so its FC is refused.
                OfferB2 = partner?.AllowB2F ?? false,

                // Receiver-side restart granting (issue #38): give the session a peer-scoped view of
                // the durable partial store, so an interrupted inbound transfer for this peer can be
                // granted FS !offset on a re-offer. Keyed by base callsign (the partial survives an
                // SSID change / a restart). Null store (tests/standalone) → no resume, pre-#38 behaviour.
                InboundResume = _partialStore is { } ps && !string.IsNullOrWhiteSpace(partnerCall)
                    ? ps.ForPeer(partnerCall)
                    : null,
            },
            outbound.Select(o => o.Wire));

        // FS verdicts come back carrying the FbbOutboundMessage; map by reference back to
        // the store number (structural record equality could alias identical drafts).
        var numbers = new Dictionary<FbbOutboundMessage, long>(ReferenceEqualityComparer.Instance);
        foreach (OutboundItem item in outbound)
        {
            numbers[item.Wire] = item.Number;
        }

        var state = new RunState(partner, partnerCall, numbers);

        await ApplyAsync(session.Advance(new FbbStart()), session, child, state, cancellationToken).ConfigureAwait(false);
        LogNegotiatedOnce(session, state);
        if (initialData is { Length: > 0 })
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                LogWireRx(_logger, partnerCall, Readable(initialData, RxLogCap), null);
            }

            await ApplyAsync(session.Advance(new FbbPeerData(initialData)), session, child, state, cancellationToken)
                .ConfigureAwait(false);
            LogNegotiatedOnce(session, state);
        }

        while (!state.Over)
        {
            byte[]? data;
            using (var idle = new CancellationTokenSource(IdleTimeout, _time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token))
            {
                try
                {
                    data = await child.ReceiveAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    LogIdle(_logger, partnerCall, IdleTimeout.TotalSeconds, null);
                    return new FbbSessionResult(Completed: false, Graceful: false);
                }
            }

            if (data is null)
            {
                LogDropped(_logger, partnerCall, null);
                return new FbbSessionResult(Completed: false, Graceful: false);
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                LogWireRx(_logger, partnerCall, Readable(data, RxLogCap), null);
            }

            await ApplyAsync(session.Advance(new FbbPeerData(data)), session, child, state, cancellationToken)
                .ConfigureAwait(false);
            LogNegotiatedOnce(session, state);
        }

        return new FbbSessionResult(
            Completed: true, state.Graceful, PeerSidRaw: session.PeerSid?.Raw, B2Active: session.B2Active);
    }

    /// <summary>
    /// Emits ONE info line per session capturing the negotiated mode + the peer's raw SID, the first
    /// time the peer's SID has been parsed (<see cref="FbbSession.PeerSid"/> non-null). Idempotent: a
    /// <see cref="RunState.NegotiatedLogged"/> latch keeps it to a single line rather than spamming on
    /// every drive-loop pass. Mode is "B2" when B2F (FC) is active for the session, else "B1".
    /// </summary>
    private void LogNegotiatedOnce(FbbSession session, RunState state)
    {
        if (state.NegotiatedLogged || session.PeerSid is not { } sid)
        {
            return;
        }

        state.NegotiatedLogged = true;
        // Arg order MUST match the message template's placeholder order: {Partner}, {Mode}, {PeerSid}.
        LogNegotiated(_logger, state.PartnerCall, session.B2Active ? "B2" : "B1", sid.Raw, null);
    }

    private async Task ApplyAsync(
        IReadOnlyList<FbbAction> actions,
        FbbSession session,
        IFbbConnection child,
        RunState state,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<FbbAction>(actions);
        while (queue.Count > 0)
        {
            switch (queue.Dequeue())
            {
                case FbbSendLine line:
                    // "the transport MUST append CRLF" (FbbSendLine contract, spec §3.13.2).
                    LogWireTx(_logger, state.PartnerCall, line.Line, null);
                    await SendToleratingCloseAsync(
                        child, session, state, Encoding.Latin1.GetBytes(line.Line + "\r\n"), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case FbbSendBytes bytes:
                    LogWireTxBin(_logger, state.PartnerCall, bytes.Data.Length, null);
                    await SendToleratingCloseAsync(child, session, state, bytes.Data, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case FbbProposalsReceived proposals:
                    IReadOnlyList<FsAnswer> answers = _receiver.Decide(proposals.Proposals, state.Partner);
                    foreach (FbbAction next in session.Advance(new FbbProposalDecisions(answers)))
                    {
                        queue.Enqueue(next);
                    }

                    break;

                case FbbMessageDelivered delivered:
                    _receiver.Deliver(delivered, state.PartnerCall);
                    break;

                case FbbOutboundResult result:
                    HandleOutboundResult(result, state);
                    break;

                case FbbProtocolError error:
                    LogProtocolError(_logger, state.PartnerCall, error.ErrorLine, null);
                    break;

                case FbbSessionOver over:
                    state.Over = true;
                    state.Graceful = over.Graceful;
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Sends one chunk, tolerating a peer disconnect once the FSM has already reached a
    /// terminal phase. The caller's closing courtesy is <c>FQ</c> after the peer's final
    /// <c>FF</c> (spec §3.1 step 5: "the side receiving FQ disconnects") — but a real LinBPQ
    /// that has received everything drops the link the instant it sends that <c>FF</c>, before
    /// our <c>FQ</c> goes out, so the node reports the handle gone ("Not connected"). Every
    /// message was already proposed, FS-resolved and (for accepts) transferred — the FSM's
    /// graceful verdict stands — so a failure on the *closing* line is logged, not raised: it
    /// must not turn a fully delivered cycle into a scheduler failure + backoff retry. A send
    /// that fails BEFORE the FSM terminated (a mid-session body or proposal) is a genuine drop
    /// and propagates unchanged.
    /// </summary>
    private async Task SendToleratingCloseAsync(
        IFbbConnection child,
        FbbSession session,
        RunState state,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        try
        {
            await child.SendAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && session.Phase is FbbSessionPhase.Finished or FbbSessionPhase.Failed)
        {
            LogCloseRace(_logger, state.PartnerCall, ex.Message, null);
        }
    }

    private void HandleOutboundResult(FbbOutboundResult result, RunState state)
    {
        if (!state.Numbers.TryGetValue(result.Message, out long number))
        {
            return;
        }

        switch (result.Answer.Kind)
        {
            case FsAnswerKind.Accept:
            case FsAnswerKind.AlreadyHave:
            case FsAnswerKind.Reject:
                // '+' transferred, '-'/'R' the partner already has it / refused it — all
                // clear this partner's queue entry (BbsStore.MarkForwarded contract,
                // compat spec §3.4); a rejected message stays routeable elsewhere.
                _store.MarkForwarded(number, state.PartnerCall);
                LogForwarded(_logger, number, state.PartnerCall, result.Answer.Kind.ToString(), null);
                break;

            case FsAnswerKind.Defer:
            case FsAnswerKind.ProposalError:
            default:
                // '='/'H' defer (and 'E') — leave queued for the next cycle (spec §3.4).
                LogDeferred(_logger, number, state.PartnerCall, null);
                break;
        }
    }

    private sealed class RunState(Partner? partner, string partnerCall, Dictionary<FbbOutboundMessage, long> numbers)
    {
        public Partner? Partner { get; } = partner;

        public string PartnerCall { get; } = partnerCall;

        public Dictionary<FbbOutboundMessage, long> Numbers { get; } = numbers;

        public bool Over { get; set; }

        public bool Graceful { get; set; }

        /// <summary>Latch so the negotiated-mode info line is emitted at most once per session.</summary>
        public bool NegotiatedLogged { get; set; }
    }

    private static readonly Action<ILogger, string, double, Exception?> LogIdle =
        LoggerMessage.Define<string, double>(LogLevel.Warning, new EventId(1, "FbbIdle"),
            "Forwarding session with {Partner} idle for {Seconds}s — abandoned");

    private static readonly Action<ILogger, string, Exception?> LogDropped =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "FbbDropped"),
            "Forwarding session with {Partner} dropped mid-session");

    private static readonly Action<ILogger, string, string, Exception?> LogProtocolError =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(3, "FbbProtocolError"),
            "Forwarding session with {Partner} failed: {ErrorLine}");

    private static readonly Action<ILogger, long, string, string, Exception?> LogForwarded =
        LoggerMessage.Define<long, string, string>(LogLevel.Information, new EventId(4, "Forwarded"),
            "Message {Number} cleared for {Partner} ({Verdict})");

    private static readonly Action<ILogger, long, string, Exception?> LogDeferred =
        LoggerMessage.Define<long, string>(LogLevel.Information, new EventId(5, "Deferred"),
            "Message {Number} deferred by {Partner}; left queued");

    private static readonly Action<ILogger, string, string, Exception?> LogCloseRace =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(6, "CloseRace"),
            "Forwarding session with {Partner} closed: peer dropped the link before our FQ ({Detail}); "
            + "all messages were delivered — treated as a graceful close");

    private static readonly Action<ILogger, string, string, string, Exception?> LogNegotiated =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, new EventId(7, "FbbNegotiated"),
            "Forwarding session with {Partner} negotiated {Mode} (peer SID {PeerSid})");

    private static readonly Action<ILogger, string, Exception?> LogInboundHeld =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(8, "InboundForwardingHeld"),
            "Inbound forwarding is HELD (forwarding.enabled = false); refusing the FBB session from {Partner}. "
            + "The link is closed and the partner will retry; no mail is accepted.");

    private static readonly Action<ILogger, string, Exception?> LogInboundDisabled =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(9, "InboundForwardingPartnerDisabled"),
            "Refusing the inbound FBB session from {Partner}: the partner is disabled. The link is closed "
            + "and the partner will retry; no mail is accepted — enable the partner to let it forward.");

    // --- Debug-level wire observability (gated by Debug; zero-cost when not enabled) ---
    // Renders the live B1F/B2F conversation: every protocol line out, every inbound chunk, and
    // transfer-block sizes. Turn on Bbs.Host.Forwarding at Debug to watch a forwarding session.

    /// <summary>Cap on the readable rendering of an inbound chunk so a binary transfer block does not
    /// flood the log; protocol lines (FA/F&gt;/FS/FF/FQ/SID) are far shorter than this.</summary>
    private const int RxLogCap = 200;

    private static readonly Action<ILogger, string, string, Exception?> LogWireTx =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(8, "FbbWireTx"),
            "FBB > {Partner}: {Line}");

    private static readonly Action<ILogger, string, int, Exception?> LogWireTxBin =
        LoggerMessage.Define<string, int>(LogLevel.Debug, new EventId(9, "FbbWireTxBin"),
            "FBB > {Partner}: <{Bytes}-byte binary block>");

    private static readonly Action<ILogger, string, string, Exception?> LogWireRx =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(10, "FbbWireRx"),
            "FBB < {Partner}: {Data}");

    /// <summary>Log-safe rendering of wire bytes: printable ASCII verbatim, CR/LF as \r/\n, any other
    /// byte as &lt;HH&gt; hex; capped at <paramref name="cap"/> with a "+Nb" tail for the remainder.</summary>
    private static string Readable(ReadOnlySpan<byte> bytes, int cap)
    {
        int n = Math.Min(bytes.Length, cap);
        var sb = new StringBuilder(n + 12);
        for (int i = 0; i < n; i++)
        {
            byte b = bytes[i];
            if (b == 13)
            {
                sb.Append("\\r");
            }
            else if (b == 10)
            {
                sb.Append("\\n");
            }
            else if (b is >= 32 and < 127)
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('<').Append(b.ToString("X2")).Append('>');
            }
        }

        if (bytes.Length > cap)
        {
            sb.Append("…+").Append(bytes.Length - cap).Append('b');
        }

        return sb.ToString();
    }
}
