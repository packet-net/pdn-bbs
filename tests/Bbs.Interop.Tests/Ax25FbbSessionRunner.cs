using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Forwarding;

namespace Bbs.Interop.Tests;

/// <summary>How one FBB session over the AX.25 leg ended.</summary>
/// <param name="Completed">The FSM reached an end state (vs the link dying / idle timeout).</param>
/// <param name="Graceful">The FF/FQ close (spec §3.1 step 5) was reached.</param>
/// <param name="Verdicts">The peer's FS verdict per store message number we proposed.</param>
/// <param name="ProtocolErrors">Any <c>*** …</c> failures surfaced by the FSM.</param>
/// <param name="B2Active">
/// Whether B2F (FC) was negotiated for this session — our offer ∩ the peer's SID (spec §3.2/§3.9).
/// The oracle-check evidence that FC/B2, not FA/B1, was on the wire.
/// </param>
/// <param name="PeerSidRaw">The peer's SID exactly as received (the live LinBPQ SID, terminator stripped).</param>
/// <param name="InboundProposals">
/// Every proposal the peer offered us, in arrival order — the inbound-direction wire evidence:
/// an <see cref="FcProposal"/> here proves the oracle proposed FC/B2 (not a silent FA/B1).
/// </param>
internal sealed record InteropFbbResult(
    bool Completed,
    bool Graceful,
    IReadOnlyDictionary<long, FsAnswerKind> Verdicts,
    IReadOnlyList<string> ProtocolErrors,
    bool B2Active,
    string? PeerSidRaw,
    IReadOnlyList<Proposal> InboundProposals);

/// <summary>
/// Drives a sans-IO <see cref="FbbSession"/> over any <see cref="IByteLink"/> bearer (AX.25 via
/// net-sim for the LinBPQ oracle, or raw TCP/telnet for the LinFBB / F6FBB oracle), both
/// roles. This is a transcription of the W5 runner
/// (src/Bbs.Host/Forwarding/FbbSessionRunner.cs) — that runner is RHP-child-shaped (it
/// takes the sealed RhpChildConnection), so the interop lane carries the same action pump
/// over the AX.25 leg: protocol lines go out CRLF-terminated (spec §3.13.2), transfers go
/// out raw, proposal decisions and deliveries delegate to
/// <see cref="InboundMessageReceiver"/>, FS verdicts land in
/// <see cref="BbsStore.MarkForwarded"/> with the same '+'/'-'/'R' vs '='/'E' split.
/// Keep in sync with the W5 runner if its semantics change.
/// </summary>
internal sealed class Ax25FbbSessionRunner
{
    private readonly BbsStore _store;
    private readonly InboundMessageReceiver _receiver;
    private readonly BbsIdentity _identity;
    private readonly string _sidVersion;
    private readonly TimeProvider _time;

    /// <summary>A silent peer ends the session after this long (real-time; oracle is live).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public Ax25FbbSessionRunner(
        BbsStore store,
        InboundMessageReceiver receiver,
        BbsIdentity identity,
        string sidVersion,
        TimeProvider time)
    {
        _store = store;
        _receiver = receiver;
        _identity = identity;
        _sidVersion = sidVersion;
        _time = time;
    }

    /// <summary>
    /// Runs the answerer side for an accepted inbound connection. The answerer pumps its
    /// initial actions (our SID + <c>de CALL&gt;</c> prompt) immediately on session-up —
    /// a LinBPQ caller waits for OUR SID before it sends anything (the W5 handoff note).
    /// A configured partner contributes its queue for reverse forwarding (spec §3.11).
    /// </summary>
    public Task<InteropFbbResult> RunAnswererAsync(IByteLink link, CancellationToken cancellationToken)
    {
        string partnerCall = Callsigns.Normalize(link.RemoteCallsign);
        Partner? partner = _store.GetPartner(partnerCall);
        IReadOnlyList<OutboundItem> outbound = partner is null
            ? []
            : OutboundBuilder.Build(
                _store.GetForwardQueue(partner.Call), partner, _identity, _time,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return RunAsync(FbbRole.Answerer, link, partner, partnerCall, outbound, cancellationToken);
    }

    /// <summary>Runs the caller side of an outbound forwarding cycle.</summary>
    public Task<InteropFbbResult> RunCallerAsync(
        IByteLink link,
        Partner partner,
        IReadOnlyList<OutboundItem> outbound,
        CancellationToken cancellationToken) =>
        RunAsync(FbbRole.Caller, link, partner, partner.Call, outbound, cancellationToken);

    private async Task<InteropFbbResult> RunAsync(
        FbbRole role,
        IByteLink link,
        Partner? partner,
        string partnerCall,
        IReadOnlyList<OutboundItem> outbound,
        CancellationToken cancellationToken)
    {
        var session = new FbbSession(
            new FbbSessionConfig
            {
                Role = role,
                OwnCallsign = _identity.Callsign,
                SidVersion = _sidVersion,

                // B2F is opt-in per partner (default off → B1 unchanged), mirroring the W5
                // FbbSessionRunner: when set we advertise '2' in our SID and the session
                // activates B2 only if the peer's SID also advertises it (B2Active = our
                // offer ∩ the peer SID — spec §3.2/§3.9). An unknown caller (no partner
                // record) is never B2-allowed, so its FC would be refused.
                OfferB2 = partner?.AllowB2F ?? false,
            },
            outbound.Select(o => o.Wire));

        // FS verdicts come back carrying the FbbOutboundMessage; map by reference back to
        // the store number (mirrors the W5 runner).
        var numbers = new Dictionary<FbbOutboundMessage, long>(ReferenceEqualityComparer.Instance);
        foreach (OutboundItem item in outbound)
        {
            numbers[item.Wire] = item.Number;
        }

        var state = new RunState(partner, partnerCall, numbers);

        await ApplyAsync(session.Advance(new FbbStart()), session, link, state, cancellationToken)
            .ConfigureAwait(false);

        while (!state.Over)
        {
            byte[]? data;
            using (var idle = new CancellationTokenSource(IdleTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token))
            {
                try
                {
                    data = await link.ReceiveAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return new InteropFbbResult(
                        Completed: false, Graceful: false, state.Verdicts, state.Errors,
                        session.B2Active, session.PeerSid?.Raw, state.InboundProposals);
                }
            }

            if (data is null)
            {
                return new InteropFbbResult(
                    Completed: false, Graceful: false, state.Verdicts, state.Errors,
                    session.B2Active, session.PeerSid?.Raw, state.InboundProposals);
            }

            await ApplyAsync(session.Advance(new FbbPeerData(data)), session, link, state, cancellationToken)
                .ConfigureAwait(false);
        }

        return new InteropFbbResult(
            Completed: true, state.Graceful, state.Verdicts, state.Errors,
            session.B2Active, session.PeerSid?.Raw, state.InboundProposals);
    }

    private async Task ApplyAsync(
        IReadOnlyList<FbbAction> actions,
        FbbSession session,
        IByteLink link,
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
                    await link.SendAsync(Encoding.Latin1.GetBytes(line.Line + "\r\n"), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case FbbSendBytes bytes:
                    await link.SendAsync(bytes.Data, cancellationToken).ConfigureAwait(false);
                    break;

                case FbbProposalsReceived proposals:
                    state.InboundProposals.AddRange(proposals.Proposals);
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
                    state.Errors.Add(error.ErrorLine);
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

        state.Verdicts[number] = result.Answer.Kind;
        switch (result.Answer.Kind)
        {
            case FsAnswerKind.Accept:
            case FsAnswerKind.AlreadyHave:
            case FsAnswerKind.Reject:
                // '+'/'-'/'R' all clear this partner's queue entry (BbsStore.MarkForwarded
                // contract, compat spec §3.4).
                _store.MarkForwarded(number, state.PartnerCall);
                break;

            case FsAnswerKind.Defer:
            case FsAnswerKind.ProposalError:
            default:
                // '='/'H'/'E' — leave queued for the next cycle (spec §3.4).
                break;
        }
    }

    private sealed class RunState(Partner? partner, string partnerCall, Dictionary<FbbOutboundMessage, long> numbers)
    {
        public Partner? Partner { get; } = partner;

        public string PartnerCall { get; } = partnerCall;

        public Dictionary<FbbOutboundMessage, long> Numbers { get; } = numbers;

        public Dictionary<long, FsAnswerKind> Verdicts { get; } = [];

        public List<string> Errors { get; } = [];

        public List<Proposal> InboundProposals { get; } = [];

        public bool Over { get; set; }

        public bool Graceful { get; set; }
    }
}
