using System.Globalization;

namespace Bbs.SevenPlus;

/// <summary>
/// Builders and parsers for the four 69-byte line types — header, extended
/// filename, code, footer. Every structural line is exactly
/// <see cref="SevenPlusCrc.LineLength"/> bytes; the <c>mcrc</c> and
/// <c>crc2</c> fields are filled in place.
/// </summary>
internal static class SevenPlusLines
{
    private const int LineLength = SevenPlusCrc.LineLength;

    /// <summary>The 8-byte header magic: <c>" go_7+. "</c>.</summary>
    public const string HeaderMagic = " go_7+. ";

    /// <summary>The footer marker prefix: <c>" stop_7+."</c>.</summary>
    public const string FooterMagic = " stop_7+.";

    /// <summary>Parsed structural fields of a header line.</summary>
    public readonly record struct Header(
        int Part,
        int Parts,
        string HeaderName,
        int FileSize,
        int BlockSize,
        int BlockLines,
        bool Extended);

    /// <summary>
    /// Builds the 12-char uppercase DOS 8.3 display name used in the header
    /// <c>%-12s</c> field: path stripped, base truncated to 8, extension to 3,
    /// joined with '.', uppercased, padded to 12 with spaces.
    /// </summary>
    public static string DosName(string filename)
    {
        var baseName = StripPath(filename);
        var dot = baseName.LastIndexOf('.');
        var name = StripWhitespace(dot >= 0 ? baseName[..dot] : baseName);
        if (name.Length > 8)
        {
            name = name[..8];
        }

        var ext = dot >= 0 ? StripWhitespace(baseName[(dot + 1)..]) : string.Empty;
        if (ext.Length > 3)
        {
            ext = ext[..3];
        }

        var full = (ext.Length > 0 ? $"{name}.{ext}" : name).ToUpperInvariant();
        return full.PadRight(12, ' ');
    }

    /// <summary>The lowercase 8-char base name (no extension) used for part file names.</summary>
    public static string DosBaseName(string filename)
    {
        var baseName = StripPath(filename);
        var dot = baseName.LastIndexOf('.');
        var name = StripWhitespace(dot >= 0 ? baseName[..dot] : baseName);
        if (name.Length > 8)
        {
            name = name[..8];
        }

        return name.ToLowerInvariant();
    }

    /// <summary>Strips any directory component from a path (both '/' and '\').</summary>
    public static string StripPath(string filename)
    {
        var slash = Math.Max(filename.LastIndexOf('/'), filename.LastIndexOf('\\'));
        return slash >= 0 ? filename[(slash + 1)..] : filename;
    }

    private static string StripWhitespace(string s)
    {
        var hasWhitespace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                hasWhitespace = true;
                break;
            }
        }

        if (!hasWhitespace)
        {
            return s;
        }

        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                buf[n++] = c;
            }
        }

        return new string(buf[..n]);
    }

    /// <summary>
    /// Header layout (69 bytes):
    /// <c>0..7</c> magic, <c>8..10</c> part %03d, <c>11..14</c> " of ",
    /// <c>15..17</c> parts %03d, <c>18</c> space, <c>19..30</c> 12-char DOS name,
    /// <c>31</c> space, <c>32..38</c> fileSize %07d, <c>39</c> space,
    /// <c>40..43</c> blockSize %04X, <c>44</c> space, <c>45..47</c> blockLines %03X,
    /// <c>48..61</c> " (7PLUS v2.2) ", <c>62..64</c> 0xB0 0xB1 0xB2,
    /// <c>65</c> '*' if extended else ' ', <c>66</c> mcrc, <c>67..68</c> crc2.
    /// </summary>
    public static byte[] BuildHeaderLine(in Header h)
    {
        var text =
            HeaderMagic +
            h.Part.ToString("D3", CultureInfo.InvariantCulture) + " of " +
            h.Parts.ToString("D3", CultureInfo.InvariantCulture) + " " +
            h.HeaderName + " " +
            h.FileSize.ToString("D7", CultureInfo.InvariantCulture) + " " +
            (h.BlockSize & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture) + " " +
            h.BlockLines.ToString("X3", CultureInfo.InvariantCulture) + " (7PLUS v2.2) ";
        if (text.Length != 62)
        {
            throw new InvalidOperationException($"header text length {text.Length} != 62: \"{text}\"");
        }

        var line = new byte[LineLength];
        WriteLatin1(line, text);
        line[62] = 0xB0;
        line[63] = 0xB1;
        line[64] = 0xB2;
        line[65] = (byte)(h.Extended ? '*' : ' ');
        SevenPlusCrc.WriteMiniCrc(line);
        SevenPlusCrc.AddCrc2(line);
        return line;
    }

    /// <summary>
    /// Extended-filename line (part 1 only). <c>0..61</c> is '/'-filled with the
    /// original name placed at offset 1; <c>62..64</c> sentinel, <c>65</c> '*',
    /// <c>66</c> mcrc, <c>67..68</c> crc2. The name terminates at the next '/'.
    /// </summary>
    public static byte[] BuildExtendedNameLine(string originalName)
    {
        var bytes = Latin1Bytes(originalName);
        if (bytes.Length > 60)
        {
            throw new ArgumentException($"extended filename too long (max 60 bytes): \"{originalName}\"", nameof(originalName));
        }

        var line = new byte[LineLength];
        line.AsSpan(0, 62).Fill((byte)'/');
        bytes.CopyTo(line.AsSpan(1));
        line[62] = 0xB0;
        line[63] = 0xB1;
        line[64] = 0xB2;
        line[65] = (byte)'*';
        SevenPlusCrc.WriteMiniCrc(line);
        SevenPlusCrc.AddCrc2(line);
        return line;
    }

    /// <summary>
    /// Footer (69 bytes): 62 spaces overlaid with
    /// <c>" stop_7+. (NAME.Pxx/yy) [HEXTS]"</c> (multi-part) or
    /// <c>" stop_7+. (NAME.7PL) [HEXTS]"</c> (single part); then 0xB0 0xB1 0xB2
    /// at 62..64, the 0xDB footer marker at 65, mcrc at 66, crc2 at 67..68.
    /// </summary>
    public static byte[] BuildFooterLine(string headerName, int part, int parts, long timestamp)
    {
        var line = new byte[LineLength];
        line.AsSpan(0, 62).Fill((byte)' ');
        line[62] = 0xB0;
        line[63] = 0xB1;
        line[64] = 0xB2;
        line[65] = 0xDB;

        var trimmed = headerName.Trim();
        var dot = trimmed.IndexOf('.');
        var baseName = dot >= 0 ? trimmed[..dot] : trimmed;
        var suffix = parts > 1
            ? $"{baseName}.P{part.ToString("X2", CultureInfo.InvariantCulture)}/{parts.ToString("X2", CultureInfo.InvariantCulture)}"
            : $"{baseName}.7PL";
        var prefix = $" stop_7+. ({suffix}) [{timestamp.ToString("X", CultureInfo.InvariantCulture)}]";
        if (prefix.Length > 62)
        {
            throw new InvalidOperationException($"footer prefix too long ({prefix.Length}): \"{prefix}\"");
        }

        WriteLatin1(line, prefix);
        SevenPlusCrc.WriteMiniCrc(line);
        SevenPlusCrc.AddCrc2(line);
        return line;
    }

    /// <summary>
    /// Encodes 62 payload bytes plus a line number into one 69-byte code line.
    /// The 62 bytes split into two 31-byte groups; each group packs into 8
    /// 32-bit accumulators (4 bytes each, last 3) which are then bit-squeezed
    /// into 8 31-bit values; the 16 values emit as 4 radix-216 chars each
    /// (little-end). Positions 64..66 carry <c>(linenum &lt;&lt; 14) | crc14</c>;
    /// 67..68 carry crc2.
    /// </summary>
    public static byte[] BuildCodeLine(ReadOnlySpan<byte> payload, int lineNumber)
    {
        if (payload.Length != 62)
        {
            throw new ArgumentException($"code line expects 62 payload bytes, got {payload.Length}", nameof(payload));
        }

        var line = new byte[LineLength];
        Span<ulong> words = stackalloc ulong[16];

        var p = 0;
        for (var g = 0; g < 2; g++)
        {
            var off = g * 8;
            for (var j = 0; j < 8; j++)
            {
                ulong v = 0;
                var nBytes = j == 7 ? 3 : 4;
                for (var k = 0; k < nBytes; k++)
                {
                    var b = p < payload.Length ? payload[p++] : (byte)0;
                    v = (v << 8) | b;
                }

                words[off + j] = v;
            }

            // Bit-squeeze (encode.c 580..587): fold the top bits of each word
            // down into the next so each of the 8 carries 31 significant bits.
            words[off + 7] = words[off + 7] | ((words[off + 6] & 0x7F) << 24);
            words[off + 6] = (words[off + 6] >> 7) | ((words[off + 5] & 0x3F) << 25);
            words[off + 5] = (words[off + 5] >> 6) | ((words[off + 4] & 0x1F) << 26);
            words[off + 4] = (words[off + 4] >> 5) | ((words[off + 3] & 0x0F) << 27);
            words[off + 3] = (words[off + 3] >> 4) | ((words[off + 2] & 0x07) << 28);
            words[off + 2] = (words[off + 2] >> 3) | ((words[off + 1] & 0x03) << 29);
            words[off + 1] = (words[off + 1] >> 2) | ((words[off + 0] & 0x01) << 30);
            words[off + 0] = words[off + 0] >> 1;
        }

        const ulong d216 = SevenPlusTables.Radix;
        var idx = 0;
        for (var i = 0; i < 16; i++)
        {
            var v = words[i];
            line[idx++] = SevenPlusTables.Code[(int)(v % d216)];
            v /= d216;
            line[idx++] = SevenPlusTables.Code[(int)(v % d216)];
            v /= d216;
            line[idx++] = SevenPlusTables.Code[(int)(v % d216)];
            v /= d216;
            line[idx++] = SevenPlusTables.Code[(int)v];
        }

        var crc14 = SevenPlusCrc.CodeLineCrc14(line);
        var packed = (long)((lineNumber & 0x1FF) << 14) | (uint)crc14;
        line[64] = SevenPlusTables.Code[(int)(packed % SevenPlusTables.Radix)];
        packed /= SevenPlusTables.Radix;
        line[65] = SevenPlusTables.Code[(int)(packed % SevenPlusTables.Radix)];
        packed /= SevenPlusTables.Radix;
        line[66] = SevenPlusTables.Code[(int)packed];

        SevenPlusCrc.AddCrc2(line);
        return line;
    }

    /// <summary>
    /// Decodes a 69-byte code line back to its 62 payload bytes. Returns false
    /// if any payload char is outside the alphabet or the 14-bit CRC fails.
    /// </summary>
    public static bool TryDecodeCodeLine(ReadOnlySpan<byte> line, Span<byte> payload, out int lineNumber)
    {
        lineNumber = 0;
        if (line.Length != LineLength || payload.Length != 62)
        {
            return false;
        }

        Span<ulong> words = stackalloc ulong[16];

        // Rebuild 16 words from chars 0..63, big-end within each group of 4
        // (decode.c 618..627): after[k] = sum over the 4 chars of digit*216^pos.
        for (int i = 0, k = 0; i < 64; i++)
        {
            if ((i & 3) == 3)
            {
                ulong v = 0;
                for (var j = i; j > i - 4; j--)
                {
                    var d = SevenPlusTables.Decode[line[j]];
                    if (d == SevenPlusTables.Invalid)
                    {
                        return false;
                    }

                    v = (v * SevenPlusTables.Radix) + d;
                }

                words[k++] = v;
            }
        }

        // Reverse the bit-squeeze (decode.c 634..641). The reference works in
        // 32-bit ulongs; mask each shifted result to 32 bits.
        const ulong m32 = 0xFFFFFFFF;
        for (var g = 0; g < 2; g++)
        {
            var off = g * 8;
            words[off + 0] = ((words[off + 0] << 1) | (words[off + 1] >> 30)) & m32;
            words[off + 1] = ((words[off + 1] << 2) | (words[off + 2] >> 29)) & m32;
            words[off + 2] = ((words[off + 2] << 3) | (words[off + 3] >> 28)) & m32;
            words[off + 3] = ((words[off + 3] << 4) | (words[off + 4] >> 27)) & m32;
            words[off + 4] = ((words[off + 4] << 5) | (words[off + 5] >> 26)) & m32;
            words[off + 5] = ((words[off + 5] << 6) | (words[off + 6] >> 25)) & m32;
            words[off + 6] = ((words[off + 6] << 7) | (words[off + 7] >> 24)) & m32;
            words[off + 7] = (words[off + 7] << 8) & m32;

            var outIdx = g * 31;
            for (var j = 0; j < 8; j++)
            {
                var v = words[off + j];
                payload[outIdx++] = (byte)((v >> 24) & 0xFF);
                payload[outIdx++] = (byte)((v >> 16) & 0xFF);
                payload[outIdx++] = (byte)((v >> 8) & 0xFF);
                if (j == 7)
                {
                    break; // last word carries only 3 bytes (low 8 bits are zero)
                }

                payload[outIdx++] = (byte)(v & 0xFF);
            }
        }

        if (!SevenPlusCrc.TryReadLineNumberAndCrc(line, out lineNumber, out var crc14))
        {
            return false;
        }

        return crc14 == SevenPlusCrc.CodeLineCrc14(line);
    }

    /// <summary>
    /// Parses a 69-byte header line, returning false unless it is a valid
    /// <c>" go_7+. "</c> header with passing mcrc and crc2.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> line, out Header header)
    {
        header = default;
        if (line.Length != LineLength)
        {
            return false;
        }

        var text = Latin1String(line);
        if (!text.StartsWith(" go_7+.", StringComparison.Ordinal))
        {
            return false;
        }

        if (text.AsSpan(11, 4).ToString() != " of " || text[18] != ' ' || text[31] != ' ' || text[39] != ' ' || text[44] != ' ')
        {
            return false;
        }

        if (!IsVersionTag(text.AsSpan(48, 14)))
        {
            return false;
        }

        if (line[62] != 0xB0 || line[63] != 0xB1 || line[64] != 0xB2)
        {
            return false;
        }

        if (!TryParseInt(text.AsSpan(8, 3), out var part) ||
            !TryParseInt(text.AsSpan(15, 3), out var parts) ||
            !TryParseInt(text.AsSpan(32, 7), out var fileSize) ||
            !TryParseHex(text.AsSpan(40, 4), out var blockSize) ||
            !TryParseHex(text.AsSpan(45, 3), out var blockLines))
        {
            return false;
        }

        if (!SevenPlusCrc.VerifyMiniCrc(line) || !SevenPlusCrc.VerifyCrc2(line))
        {
            return false;
        }

        header = new Header(
            part, parts,
            text.Substring(19, 12),
            fileSize, blockSize, blockLines,
            line[65] == '*');
        return true;
    }

    /// <summary>
    /// Parses the extended-filename line (part 1), returning the recovered name
    /// or null if the line is not a valid extended-name line.
    /// </summary>
    public static string? ParseExtendedName(ReadOnlySpan<byte> line)
    {
        if (line.Length != LineLength || line[0] != '/')
        {
            return null;
        }

        if (line[62] != 0xB0 || line[63] != 0xB1 || line[64] != 0xB2 || line[65] != '*')
        {
            return null;
        }

        if (!SevenPlusCrc.VerifyMiniCrc(line) || !SevenPlusCrc.VerifyCrc2(line))
        {
            return null;
        }

        var end = 1;
        while (end < 62 && line[end] != '/')
        {
            end++;
        }

        return Latin1String(line[1..end]);
    }

    /// <summary>
    /// Parses a footer line, returning the footer timestamp (or null if absent)
    /// when the line is a valid <c>" stop_7+."</c> footer; returns false
    /// otherwise.
    /// </summary>
    public static bool TryParseFooter(ReadOnlySpan<byte> line, out long? timestamp)
    {
        timestamp = null;
        if (line.Length != LineLength)
        {
            return false;
        }

        var text = Latin1String(line);
        if (!text.StartsWith(" stop_7+.", StringComparison.Ordinal))
        {
            return false;
        }

        if (line[62] != 0xB0 || line[63] != 0xB1 || line[64] != 0xB2 || line[65] != 0xDB)
        {
            return false;
        }

        if (!SevenPlusCrc.VerifyMiniCrc(line) || !SevenPlusCrc.VerifyCrc2(line))
        {
            return false;
        }

        var open = text.IndexOf('[', StringComparison.Ordinal);
        var close = open >= 0 ? text.IndexOf(']', open) : -1;
        if (open >= 0 && close > open)
        {
            var hex = text.AsSpan(open + 1, close - open - 1);
            if (long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ts))
            {
                timestamp = ts;
            }
        }

        return true;
    }

    private static bool IsVersionTag(ReadOnlySpan<char> s)
    {
        // Matches " (7PLUS v2.<digit>) " — the reference tolerates any minor.
        if (s.Length != 14)
        {
            return false;
        }

        return s.StartsWith(" (7PLUS v2.") && char.IsAsciiDigit(s[11]) && s[12] == ')' && s[13] == ' ';
    }

    private static bool TryParseInt(ReadOnlySpan<char> s, out int value)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryParseHex(ReadOnlySpan<char> s, out int value)
        => int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static void WriteLatin1(Span<byte> dest, string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c > 0xFF)
            {
                throw new ArgumentException($"non-latin1 char U+{(int)c:X4} in \"{s}\"", nameof(s));
            }

            dest[i] = (byte)c;
        }
    }

    private static byte[] Latin1Bytes(string s)
    {
        var bytes = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c > 0xFF)
            {
                throw new ArgumentException($"non-latin1 char U+{(int)c:X4} in \"{s}\"", nameof(s));
            }

            bytes[i] = (byte)c;
        }

        return bytes;
    }

    private static string Latin1String(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length, bytes.ToArray(), static (chars, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                chars[i] = (char)src[i];
            }
        });
    }
}
