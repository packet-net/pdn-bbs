using System.Text;

namespace Bbs.Fbb.Tests;

/// <summary>
/// Interoperability tests proving this BBS speaks the FBB forwarding protocol
/// as Jean-Paul Roubelat F6FBB's original <c>FBB</c> BBS implements it — the
/// software the whole protocol is named after. These are wire-transcript
/// conformance tests: they drive the real <see cref="FbbSession"/> state
/// machine and codecs with the exact bytes an FBB BBS puts on (and expects
/// off) the wire, sourced from the official FBB protocol documentation
/// referenced in <c>docs/linbpq-mail-compat.md</c> —
/// [FBB-PROTO] (<c>f6fbb.org/protocole.html</c>), [FBB-APP9]
/// ("Forward protocol", the <c>F&gt; HH</c> checksum + R/E/H responses),
/// [FBB-APP10] ("Compressed forward") and [FBB-SID] (the SID feature table).
///
/// They complement, not replace, the live LinBPQ oracle lane
/// (<c>tests/Bbs.Interop.Tests</c>): LinBPQ is a re-implementation of FBB's
/// forwarding, whereas these pin behaviour against FBB's <em>own</em>
/// dialect — its SID shapes, its <c>FA</c>/<c>FB</c> proposals, its
/// <c>F&gt; HH</c> checksum, its French node banner/prompt, its R: routing
/// headers, and — the crux of interop — its LZHUF compression. The LZHUF
/// golden vectors these reuse from <see cref="Vectors"/> were produced by
/// <c>lzhuf_1.c</c>, the canonical FBB compressor (<c>lzhuf32.c</c> in the FBB
/// tree), so a body we frame is byte-identical to one FBB would transmit and
/// vice versa.
/// </summary>
public class F6fbbInteropTests
{
    /// <summary>A modern F6FBB 7.x SID — A/R/X feature letters are FBB-specific and must be inert ([FBB-SID]).</summary>
    private const string F6fbb7Sid = "[FBB-7.0.8-AB1FHMRX$]";

    /// <summary>Our answering SID.</summary>
    private const string OurSidLine = "[PDN-0.1.0-B1FHM$]";

    private static FbbSessionConfig Config(FbbRole role) => new()
    {
        Role = role,
        OwnCallsign = "GB7PDN",
        SidVersion = "0.1.0",
    };

    /// <summary>A message bound for an F6FBB partner (FC1MVP, homed at the F6FBB BBS in France).</summary>
    private static FbbOutboundMessage Message(string bid, string body, string title) => new()
    {
        MessageType = 'P',
        From = "M0LTE",
        AtBbs = "F6FBB.FFPC.FRA.EU",
        To = "FC1MVP",
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

    /// <summary>One framed compressed transfer exactly as FBB emits it: LZHUF (B1, the e1 container) inside SOH/STX/EOT.</summary>
    private static byte[] FramedB1(string title, string body) =>
        BlockFraming.EncodeMessage(
            title,
            0,
            LzhufContainer.Encode(LzhufContainerKind.B1, Encoding.ASCII.GetBytes(body)));

    private static string Fa(string from, string atbbs, string to, string bid, int size, char type = 'P') =>
        new FaProposal('A', type, from, atbbs, to, bid, size).ToWireLine();

    private static string Terminator(params string[] proposals) =>
        ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum(proposals));

    // --- SID negotiation: the FBB dialects we must talk to ---

    [Theory]
    // Modern F6FBB (7.x) offering the B1 compressed container; A/R/X are FBB-specific and inert.
    [InlineData("[FBB-7.0.8-AB1FHMRX$]", LzhufContainerKind.B1, true, false)]
    [InlineData("[FBB-7.00-B1FHM$]", LzhufContainerKind.B1, true, false)]
    // Earlier F6FBB (5.x) that also speaks B1.
    [InlineData("[FBB-5.15-B1FHM$]", LzhufContainerKind.B1, true, false)]
    // Legacy F6FBB compressed V0 ("B", no digit) — the CRC-less container FBB 5.x originated.
    [InlineData("[FBB-5.11-BFHM$]", LzhufContainerKind.B, false, false)]
    public void F6fbbSid_NegotiatesTheRightContainer(
        string sidLine,
        LzhufContainerKind expectedContainer,
        bool expectsB1,
        bool expectsB2)
    {
        var session = new FbbSession(Config(FbbRole.Answerer));
        session.Advance(new FbbStart());

        Assert.Empty(FeedLine(session, sidLine));

        Assert.Equal("FBB", session.PeerSid!.Author);
        Assert.True(session.PeerSid.SupportsCompression);
        Assert.True(session.PeerSid.SupportsBlockedFbb);
        Assert.True(session.PeerSid.SupportsBid); // every FBB SID ends '$'
        Assert.Equal(expectsB1, session.PeerSid.SupportsB1);
        Assert.Equal(expectsB2, session.PeerSid.SupportsB2);
        Assert.False(session.PeerSid.MentionsBpq); // never trips the BPQ↔BPQ extensions
        Assert.Equal(expectedContainer, session.NegotiatedContainer);

        // An answerer that has parsed the caller's SID is now waiting for the
        // caller's first proposal block (spec §3.1 step 3).
        Assert.Equal(FbbSessionPhase.PeerTurn, session.Phase);
    }

    [Fact]
    public void LegacyF6fbbUncompressedSid_IsDeliberatelyRefused()
    {
        // FBB 5.x can run uncompressed ("[FBB-5.11-FHM$]", F with no B — the
        // ASCII/MBL forward of [FBB-PROTO]'s worked example). Like LinBPQ, this
        // FBB session refuses it (compressed forwarding only); the guard is
        // diagnosed locally and NOT transmitted (spec §3.2). This documents the
        // single deliberate non-interop point with FBB.
        var session = new FbbSession(Config(FbbRole.Answerer));
        session.Advance(new FbbStart());

        var actions = FeedLine(session, "[FBB-5.11-FHM$]");

        Assert.Empty(Lines(actions));
        Assert.Contains(
            "Uncompressed Blocked Forwarding",
            Assert.Single(actions.OfType<FbbProtocolError>()).ErrorLine,
            StringComparison.Ordinal);
        Assert.Equal(FbbSessionPhase.Failed, session.Phase);
    }

    // --- Answerer: an F6FBB BBS dials us and forwards two messages, we reply with one ---

    [Fact]
    public void Answerer_F6fbbForwardsTwo_WeAcceptDecodeAndReplyOne()
    {
        var ourReply = Message("9_GB7PDN", Vectors.BpqMsgText, "Reply de GB7PDN");
        var session = new FbbSession(Config(FbbRole.Answerer), [ourReply]);

        // We answer: SID first, then the de-CALL> prompt (spec §3.1 step 2).
        var start = session.Advance(new FbbStart());
        Assert.Equal([OurSidLine, "de GB7PDN>"], Lines(start));

        // The F6FBB caller answers with its SID; we negotiate the B1 container.
        Assert.Empty(FeedLine(session, F6fbb7Sid));
        Assert.Equal(LzhufContainerKind.B1, session.NegotiatedContainer);

        // F6FBB's proposal block: a personal message for one of our users and a
        // bulletin, both with FBB-style hierarchical @BBS fields and FBB BIDs.
        var personal = Vectors.BpqMsgText;
        const string bulletin = "R:970624/1815z 22@F6FBB.#73.FRA.EU FBB7.00\r\n\r\nBulletin de F6FBB.\r\n";
        var p1 = Fa("FC1GHV", "FC1GHV.FFPC.FRA.EU", "M0LTE", "24657_F6FBB", Vectors.Ascii(personal).Length);
        var p2 = Fa("F6FBB", "FRA", "FBB", "22_F6FBB", Vectors.Ascii(bulletin).Length, type: 'B');

        Assert.Empty(FeedLine(session, p1));
        Assert.Empty(FeedLine(session, p2));

        // The F> checksum F6FBB transmits is the very value our ComputeChecksum
        // produces — proving the checksum is byte-for-byte compatible.
        var block = FeedLine(session, Terminator(p1, p2));
        var received = Assert.IsType<FbbProposalsReceived>(Assert.Single(block));
        Assert.Equal(2, received.Proposals.Count);
        Assert.Equal("24657_F6FBB", Assert.IsType<FaProposal>(received.Proposals[0]).Bid);
        Assert.Equal("FC1GHV.FFPC.FRA.EU", Assert.IsType<FaProposal>(received.Proposals[0]).AtBbs);
        Assert.Equal(FbbSessionPhase.AwaitingDecisions, session.Phase);

        // Accept both -> FS ++, then the two LZHUF transfers arrive (split mid-stream
        // to exercise reassembly across packets).
        Assert.Equal(["FS ++"], Lines(session.Advance(new FbbProposalDecisions([FsAnswer.Accept, FsAnswer.Accept]))));

        var wire = FramedB1("Bonjour de FC1GHV", personal)
            .Concat(FramedB1("DX News", bulletin))
            .ToArray();
        Assert.Empty(FeedBytes(session, wire[..50]));
        var deliveries = FeedBytes(session, wire[50..]);

        var delivered = deliveries.OfType<FbbMessageDelivered>().ToList();
        Assert.Equal(2, delivered.Count);
        Assert.Equal("Bonjour de FC1GHV", delivered[0].Title);
        Assert.Equal(Vectors.Ascii(personal), delivered[0].Body.ToArray());
        Assert.Equal("DX News", delivered[1].Title);
        Assert.Equal(Vectors.Ascii(bulletin), delivered[1].Body.ToArray());

        // The turn reverses: we propose our reply (always with the checksum we send).
        var ourProposal = Fa("M0LTE", "F6FBB.FFPC.FRA.EU", "FC1MVP", "9_GB7PDN", Vectors.Ascii(Vectors.BpqMsgText).Length);
        Assert.Equal([ourProposal, Terminator(ourProposal)], Lines(deliveries));
        Assert.Equal(FbbSessionPhase.AwaitingFs, session.Phase);

        // F6FBB accepts; we ship the framed compressed body (byte-identical to
        // what FBB's own lzhuf would have produced for the same plaintext).
        var sent = FeedLine(session, "FS +");
        Assert.Equal(FsAnswerKind.Accept, Assert.Single(sent.OfType<FbbOutboundResult>()).Answer.Kind);
        Assert.Equal(
            FramedB1("Reply de GB7PDN", Vectors.BpqMsgText),
            Assert.Single(sent.OfType<FbbSendBytes>()).Data.ToArray());

        // F6FBB has nothing more (FF); our queue is empty too -> FQ, clean close.
        var end = FeedLine(session, "FF");
        Assert.Equal(["FQ"], Lines(end));
        Assert.True(Assert.Single(end.OfType<FbbSessionOver>()).Graceful);
        Assert.Equal(FbbSessionPhase.Finished, session.Phase);
    }

    // --- Caller: we dial an F6FBB BBS (its banner + de-CALL> prompt without a terminator) ---

    [Fact]
    public void Caller_DialsF6fbb_SendsOneSkipsOne_ThenReceivesOne()
    {
        var msg1 = Message("123_GB7PDN", Vectors.BpqMsgText, "Msg one");
        var msg2 = Message("124_GB7PDN", "R:260611/0931Z 124@GB7PDN.#23.GBR.EURO PDN0.1\r\n\r\nNumber two.\r\n", "Msg two");
        var session = new FbbSession(Config(FbbRole.Caller), [msg1, msg2]);

        Assert.Empty(session.Advance(new FbbStart()));

        // F6FBB answers: SID, a French node banner, then the prompt — which FBB
        // sends WITHOUT a line terminator (spec §3.16(a)/§1.2). The banner never
        // ends in '>' so it is skipped; the bare-'>' run is the prompt.
        Assert.Empty(FeedLine(session, F6fbb7Sid));
        Assert.Empty(FeedLine(session, "[FBB] Bonjour, vous etes en liaison avec la BBS F6FBB"));
        var turn = session.Advance(new FbbPeerData(Encoding.ASCII.GetBytes("de F6FBB>")));

        // Our SID, then immediately our proposal block (spec §3.1 step 3).
        var p1 = Fa("M0LTE", "F6FBB.FFPC.FRA.EU", "FC1MVP", "123_GB7PDN", Vectors.Ascii(Vectors.BpqMsgText).Length);
        var p2 = Fa("M0LTE", "F6FBB.FFPC.FRA.EU", "FC1MVP", "124_GB7PDN", msg2.Body.Length);
        Assert.Equal([OurSidLine, p1, p2, Terminator(p1, p2)], Lines(turn));

        // F6FBB: send the first, already-has the second (the FBB '-' answer).
        var fs = FeedLine(session, "FS +-");
        var results = fs.OfType<FbbOutboundResult>().ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(FsAnswerKind.Accept, results[0].Answer.Kind);
        Assert.Equal(FsAnswerKind.AlreadyHave, results[1].Answer.Kind);
        Assert.Equal(
            FramedB1("Msg one", Vectors.BpqMsgText),
            Assert.Single(fs.OfType<FbbSendBytes>()).Data.ToArray());

        // The turn reverses: F6FBB proposes one message back to us.
        const string theirBody = "R:970624/1820z 1042@F6FBB.#73.FRA.EU FBB7.00\r\n\r\nMerci, 73 de FC1MVP.\r\n";
        var their = Fa("FC1MVP", "F6FBB.FFPC.FRA.EU", "M0LTE", "1042_F6FBB", Vectors.Ascii(theirBody).Length);
        Assert.Empty(FeedLine(session, their));
        Assert.Single(FeedLine(session, Terminator(their)).OfType<FbbProposalsReceived>());

        Assert.Equal(["FS +"], Lines(session.Advance(new FbbProposalDecisions([FsAnswer.Accept]))));

        var deliveries = FeedBytes(session, FramedB1("Re: hello", theirBody));
        Assert.Equal(Vectors.Ascii(theirBody), Assert.Single(deliveries.OfType<FbbMessageDelivered>()).Body.ToArray());

        // Our queue is empty -> FF; F6FBB closes with FQ.
        Assert.Equal(["FF"], Lines(deliveries));
        Assert.True(Assert.Single(FeedLine(session, "FQ").OfType<FbbSessionOver>()).Graceful);
        Assert.Equal(FbbSessionPhase.Finished, session.Phase);
    }

    // --- The F> HH proposal-block checksum is FBB-compatible both ways ---

    [Fact]
    public void F6fbbProposalChecksum_VerifiesAndMismatchAborts()
    {
        // The two FBB proposal lines from [FBB-PROTO]'s worked example.
        string[] fbbBlock =
        [
            "FB P F6FBB FC1GHV.FFPC.FRA.EU FC1MVP 24657_F6FBB 1345",
            "FB B F6FBB FRA FBB 22_456_F6FBB 8548",
        ];

        // BuildTerminator(ComputeChecksum(...)) is exactly the F> HH line FBB
        // transmits for this block; the value is well-formed two-hex-digit.
        var terminator = Terminator(fbbBlock);
        Assert.Matches("^F> [0-9A-F][0-9A-F]$", terminator);

        // Feeding the matching terminator surfaces the block (checksum verified).
        var ok = new FbbSession(Config(FbbRole.Answerer));
        ok.Advance(new FbbStart());
        FeedLine(ok, F6fbb7Sid);
        Assert.Empty(FeedLine(ok, fbbBlock[0]));
        Assert.Empty(FeedLine(ok, fbbBlock[1]));
        Assert.IsType<FbbProposalsReceived>(Assert.Single(FeedLine(ok, terminator)));

        // A corrupted checksum aborts with the exact FBB/BPQ wire error (spec §3.12).
        var bad = new FbbSession(Config(FbbRole.Answerer));
        bad.Advance(new FbbStart());
        FeedLine(bad, F6fbb7Sid);
        Assert.Empty(FeedLine(bad, fbbBlock[0]));
        var actions = FeedLine(bad, "F> 00");
        Assert.Equal(["*** Proposal Checksum Error"], Lines(actions));
        Assert.Equal(FbbSessionPhase.Failed, bad.Phase);
    }

    // --- FBB's FA/FB proposal-line dialect parses to the right fields ---

    [Fact]
    public void F6fbbProposalLines_Parse()
    {
        // [FBB-PROTO]'s annotated example: FB = ASCII mode, FBB hierarchical @BBS.
        var fb = Assert.IsType<FaProposal>(
            Proposal.Parse("FB P F6FBB FC1GHV.FFPC.FRA.EU FC1MVP 24657_F6FBB 1345"));
        Assert.Equal('B', fb.Verb); // ASCII-mode proposal verb
        Assert.Equal('P', fb.MessageType);
        Assert.Equal("F6FBB", fb.From);
        Assert.Equal("FC1GHV.FFPC.FRA.EU", fb.AtBbs);
        Assert.Equal("FC1MVP", fb.To);
        Assert.Equal("24657_F6FBB", fb.Bid);
        Assert.Equal(1345, fb.Size);
        Assert.False(fb.RequiresPoliteReject);

        // Compressed-mode bulletin proposal (FA), FBB BID form.
        var fa = Assert.IsType<FaProposal>(Proposal.Parse("FA B F6FBB FRA FBB 22_456_F6FBB 8548"));
        Assert.Equal('A', fa.Verb);
        Assert.Equal('B', fa.MessageType);
        Assert.Equal("22_456_F6FBB", fa.Bid);
    }

    // --- FBB's R: routing-header dialect (the @:CALL.HIER [QTH] $:BID form) ---

    [Fact]
    public void F6fbbRoutingHeader_Parses()
    {
        // The classic FBB R: line: @:CALL.hierarchical, a bracketed QTH that may
        // contain spaces, and the $:BID trailer (spec §3.14).
        var r = RLine.TryParse("R:970624/1815z @:F6FBB.#73.FRA.EU [La Rochelle] $:24657_F6FBB");
        Assert.NotNull(r);
        Assert.Equal("F6FBB", r.Callsign);
        Assert.Equal("F6FBB.#73.FRA.EU", r.HierarchicalAddress);
        Assert.Equal("La Rochelle", r.Qth);
        Assert.Equal("24657_F6FBB", r.Bid);
    }

    [Fact]
    public void F6fbbRoutingChain_DetectsOurOwnLoop()
    {
        // A trace that has already transited us twice is a loop (spec §3.14(b)).
        var chain = RLine.ExtractLeadingRLines(
        [
            "R:260611/1130Z 7@GB7PDN.#23.GBR.EURO PDN0.1",
            "R:260611/1030Z 6@F6FBB.#73.FRA.EU FBB7.00",
            "R:260611/0930Z 5@GB7PDN.#23.GBR.EURO PDN0.1",
        ]);
        Assert.True(RLine.IsLikelyLooping(chain, "GB7PDN"));
        Assert.Equal(1, RLine.CountCallsignOccurrences(chain, "F6FBB"));
    }

    // --- The crux of interop: FBB LZHUF is our LZHUF, byte-for-byte ---

    [Fact]
    public void FbbLzhufContainer_IsByteIdentical_BothDirections()
    {
        // Vectors.HelloE1 is the e1 container produced by the canonical FBB
        // compressor (lzhuf_1.c / lzhuf32.c). Our encoder must produce the same
        // bytes (so FBB can decompress what we send) and our decoder must accept
        // FBB's bytes (so we can read what FBB sends). N=2048, F=60, THRESHOLD=2
        // — a stock (N=4096) LZHUF would diverge here.
        var plaintext = Vectors.Ascii(Vectors.HelloText);

        Assert.Equal(Vectors.Hex(Vectors.HelloE1), LzhufContainer.Encode(LzhufContainerKind.B1, plaintext));
        Assert.Equal(plaintext, LzhufContainer.Decode(LzhufContainerKind.B1, Vectors.Hex(Vectors.HelloE1)));
    }

    // --- End-to-end: a body framed exactly as FBB sends it survives a full receive ---

    [Fact]
    public void Answerer_DecodesAnFbbFramedBody_WindowWrapStress()
    {
        // 6840 bytes of repetitive text wraps FBB's N=2048 LZHUF window three
        // times — the case that catches an incompatible compressor. Framed and
        // delivered exactly as F6FBB would put it on the wire.
        var session = new FbbSession(Config(FbbRole.Answerer));
        session.Advance(new FbbStart());
        FeedLine(session, F6fbb7Sid);

        var body = Vectors.WrapText();
        var proposal = "FA B F6FBB F6FBB.FFPC.FRA.EU GB7PDN 30000_F6FBB " + body.Length;
        Assert.Empty(FeedLine(session, proposal));
        Assert.IsType<FbbProposalsReceived>(Assert.Single(FeedLine(session, Terminator(proposal))));

        Assert.Equal(["FS +"], Lines(session.Advance(new FbbProposalDecisions([FsAnswer.Accept]))));

        var framed = BlockFraming.EncodeMessage(
            "Window wrap stress",
            0,
            LzhufContainer.Encode(LzhufContainerKind.B1, body));
        var deliveries = FeedBytes(session, framed);

        var delivered = Assert.Single(deliveries.OfType<FbbMessageDelivered>());
        Assert.Equal("Window wrap stress", delivered.Title);
        Assert.Equal(body, delivered.Body.ToArray());
    }
}
