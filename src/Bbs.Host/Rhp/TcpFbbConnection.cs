using System.Net.Sockets;

namespace Bbs.Host.Rhp;

/// <summary>
/// An <see cref="IFbbConnection"/> over a raw TCP socket — the bearer for inbound FBB-over-TCP
/// forwarding (BPQ <c>FBBPORT</c>, issue #40). It carries the same FBB session the AX.25/RHP path
/// does; only the transport differs.
///
/// <para><see cref="RemoteCallsign"/> is the callsign the partner presented in the in-protocol FBB
/// login handshake (connectionless TCP has no link-layer callsign), so the shared
/// <see cref="Bbs.Host.Forwarding.FbbSessionRunner"/> resolves the partner and applies its policy by
/// that identity exactly as for an RHP child.</para>
/// </summary>
public sealed class TcpFbbConnection(Stream stream, string remoteCallsign) : IFbbConnection
{
    private const int ReadBufferSize = 4096;
    private readonly byte[] _buffer = new byte[ReadBufferSize];

    /// <inheritdoc/>
    public string RemoteCallsign { get; } = remoteCallsign;

    /// <inheritdoc/>
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            int n = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return null; // peer closed (FIN) — mirrors RhpChildConnection's null-on-close contract
            }

            return _buffer.AsSpan(0, n).ToArray();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            return null; // a dropped socket is a closed stream
        }
    }

    /// <inheritdoc/>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        try
        {
            stream.Dispose();
        }
        catch (IOException)
        {
            // best effort
        }

        return Task.CompletedTask;
    }
}
