using Bbs.Core;

namespace Bbs.Host.Tests;

/// <summary>
/// The greet-immediately inbound demux (design decision 1; compat spec §1.1/§3.1): on
/// accept the host sends its SID line then starts the console (its greeting/prompt flow
/// immediately), while the first inbound line decides FBB-answerer-vs-console behind the
/// console's first-line gate.
/// </summary>
public class DemuxTests
{
    private const string OwnSid = "[PDN-0.1.0-B1FHM$]";
    private const string PeerSid = "[BPQ-6.0.24.44-B1FHM$]";

    [Fact]
    public async Task KnownPartnerCaller_GetsSidAndPrompt_ThenContinueModeAnswerer()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ" });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");

        // Greet-immediately: our SID is the first thing on the wire (spec §3.1 step 2),
        // and a known partner (BBS-flagged, spec §2.5) gets only the de CALL> prompt.
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        // The peer's SID hands the stream to the FBB answerer in continue-mode: no
        // duplicate SID/prompt precedes the FQ (the next host line IS the FQ).
        await peer.SendLineAsync(PeerSid);
        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task UnknownSidShapedCaller_NamePromptDoesNotEatTheSid()
    {
        await using var host = new HostHarness();
        await host.StartLinkAsync();
        host.StartDemux();

        // No partner record: LinBPQ's "temporary BBS" classification — the caller is
        // greeted like a new user, but its SID still selects the FBB answerer.
        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
        Assert.Equal("Please enter your Name", await peer.ReadLineAsync());
        Assert.Equal(">", await peer.ReadLineAsync());

        // Load-bearing: the demux peeks the SID BEFORE the console consumes any input,
        // so the new-user name prompt cannot eat it (spec §1.1) — the answerer runs.
        await peer.SendLineAsync(PeerSid);
        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task KnownUser_GreetingFlowsImmediatelyOnAccept()
    {
        await using var host = new HostHarness();
        host.Store.UpsertUser(new User { Callsign = "M0ABC", Name = "Alice", HomeBbs = "GB7PDN" });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");

        // SID + greeting + prompt all arrive before the peer has sent a byte
        // (compat spec §1.1 — human callers ignore the SID line, real BPQ shows it too).
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
        Assert.Equal("Hello Alice. Latest Message is 0, Last listed is 0", await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        // The gated first line is dispatched as the first command: sign-off + close.
        await peer.SendLineAsync("B");
        Assert.Equal("73 de GB7PDN", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task UnknownHumanFirstLine_IsConsumedAsTheName()
    {
        await using var host = new HostHarness();
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
        Assert.Equal("Please enter your Name", await peer.ReadLineAsync());
        Assert.Equal(">", await peer.ReadLineAsync());

        // A non-SID first line opens the gate: it feeds the console's pending read —
        // here the new-user name prompt (compat spec §1.1).
        await peer.SendLineAsync("Tom");
        Assert.Equal("Hello Tom. Latest Message is 0, Last listed is 0", await peer.ReadLineAsync());

        // No home BBS yet → the verbatim two-line nag (spec §1.1), then the prompt.
        Assert.Equal("Please enter your Home BBS using the Home command.", await peer.ReadLineAsync());
        Assert.Equal("You may also enter your QTH and ZIP/Postcode using qth and zip commands.", await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        await peer.SendLineAsync("B");
        Assert.Equal("73 de GB7PDN", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task SilentCaller_SeesGreetingImmediately_ConsoleLivesPastExpiry()
    {
        await using var host = new HostHarness(firstLineWait: TimeSpan.FromSeconds(30));
        host.Store.UpsertUser(new User { Callsign = "M0ABC", Name = "Alice", HomeBbs = "GB7PDN" });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");

        // Greet-immediately: NO time advance is needed to see the greeting (the old
        // silent-peek flow deadlocked exactly here — compat spec §1.1).
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
        Assert.Equal("Hello Alice. Latest Message is 0, Last listed is 0", await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        // Past the demux window the session is the console's; input still round-trips.
        host.Time.Advance(TimeSpan.FromSeconds(31));
        await peer.SendLineAsync("B");
        Assert.Equal("73 de GB7PDN", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task SidAfterExpiry_IsAConsoleCommandNotAForwardingOpener()
    {
        await using var host = new HostHarness(firstLineWait: TimeSpan.FromSeconds(30));
        host.Store.UpsertUser(new User { Callsign = "M0ABC", Name = "Alice", HomeBbs = "GB7PDN" });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
        Assert.Equal("Hello Alice. Latest Message is 0, Last listed is 0", await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        // Expire the window FIRST (and give the demux a beat to observe it): the
        // Fbb-vs-console decision is final from here on (design decision 1).
        host.Time.Advance(TimeSpan.FromSeconds(31));
        await Task.Delay(100);

        // A SID arriving late is plain console input — an invalid command, never the
        // FBB answerer.
        await peer.SendLineAsync(PeerSid);
        Assert.Equal("Invalid Command", await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        await peer.SendLineAsync("B");
        Assert.Equal("73 de GB7PDN", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task ConsoleEnteredMessage_IsRoutedWhenSessionEnds()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        host.Store.UpsertUser(new User { Callsign = "M0ABC", Name = "Alice", HomeBbs = "GB7PDN" });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");
        await peer.SendLineAsync("S G8ABC");
        // Skip output until the title prompt, answer the flow, then sign off.
        await ReadUntilAsync(peer, "Enter Title (only):");
        await peer.SendLineAsync("Hello there");
        await ReadUntilAsync(peer, "Enter Message Text (end with /ex or ctrl/z)");
        await peer.SendLineAsync("First line.");
        await peer.SendLineAsync("/ex");
        await peer.SendLineAsync("B");
        await peer.WaitForHostCloseAsync();

        // The demux routed the new message into the forward queues at session end.
        IReadOnlyList<Message> queue = host.Store.GetForwardQueue("GB7BPQ");
        Message queued = Assert.Single(queue);
        Assert.Equal("HELLO THERE", queued.Subject.ToUpperInvariant());
    }

    private static async Task ReadUntilAsync(FakeRhpPeer peer, string expected)
    {
        for (int i = 0; i < 50; i++)
        {
            string line = await peer.ReadLineAsync();
            if (line.Contains(expected, StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail($"Never saw \"{expected}\".");
    }
}
