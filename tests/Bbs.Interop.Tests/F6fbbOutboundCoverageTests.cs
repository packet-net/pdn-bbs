using System.Globalization;
using System.Net;
using System.Text;
using Bbs.Core;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Interop.Tests;

/// <summary>
/// Comprehensive OUTBOUND (pdn-bbs is the caller, dialling real xfbbd Q0FBB-1 over AXUDP)
/// real-F6FBB coverage beyond the single-message PoC. Every test drives the PRODUCTION
/// <see cref="FbbSessionRunner.RunCallerAsync"/> over an AXUDP-backed <see cref="Bbs.Host.Rhp.IFbbConnection"/>
/// and asserts on BOTH the captured wire (via <see cref="LoggingFbbConnection"/>) and the pdn-bbs
/// store outcome. Canonical F6FBB 7.0.11 — B1 only (its SID carries no '2'), so these stay on the
/// FA/B1 path. Requires the f6fbb-on-kernel VM booted (run/run-vm.sh NET=tap); serialised with the
/// rest of the real-F6FBB lane through <see cref="F6fbbCollection"/> (shared host port 10093).
/// </summary>
[Trait("Category", "InteropF6fbb")]
[Collection(F6fbbCollection.Name)]
public class F6fbbOutboundCoverageTests
{
    private static readonly IPEndPoint Vm = new(IPAddress.Parse("192.168.76.2"), 10093);

    /// <summary>OUT-11: empty queue — the caller opens with FF and the session closes FF/FQ-clean
    /// with no proposal ever on the wire and nothing marked Forwarded.</summary>
    [Fact]
    public async Task EmptyQueue_OpensAndClosesGracefully_NoProposal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost("Q0PDN", "#42.GBR.EURO");
        var partner = new Partner { Call = "Q0FBB", AtCalls = ["Q0FBB"] };
        host.Store.UpsertPartner(partner);

        IReadOnlyList<OutboundItem> outbound = BuildOutbound(host, partner);
        Assert.Empty(outbound);

        (FbbSessionResult result, List<string> wire) = await ForwardOnceAsync(host, partner, outbound, ct);

        Assert.True(result.Completed, "session did not complete");
        Assert.True(result.Graceful, "session did not close FF/FQ-clean");
        Assert.StartsWith("[FBB-7.0.11", result.PeerSidRaw);
        Assert.DoesNotContain(wire, l => TxStartsWith(l, "FA "));
    }

    /// <summary>OUT-03: three small personals proposed in a single block (≤5 per block, &lt;10 KB) —
    /// three FA lines, one F&gt;, all three accepted and Forwarded.</summary>
    [Fact]
    public async Task ThreeMessages_OneProposalBlock_AllForwarded()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost("Q0PDN", "#42.GBR.EURO");
        var partner = new Partner { Call = "Q0FBB", AtCalls = ["Q0FBB"] };
        host.Store.UpsertPartner(partner);

        long secs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var numbers = new List<long>();
        for (int i = 0; i < 3; i++)
        {
            string bid = string.Create(CultureInfo.InvariantCulture, $"{secs % 10000:D4}{i}_Q0PDN");
            Message m = host.Store.AddMessage(new MessageDraft
            {
                Type = MessageType.Personal, From = "M0LTE", Recipients = ["Q0FBB"], At = "Q0FBB",
                Bid = bid, Subject = $"multi {i} {secs}",
                Body = Encoding.Latin1.GetBytes($"pdn multi-message body {i} {secs}\r"),
            });
            host.Routing.RouteMessage(m);
            numbers.Add(m.Number);
        }

        Assert.Equal(3, host.Store.GetForwardQueue("Q0FBB").Count);
        IReadOnlyList<OutboundItem> outbound = BuildOutbound(host, partner);
        Assert.Equal(3, outbound.Count);

        (FbbSessionResult result, List<string> wire) = await ForwardOnceAsync(host, partner, outbound, ct);

        Assert.True(result.Completed && result.Graceful, "session was not clean");
        // All three proposals went out as FA lines, in one block (one F> terminator).
        Assert.Equal(3, wire.Count(l => TxStartsWith(l, "FA P")));
        Assert.Equal(1, wire.Count(l => TxStartsWith(l, "F>")));
        Assert.Empty(host.Store.GetForwardQueue("Q0FBB"));
        foreach (long n in numbers)
        {
            Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(n)!.Status);
        }
    }

    /// <summary>OUT-05: a message over the partner's MaxTxSize is skipped + Held via the onOversize
    /// callback; the under-cap message in the same queue still forwards.</summary>
    [Fact]
    public async Task OversizeMessage_HeldNotProposed_SmallStillForwarded()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost("Q0PDN", "#42.GBR.EURO");
        var partner = new Partner { Call = "Q0FBB", AtCalls = ["Q0FBB"], MaxTxSize = 200 };
        host.Store.UpsertPartner(partner);

        long secs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Message small = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal, From = "M0LTE", Recipients = ["Q0FBB"], At = "Q0FBB",
            Bid = string.Create(CultureInfo.InvariantCulture, $"{secs % 10000:D4}s_Q0PDN"),
            Subject = $"small {secs}", Body = Encoding.Latin1.GetBytes($"small body {secs}\r"),
        });
        host.Routing.RouteMessage(small);
        Message large = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal, From = "M0LTE", Recipients = ["Q0FBB"], At = "Q0FBB",
            Bid = string.Create(CultureInfo.InvariantCulture, $"{secs % 10000:D4}L_Q0PDN"),
            Subject = $"large {secs}", Body = Encoding.Latin1.GetBytes(new string('X', 600) + "\r"),
        });
        host.Routing.RouteMessage(large);

        var held = new List<long>();
        IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
            host.Store.GetForwardQueue("Q0FBB"), partner, host.Identity, TimeProvider.System, NullLogger.Instance,
            onOversize: (number, bytes) => { host.Store.HoldMessage(number, $"too large for Q0FBB ({bytes} bytes)"); held.Add(number); });

        Assert.Single(outbound);                       // only the small one is proposed
        Assert.Equal(large.Number, Assert.Single(held));

        (FbbSessionResult result, _) = await ForwardOnceAsync(host, partner, outbound, ct);

        Assert.True(result.Completed && result.Graceful, "session was not clean");
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(small.Number)!.Status);
        Assert.Equal(MessageStatus.Held, host.Store.GetMessage(large.Number)!.Status);
    }

    /// <summary>OUT-17: pdn OFFERS B2 (partner AllowB2F = true → '2' in our SID), but canonical
    /// xfbbd 7.0.11 advertises no '2', so B2 must NOT activate — the transfer stays FA/B1 and the
    /// message is still Forwarded. The negative gate against a silent B2 mis-activation.</summary>
    [Fact]
    public async Task B2Offered_ButPeerIsB1Only_FallsBackToFa_AndForwards()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost("Q0PDN", "#42.GBR.EURO");
        var partner = new Partner { Call = "Q0FBB", AtCalls = ["Q0FBB"], AllowB2F = true };
        host.Store.UpsertPartner(partner);

        long secs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Message m = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal, From = "M0LTE", Recipients = ["Q0FBB"], At = "Q0FBB",
            Bid = string.Create(CultureInfo.InvariantCulture, $"{secs % 100000:D5}_Q0PDN"),
            Subject = $"b2neg {secs}", Body = Encoding.Latin1.GetBytes($"b2-negotiation body {secs}\r"),
        });
        host.Routing.RouteMessage(m);

        (FbbSessionResult result, List<string> wire) = await ForwardOnceAsync(host, partner, BuildOutbound(host, partner), ct);

        Assert.True(result.Completed && result.Graceful, "session was not clean");
        Assert.StartsWith("[FBB-7.0.11", result.PeerSidRaw);
        Assert.False(result.B2Active, "B2 activated against a B1-only xfbbd (silent mis-negotiation)");
        Assert.Contains(wire, l => TxStartsWith(l, "FA "));     // B1 proposal
        Assert.DoesNotContain(wire, l => TxStartsWith(l, "FC ")); // never FC
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(m.Number)!.Status);
    }

    private static bool IsTx(string line) => line.StartsWith("TX", StringComparison.Ordinal);

    // The readable frame content after the "TX/RX [len] " prefix. A real protocol line (FA/FC/F>)
    // is flushed as its own SendAsync, so its content STARTS with the token; LZHUF transfer blocks
    // render arbitrary bytes that can contain "F>"/"FC" mid-stream — so match on the start only.
    private static string Content(string line)
    {
        int i = line.IndexOf("] ", StringComparison.Ordinal);
        return i < 0 ? line : line[(i + 2)..];
    }

    private static bool TxStartsWith(string line, string token) =>
        IsTx(line) && Content(line).StartsWith(token, StringComparison.Ordinal);

    private static IReadOnlyList<OutboundItem> BuildOutbound(InteropBbsHost host, Partner partner) =>
        OutboundBuilder.Build(
            host.Store.GetForwardQueue(partner.Call), partner, host.Identity,
            TimeProvider.System, NullLogger.Instance);

    /// <summary>Dials real xfbbd over AXUDP, runs the production caller once with the wire captured,
    /// and tears the link down. One session per call (one host-port-10093 bind, released on dispose).</summary>
    private static async Task<(FbbSessionResult Result, List<string> Wire)> ForwardOnceAsync(
        InteropBbsHost host, Partner partner, IReadOnlyList<OutboundItem> outbound, CancellationToken ct)
    {
        var wire = new List<string>();
        await using Ax25Endpoint endpoint = await Ax25Endpoint.AttachAxudpAsync(Vm, localPort: 10093, "Q0PDN-1", ct);
        Ax25ByteSession raw = await endpoint.ConnectAsync("Q0FBB-1", ct);
        var link = new LoggingFbbConnection(raw, wire);
        var runner = new FbbSessionRunner(
            host.Store, host.Receiver, host.Identity, InteropBbsHost.Version,
            TimeProvider.System, NullLogger<FbbSessionRunner>.Instance);
        FbbSessionResult result = await runner.RunCallerAsync(link, partner, outbound, ct);
        await raw.CloseAsync(ct);
        return (result, wire);
    }
}
