namespace Bbs.Interop.Tests;

/// <summary>
/// A bidirectional byte-stream link carrying ONE FBB forwarding session, abstracted over its
/// transport so <see cref="Ax25FbbSessionRunner"/> can drive a real <see cref="Bbs.Fbb.FbbSession"/>
/// over either bearer the interop lane uses:
/// <list type="bullet">
///   <item><see cref="Ax25ByteSession"/> — AX.25 over the net-sim simulated RF channel (the LinBPQ oracle), or</item>
///   <item><see cref="TcpByteSession"/> — a raw-TCP/telnet forwarding connection (the LinFBB / F6FBB oracle).</item>
/// </list>
/// The four members are exactly the shape the host's own <see cref="Bbs.Host.Rhp.IFbbConnection"/>
/// exposes (null-on-close <c>ReceiveAsync</c>, <c>SendAsync</c>, <c>CloseAsync</c>,
/// <c>RemoteCallsign</c>), so the runner's action pump is a 1:1 transcription of the production
/// FbbSessionRunner regardless of transport.
/// </summary>
internal interface IByteLink
{
    /// <summary>The far station's callsign for partner lookup (SSID may be present; matching is on the base call).</summary>
    string RemoteCallsign { get; }

    /// <summary>Awaits the next inbound chunk; <see langword="null"/> once the link has closed.</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Sends bytes to the far station.</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Closes the link. Best-effort; idempotent.</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}
