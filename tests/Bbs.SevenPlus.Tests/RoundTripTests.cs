using System.Text;

namespace Bbs.SevenPlus.Tests;

/// <summary>
/// Encode→decode round-trip across a spread of payloads and the structural
/// invariants of the encoder output (every line exactly 69 bytes, re-parseable).
/// </summary>
public class RoundTripTests
{
    public static TheoryData<int> Sizes =>
    [
        1, 2, 31, 61, 62, 63, 124, 125,
        8555, 8556, 8557,        // around the default single-line-block boundary
        138 * 62,                // exactly one default part
        (138 * 62) + 1,          // one byte into a second part
        2 * 138 * 62,            // exactly two parts
        159293, 159294, 159295,  // the reference's multi-part edge sizes
    ];

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RoundTrip_DefaultGeometry_RecoversBytesExactly(int size)
    {
        var src = Pseudo(size);
        var encoded = SevenPlusEncoder.Encode(src, new SevenPlusEncodeOptions
        {
            FileName = "rand.bin",
            Timestamp = 1_700_000_000,
        });

        AssertAllLines69Bytes(encoded);

        var parts = ScanAll(encoded);
        var (complete, content, report) = SevenPlusFile.TryAssemble(parts);

        Assert.True(complete, $"size {size} not complete: missing parts [{string.Join(",", report.MissingParts)}], corrupt {report.CorruptedLines}");
        Assert.Equal(size, content!.Length);
        Assert.True(src.AsSpan().SequenceEqual(content), $"size {size} differs after round-trip");
    }

    [Fact]
    public void RoundTrip_AllByteValues_AndSentinelBytes_Survive()
    {
        // Every byte value 0..255, then the sentinel run 0xB0 0xB1 0xB2 repeated.
        var src = new byte[256 + 12];
        for (var i = 0; i < 256; i++)
        {
            src[i] = (byte)i;
        }

        for (var i = 0; i < 12; i += 3)
        {
            src[256 + i] = 0xB0;
            src[256 + i + 1] = 0xB1;
            src[256 + i + 2] = 0xB2;
        }

        var encoded = SevenPlusEncoder.Encode(src, "bytes.bin");
        var parts = SevenPlusScanner.ExtractParts(Encoding.Latin1.GetBytes(string.Concat(encoded)));
        var (complete, content, _) = SevenPlusFile.TryAssemble(parts);

        Assert.True(complete);
        Assert.True(src.AsSpan().SequenceEqual(content));
    }

    [Fact]
    public void RoundTrip_SingleByte_ProducesOneSinglePart()
    {
        var encoded = SevenPlusEncoder.Encode([0x42], new SevenPlusEncodeOptions
        {
            FileName = "x.dat",
            Timestamp = 1,
        });

        Assert.Single(encoded);
        Assert.Equal("x.7pl", encoded[0].Name); // single part uses the .7pl name

        var parts = SevenPlusScanner.ExtractParts(encoded[0].Bytes);
        var (complete, content, _) = SevenPlusFile.TryAssemble(parts);
        Assert.True(complete);
        Assert.Equal(new byte[] { 0x42 }, content);
    }

    [Fact]
    public void Encode_ZeroLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => SevenPlusEncoder.Encode(Array.Empty<byte>(), "empty.bin"));
    }

    [Theory]
    [InlineData(SevenPlusLineSeparator.CrLf)]
    [InlineData(SevenPlusLineSeparator.Lf)]
    [InlineData(SevenPlusLineSeparator.Cr)]
    public void RoundTrip_TolerantOfAllLineSeparators(SevenPlusLineSeparator sep)
    {
        var src = Pseudo(500);
        var encoded = SevenPlusEncoder.Encode(src, new SevenPlusEncodeOptions
        {
            FileName = "ls.bin",
            Timestamp = 1,
            LineSeparator = sep,
        });

        var parts = SevenPlusScanner.ExtractParts(encoded[0].Bytes);
        var (complete, content, _) = SevenPlusFile.TryAssemble(parts);
        Assert.True(complete);
        Assert.True(src.AsSpan().SequenceEqual(content));
    }

    [Fact]
    public void PartCount_SplitsIntoRequestedNumberOfParts_AndRoundTrips()
    {
        var src = Pseudo(25_000);
        var encoded = SevenPlusEncoder.Encode(src, new SevenPlusEncodeOptions
        {
            FileName = "rand.bin",
            PartCount = 5,
            Timestamp = 1,
        });

        Assert.Equal(5, encoded.Count);

        var parts = ScanAll(encoded);
        var (complete, content, _) = SevenPlusFile.TryAssemble(parts);
        Assert.True(complete);
        Assert.True(src.AsSpan().SequenceEqual(content));
    }

    [Fact]
    public void Terminator_AppendsAfterFooter_AndIsIgnoredOnDecode()
    {
        var src = Pseudo(500);
        var encoded = SevenPlusEncoder.Encode(src, new SevenPlusEncodeOptions
        {
            FileName = "rand.bin",
            Timestamp = 1,
            Terminator = "/ex",
        });

        var text = encoded[0].Text;
        var stopIdx = text.IndexOf(" stop_7+.", StringComparison.Ordinal);
        var exIdx = text.IndexOf("/ex", StringComparison.Ordinal);
        Assert.True(stopIdx >= 0);
        Assert.True(exIdx > stopIdx, "terminator must follow the footer");

        var parts = SevenPlusScanner.ExtractParts(encoded[0].Bytes);
        var (complete, content, _) = SevenPlusFile.TryAssemble(parts);
        Assert.True(complete);
        Assert.True(src.AsSpan().SequenceEqual(content));
    }

    private static void AssertAllLines69Bytes(IReadOnlyList<SevenPlusEncoder.EncodedPart> parts)
    {
        foreach (var part in parts)
        {
            // Each part is structural lines separated by the line separator; the
            // last line may carry a terminator (none here). Strip the trailing
            // separator and check every line is exactly 69 bytes.
            var bytes = part.Bytes;
            foreach (var line in SplitAnySep(bytes))
            {
                Assert.Equal(69, line.Length);
            }
        }
    }

    private static IEnumerable<byte[]> SplitAnySep(byte[] buf)
    {
        var start = 0;
        var i = 0;
        while (i < buf.Length)
        {
            var b = buf[i];
            if (b == 0x0A)
            {
                var end = i > start && buf[i - 1] == 0x0D ? i - 1 : i;
                yield return buf[start..end];
                i++;
                start = i;
            }
            else if (b == 0x0D)
            {
                if (i + 1 < buf.Length && buf[i + 1] == 0x0A)
                {
                    yield return buf[start..i];
                    i += 2;
                }
                else
                {
                    yield return buf[start..i];
                    i++;
                }

                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < buf.Length)
        {
            yield return buf[start..];
        }
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

    private static byte[] Pseudo(int n)
    {
        // Deterministic LCG fill (matches the kind of spread the reference tests use).
        var a = new byte[n];
        uint state = 0x12345678;
        for (var i = 0; i < n; i++)
        {
            state = (state * 1103515245) + 12345;
            a[i] = (byte)(state >> 16);
        }

        return a;
    }
}
