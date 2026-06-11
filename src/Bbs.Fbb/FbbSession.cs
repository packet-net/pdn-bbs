using System.Text;

namespace Bbs.Fbb;

/// <summary>Which side of the forwarding connection this session plays — spec §3.1.</summary>
public enum FbbRole
{
    /// <summary>We dialled the peer: wait for its SID + <c>&gt;</c> prompt, then send ours (spec §3.15.1).</summary>
    Caller = 0,

    /// <summary>The peer dialled us: send our SID first (spec §3.1 step 2).</summary>
    Answerer,
}

/// <summary>Externally observable progress of an <see cref="FbbSession"/>.</summary>
public enum FbbSessionPhase
{
    /// <summary>Waiting for <see cref="FbbStart"/>.</summary>
    Created = 0,

    /// <summary>Waiting for the peer's SID line.</summary>
    AwaitingPeerSid,

    /// <summary>Caller only: SID seen, waiting for the <c>&gt;</c>-terminated prompt (spec §3.1 step 2).</summary>
    AwaitingPrompt,

    /// <summary>The peer holds the turn: expecting its proposals, FF or FQ.</summary>
    PeerTurn,

    /// <summary>A proposal block was surfaced; waiting for <see cref="FbbProposalDecisions"/>.</summary>
    AwaitingDecisions,

    /// <summary>Receiving the binary transfers for the proposals we accepted.</summary>
    ReceivingMessages,

    /// <summary>We proposed; waiting for the peer's FS line.</summary>
    AwaitingFs,

    /// <summary>The session ended gracefully (FF/FQ).</summary>
    Finished,

    /// <summary>The session ended on a protocol failure.</summary>
    Failed,
}

/// <summary>Static configuration for one forwarding session.</summary>
public sealed record FbbSessionConfig
{
    /// <summary>Which side we play.</summary>
    public required FbbRole Role { get; init; }

    /// <summary>Our BBS callsign — used for the answerer's <c>de CALL&gt;</c> prompt (spec §1.2).</summary>
    public required string OwnCallsign { get; init; }

    /// <summary>The version field of our SID, <c>[PDN-&lt;version&gt;-B1FHM$]</c> (spec §3.2).</summary>
    public string SidVersion { get; init; } = "0.1.0";

    /// <summary>Advertise B2F (<c>B12FHM$</c>) — future, spec §8 SHOULD.</summary>
    public bool OfferB2 { get; init; }

    /// <summary>
    /// Cap on the accumulated (uncompressed) sizes proposed per block —
    /// BPQ's <c>MaxFBBBlock</c>, default 10000 raw bytes (spec §3.3/§4.1).
    /// At least one message is always proposed regardless.
    /// </summary>
    public int MaxBlockBytes { get; init; } = ProposalBlock.DefaultMaxBlockBytes;
}

/// <summary>
/// The sans-IO FBB compressed-forwarding session state machine, both roles —
/// spec §3 throughout. Feed it <see cref="FbbInput"/> events; it returns the
/// <see cref="FbbAction"/>s the host must perform. It owns line/block
/// framing, SID negotiation, proposal blocks and their <c>F&gt;</c>
/// checksum, FS handling, the binary SOH/STX/EOT transfers (LZHUF B/B1
/// containers) and the FF/FQ turn-taking.
/// </summary>
/// <remarks>
/// Cross-implementation tolerances encoded here (spec §3.3/§3.4/§3.13):
/// the <c>F&gt;</c> checksum is always emitted but only verified when
/// present (BPQ always checksums; JNOS sends a bare <c>F&gt;</c> for FA);
/// FS accepts <c>Y</c>/<c>N</c>/<c>L</c>/<c>H</c>/<c>R</c>/<c>E</c> inbound
/// but we only ever emit <c>+ - = !n</c>; <c>;</c>-prefixed lines are
/// comments; any inbound <c>***</c> line is fatal; an <c>FF</c> met with an
/// empty queue is answered <c>FQ</c> and the session ends.
/// </remarks>
public sealed class FbbSession
{
    private const string ProposalChecksumErrorLine = "*** Proposal Checksum Error";
    private const string MessageChecksumErrorLine = "*** Message Checksum Error";
    private const string InvalidProposalErrorLine = "*** Protocol Error - Invalid Proposal";
    private const string TooManyProposalsErrorLine = "*** Protocol Error - Too Many Proposals";

    private readonly FbbSessionConfig _config;
    private readonly Queue<FbbOutboundMessage> _outbound;
    private readonly List<byte> _rx = [];
    private readonly List<Proposal> _pendingInbound = [];
    private readonly List<string> _pendingInboundRaw = [];
    private readonly Queue<Proposal> _acceptedInbound = new();

    private FbbBlockReader? _reader;
    private List<FbbOutboundMessage>? _proposedBatch;
    private LzhufContainerKind _container = LzhufContainerKind.B1;
    private bool _skipNextLf;

    /// <summary>Creates a session with the messages we intend to forward (may be empty).</summary>
    public FbbSession(FbbSessionConfig config, IEnumerable<FbbOutboundMessage>? outbound = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _outbound = new Queue<FbbOutboundMessage>(outbound ?? []);
    }

    /// <summary>Where the session currently stands.</summary>
    public FbbSessionPhase Phase { get; private set; } = FbbSessionPhase.Created;

    /// <summary>The peer's parsed SID, once received.</summary>
    public Sid? PeerSid { get; private set; }

    /// <summary>The container negotiated from the SID exchange (B1 unless the peer is V0-only) — spec §3.0.</summary>
    public LzhufContainerKind NegotiatedContainer => _container;

    /// <summary>
    /// Drives the machine: applies one input and returns the actions to
    /// perform, in order.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The input is invalid for the current phase (a host-side sequencing
    /// bug, e.g. decisions when none were requested).
    /// </exception>
    public IReadOnlyList<FbbAction> Advance(FbbInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var actions = new List<FbbAction>();
        switch (input)
        {
            case FbbStart:
                HandleStart(actions);
                break;
            case FbbPeerData data:
                AppendReceived(data.Data.Span);
                break;
            case FbbProposalDecisions decisions:
                HandleDecisions(decisions, actions);
                break;
            default:
                throw new InvalidOperationException($"Unknown input type {input.GetType().Name}.");
        }

        Drain(actions);
        return actions;
    }

    private void HandleStart(List<FbbAction> actions)
    {
        if (Phase != FbbSessionPhase.Created)
        {
            throw new InvalidOperationException("Session already started.");
        }

        if (_config.Role == FbbRole.Answerer)
        {
            // "The SID is always sent by the BBS as the first line after the
            // connection" [FBB-SID], followed by a >-terminated prompt the
            // caller may wait for (spec §3.1 step 2).
            actions.Add(new FbbSendLine(Sid.Build(_config.SidVersion, _config.OfferB2)));
            actions.Add(new FbbSendLine($"de {_config.OwnCallsign}>"));
        }

        Phase = FbbSessionPhase.AwaitingPeerSid;
    }

    private void AppendReceived(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _rx.Add(b);
        }
    }

    private void HandleDecisions(FbbProposalDecisions decisions, List<FbbAction> actions)
    {
        if (Phase != FbbSessionPhase.AwaitingDecisions)
        {
            throw new InvalidOperationException("No proposal block is awaiting decisions.");
        }

        ArgumentNullException.ThrowIfNull(decisions.Answers);
        if (decisions.Answers.Count != _pendingInbound.Count)
        {
            throw new InvalidOperationException(
                $"Got {decisions.Answers.Count} answers for {_pendingInbound.Count} proposals (spec §3.4).");
        }

        var final = new FsAnswer[_pendingInbound.Count];
        for (var i = 0; i < final.Length; i++)
        {
            // The per-proposal limit class: an oversize TO gets a polite '-'
            // regardless of host policy (spec §3.3 [BPQ-SRC]).
            final[i] = _pendingInbound[i] is FaProposal { RequiresPoliteReject: true }
                ? FsAnswer.AlreadyHave
                : decisions.Answers[i];
        }

        actions.Add(new FbbSendLine(FsResponse.Emit(final)));
        for (var i = 0; i < final.Length; i++)
        {
            if (final[i].Kind == FsAnswerKind.Accept)
            {
                _acceptedInbound.Enqueue(_pendingInbound[i]);
            }
        }

        _pendingInbound.Clear();
        _pendingInboundRaw.Clear();
        if (_acceptedInbound.Count > 0)
        {
            _reader = new FbbBlockReader();
            Phase = FbbSessionPhase.ReceivingMessages;
        }
        else
        {
            // No bodies follow; the block is complete and the turn reverses
            // to us (spec §3.1 step 4).
            TakeTurn(actions, peerSaidFf: false);
        }
    }

    private void Drain(List<FbbAction> actions)
    {
        while (Phase is not (FbbSessionPhase.Created or FbbSessionPhase.AwaitingDecisions
            or FbbSessionPhase.Finished or FbbSessionPhase.Failed))
        {
            if (Phase == FbbSessionPhase.ReceivingMessages)
            {
                if (_rx.Count == 0 || !FeedReader(actions))
                {
                    return;
                }

                continue;
            }

            if (!TryTakeLine(out var line))
            {
                // Caller-side prompt tolerance: FBB-style prompts may arrive
                // without a line terminator; a buffered run ending in '>' is
                // the prompt (spec §3.1 step 2).
                if (Phase == FbbSessionPhase.AwaitingPrompt && _rx.Count > 0 && _rx[^1] == (byte)'>')
                {
                    line = Encoding.Latin1.GetString([.. _rx]);
                    _rx.Clear();
                }
                else
                {
                    return;
                }
            }

            ProcessLine(line, actions);
        }
    }

    private bool TryTakeLine(out string line)
    {
        line = "";
        if (_skipNextLf && _rx.Count > 0)
        {
            if (_rx[0] == 0x0A)
            {
                _rx.RemoveAt(0);
            }

            _skipNextLf = false;
        }

        var idx = -1;
        for (var i = 0; i < _rx.Count; i++)
        {
            if (_rx[i] is 0x0D or 0x0A)
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            return false;
        }

        line = Encoding.Latin1.GetString([.. _rx[..idx]]);
        var remove = idx + 1;
        if (_rx[idx] == 0x0D)
        {
            if (idx + 1 < _rx.Count)
            {
                if (_rx[idx + 1] == 0x0A)
                {
                    remove++;
                }
            }
            else
            {
                _skipNextLf = true;
            }
        }

        _rx.RemoveRange(0, remove);
        return true;
    }

    private void ProcessLine(string line, List<FbbAction> actions)
    {
        if (line.Length == 0)
        {
            return;
        }

        // "Any line starting ';' is a comment and MUST be ignored" [WL-B2F,
        // spec §3.1 step 3] — covers ;FW:, ; MSGTYPES, ;PQ:.
        if (line.StartsWith(';'))
        {
            return;
        }

        // Any inbound "*** …" line is the peer reporting a fatal protocol
        // failure (spec §3.12).
        if (line.StartsWith("***", StringComparison.Ordinal))
        {
            FailFromPeer(line, actions);
            return;
        }

        switch (Phase)
        {
            case FbbSessionPhase.AwaitingPeerSid:
                ProcessAwaitingSid(line, actions);
                break;
            case FbbSessionPhase.AwaitingPrompt:
                ProcessAwaitingPrompt(line, actions);
                break;
            case FbbSessionPhase.PeerTurn:
                ProcessPeerTurn(line, actions);
                break;
            case FbbSessionPhase.AwaitingFs:
                ProcessAwaitingFs(line, actions);
                break;
            case FbbSessionPhase.Created:
            case FbbSessionPhase.AwaitingDecisions:
            case FbbSessionPhase.ReceivingMessages:
            case FbbSessionPhase.Finished:
            case FbbSessionPhase.Failed:
            default:
                break;
        }
    }

    private void ProcessAwaitingSid(string line, List<FbbAction> actions)
    {
        if (!Sid.IsSidShaped(line))
        {
            return; // banner / welcome text — ignored until the SID arrives
        }

        if (!Sid.TryParse(line, out var sid))
        {
            FailLocally("*** Protocol Error - Invalid SID", actions);
            return;
        }

        PeerSid = sid;

        // The compression guard, mirrored from LinBPQ (spec §3.2): FBB
        // blocked without compression is unsupported, and a $-only peer
        // means MBL text mode, which this FBB session does not speak.
        if (!sid.SupportsBlockedFbb || !sid.SupportsCompression)
        {
            FailLocally(
                "Uncompressed Blocked Forwarding is no longer supported - reconfgure BBS for MBL forwarding",
                actions);
            return;
        }

        _container = sid.SupportsB1 || sid.SupportsB2 ? LzhufContainerKind.B1 : LzhufContainerKind.B;
        if (_config.Role == FbbRole.Answerer)
        {
            Phase = FbbSessionPhase.PeerTurn; // the caller proposes first (spec §3.1 step 3)
        }
        else
        {
            Phase = FbbSessionPhase.AwaitingPrompt;
        }
    }

    private void ProcessAwaitingPrompt(string line, List<FbbAction> actions)
    {
        if (!line.TrimEnd().EndsWith('>'))
        {
            return; // welcome text never ends in '>' (BPQ strips it — spec §1.2)
        }

        // "Caller replies with its own SID … then immediately sends its
        // first proposal block" (spec §3.1 step 3); with an empty queue the
        // caller opens with FF instead (spec §3.11).
        actions.Add(new FbbSendLine(Sid.Build(_config.SidVersion, _config.OfferB2)));
        TakeTurn(actions, peerSaidFf: false);
    }

    private void ProcessPeerTurn(string line, List<FbbAction> actions)
    {
        var trimmed = line.TrimEnd();
        if (trimmed.StartsWith("FA ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("FB ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("FC ", StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingInbound.Count >= ProposalBlock.MaxProposalsPerBlock)
            {
                // BPQ allocates exactly 5 slots (spec §3.13.5,
                // [VERIFY-ORACLE #14]); we refuse rather than overflow.
                FailWithError(TooManyProposalsErrorLine, actions);
                return;
            }

            try
            {
                _pendingInbound.Add(Proposal.Parse(line));
                _pendingInboundRaw.Add(line);
            }
            catch (FbbProtocolException ex)
            {
                // "an error message will be sent immediately followed by a
                // disconnection" [FBB-PROTO, spec §3.3].
                FailWithError(ex.WireErrorLine ?? InvalidProposalErrorLine, actions);
            }

            return;
        }

        if (trimmed.StartsWith("F>", StringComparison.Ordinal))
        {
            if (!ProposalBlock.TryParseTerminator(trimmed, out var checksum) || _pendingInbound.Count == 0)
            {
                FailWithError(InvalidProposalErrorLine, actions);
                return;
            }

            // Verify only when the checksum is present — JNOS sends the bare
            // F> for FA (spec §3.3).
            if (checksum is { } received && received != ProposalBlock.ComputeChecksum(_pendingInboundRaw))
            {
                FailWithError(ProposalChecksumErrorLine, actions);
                return;
            }

            actions.Add(new FbbProposalsReceived([.. _pendingInbound]));
            Phase = FbbSessionPhase.AwaitingDecisions;
            return;
        }

        if (string.Equals(trimmed, "FF", StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingInbound.Count > 0)
            {
                FailWithError(InvalidProposalErrorLine, actions); // FF inside a proposal block
                return;
            }

            TakeTurn(actions, peerSaidFf: true);
            return;
        }

        if (string.Equals(trimmed, "FQ", StringComparison.OrdinalIgnoreCase))
        {
            // "The side receiving FQ disconnects" (spec §3.1 step 5).
            actions.Add(new FbbSessionOver(Graceful: true));
            Phase = FbbSessionPhase.Finished;
            return;
        }

        // Anything else in the peer's turn (stray text) is ignored.
    }

    private void ProcessAwaitingFs(string line, List<FbbAction> actions)
    {
        var trimmed = line.TrimEnd();
        if (trimmed.StartsWith("FS", StringComparison.OrdinalIgnoreCase))
        {
            var batch = _proposedBatch!;
            IReadOnlyList<FsAnswer> answers;
            try
            {
                answers = FsResponse.Parse(trimmed, batch.Count);
            }
            catch (FbbProtocolException ex)
            {
                FailWithError(ex.WireErrorLine ?? FsResponse.InvalidResponseErrorLine, actions);
                return;
            }

            // "after FS, the proposer transmits the accepted messages
            // immediately, in proposal order, with no per-message framing
            // between FS and the first byte" (spec §3.4).
            for (var i = 0; i < batch.Count; i++)
            {
                actions.Add(new FbbOutboundResult(batch[i], answers[i]));
                if (answers[i].Kind == FsAnswerKind.Accept)
                {
                    SendMessage(batch[i], answers[i].Offset, actions);
                }
            }

            _proposedBatch = null;
            Phase = FbbSessionPhase.PeerTurn; // the turn reverses (spec §3.1 step 4)
            return;
        }

        if (trimmed.StartsWith("F", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(trimmed, "FF", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "FQ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("FA ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("F>", StringComparison.Ordinal)))
        {
            FailWithError(FsResponse.InvalidResponseErrorLine, actions);
            return;
        }

        // Non-protocol text while awaiting FS is ignored.
    }

    private void SendMessage(FbbOutboundMessage message, int offset, List<FbbAction> actions)
    {
        var compressed = LzhufContainer.Encode(_container, message.Body.Span);
        byte[] payload;
        var headerOffset = 0;
        if (offset > 0 && _container == LzhufContainerKind.B1 && offset + 6 <= compressed.Length)
        {
            // Honouring !n: "resend bytes 0-5 then from n+6" — the receiver
            // already holds n post-header bytes (spec §3.8).
            payload = new byte[compressed.Length - offset];
            compressed.AsSpan(0, 6).CopyTo(payload);
            compressed.AsSpan(offset + 6).CopyTo(payload.AsSpan(6));
            headerOffset = offset;
        }
        else
        {
            // Offset 0, a V0 container (no resume in B0 — spec §3.8), or an
            // offset beyond what we hold: send the whole object from scratch.
            payload = compressed;
        }

        var title = message.Title.Length > BlockFraming.MaxTitleLength
            ? message.Title[..BlockFraming.MaxTitleLength]
            : message.Title;
        actions.Add(new FbbSendBytes(BlockFraming.EncodeMessage(title, headerOffset, payload)));
    }

    private bool FeedReader(List<FbbAction> actions)
    {
        var reader = _reader!;
        var buffer = _rx.ToArray();
        var status = reader.Feed(buffer, out var consumed);
        _rx.RemoveRange(0, consumed);
        switch (status)
        {
            case FbbBlockReaderStatus.NeedMoreData:
                return false;

            case FbbBlockReaderStatus.Complete:
                var proposal = _acceptedInbound.Dequeue();
                byte[] body;
                try
                {
                    body = LzhufContainer.Decode(_container, reader.Payload.Span);
                }
                catch (LzhufFormatException)
                {
                    // The container CRC16/format failing is the same wire
                    // failure class as a bad EOT checksum (spec §3.7/§3.12).
                    FailWithError(MessageChecksumErrorLine, actions);
                    return false;
                }

                actions.Add(new FbbMessageDelivered(proposal, reader.Title, body));
                if (_acceptedInbound.Count > 0)
                {
                    _reader = new FbbBlockReader();
                }
                else
                {
                    _reader = null;

                    // "When the other BBS has received all the messages in a
                    // block, it implicitly acknowledges by sending its
                    // proposal" [FBB-PROTO, spec §3.1 step 4].
                    TakeTurn(actions, peerSaidFf: false);
                }

                return true;

            case FbbBlockReaderStatus.ChecksumMismatch:
                FailWithError(MessageChecksumErrorLine, actions);
                return false;

            case FbbBlockReaderStatus.FramingError:
            default:
                FailWithError(MessageChecksumErrorLine, actions);
                return false;
        }
    }

    private void TakeTurn(List<FbbAction> actions, bool peerSaidFf)
    {
        if (_outbound.Count > 0)
        {
            var batch = new List<FbbOutboundMessage>();
            var lines = new List<string>();
            var accumulated = 0;
            while (_outbound.Count > 0 && batch.Count < ProposalBlock.MaxProposalsPerBlock)
            {
                var candidate = _outbound.Peek();

                // The MaxFBBBlock byte cap (spec §3.3): stop before the
                // message that would exceed it, but always propose at least
                // one.
                if (batch.Count > 0 && accumulated + candidate.Body.Length > _config.MaxBlockBytes)
                {
                    break;
                }

                _outbound.Dequeue();
                batch.Add(candidate);
                accumulated += candidate.Body.Length;
                lines.Add(new FaProposal(
                    'A',
                    candidate.MessageType,
                    candidate.From,
                    candidate.AtBbs,
                    candidate.To,
                    candidate.Bid,
                    candidate.Body.Length).ToWireLine());
            }

            foreach (var line in lines)
            {
                actions.Add(new FbbSendLine(line));
            }

            // "LinBPQ always sends the checksum … Our BBS MUST send it"
            // (spec §3.3).
            actions.Add(new FbbSendLine(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum(lines))));
            _proposedBatch = batch;
            Phase = FbbSessionPhase.AwaitingFs;
        }
        else if (peerSaidFf)
        {
            // "If the other side also has nothing it sends FQ and the link
            // is disconnected" [FBB-PROTO, spec §3.1 step 5].
            actions.Add(new FbbSendLine("FQ"));
            actions.Add(new FbbSessionOver(Graceful: true));
            Phase = FbbSessionPhase.Finished;
        }
        else
        {
            actions.Add(new FbbSendLine("FF"));
            Phase = FbbSessionPhase.PeerTurn;
        }
    }

    private void FailWithError(string errorLine, List<FbbAction> actions)
    {
        // Spec §3.12: the error line is transmitted, then the link drops.
        actions.Add(new FbbSendLine(errorLine));
        actions.Add(new FbbProtocolError(errorLine));
        actions.Add(new FbbSessionOver(Graceful: false));
        Phase = FbbSessionPhase.Failed;
    }

    private void FailFromPeer(string peerLine, List<FbbAction> actions)
    {
        actions.Add(new FbbProtocolError(peerLine));
        actions.Add(new FbbSessionOver(Graceful: false));
        Phase = FbbSessionPhase.Failed;
    }

    private void FailLocally(string detail, List<FbbAction> actions)
    {
        // Diagnosed locally and not transmitted (LinBPQ logs its equivalent
        // and disconnects — spec §3.2).
        actions.Add(new FbbProtocolError(detail));
        actions.Add(new FbbSessionOver(Graceful: false));
        Phase = FbbSessionPhase.Failed;
    }
}
