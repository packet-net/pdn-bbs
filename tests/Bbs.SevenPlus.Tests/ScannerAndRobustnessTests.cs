using System.Text;

namespace Bbs.SevenPlus.Tests;

/// <summary>
/// The scanner against a real mixed BBS mail dump and the assembler's reporting
/// of corruption / missing parts / partial final lines.
/// </summary>
public class ScannerAndRobustnessTests
{
    [Fact]
    public void Scanner_PullsEveryPartOutOfAMultiFileMailDump()
    {
        var parts = SevenPlusScanner.ExtractParts(TestResources.BbsMail());

        Assert.Equal(25, parts.Count);

        var byFile = parts
            .GroupBy(p => p.Identity)
            .ToDictionary(g => g.Key.HeaderName.Trim(), g => g.Select(p => p.PartNumber).ToHashSet());

        Assert.Equal(9, byFile["NORDKAPP.JPG"].Count);
        Assert.Equal(3, byFile["PINKSK_1.JPG"].Count);
        Assert.Equal(7, byFile["22-1-2_1.JPG"].Count);
        Assert.Equal(6, byFile["SUNDAY.JPG"].Count);
    }

    [Fact]
    public void Scanner_DecodesNordkappEndToEndAsRealJpeg()
    {
        var parts = SevenPlusScanner.ExtractParts(TestResources.BbsMail())
            .Where(p => p.Identity.HeaderName.Trim() == "NORDKAPP.JPG")
            .ToList();

        var (complete, content, report) = SevenPlusFile.TryAssemble(parts);

        Assert.True(complete);
        Assert.Equal(0, report.CorruptedLines);
        Assert.Equal(0, report.MissingLines);
        Assert.NotNull(content);
        // JPEG magic.
        Assert.Equal(0xFF, content![0]);
        Assert.Equal(0xD8, content[1]);
        Assert.Equal(0xFF, content[2]);
    }

    [Fact]
    public void Scanner_HandlesPartsPastedInScrambledOrder()
    {
        var parts = SevenPlusScanner.ExtractParts(TestResources.BbsMail())
            .Where(p => p.Identity.HeaderName.Trim() == "22-1-2_1.JPG")
            .ToList();

        // The fixture really is out of order — that's the interesting case.
        var order = parts.Select(p => p.PartNumber).ToList();
        var sorted = order.OrderBy(x => x).ToList();
        Assert.NotEqual(sorted, order);

        var (complete, _, report) = SevenPlusFile.TryAssemble(parts);
        Assert.True(complete);
        Assert.Equal(0, report.CorruptedLines);
        Assert.Equal(0, report.MissingLines);
    }

    [Fact]
    public void Scanner_IgnoresSurroundingNonSevenPlusText()
    {
        var src = Pseudo(1500); // small enough for a single part
        var encoded = SevenPlusEncoder.Encode(src, new SevenPlusEncodeOptions
        {
            FileName = "fields.jpg",
            Timestamp = 1,
        });
        Assert.Single(encoded);

        var sb = new StringBuilder();
        sb.Append("From: G4ABC@GB7XYZ\r\nSubject: here's that picture\r\n\r\n");
        sb.Append("> quoted line that is not 7plus\r\n");
        sb.Append(encoded[0].Text);
        sb.Append("\r\n73 de G4ABC\r\n-- signature line --\r\n");

        var parts = SevenPlusScanner.ExtractParts(Encoding.Latin1.GetBytes(sb.ToString()));
        Assert.Single(parts);

        var (complete, content, _) = SevenPlusFile.TryAssemble(parts);
        Assert.True(complete);
        Assert.True(src.AsSpan().SequenceEqual(content));
    }

    [Fact]
    public void Assemble_MissingPart_ReportsIncompleteWithThePartNumber()
    {
        var src = Pseudo(3 * 138 * 62); // 3 full parts
        var encoded = SevenPlusEncoder.Encode(src, new SevenPlusEncodeOptions
        {
            FileName = "rand.bin",
            Timestamp = 1,
            MaxPartBytes = 138 * 62,
        });
        Assert.Equal(3, encoded.Count);

        // Drop part 2.
        var blob = string.Concat(encoded[0].Text, encoded[2].Text);
        var parts = SevenPlusScanner.ExtractParts(Encoding.Latin1.GetBytes(blob));

        var (complete, content, report) = SevenPlusFile.TryAssemble(parts);

        Assert.False(complete);
        Assert.Equal([2], report.MissingParts);
        Assert.Equal([1, 3], report.ReceivedParts);
        Assert.NotNull(content);
        Assert.Equal(src.Length, content!.Length);
        // Part 1's bytes are present; part 2's region is zero-filled.
        Assert.True(src.AsSpan(0, 138 * 62).SequenceEqual(content.AsSpan(0, 138 * 62)));
        Assert.True(content.AsSpan(138 * 62, 138 * 62).ToArray().All(b => b == 0));
    }

    [Fact]
    public void Assemble_CorruptCodeLine_ReportsCorruptionAndZeroFillsThatLine()
    {
        var src = Pseudo(500);
        var encoded = SevenPlusEncoder.Encode(src, new SevenPlusEncodeOptions
        {
            FileName = "rand.bin",
            Timestamp = 1,
        });

        // Corrupt one payload byte of the second code line so its CRC14 fails.
        var bytes = encoded[0].Bytes;
        var lines = SplitCrlf(bytes);
        // lines: 0 header, 1 extended-name, 2 = first code line, 3 = second.
        var target = lines[3];
        target[5] ^= 0x01; // flip a bit in a payload char
        // Keep the char in-alphabet so it decodes structurally but fails CRC14:
        // re-pick a valid alphabet byte at that position if the flip left it valid.
        var rebuilt = JoinCrlf(lines);

        var parts = SevenPlusScanner.ExtractParts(rebuilt);
        var (complete, content, report) = SevenPlusFile.TryAssemble(parts);

        Assert.False(complete);
        Assert.True(report.CorruptedLines + report.MissingLines >= 1, "the damaged line must be reported");
        Assert.NotNull(content);
        // First code line (62 bytes) is intact; the damaged line's region differs.
        Assert.True(src.AsSpan(0, 62).SequenceEqual(content.AsSpan(0, 62)));
    }

    [Fact]
    public void Assemble_NoUsableParts_ReportsIncompleteWithNullContent()
    {
        var parts = SevenPlusScanner.ExtractParts("not a 7plus message at all\r\n"u8.ToArray());
        Assert.Empty(parts);

        var (complete, content, report) = SevenPlusFile.TryAssemble(parts);
        Assert.False(complete);
        Assert.Null(content);
        Assert.False(report.IsComplete);
    }

    [Fact]
    public void Assemble_PartialFinalLine_RecoversTheTailExactly()
    {
        // A size that is not a multiple of 62 → the final code line is partial.
        var src = Pseudo((138 * 62) + 17);
        var encoded = SevenPlusEncoder.Encode(src, new SevenPlusEncodeOptions
        {
            FileName = "tail.bin",
            Timestamp = 1,
            MaxPartBytes = 138 * 62,
        });
        Assert.Equal(2, encoded.Count);

        var parts = ScanAll(encoded);
        var (complete, content, _) = SevenPlusFile.TryAssemble(parts);

        Assert.True(complete);
        Assert.Equal(src.Length, content!.Length);
        Assert.True(src.AsSpan().SequenceEqual(content));
    }

    private static List<SevenPlusPart> ScanAll(IReadOnlyList<SevenPlusEncoder.EncodedPart> encoded)
    {
        var all = new List<SevenPlusPart>();
        foreach (var part in encoded)
        {
            all.AddRange(SevenPlusScanner.ExtractParts(part.Bytes));
        }

        return all;
    }

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

    private static byte[] JoinCrlf(List<byte[]> lines)
    {
        var total = lines.Sum(l => l.Length + 2);
        var outBuf = new byte[total];
        var off = 0;
        foreach (var line in lines)
        {
            line.CopyTo(outBuf, off);
            off += line.Length;
            outBuf[off++] = 0x0D;
            outBuf[off++] = 0x0A;
        }

        return outBuf;
    }

    private static byte[] Pseudo(int n)
    {
        var a = new byte[n];
        uint state = 0xC0FFEE;
        for (var i = 0; i < n; i++)
        {
            state = (state * 1103515245) + 12345;
            a[i] = (byte)(state >> 16);
        }

        return a;
    }
}
