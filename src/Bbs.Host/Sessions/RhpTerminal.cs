using System.Text;
using Bbs.Console;
using Bbs.Host.Rhp;

namespace Bbs.Host.Sessions;

/// <summary>
/// <see cref="IBbsTerminal"/> over an RHP child connection: Latin-1 both ways, inbound
/// CR/CRLF/LF-tolerant line reads, outbound text sent as-is (the console engine already
/// emits the CR discipline the RF side wants). The demux hands over its peek state — the
/// line assembler plus any lines completed during the first-line sniff — so typed-ahead
/// input is not lost.
/// </summary>
public sealed class RhpTerminal : IBbsTerminal
{
    private readonly RhpChildConnection _child;
    private readonly LineAssembler _assembler;
    private readonly Queue<string> _pending;

    /// <summary>Wraps <paramref name="child"/>, optionally adopting demux peek state.</summary>
    public RhpTerminal(RhpChildConnection child, LineAssembler? assembler = null, Queue<string>? pendingLines = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        _child = child;
        _assembler = assembler ?? new LineAssembler();
        _pending = pendingLines ?? new Queue<string>();
    }

    /// <inheritdoc/>
    public string RemoteCallsign => _child.RemoteCallsign;

    /// <inheritdoc/>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (_pending.Count == 0)
        {
            byte[]? data = await _child.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                return null;
            }

            foreach (string line in _assembler.Feed(data))
            {
                _pending.Enqueue(line);
            }
        }

        return _pending.Dequeue();
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            await _child.SendAsync(Encoding.Latin1.GetBytes(text), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The link or stream died under the session — surface it the way the console
            // engine expects (it converts this to BbsSessionEndReason.Drop).
            throw new BbsTerminalClosedException("The RHP stream closed.", ex);
        }
    }
}
