namespace Bbs.SevenPlus.Tests;

/// <summary>
/// The radix-216 alphabet, the CRC table, and the three 7plus CRCs, pinned
/// against concrete values read from the reference (utils.c / crc.ts) and from
/// the golden sample part fields.p01.
/// </summary>
public class TablesAndCrcTests
{
    [Fact]
    public void Alphabet_HasExactly216SymbolsAndRoundTrips()
    {
        var seen = new HashSet<byte>();
        for (var i = 0; i < SevenPlusTables.Radix; i++)
        {
            var b = SevenPlusTables.Code[i];
            Assert.True(seen.Add(b), $"duplicate alphabet byte 0x{b:X2}");
            Assert.Equal(i, SevenPlusTables.Decode[b]);
        }

        Assert.Equal(216, seen.Count);
    }

    [Fact]
    public void Alphabet_DecodeMapsOnlyValidCodeBytes()
    {
        var valid = 0;
        for (var b = 0; b < 256; b++)
        {
            if (SevenPlusTables.Decode[b] != SevenPlusTables.Invalid)
            {
                valid++;
                Assert.Equal(b, SevenPlusTables.Code[SevenPlusTables.Decode[b]]);
            }
        }

        Assert.Equal(216, valid);
    }

    [Fact]
    public void Alphabet_FirstSymbolsMatchReference()
    {
        // From the reference tables.ts: code[0..15] read out of utils.c init.
        int[] expected = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31];
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], SevenPlusTables.Code[i]);
        }

        // 0x2A is skipped between 0x29 and 0x2B; the high band reaches 0xFC.
        Assert.Equal(SevenPlusTables.Invalid, SevenPlusTables.Decode[0x2A]);
        Assert.Equal(SevenPlusTables.Invalid, SevenPlusTables.Decode[0x91]);
        Assert.Equal(SevenPlusTables.Invalid, SevenPlusTables.Decode[0x93]);
        Assert.Equal(0xFC, SevenPlusTables.Code[215]);
    }

    [Fact]
    public void CrcTable_HasReferenceStructuralValues()
    {
        Assert.Equal(0, SevenPlusTables.CrcTable[0]);
        Assert.Equal(0x1021, SevenPlusTables.CrcTable[1]);
    }

    [Fact]
    public void CrcStep_OverHeaderMagic_MatchesReference()
    {
        // " go_7+. " fed byte-by-byte → 0x93E8 (dumped from the reference crcStep).
        var crc = 0;
        foreach (var b in " go_7+. "u8)
        {
            crc = SevenPlusCrc.Step(crc, b);
        }

        Assert.Equal(0x93E8, crc);
    }

    [Fact]
    public void HeaderLine_MiniCrcAndCrc2_ValidateAndRoundTrip()
    {
        var header = FirstLineStartingWith(TestResources.Bytes("fields.p01"), " go_7+.");
        Assert.Equal(69, header.Length);
        Assert.True(SevenPlusCrc.VerifyMiniCrc(header));
        Assert.True(SevenPlusCrc.VerifyCrc2(header));

        // Re-deriving crc2 reproduces bytes 67..68 (pinned 188, 46 from the ref).
        var mut = (byte[])header.Clone();
        mut[67] = 0;
        mut[68] = 0;
        SevenPlusCrc.AddCrc2(mut);
        Assert.Equal(header[67], mut[67]);
        Assert.Equal(header[68], mut[68]);
        Assert.Equal(188, header[67]);
        Assert.Equal(46, header[68]);
    }

    [Fact]
    public void FirstCodeLine_LineNumberAndCrc14_MatchReference()
    {
        // p01 layout: line 0 header, line 1 extended-name, line 2 = first code line.
        var line = NthLine(TestResources.Bytes("fields.p01"), 2);
        Assert.Equal(69, line.Length);

        Assert.True(SevenPlusCrc.TryReadLineNumberAndCrc(line, out var lineNumber, out var crc14));
        Assert.Equal(0, lineNumber);
        Assert.Equal(7603, crc14); // pinned from the reference crc_n_lnum
        Assert.Equal(crc14, SevenPlusCrc.CodeLineCrc14(line));

        // crc15 round-trips through AddCrc2.
        var mut = (byte[])line.Clone();
        mut[67] = 0;
        mut[68] = 0;
        SevenPlusCrc.AddCrc2(mut);
        Assert.Equal(line[67], mut[67]);
        Assert.Equal(line[68], mut[68]);
    }

    [Fact]
    public void FooterLine_MiniCrcValidates()
    {
        var footer = FirstLineStartingWith(TestResources.Bytes("fields.p01"), " stop_7+.");
        Assert.Equal(69, footer.Length);
        Assert.True(SevenPlusCrc.VerifyMiniCrc(footer));
        Assert.True(SevenPlusCrc.VerifyCrc2(footer));
    }

    [Fact]
    public void AllCodeLines_InP01_HaveSequentialLineNumbersAndPassCrc14()
    {
        var lines = SplitCrlf(TestResources.Bytes("fields.p01"));
        // lines[2..139] are the 138 code lines for this part.
        for (var i = 0; i < 138; i++)
        {
            var line = lines[i + 2];
            Assert.True(SevenPlusCrc.TryReadLineNumberAndCrc(line, out var lineNumber, out var crc14));
            Assert.Equal(i, lineNumber);
            Assert.Equal(crc14, SevenPlusCrc.CodeLineCrc14(line));
        }
    }

    private static byte[] FirstLineStartingWith(byte[] buf, string prefix)
    {
        foreach (var line in SplitCrlf(buf))
        {
            if (line.Length >= prefix.Length && Latin1(line).StartsWith(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new InvalidOperationException($"no line starting with \"{prefix}\"");
    }

    private static byte[] NthLine(byte[] buf, int n) => SplitCrlf(buf)[n];

    private static List<byte[]> SplitCrlf(byte[] buf)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            if (buf[i] == 0x0A)
            {
                var end = i > start && buf[i - 1] == 0x0D ? i - 1 : i;
                lines.Add(buf[start..end]);
                start = i + 1;
            }
        }

        if (start < buf.Length)
        {
            lines.Add(buf[start..]);
        }

        return lines;
    }

    private static string Latin1(byte[] b)
    {
        var chars = new char[b.Length];
        for (var i = 0; i < b.Length; i++)
        {
            chars[i] = (char)b[i];
        }

        return new string(chars);
    }
}
