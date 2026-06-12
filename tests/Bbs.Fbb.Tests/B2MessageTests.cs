using System.Text;

namespace Bbs.Fbb.Tests;

public class B2MessageTests
{
    // --- Encode: byte-exact golden vectors (spec §3.9) -----------------------

    [Fact]
    public void Encode_ProducesTheCanonicalLinbpqHeaderOrder()
    {
        // The header order LinBPQ emits for a natively-stored message
        // [BPQ-SRC FBBRoutines.c:1816, spec §3.9 ~line 370]:
        //   MID Date Type From To Subject Mbo Content-Type
        //   Content-Transfer-Encoding Body, blank line, then the body.
        // Body: count EXCLUDES the terminating CRLF (mandatory + additional).
        var msg = new B2Message
        {
            Mid = "123_GB7PDN",
            Date = "2026/06/12 14:33",
            Type = B2MessageType.Private,
            From = "M0LTE",
            To = ["G8BPQ"],
            Subject = "Hello there",
            Mbo = "GB7PDN",
            Body = Encoding.ASCII.GetBytes("Hello, world!\r\n"),
        };

        const string expectedHeader =
            "MID: 123_GB7PDN\r\n" +
            "Date: 2026/06/12 14:33\r\n" +
            "Type: Private\r\n" +
            "From: M0LTE\r\n" +
            "To: G8BPQ\r\n" +
            "Subject: Hello there\r\n" +
            "Mbo: GB7PDN\r\n" +
            "Content-Type: text/plain\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "Body: 15\r\n" + // "Hello, world!\r\n" is exactly 15 bytes
            "\r\n";
        // Header + body (15 bytes) + the mandatory additional terminating CRLF.
        var expected = Encoding.ASCII.GetBytes(expectedHeader + "Hello, world!\r\n" + "\r\n");

        Assert.Equal(expected, msg.Encode());
    }

    [Fact]
    public void Encode_RepeatsToAndCcLines_AndOrdersFilesAfterBody()
    {
        // Multiple To:/Cc: become repeated lines; File: order = attachment
        // order; File:/Body: counts exclude their terminating CRLF (spec §3.9).
        var nola = Encoding.ASCII.GetBytes("0123456789"); // 10 bytes
        var boat = Encoding.ASCII.GetBytes("abcdefghijklmno"); // 15 bytes
        var msg = new B2Message
        {
            Mid = "12345_K4CJX",
            Date = "1999/09/22 14:33",
            Type = B2MessageType.Private,
            From = "SMTP:user@example.com",
            To = ["W1AW", "W4ABC"],
            Cc = ["N8PGR"],
            Subject = "This is a sample address header",
            Mbo = "SMTP",
            Body = Encoding.ASCII.GetBytes("body text"), // 9 bytes
            Files =
            [
                new B2Attachment("NOLA.XLS", nola),
                new B2Attachment("NEWBOAT.HOMEPORT.JPG", boat),
            ],
        };

        const string expectedHeader =
            "MID: 12345_K4CJX\r\n" +
            "Date: 1999/09/22 14:33\r\n" +
            "Type: Private\r\n" +
            "From: SMTP:user@example.com\r\n" +
            "To: W1AW\r\n" +
            "To: W4ABC\r\n" +
            "Cc: N8PGR\r\n" +
            "Subject: This is a sample address header\r\n" +
            "Mbo: SMTP\r\n" +
            "Content-Type: text/plain\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "Body: 9\r\n" +
            "File: 10 NOLA.XLS\r\n" +
            "File: 15 NEWBOAT.HOMEPORT.JPG\r\n" +
            "\r\n";
        // body+CRLF, then each attachment+CRLF, in File: order.
        var expected = Encoding.ASCII.GetBytes(expectedHeader)
            .Concat(Encoding.ASCII.GetBytes("body text\r\n"))
            .Concat(nola).Concat(Encoding.ASCII.GetBytes("\r\n"))
            .Concat(boat).Concat(Encoding.ASCII.GetBytes("\r\n"))
            .ToArray();

        Assert.Equal(expected, msg.Encode());
    }

    [Theory]
    [InlineData(B2MessageType.Private, "Private")]
    [InlineData(B2MessageType.Bulletin, "Bulletin")]
    [InlineData(B2MessageType.Service, "Service")]
    [InlineData(B2MessageType.Inquiry, "Inquiry")]
    [InlineData(B2MessageType.PositionReport, "Position Report")]
    [InlineData(B2MessageType.PositionRequest, "Position Request")]
    [InlineData(B2MessageType.Option, "Option")]
    [InlineData(B2MessageType.System, "System")]
    public void Encode_SpellsOutTheTypeWord(B2MessageType type, string word)
    {
        var msg = NewMinimal() with { Type = type };
        var text = Encoding.ASCII.GetString(msg.Encode());
        Assert.Contains($"Type: {word}\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_RejectsEmptyBody()
    {
        // "body may not be empty" (spec §3.9).
        var msg = NewMinimal() with { Body = ReadOnlyMemory<byte>.Empty };
        Assert.Throws<FbbProtocolException>(() => msg.Encode());
    }

    [Fact]
    public void Encode_RequiresAtLeastOneRecipient()
    {
        var msg = NewMinimal() with { To = [] };
        Assert.Throws<FbbProtocolException>(() => msg.Encode());
    }

    // --- Decode: parse-tolerance (spec §3.9) ---------------------------------

    [Fact]
    public void Decode_IsCaseInsensitiveOnFieldNamesButKeepsValueCase()
    {
        // case-insensitive field NAMES; field VALUES preserve case (spec §3.9).
        var bytes = Ascii(
            "mid: 7_GB7PDN\r\n" +
            "DATE: 2026/06/12 09:00\r\n" +
            "tYpE: Private\r\n" +
            "from: M0LTE\r\n" +
            "TO: G8BPQ\r\n" +
            "subject: MixedCase Subject\r\n" +
            "\r\n" +
            "Body Bytes\r\n");
        var msg = B2Message.Decode(bytes);
        Assert.Equal("7_GB7PDN", msg.Mid);
        Assert.Equal("MixedCase Subject", msg.Subject); // value case preserved
        Assert.Equal(["G8BPQ"], msg.To);
        Assert.Equal("Body Bytes\r\n", Encoding.ASCII.GetString(msg.Body.Span));
    }

    [Fact]
    public void Decode_IgnoresUnknownFields()
    {
        // "unknown fields MUST be ignored" (spec §3.9).
        var bytes = Ascii(
            "Mid: 7_GB7PDN\r\n" +
            "X-Weird-Field: whatever\r\n" +
            "To: G8BPQ\r\n" +
            "Content-Type: text/plain\r\n" + // known-but-fixed, also tolerated
            "\r\n" +
            "hello\r\n");
        var msg = B2Message.Decode(bytes);
        Assert.Equal("7_GB7PDN", msg.Mid);
        Assert.Equal(["G8BPQ"], msg.To);
    }

    [Fact]
    public void Decode_CollectsMultipleToAndCcLines_InOrder()
    {
        var bytes = Ascii(
            "Mid: 7_GB7PDN\r\n" +
            "To: W1AW\r\n" +
            "To: W4ABC\r\n" +
            "Cc: N8PGR\r\n" +
            "Cc: K4CJX\r\n" +
            "\r\n" +
            "body\r\n");
        var msg = B2Message.Decode(bytes);
        Assert.Equal(["W1AW", "W4ABC"], msg.To);
        Assert.Equal(["N8PGR", "K4CJX"], msg.Cc);
    }

    [Theory]
    [InlineData("12345_K4CJX@MPS@R", "12345_K4CJX")]
    [InlineData("99_GB7PDN", "99_GB7PDN")]
    public void Decode_StripsMpsRSuffixFromMid(string wireMid, string expected)
    {
        // "B2F MIDs may arrive with @MPS@R suffixes — strip them"
        // [BPQ-SRC FBBRoutines.c:708, spec §2.3].
        var bytes = Ascii($"Mid: {wireMid}\r\nTo: G8BPQ\r\n\r\nbody\r\n");
        Assert.Equal(expected, B2Message.Decode(bytes).Mid);
    }

    [Fact]
    public void Decode_RejectsWhenMidIsNotFirst()
    {
        // "Mid: MUST be the first line" (spec §3.9).
        var bytes = Ascii("To: G8BPQ\r\nMid: 7_GB7PDN\r\n\r\nbody\r\n");
        Assert.Throws<FbbProtocolException>(() => B2Message.Decode(bytes));
    }

    [Fact]
    public void Decode_RejectsEmptyBody()
    {
        // "body may not be empty" (spec §3.9) — header, blank line, nothing.
        var bytes = Ascii("Mid: 7_GB7PDN\r\nTo: G8BPQ\r\n\r\n");
        Assert.Throws<FbbProtocolException>(() => B2Message.Decode(bytes));
    }

    [Fact]
    public void Decode_ParsesBodyAndAttachmentsByTheirCounts()
    {
        var nola = Encoding.ASCII.GetBytes("0123456789"); // 10
        var bytes = Encoding.ASCII.GetBytes(
                "Mid: 12345_K4CJX\r\n" +
                "To: W1AW\r\n" +
                "Body: 9\r\n" +
                "File: 10 NOLA.XLS\r\n" +
                "\r\n" +
                "body text\r\n")
            .Concat(nola).Concat(Encoding.ASCII.GetBytes("\r\n"))
            .ToArray();
        var msg = B2Message.Decode(bytes);
        Assert.Equal("body text", Encoding.ASCII.GetString(msg.Body.Span));
        var file = Assert.Single(msg.Files);
        Assert.Equal("NOLA.XLS", file.Name);
        Assert.Equal(nola, file.Content.ToArray());
    }

    [Fact]
    public void Decode_ToleratesBareCrAndLfHeaderTermination()
    {
        // BPQ emits CR-terminated lines; tolerate both on receive (spec §3.13.2).
        var bytes = Ascii("Mid: 7_GB7PDN\nTo: G8BPQ\n\nbody\n");
        var msg = B2Message.Decode(bytes);
        Assert.Equal("7_GB7PDN", msg.Mid);
        Assert.Equal(["G8BPQ"], msg.To);
    }

    // --- Round-trip through the existing B1 LZHUF container (spec §3.7) -------

    [Fact]
    public void B2Object_TwoToCcAndTwoFiles_RoundTripsThroughTheContainer()
    {
        // The B2-completeness wire shape: 2 To + 1 Cc + 2 File: parts survive Encode → container →
        // Decode byte-exact (the codec was already complete; this pins the multi-everything wire).
        byte[] f1 = [0x01, 0x02, 0x03, 0x00, 0xFF, 0x80];
        byte[] f2 = Encoding.ASCII.GetBytes("second file payload\r\n");
        var original = new B2Message
        {
            Mid = "99_GB7PDN",
            Type = B2MessageType.Private,
            From = "M0LTE",
            To = ["W1AW", "W4ABC"],
            Cc = ["N8PGR"],
            Subject = "Multi everything",
            Mbo = "GB7PDN",
            Body = Encoding.ASCII.GetBytes("Body for many.\r\n"),
            Files = [new B2Attachment("ONE.BIN", f1), new B2Attachment("two.txt", f2)],
        };

        var roundTripped = B2Message.Decode(
            LzhufContainer.Decode(LzhufContainerKind.B1, LzhufContainer.Encode(LzhufContainerKind.B1, original.Encode())));

        Assert.Equal(["W1AW", "W4ABC"], roundTripped.To);
        Assert.Equal(["N8PGR"], roundTripped.Cc);
        Assert.Equal(2, roundTripped.Files.Count);
        Assert.Equal("ONE.BIN", roundTripped.Files[0].Name);
        Assert.Equal(f1, roundTripped.Files[0].Content.ToArray());
        Assert.Equal("two.txt", roundTripped.Files[1].Name);
        Assert.Equal(f2, roundTripped.Files[1].Content.ToArray());
    }

    [Fact]
    public void B2Object_RidesTheExistingB1LzhufContainerUnchanged()
    {
        // "For FC, the plaintext is the entire B2 message" (spec §3.7): the
        // B2 object is content-agnostic input to the existing B1 container.
        var original = new B2Message
        {
            Mid = "42_GB7PDN",
            Date = "2026/06/12 14:33",
            Type = B2MessageType.Private,
            From = "M0LTE",
            To = ["W1AW", "W4ABC"],
            Cc = ["N8PGR"],
            Subject = "Round trip",
            Mbo = "GB7PDN",
            Body = Encoding.ASCII.GetBytes("The quick brown fox.\r\n"),
            Files = [new B2Attachment("DATA.BIN", new byte[] { 1, 2, 3, 4, 5, 0, 255, 128 })],
        };

        var obj = original.Encode();
        var compressed = LzhufContainer.Encode(LzhufContainerKind.B1, obj);
        var roundTrippedObj = LzhufContainer.Decode(LzhufContainerKind.B1, compressed);
        Assert.Equal(obj, roundTrippedObj); // bytes survive the container unchanged

        var decoded = B2Message.Decode(roundTrippedObj);
        Assert.Equal(original.Mid, decoded.Mid);
        Assert.Equal(original.To, decoded.To);
        Assert.Equal(original.Cc, decoded.Cc);
        Assert.Equal(original.Subject, decoded.Subject);
        Assert.Equal("The quick brown fox.\r\n", Encoding.ASCII.GetString(decoded.Body.Span));
        var file = Assert.Single(decoded.Files);
        Assert.Equal("DATA.BIN", file.Name);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 0, 255, 128 }, file.Content.ToArray());
    }

    private static B2Message NewMinimal() => new()
    {
        Mid = "1_GB7PDN",
        Type = B2MessageType.Private,
        To = ["G8BPQ"],
        Body = Encoding.ASCII.GetBytes("x"),
    };

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
}
