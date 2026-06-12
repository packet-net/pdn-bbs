using System.Text;

namespace Bbs.SevenPlus.Tests;

/// <summary>
/// Decode and encode against the reference's own golden vectors (fields.jpg and
/// its 17 parts). Decode must recover the JPEG byte-for-byte; encode must
/// reproduce the reference part files byte-for-byte — both directions prove wire
/// compatibility with the reference implementation.
/// </summary>
public class GoldenVectorTests
{
    // Footer timestamp 5C906C09 (read from fields.p11's footer) — the reference
    // encode-golden test pulls this from the part to reproduce the bytes.
    private const long FieldsTimestamp = 0x5C906C09;

    [Fact]
    public void Decode_AllFieldsParts_RecoversJpegByteForByte()
    {
        var source = TestResources.FieldsJpg();
        var parts = ScanAll(TestResources.FieldsParts());

        var (complete, content, report) = SevenPlusFile.TryAssemble(parts);

        Assert.True(complete);
        Assert.Equal(0, report.CorruptedLines);
        Assert.Equal(0, report.MissingLines);
        Assert.Empty(report.MissingParts);
        Assert.NotNull(content);
        Assert.Equal(source.Length, content!.Length);
        Assert.True(source.AsSpan().SequenceEqual(content), "decoded bytes differ from fields.jpg");
    }

    [Fact]
    public void Decode_RecoversExtendedFilenameAndTimestamp()
    {
        var parts = ScanAll(TestResources.FieldsParts());
        var (_, _, report) = SevenPlusFile.TryAssemble(parts);

        Assert.Equal("fields.jpg", report.FileName);
        Assert.NotNull(report.Timestamp);
        Assert.Equal(FieldsTimestamp, report.Timestamp);
    }

    [Fact]
    public void Decode_AcceptsPartsInShuffledOrder()
    {
        var source = TestResources.FieldsJpg();
        var parts = Enumerable.Reverse(ScanAll(TestResources.FieldsParts())).ToList();

        var (complete, content, _) = SevenPlusFile.TryAssemble(parts);

        Assert.True(complete);
        Assert.True(source.AsSpan().SequenceEqual(content));
    }

    [Fact]
    public void Encode_ReproducesAll17ReferenceParts_ByteForByte()
    {
        var source = TestResources.FieldsJpg();
        var referenceParts = TestResources.FieldsParts();

        var encoded = SevenPlusEncoder.Encode(source, new SevenPlusEncodeOptions
        {
            FileName = "fields.jpg",
            Timestamp = FieldsTimestamp,
            // Defaults: 138-line blocks, extended name, CRLF — match the reference.
        });

        Assert.Equal(17, encoded.Count);
        for (var i = 0; i < 17; i++)
        {
            var expectedName = $"fields.p{i + 1:x2}";
            Assert.Equal(expectedName, encoded[i].Name);

            var got = Encoding.Latin1.GetBytes(encoded[i].Text);
            var expected = referenceParts[i];
            Assert.True(
                got.AsSpan().SequenceEqual(expected),
                $"part {i + 1} differs (len got {got.Length}, expected {expected.Length}; {FirstDiff(got, expected)})");
        }
    }

    [Fact]
    public void Encode_ThenScan_RoundTripsThroughThePublicApi()
    {
        var source = TestResources.FieldsJpg();
        var encoded = SevenPlusEncoder.Encode(source, "fields.jpg", maxPartBytes: 138 * 62);

        // Concatenate all part strings as one mixed text blob (as a host would
        // see them across messages) and run the scanner + assembler over it.
        var blob = Encoding.Latin1.GetBytes(string.Concat(encoded));
        var parts = SevenPlusScanner.ExtractParts(blob);

        var (complete, content, report) = SevenPlusFile.TryAssemble(parts);
        Assert.True(complete);
        Assert.Equal(source.Length, report.FileSize);
        Assert.True(source.AsSpan().SequenceEqual(content));
    }

    private static List<SevenPlusPart> ScanAll(IReadOnlyList<byte[]> partFiles)
    {
        var all = new List<SevenPlusPart>();
        foreach (var file in partFiles)
        {
            all.AddRange(SevenPlusScanner.ExtractParts(file));
        }

        return all;
    }

    private static string FirstDiff(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return $"first diff at byte {i}: got 0x{a[i]:X2}, expected 0x{b[i]:X2}";
            }
        }

        return "differ only in length";
    }
}
