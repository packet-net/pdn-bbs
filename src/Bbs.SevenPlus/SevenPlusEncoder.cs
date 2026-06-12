using System.Text;

namespace Bbs.SevenPlus;

/// <summary>Line separator for encoded 7plus output.</summary>
public enum SevenPlusLineSeparator
{
    /// <summary><c>\r\n</c> — the 7plus default and what BBS forwarding expects.</summary>
    CrLf,

    /// <summary><c>\n</c>.</summary>
    Lf,

    /// <summary><c>\r</c>.</summary>
    Cr,
}

/// <summary>Options controlling 7plus encoding. Defaults match the reference encoder.</summary>
public sealed class SevenPlusEncodeOptions
{
    /// <summary>The original filename stored in the header and (when extended) the long-name line.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Target payload bytes per part. <c>null</c> or 0 → single part. Rounded up
    /// to a whole number of 62-byte code lines, capped at 512 lines (31744
    /// bytes) per part. When <see cref="PartCount"/> is set it takes precedence.
    /// </summary>
    public int? MaxPartBytes { get; init; }

    /// <summary>Split into roughly this many equal parts. Overrides <see cref="MaxPartBytes"/>.</summary>
    public int? PartCount { get; init; }

    /// <summary>Footer timestamp, unix seconds. Deterministic codec — caller supplies it.</summary>
    public long Timestamp { get; init; }

    /// <summary>Line separator for the emitted text. Default <see cref="SevenPlusLineSeparator.CrLf"/>.</summary>
    public SevenPlusLineSeparator LineSeparator { get; init; } = SevenPlusLineSeparator.CrLf;

    /// <summary>Emit the extended long-filename line in part 1. Default true (matches the reference).</summary>
    public bool ExtendedName { get; init; } = true;

    /// <summary>
    /// Optional line appended after each part's footer (the reference's <c>-t</c>);
    /// e.g. <c>/ex</c> to end an upload to a packet BBS. The decoder ignores it.
    /// </summary>
    public string? Terminator { get; init; }
}

/// <summary>
/// Encodes a binary blob into wire-faithful 7plus parts. Each part is the
/// classic header line + (part 1) extended-name line + code lines + footer line,
/// every structural line exactly 69 bytes. Output is produced as text the
/// reference decoder accepts byte-for-byte. Sans-IO and deterministic — the
/// caller supplies the timestamp.
/// </summary>
public static class SevenPlusEncoder
{
    private const int PayloadBytesPerLine = 62;
    private const int MaxLinesPerPart = 512;
    private const int DefaultBlockSize = 138 * PayloadBytesPerLine;

    /// <summary>
    /// Encodes <paramref name="content"/> into one or more 7plus parts, each a
    /// single string of 69-byte lines joined by CRLF. This is the host-facing
    /// shape: <c>maxPartBytes</c> null → a single <c>.7pl</c>-style part.
    /// </summary>
    public static IReadOnlyList<string> Encode(byte[] content, string fileName, int? maxPartBytes = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Encode(content, new SevenPlusEncodeOptions { FileName = fileName, MaxPartBytes = maxPartBytes })
            .Select(p => p.Text)
            .ToList();
    }

    /// <summary>One encoded part: its conventional filename and its full text.</summary>
    public readonly record struct EncodedPart(string Name, string Text)
    {
        /// <summary>The encoded bytes (latin1 of <see cref="Text"/>, the exact wire bytes).</summary>
        public byte[] Bytes => Latin1.GetBytes(Text);
    }

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>
    /// Encodes with full options, returning each part's conventional name plus
    /// text. Used by the simple <see cref="Encode(byte[], string, int?)"/> and by
    /// callers that need part filenames or non-default geometry/line-endings.
    /// </summary>
    public static IReadOnlyList<EncodedPart> Encode(byte[] content, SevenPlusEncodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var size = content.Length;
        if (size == 0)
        {
            throw new ArgumentException("7plus refuses to encode a zero-length file", nameof(content));
        }

        var headerName = SevenPlusLines.DosName(options.FileName);
        var baseName = SevenPlusLines.DosBaseName(options.FileName);

        var (blockLines, parts) = ComputeGeometry(size, options);
        var partPayload = blockLines * PayloadBytesPerLine;

        var sep = options.LineSeparator switch
        {
            SevenPlusLineSeparator.CrLf => "\r\n",
            SevenPlusLineSeparator.Lf => "\n",
            SevenPlusLineSeparator.Cr => "\r",
            _ => "\r\n",
        };

        var result = new List<EncodedPart>(parts);
        Span<byte> slice = stackalloc byte[PayloadBytesPerLine];

        for (var part = 1; part <= parts; part++)
        {
            var sb = new StringBuilder();

            var startByte = (part - 1) * partPayload;
            var endByte = Math.Min(startByte + partPayload, size);
            var linesInPart = part == parts && parts > 1
                ? Math.Max(1, CeilDiv(endByte - startByte, PayloadBytesPerLine))
                : blockLines;

            // The header %04X blocksize field shrinks on a short final part; the
            // %03X blockLines field stays at the full geometry.
            var blockSizeField = (linesInPart * 64) & 0xFFFF;

            AppendLine(sb, SevenPlusLines.BuildHeaderLine(new SevenPlusLines.Header(
                part, parts, headerName, size, blockSizeField, blockLines, options.ExtendedName)), sep);

            if (part == 1 && options.ExtendedName)
            {
                AppendLine(sb, SevenPlusLines.BuildExtendedNameLine(SevenPlusLines.StripPath(options.FileName)), sep);
            }

            for (var l = 0; l < linesInPart; l++)
            {
                var off = startByte + (l * PayloadBytesPerLine);
                slice.Clear();
                var available = Math.Max(0, Math.Min(PayloadBytesPerLine, size - off));
                content.AsSpan(off, available).CopyTo(slice);
                AppendLine(sb, SevenPlusLines.BuildCodeLine(slice, l), sep);
            }

            AppendLine(sb, SevenPlusLines.BuildFooterLine(headerName, part, parts, options.Timestamp), sep);

            if (!string.IsNullOrEmpty(options.Terminator))
            {
                sb.Append(options.Terminator).Append(sep);
            }

            var name = parts == 1 ? $"{baseName}.7pl" : $"{baseName}.p{part:x2}";
            result.Add(new EncodedPart(name, sb.ToString()));
        }

        return result;
    }

    private static (int BlockLines, int Parts) ComputeGeometry(int size, SevenPlusEncodeOptions options)
    {
        // Mirrors encode.c sizing: derive a per-part payload, cap at 512 lines,
        // round up to whole code lines, then count parts.
        var blocksize = options.MaxPartBytes ?? DefaultBlockSize;

        if (options.PartCount is { } pc)
        {
            if (pc < 1)
            {
                throw new ArgumentException($"PartCount must be >= 1 (got {pc})", nameof(options));
            }

            blocksize = CeilDiv(CeilDiv(size + 61, PayloadBytesPerLine), pc) * PayloadBytesPerLine;
        }

        if (blocksize == 0 || blocksize > size)
        {
            blocksize = size;
        }

        if (blocksize > MaxLinesPerPart * PayloadBytesPerLine)
        {
            blocksize = MaxLinesPerPart * PayloadBytesPerLine;
        }

        var blockLines = CeilDiv(blocksize, PayloadBytesPerLine);
        var partPayload = blockLines * PayloadBytesPerLine;
        var parts = CeilDiv(size, partPayload);
        if (parts > 255)
        {
            throw new ArgumentException(
                $"7plus supports at most 255 parts (got {parts}); choose a larger part size", nameof(options));
        }

        return (blockLines, parts);
    }

    private static void AppendLine(StringBuilder sb, byte[] line, string sep)
    {
        foreach (var b in line)
        {
            sb.Append((char)b);
        }

        sb.Append(sep);
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
