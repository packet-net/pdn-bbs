using System.Globalization;
using System.Net;
using System.Text;
using Bbs.Core;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Interop.Tests;

/// <summary>
/// THE POC — pdn-bbs's REAL, SHIPPING forwarding code forwards a real message into REAL
/// F6FBB (xfbbd) over AXUDP. It drives the production <see cref="FbbSessionRunner"/> (the
/// same class that ships over RHP and FBBPORT) over an AXUDP-backed
/// <see cref="Bbs.Host.Rhp.IFbbConnection"/> — NOT a test transcription. pdn-bbs is the
/// CALLER (Q0PDN-1) dialling xfbbd (Q0FBB-1); it proposes one personal message via FBB B1
/// compressed forwarding, xfbbd accepts ('+'), pdn-bbs streams the LZHUF/CRC16/SOH-STX-EOT
/// object, and the production store marks it Forwarded. Canonical FBB (the original
/// Jean-Paul Roubelat code), not LinBPQ's reimplementation and not captured samples.
///
/// Requires the f6fbb-on-kernel VM booted (run/run-vm.sh NET=tap). On-disk persistence in
/// xfbbd's store is verified out-of-band (loop-mount the halted ext4 image and grep
/// /usr/local/var/ax25/fbb/mail/mail*/m_*.mes for the nonce).
/// </summary>
[Trait("Category", "InteropF6fbb")]
[Collection(F6fbbCollection.Name)]
public class F6fbbOutboundPocTests
{
    private static IPEndPoint Vm => F6fbbRig.Endpoint;

    [SkippableFact]
    public async Task PocOutbound_ForwardsOneMessageIntoF6fbb()
    {
        await F6fbbRig.RequireAsync();

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost("Q0PDN", "#42.GBR.EURO");

        // Fresh BID each run, or xfbbd dedups our proposal to '-' and stores nothing.
        string nonce = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}");
        string bid = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000:D5}_Q0PDN");
        string title = $"pdn-out {nonce}";
        string bodyText = $"pdn-bbs outbound interop body {nonce}";

        var partner = new Partner { Call = "Q0FBB", AtCalls = ["Q0FBB"] };
        host.Store.UpsertPartner(partner);
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["Q0FBB"],
            At = "Q0FBB",
            Bid = bid,
            Subject = title,
            Body = Encoding.Latin1.GetBytes(bodyText + "\r"),
        });
        host.Routing.RouteMessage(stored);
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("Q0FBB")).Number);

        // Dial real xfbbd over AXUDP. The session handle IS an IFbbConnection.
        await using var endpoint = await Ax25Endpoint.AttachAxudpAsync(Vm, localPort: 10093, "Q0PDN-1", ct);
        Ax25ByteSession link = await endpoint.ConnectAsync("Q0FBB-1", ct);

        // Drive the PRODUCTION forwarding runner — the exact class that ships over RHP/FBBPORT.
        var runner = new FbbSessionRunner(
            host.Store, host.Receiver, host.Identity, InteropBbsHost.Version,
            TimeProvider.System, NullLogger<FbbSessionRunner>.Instance);

        IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
            host.Store.GetForwardQueue(partner.Call), partner, host.Identity,
            TimeProvider.System, NullLogger.Instance);
        FbbSessionResult result = await runner.RunCallerAsync(link, partner, outbound, ct);
        await link.CloseAsync(ct);

        // On the wire: the FBB session reached FF/FQ.
        Assert.True(result.Completed, "FBB session did not complete (link died or idle timeout)");
        Assert.True(result.Graceful, "FBB session did not close gracefully (FF/FQ)");

        // DIVERGENCE PROBE: the peer is canonical F6FBB ([FBB-7.0.11-…$]), not LinBPQ ([BPQ-…]).
        Assert.StartsWith("[FBB-7.0.11", result.PeerSidRaw);

        // [FBB-…$] advertises no '2', so B2 must NOT activate (B1 lingua franca).
        Assert.False(result.B2Active);

        // The production runner cleared the queue (MarkForwarded on xfbbd's '+'): real store outcome.
        Assert.Empty(host.Store.GetForwardQueue("Q0FBB"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);
    }
}
