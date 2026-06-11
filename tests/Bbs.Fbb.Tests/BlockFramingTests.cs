namespace Bbs.Fbb.Tests;

public class BlockFramingTests
{
    [Fact]
    public void Header_MatchesTheSpecTranscriptShape()
    {
        // Spec §3.16(a): <01><0D>Hello<00>"     0"<00> — len 13 = 5(title)+8.
        var header = BlockFraming.EncodeHeader("Hello", 0);
        Assert.Equal((byte)0x01, header[0]);
        Assert.Equal((byte)13, header[1]);
        Assert.Equal(Vectors.Ascii("Hello"), header[2..7]);
        Assert.Equal((byte)0x00, header[7]);
        Assert.Equal(Vectors.Ascii("     0"), header[8..14]);
        Assert.Equal((byte)0x00, header[14]);
        Assert.Equal(15, header.Length);
    }

    [Fact]
    public void Trailer_ChecksumIsTwosComplementOfPayloadOnly()
    {
        byte[] payload = [0x10, 0x20, 0x30];
        Assert.Equal((byte)(0x100 - 0x60), BlockFraming.ComputeTrailerChecksum(payload));
        Assert.Equal((byte)0, BlockFraming.ComputeTrailerChecksum([]));
    }

    [Fact]
    public void EncodeMessage_SplitsInto250ByteBlocks()
    {
        var payload = Vectors.LcgBytes(306); // spec §3.16(a): blocks of 250 + 56
        var framed = BlockFraming.EncodeMessage("Hello", 0, payload);

        var reader = new FbbBlockReader();
        var status = reader.Feed(framed, out var consumed);
        Assert.Equal(FbbBlockReaderStatus.Complete, status);
        Assert.Equal(framed.Length, consumed);
        Assert.Equal("Hello", reader.Title);
        Assert.Equal(0, reader.Offset);
        Assert.Equal(payload, reader.Payload.ToArray());

        // First STX block must carry 250 bytes.
        var firstStx = framed[15];
        Assert.Equal((byte)0x02, firstStx);
        Assert.Equal((byte)250, framed[16]);
    }

    [Fact]
    public void Reader_AcceptsBlockLengthZeroAs256()
    {
        // Spec §3.6: "size = 1..255, or 0x00 meaning 256".
        var payload = Vectors.LcgBytes(256);
        var frame = new List<byte>(BlockFraming.EncodeHeader("T", 0)) { 0x02, 0x00 };
        frame.AddRange(payload);
        frame.Add(0x04);
        frame.Add(BlockFraming.ComputeTrailerChecksum(payload));

        var reader = new FbbBlockReader();
        Assert.Equal(FbbBlockReaderStatus.Complete, reader.Feed([.. frame], out _));
        Assert.Equal(payload, reader.Payload.ToArray());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("     0")]
    [InlineData("000000")]
    public void Reader_ParsesAllOffsetZeroForms(string offsetText)
    {
        // Spec §3.6: parse "0", %6d and %06d forms.
        var frame = BuildHeaderWithOffsetText("Subj", offsetText);
        var reader = new FbbBlockReader();
        reader.Feed(frame, out _);
        Assert.Equal("Subj", reader.Title);
        Assert.Equal(0, reader.Offset);
    }

    [Theory]
    [InlineData("  1234", 1234)]
    [InlineData("001234", 1234)]
    [InlineData("1234", 1234)]
    public void Reader_ParsesResumeOffsets(string offsetText, int expected)
    {
        var frame = BuildHeaderWithOffsetText("Subj", offsetText);
        var reader = new FbbBlockReader();
        reader.Feed(frame, out _);
        Assert.Equal(expected, reader.Offset);
    }

    [Fact]
    public void Reader_SurvivesByteAtATimeFeeding()
    {
        var payload = Vectors.LcgBytes(700);
        var framed = BlockFraming.EncodeMessage("Drip", 0, payload);
        var reader = new FbbBlockReader();
        var status = FbbBlockReaderStatus.NeedMoreData;
        foreach (var b in framed)
        {
            status = reader.Feed([b], out var consumed);
            Assert.Equal(1, consumed);
        }

        Assert.Equal(FbbBlockReaderStatus.Complete, status);
        Assert.Equal(payload, reader.Payload.ToArray());
    }

    [Fact]
    public void Reader_LeavesTrailingBytesUnconsumed()
    {
        var framed = BlockFraming.EncodeMessage("T", 0, Vectors.LcgBytes(10));
        var withTrailing = framed.Concat(Vectors.Ascii("FF\r\n")).ToArray();
        var reader = new FbbBlockReader();
        Assert.Equal(FbbBlockReaderStatus.Complete, reader.Feed(withTrailing, out var consumed));
        Assert.Equal(framed.Length, consumed);
    }

    [Fact]
    public void Reader_BadEotChecksum_IsTheMessageChecksumErrorClass()
    {
        var framed = BlockFraming.EncodeMessage("T", 0, Vectors.LcgBytes(10));
        framed[^1] ^= 0x01;
        var reader = new FbbBlockReader();
        Assert.Equal(FbbBlockReaderStatus.ChecksumMismatch, reader.Feed(framed, out _));
        Assert.NotEqual(reader.ComputedChecksum, reader.ReceivedChecksum);
    }

    [Fact]
    public void Reader_CorruptPayloadByte_FailsTheChecksum()
    {
        var framed = BlockFraming.EncodeMessage("T", 0, Vectors.LcgBytes(40));
        framed[20] ^= 0x55; // inside the STX payload
        var reader = new FbbBlockReader();
        Assert.Equal(FbbBlockReaderStatus.ChecksumMismatch, reader.Feed(framed, out _));
    }

    [Fact]
    public void Reader_NonSohLeadByte_IsFramingError()
    {
        var reader = new FbbBlockReader();
        Assert.Equal(FbbBlockReaderStatus.FramingError, reader.Feed([0x02, 0x01, 0x00], out _));
    }

    [Fact]
    public void Reader_ToleratesRestartPreambleWithoutStoringIt()
    {
        // Spec §3.8 [VERIFY-ORACLE #12]: on resume BPQ first emits a 6-byte
        // STX block carrying the original CRC+length, included in the EOT
        // checksum but not part of the resumed payload.
        byte[] preamble = [0xAA, 0xBB, 0x10, 0x00, 0x00, 0x00];
        var payload = Vectors.LcgBytes(30);

        var frame = new List<byte>(BuildHeaderWithOffsetText("Resumed", "   100"));
        frame.Add(0x02);
        frame.Add(6);
        frame.AddRange(preamble);
        frame.Add(0x02);
        frame.Add((byte)payload.Length);
        frame.AddRange(payload);
        frame.Add(0x04);
        frame.Add(BlockFraming.ComputeTrailerChecksum([.. preamble, .. payload]));

        var reader = new FbbBlockReader();
        Assert.Equal(FbbBlockReaderStatus.Complete, reader.Feed([.. frame], out _));
        Assert.Equal(100, reader.Offset);
        Assert.Equal(payload, reader.Payload.ToArray()); // preamble not stored
    }

    [Fact]
    public void Reader_OffsetZero_DoesNotTreatSixByteBlockAsPreamble()
    {
        // A from-scratch transfer may legitimately start with a 6-byte block
        // (e.g. the e1 container of an empty message).
        byte[] payload = [0, 0, 0, 0, 0, 0];
        var frame = new List<byte>(BlockFraming.EncodeHeader("T", 0)) { 0x02, 6 };
        frame.AddRange(payload);
        frame.Add(0x04);
        frame.Add(BlockFraming.ComputeTrailerChecksum(payload));

        var reader = new FbbBlockReader();
        Assert.Equal(FbbBlockReaderStatus.Complete, reader.Feed([.. frame], out _));
        Assert.Equal(payload, reader.Payload.ToArray());
    }

    [Fact]
    public void EncodeHeader_EnforcesWireLimits()
    {
        Assert.Throws<FbbProtocolException>(() => BlockFraming.EncodeHeader(new string('x', 81), 0));
        Assert.Throws<FbbProtocolException>(() => BlockFraming.EncodeHeader("ok", 1_000_000));
        Assert.Throws<FbbProtocolException>(() => BlockFraming.EncodeHeader("a\0b", 0));
    }

    private static byte[] BuildHeaderWithOffsetText(string title, string offsetText)
    {
        var titleBytes = Vectors.Ascii(title);
        var offsetBytes = Vectors.Ascii(offsetText);
        var header = new List<byte>
        {
            0x01,
            (byte)(titleBytes.Length + offsetBytes.Length + 2),
        };
        header.AddRange(titleBytes);
        header.Add(0x00);
        header.AddRange(offsetBytes);
        header.Add(0x00);
        return [.. header];
    }
}
