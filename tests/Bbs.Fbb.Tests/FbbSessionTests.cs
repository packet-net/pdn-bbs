using System.Text;

namespace Bbs.Fbb.Tests;

public class FbbSessionTests
{
    private const string PeerSidLine = "[BPQ-6.0.25.30-B12FWIHJM$]";
    private const string OurSidLine = "[PDN-0.1.0-B1FHM$]";

    private static FbbSessionConfig Config(FbbRole role) => new()
    {
        Role = role,
        OwnCallsign = "GB7PDN",
        SidVersion = "0.1.0",
    };

    private static FbbOutboundMessage Message(string bid, string body, string title = "Test message") => new()
    {
        MessageType = 'P',
        From = "M0LTE",
        AtBbs = "GB7BPQ.#23.GBR.EURO",
        To = "G8BPQ",
        Bid = bid,
        Title = title,
        Body = Encoding.ASCII.GetBytes(body),
    };

    private static IReadOnlyList<FbbAction> FeedLine(FbbSession session, string line) =>
        session.Advance(new FbbPeerData(Encoding.ASCII.GetBytes(line + "\r")));

    private static IReadOnlyList<FbbAction> FeedBytes(FbbSession session, byte[] data) =>
        session.Advance(new FbbPeerData(data));

    private static List<string> Lines(IEnumerable<FbbAction> actions) =>
        [.. actions.OfType<FbbSendLine>().Select(a => a.Line)];

    private static byte[] FramedB1(string title, string body) =>
        BlockFraming.EncodeMessage(
            title,
            0,
            LzhufContainer.Encode(LzhufContainerKind.B1, Encoding.ASCII.GetBytes(body)));

    // --- Transcript: full two-message exchange, answerer role ---

    [Fact]
    public void Answerer_FullTwoMessageExchange()
    {
        var ours = Message("9_GB7PDN", Vectors.BpqMsgText);
        var session = new FbbSession(Config(FbbRole.Answerer), [ours]);

        // We answer the connect: SID first, then a >-terminated prompt.
        var startActions = session.Advance(new FbbStart());
        Assert.Equal([OurSidLine, "de GB7PDN>"], Lines(startActions));
        Assert.Equal(FbbSessionPhase.AwaitingPeerSid, session.Phase);

        // Caller's SID, comment line, then a two-proposal block.
        Assert.Empty(FeedLine(session, PeerSidLine));
        Assert.True(session.PeerSid!.SupportsB1);
        Assert.Equal(LzhufContainerKind.B1, session.NegotiatedContainer);
        Assert.Empty(FeedLine(session, "; WL2K DE GB7BPQ (IO91)")); // comments ignored (spec §3.1)

        string[] proposals =
        [
            "FA P M0LTE GB7BPQ.#23.GBR.EURO G8BPQ 123_GB7PDN 117",
            "FA B G8BPQ GBR PACKET 1043_GB7BPQ 60",
        ];
        Assert.Empty(FeedLine(session, proposals[0]));
        Assert.Empty(FeedLine(session, proposals[1]));
        var terminator = ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum(proposals));
        var blockActions = FeedLine(session, terminator);
        var received = Assert.IsType<FbbProposalsReceived>(Assert.Single(blockActions));
        Assert.Equal(2, received.Proposals.Count);
        Assert.Equal("123_GB7PDN", Assert.IsType<FaProposal>(received.Proposals[0]).Bid);
        Assert.Equal(FbbSessionPhase.AwaitingDecisions, session.Phase);

        // Accept both -> FS ++, then the two bodies arrive (split feeds to
        // exercise reassembly).
        var fsActions = session.Advance(new FbbProposalDecisions([FsAnswer.Accept, FsAnswer.Accept]));
        Assert.Equal(["FS ++"], Lines(fsActions));

        const string body2 = "R:260611/0915Z 44@GB7BPQ.#23.GBR.EURO BPQ6.0.25\r\n\r\nSecond message.\r\n";
        var wire = FramedB1("First", Vectors.BpqMsgText).Concat(FramedB1("Second", body2)).ToArray();
        var firstHalf = wire[..50]; // split inside the first transfer
        var secondHalf = wire[50..];
        Assert.Empty(FeedBytes(session, firstHalf));
        var deliveries = FeedBytes(session, secondHalf);

        var delivered = deliveries.OfType<FbbMessageDelivered>().ToList();
        Assert.Equal(2, delivered.Count);
        Assert.Equal("First", delivered[0].Title);
        Assert.Equal(Vectors.Ascii(Vectors.BpqMsgText), delivered[0].Body.ToArray());
        Assert.Equal("Second", delivered[1].Title);
        Assert.Equal(Vectors.Ascii(body2), delivered[1].Body.ToArray());

        // After the peer's block, the turn reverses: we propose our message.
        var ourProposal = "FA P M0LTE GB7BPQ.#23.GBR.EURO G8BPQ 9_GB7PDN 117";
        var ourTerminator = ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([ourProposal]));
        Assert.Equal([ourProposal, ourTerminator], Lines(deliveries));
        Assert.Equal(FbbSessionPhase.AwaitingFs, session.Phase);

        // Peer accepts; we ship the framed compressed message.
        var sendActions = FeedLine(session, "FS +");
        var result = Assert.Single(sendActions.OfType<FbbOutboundResult>());
        Assert.Equal(FsAnswerKind.Accept, result.Answer.Kind);
        var sent = Assert.Single(sendActions.OfType<FbbSendBytes>());
        Assert.Equal(FramedB1("Test message", Vectors.BpqMsgText), sent.Data.ToArray());

        // Peer has nothing more: FF -> we are empty too -> FQ and done.
        var endActions = FeedLine(session, "FF");
        Assert.Equal(["FQ"], Lines(endActions));
        var over = Assert.Single(endActions.OfType<FbbSessionOver>());
        Assert.True(over.Graceful);
        Assert.Equal(FbbSessionPhase.Finished, session.Phase);
    }

    // --- Transcript: full exchange, caller role ---

    [Fact]
    public void Caller_FullTwoMessageExchange()
    {
        var msg1 = Message("123_GB7PDN", Vectors.BpqMsgText);
        var msg2 = Message("124_GB7PDN", "R:260611/0931Z 124@GB7PDN.#23.GBR.EURO PDN0.1\r\n\r\nNumber two.\r\n");
        var session = new FbbSession(Config(FbbRole.Caller), [msg1, msg2]);

        Assert.Empty(session.Advance(new FbbStart()));

        // Called station: SID, banner, prompt (spec §3.16(a)) — the prompt
        // arrives without a terminator, as FBB sends it.
        Assert.Empty(FeedLine(session, PeerSidLine));
        Assert.Empty(FeedLine(session, "Hello PDN. Latest Message is 41, Last listed is 0"));
        var turnActions = session.Advance(new FbbPeerData(Encoding.ASCII.GetBytes("de GB7BPQ>")));

        // Our SID, then immediately the proposal block (spec §3.1 step 3).
        string[] expectedProposals =
        [
            "FA P M0LTE GB7BPQ.#23.GBR.EURO G8BPQ 123_GB7PDN 117",
            "FA P M0LTE GB7BPQ.#23.GBR.EURO G8BPQ 124_GB7PDN 62",
        ];
        var expectedTerminator = ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum(expectedProposals));
        Assert.Equal([OurSidLine, .. expectedProposals, expectedTerminator], Lines(turnActions));

        // Peer: first accepted, second already there.
        var fsActions = FeedLine(session, "FS +-");
        var results = fsActions.OfType<FbbOutboundResult>().ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(FsAnswerKind.Accept, results[0].Answer.Kind);
        Assert.Equal(FsAnswerKind.AlreadyHave, results[1].Answer.Kind);
        var sent = Assert.Single(fsActions.OfType<FbbSendBytes>());
        Assert.Equal(FramedB1("Test message", Vectors.BpqMsgText), sent.Data.ToArray());

        // The turn reverses: the peer proposes one message back.
        const string theirProposal = "FA P G8BPQ GB7PDN.#23.GBR.EURO M0LTE 1042_GB7BPQ 312";
        Assert.Empty(FeedLine(session, theirProposal));
        var blockActions = FeedLine(
            session,
            ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([theirProposal])));
        Assert.Single(blockActions.OfType<FbbProposalsReceived>());

        Assert.Equal(["FS +"], Lines(session.Advance(new FbbProposalDecisions([FsAnswer.Accept]))));

        const string theirBody = "R:260611/0940Z 1042@GB7BPQ.#23.GBR.EURO BPQ6.0.25\r\n\r\nReply.\r\n";
        var deliveries = FeedBytes(session, FramedB1("Reply", theirBody));
        var delivered = Assert.Single(deliveries.OfType<FbbMessageDelivered>());
        Assert.Equal(Vectors.Ascii(theirBody), delivered.Body.ToArray());

        // Our turn again, queue empty -> FF; peer closes with FQ.
        Assert.Equal(["FF"], Lines(deliveries));
        var endActions = FeedLine(session, "FQ");
        Assert.True(Assert.Single(endActions.OfType<FbbSessionOver>()).Graceful);
        Assert.Equal(FbbSessionPhase.Finished, session.Phase);
    }

    // --- Dup BID and defer answers from the answering side ---

    [Fact]
    public void Answerer_DupBid_AnswersMinus()
    {
        var session = StartedAnswerer(out _);
        FeedProposalBlock(session, "FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN 99");
        var actions = session.Advance(new FbbProposalDecisions([FsAnswer.AlreadyHave]));

        // FS -, no bodies expected, and with an empty queue the turn
        // reverses into our FF.
        Assert.Equal(["FS -", "FF"], Lines(actions));
        Assert.Equal(FbbSessionPhase.PeerTurn, session.Phase);
    }

    [Fact]
    public void Answerer_Defer_AnswersEquals()
    {
        var session = StartedAnswerer(out _);
        FeedProposalBlock(session, "FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN 99");
        var actions = session.Advance(new FbbProposalDecisions([FsAnswer.Defer]));
        Assert.Equal(["FS =", "FF"], Lines(actions));
    }

    [Fact]
    public void Answerer_OversizeTo_IsForcedToMinusDespiteHostAccept()
    {
        // Spec §3.3: a TO >6 chars gets a polite '-', not a protocol error.
        var session = StartedAnswerer(out _);
        FeedProposalBlock(session, "FA P M0LTE GB7BPQ TOOLONGCALL 1_X 10");
        var actions = session.Advance(new FbbProposalDecisions([FsAnswer.Accept]));
        Assert.Equal(["FS -", "FF"], Lines(actions));
    }

    // --- The empty session: FF -> FQ ---

    [Fact]
    public void Answerer_EmptyBothSides_FfBecomesFq()
    {
        var session = StartedAnswerer(out _);
        var actions = FeedLine(session, "FF");
        Assert.Equal(["FQ"], Lines(actions));
        Assert.True(Assert.Single(actions.OfType<FbbSessionOver>()).Graceful);
        Assert.Equal(FbbSessionPhase.Finished, session.Phase);
    }

    [Fact]
    public void Caller_EmptyQueue_OpensWithFf()
    {
        // "a caller with nothing to send opens with FF instead of a proposal
        // block" (spec §3.11).
        var session = new FbbSession(Config(FbbRole.Caller));
        session.Advance(new FbbStart());
        FeedLine(session, PeerSidLine);
        var actions = FeedLine(session, "de GB7BPQ>");
        Assert.Equal([OurSidLine, "FF"], Lines(actions));

        var endActions = FeedLine(session, "FQ");
        Assert.True(Assert.Single(endActions.OfType<FbbSessionOver>()).Graceful);
    }

    // --- Continue-mode (SidAlreadySent): the host already greeted the caller ---

    [Fact]
    public void ContinueModeAnswerer_StartEmitsNothing()
    {
        // The host's greet-immediately flow put our SID (and prompt) on the wire
        // before the FSM exists, so FbbStart emits neither (spec §3.1 step 2 is
        // host-owned in this mode).
        var session = new FbbSession(Config(FbbRole.Answerer) with { SidAlreadySent = true });
        Assert.Empty(session.Advance(new FbbStart()));
        Assert.Equal(FbbSessionPhase.AwaitingPeerSid, session.Phase);
    }

    [Fact]
    public void ContinueModeAnswerer_EmptyBothSides_RunsToGracefulFq()
    {
        // The whole continue-mode happy path: the peeked SID is fed in, then the
        // exchange proceeds exactly as a normal answerer's (FF with empty queues
        // becomes FQ — spec §3.1 step 5).
        var session = new FbbSession(Config(FbbRole.Answerer) with { SidAlreadySent = true });
        Assert.Empty(session.Advance(new FbbStart()));
        Assert.Empty(FeedLine(session, PeerSidLine));
        Assert.Equal(FbbSessionPhase.PeerTurn, session.Phase);

        var actions = FeedLine(session, "FF");
        Assert.Equal(["FQ"], Lines(actions));
        Assert.True(Assert.Single(actions.OfType<FbbSessionOver>()).Graceful);
        Assert.Equal(FbbSessionPhase.Finished, session.Phase);
    }

    [Fact]
    public void ContinueModeCaller_IsRejectedAtConstruction()
    {
        // A caller never pre-sends its SID — it answers the peer's (spec §3.1 step 3).
        Assert.Throws<ArgumentException>(
            () => new FbbSession(Config(FbbRole.Caller) with { SidAlreadySent = true }));
    }

    // --- Checksum failures abort with the exact error lines ---

    [Fact]
    public void Answerer_BadProposalChecksum_Aborts()
    {
        var session = StartedAnswerer(out _);
        Assert.Empty(FeedLine(session, "FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN 99"));
        var actions = FeedLine(session, "F> 00"); // wrong on purpose
        Assert.Equal(["*** Proposal Checksum Error"], Lines(actions));
        Assert.Equal(
            "*** Proposal Checksum Error",
            Assert.Single(actions.OfType<FbbProtocolError>()).ErrorLine);
        Assert.False(Assert.Single(actions.OfType<FbbSessionOver>()).Graceful);
        Assert.Equal(FbbSessionPhase.Failed, session.Phase);
    }

    [Fact]
    public void Answerer_BadEotChecksum_Aborts()
    {
        var session = StartedAnswerer(out _);
        FeedProposalBlock(session, "FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN 117");
        session.Advance(new FbbProposalDecisions([FsAnswer.Accept]));

        var framed = FramedB1("First", Vectors.BpqMsgText);
        framed[^1] ^= 0x01;
        var actions = FeedBytes(session, framed);
        Assert.Equal(["*** Message Checksum Error"], Lines(actions));
        Assert.Equal(FbbSessionPhase.Failed, session.Phase);
    }

    [Fact]
    public void Answerer_CorruptLzhufCrc_AbortsAsChecksumError()
    {
        var session = StartedAnswerer(out _);
        FeedProposalBlock(session, "FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN 117");
        session.Advance(new FbbProposalDecisions([FsAnswer.Accept]));

        // Valid block framing/EOT, corrupt e1 CRC inside.
        var container = LzhufContainer.Encode(LzhufContainerKind.B1, Vectors.Ascii(Vectors.BpqMsgText));
        container[0] ^= 0xFF;
        var actions = FeedBytes(session, BlockFraming.EncodeMessage("First", 0, container));
        Assert.Equal(["*** Message Checksum Error"], Lines(actions));
        Assert.Equal(FbbSessionPhase.Failed, session.Phase);
    }

    // --- The JNOS flavour: bare F> ---

    [Fact]
    public void Answerer_JnosBareTerminator_AcceptsWithoutVerification()
    {
        var session = StartedAnswerer(out var ours, Message("9_GB7PDN", Vectors.BpqMsgText));
        Assert.Empty(FeedLine(session, "FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN 117"));
        var actions = FeedLine(session, "F>"); // no checksum (spec §3.3: JNOS)
        var received = Assert.IsType<FbbProposalsReceived>(Assert.Single(actions));
        Assert.Single(received.Proposals);

        var fsActions = session.Advance(new FbbProposalDecisions([FsAnswer.Accept]));
        Assert.Equal(["FS +"], Lines(fsActions));

        var deliveries = FeedBytes(session, FramedB1("Hi", Vectors.BpqMsgText));
        Assert.Single(deliveries.OfType<FbbMessageDelivered>());

        // Our turn: ours gets proposed, with the checksum we always send.
        var lines = Lines(deliveries);
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("FA P ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("F> ", lines[1], StringComparison.Ordinal);
        Assert.NotNull(ours);
    }

    // --- FS answer tolerances when we are the proposer ---

    [Theory]
    [InlineData("FS Y", FsAnswerKind.Accept, true)]
    [InlineData("FS H", FsAnswerKind.Defer, false)] // H = defer, never send (spec §3.4)
    [InlineData("FS R", FsAnswerKind.Reject, false)]
    [InlineData("FS E", FsAnswerKind.ProposalError, false)]
    [InlineData("FS N", FsAnswerKind.AlreadyHave, false)]
    public void Proposer_FsAlphabet_MapsToDispositions(string fsLine, FsAnswerKind expected, bool sends)
    {
        var session = ProposingAnswerer();
        var actions = FeedLine(session, fsLine);
        var result = Assert.Single(actions.OfType<FbbOutboundResult>());
        Assert.Equal(expected, result.Answer.Kind);
        Assert.Equal(sends, actions.OfType<FbbSendBytes>().Any());
        Assert.Equal(FbbSessionPhase.PeerTurn, session.Phase);
    }

    [Fact]
    public void Proposer_ResumeOffset_IsHonoured()
    {
        // Spec §3.8: "resend bytes 0-5 then from n+6".
        var body = Encoding.ASCII.GetString(Vectors.WrapText());
        var session = ProposingAnswerer(Message("9_GB7PDN", body, "Big"));
        var actions = FeedLine(session, "FS !100");

        var compressed = LzhufContainer.Encode(LzhufContainerKind.B1, Vectors.WrapText());
        Assert.True(compressed.Length > 106);
        var expectedPayload = new byte[compressed.Length - 100];
        compressed.AsSpan(0, 6).CopyTo(expectedPayload);
        compressed.AsSpan(106).CopyTo(expectedPayload.AsSpan(6));

        var sent = Assert.Single(actions.OfType<FbbSendBytes>());
        Assert.Equal(BlockFraming.EncodeMessage("Big", 100, expectedPayload), sent.Data.ToArray());
    }

    [Fact]
    public void Proposer_FsAnswerCountMismatch_AbortsWithBpqErrorLine()
    {
        var session = ProposingAnswerer();
        var actions = FeedLine(session, "FS ++");
        Assert.Equal(["*** Protocol Error - Invalid Proposal Response'"], Lines(actions));
        Assert.Equal(FbbSessionPhase.Failed, session.Phase);
    }

    // --- SID negotiation edges ---

    [Fact]
    public void CompressionGuard_FWithoutB_FailsWithoutSendingAnything()
    {
        // Spec §3.2: LinBPQ logs (does not send) the guard text and drops.
        var session = StartedAnswererRaw();
        var actions = FeedLine(session, "[OLD-1.0-FHM$]");
        Assert.Empty(Lines(actions));
        var error = Assert.Single(actions.OfType<FbbProtocolError>());
        Assert.Contains("Uncompressed Blocked Forwarding", error.ErrorLine, StringComparison.Ordinal);
        Assert.Equal(FbbSessionPhase.Failed, session.Phase);
    }

    [Fact]
    public void MblOnlySid_IsRefusedByThisFbbSession()
    {
        var session = StartedAnswererRaw();
        var actions = FeedLine(session, "[BPQ-6.0.25.30-IHM$]");
        Assert.Single(actions.OfType<FbbProtocolError>());
        Assert.Equal(FbbSessionPhase.Failed, session.Phase);
    }

    [Fact]
    public void B0Peer_NegotiatesTheCrcLessContainer()
    {
        var session = StartedAnswererRaw();
        Assert.Empty(FeedLine(session, "[FBB-5.15-BFHM$]"));
        Assert.Equal(LzhufContainerKind.B, session.NegotiatedContainer);

        FeedProposalBlock(session, "FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN 117");
        session.Advance(new FbbProposalDecisions([FsAnswer.Accept]));
        var framed = BlockFraming.EncodeMessage(
            "V0",
            0,
            LzhufContainer.Encode(LzhufContainerKind.B, Vectors.Ascii(Vectors.BpqMsgText)));
        var deliveries = FeedBytes(session, framed);
        var delivered = Assert.Single(deliveries.OfType<FbbMessageDelivered>());
        Assert.Equal(Vectors.Ascii(Vectors.BpqMsgText), delivered.Body.ToArray());
    }

    // --- Fatal inbound conditions ---

    [Fact]
    public void InboundStarsLine_IsFatal()
    {
        var session = StartedAnswerer(out _);
        var actions = FeedLine(session, "*** Protocol Error - Invalid Proposal Response'");
        Assert.Empty(Lines(actions)); // peer-reported: nothing transmitted back
        Assert.Equal(
            "*** Protocol Error - Invalid Proposal Response'",
            Assert.Single(actions.OfType<FbbProtocolError>()).ErrorLine);
        Assert.False(Assert.Single(actions.OfType<FbbSessionOver>()).Graceful);
    }

    [Fact]
    public void SixthProposalInABlock_IsRefused()
    {
        // Spec §3.13.5 [VERIFY-ORACLE #14]: BPQ allocates exactly 5 slots.
        var session = StartedAnswerer(out _);
        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(FeedLine(session, $"FA P M0LTE GB7BPQ G8BPQ {i}_GB7PDN 10"));
        }

        var actions = FeedLine(session, "FA P M0LTE GB7BPQ G8BPQ 5_GB7PDN 10");
        Assert.Equal(["*** Protocol Error - Too Many Proposals"], Lines(actions));
        Assert.Equal(FbbSessionPhase.Failed, session.Phase);
    }

    [Fact]
    public void MalformedProposal_IsSessionFatal()
    {
        var session = StartedAnswerer(out _);
        var actions = FeedLine(session, "FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN"); // 6 fields
        Assert.Equal(["*** Protocol Error - Invalid Proposal"], Lines(actions));
        Assert.Equal(FbbSessionPhase.Failed, session.Phase);
    }

    // --- Block building limits when proposing ---

    [Fact]
    public void Proposer_CapsBlocksAtFiveProposals()
    {
        var messages = Enumerable.Range(0, 7)
            .Select(i => Message($"{i}_GB7PDN", $"Body number {i}\r\n"))
            .ToArray();
        var session = new FbbSession(Config(FbbRole.Answerer), messages);
        session.Advance(new FbbStart());
        FeedLine(session, PeerSidLine);

        // Peer opens with FF; our first block must hold exactly 5 proposals.
        var firstBlock = Lines(FeedLine(session, "FF"));
        Assert.Equal(6, firstBlock.Count); // 5 × FA + F>
        Assert.All(firstBlock[..5], l => Assert.StartsWith("FA ", l, StringComparison.Ordinal));
        Assert.StartsWith("F> ", firstBlock[5], StringComparison.Ordinal);

        // Accept all 5, peer FFs again, the remaining 2 follow.
        var sendActions = FeedLine(session, "FS +++++");
        Assert.Equal(5, sendActions.OfType<FbbSendBytes>().Count());
        var secondBlock = Lines(FeedLine(session, "FF"));
        Assert.Equal(3, secondBlock.Count); // 2 × FA + F>
    }

    [Fact]
    public void Proposer_RespectsMaxBlockBytes()
    {
        // 3 × 4000-byte messages with a 10000-byte cap -> 2 in the first
        // block (spec §3.3 MaxFBBBlock).
        var big = new string('x', 4000);
        var messages = Enumerable.Range(0, 3).Select(i => Message($"{i}_GB7PDN", big)).ToArray();
        var config = Config(FbbRole.Answerer) with { MaxBlockBytes = 10000 };
        var session = new FbbSession(config, messages);
        session.Advance(new FbbStart());
        FeedLine(session, PeerSidLine);

        var block = Lines(FeedLine(session, "FF"));
        Assert.Equal(3, block.Count); // 2 × FA + F>
    }

    // --- Input sequencing guards ---

    [Fact]
    public void DecisionsWithoutPendingProposals_IsAHostBug()
    {
        var session = StartedAnswerer(out _);
        Assert.Throws<InvalidOperationException>(
            () => session.Advance(new FbbProposalDecisions([FsAnswer.Accept])));
    }

    [Fact]
    public void WrongDecisionCount_IsAHostBug()
    {
        var session = StartedAnswerer(out _);
        FeedProposalBlock(session, "FA P M0LTE GB7BPQ G8BPQ 123_GB7PDN 99");
        Assert.Throws<InvalidOperationException>(
            () => session.Advance(new FbbProposalDecisions([FsAnswer.Accept, FsAnswer.Accept])));
    }

    [Fact]
    public void StartingTwice_IsAHostBug()
    {
        var session = new FbbSession(Config(FbbRole.Caller));
        session.Advance(new FbbStart());
        Assert.Throws<InvalidOperationException>(() => session.Advance(new FbbStart()));
    }

    // --- Loopback: both roles of this FSM interoperate end-to-end ---

    [Fact]
    public void Loopback_CallerAndAnswerer_ExchangeOneMessageEachWay()
    {
        const string callerBody = "R:260611/0930Z 1@GB7PDN.#23.GBR.EURO PDN0.1\r\n\r\nFrom the caller.\r\n";
        const string answererBody = "R:260611/0931Z 2@GB7BPQ.#23.GBR.EURO BPQ6.0.25\r\n\r\nFrom the answerer.\r\n";
        var caller = new FbbSession(Config(FbbRole.Caller), [Message("1_GB7PDN", callerBody, "Out")]);
        var answerer = new FbbSession(
            new FbbSessionConfig { Role = FbbRole.Answerer, OwnCallsign = "GB7BPQ", SidVersion = "0.1.0" },
            [Message("2_GB7BPQ", answererBody, "Back")]);

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
                        pending.Enqueue((
                            source,
                            new FbbProposalDecisions([.. proposals.Proposals.Select(_ => FsAnswer.Accept)])));
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
        var atAnswerer = Assert.Single(delivered, d => ReferenceEquals(d.Receiver, answerer));
        var atCaller = Assert.Single(delivered, d => ReferenceEquals(d.Receiver, caller));
        Assert.Equal(Vectors.Ascii(callerBody), atAnswerer.Delivery.Body.ToArray());
        Assert.Equal("Out", atAnswerer.Delivery.Title);
        Assert.Equal(Vectors.Ascii(answererBody), atCaller.Delivery.Body.ToArray());
        Assert.Equal("Back", atCaller.Delivery.Title);
    }

    // --- Helpers ---

    private static FbbSession StartedAnswererRaw()
    {
        var session = new FbbSession(Config(FbbRole.Answerer));
        session.Advance(new FbbStart());
        return session;
    }

    private static FbbSession StartedAnswerer(out FbbOutboundMessage? queued, FbbOutboundMessage? message = null)
    {
        queued = message;
        var session = new FbbSession(
            Config(FbbRole.Answerer),
            message is null ? [] : [message]);
        session.Advance(new FbbStart());
        FeedLine(session, PeerSidLine);
        return session;
    }

    private static FbbSession ProposingAnswerer(FbbOutboundMessage? message = null)
    {
        var session = new FbbSession(
            Config(FbbRole.Answerer),
            [message ?? Message("9_GB7PDN", Vectors.BpqMsgText)]);
        session.Advance(new FbbStart());
        FeedLine(session, PeerSidLine);
        FeedLine(session, "FF"); // peer passes the turn; we propose
        Assert.Equal(FbbSessionPhase.AwaitingFs, session.Phase);
        return session;
    }

    private static void FeedProposalBlock(FbbSession session, params string[] proposalLines)
    {
        foreach (var line in proposalLines)
        {
            Assert.Empty(FeedLine(session, line));
        }

        var actions = FeedLine(
            session,
            ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum(proposalLines)));
        Assert.Single(actions.OfType<FbbProposalsReceived>());
    }
}
