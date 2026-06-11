namespace Bbs.Fbb.Tests;

public class LzhufTests
{
    // --- The spec's pinned golden vector (compat spec design decision 2) ---

    [Fact]
    public void HelloWorld_EncodesToPinnedE1Bytes()
    {
        var encoded = LzhufContainer.Encode(LzhufContainerKind.B1, Vectors.Ascii(Vectors.HelloText));
        Assert.Equal(Vectors.Hex(Vectors.HelloE1), encoded);
    }

    [Fact]
    public void HelloWorld_PinnedE1Bytes_DecodeToPlaintext()
    {
        var decoded = LzhufContainer.Decode(LzhufContainerKind.B1, Vectors.Hex(Vectors.HelloE1));
        Assert.Equal(Vectors.Ascii(Vectors.HelloText), decoded);
    }

    // --- Cross-check vectors generated with the reference binary (/tmp/lztest/lzhuf_1) ---

    [Fact]
    public void BpqMessage_MatchesReferenceBinary_E1()
    {
        var plaintext = Vectors.Ascii(Vectors.BpqMsgText);
        Assert.Equal(Vectors.Hex(Vectors.BpqMsgE1), LzhufContainer.Encode(LzhufContainerKind.B1, plaintext));
        Assert.Equal(plaintext, LzhufContainer.Decode(LzhufContainerKind.B1, Vectors.Hex(Vectors.BpqMsgE1)));
    }

    [Fact]
    public void BpqMessage_MatchesReferenceBinary_BContainer()
    {
        var plaintext = Vectors.Ascii(Vectors.BpqMsgText);
        Assert.Equal(Vectors.Hex(Vectors.BpqMsgB), LzhufContainer.Encode(LzhufContainerKind.B, plaintext));
        Assert.Equal(plaintext, LzhufContainer.Decode(LzhufContainerKind.B, Vectors.Hex(Vectors.BpqMsgB)));
    }

    [Fact]
    public void WindowWrapText_MatchesReferenceBinary()
    {
        // 6840 bytes — wraps the N=2048 ring buffer three times; a stock
        // (N=4096) port diverges here (spec §9 item 5).
        var plaintext = Vectors.WrapText();
        Assert.Equal(Vectors.Hex(Vectors.WrapE1), LzhufContainer.Encode(LzhufContainerKind.B1, plaintext));
        Assert.Equal(plaintext, LzhufContainer.Decode(LzhufContainerKind.B1, Vectors.Hex(Vectors.WrapE1)));
    }

    [Fact]
    public void AllSpaces_MatchesReferenceBinary()
    {
        // 256 × 0x20 matches the space-prefilled window from the first byte.
        var plaintext = new byte[256];
        Array.Fill(plaintext, (byte)0x20);
        Assert.Equal(Vectors.Hex(Vectors.SpacesE1), LzhufContainer.Encode(LzhufContainerKind.B1, plaintext));
        Assert.Equal(plaintext, LzhufContainer.Decode(LzhufContainerKind.B1, Vectors.Hex(Vectors.SpacesE1)));
    }

    [Fact]
    public void IncompressibleInput_MatchesReferenceBinary_AndExpands()
    {
        var plaintext = Vectors.LcgBytes(512);
        var encoded = LzhufContainer.Encode(LzhufContainerKind.B1, plaintext);
        Assert.Equal(Vectors.Hex(Vectors.LcgE1), encoded);
        Assert.True(encoded.Length > plaintext.Length, "incompressible input must expand");
        Assert.Equal(plaintext, LzhufContainer.Decode(LzhufContainerKind.B1, encoded));
    }

    [Fact]
    public void EmptyInput_MatchesReferenceBinary()
    {
        Assert.Equal(Vectors.Hex(Vectors.EmptyE1), LzhufContainer.Encode(LzhufContainerKind.B1, []));
        Assert.Empty(LzhufContainer.Decode(LzhufContainerKind.B1, Vectors.Hex(Vectors.EmptyE1)));
    }

    // --- Round-trip properties ---

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(59)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(2047)]
    [InlineData(2048)]
    [InlineData(2049)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void RandomBinary_RoundTrips_BothContainers(int length)
    {
        var plaintext = Vectors.LcgBytes(length, seed: (uint)(7 + length));
        foreach (var kind in new[] { LzhufContainerKind.B, LzhufContainerKind.B1 })
        {
            var decoded = LzhufContainer.Decode(kind, LzhufContainer.Encode(kind, plaintext));
            Assert.Equal(plaintext, decoded);
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(2048)]
    [InlineData(3000)]
    public void AllSpaces_RoundTrips(int length)
    {
        var plaintext = new byte[length];
        Array.Fill(plaintext, (byte)0x20);
        var decoded = LzhufContainer.Decode(LzhufContainerKind.B1, LzhufContainer.Encode(LzhufContainerKind.B1, plaintext));
        Assert.Equal(plaintext, decoded);
    }

    [Fact]
    public void CompressibleText_RoundTrips_At64K()
    {
        var line = Vectors.Ascii("The quick brown fox jumps over the lazy dog 0123456789.\r\n");
        var plaintext = new byte[65536];
        for (var i = 0; i < plaintext.Length; i++)
        {
            plaintext[i] = line[i % line.Length];
        }

        var encoded = LzhufContainer.Encode(LzhufContainerKind.B1, plaintext);
        Assert.True(encoded.Length < plaintext.Length / 2, "repetitive text should compress well");
        Assert.Equal(plaintext, LzhufContainer.Decode(LzhufContainerKind.B1, encoded));
    }

    // --- Error paths ---

    [Fact]
    public void CorruptedCrc_IsRejectedWithTypedError()
    {
        var encoded = LzhufContainer.Encode(LzhufContainerKind.B1, Vectors.Ascii(Vectors.HelloText));
        encoded[0] ^= 0xFF;
        var ex = Assert.Throws<LzhufCrcMismatchException>(
            () => LzhufContainer.Decode(LzhufContainerKind.B1, encoded));
        Assert.NotEqual(ex.StoredCrc, ex.ComputedCrc);
    }

    [Fact]
    public void CorruptedBitstream_FailsCrcVerification()
    {
        var encoded = LzhufContainer.Encode(LzhufContainerKind.B1, Vectors.Ascii(Vectors.BpqMsgText));
        encoded[^1] ^= 0x01;
        Assert.Throws<LzhufCrcMismatchException>(() => LzhufContainer.Decode(LzhufContainerKind.B1, encoded));
    }

    [Theory]
    [InlineData(LzhufContainerKind.B, 3)]
    [InlineData(LzhufContainerKind.B1, 5)]
    public void TruncatedContainer_Throws(LzhufContainerKind kind, int length)
    {
        var ex = Record.Exception(() => LzhufContainer.Decode(kind, new byte[length]));
        Assert.IsAssignableFrom<LzhufFormatException>(ex);
    }

    [Fact]
    public void OversizedLengthHeader_IsRejected()
    {
        // [len32 = 0xFFFFFFF0][no bitstream] must not allocate 4 GB.
        Assert.Throws<LzhufFormatException>(
            () => Lzhuf.Decode([0xF0, 0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void Crc16Xmodem_MatchesKnownValue()
    {
        // CRC-16/XMODEM("123456789") = 0x31C3 — the standard check value.
        Assert.Equal(0x31C3, Crc16Xmodem.Compute(Vectors.Ascii("123456789")));
    }
}
