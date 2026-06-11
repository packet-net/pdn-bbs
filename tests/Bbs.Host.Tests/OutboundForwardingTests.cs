using System.Text;
using Bbs.Core;
using Bbs.Fbb;

namespace Bbs.Host.Tests;

public class OutboundForwardingTests
{
    private const string PeerSid = "[BPQ-6.0.24.44-B1FHM$]";

    [Fact]
    public async Task FullOutboundCycle_OpensTargetTransfersAndMarksForwarded()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = ["C GB7BPQ-1"], // the connect target, not the partner call
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        // A new local message routes, queues, and (FWDNewImmediately) nudges the scheduler.
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "Hi",
            Body = Encoding.Latin1.GetBytes("Local test body.\r"),
        });
        host.Routing.RouteMessage(stored);

        // The cycle opens the script target (spec §4.4: the last C <target> is the dial).
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ-1", peer.Remote);
        Assert.Equal(HostHarness.OwnCall, peer.Local);

        // We are the caller: the answerer's SID + prompt come first (spec §3.1).
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7BPQ>\r");

        Assert.Equal("[PDN-0.1.0-B1FHM$]", await peer.ReadLineAsync());
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
        string plaintext = Encoding.Latin1.GetString(LzhufContainer.Decode(LzhufContainerKind.B1, reader.Payload.Span));

        // Send-time R: line + first-hop blank separator + the stored body (spec §3.7/§3.14).
        Assert.Equal("R:260611/1200Z 1@GB7PDN.#23.GBR.EURO PDN0.1.0\r\rLocal test body.\r", plaintext);

        // Peer has nothing for us → FF; we are done → FQ; the host hangs up.
        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();

        // FS '+' → MarkForwarded: queue cleared, single-partner message goes F.
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);
    }

    [Fact]
    public async Task IntervalTimer_FiresCycleWithoutNudge()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ForwardIntervalSeconds = 3600,
            ForwardNewImmediately = false, // timer only
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "Timer",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        host.Routing.RouteMessage(stored);

        // No nudge configured — nothing happens until the interval elapses.
        Assert.Equal(0, host.Server.OpenAttempts);
        await host.AdvanceUntilAsync(TimeSpan.FromMinutes(20), () => Task.FromResult(host.Server.OpenAttempts > 0));

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ", peer.Remote); // no script → dial the partner call itself
    }

    [Fact]
    public async Task DeferredProposal_LeavesMessageQueued()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "Deferred",
            Body = Encoding.Latin1.GetBytes("Later.\r"),
        });
        host.Routing.RouteMessage(stored);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7BPQ>\r");
        await peer.ReadLineAsync(); // our SID
        await peer.ReadLineAsync(); // FA
        await peer.ReadLineAsync(); // F>

        // '=' = try again later (spec §3.4): no transfer, entry stays queued.
        await peer.SendLineAsync("FS =");
        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();

        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
        Assert.Equal(MessageStatus.Unread, host.Store.GetMessage(stored.Number)!.Status);
    }

    [Fact]
    public async Task FailedOpen_RetriesAfterBackoff()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ForwardNewImmediately = true,
            ForwardIntervalSeconds = 3600,
        });
        host.Server.OpenResult = _ => 15; // "No Route"
        await host.StartLinkAsync();
        host.StartScheduler();

        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "Retry",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        host.Routing.RouteMessage(stored);

        // First attempt fails immediately (nudge path).
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(1), () => Task.FromResult(host.Server.OpenAttempts >= 1));

        // The retry happens after the backoff (60 s for failure #1), well inside the interval.
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(30), () => Task.FromResult(host.Server.OpenAttempts >= 2));
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
    }

    [Fact]
    public void RetryDelay_DoublesAndCapsAtInterval()
    {
        var interval = TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.FromSeconds(60), Forwarding.ForwardingScheduler.RetryDelay(1, interval));
        Assert.Equal(TimeSpan.FromSeconds(120), Forwarding.ForwardingScheduler.RetryDelay(2, interval));
        Assert.Equal(TimeSpan.FromSeconds(240), Forwarding.ForwardingScheduler.RetryDelay(3, interval));
        Assert.Equal(interval, Forwarding.ForwardingScheduler.RetryDelay(10, interval));
        Assert.Equal(TimeSpan.FromMinutes(2), Forwarding.ForwardingScheduler.RetryDelay(10, TimeSpan.FromMinutes(2)));
    }
}
