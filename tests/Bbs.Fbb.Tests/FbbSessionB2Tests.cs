using System.Text;

namespace Bbs.Fbb.Tests;

/// <summary>
/// B2F (FC) wiring of the forwarding FSM — spec §3.3/§3.9. B2 is active for a
/// session iff <see cref="FbbSessionConfig.OfferB2"/> is set AND the peer's SID
/// advertises B2 (<see cref="Sid.SupportsB2"/>). When active, the proposer emits
/// uniform <c>FC EM</c> proposals (MID + uncompressed size + compressed size) and
/// transfers each queued <see cref="FbbOutboundMessage.Body"/> — already a B2
/// object — through the existing B1 framing; the receiver accepts FC, receives the
/// object through the same framing and surfaces it as <see cref="FbbMessageDelivered"/>.
/// When NOT active the FSM is byte-for-byte the B1 path (covered by FbbSessionTests).
/// </summary>
public class FbbSessionB2Tests
{
    private const string B2PeerSid = "[BPQ-6.0.25.30-B12FWIHJM$]"; // advertises B2
    private const string B1PeerSid = "[BPQ-6.0.24.44-B1FHM$]";     // B1 only
    private const string OurB1Sid = "[PDN-0.1.0-B1FHM$]";
    private const string OurB2Sid = "[PDN-0.1.0-B12FHM$]";

    private static FbbSessionConfig Config(FbbRole role, bool offerB2) => new()
    {
        Role = role,
        OwnCallsign = "GB7PDN",
        SidVersion = "0.1.0",
        OfferB2 = offerB2,
    };

    /// <summary>A queued message whose body is a real B2 object (what OutboundBuilder ships in B2 mode).</summary>
    private static FbbOutboundMessage B2Queued(string mid, string bodyText, string title = "Subject")
    {
        byte[] obj = new B2Message
        {
            Mid = mid,
            Type = B2MessageType.Private,
            From = "M0LTE",
            To = ["G8BPQ"],
            Subject = title,
            Date = "2026/06/11 12:00",
            Mbo = "GB7PDN",
            Body = Encoding.ASCII.GetBytes(bodyText),
        }.Encode();

        return new FbbOutboundMessage
        {
            MessageType = 'P',
            From = "M0LTE",
            AtBbs = "GB7BPQ",
            To = "G8BPQ",
            Bid = mid,
            Title = title,
            Body = Encoding.ASCII.GetBytes(bodyText), // the B1 plaintext (unused while B2 is active)
            B2Object = obj,                            // what the session ships as FC + transfer
        };
    }

    private static IReadOnlyList<FbbAction> FeedLine(FbbSession session, string line) =>
        session.Advance(new FbbPeerData(Encoding.ASCII.GetBytes(line + "\r")));

    private static IReadOnlyList<FbbAction> FeedBytes(FbbSession session, byte[] data) =>
        session.Advance(new FbbPeerData(data));

    private static List<string> Lines(IEnumerable<FbbAction> actions) =>
        [.. actions.OfType<FbbSendLine>().Select(a => a.Line)];

    private static byte[] FramedB1(string title, byte[] obj) =>
        BlockFraming.EncodeMessage(title, 0, LzhufContainer.Encode(LzhufContainerKind.B1, obj));

    // --- Negotiation matrix ---

    [Fact]
    public void Negotiation_OfferFalse_SendsB1Sid_RegardlessOfPeer()
    {
        // AllowB2=false (OfferB2 false): our SID is B1-only and we propose FA — the
        // B1 regression. The peer advertising B2 changes nothing.
        var session = new FbbSession(Config(FbbRole.Answerer, offerB2: false));
        Assert.Equal([OurB1Sid, "de GB7PDN>"], Lines(session.Advance(new FbbStart())));
        FeedLine(session, B2PeerSid);
        Assert.True(session.PeerSid!.SupportsB2);
        Assert.False(session.B2Active); // gate is AND: peer-B2 alone never flips it
    }

    [Fact]
    public void Negotiation_OfferTrue_PeerB2_ActivatesB2_AnswererSid()
    {
        var session = new FbbSession(Config(FbbRole.Answerer, offerB2: true));
        Assert.Equal([OurB2Sid, "de GB7PDN>"], Lines(session.Advance(new FbbStart())));
        FeedLine(session, B2PeerSid);
        Assert.True(session.B2Active);
        Assert.Equal(LzhufContainerKind.B1, session.NegotiatedContainer); // B2 rides B1 framing
    }

    [Fact]
    public void Negotiation_OfferTrue_PeerB1Only_FallsBackToB1()
    {
        // We offered B2 but the partner is B1-only — the SID intersection drops B2, so
        // we fall back to FA/B1. The container stays B1.
        var session = new FbbSession(Config(FbbRole.Answerer, offerB2: true));
        session.Advance(new FbbStart());
        FeedLine(session, B1PeerSid);
        Assert.False(session.PeerSid!.SupportsB2);
        Assert.False(session.B2Active);
        Assert.Equal(LzhufContainerKind.B1, session.NegotiatedContainer);
    }

    [Fact]
    public void Negotiation_Caller_OfferTrue_PeerB2_AdvertisesB2Sid()
    {
        var session = new FbbSession(Config(FbbRole.Caller, offerB2: true));
        session.Advance(new FbbStart());
        FeedLine(session, B2PeerSid);
        var actions = FeedLine(session, "de GB7BPQ>");
        Assert.Contains(OurB2Sid, Lines(actions)); // caller answers the peer's SID with its own
        Assert.True(session.B2Active);
    }

    // --- Outbound B2: FC proposal + transfer ---

    [Fact]
    public void Outbound_B2Active_ProposesFcEm_WithCorrectMidAndSizes()
    {
        const string bodyText = "Hello over B2F.\r\n";
        var queued = B2Queued("9_GB7PDN", bodyText);
        byte[] obj = queued.B2Object!.Value.ToArray();
        int csize = LzhufContainer.Encode(LzhufContainerKind.B1, obj).Length;

        var session = new FbbSession(Config(FbbRole.Answerer, offerB2: true), [queued]);
        session.Advance(new FbbStart());
        FeedLine(session, B2PeerSid);

        // Peer passes the turn → we propose. Uniform FC EM (not FA), MID + usize + csize.
        var lines = Lines(FeedLine(session, "FF"));
        string expectedFc = $"FC EM 9_GB7PDN {obj.Length} {csize} 0";
        Assert.Equal([expectedFc, ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([expectedFc]))], lines);
        Assert.Equal(FbbSessionPhase.AwaitingFs, session.Phase);

        // FS + → the B2 object is transferred through the B1 framing; it decodes back.
        var sendActions = FeedLine(session, "FS +");
        Assert.Equal(FsAnswerKind.Accept, Assert.Single(sendActions.OfType<FbbOutboundResult>()).Answer.Kind);
        var sent = Assert.Single(sendActions.OfType<FbbSendBytes>());
        Assert.Equal(FramedB1("Subject", obj), sent.Data.ToArray());

        // The transferred bytes are exactly the encoded B2 object.
        var reader = new FbbBlockReader();
        reader.Feed(sent.Data.ToArray(), out _);
        byte[] decodedObj = LzhufContainer.Decode(LzhufContainerKind.B1, reader.Payload.Span);
        B2Message round = B2Message.Decode(decodedObj);
        Assert.Equal("9_GB7PDN", round.Mid);
        Assert.Equal(bodyText, Encoding.ASCII.GetString(round.Body.Span));
    }

    [Fact]
    public void Outbound_B2Active_ProposesAllQueuedAsFc_Uniform()
    {
        var session = new FbbSession(
            Config(FbbRole.Answerer, offerB2: true),
            [B2Queued("1_GB7PDN", "one\r\n"), B2Queued("2_GB7PDN", "two\r\n")]);
        session.Advance(new FbbStart());
        FeedLine(session, B2PeerSid);

        var lines = Lines(FeedLine(session, "FF"));
        Assert.Equal(3, lines.Count); // 2 × FC + F>
        Assert.All(lines[..2], l => Assert.StartsWith("FC EM ", l, StringComparison.Ordinal));
        Assert.StartsWith("F> ", lines[2], StringComparison.Ordinal);
    }

    // --- Inbound B2: accept FC, receive object, deliver ---

    [Fact]
    public void Inbound_B2Active_AcceptsFc_ReceivesObject_Delivers()
    {
        const string bodyText = "Inbound B2 body.\r\n";
        byte[] obj = new B2Message
        {
            Mid = "123_GB7BPQ",
            Type = B2MessageType.Private,
            From = "M0XYZ",
            To = ["G8ABC"],
            Subject = "Hi there",
            Body = Encoding.ASCII.GetBytes(bodyText),
        }.Encode();
        int csize = LzhufContainer.Encode(LzhufContainerKind.B1, obj).Length;

        var session = new FbbSession(Config(FbbRole.Answerer, offerB2: true));
        session.Advance(new FbbStart());
        FeedLine(session, B2PeerSid);

        string fc = $"FC EM 123_GB7BPQ {obj.Length} {csize} 0";
        Assert.Empty(FeedLine(session, fc));
        var block = FeedLine(session, ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([fc])));
        var received = Assert.IsType<FbbProposalsReceived>(Assert.Single(block));
        var fcProp = Assert.IsType<FcProposal>(Assert.Single(received.Proposals));
        Assert.Equal("123_GB7BPQ", fcProp.Mid);
        Assert.Equal(obj.Length, fcProp.UncompressedSize);

        // Accept → FS +, then the B2 object arrives through B1 framing.
        Assert.Equal(["FS +"], Lines(session.Advance(new FbbProposalDecisions([FsAnswer.Accept]))));
        var deliveries = FeedBytes(session, FramedB1("Hi there", obj));
        var delivered = Assert.Single(deliveries.OfType<FbbMessageDelivered>());
        Assert.IsType<FcProposal>(delivered.Proposal);
        // The delivered body is the whole B2 object (spec §3.9) — it decodes.
        B2Message decoded = B2Message.Decode(delivered.Body.Span);
        Assert.Equal("123_GB7BPQ", decoded.Mid);
        Assert.Equal("M0XYZ", decoded.From);
        Assert.Equal(bodyText, Encoding.ASCII.GetString(decoded.Body.Span));
    }

    [Fact]
    public void Inbound_DupFc_HostAnswersMinus_NoTransfer()
    {
        // The host's dup-BID policy answers an FC '-' exactly as it would an FA; the FSM
        // emits FS - and, with an empty queue, reverses into FF (spec §3.4).
        var session = new FbbSession(Config(FbbRole.Answerer, offerB2: true));
        session.Advance(new FbbStart());
        FeedLine(session, B2PeerSid);

        string fc = "FC EM 5_GB7BPQ 200 120 0";
        Assert.Empty(FeedLine(session, fc));
        FeedLine(session, ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([fc])));
        var actions = session.Advance(new FbbProposalDecisions([FsAnswer.AlreadyHave]));
        Assert.Equal(["FS -", "FF"], Lines(actions));
        Assert.Empty(actions.OfType<FbbMessageDelivered>());
    }

    // --- Loopback: B2 caller ↔ B2 answerer, one object each way ---

    [Fact]
    public void Loopback_BothB2_ExchangeOneB2ObjectEachWay()
    {
        var caller = new FbbSession(Config(FbbRole.Caller, offerB2: true), [B2Queued("1_GB7PDN", "from caller\r\n", "Out")]);
        var answerer = new FbbSession(
            new FbbSessionConfig { Role = FbbRole.Answerer, OwnCallsign = "GB7BPQ", SidVersion = "0.1.0", OfferB2 = true },
            [B2Queued("2_GB7BPQ", "from answerer\r\n", "Back")]);

        var delivered = new List<(FbbSession Receiver, FbbMessageDelivered Delivery)>();
        var pending = new Queue<(FbbSession Target, FbbInput Input)>();

        void Dispatch(FbbSession source, FbbSession peer, IReadOnlyList<FbbAction> actions)
        {
            foreach (var action in actions)
            {
                switch (action)
                {
                    case FbbSendLine line:
                        pending.Enqueue((peer, new FbbPeerData(Encoding.ASCII.GetBytes(line.Line + "\r\n"))));
                        break;
                    case FbbSendBytes bytes:
                        pending.Enqueue((peer, new FbbPeerData(bytes.Data)));
                        break;
                    case FbbProposalsReceived proposals:
                        pending.Enqueue((source, new FbbProposalDecisions([.. proposals.Proposals.Select(_ => FsAnswer.Accept)])));
                        break;
                    case FbbMessageDelivered delivery:
                        delivered.Add((source, delivery));
                        break;
                    default:
                        break;
                }
            }
        }

        Dispatch(answerer, caller, answerer.Advance(new FbbStart()));
        Dispatch(caller, answerer, caller.Advance(new FbbStart()));
        var guard = 0;
        while (pending.Count > 0 && guard++ < 200)
        {
            var (target, input) = pending.Dequeue();
            var peer = ReferenceEquals(target, caller) ? answerer : caller;
            Dispatch(target, peer, target.Advance(input));
        }

        Assert.Equal(FbbSessionPhase.Finished, caller.Phase);
        Assert.Equal(FbbSessionPhase.Finished, answerer.Phase);
        Assert.Equal(2, delivered.Count);
        Assert.True(caller.B2Active);
        Assert.True(answerer.B2Active);

        var atAnswerer = Assert.Single(delivered, d => ReferenceEquals(d.Receiver, answerer));
        var atCaller = Assert.Single(delivered, d => ReferenceEquals(d.Receiver, caller));
        Assert.Equal("from caller\r\n", Encoding.ASCII.GetString(B2Message.Decode(atAnswerer.Delivery.Body.Span).Body.Span));
        Assert.Equal("from answerer\r\n", Encoding.ASCII.GetString(B2Message.Decode(atCaller.Delivery.Body.Span).Body.Span));
    }
}
