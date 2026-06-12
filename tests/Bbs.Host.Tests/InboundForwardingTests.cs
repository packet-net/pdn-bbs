using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;

namespace Bbs.Host.Tests;

public class InboundForwardingTests
{
    private const string PeerSid = "[BPQ-6.0.24.44-B1FHM$]";

    [Fact]
    public async Task FullInboundSession_StoresMessageWithRChainAndRoutesOnward()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ" });
        host.Store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(PeerSid);
        Assert.Equal("[PDN-0.1.0-B1FHM$]", await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        // The sender's plaintext: its R: line, the first-hop blank, the body (spec §3.7).
        const string body = "R:260611/1000Z 44@GB7BPQ.#23.GBR.EURO BPQ6.0.24\r\rHello from BPQ.\r";
        string proposal = string.Create(
            CultureInfo.InvariantCulture, $"FA P M0XYZ GB7PDN G8ABC 123_GB7BPQ {body.Length}");
        await peer.SendLineAsync(proposal);
        await peer.SendLineAsync(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([proposal])));

        Assert.Equal("FS +", await peer.ReadLineAsync());

        byte[] wire = BlockFraming.EncodeMessage(
            "Test subject", 0, LzhufContainer.Encode(LzhufContainerKind.B1, Encoding.Latin1.GetBytes(body)));
        await peer.SendBytesAsync(wire);

        // Receipt is implicitly acknowledged by our turn — empty queue → FF; peer FQ ends it.
        Assert.Equal("FF", await peer.ReadLineAsync());
        await peer.SendLineAsync("FQ");
        await peer.WaitForHostCloseAsync();

        // The message landed through the Core receive path.
        Message stored = Assert.Single(host.Store.ListMessages(new MessageQuery()));
        Assert.Equal(MessageType.Personal, stored.Type);
        Assert.Equal("M0XYZ", stored.From);
        Assert.Equal("G8ABC", Assert.Single(stored.Recipients).ToCall);
        Assert.Equal("123_GB7BPQ", stored.Bid);
        Assert.Equal("Test subject", stored.Subject);
        Assert.Equal("GB7BPQ", stored.ReceivedFrom);
        Assert.Equal(body, stored.GetBodyText()); // verbatim — the R: chain is intact

        // The BID is recorded with its arrival direction (routing loop guard input).
        BidRecord? bid = host.Store.LookupBid("123_GB7BPQ");
        Assert.NotNull(bid);
        Assert.Equal("GB7BPQ", bid.FirstSeenFrom);
        Assert.Equal(stored.Number, bid.MessageNumber);

        // Local delivery beats forwarding (design.md rule #1): this personal is @GB7PDN — our
        // own callsign — so it is for a mailbox here and stays local. It must NOT leak onward to
        // the wildcard partner, and of course never back toward GB7BPQ. (Onward forwarding of a
        // genuinely-remote-addressed inbound message is covered by RoutesGenuinelyOnward below.)
        Assert.Empty(host.Store.GetForwardQueue("GB7RDG"));
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
    }

    [Fact]
    public async Task RoutesGenuinelyOnward_WhenInboundIsAddressedElsewhere()
    {
        // Companion to the test above: an inbound personal whose AT is NOT us and whose TO is
        // not a local user is genuine transit — it still routes onward through the wildcard
        // partner, never back toward the sender. Confirms the local-first rule does not block
        // legitimate forwarding.
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ" });
        host.Store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(PeerSid);
        Assert.Equal("[PDN-0.1.0-B1FHM$]", await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        const string body = "R:260611/1000Z 44@GB7BPQ.#23.GBR.EURO BPQ6.0.24\r\rOnward.\r";
        // @GB7BSK — a remote BBS, not us — so this is genuine onward transit.
        string proposal = string.Create(
            CultureInfo.InvariantCulture, $"FA P M0XYZ GB7BSK G4XYZ 124_GB7BPQ {body.Length}");
        await peer.SendLineAsync(proposal);
        await peer.SendLineAsync(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([proposal])));

        Assert.Equal("FS +", await peer.ReadLineAsync());

        byte[] wire = BlockFraming.EncodeMessage(
            "Onward subject", 0, LzhufContainer.Encode(LzhufContainerKind.B1, Encoding.Latin1.GetBytes(body)));
        await peer.SendBytesAsync(wire);

        Assert.Equal("FF", await peer.ReadLineAsync());
        await peer.SendLineAsync("FQ");
        await peer.WaitForHostCloseAsync();

        Message stored = Assert.Single(host.Store.ListMessages(new MessageQuery()));
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7RDG")).Number);
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
    }

    [Fact]
    public async Task DuplicateBid_IsRefusedWithAlreadyHave()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ" });
        host.Store.RecordBid("1043_GB7BPQ");
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(PeerSid);
        await peer.ReadLineAsync(); // SID
        await peer.ReadLineAsync(); // prompt

        string proposal = "FA B G8XYZ GBR PACKET 1043_GB7BPQ 60";
        await peer.SendLineAsync(proposal);
        await peer.SendLineAsync(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([proposal])));

        // A known bulletin BID → '-' (compat spec §2.3/§4.3); no transfer follows.
        Assert.Equal("FS -", await peer.ReadLineAsync());
        Assert.Equal("FF", await peer.ReadLineAsync());
        await peer.SendLineAsync("FQ");
        await peer.WaitForHostCloseAsync();

        Assert.Empty(host.Store.ListMessages(new MessageQuery()));
    }

    [Fact]
    public async Task OversizeProposal_IsRefusedWithAlreadyHave()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", MaxRxSize = 100 });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(PeerSid);
        await peer.ReadLineAsync();
        await peer.ReadLineAsync();

        string proposal = "FA P M0XYZ GB7PDN G8ABC 9_GB7BPQ 5000";
        await peer.SendLineAsync(proposal);
        await peer.SendLineAsync(ProposalBlock.BuildTerminator(ProposalBlock.ComputeChecksum([proposal])));

        Assert.Equal("FS -", await peer.ReadLineAsync()); // size > MaxRXSize → '-' (spec §4.3)
        Assert.Equal("FF", await peer.ReadLineAsync());
        await peer.SendLineAsync("FQ");
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task AnswererWithQueuedMail_ReverseForwardsOnFf()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });

        Message queued = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "Outbound",
            Body = Encoding.Latin1.GetBytes("Reverse test.\r"),
        });
        host.Routing.RouteMessage(queued);
        Assert.Single(host.Store.GetForwardQueue("GB7BPQ"));

        await host.StartLinkAsync();
        host.StartDemux();

        // The partner dials US, sends nothing of its own (FF) — reverse forwarding
        // (spec §3.11): our turn, we propose the queued message.
        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync(PeerSid);
        await peer.ReadLineAsync();
        await peer.ReadLineAsync();
        await peer.SendLineAsync("FF");

        string fa = await peer.ReadLineAsync();
        Assert.StartsWith("FA P M0LTE GB7BPQ G8ABC 1_GB7PDN ", fa, StringComparison.Ordinal);
        string terminator = await peer.ReadLineAsync();
        Assert.StartsWith("F> ", terminator, StringComparison.Ordinal);

        await peer.SendLineAsync("FS +");

        // The framed compressed transfer arrives.
        var reader = new FbbBlockReader();
        byte[] leftover = await ReadOneTransferAsync(peer, reader);
        peer.PushBackForLines(leftover);
        Assert.Equal("Outbound", reader.Title);
        byte[] plaintext = LzhufContainer.Decode(LzhufContainerKind.B1, reader.Payload.Span);
        string text = Encoding.Latin1.GetString(plaintext);
        Assert.StartsWith("R:260611/1200Z 1@GB7PDN.#23.GBR.EURO PDN0.1.0\r\n\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("Reverse test.\r", text, StringComparison.Ordinal);

        // The turn reverses; the peer still has nothing → FF; we are empty → FQ, done.
        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();

        // FS '+' cleared the queue entry; the single-partner message is now F.
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(queued.Number)!.Status);
    }

    /// <summary>Feeds host chunks into <paramref name="reader"/> until one transfer completes; returns unconsumed tail bytes.</summary>
    internal static async Task<byte[]> ReadOneTransferAsync(FakeRhpPeer peer, FbbBlockReader reader)
    {
        while (true)
        {
            byte[] chunk = await peer.ReadChunkRawAsync();
            FbbBlockReaderStatus status = reader.Feed(chunk, out int consumed);
            if (status == FbbBlockReaderStatus.Complete)
            {
                return chunk[consumed..];
            }

            Assert.Equal(FbbBlockReaderStatus.NeedMoreData, status);
        }
    }
}
