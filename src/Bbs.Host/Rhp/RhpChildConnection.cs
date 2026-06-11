using System.Threading.Channels;

namespace Bbs.Host.Rhp;

/// <summary>
/// One AX.25 stream carried over the node's RHPv2 surface — either an accepted inbound
/// child handle or an outbound <c>open</c>(Active) handle. Inbound bytes arrive from the
/// link's <c>recv</c> pushes; <see cref="ReceiveAsync"/> returns <see langword="null"/>
/// once the stream is closed (server <c>close</c> push, link loss or local close).
/// </summary>
public sealed class RhpChildConnection
{
    private readonly RhpNodeLink _link;
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    internal RhpChildConnection(RhpNodeLink link, int handle, string remoteCallsign)
    {
        _link = link;
        Handle = handle;
        RemoteCallsign = remoteCallsign;
    }

    /// <summary>The RHP handle for this stream.</summary>
    public int Handle { get; }

    /// <summary>The far station's callsign as reported by the node (SSID included).</summary>
    public string RemoteCallsign { get; }

    /// <summary>
    /// Awaits the next inbound chunk; <see langword="null"/> when the stream has closed.
    /// </summary>
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Sends bytes to the far station (`send` on the handle).</summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        _link.SendOnChildAsync(Handle, data, cancellationToken);

    /// <summary>
    /// Closes the stream at the node. Best-effort: failures (link already down, handle
    /// already closed) are swallowed — the local channel completes either way.
    /// </summary>
    public Task CloseAsync(CancellationToken cancellationToken) =>
        _link.CloseChildAsync(Handle, cancellationToken);

    internal void Deliver(byte[] data) => _inbound.Writer.TryWrite(data);

    internal void MarkClosed() => _inbound.Writer.TryComplete();
}
