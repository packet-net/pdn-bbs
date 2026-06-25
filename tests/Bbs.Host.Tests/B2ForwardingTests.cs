using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Forwarding;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Host.Tests;

/// <summary>
/// The host wiring of B2F (FC) forwarding — spec §3.3/§3.9, gated per-partner on
/// <see cref="Partner.AllowB2F"/> (default FALSE ⇒ today's B1 behaviour unchanged). B2 is
/// active for a session iff the partner is AllowB2F AND its SID advertises B2. Covers the
/// negotiation matrix on the wire, an outbound B2 cycle (FC EM → FS + → B2 object), an
/// inbound B2 cycle (FC accept → decode → store), the guard that a non-AllowB2 partner's FC
/// is still refused, and that the local-delivery + auto-create rules reach B2 inbound.
/// </summary>
public class B2ForwardingTests
{
    private const string B2PeerSid = "[BPQ-6.0.25.30-B12FWIHJM$]"; // advertises B2
    private const string B1PeerSid = "[BPQ-6.0.24.44-B1FHM$]";     // B1 only
    private const string OurB1Sid = "[PDN-0.1.0-B1FHM$]";
    private const string OurB2Sid = "[PDN-0.1.0-B12FHM$]";

    // --- Direct-receiver B2 inbound: decode + store + the home-BBS rules ---

    /// <summary>Builds an inbound B2 delivery (object + matching FC proposal) and feeds it through the real receiver.</summary>
    private static Message DeliverB2(
        HostHarness host,
        string mid,
        string to,
        string bodyText,
        string from = "M0XYZ",
        string subject = "B2 subject",
        B2MessageType type = B2MessageType.Private,
        string fromPartner = "GB7BPQ")
    {
        byte[] obj = new B2Message
        {
            Mid = mid,
            Type = type,
            From = from,
            To = [to],
            Subject = subject,
            Date = "2026/06/11 10:00",
            Mbo = "GB7BPQ",
            Body = Encoding.ASCII.GetBytes(bodyText),
        }.Encode();
        int csize = LzhufContainer.Encode(LzhufContainerKind.B1, obj).Length;
        var proposal = new FcProposal(FcType.Em, mid, obj.Length, csize);
        var delivered = new FbbMessageDelivered(proposal, subject, obj);
        return host.Receiver.Deliver(delivered, fromPartner)!;
    }

    [Fact]
    public async Task InboundB2_Decoded_StoresFromToSubjectBodyAndBid()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AllowB2F = true });

        const string bodyText = "B2 message body line one.\r\nLine two.\r\n";
        Message stored = DeliverB2(host, "123_GB7BPQ", "G4XYZ@GB7BSK", bodyText, from: "M0XYZ", subject: "Hello via B2");

        Assert.Equal(MessageType.Personal, stored.Type);
        Assert.Equal("M0XYZ", stored.From);
        Assert.Equal("G4XYZ", Assert.Single(stored.Recipients).ToCall);
        Assert.Equal("123_GB7BPQ", stored.Bid); // MID becomes the BID
        Assert.Equal("Hello via B2", stored.Subject);
        Assert.Equal(bodyText, stored.GetBodyText()); // the B2 Body part is stored verbatim
        Assert.Equal("GB7BPQ", stored.ReceivedFrom);

        // The BID is recorded with arrival direction (routing loop-guard input), as for B1.
        BidRecord? bid = host.Store.LookupBid("123_GB7BPQ");
        Assert.NotNull(bid);
        Assert.Equal("GB7BPQ", bid.FirstSeenFrom);
    }

    [Fact]
    public async Task InboundB2_HomedPersonal_StaysLocal_AndAutoCreatesUser()
    {
        // The home-BBS rules (design.md rules #1/#2) must reach the B2 inbound path: a personal
        // whose To is homed here (To: <call>@<us>) auto-creates the skeletal user and does NOT
        // leak onward to a wildcard partner.
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AllowB2F = true });
        host.Store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });
        Assert.False(host.Store.UserExists("G0NEW"));

        Message stored = DeliverB2(host, "9_GB7BPQ", $"G0NEW@{HostHarness.OwnCall}.{HostHarness.HRoute}", "Your mail.\r\n");

        Assert.True(host.Store.UserExists("G0NEW")); // rule #2: auto-created
        Assert.Single(host.Store.ListMessages(new MessageQuery { ToCall = "G0NEW" }));
        // rule #1: homed here → zero forward targets, no leak to the wildcard partner.
        Assert.Empty(host.Store.GetForwardQueue("GB7RDG"));
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal("G0NEW", Assert.Single(stored.Recipients).ToCall);
    }

    [Fact]
    public async Task InboundB2_RemoteAddressed_RoutesOnward()
    {
        // Companion: a genuinely-remote B2 personal still forwards onward (the local-first rule
        // doesn't block legitimate transit).
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AllowB2F = true });
        host.Store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });

        Message stored = DeliverB2(host, "10_GB7BPQ", "G4XYZ@GB7BSK", "Onward.\r\n");

        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7RDG")).Number);
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
    }

    // --- Negotiation matrix on the wire (inbound answerer) ---

    [Fact]
    public async Task Negotiation_AllowB2False_AnswersWithB1Sid_EvenToB2Partner()
    {
        // The default (AllowB2F unset): we answer a B2-capable partner with a B1-only SID —
        // the B1 regression is byte-for-byte unchanged.
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ" }); // AllowB2F defaults false
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(B2PeerSid);
        Assert.Equal(OurB1Sid, await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());
        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task Negotiation_AllowB2True_B2Partner_AnswersWithB2Sid()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AllowB2F = true });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(B2PeerSid);
        Assert.Equal(OurB2Sid, await peer.ReadLineAsync()); // we advertise '2'
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());
        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task Negotiation_AllowB2True_B1OnlyPartner_FallsBackToFaB1()
    {
        // We allow B2 but the partner is B1-only → the SID intersection drops B2, and a queued
        // message is proposed as FA (B1), not FC.
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AllowB2F = true, AtCalls = ["*"] });

        Message queued = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "Fallback",
            Body = Encoding.Latin1.GetBytes("B1 fallback body.\r"),
        });
        host.Routing.RouteMessage(queued);
        Assert.Single(host.Store.GetForwardQueue("GB7BPQ"));

        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(B1PeerSid);
        Assert.Equal(OurB2Sid, await peer.ReadLineAsync()); // we still advertise '2'; the peer doesn't take it
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());
        await peer.SendLineAsync("FF");

        // FA, not FC — the B1 fallback.
        string proposal = await peer.ReadLineAsync();
        Assert.StartsWith("FA P M0LTE GB7BPQ G8ABC 1_GB7PDN ", proposal, StringComparison.Ordinal);
    }

    // --- Inbound FC from a NON-AllowB2 partner: still refused with '-' (guard intact) ---

    [Fact]
    public async Task Inbound_FcFromNonAllowB2Partner_IsStillRefusedWithMinus()
    {
        // We never advertised B2 to this partner; if it sends FC anyway, the guard refuses it
        // with '-' (InboundMessageReceiver: never advertised → never legitimately offered).
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ" }); // AllowB2F false
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(B2PeerSid);
        await peer.ReadLineAsync(); // SID (B1 — we didn't advertise B2)
        await peer.ReadLineAsync(); // prompt

        string fc = "FC EM 7_GB7BPQ 300 180 0";
        await peer.SendLineAsync(fc);
        await peer.SendLineAsync(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([fc])));

        Assert.Equal("FS -", await peer.ReadLineAsync());
        Assert.Equal("FF", await peer.ReadLineAsync());
        await peer.SendLineAsync("FQ");
        await peer.WaitForHostCloseAsync();
        Assert.Empty(host.Store.ListMessages(new MessageQuery()));
    }

    [Fact]
    public async Task Inbound_DuplicateFcBid_FromAllowB2Partner_RefusedWithMinus()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AllowB2F = true });
        host.Store.RecordBid("55_GB7BPQ");
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(B2PeerSid);
        await peer.ReadLineAsync(); // SID (B2)
        await peer.ReadLineAsync(); // prompt

        string fc = "FC EM 55_GB7BPQ 300 180 0";
        await peer.SendLineAsync(fc);
        await peer.SendLineAsync(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([fc])));

        Assert.Equal("FS -", await peer.ReadLineAsync()); // known BID → '-' (spec §4.3/§2.3)
        Assert.Equal("FF", await peer.ReadLineAsync());
        await peer.SendLineAsync("FQ");
        await peer.WaitForHostCloseAsync();
        Assert.Empty(host.Store.ListMessages(new MessageQuery()));
    }

    // --- Full outbound B2 cycle: FC EM proposal → FS + → B2 object transfer → MarkForwarded ---

    [Fact]
    public async Task FullOutboundB2Cycle_ProposesFcEm_TransfersB2Object_MarksForwarded()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AllowB2F = true,
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ-1" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "B2 hello",
            Body = Encoding.Latin1.GetBytes("Local body over B2.\r"),
        });
        host.Routing.RouteMessage(stored);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await peer.SendLineAsync(B2PeerSid);
        await peer.SendTextAsync("de GB7BPQ>\r");

        Assert.Equal(OurB2Sid, await peer.ReadLineAsync()); // our SID advertises '2'
        string fc = await peer.ReadLineAsync();
        Assert.StartsWith("FC EM 1_GB7PDN ", fc, StringComparison.Ordinal); // uniform FC EM with the BID as MID
        string terminator = await peer.ReadLineAsync();
        Assert.True(ProposalBlock.TryParseTerminator(terminator, out byte? checksum));
        Assert.Equal(ProposalBlock.ComputeChecksum([fc]), checksum);

        // The FC sizes: uncompressed B2 object length and compressed length, both > 0.
        string[] fields = fc.Split(' ');
        int usize = int.Parse(fields[3], CultureInfo.InvariantCulture);
        int csize = int.Parse(fields[4], CultureInfo.InvariantCulture);
        Assert.True(usize > 0 && csize > 0);

        await peer.SendLineAsync("FS +");

        var reader = new FbbBlockReader();
        byte[] leftover = await InboundForwardingTests.ReadOneTransferAsync(peer, reader);
        peer.PushBackForLines(leftover);
        byte[] obj = LzhufContainer.Decode(LzhufContainerKind.B1, reader.Payload.Span);
        Assert.Equal(usize, obj.Length); // the FC usize matched the object

        // The transferred bytes decode to a B2 message carrying the stored mail.
        B2Message decoded = B2Message.Decode(obj);
        Assert.Equal("1_GB7PDN", decoded.Mid);
        Assert.Equal("M0LTE", decoded.From);
        Assert.Equal("G8ABC", Assert.Single(decoded.To));
        Assert.Equal("B2 hello", decoded.Subject);
        Assert.Equal("GB7PDN", decoded.Mbo); // our BBS call
        Assert.Equal(B2MessageType.Private, decoded.Type);
        // The B2 Body carries the same plaintext B1 would (R: line + first-hop blank + body).
        string b2Body = Encoding.Latin1.GetString(decoded.Body.Span);
        Assert.Equal("R:260611/1200Z 1@GB7PDN.#23.GBR.EURO PDN0.1.0\r\n\r\nLocal body over B2.\r", b2Body);

        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();

        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);
    }

    // --- Teardown race: the caller's closing FQ vs a peer that drops on its FF ---

    [Fact]
    public async Task OutboundB2Cycle_PeerDropsLinkOnItsFf_StillGracefulAndForwarded()
    {
        // The live GB7RDG B2 go-live (2026-06-12) surfaced this: a real LinBPQ that has
        // FS-accepted and received our message drops the AX.25 link the instant it sends its
        // closing FF — before our courtesy FQ reaches the wire (spec §3.1 step 5, "the side
        // receiving FQ disconnects", taken eagerly). The node then refuses our FQ ("17 Not
        // connected"). That MUST NOT turn a fully delivered cycle into a failure + backoff
        // retry: every message was delivered and FS-resolved, so the close is graceful. The
        // fix is protocol-agnostic; exercised here on the B2 path that surfaced it.
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AllowB2F = true,
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ-1" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();

        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "B2 teardown",
            Body = Encoding.Latin1.GetBytes("B2 body, peer hangs up on its FF.\r"),
        });
        host.Routing.RouteMessage(stored);

        Partner partner = host.Store.GetPartner("GB7BPQ")!;
        IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
            host.Store.GetForwardQueue("GB7BPQ"), partner, host.Identity, host.Time, NullLogger.Instance);

        // Drive the caller directly so we can assert the session's graceful verdict (the
        // scheduler would swallow the difference — the message is MarkForwarded either way).
        RhpChildConnection child = await host.Link.OpenAsync("GB7BPQ-1", null, host.Token);
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Task<FbbSessionResult> run = host.Runner.RunCallerAsync(child, partner, outbound, host.Token);

        await peer.SendLineAsync(B2PeerSid);
        await peer.SendTextAsync("de GB7BPQ>\r");
        Assert.Equal(OurB2Sid, await peer.ReadLineAsync());
        Assert.StartsWith("FC EM ", await peer.ReadLineAsync(), StringComparison.Ordinal);
        await peer.ReadLineAsync(); // F> checksum terminator

        await peer.SendLineAsync("FS +");
        var reader = new FbbBlockReader();
        byte[] leftover = await InboundForwardingTests.ReadOneTransferAsync(peer, reader);
        peer.PushBackForLines(leftover);

        // The peer drops the link, THEN sends its closing FF: the host reacts to FF by sending
        // its FQ, and that send is refused ("Not connected") because the far end is already gone.
        peer.MarkDisconnected();
        await peer.SendLineAsync("FF");

        FbbSessionResult result = await run.WaitAsync(TestTimeout.Default);
        Assert.True(result.Completed);
        Assert.True(result.Graceful); // the dropped closing FQ is not a cycle failure
        Assert.True(result.B2Active); // the peer advertised B2 and we offered it (the observability fields)
        Assert.Equal(B2PeerSid, result.PeerSidRaw);
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);
    }

    [Fact]
    public async Task B2Session_LogsNegotiatedMode_AndPeerSid_InTemplateOrder()
    {
        // Regression for the observability log line: the LogNegotiated delegate's arguments MUST be
        // passed in the message template's placeholder order — {Partner}, {Mode}, {PeerSid}. A swap
        // (peerSid where mode was expected) rendered the misleading "negotiated <SID> (peer SID B2)",
        // which the lab caught but no test did. Drive a real B2 caller cycle and assert the rendered
        // line reads "... negotiated B2 (peer SID <raw>)".
        var capture = new CapturingLogger<FbbSessionRunner>();
        await using var host = new HostHarness(runnerLogger: capture);
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AllowB2F = true,
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ-1" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();

        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "B2 log",
            Body = Encoding.Latin1.GetBytes("body for the negotiated-log test.\r"),
        });
        host.Routing.RouteMessage(stored);

        Partner partner = host.Store.GetPartner("GB7BPQ")!;
        IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
            host.Store.GetForwardQueue("GB7BPQ"), partner, host.Identity, host.Time, NullLogger.Instance);

        RhpChildConnection child = await host.Link.OpenAsync("GB7BPQ-1", null, host.Token);
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Task<FbbSessionResult> run = host.Runner.RunCallerAsync(child, partner, outbound, host.Token);

        await peer.SendLineAsync(B2PeerSid);
        await peer.SendTextAsync("de GB7BPQ>\r");
        Assert.Equal(OurB2Sid, await peer.ReadLineAsync());
        Assert.StartsWith("FC EM ", await peer.ReadLineAsync(), StringComparison.Ordinal);
        await peer.ReadLineAsync(); // F> checksum terminator

        await peer.SendLineAsync("FS +");
        var reader = new FbbBlockReader();
        byte[] leftover = await InboundForwardingTests.ReadOneTransferAsync(peer, reader);
        peer.PushBackForLines(leftover);

        peer.MarkDisconnected();
        await peer.SendLineAsync("FF");

        FbbSessionResult result = await run.WaitAsync(TestTimeout.Default);
        Assert.True(result.B2Active);

        string? negotiated = null;
        int count = 0;
        foreach (string m in capture.Messages)
        {
            if (m.Contains("negotiated", StringComparison.Ordinal))
            {
                negotiated = m;
                count++;
            }
        }

        Assert.Equal(1, count); // latched: exactly one negotiated line per session
        Assert.Equal($"Forwarding session with GB7BPQ negotiated B2 (peer SID {B2PeerSid})", negotiated);
    }

    /// <summary>Captures rendered log messages so a test can assert the formatted output (e.g. the
    /// placeholder order of a <see cref="Microsoft.Extensions.Logging.LoggerMessage"/> delegate).</summary>
    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task OutboundB2Cycle_LinkDropsBeforeBody_LeavesMessageQueued_NotForwarded()
    {
        // The live GB7RDG B2 go-live (2026-06-12) also surfaced this data-loss class: the node
        // reported the AX.25 session gone ("17 Not connected") when pdn went to push a body that
        // spanned more than one frame over the half-duplex link (the node-side multi-frame TX is a
        // separate bug). pdn USED to clear the queue entry on the FS-accept — BEFORE the body was
        // sent — so the message was lost (marked Forwarded, never delivered, never retried). The
        // body transfer now precedes its result: a failed body send aborts the action loop before
        // the mark, so the message stays queued for the next cycle.
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AllowB2F = true,
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ-1" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();

        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "B2 lost body",
            Body = Encoding.Latin1.GetBytes("B2 body the link drops before we can send.\r"),
        });
        host.Routing.RouteMessage(stored);

        Partner partner = host.Store.GetPartner("GB7BPQ")!;
        IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
            host.Store.GetForwardQueue("GB7BPQ"), partner, host.Identity, host.Time, NullLogger.Instance);

        RhpChildConnection child = await host.Link.OpenAsync("GB7BPQ-1", null, host.Token);
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Task<FbbSessionResult> run = host.Runner.RunCallerAsync(child, partner, outbound, host.Token);

        await peer.SendLineAsync(B2PeerSid);
        await peer.SendTextAsync("de GB7BPQ>\r");
        Assert.Equal(OurB2Sid, await peer.ReadLineAsync());
        Assert.StartsWith("FC EM ", await peer.ReadLineAsync(), StringComparison.Ordinal);
        await peer.ReadLineAsync(); // F> terminator

        // The far end drops BEFORE pdn sends the body: the accept arrives, but the body push fails.
        peer.MarkDisconnected();
        await peer.SendLineAsync("FS +");

        // The body send fails and surfaces (not swallowed — it is not a terminal-phase close).
        await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TestTimeout.Default));

        // The message was NOT cleared: it stays queued for the next cycle, not silently dropped.
        Assert.Single(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.NotEqual(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);
    }

    // --- B2 completeness: multi-recipient (To/Cc) + File: attachments, both directions ---

    [Fact]
    public async Task InboundB2_MultiRecipientAndAttachments_StoresAllToCcAndFiles()
    {
        // A B2 object with 2 To + 1 Cc + 2 File: parts: ALL recipients land (To/Cc flagged) and
        // BOTH attachments are stored byte-exact. Routing runs once on the primary To (G4XYZ).
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AllowB2F = true });
        host.Store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });

        byte[] file1 = [0x01, 0x02, 0x03, 0xFF, 0x00, 0x80];
        byte[] file2 = Encoding.Latin1.GetBytes("second attachment\r\n");
        byte[] obj = new B2Message
        {
            Mid = "300_GB7BPQ",
            Type = B2MessageType.Private,
            From = "M0XYZ",
            To = ["G4XYZ@GB7BSK", "G8ABC@GB7BSK"],
            Cc = ["M0CCC@GB7BSK"],
            Subject = "multi + files",
            Mbo = "GB7BPQ",
            Body = Encoding.ASCII.GetBytes("Body for several.\r\n"),
            Files =
            [
                new B2Attachment("PHOTO.JPG", file1),
                new B2Attachment("readme.txt", file2),
            ],
        }.Encode();
        int csize = LzhufContainer.Encode(LzhufContainerKind.B1, obj).Length;
        var proposal = new FcProposal(FcType.Em, "300_GB7BPQ", obj.Length, csize);
        Message stored = host.Receiver.Deliver(new FbbMessageDelivered(proposal, "multi + files", obj), "GB7BPQ")!;

        // Both To-recipients stored (cc=false), the Cc stored (cc=true).
        Assert.Equal(["G4XYZ", "G8ABC"], stored.Recipients.Where(r => !r.Cc).Select(r => r.ToCall).Order());
        MessageRecipient cc = Assert.Single(stored.Recipients, r => r.Cc);
        Assert.Equal("M0CCC", cc.ToCall);

        // Both attachments stored byte-exact, in wire order.
        Assert.Equal(2, stored.Attachments.Count);
        Assert.Equal("PHOTO.JPG", stored.Attachments[0].Name);
        Assert.Equal(file1, stored.Attachments[0].Content.ToArray());
        Assert.Equal("readme.txt", stored.Attachments[1].Name);
        Assert.Equal(file2, stored.Attachments[1].Content.ToArray());

        // F-1 deferral: routed ONCE on the primary To's route (full To: list rides the one hop).
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7RDG")).Number);
    }

    [Fact]
    public async Task InboundB2_CcRecipient_DoesNotCreateItsOwnForwardTarget()
    {
        // A Cc is informational: it is stored, but it must NEVER drive an independent forward
        // target. Here the To homes on GB7RDG and the Cc homes on GB7CIP — only GB7RDG must be
        // queued. (Cc-routing would leak the message to GB7CIP, which a real LinBPQ also avoids —
        // it drops Cc on receipt.)
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AllowB2F = true });
        host.Store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["GB7RDG"] });
        host.Store.UpsertPartner(new Partner { Call = "GB7CIP", AtCalls = ["GB7CIP"] });

        byte[] obj = new B2Message
        {
            Mid = "301_GB7BPQ",
            Type = B2MessageType.Private,
            From = "M0XYZ",
            To = ["G4XYZ@GB7RDG"],
            Cc = ["M0CCC@GB7CIP"],
            Subject = "cc elsewhere",
            Mbo = "GB7BPQ",
            Body = Encoding.ASCII.GetBytes("Body.\r\n"),
        }.Encode();
        int csize = LzhufContainer.Encode(LzhufContainerKind.B1, obj).Length;
        var proposal = new FcProposal(FcType.Em, "301_GB7BPQ", obj.Length, csize);
        Message stored = host.Receiver.Deliver(new FbbMessageDelivered(proposal, "cc elsewhere", obj), "GB7BPQ")!;

        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7RDG")).Number); // To's home
        Assert.Empty(host.Store.GetForwardQueue("GB7CIP")); // the Cc's home is NOT queued
        Assert.Equal("M0CCC", Assert.Single(stored.Recipients, r => r.Cc).ToCall); // but the Cc IS stored
    }

    [Fact]
    public async Task OutboundB2_MultiRecipientAndAttachments_EmitsAllToCcAndFiles()
    {
        // A stored message with 2 To + 1 Cc + 2 attachments builds a B2 object that carries every
        // To: line, every Cc: line, and every File: part — the relay-onward path.
        await using var ts = new HostHarness();
        byte[] file1 = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] file2 = Encoding.Latin1.GetBytes("notes\r\n");
        Message stored = ts.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC", "G4XYZ"],
            CcRecipients = ["M0CCC"],
            Subject = "relay onward",
            Body = Encoding.Latin1.GetBytes("relay me\r"),
            Attachments =
            [
                new MessageAttachment("DATA.BIN", file1),
                new MessageAttachment("notes.txt", file2),
            ],
        });
        Message loaded = ts.Store.GetMessage(stored.Number)!;

        var partner = new Partner { Call = "GB7BPQ", AllowB2F = true, AtCalls = ["*"] };
        IReadOnlyList<OutboundItem> items = OutboundBuilder.Build(
            [loaded], partner, ts.Identity, ts.Time, NullLogger.Instance);
        ReadOnlyMemory<byte>? b2Object = Assert.Single(items).Wire.B2Object;
        Assert.NotNull(b2Object);

        B2Message decoded = B2Message.Decode(b2Object.Value.Span);
        Assert.Equal(["G4XYZ", "G8ABC"], decoded.To);   // both To (store orders recipients by callsign)
        Assert.Equal(["M0CCC"], decoded.Cc);            // the Cc
        Assert.Equal(2, decoded.Files.Count);
        Assert.Equal("DATA.BIN", decoded.Files[0].Name);
        Assert.Equal(file1, decoded.Files[0].Content.ToArray());
        Assert.Equal("notes.txt", decoded.Files[1].Name);
        Assert.Equal(file2, decoded.Files[1].Content.ToArray());
    }

    [Fact]
    public async Task OutboundB2_SingleRecipientNoAttachment_WireIsUnchanged()
    {
        // The regression guard: one To, no Cc, no attachment builds the IDENTICAL single-recipient
        // object as before — one To: line, no Cc:/File:. (The GB7RDG↔pdn wire is untouched.)
        await using var ts = new HostHarness();
        Message stored = ts.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "plain",
            Body = Encoding.Latin1.GetBytes("just one\r"),
        });
        Message loaded = ts.Store.GetMessage(stored.Number)!;

        var partner = new Partner { Call = "GB7BPQ", AllowB2F = true, AtCalls = ["*"] };
        IReadOnlyList<OutboundItem> items = OutboundBuilder.Build(
            [loaded], partner, ts.Identity, ts.Time, NullLogger.Instance);
        ReadOnlyMemory<byte>? b2Object = Assert.Single(items).Wire.B2Object;
        Assert.NotNull(b2Object);
        B2Message decoded = B2Message.Decode(b2Object.Value.Span);

        Assert.Equal("G8ABC", Assert.Single(decoded.To));
        Assert.Empty(decoded.Cc);
        Assert.Empty(decoded.Files);
    }

    // --- Full inbound B2 cycle through the demux: FC accept → object → decode → store ---

    [Fact]
    public async Task FullInboundB2Cycle_AcceptsFc_ReceivesObject_StoresDecodedMessage()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AllowB2F = true });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(B2PeerSid);
        Assert.Equal(OurB2Sid, await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        const string bodyText = "Inbound B2 body.\r\n73\r\n";
        byte[] obj = new B2Message
        {
            Mid = "200_GB7BPQ",
            Type = B2MessageType.Private,
            From = "M0XYZ",
            To = ["G4XYZ@GB7BSK"],
            Subject = "Wire B2 in",
            Mbo = "GB7BPQ",
            Body = Encoding.ASCII.GetBytes(bodyText),
        }.Encode();
        int csize = LzhufContainer.Encode(LzhufContainerKind.B1, obj).Length;

        string fc = string.Create(CultureInfo.InvariantCulture, $"FC EM 200_GB7BPQ {obj.Length} {csize} 0");
        await peer.SendLineAsync(fc);
        await peer.SendLineAsync(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([fc])));

        Assert.Equal("FS +", await peer.ReadLineAsync());

        await peer.SendBytesAsync(BlockFraming.EncodeMessage(
            "Wire B2 in", 0, LzhufContainer.Encode(LzhufContainerKind.B1, obj)));

        Assert.Equal("FF", await peer.ReadLineAsync());
        await peer.SendLineAsync("FQ");
        await peer.WaitForHostCloseAsync();

        Message stored = Assert.Single(host.Store.ListMessages(new MessageQuery()));
        Assert.Equal(MessageType.Personal, stored.Type);
        Assert.Equal("M0XYZ", stored.From);
        Assert.Equal("G4XYZ", Assert.Single(stored.Recipients).ToCall);
        Assert.Equal("200_GB7BPQ", stored.Bid);
        Assert.Equal("Wire B2 in", stored.Subject);
        Assert.Equal("GB7BPQ", stored.ReceivedFrom);
        Assert.Equal(bodyText, stored.GetBodyText());
    }

    [Fact]
    public async Task FullInboundB2Cycle_MultiRecipientAndAttachment_StoredThroughTheContainer()
    {
        // End-to-end through the demux + LZHUF container + block framing: a 2-To + 1-Cc + 1-File
        // B2 object decodes and stores all recipients (To/Cc) and the attachment byte-exact.
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AllowB2F = true });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(B2PeerSid);
        Assert.Equal(OurB2Sid, await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        byte[] fileBytes = [0x00, 0x01, 0xFE, 0xFF, 0x42];
        byte[] obj = new B2Message
        {
            Mid = "201_GB7BPQ",
            Type = B2MessageType.Private,
            From = "M0XYZ",
            To = ["G4XYZ@GB7BSK", "G8ABC@GB7BSK"],
            Cc = ["M0CCC@GB7BSK"],
            Subject = "Wire B2 multi",
            Mbo = "GB7BPQ",
            Body = Encoding.ASCII.GetBytes("Multi body.\r\n"),
            Files = [new B2Attachment("ATTACH.BIN", fileBytes)],
        }.Encode();
        int csize = LzhufContainer.Encode(LzhufContainerKind.B1, obj).Length;

        string fc = string.Create(CultureInfo.InvariantCulture, $"FC EM 201_GB7BPQ {obj.Length} {csize} 0");
        await peer.SendLineAsync(fc);
        await peer.SendLineAsync(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([fc])));

        Assert.Equal("FS +", await peer.ReadLineAsync());
        await peer.SendBytesAsync(BlockFraming.EncodeMessage(
            "Wire B2 multi", 0, LzhufContainer.Encode(LzhufContainerKind.B1, obj)));

        Assert.Equal("FF", await peer.ReadLineAsync());
        await peer.SendLineAsync("FQ");
        await peer.WaitForHostCloseAsync();

        Message stored = Assert.Single(host.Store.ListMessages(new MessageQuery()));
        Assert.Equal(["G4XYZ", "G8ABC"], stored.Recipients.Where(r => !r.Cc).Select(r => r.ToCall).Order());
        Assert.Equal("M0CCC", Assert.Single(stored.Recipients, r => r.Cc).ToCall);
        Assert.Equal("ATTACH.BIN", Assert.Single(stored.Attachments).Name);
        Assert.Equal(fileBytes, Assert.Single(stored.Attachments).Content.ToArray());
    }
}
