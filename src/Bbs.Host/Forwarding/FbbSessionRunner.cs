using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>How one Fbb session over RHP ended.</summary>
/// <param name="Completed">The FSM reached an end state (vs the link dying / idle timeout).</param>
/// <param name="Graceful">The FF/FQ close (spec §3.1 step 5) was reached.</param>
public sealed record FbbSessionResult(bool Completed, bool Graceful);

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

    /// <summary>A silent peer ends the session after this long (TimeProvider-driven).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Creates the runner.</summary>
    public FbbSessionRunner(
        BbsStore store,
        InboundMessageReceiver receiver,
        BbsIdentity identity,
        string sidVersion,
        TimeProvider time,
        ILogger<FbbSessionRunner> logger)
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
    /// </summary>
    public Task<FbbSessionResult> RunAnswererAsync(
        RhpChildConnection child,
        byte[] initialData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(initialData);

        Partner? partner = _store.GetPartner(child.RemoteCallsign);
        string partnerCall = partner?.Call ?? Callsigns.Normalize(child.RemoteCallsign);
        IReadOnlyList<OutboundItem> outbound = partner is null
            ? []
            : OutboundBuilder.Build(_store.GetForwardQueue(partner.Call), partner, _identity, _time, _logger);
        return RunAsync(FbbRole.Answerer, child, partner, partnerCall, outbound, initialData, cancellationToken);
    }

    /// <summary>
    /// Runs the caller side of an outbound forwarding cycle (the scheduler's path).
    /// <paramref name="initialData"/> carries any bytes a connect script left unconsumed
    /// (the partner's SID line and tail when the script's SID-wait completed — spec §4.4).
    /// </summary>
    public Task<FbbSessionResult> RunCallerAsync(
        RhpChildConnection child,
        Partner partner,
        IReadOnlyList<OutboundItem> outbound,
        CancellationToken cancellationToken,
        byte[]? initialData = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(partner);
        ArgumentNullException.ThrowIfNull(outbound);
        return RunAsync(FbbRole.Caller, child, partner, partner.Call, outbound, initialData, cancellationToken);
    }

    private async Task<FbbSessionResult> RunAsync(
        FbbRole role,
        RhpChildConnection child,
        Partner? partner,
        string partnerCall,
        IReadOnlyList<OutboundItem> outbound,
        byte[]? initialData,
        CancellationToken cancellationToken)
    {
        var session = new FbbSession(
            new FbbSessionConfig
            {
                Role = role,
                OwnCallsign = _identity.Callsign,
                SidVersion = _sidVersion,

                // Answerer = continue-mode: the demux greeted (SID + prompt) on accept.
                SidAlreadySent = role == FbbRole.Answerer,

                // B2F is opt-in per partner (default off → B1 unchanged). When set we advertise
                // '2' in our SID; the session then activates B2 only if the peer's SID also
                // advertises it (B2Active = our offer ∩ the peer SID — spec §3.2/§3.9). An
                // unknown caller (no partner record) is never B2-allowed, so its FC is refused.
                OfferB2 = partner?.AllowB2F ?? false,
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
        if (initialData is { Length: > 0 })
        {
            await ApplyAsync(session.Advance(new FbbPeerData(initialData)), session, child, state, cancellationToken)
                .ConfigureAwait(false);
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

            await ApplyAsync(session.Advance(new FbbPeerData(data)), session, child, state, cancellationToken)
                .ConfigureAwait(false);
        }

        return new FbbSessionResult(Completed: true, state.Graceful);
    }

    private async Task ApplyAsync(
        IReadOnlyList<FbbAction> actions,
        FbbSession session,
        RhpChildConnection child,
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
                    await child.SendAsync(Encoding.Latin1.GetBytes(line.Line + "\r\n"), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case FbbSendBytes bytes:
                    await child.SendAsync(bytes.Data, cancellationToken).ConfigureAwait(false);
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
}
