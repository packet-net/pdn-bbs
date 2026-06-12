namespace Bbs.SevenPlus;

/// <summary>
/// Scans arbitrary text — a BBS mail dump, a forwarded bulletin, anything that
/// may carry one or more 7plus parts surrounded by headers, quoting and
/// signatures — and pulls out every well-formed part it finds. Tolerant of
/// LF / CRLF / CR line endings and of non-7plus lines before, between and after
/// the parts. This is sans-IO: hand it bytes, get back parsed parts.
/// </summary>
public static class SevenPlusScanner
{
    private const byte Lf = 0x0A;
    private const byte Cr = 0x0D;
    private const int LineLength = SevenPlusCrc.LineLength;

    /// <summary>
    /// Extracts every 7plus part contained in <paramref name="text"/>. A part is
    /// recognised as a valid header line followed (later) by a valid footer line;
    /// the 69-byte code lines between them are collected. Garbage lines inside a
    /// block are ignored. Parts may appear in any order and for any number of
    /// distinct files; group them by <see cref="SevenPlusPart.Identity"/>.
    /// </summary>
    public static IReadOnlyList<SevenPlusPart> ExtractParts(ReadOnlySpan<byte> text)
    {
        var lines = SplitLines(text);
        var parts = new List<SevenPlusPart>();

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i].Span;
            if (line.Length != LineLength || !SevenPlusLines.TryParseHeader(line, out var header))
            {
                i++;
                continue;
            }

            // Optional extended-name line immediately after the header (part 1).
            var bodyStart = i + 1;
            string? extendedName = null;
            if (header is { Extended: true, Part: 1 } && bodyStart < lines.Count && lines[bodyStart].Length == LineLength)
            {
                var parsed = SevenPlusLines.ParseExtendedName(lines[bodyStart].Span);
                if (parsed is not null)
                {
                    extendedName = parsed;
                    bodyStart++;
                }
            }

            // Find this block's footer: the next valid footer line.
            var footerIdx = -1;
            long? timestamp = null;
            for (var j = bodyStart; j < lines.Count; j++)
            {
                if (lines[j].Length == LineLength && SevenPlusLines.TryParseFooter(lines[j].Span, out var ts))
                {
                    footerIdx = j;
                    timestamp = ts;
                    break;
                }
            }

            if (footerIdx < 0)
            {
                // Unterminated block — no footer in the rest of the text. Stop;
                // there can be no further complete parts beyond an open block.
                break;
            }

            var codeLines = new List<byte[]>();
            for (var j = bodyStart; j < footerIdx; j++)
            {
                if (lines[j].Length == LineLength)
                {
                    codeLines.Add(lines[j].ToArray());
                }
            }

            var identity = new SevenPlusPartIdentity(
                header.HeaderName, header.FileSize, header.Parts, header.BlockLines);
            parts.Add(new SevenPlusPart(
                identity, header.Part, header.BlockLines, header.HeaderName, extendedName, timestamp, codeLines));

            i = footerIdx + 1;
        }

        return parts;
    }

    /// <inheritdoc cref="ExtractParts(System.ReadOnlySpan{byte})"/>
    public static IReadOnlyList<SevenPlusPart> ExtractParts(byte[] text) => ExtractParts(text.AsSpan());

    /// <summary>
    /// Splits a buffer into lines on any of LF, CRLF or CR. Line slices exclude
    /// the terminator; a trailing unterminated line is kept.
    /// </summary>
    private static List<ReadOnlyMemory<byte>> SplitLines(ReadOnlySpan<byte> buf)
    {
        // We must return memory the caller can keep, so copy into one backing
        // array and slice it. (Spans can't escape; the scanner keeps no other
        // reference to the input.)
        var backing = buf.ToArray();
        var lines = new List<ReadOnlyMemory<byte>>();
        var start = 0;
        var i = 0;
        while (i < backing.Length)
        {
            var b = backing[i];
            if (b == Lf)
            {
                var end = i > start && backing[i - 1] == Cr ? i - 1 : i;
                lines.Add(backing.AsMemory(start, end - start));
                i++;
                start = i;
            }
            else if (b == Cr)
            {
                if (i + 1 < backing.Length && backing[i + 1] == Lf)
                {
                    lines.Add(backing.AsMemory(start, i - start));
                    i += 2;
                    start = i;
                }
                else
                {
                    lines.Add(backing.AsMemory(start, i - start));
                    i++;
                    start = i;
                }
            }
            else
            {
                i++;
            }
        }

        if (start < backing.Length)
        {
            lines.Add(backing.AsMemory(start));
        }

        return lines;
    }
}
