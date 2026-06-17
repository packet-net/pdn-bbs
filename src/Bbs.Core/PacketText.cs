using System.Text;

namespace Bbs.Core;

/// <summary>
/// Encoding helpers for human-readable header text carried over the packet
/// wire — the FBB B1 SOH title (spec §3.6) and B2F header values such as
/// <c>Subject:</c> (spec §3.9). The body of a message is handled elsewhere
/// (stored raw, decoded UTF-8-or-Latin-1 at display); these helpers cover the
/// SUBJECT/title fields, which are stored as decoded strings and so must be
/// decoded correctly at ingest and round-trip faithfully at egress.
/// </summary>
/// <remarks>
/// <para><b>Ingest (<see cref="DecodeHeader"/>):</b> decode as UTF-8 when the
/// bytes are valid UTF-8 (strict decoder), otherwise fall back to Latin-1.
/// Modern gateways/Winlink emit UTF-8 subjects; legacy FBB BBSes emit Latin-1.
/// A strict UTF-8 attempt distinguishes the two losslessly: any byte run that
/// is not well-formed UTF-8 (e.g. a lone 0xA3 '£') falls through to Latin-1,
/// where every single byte maps to the matching U+0000..U+00FF code point.</para>
///
/// <para><b>Egress (<see cref="EncodeHeader"/>):</b> always encode as UTF-8.
/// This is correct and interop-safe:</para>
/// <list type="bullet">
/// <item>UTF-8 of an ASCII string == ASCII bytes == Latin-1 bytes, so ASCII
/// subjects are byte-identical on the wire — no interop change.</item>
/// <item>A UTF-8 subject decoded then UTF-8-encoded reproduces the SAME bytes
/// (byte-faithful round-trip), so a received UTF-8 subject is forwarded
/// unchanged.</item>
/// <item>Only a genuine Latin-1 high-byte subject (e.g. 0xA3 '£', invalid
/// UTF-8 → decoded as Latin-1 → U+00A3) re-encodes to 2 UTF-8 bytes
/// (0xC2 0xA3) — a deliberate, rare "upgrade". This fixes display without
/// degrading the forward; Latin-1 egress would instead turn a received UTF-8
/// subject into '?' downstream, which is why we do NOT encode as Latin-1.</item>
/// </list>
/// </remarks>
public static class PacketText
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes packet header text: UTF-8 when <paramref name="bytes"/> is valid
    /// UTF-8, otherwise Latin-1 (a byte-for-byte fallback that never fails). See
    /// the type remarks for why this losslessly distinguishes the two encodings.
    /// </summary>
    public static string DecodeHeader(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// Encodes packet header text as UTF-8. ASCII strings are byte-identical to
    /// their ASCII/Latin-1 form (no interop change); a string previously decoded
    /// from UTF-8 by <see cref="DecodeHeader"/> re-encodes to the same bytes
    /// (byte-faithful round-trip). See the type remarks for the full rationale.
    /// </summary>
    public static byte[] EncodeHeader(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Decodes a stored message BODY for DISPLAY: UTF-8 when the bytes are valid UTF-8 (the common
    /// ASCII / UTF-8 case — gateways, Winlink, copy-pasted smart quotes, an SMTP-submitted Unicode
    /// body), otherwise Latin-1 (genuine 8-bit packet content, e.g. an inline 7plus block whose
    /// high-byte alphabet is not well-formed UTF-8). Bodies are stored byte-transparent (Latin-1
    /// round-trips for forwarding fidelity — see <see cref="Message.GetBodyText"/>); this render-only
    /// helper recovers the intended text. The same strict-UTF-8-then-Latin-1 discrimination as
    /// <see cref="DecodeHeader"/>, just over the body span.
    /// </summary>
    public static string DecodeBody(ReadOnlySpan<byte> bytes) => DecodeHeader(bytes);

    /// <summary>
    /// Encodes a body string for storage without losing any character. ASCII / Latin-1 text encodes as
    /// Latin-1 — byte-transparent, byte-identical on the packet wire, exactly the historical path
    /// (no interop change, the forwarding round-trip is preserved). Text carrying a character outside
    /// Latin-1 (a code point above U+00FF — €, emoji, CJK, …) encodes as UTF-8 instead, so it survives
    /// storage losslessly; <see cref="DecodeBody"/> recovers it (the bytes are well-formed UTF-8, so the
    /// strict-UTF-8 display decode picks them up). The historical <c>Encoding.Latin1.GetBytes</c> silently
    /// mapped such characters to '?', which this replaces.
    /// </summary>
    /// <remarks>
    /// A body that already carries raw 8-bit binary (an inline 7plus block) must stay Latin-1 so the
    /// blob round-trips byte-exact; such a body is pure Latin-1 by construction (the 7plus alphabet is
    /// &lt;= 0xFC and the prose is the user's text), so it takes the Latin-1 branch — a body carrying a
    /// 7plus blob never holds a character above U+00FF, so this method never wrongly UTF-8-encodes one.
    /// </remarks>
    public static byte[] EncodeBody(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            if (c > 'ÿ')
            {
                return Encoding.UTF8.GetBytes(text);
            }
        }

        return Encoding.Latin1.GetBytes(text);
    }
}
