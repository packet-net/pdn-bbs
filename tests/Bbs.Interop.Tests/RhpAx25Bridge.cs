using Bbs.Host.Sessions;
using Bbs.Host.Tests;

namespace Bbs.Interop.Tests;

/// <summary>
/// Thread-safe line capture over one direction of a bridged stream, so tests can assert
/// on the transcript (e.g. "our SID is the FIRST thing the host sent") without stealing
/// bytes from the pump.
/// </summary>
internal sealed class BridgeCapture
{
    private readonly LineAssembler _lines = new();
    private readonly List<string> _completed = [];
    private readonly object _gate = new();

    /// <summary>Mirrors a chunk into the capture.</summary>
    public void Feed(byte[] data)
    {
        lock (_gate)
        {
            foreach (string line in _lines.Feed(data))
            {
                _completed.Add(line);
            }
        }
    }

    /// <summary>The completed lines so far, in order.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _completed];
            }
        }
    }

    /// <summary>Index of the first line satisfying <paramref name="predicate"/>, or -1.</summary>
    public int IndexOf(Func<string, bool> predicate)
    {
        IReadOnlyList<string> lines = Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            if (predicate(lines[i]))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Bridges one accepted/dialled stream between the composed host's RHP wire (a
/// <see cref="FakeRhpPeer"/> on the linked <see cref="FakeRhpServer"/>) and the AX.25 leg
/// to the oracle (an <see cref="Ax25ByteSession"/> over netsim). This is the seam a real
/// pdn node occupies: the host speaks its real RHPv2 client wire to the fake node, which
/// forwards the session bytes verbatim onto the simulated RF channel — the least-contrived
/// faithful path to testing the REAL composed host against the live oracle (the RHP wire
/// itself is contract-pinned by packet.net docs/rhp2-server.md and the W5 unit lane).
///
/// Close propagation both ways: the oracle's DISC completes the AX.25 session → a server
/// `close` push tells the host; the host closing its handle ends the peer channel → the
/// bridge DISCs the AX.25 link (the oracle holds dead partner sessions for ~100 s
/// otherwise — docker/README observed delta 9).
/// </summary>
internal static class RhpAx25Bridge
{
    /// <summary>
    /// Pumps until both directions have closed (or <paramref name="cancellationToken"/>
    /// fires). Chunks are mirrored into the captures.
    /// </summary>
    public static async Task PumpAsync(
        FakeRhpPeer peer,
        Ax25ByteSession ax25,
        BridgeCapture hostToOracle,
        BridgeCapture oracleToHost,
        CancellationToken cancellationToken)
    {
        Task a = OracleToHostAsync(peer, ax25, oracleToHost, cancellationToken);
        Task b = HostToOracleAsync(peer, ax25, hostToOracle, cancellationToken);
        await Task.WhenAll(a, b).ConfigureAwait(false);
    }

    private static async Task OracleToHostAsync(
        FakeRhpPeer peer, Ax25ByteSession ax25, BridgeCapture capture, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                byte[]? data = await ax25.ReceiveAsync(ct).ConfigureAwait(false);
                if (data is null)
                {
                    // The oracle hung up — tell the host via a server close push.
                    await peer.PushCloseAsync().ConfigureAwait(false);
                    return;
                }

                capture.Feed(data);
                await peer.SendBytesAsync(data).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Test deadline — the assertion that follows reports the real failure.
        }
        catch (IOException)
        {
            // The fake-node connection died under us (host shut down) — both loops end.
        }
    }

    private static async Task HostToOracleAsync(
        FakeRhpPeer peer, Ax25ByteSession ax25, BridgeCapture capture, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // Generous per-read guard far above any FBB inter-message gap; the test
                // deadline (ct) is the real bound.
                byte[] chunk = await peer.ReadChunkRawAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                capture.Feed(chunk);
                await ax25.SendAsync(chunk, ct).ConfigureAwait(false);
            }
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            // The host closed its handle — session over; release the RF channel promptly.
            await ax25.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Test deadline.
        }
        catch (TimeoutException)
        {
            // No host traffic inside the guard window — the deadline-bounded assertions
            // surface the real failure.
        }
    }
}
