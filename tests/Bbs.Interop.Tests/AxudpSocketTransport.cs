using System.Net;
using System.Runtime.CompilerServices;
using Packet.Ax25.Transport;
using Packet.Axudp;
using Packet.Core;

namespace Bbs.Interop.Tests;

/// <summary>
/// Presents the published <see cref="AxudpSocket"/> (Packet.Axudp) as an
/// <see cref="IAx25Transport"/> so an <c>Ax25Listener</c> runs over an AXUDP tunnel — the
/// real-F6FBB VM leg (host 192.168.76.1 ↔ VM 192.168.76.2:10093 over the f6fbbr0 bridge).
/// <para>
/// This is a faithful, byte-for-byte mirror of <c>Packet.Node.Core.Transports.AxudpFrameTransport</c>,
/// which is NOT published (it lives in the unpublished Packet.Node.Core). On send it appends the
/// 2-octet AX.25 FCS (CRC-16-CCITT, low byte first — the RFC-1226 AXIP/AXUDP wire form) that
/// <see cref="AxudpSocket"/> strips + validates on receive; the listener only ever sees the bare
/// frame body. The published <see cref="AxudpSocket"/> does the actual encapsulation; this adds
/// only the neutral <see cref="IAx25Transport"/> seam (no CSMA / TX-completion — a UDP link has
/// neither). LIFT THIS OUT the moment AxudpFrameTransport ships in a published package.
/// </para>
/// </summary>
internal sealed class AxudpSocketTransport : IAx25Transport
{
    private readonly AxudpSocket _socket;
    private readonly IPEndPoint _remote;
    private readonly TimeProvider _clock;
    private int _disposed;

    /// <summary>The local UDP port bound for receive (0 in the ctor resolves to a real ephemeral port).</summary>
    public int LocalPort => _socket.LocalPort;

    /// <summary>Open an AXUDP transport: bind <paramref name="localPort"/> and send every frame to <paramref name="remote"/>.</summary>
    public AxudpSocketTransport(IPEndPoint remote, int localPort = 0, TimeProvider? timeProvider = null)
    {
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _clock = timeProvider ?? TimeProvider.System;
        _socket = new AxudpSocket(localPort);
    }

    /// <inheritdoc/>
    public async Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        // The listener hands us the AX.25 frame body (no FCS) — that is the AXUDP datagram payload.
        // Append the 2-octet FCS (low byte first); AxudpSocket strips + validates it on the far side.
        ReadOnlySpan<byte> body = ax25.Span;
        var withFcs = new byte[body.Length + 2];
        body.CopyTo(withFcs);
        ushort fcs = Crc16Ccitt.Compute(body);
        withFcs[body.Length] = (byte)(fcs & 0xFF);
        withFcs[body.Length + 1] = (byte)((fcs >> 8) & 0xFF);
        await _socket.SendRawAsync(_remote, withFcs, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AxudpReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break; // socket disposed out from under us (shutdown)
            }

            // AxudpSocket already stripped + validated the FCS and dropped any bad-FCS datagram,
            // so result.RawFrame is the bare AX.25 frame body — yield it directly.
            yield return new Ax25InboundFrame(result.RawFrame, PortId: 0, ReceivedAt: _clock.GetUtcNow());
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _socket.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
