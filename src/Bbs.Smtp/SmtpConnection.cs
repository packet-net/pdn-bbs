using System.Buffers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Bbs.Smtp;

/// <summary>
/// The line transport for one SMTP connection over a duplex <see cref="Stream"/> (a plaintext socket
/// stream, or an <see cref="System.Net.Security.SslStream"/>). It reads CRLF command lines and writes
/// response strings, plus the special <c>DATA</c> reader that consumes the message up to the
/// <c>CRLF.CRLF</c> terminator, performing RFC 5321 §4.5.2 dot-unstuffing (a leading <c>.</c> on a line
/// is stripped; the line <c>.</c> alone ends the data) and capping the total at a configured ceiling.
/// </summary>
/// <remarks>
/// Bytes are read and decoded as Latin-1 so every octet survives 1:1 into the parser/MIME loader (the
/// same byte-preserving choice as the IMAP transport). The DATA reader returns the raw message octets
/// (the same Latin-1 round-trip) for MimeKit to parse.
/// </remarks>
public sealed class SmtpConnection : IDisposable
{
    // A generous cap so a malformed client can't drive us out of memory on one command line.
    private const int MaxCommandBytes = 1 << 16; // 64 KiB — SMTP command lines are tiny (RFC 5321 §4.5.3.1.4 caps at 512)

    private Stream _stream;
    private readonly int _maxMessageBytes;
    private readonly byte[] _readBuffer = new byte[8192];
    private int _bufferStart;
    private int _bufferEnd;

    // When the transport is upgraded in place (STARTTLS), the new SslStream is owned here so it is disposed
    // with the connection. The original inner stream is closed by the SslStream (leaveInnerStreamOpen:false).
    private SslStream? _upgradedStream;

    /// <summary>Wraps <paramref name="stream"/> as an SMTP transport with a <paramref name="maxMessageBytes"/> DATA cap.</summary>
    public SmtpConnection(Stream stream, int maxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        _stream = stream;
        _maxMessageBytes = maxMessageBytes;
    }

    /// <summary>Reads one CRLF-terminated command line (the CRLF stripped), or null at end of stream.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new ArrayBufferWriter<byte>(128);
        while (true)
        {
            if (_bufferStart >= _bufferEnd)
            {
                _bufferStart = 0;
                _bufferEnd = await _stream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
                if (_bufferEnd == 0)
                {
                    return bytes.WrittenCount == 0 ? null : Encoding.Latin1.GetString(bytes.WrittenSpan);
                }
            }

            while (_bufferStart < _bufferEnd)
            {
                byte b = _readBuffer[_bufferStart++];
                if (b == '\n')
                {
                    ReadOnlySpan<byte> span = bytes.WrittenSpan;
                    if (span.Length > 0 && span[^1] == '\r')
                    {
                        span = span[..^1];
                    }

                    return Encoding.Latin1.GetString(span);
                }

                bytes.Write([b]);
                if (bytes.WrittenCount > MaxCommandBytes)
                {
                    throw new InvalidOperationException("SMTP command line exceeded the maximum length.");
                }
            }
        }
    }

    /// <summary>
    /// Reads the DATA payload up to the terminating line <c>.</c> (RFC 5321 §4.5.2): each data line is
    /// CRLF-joined into the result, a leading <c>.</c> is dot-unstuffed, and the bare <c>.</c> line ends
    /// the message. The returned octets are the message MimeKit parses. Throws if the total exceeds the
    /// configured ceiling, or at EOF before the terminator.
    /// </summary>
    public async Task<byte[]> ReadDataAsync(CancellationToken cancellationToken)
    {
        var message = new ArrayBufferWriter<byte>(4096);
        bool first = true;
        while (true)
        {
            string? line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new EndOfStreamException("Connection closed while reading SMTP DATA.");
            }

            if (line == ".")
            {
                break; // CRLF.CRLF terminator
            }

            // Dot-unstuffing: a line that began with '.' had an extra '.' prepended by the client.
            if (line.StartsWith('.'))
            {
                line = line[1..];
            }

            if (!first)
            {
                message.Write("\r\n"u8);
            }

            first = false;
            message.Write(Encoding.Latin1.GetBytes(line));

            if (message.WrittenCount > _maxMessageBytes)
            {
                throw new InvalidOperationException("SMTP message exceeded the maximum size.");
            }
        }

        return message.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Upgrades the underlying transport to TLS in place (RFC 3207 STARTTLS): the CURRENT inner stream (the
    /// plaintext socket stream) is wrapped in a server-authenticated <see cref="SslStream"/>, and on a
    /// successful handshake the connection's stream is swapped to it so every later read/write rides the
    /// encrypted channel. The handshake is bounded by <paramref name="handshakeTimeout"/> (mirroring the
    /// implicit-TLS path) so a peer that completes <c>STARTTLS</c> but never sends a ClientHello cannot hang
    /// the connection. Any bytes buffered <b>before</b> the handshake are discarded — post-STARTTLS commands
    /// MUST come only from the TLS stream, so a pre-handshake pipelined command (a known injection vector)
    /// is never honoured. Throws on handshake failure or timeout (the caller closes the connection).
    /// </summary>
    /// <param name="certificate">The server certificate presented in the handshake (shared with implicit TLS).</param>
    /// <param name="handshakeTimeout">How long to wait for the TLS handshake before failing fast.</param>
    /// <param name="cancellationToken">Cancels the handshake (server shutdown).</param>
    public async Task UpgradeToServerTlsAsync(
        X509Certificate2 certificate, TimeSpan handshakeTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (_upgradedStream is not null)
        {
            throw new InvalidOperationException("The SMTP connection has already been upgraded to TLS.");
        }

        // Discard anything buffered before the handshake: post-STARTTLS reads must come only from the TLS
        // stream (RFC 3207 §4.2 / §6 — do not honour commands pipelined across the upgrade boundary).
        _bufferStart = 0;
        _bufferEnd = 0;

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCts.CancelAfter(handshakeTimeout);
        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                handshakeCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Dispose the half-built SslStream (which also closes the inner stream) and surface the fault so
            // the caller closes the connection.
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Swap the live transport to the encrypted stream; it is now owned by the connection.
        _stream = ssl;
        _upgradedStream = ssl;
    }

    /// <summary>Writes a US-ASCII response string verbatim (it must already end with CRLF).</summary>
    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Disposes the live stream — after a STARTTLS upgrade that is the <see cref="SslStream"/> we own, which
    /// in turn closes the inner socket stream (it was wrapped with <c>leaveInnerStreamOpen:false</c>). The
    /// server's own <c>using</c> on the original <c>NetworkStream</c> is then a harmless second dispose.
    /// </remarks>
    public void Dispose() => _stream.Dispose();
}
