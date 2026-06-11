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
/// <remarks>
/// First-line gate (the greet-immediately demux, design decision 1): the console session
/// starts the moment a child is accepted so its greeting flows immediately, but its FIRST
/// read parks on <paramref name="firstLineGate"/> until the demux has decided Fbb-vs-console
/// from the first inbound line. The gate resolving <see langword="true"/> releases input to
/// the console; <see langword="false"/> means the stream was handed to the FBB answerer (or
/// died during the peek) — reads return <see langword="null"/> and the console unwinds as a
/// Drop having consumed no input and written nothing further. This is what guarantees a
/// partner's SID is never eaten by the new-user name prompt (compat spec §1.1).
/// </remarks>
public sealed class RhpTerminal : IBbsTerminal
{
    private readonly RhpChildConnection _child;
    private readonly LineAssembler _assembler;
    private readonly Queue<string> _pending;
    private Task<bool>? _gate;

    /// <summary>Wraps <paramref name="child"/>, optionally adopting demux peek state and a first-line gate.</summary>
    public RhpTerminal(
        RhpChildConnection child,
        LineAssembler? assembler = null,
        Queue<string>? pendingLines = null,
        Task<bool>? firstLineGate = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        _child = child;
        _assembler = assembler ?? new LineAssembler();
        _pending = pendingLines ?? new Queue<string>();
        _gate = firstLineGate;
    }

    /// <inheritdoc/>
    public string RemoteCallsign => _child.RemoteCallsign;

    /// <inheritdoc/>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_gate is { } gate)
        {
            if (!await gate.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                return null; // handed off to the FBB answerer (or closed during the peek)
            }

            _gate = null;
        }

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
