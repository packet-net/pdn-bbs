using System.Text;
using Bbs.Core;

namespace Bbs.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PacketText"/> — the header-text codec for FBB B1
/// titles and B2F header values (e.g. <c>Subject:</c>). Pins the three
/// load-bearing invariants: ASCII is byte-identical on the wire, a UTF-8 value
/// round-trips byte-for-byte, and a genuine Latin-1 high byte is decoded
/// faithfully then "upgraded" to its 2-byte UTF-8 form on egress.
/// </summary>
public sealed class PacketTextTests
{
    [Fact]
    public void EncodeHeader_AsciiText_IsByteIdenticalToAscii()
    {
        const string text = "Hello there 123";
        var utf8 = PacketText.EncodeHeader(text);

        Assert.Equal(Encoding.ASCII.GetBytes(text), utf8);
        Assert.Equal(Encoding.Latin1.GetBytes(text), utf8); // ASCII == Latin-1 == UTF-8
    }

    [Fact]
    public void DecodeHeader_AsciiBytes_DecodesToSameString()
    {
        const string text = "Plain ASCII subject";
        Assert.Equal(text, PacketText.DecodeHeader(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void RoundTrip_AsciiSubject_IsByteExact()
    {
        const string subject = "Re: Weekly net minutes";
        var encoded = PacketText.EncodeHeader(subject);

        Assert.Equal(subject, PacketText.DecodeHeader(encoded));
        Assert.Equal(Encoding.ASCII.GetBytes(subject), encoded);
    }

    [Fact]
    public void DecodeHeader_ValidUtf8_DecodesAsUtf8()
    {
        // Smart quotes “ ” (U+201C/U+201D) + an accented char é (U+00E9).
        const string subject = "He said “hello” to José";
        var utf8 = Encoding.UTF8.GetBytes(subject);

        Assert.Equal(subject, PacketText.DecodeHeader(utf8));
    }

    [Fact]
    public void RoundTrip_Utf8Subject_IsByteFaithful()
    {
        const string subject = "He said “hello” to José";
        var wire = Encoding.UTF8.GetBytes(subject);

        // Decode-then-encode reproduces the SAME bytes.
        var decoded = PacketText.DecodeHeader(wire);
        Assert.Equal(subject, decoded);
        Assert.Equal(wire, PacketText.EncodeHeader(decoded));
    }

    [Fact]
    public void DecodeHeader_Latin1HighByte_FallsBackToLatin1()
    {
        // 0xA3 alone is invalid UTF-8 → must decode as Latin-1 → '£' (U+00A3).
        byte[] latin1 = [(byte)'C', (byte)'o', (byte)'s', (byte)'t', (byte)' ', 0xA3, (byte)'5'];

        Assert.Equal("Cost £5", PacketText.DecodeHeader(latin1));
    }

    [Fact]
    public void RoundTrip_Latin1HighByte_UpgradesToTwoByteUtf8()
    {
        // The deliberate, rare upgrade: 0xA3 ('£') → U+00A3 → 0xC2 0xA3 on egress.
        byte[] latin1 = [0xA3];
        var decoded = PacketText.DecodeHeader(latin1);

        Assert.Equal("£", decoded);
        Assert.Equal([0xC2, 0xA3], PacketText.EncodeHeader(decoded));
    }

    // ---------------------------------------------------------------- EncodeBody / DecodeBody

    [Fact]
    public void EncodeBody_AsciiText_StaysByteTransparentLatin1()
    {
        // The common path must be byte-identical to the historical Encoding.Latin1.GetBytes — no
        // interop change, the packet wire and the forwarding round-trip are untouched.
        const string body = "Hello\rWorld\r";
        Assert.Equal(Encoding.Latin1.GetBytes(body), PacketText.EncodeBody(body));
    }

    [Fact]
    public void EncodeBody_Latin1HighBytes_StayLatin1()
    {
        // £ (U+00A3) and é (U+00E9) are inside Latin-1, so the body stays a single byte each — exactly
        // the prior behaviour (BbsStore round-trips "£é\rA" as Latin-1). Every character here is <= U+00FF
        // (an ASCII hyphen, not an em-dash, which would be above the range).
        const string body = "Cost £5 - cafeé";
        Assert.True(body.All(c => c <= 'ÿ'));
        Assert.Equal(Encoding.Latin1.GetBytes(body), PacketText.EncodeBody(body));
    }

    [Fact]
    public void EncodeBody_NonLatin1_EncodesAsUtf8_LosslessRoundTrip()
    {
        // The fix: a character above U+00FF (€ U+20AC, an emoji, CJK) is NOT representable in Latin-1.
        // The old Encoding.Latin1.GetBytes mapped it to '?'; EncodeBody stores UTF-8 instead so it
        // survives, and DecodeBody recovers it exactly.
        const string body = "Price: 5€ — 日本語 😀";

        byte[] encoded = PacketText.EncodeBody(body);
        Assert.Equal(Encoding.UTF8.GetBytes(body), encoded);

        // Lossy proof: the old path would have destroyed it.
        Assert.NotEqual(body, Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(body)));

        // The new path round-trips byte-for-byte through the display decode.
        Assert.Equal(body, PacketText.DecodeBody(encoded));
    }

    [Fact]
    public void DecodeBody_InlineSevenPlusHighBytes_DecodesAsLatin1()
    {
        // A 7plus block carries raw 8-bit bytes (alphabet up to 0xFC) that are NOT well-formed UTF-8, so
        // a body containing one decodes byte-transparent as Latin-1 — never mojibaked, never altered.
        byte[] body = [(byte)'7', (byte)'+', (byte)'\r', 0x85, 0xFC, 0x92, (byte)'\r'];
        string decoded = PacketText.DecodeBody(body);

        Assert.Equal(Encoding.Latin1.GetString(body), decoded);
        Assert.Equal(body.Length, decoded.Length); // one char per byte — no multi-byte folding
    }
}
