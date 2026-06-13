using System.Buffers;
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

    private readonly Stream _stream;
    private readonly int _maxMessageBytes;
    private readonly byte[] _readBuffer = new byte[8192];
    private int _bufferStart;
    private int _bufferEnd;

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

    /// <summary>Writes a US-ASCII response string verbatim (it must already end with CRLF).</summary>
    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose() => _stream.Dispose();
}
