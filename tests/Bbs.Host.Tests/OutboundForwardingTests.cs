using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;

namespace Bbs.Host.Tests;

public class OutboundForwardingTests
{
    private const string PeerSid = "[BPQ-6.0.24.44-B1FHM$]";

    [Fact]
    public async Task PartnerCreatedAtRuntime_GetsADialingLoop_AfterReconcile()
    {
        // Store-first: a partner added via the editor AFTER the scheduler is running must get a
        // forwarding loop. The supervisor spins one on Reconcile() (the editor's signal); the fresh
        // loop checks its queue immediately (nudge-on-spin) and dials — no restart, no interval wait.
        await using var host = new HostHarness();
        await host.StartLinkAsync();
        host.StartScheduler(); // no partners at startup

        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7NEW",
            AtCalls = ["*"],
            ConnectScript = ["C GB7NEW-1"],
            ForwardNewImmediately = true,
        });
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "runtime",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        host.Routing.RouteMessage(stored); // queues to GB7NEW (the immediate nudge is lost — no loop yet)
        host.Scheduler!.Reconcile();       // the editor's signal → supervisor spins the loop → it dials

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7NEW-1", peer.Remote);

        // Let the cycle make progress so dispose unwinds cleanly rather than mid-connect.
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7NEW>\r");
    }

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

        // The cycle opens the script target (spec §4.4: the first C <target> names the dial).
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

        // Send-time R: line (CRLF-terminated) + first-hop CRLF blank separator + the stored
        // body (spec §3.7/§3.14). CRLF is required: a bare-CR R: line NULL-derefs LinBPQ.
        Assert.Equal("R:260611/1200Z 1@GB7PDN.#23.GBR.EURO PDN0.1.0\r\n\r\nLocal test body.\r", plaintext);

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

    [Fact]
    public async Task CallerSession_AcceptsPartnerReverseForward_AndStoresIt()
    {
        // We DIAL the partner to send our own queued message; after our batch the turn reverses
        // (spec §3.11) and the partner reverse-forwards a message of its own. The caller must
        // accept it and receive it through the same Core receive path the answerer uses.
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        Message ours = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "Ours",
            Body = Encoding.Latin1.GetBytes("Our outbound.\r"),
        });
        host.Routing.RouteMessage(ours);

        // We are the caller: the answerer's SID + prompt come first.
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ", peer.Remote);
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7BPQ>\r");

        Assert.Equal("[PDN-0.1.0-B1FHM$]", await peer.ReadLineAsync());
        string fa = await peer.ReadLineAsync();
        Assert.StartsWith("FA P M0LTE GB7BPQ G8ABC 1_GB7PDN ", fa, StringComparison.Ordinal);
        await peer.ReadLineAsync(); // F>

        await peer.SendLineAsync("FS +");
        var reader = new FbbBlockReader();
        byte[] leftover = await InboundForwardingTests.ReadOneTransferAsync(peer, reader);
        peer.PushBackForLines(leftover);
        Assert.Equal("Ours", reader.Title);

        // The turn reverses to the partner: it now proposes a message FOR US (in-session reverse).
        const string body = "R:260611/1000Z 55@GB7BPQ.#23.GBR.EURO BPQ6.0.24\r\rReverse from BPQ.\r";
        string reverse = string.Create(
            CultureInfo.InvariantCulture, $"FA P M0XYZ GB7PDN G7DEF 500_GB7BPQ {body.Length}");
        await peer.SendLineAsync(reverse);
        await peer.SendLineAsync(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([reverse])));

        // We accept the reverse proposal (new BID, in size) and receive its transfer.
        Assert.Equal("FS +", await peer.ReadLineAsync());
        byte[] wire = BlockFraming.EncodeMessage(
            "Reverse subject", 0, LzhufContainer.Encode(LzhufContainerKind.B1, Encoding.Latin1.GetBytes(body)));
        await peer.SendBytesAsync(wire);

        // Receipt is implicitly acknowledged by our turn — we are empty now → FF; peer FQ ends it.
        Assert.Equal("FF", await peer.ReadLineAsync());
        await peer.SendLineAsync("FQ");
        await peer.WaitForHostCloseAsync();

        // Our outbound was forwarded; the partner's reverse message landed through the Core path.
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(ours.Number)!.Status);

        Message collected = Assert.Single(host.Store.ListMessages(new MessageQuery { FromCall = "M0XYZ" }));
        Assert.Equal("M0XYZ", collected.From);
        Assert.Equal("G7DEF", Assert.Single(collected.Recipients).ToCall);
        Assert.Equal("500_GB7BPQ", collected.Bid);
        Assert.Equal("Reverse subject", collected.Subject);
        Assert.Equal("GB7BPQ", collected.ReceivedFrom);
        Assert.Equal(body, collected.GetBodyText()); // verbatim — the R: chain is intact
        Assert.Equal("GB7BPQ", host.Store.LookupBid("500_GB7BPQ")!.FirstSeenFrom);
    }

    [Fact]
    public async Task ReversePoll_DialsEmptyOutboundPartner_AndCollectsItsQueue()
    {
        // A collect partner with NOTHING of ours queued is still dialled on the interval cadence
        // (a deliberate poll). We open the session with FF; the partner reverse-forwards; we
        // collect it. This is how an asymmetric partner that cannot dial us gets its mail to us.
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7CIP",
            AtCalls = ["*"],
            Collect = true,
            ForwardIntervalSeconds = 3600,
            ForwardNewImmediately = false,
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        // Nothing of ours queued — no nudge. The poll fires only when the interval elapses.
        Assert.Empty(host.Store.GetForwardQueue("GB7CIP"));
        Assert.Equal(0, host.Server.OpenAttempts);
        await host.AdvanceUntilAsync(TimeSpan.FromMinutes(20), () => Task.FromResult(host.Server.OpenAttempts > 0));

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7CIP", peer.Remote); // no script → dial the partner call itself
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7CIP>\r");

        // We have nothing to send, so the caller opens its turn with FF (spec §3.11).
        Assert.Equal("[PDN-0.1.0-B1FHM$]", await peer.ReadLineAsync());
        Assert.Equal("FF", await peer.ReadLineAsync());

        // The partner now reverse-forwards a message held for us.
        const string body = "R:260611/0900Z 7@GB7CIP.#23.GBR.EURO BPQ6.0.24\r\rHeld for you.\r";
        string reverse = string.Create(
            CultureInfo.InvariantCulture, $"FA P G3AAA GB7PDN G4BBB 99_GB7CIP {body.Length}");
        await peer.SendLineAsync(reverse);
        await peer.SendLineAsync(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([reverse])));

        Assert.Equal("FS +", await peer.ReadLineAsync());
        byte[] wire = BlockFraming.EncodeMessage(
            "Polled subject", 0, LzhufContainer.Encode(LzhufContainerKind.B1, Encoding.Latin1.GetBytes(body)));
        await peer.SendBytesAsync(wire);

        // Our turn again — still nothing → FF; peer FQ closes the clean cycle.
        Assert.Equal("FF", await peer.ReadLineAsync());
        await peer.SendLineAsync("FQ");
        await peer.WaitForHostCloseAsync();

        Message collected = Assert.Single(host.Store.ListMessages(new MessageQuery { FromCall = "G3AAA" }));
        Assert.Equal("G4BBB", Assert.Single(collected.Recipients).ToCall);
        Assert.Equal("99_GB7CIP", collected.Bid);
        Assert.Equal("GB7CIP", collected.ReceivedFrom);
        Assert.Equal(body, collected.GetBodyText());
    }

    [Fact]
    public async Task ReversePoll_NothingToSendAndNothingCollected_IsACleanNoOp()
    {
        // The collect poll dials, finds the partner has nothing either, and the FF↔FQ handshake
        // closes gracefully — no error, no message stored, and (graceful) no backoff is armed.
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7CIP",
            AtCalls = ["*"],
            Collect = true,
            ForwardIntervalSeconds = 3600,
            ForwardNewImmediately = false,
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        await host.AdvanceUntilAsync(TimeSpan.FromMinutes(20), () => Task.FromResult(host.Server.OpenAttempts > 0));
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7CIP>\r");

        Assert.Equal("[PDN-0.1.0-B1FHM$]", await peer.ReadLineAsync());
        Assert.Equal("FF", await peer.ReadLineAsync()); // we have nothing
        await peer.SendLineAsync("FF");                  // the partner has nothing
        Assert.Equal("FQ", await peer.ReadLineAsync());  // clean close
        await peer.WaitForHostCloseAsync();

        Assert.Empty(host.Store.ListMessages(new MessageQuery()));
    }

    [Fact]
    public async Task DefaultPartner_NoCollect_NeverDialsOnEmptyQueue()
    {
        // The default (collect absent): an enabled partner with an empty queue is NEVER dialled,
        // even across many interval cycles — a quiet link stays quiet (existing behaviour intact).
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ForwardIntervalSeconds = 3600,
            ForwardNewImmediately = false,
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));

        // Advance well past several interval cycles; the empty queue must never trigger a dial.
        for (int i = 0; i < 10; i++)
        {
            host.Time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(10);
        }

        Assert.Equal(0, host.Server.OpenAttempts);
    }
}
