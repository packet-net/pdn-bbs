using System.Text;
using Bbs.Core;
using Bbs.Fbb;

namespace Bbs.Host.Tests;

/// <summary>
/// Connect-script execution at the scheduler level (compat spec §4.4): post-open steps
/// run in order with response waits, node chatter is absorbed before the FBB caller
/// session starts, failure text and silent nodes fail the cycle onto the backoff-retry
/// path, and a <c>C &lt;port&gt; &lt;target&gt;</c> connect pins the port on the RHP open.
/// </summary>
public class ConnectScriptRunnerTests
{
    private const string PeerSid = "[BPQ-6.0.24.44-B1FHM$]";
    private const string OwnSid = "[PDN-0.1.0-B1FHM$]";

    [Fact]
    public async Task ScriptedCycle_NavigatesTheNodeThenRunsTheFullForward()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = ["C GB7BPQ-1", "BBS"],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        Message stored = QueueOne(host, "Hi");

        // The first C names the open (spec §4.4); the remaining line is a step.
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ-1", peer.Remote);
        Assert.Equal("BBS", await peer.ReadLineAsync());

        // Node chatter before the SID — including a "*** Connected" progress line the
        // FBB FSM would treat as fatal (spec §3.12) — is consumed by the script layer.
        await peer.SendLineAsync("GB7BPQ pdn-bbs oracle");
        await peer.SendLineAsync("*** Connected to GB7BPQ-1");
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7BPQ-1>\r");

        // The FBB caller session starts on the SID line the script handed over: our
        // SID, then the proposal block (spec §3.1 step 3).
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
        string fa = await peer.ReadLineAsync();
        Assert.StartsWith("FA P M0LTE GB7BPQ G8ABC 1_GB7PDN ", fa, StringComparison.Ordinal);
        string terminator = await peer.ReadLineAsync();
        Assert.True(ProposalBlock.TryParseTerminator(terminator, out byte? checksum));
        Assert.Equal(ProposalBlock.ComputeChecksum([fa]), checksum);

        await peer.SendLineAsync("FS +");

        var reader = new FbbBlockReader();
        byte[] leftover = await InboundForwardingTests.ReadOneTransferAsync(peer, reader);
        peer.PushBackForLines(leftover);
        Assert.Equal("Hi", reader.Title);

        // Peer has nothing for us → FF; we are done → FQ; the host hangs up.
        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();

        // FS '+' → MarkForwarded (spec §3.4): queue cleared, single-partner message F.
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);
    }

    [Fact]
    public async Task IntermediateStep_WaitsForNodeProgressBeforeTheNextSend()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = ["C GB7BPQ", "C GB7RDG", "BBS"],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ", peer.Remote);

        // The second C is a node-level command, sent verbatim (spec §4.4).
        Assert.Equal("C GB7RDG", await peer.ReadLineAsync());

        // Nothing more goes out until the node reports progress.
        await Task.Delay(200);
        Assert.False(peer.TryReadLine(out string early), $"Sent before the node answered: \"{early}\"");

        // "the software knows what to look for" (spec §4.4): a CONNECTED line releases
        // the next step.
        await peer.SendLineAsync("*** Connected to GB7RDG");
        Assert.Equal("BBS", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task NodeFailureText_FailsTheCycleAndRetriesWithBackoff()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = ["C GB7BPQ", "C GB7RDG", "BBS"],
            ForwardNewImmediately = true,
            ForwardIntervalSeconds = 3600,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        Message stored = QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("C GB7RDG", await peer.ReadLineAsync());

        // Failure text from the spec §4.4 ELSE-detection list fails the cycle: the
        // host closes the child without sending the next step.
        await peer.SendLineAsync("GB7BPQ:BPQ} Failure with GB7RDG");
        await peer.WaitForHostCloseAsync();

        // The queue entry survives, and the retry dials again after the 60 s backoff
        // (failure #1), well inside the partner interval.
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(30), () => Task.FromResult(host.Server.OpenAttempts >= 2));
    }

    [Fact]
    public async Task SilentNode_TimesOutAtConTimeoutAndRetries()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = ["C GB7BPQ", "C GB7RDG", "BBS"],
            ForwardNewImmediately = true,
            ForwardIntervalSeconds = 3600,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        Message stored = QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("C GB7RDG", await peer.ReadLineAsync());

        // The node never answers: the response wait is bounded by ConTimeout (default
        // 60 s, TimeProvider-driven — spec §4.1); the failed cycle closes the child and
        // a second dial follows after the 60 s backoff.
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(15), () => Task.FromResult(host.Server.OpenAttempts >= 2));
        await peer.WaitForHostCloseAsync();
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
    }

    [Fact]
    public async Task PortedConnect_PinsThePortOnTheOpen()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = ["C 2 GB7BPQ-1"],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        // C <port> <target> (spec §4.4): the port rides the RHP open.
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ-1", peer.Remote);
        Assert.Equal("2", peer.Port);

        // A stepless plan skips the script layer entirely: the FBB caller flow starts
        // directly (the answerer's SID + prompt draw our SID — spec §3.1).
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7BPQ-1>\r");
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
    }

    /// <summary>Stores + routes one personal message so the partner queue is non-empty (nudges the scheduler).</summary>
    private static Message QueueOne(HostHarness host, string subject = "Retry")
    {
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = subject,
            Body = Encoding.Latin1.GetBytes("Local test body.\r"),
        });
        host.Routing.RouteMessage(stored);
        return stored;
    }
}
