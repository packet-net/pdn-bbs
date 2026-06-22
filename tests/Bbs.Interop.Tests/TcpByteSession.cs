using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Bbs.Fbb;

namespace Bbs.Interop.Tests;

/// <summary>
/// One FBB forwarding session over a raw TCP socket — the bearer for the LinFBB (F6FBB) oracle's
/// TCP/telnet forward port (the transport the user chose for the F6FBB interop lane). Shaped exactly
/// like <see cref="Ax25ByteSession"/> (an <see cref="IByteLink"/>) so the shared
/// <see cref="Ax25FbbSessionRunner"/> drives the same FBB FSM over it unchanged; only the transport
/// and the pre-session login differ.
///
/// <para><b>F6FBB telnet login.</b> Unlike BPQ's raw FBBPORT (callsign line, optional password,
/// straight into the SID), a LinFBB telnet/TCP forward port answers with a node login dialogue before
/// the FBB SID. <see cref="ConnectAsync"/> navigates it heuristically: it sends our callsign at the
/// first non-SID prompt and the password at a <c>password</c> prompt, then stops the moment the
/// peer's SID line (<c>[…-…]</c>) appears — handing that SID, and every byte after it, straight to the
/// FBB session so nothing is consumed. The exact prompt wording is set by the oracle's
/// <c>port.sys</c>/login config (docker/f6fbb), so this is deliberately tolerant; if a future LinFBB
/// build changes the prompts, widen the match here rather than in the FSM.</para>
/// </summary>
internal sealed class TcpByteSession : IByteLink, IAsyncDisposable
{
    private static readonly TimeSpan LoginIdle = TimeSpan.FromSeconds(20);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pump;
    private bool _disposed;

    private TcpByteSession(TcpClient client, NetworkStream stream, string remoteCallsign, byte[] leftover)
    {
        _client = client;
        _stream = stream;
        RemoteCallsign = remoteCallsign;
        if (leftover.Length > 0)
        {
            _inbound.Writer.TryWrite(leftover);
        }

        _pump = PumpAsync(_pumpCts.Token);
    }

    /// <inheritdoc/>
    public string RemoteCallsign { get; }

    /// <summary>
    /// Connects to the LinFBB oracle's TCP/telnet forward port, navigates its login dialogue as
    /// <paramref name="loginCall"/> / <paramref name="password"/>, and returns a link positioned at
    /// the peer's SID (the first FBB line). <paramref name="partnerCall"/> is the identity the runner
    /// keys the partner by (normally the same base call as <paramref name="loginCall"/>).
    /// </summary>
    public static async Task<TcpByteSession> ConnectAsync(
        string host,
        int port,
        string partnerCall,
        string loginCall,
        string password,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        NetworkStream stream = client.GetStream();

        byte[] leftover = await NavigateLoginAsync(stream, loginCall, password, cancellationToken)
            .ConfigureAwait(false);
        return new TcpByteSession(client, stream, partnerCall, leftover);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task CloseAsync(CancellationToken cancellationToken) => DisposeAsync().AsTask();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            _stream.Dispose();
            _client.Dispose();
        }
        catch (IOException)
        {
            // best effort
        }

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on cancel
        }

        _pumpCts.Dispose();
    }

    /// <summary>The background read pump: socket → inbound channel, null-on-close like the AX.25 leg.</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    break; // FIN
                }

                _inbound.Writer.TryWrite(buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
        {
            // a dropped socket / cancel is a closed stream
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Reads the pre-SID login dialogue, sending the callsign and (if prompted) password, and returns
    /// the bytes from the peer's SID line onward (already off the socket but belonging to the FBB
    /// session). Bounded by an idle timeout so a silent / unexpected dialogue fails fast.
    /// </summary>
    private static async Task<byte[]> NavigateLoginAsync(
        NetworkStream stream, string loginCall, string password, CancellationToken cancellationToken)
    {
        var acc = new List<byte>();
        var buffer = new byte[4096];
        bool sentCall = false;
        bool sentPass = false;

        while (true)
        {
            using var idle = new CancellationTokenSource(LoginIdle);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);

            int n;
            try
            {
                n = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "LinFBB telnet login stalled before the FBB SID. Bytes so far: " +
                    $"\"{Encoding.Latin1.GetString([.. acc])}\". Check docker/f6fbb port.sys/login config.");
            }

            if (n <= 0)
            {
                throw new IOException("LinFBB closed the connection during the telnet login dialogue.");
            }

            acc.AddRange(buffer.AsSpan(0, n).ToArray());
            string text = Encoding.Latin1.GetString([.. acc]);

            // The moment the SID appears, login is over: return it (and any trailing bytes) verbatim.
            int sidStart = FindSidLineStart(text);
            if (sidStart >= 0)
            {
                return [.. acc.Skip(sidStart)];
            }

            string lower = text.ToLowerInvariant();
            if (!sentCall && (lower.Contains("call") || lower.Contains("login") || lower.TrimEnd().EndsWith(':')))
            {
                await WriteLineAsync(stream, loginCall, cancellationToken).ConfigureAwait(false);
                sentCall = true;
            }
            else if (sentCall && !sentPass && lower.Contains("password"))
            {
                await WriteLineAsync(stream, password, cancellationToken).ConfigureAwait(false);
                sentPass = true;
            }
        }
    }

    /// <summary>Returns the byte offset where the first SID-shaped line (<c>[…-…]</c>) begins, or -1.</summary>
    private static int FindSidLineStart(string text)
    {
        int pos = 0;
        foreach (string rawLine in text.Split('\n'))
        {
            if (Sid.IsSidShaped(rawLine.Trim('\r', ' ', '\t')))
            {
                return pos;
            }

            pos += rawLine.Length + 1; // + the '\n' the split consumed
        }

        return -1;
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
