using System.Globalization;
using System.Text;

namespace Bbs.Fbb;

/// <summary>
/// Emit-side helpers for the FBB binary blocked transfer — spec §3.6: SOH
/// header (<c>SOH len title NUL offset-ascii NUL</c>), STX data blocks, EOT
/// trailer with the 8-bit two's-complement checksum over STX payload bytes
/// only. "Unlike YAPP transfers, there is no individual packet
/// acknowledgement" [FBB-PROTO].
/// </summary>
public static class BlockFraming
{
    /// <summary>Start-of-header marker.</summary>
    public const byte Soh = 0x01;

    /// <summary>Start-of-data-block marker.</summary>
    public const byte Stx = 0x02;

    /// <summary>End-of-transfer marker.</summary>
    public const byte Eot = 0x04;

    /// <summary>
    /// Largest STX payload we emit. LinBPQ emits 250-byte blocks in B1 mode
    /// (spec §3.6); receivers must accept 1-256.
    /// </summary>
    public const int MaxEmittedBlockSize = 250;

    /// <summary>FBB's title limit in the SOH header (BPQ truncates >60 on receive) — spec §3.6.</summary>
    public const int MaxTitleLength = 80;

    /// <summary>The offset field is "1 to 6 bytes" of ASCII decimal — spec §3.6.</summary>
    public const int MaxOffset = 999999;

    /// <summary>
    /// Encodes the SOH header block. The offset is rendered right-justified
    /// in 6 characters (<c>%6d</c>), LinBPQ's B1 shape — spec §3.6: "We
    /// SHOULD send 0 or the granted offset, any width 1-6".
    /// </summary>
    /// <exception cref="FbbProtocolException">The title or offset exceeds its wire limit.</exception>
    public static byte[] EncodeHeader(string title, int offset)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (title.Length > MaxTitleLength || title.Contains('\0', StringComparison.Ordinal))
        {
            throw new FbbProtocolException($"SOH title must be ≤{MaxTitleLength} chars with no NUL (spec §3.6).");
        }

        if (offset is < 0 or > MaxOffset)
        {
            throw new FbbProtocolException("SOH offset must be 0-999999 (1-6 ASCII digits, spec §3.6).");
        }

        var offsetText = offset.ToString(CultureInfo.InvariantCulture).PadLeft(6);
        var titleBytes = Encoding.Latin1.GetBytes(title);
        var len = titleBytes.Length + offsetText.Length + 2;
        var header = new byte[2 + len];
        header[0] = Soh;
        header[1] = (byte)len;
        titleBytes.CopyTo(header, 2);
        header[2 + titleBytes.Length] = 0x00;
        Encoding.ASCII.GetBytes(offsetText, header.AsSpan(3 + titleBytes.Length));
        header[^1] = 0x00;
        return header;
    }

    /// <summary>
    /// Computes the EOT checksum: two's complement of the 8-bit sum of all
    /// STX-block payload bytes — not the SOH header, not the STX/size
    /// framing bytes [FBB-PROTO; BPQ-SRC:1263; spec §3.6].
    /// </summary>
    public static byte ComputeTrailerChecksum(ReadOnlySpan<byte> payload)
    {
        var sum = 0;
        foreach (var b in payload)
        {
            sum += b;
        }

        return (byte)(-sum);
    }

    /// <summary>
    /// Frames one complete message transfer: SOH header, the payload split
    /// into ≤250-byte STX blocks, then EOT + checksum (spec §3.6).
    /// <paramref name="payload"/> is the exact byte run to ship — for a
    /// resumed transfer the caller slices the compressed object first
    /// (spec §3.8).
    /// </summary>
    public static byte[] EncodeMessage(string title, int offset, ReadOnlySpan<byte> payload)
    {
        var header = EncodeHeader(title, offset);
        var blockCount = (payload.Length + MaxEmittedBlockSize - 1) / MaxEmittedBlockSize;
        var result = new byte[header.Length + payload.Length + (blockCount * 2) + 2];
        header.CopyTo(result, 0);
        var w = header.Length;
        var remaining = payload;
        while (remaining.Length > 0)
        {
            var n = Math.Min(remaining.Length, MaxEmittedBlockSize);
            result[w++] = Stx;
            result[w++] = (byte)n;
            remaining[..n].CopyTo(result.AsSpan(w));
            w += n;
            remaining = remaining[n..];
        }

        result[w++] = Eot;
        result[w] = ComputeTrailerChecksum(payload);
        return result;
    }
}

/// <summary>Outcome of feeding bytes to a <see cref="FbbBlockReader"/>.</summary>
public enum FbbBlockReaderStatus
{
    /// <summary>The transfer is incomplete; feed more bytes.</summary>
    NeedMoreData = 0,

    /// <summary>The EOT arrived and its checksum verified; <see cref="FbbBlockReader.Payload"/> is complete.</summary>
    Complete,

    /// <summary>
    /// The EOT checksum failed — the spec's
    /// <c>*** Message Checksum Error</c> class of failure (spec §3.6/§3.12).
    /// </summary>
    ChecksumMismatch,

    /// <summary>A structurally invalid byte arrived (not SOH/STX/EOT where one was required).</summary>
    FramingError,
}

/// <summary>
/// Incremental, sans-IO parser for one SOH/STX/EOT message transfer —
/// spec §3.6. Parses both the <c>"0"</c> and the right-justified
/// <c>%6d</c>/<c>%06d</c> offset forms ("it parses leniently — atoi after
/// the title's NUL" [BPQ-SRC]), treats an STX length byte of 0 as 256, and
/// parse-tolerates BPQ's 6-byte restart preamble on resumed transfers
/// without storing it (its bytes still count toward the EOT checksum —
/// spec §3.8, [VERIFY-ORACLE #12]).
/// </summary>
public sealed class FbbBlockReader
{
    private enum State
    {
        ExpectSoh,
        HeaderLength,
        HeaderBody,
        ExpectMarker,
        BlockLength,
        BlockData,
        Trailer,
        Done,
        Failed,
    }

    private readonly List<byte> _headerBody = [];
    private readonly List<byte> _payload = [];
    private State _state = State.ExpectSoh;
    private int _headerLength;
    private int _blockRemaining;
    private bool _firstBlock = true;
    private bool _skippingPreamble;
    private int _checksumAccumulator;

    /// <summary>The title from the SOH header (Latin-1; available once the header has parsed).</summary>
    public string Title { get; private set; } = "";

    /// <summary>The resume offset from the SOH header (0 for a from-scratch transfer).</summary>
    public int Offset { get; private set; }

    /// <summary>The concatenated STX payloads (the compressed object), valid once Complete.</summary>
    public ReadOnlyMemory<byte> Payload => _payload.ToArray();

    /// <summary>The checksum byte received in the EOT trailer.</summary>
    public byte ReceivedChecksum { get; private set; }

    /// <summary>The checksum computed over the received payload bytes.</summary>
    public byte ComputedChecksum => (byte)(-_checksumAccumulator);

    /// <summary>
    /// Consumes bytes from <paramref name="data"/>. Stops consuming at the
    /// end of the transfer so trailing bytes (the next protocol line) remain
    /// for the caller; <paramref name="consumed"/> reports how many bytes
    /// were taken.
    /// </summary>
    public FbbBlockReaderStatus Feed(ReadOnlySpan<byte> data, out int consumed)
    {
        consumed = 0;
        while (consumed < data.Length && _state is not (State.Done or State.Failed))
        {
            var b = data[consumed];
            switch (_state)
            {
                case State.ExpectSoh:
                    if (b != BlockFraming.Soh)
                    {
                        _state = State.Failed;
                        return FbbBlockReaderStatus.FramingError;
                    }

                    consumed++;
                    _state = State.HeaderLength;
                    break;

                case State.HeaderLength:
                    consumed++;
                    _headerLength = b;
                    if (_headerLength < 2)
                    {
                        _state = State.Failed;
                        return FbbBlockReaderStatus.FramingError;
                    }

                    _state = State.HeaderBody;
                    break;

                case State.HeaderBody:
                    var headerTake = Math.Min(_headerLength - _headerBody.Count, data.Length - consumed);
                    for (var i = 0; i < headerTake; i++)
                    {
                        _headerBody.Add(data[consumed + i]);
                    }

                    consumed += headerTake;
                    if (_headerBody.Count == _headerLength)
                    {
                        if (!ParseHeaderBody())
                        {
                            _state = State.Failed;
                            return FbbBlockReaderStatus.FramingError;
                        }

                        _state = State.ExpectMarker;
                    }

                    break;

                case State.ExpectMarker:
                    consumed++;
                    if (b == BlockFraming.Stx)
                    {
                        _state = State.BlockLength;
                    }
                    else if (b == BlockFraming.Eot)
                    {
                        _state = State.Trailer;
                    }
                    else
                    {
                        _state = State.Failed;
                        return FbbBlockReaderStatus.FramingError;
                    }

                    break;

                case State.BlockLength:
                    consumed++;
                    _blockRemaining = b == 0 ? 256 : b; // length byte 0 = 256 (spec §3.6)

                    // BPQ's restart quirk: a resumed transfer opens with a
                    // 6-byte STX block repeating the original CRC+length;
                    // skip its payload but keep it in the checksum
                    // (spec §3.8, [VERIFY-ORACLE #12]).
                    _skippingPreamble = _firstBlock && Offset > 0 && _blockRemaining == 6;
                    _firstBlock = false;
                    _state = State.BlockData;
                    break;

                case State.BlockData:
                    var take = Math.Min(_blockRemaining, data.Length - consumed);
                    for (var i = 0; i < take; i++)
                    {
                        var pb = data[consumed + i];
                        _checksumAccumulator += pb;
                        if (!_skippingPreamble)
                        {
                            _payload.Add(pb);
                        }
                    }

                    consumed += take;
                    _blockRemaining -= take;
                    if (_blockRemaining == 0)
                    {
                        _state = State.ExpectMarker;
                    }

                    break;

                case State.Trailer:
                    consumed++;
                    ReceivedChecksum = b;
                    if (((_checksumAccumulator + b) & 0xFF) != 0)
                    {
                        _state = State.Failed;
                        return FbbBlockReaderStatus.ChecksumMismatch;
                    }

                    _state = State.Done;
                    return FbbBlockReaderStatus.Complete;

                case State.Done:
                case State.Failed:
                default:
                    return FbbBlockReaderStatus.FramingError;
            }
        }

        return _state switch
        {
            State.Done => FbbBlockReaderStatus.Complete,
            State.Failed => FbbBlockReaderStatus.FramingError,
            _ => FbbBlockReaderStatus.NeedMoreData,
        };
    }

    private bool ParseHeaderBody()
    {
        var body = _headerBody.ToArray();
        var firstNul = Array.IndexOf(body, (byte)0);
        if (firstNul < 0)
        {
            return false; // no title terminator
        }

        Title = Encoding.Latin1.GetString(body, 0, firstNul);

        // Lenient atoi after the title's NUL [BPQ-SRC]: skip whitespace,
        // take optional digits, ignore anything after (incl. the final NUL).
        var i = firstNul + 1;
        while (i < body.Length && body[i] is 0x20 or 0x09)
        {
            i++;
        }

        var value = 0;
        while (i < body.Length && body[i] is >= (byte)'0' and <= (byte)'9')
        {
            value = (value * 10) + (body[i] - '0');
            if (value > BlockFraming.MaxOffset)
            {
                return false;
            }

            i++;
        }

        Offset = value;
        return true;
    }
}
