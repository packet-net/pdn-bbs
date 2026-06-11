using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.Host.Tests;

namespace Bbs.Interop.Tests;

/// <summary>
/// The composed host → oracle via NODE NAVIGATION (the spec §4.4 connect-script lane):
/// the partner's script is <c>C GB7BPQ</c> (the oracle's NODE callsign, not the BBS
/// application) followed by <c>BBS</c> — the APPLICATION verb at the node prompt
/// (bpq32.cfg <c>APPLICATION 1,BBS,…</c>). The host's scheduler resolves the script,
/// opens to the node, sends the verb, waits out the node chatter for the BBS's SID, and
/// runs the real FBB caller session — the message must land in the oracle's on-disk
/// store. Everything host-side is the production composition; the AX.25 leg rides
/// <see cref="RhpAx25Bridge"/>.
/// </summary>
[Trait("Category", "Interop")]
[Collection(OracleCollection.Name)]
public class HostOutboundScriptInteropTests
{
    [Fact]
    public async Task OutboundCycle_NavigatesNodePromptViaScript_DeliversIntoOracleStore()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        CancellationToken ct = deadline.Token;

        await using ComposedInteropHost host = await ComposedInteropHost.StartAsync("""
            partners:
              - call: GB7BPQ
                connectScript:
                  - C GB7BPQ
                  - BBS
                sendImmediately: true
                at: [GB7BPQ]
            """);

        await using var endpoint = await Ax25Endpoint.AttachAsync(
            OracleFixture.KissHost, OracleFixture.KissPort, InteropBbsHost.AxCall, ct);

        string nonce = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}");
        string bid = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000:D5}_PDNBBS");
        string title = $"pdn-script-out {nonce}";
        string bodyText = $"composed host node-navigation outbound body {nonce}";

        // Inject through the production store + routing, the way any session would.
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["GB7BPQ"],
            At = "GB7BPQ",
            Bid = bid,
            Subject = title,
            Body = Encoding.Latin1.GetBytes(bodyText + "\r"),
        });
        host.Routing.RouteMessage(stored); // queues + nudges the scheduler (sendImmediately)
        Assert.Single(host.Store.GetForwardQueue("GB7BPQ"));

        // The scheduler resolves the script: the open names the NODE, not the BBS call.
        FakeRhpPeer peer = await host.Server.NextOpenAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(OracleFixture.OracleNodeCall, peer.Remote);

        // Bridge the open onto the RF channel by dialling the node ourselves.
        Ax25ByteSession link = await endpoint.ConnectAsync(OracleFixture.OracleNodeCall, ct);
        var hostOut = new BridgeCapture();
        var oracleOut = new BridgeCapture();
        Task pump = RhpAx25Bridge.PumpAsync(peer, link, hostOut, oracleOut, ct);

        // The session must complete: queue cleared, single-partner message goes F.
        await WaitUntilAsync(
            () => host.Store.GetForwardQueue("GB7BPQ").Count == 0,
            () => $"queue never cleared; host sent: [{string.Join(" | ", hostOut.Lines)}]; " +
                  $"oracle sent: [{string.Join(" | ", oracleOut.Lines)}]",
            ct);
        await WaitUntilAsync(
            () => host.Store.GetMessage(stored.Number)!.Status == MessageStatus.Forwarded,
            () => $"message never marked Forwarded (status {host.Store.GetMessage(stored.Number)!.Status})",
            ct);
        await pump; // host closed the handle → the bridge DISCs the RF link

        // The navigation machinery on the wire: the script verb went out first, and the
        // FBB exchange (our SID) only began after it.
        int verbIndex = hostOut.IndexOf(line => line.Trim() == "BBS");
        int sidIndex = hostOut.IndexOf(line => line.StartsWith("[PDN-", StringComparison.Ordinal));
        Assert.True(verbIndex >= 0, $"the script verb BBS was never sent; host sent: [{string.Join(" | ", hostOut.Lines)}]");
        Assert.True(sidIndex > verbIndex, $"our SID (index {sidIndex}) did not follow the BBS verb (index {verbIndex})");
        Assert.True(
            oracleOut.IndexOf(l => l.StartsWith("[BPQ-", StringComparison.Ordinal)) >= 0,
            $"no BPQMail SID from the oracle; it sent: [{string.Join(" | ", oracleOut.Lines)}]");

        // And the oracle's on-disk store has the body (spec §7.4 — the primary target).
        string mes = await OracleFixture.WaitForMailFileAsync(nonce, TimeSpan.FromSeconds(30), ct);
        Assert.Contains(bodyText, mes, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> failure, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(failure());
            }

            await Task.Delay(500, ct);
        }
    }
}
