using System.Net;
using System.Text;

namespace Bbs.Interop.Tests;

/// <summary>
/// R2 transport smoke — the single highest-value early check. Proves a .NET AX.25 v2.0
/// (SABM) connected-mode session over AXUDP reaches REAL F6FBB (xfbbd, Q0FBB-1) through the
/// VM's ax25ipd → kernel-AX.25, by capturing the FBB SID xfbbd sends as its first line on
/// connect. This isolates the transport + link + FCS layer from the FBB protocol FSM: if
/// the SID arrives, the whole bearer is proven and the PoC is pure FBB.
///
/// Requires the f6fbb-on-kernel VM booted (run/run-vm.sh NET=tap; host 192.168.76.1 ↔
/// VM 192.168.76.2:10093). Run with: dotnet test --filter "FullyQualifiedName~F6fbbSmoke".
/// </summary>
[Trait("Category", "InteropF6fbb")]
[Collection(F6fbbCollection.Name)]
public class F6fbbSmokeTests
{
    private static IPEndPoint Vm => F6fbbRig.Endpoint;

    [SkippableFact]
    public async Task TransportSmoke_ConnectsToQ0fbb_ReceivesFbbSid()
    {
        await F6fbbRig.RequireAsync();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken ct = deadline.Token;

        await using var endpoint = await Ax25Endpoint.AttachAxudpAsync(Vm, localPort: 10093, "Q0PDN-1", ct);

        // ConnectAsync only returns once the SABM→UA handshake completed (else it throws):
        // a successful return already proves the AX.25 link is up over AXUDP.
        Ax25ByteSession link = await endpoint.ConnectAsync("Q0FBB-1", ct);
        Assert.Equal("Q0FBB-1", link.RemoteCallsign);

        // First CR-terminated line from xfbbd: it sends its SID first on every connect
        // (the proven linbpq transcript: [FBB-7.0.11-AB1FHMRX$] arrived before any banner).
        var buf = new StringBuilder();
        while (buf.ToString().IndexOf('\r') < 0 && buf.Length < 512)
        {
            byte[]? chunk = await link.ReceiveAsync(ct);
            if (chunk is null) break;
            buf.Append(Encoding.Latin1.GetString(chunk));
        }
        string captured = buf.ToString();
        await link.CloseAsync(ct);

        string firstLine = captured.Split('\r', '\n')[0];
        Assert.True(
            firstLine.Contains("[FBB-", StringComparison.Ordinal) &&
            firstLine.Contains("$]", StringComparison.Ordinal),
            $"expected a canonical FBB SID as xfbbd's first line; got: '{captured.Replace("\r", "\\r").Replace("\n", "\\n")}'");
    }
}
