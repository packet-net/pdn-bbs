namespace Bbs.SevenPlus;

/// <summary>
/// The three CRCs 7plus relies on, all derived from one table-driven 16-bit
/// step (<see cref="Step"/>, init 0):
/// <list type="bullet">
/// <item>a 14-bit per-code-line CRC over the 64 payload chars, packed with the
/// line number into positions 64..66;</item>
/// <item>a 15-bit structural CRC (<c>crc2</c>) over the first 67 bytes of a
/// line, fed <b>in reverse</b>, written as two radix-216 chars at 67..68 on
/// every structural line;</item>
/// <item>an 8-bit mod-216 mini-CRC (<c>mcrc</c>) validating header/footer
/// lines at the <c>0xB0 0xB1 0xB2 ?</c> sentinel.</item>
/// </list>
/// Reference: utils.c <c>crc_calc</c>, <c>mcrc</c>, <c>add_crc2</c>,
/// <c>crc_n_lnum</c>.
/// </summary>
internal static class SevenPlusCrc
{
    /// <summary>The structural line length 7plus uses everywhere: exactly 69 bytes.</summary>
    public const int LineLength = 69;

    /// <summary>
    /// One CRC step: <c>crctab[crc &gt;&gt; 8] ^ ((crc &amp; 0xff) &lt;&lt; 8 | (x &amp; 0xff))</c>.
    /// </summary>
    public static int Step(int crc, byte x)
    {
        return (SevenPlusTables.CrcTable[(crc >> 8) & 0xFF] ^ (((crc & 0xFF) << 8) | x)) & 0xFFFF;
    }

    /// <summary>
    /// Writes the mini header/footer CRC: finds the <c>0xB0 0xB1</c> sentinel,
    /// computes the CRC over bytes <c>[0 .. pos+4)</c> reduced mod 216, and
    /// stores the radix-216 char at <c>pos+4</c>. Returns false (no write) if
    /// the sentinel is missing.
    /// </summary>
    public static bool WriteMiniCrc(Span<byte> line)
    {
        if (!TryComputeMiniCrc(line, out var crc, out var slot))
        {
            return false;
        }

        line[slot] = SevenPlusTables.Code[crc];
        return true;
    }

    /// <summary>Verifies the mini header/footer CRC at the <c>0xB0 0xB1 ?</c> sentinel.</summary>
    public static bool VerifyMiniCrc(ReadOnlySpan<byte> line)
    {
        if (!TryComputeMiniCrc(line, out var crc, out var slot))
        {
            return false;
        }

        return SevenPlusTables.Decode[line[slot]] == crc;
    }

    private static bool TryComputeMiniCrc(ReadOnlySpan<byte> line, out int crc, out int slot)
    {
        crc = 0;
        slot = -1;
        var pos = -1;
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == 0xB0 && line[i + 1] == 0xB1)
            {
                pos = i;
                break;
            }
        }

        if (pos < 0)
        {
            return false;
        }

        var j = pos + 4;
        if (j >= line.Length)
        {
            return false;
        }

        var c = 0;
        for (var i = 0; i < j; i++)
        {
            c = Step(c, line[i]);
        }

        crc = c % SevenPlusTables.Radix;
        slot = j;
        return true;
    }

    /// <summary>
    /// Writes the 15-bit structural <c>crc2</c> into positions 67..68. The CRC is
    /// fed the first 67 bytes (indices 66..0) <b>in reverse</b>, masked to 15
    /// bits, then split base-216: low digit at 67, high digit at 68.
    /// </summary>
    public static void AddCrc2(Span<byte> line)
    {
        if (line.Length != LineLength)
        {
            throw new ArgumentException($"crc2 expects a {LineLength}-byte line, got {line.Length}", nameof(line));
        }

        var crc = 0;
        for (var i = 66; i >= 0; i--)
        {
            crc = Step(crc, line[i]);
        }

        crc &= 0x7FFF;
        line[67] = SevenPlusTables.Code[crc % SevenPlusTables.Radix];
        line[68] = SevenPlusTables.Code[crc / SevenPlusTables.Radix];
    }

    /// <summary>True when positions 67..68 match the reverse-fed 15-bit CRC.</summary>
    public static bool VerifyCrc2(ReadOnlySpan<byte> line)
    {
        if (line.Length != LineLength)
        {
            return false;
        }

        var crc = 0;
        for (var i = 66; i >= 0; i--)
        {
            crc = Step(crc, line[i]);
        }

        crc &= 0x7FFF;
        return line[67] == SevenPlusTables.Code[crc % SevenPlusTables.Radix]
            && line[68] == SevenPlusTables.Code[crc / SevenPlusTables.Radix];
    }

    /// <summary>
    /// Unpacks the line number (9 bits) and 14-bit CRC packed into the three
    /// radix-216 chars at positions 64..66 as <c>(linenum &lt;&lt; 14) | crc14</c>.
    /// Returns false if any of the three bytes is outside the alphabet.
    /// </summary>
    public static bool TryReadLineNumberAndCrc(ReadOnlySpan<byte> line, out int lineNumber, out int crc14)
    {
        lineNumber = 0;
        crc14 = 0;
        var d64 = SevenPlusTables.Decode[line[64]];
        var d65 = SevenPlusTables.Decode[line[65]];
        var d66 = SevenPlusTables.Decode[line[66]];
        if (d64 == SevenPlusTables.Invalid || d65 == SevenPlusTables.Invalid || d66 == SevenPlusTables.Invalid)
        {
            return false;
        }

        // 216^2 = 46656; the three little-end digits reconstruct the packed value.
        var packed = (46656 * d66) + (216 * d65) + d64;
        lineNumber = packed >> 14;
        crc14 = packed & 0x3FFF;
        return true;
    }

    /// <summary>The 14-bit CRC over the first 64 bytes (the payload chars) of a code line.</summary>
    public static int CodeLineCrc14(ReadOnlySpan<byte> line)
    {
        var crc = 0;
        for (var i = 0; i < 64; i++)
        {
            crc = Step(crc, line[i]);
        }

        return crc & 0x3FFF;
    }
}
