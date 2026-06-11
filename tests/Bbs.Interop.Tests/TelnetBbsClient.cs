using System.Net.Sockets;
using System.Text;

namespace Bbs.Interop.Tests;

/// <summary>
/// Minimal telnet line client for driving the oracle's node prompt + BPQMail — a C# port
/// of the proven driver embedded in docker/oracle/smoke.sh (itself adapted from the
/// m0lte/linbpq integration harness): RFC 854 IAC negotiation stripped, read-until-marker
/// with a deadline, and the 0.3 s line pacing the README documents (BPQMail misparses
/// rapidly-arrived CR-separated lines that land in one buffer — spec §3.13.2 — so body
/// and /EX sent back-to-back swallow the terminator; observed live).
/// </summary>
internal sealed class TelnetBbsClient : IDisposable
{
    /// <summary>The §3.13.2 inter-line pacing (matches smoke.sh / the m0lte harness).</summary>
    private static readonly TimeSpan Pacing = TimeSpan.FromSeconds(0.3);

    private const byte Iac = 0xFF;
    private const byte Dont = 0xFE;
    private const byte Do = 0xFD;
    private const byte Wont = 0xFC;
    private const byte Will = 0xFB;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly List<byte> _buffer = [];
    private readonly byte[] _read = new byte[4096];

    private TelnetBbsClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>
    /// Connects, retrying briefly: the container can be healthy (HTTP up) a beat before
    /// the telnet listener answers (smoke.sh does the same).
    /// </summary>
    public static async Task<TelnetBbsClient> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                return new TelnetBbsClient(tcp);
            }
            catch (SocketException)
            {
                tcp.Dispose();
                if (DateTime.UtcNow > deadline)
                {
                    throw;
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sends one CR-terminated line (the node/BBS line discipline).</summary>
    public async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(line + "\r");
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads (IAC-stripped) until <paramref name="marker"/> appears; returns everything
    /// up to and including it. Throws <see cref="TimeoutException"/> with the partial
    /// transcript on a miss — every wait is a poll-with-deadline.
    /// </summary>
    public async Task<string> ReadUntilAsync(string marker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        byte[] markerBytes = Encoding.Latin1.GetBytes(marker);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            int index = IndexOf(_buffer, markerBytes);
            if (index >= 0)
            {
                int end = index + markerBytes.Length;
                string result = Encoding.Latin1.GetString([.. _buffer[..end]]);
                _buffer.RemoveRange(0, end);
                return result;
            }

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"never saw '{marker}' on telnet; buffered: '{Encoding.Latin1.GetString([.. _buffer])}'");
            }

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(remaining);
            int n;
            try
            {
                n = await _stream.ReadAsync(_read, readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"never saw '{marker}' on telnet; buffered: '{Encoding.Latin1.GetString([.. _buffer])}'");
            }

            if (n == 0)
            {
                throw new IOException($"telnet closed before '{marker}' arrived");
            }

            AppendStrippingIac(_read.AsSpan(0, n));
        }
    }

    /// <summary>Logs in at the node prompt (bpq32.cfg USER line) and enters the BBS.</summary>
    public async Task LoginAndEnterBbsAsync(CancellationToken cancellationToken)
    {
        await ReadUntilAsync("user:", TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        await SendLineAsync("admin", cancellationToken).ConfigureAwait(false);
        await ReadUntilAsync("password:", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        await SendLineAsync("admin", cancellationToken).ConfigureAwait(false);
        await ReadUntilAsync("Telnet Server\r\n", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        // Prompt is `de <BBSName>>` (spec §1.2); the welcome carries BPQMail's SID.
        await SendLineAsync("BBS", cancellationToken).ConfigureAwait(false);
        string welcome = await ReadUntilAsync($"de {OracleFixture.OracleBbsCall}>", TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        if (!welcome.Contains("[BPQ-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"no BPQMail SID in BBS welcome: '{welcome}'");
        }
    }

    /// <summary>
    /// Posts one message via the exact §1.5 entry flow and returns the acceptance text
    /// (which must carry the <c>Message: n Bid: …</c> shape).
    /// </summary>
    public async Task<string> PostMessageAsync(
        string sendLine, string title, string body, CancellationToken cancellationToken)
    {
        await SendLineAsync(sendLine, cancellationToken).ConfigureAwait(false);
        await ReadUntilAsync("Enter Title", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        await SendLineAsync(title, cancellationToken).ConfigureAwait(false);
        await ReadUntilAsync("Enter Message Text", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        // Pace body and /EX apart (smoke.sh / spec §3.13.2 — see class remarks).
        await Task.Delay(Pacing, cancellationToken).ConfigureAwait(false);
        await SendLineAsync(body, cancellationToken).ConfigureAwait(false);
        await Task.Delay(Pacing, cancellationToken).ConfigureAwait(false);
        await SendLineAsync("/EX", cancellationToken).ConfigureAwait(false);

        string acceptance = await ReadUntilAsync("Bid:", TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        acceptance += await ReadUntilAsync($"de {OracleFixture.OracleBbsCall}>", TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        if (!acceptance.Contains("Message:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"no Message:/Bid: acceptance: '{acceptance}'");
        }

        return acceptance;
    }

    /// <summary>Sends a BBS command and returns everything up to the next prompt.</summary>
    public async Task<string> CommandAsync(string command, CancellationToken cancellationToken)
    {
        await SendLineAsync(command, cancellationToken).ConfigureAwait(false);
        return await ReadUntilAsync($"de {OracleFixture.OracleBbsCall}>", TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Signs off with <c>B</c>. Over the telnet host path no <c>73 de …</c> reaches the
    /// client — the node reports <c>*** Disconnected from Stream …</c> instead (README
    /// observed delta #5) — so this tolerates every end-of-session shape.
    /// </summary>
    public async Task SignOffAsync(CancellationToken cancellationToken)
    {
        await SendLineAsync("B", cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadUntilAsync("Disconnected", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            // The disconnect race is benign — the message was already accepted.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }

    private void AppendStrippingIac(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == Iac)
            {
                if (i + 2 < data.Length && data[i + 1] is Do or Dont or Will or Wont)
                {
                    i += 2;
                }
                else if (i + 1 < data.Length)
                {
                    i += 1;
                }

                continue;
            }

            _buffer.Add(b);
        }
    }

    private static int IndexOf(List<byte> haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
