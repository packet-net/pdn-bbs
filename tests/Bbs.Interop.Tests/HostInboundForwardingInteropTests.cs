using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Tests;

namespace Bbs.Interop.Tests;

/// <summary>
/// Oracle → the REAL composed host (the missing host-path lane): a message posted on
/// BPQMail <c>@ PDNBBS</c> makes the oracle dial PDNBBS-1 over netsim; the AX.25 leg is
/// bridged into the production host's RHP wire (<see cref="RhpAx25Bridge"/> — nothing
/// inside the host is faked: real RhpNodeLink, real InboundDemux, real FbbSessionRunner,
/// real store). Asserts the full greet-immediately flow: our SID is the FIRST thing the
/// host sends (no caller-speaks-first deadlock), the oracle's SID answers it, and the
/// message lands in the host's store. This passing against the oracle's stock
/// <c>ConnectScript = "C 2 PDNBBS-1"</c> is also the proof that the oracle needs no
/// SKIPPROMPT to forward to us (docker/README).
/// </summary>
[Trait("Category", "Interop")]
[Collection(OracleCollection.Name)]
public class HostInboundForwardingInteropTests
{
    [Fact]
    public async Task InboundCycle_OracleDialsTheComposedHost_GreetImmediatelyDelivers()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = deadline.Token;

        // Partner records under both spellings the oracle may present on the dial
        // (its own logs strip SSIDs inbound — README delta 8 — so don't bet on one).
        // enabled: false keeps the host's own scheduler quiet; inbound is unaffected.
        await using ComposedInteropHost host = await ComposedInteropHost.StartAsync("""
            partners:
              - call: GB7BPQ
                enabled: false
              - call: GB7BPQ-1
                enabled: false
            """);

        // Listen as PDNBBS-1 BEFORE posting, so the oracle's first ~2 s dial finds us.
        await using var endpoint = await Ax25Endpoint.AttachAsync(
            OracleFixture.KissHost, OracleFixture.KissPort, InteropBbsHost.AxCall, ct);

        string nonce = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}");
        string title = $"pdn-host-in {nonce}";
        string bodyText = $"oracle to composed host inbound body {nonce}";

        using (var telnet = await TelnetBbsClient.ConnectAsync(OracleFixture.KissHost, OracleFixture.TelnetPort, ct))
        {
            await telnet.LoginAndEnterBbsAsync(ct);
            await telnet.PostMessageAsync("S M0LTE @ PDNBBS", title, bodyText, ct);
            await telnet.SignOffAsync(ct);
        }

        // Bridge each oracle dial into the host until the nonce lands: tolerates a first
        // cycle the oracle abandons and redial cycles, exactly like the adapter-path test.
        Message? received = null;
        BridgeCapture? hostOut = null;
        BridgeCapture? oracleOut = null;
        while (received is null)
        {
            Ax25ByteSession link;
            try
            {
                link = await endpoint.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"the oracle never completed a forwarding dial to the composed host; " +
                    $"host sent: [{string.Join(" | ", hostOut?.Lines ?? [])}]; " +
                    $"oracle sent: [{string.Join(" | ", oracleOut?.Lines ?? [])}]");
            }

            // Surface the dial to the host as a real accept push and pump until the
            // session closes (either side).
            FakeRhpPeer peer = await host.Server.AcceptChildAsync(link.RemoteCallsign);
            hostOut = new BridgeCapture();
            oracleOut = new BridgeCapture();
            await RhpAx25Bridge.PumpAsync(peer, link, hostOut, oracleOut, ct);

            received = host.Store
                .ListMessages(new MessageQuery { IncludeHeld = true })
                .FirstOrDefault(m => m.Subject.Contains(nonce, StringComparison.Ordinal));
        }

        // Greet-immediately on the wire: the very first line the host sent is our SID —
        // before the oracle said anything at all (a real LinBPQ caller waits for it).
        Assert.True(hostOut!.Lines.Count > 0, "the host sent nothing");
        string greeting = hostOut.Lines[0];
        Assert.True(Sid.IsSidShaped(greeting), $"the host's first line was not a SID: '{greeting}'");
        Assert.StartsWith("[PDN-", greeting, StringComparison.Ordinal);

        // The oracle answered with ITS SID (the forwarding handshake engaged).
        Assert.True(
            oracleOut!.IndexOf(Sid.IsSidShaped) >= 0,
            $"no SID from the oracle; it sent: [{string.Join(" | ", oracleOut.Lines)}]");

        // The delivery went through the production receive path into the host's store.
        Assert.Equal(MessageType.Personal, received.Type);
        Assert.Equal(MessageStatus.Unread, received.Status); // TO M0LTE @ us = local delivery
        Assert.Contains(received.Recipients, r => r.ToCall == "M0LTE");
        Assert.Equal("GB7BPQ", received.From); // the admin telnet user's callsign
        Assert.NotNull(received.ReceivedFrom);
        Assert.StartsWith("GB7BPQ", received.ReceivedFrom, StringComparison.Ordinal);
        Assert.EndsWith("_GB7BPQ-1", received.Bid, StringComparison.Ordinal);

        // Body verbatim with the oracle's R: chain intact at the head.
        string text = Encoding.Latin1.GetString(received.Body.Span);
        Assert.Contains(bodyText, text, StringComparison.Ordinal);
        IReadOnlyList<RLine> chain = RLine.ExtractLeadingRLines(text.ReplaceLineEndings("\n").Split('\n'));
        Assert.NotEmpty(chain);
        Assert.Contains(chain, r => r.Raw.Contains("GB7BPQ", StringComparison.OrdinalIgnoreCase));
    }
}
