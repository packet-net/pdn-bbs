// ---------------------------------------------------------------------------
// 7PLUS codec — provenance and licence
//
// The 7plus binary-over-text format is the work of Axel Bauda (DG1BBQ),
// distributed under amateur-radio terms: "no commercial use, no sale,
// circulate freely". pdn-bbs is a non-commercial amateur-radio project and
// uses the format under those terms.
//
// This C# implementation is an independent, clean-room codec written from the
// wire format. The constant tables, CRC definitions and structural offsets
// were read from the reference implementation
//   https://github.com/packethacking/7plus-browser   (TypeScript)
// which was itself built from the original C
//   https://github.com/hb9xar/7plus   (verified against 7plus v2.21 / 990411)
// to be byte-faithful. No reference code was transliterated — only the exact
// byte values (the 216-symbol alphabet, the CRC remainder seeds, the fixed
// header/footer layout) were reproduced so the two encoders agree on the wire.
// The reference's sample-data is reused as a validation oracle (golden
// vectors) in the test project; it is freely-circulatable amateur test data.
// ---------------------------------------------------------------------------

namespace Bbs.SevenPlus;

/// <summary>
/// The constant tables 7plus is built on: the 216-symbol text-safe alphabet
/// (radix-216 "code" table) and the 16-bit CRC lookup table. Both are derived
/// at type-init from the same generating rules as the reference C (utils.c
/// <c>init_codetab</c> / <c>init_crctab</c>); the values are pinned byte-for-byte
/// by the table tests against the reference.
/// </summary>
internal static class SevenPlusTables
{
    /// <summary>The radix base: the alphabet has exactly 216 symbols.</summary>
    public const int Radix = 216;

    /// <summary><c>Code[0..215]</c> → the text-safe byte written to the wire for that digit.</summary>
    public static readonly byte[] Code = BuildCode();

    /// <summary>
    /// <c>Decode[b]</c> → the radix-216 digit (0..215) for byte <c>b</c>, or
    /// <see cref="Invalid"/> (255) if <c>b</c> is not part of the alphabet.
    /// </summary>
    public static readonly byte[] Decode = BuildDecode(Code);

    /// <summary>Sentinel returned by <see cref="Decode"/> for bytes outside the alphabet.</summary>
    public const byte Invalid = 255;

    /// <summary>The 16-bit CRC lookup table (CCITT-16 polynomial, DC4OX seeds).</summary>
    public static readonly ushort[] CrcTable = BuildCrcTable();

    // The alphabet spans, in order, the printable/high byte ranges that survive
    // 7-bit-and-control-stripping text links. Exactly: 0x21..0x29, 0x2B..0x7E,
    // 0x80..0x90, 0x92, 0x94..0xFC = 9 + 84 + 17 + 1 + 105 = 216 symbols.
    private static byte[] BuildCode()
    {
        var code = new byte[Radix];
        var j = 0;
        for (var i = 0x21; i < 0x2A; i++) code[j++] = (byte)i; // 9
        for (var i = 0x2B; i < 0x7F; i++) code[j++] = (byte)i; // 84
        for (var i = 0x80; i < 0x91; i++) code[j++] = (byte)i; // 17
        code[j++] = 0x92;                                      // 1
        for (var i = 0x94; i < 0xFD; i++) code[j++] = (byte)i; // 105
        if (j != Radix)
        {
            throw new InvalidOperationException($"7plus alphabet built {j} symbols, expected {Radix}");
        }

        return code;
    }

    private static byte[] BuildDecode(byte[] code)
    {
        var decode = new byte[256];
        Array.Fill(decode, Invalid);
        for (var i = 0; i < code.Length; i++)
        {
            decode[code[i]] = (byte)i;
        }

        return decode;
    }

    // Per-bit CRC remainders from utils.c init_crctab — the byte's eight bits
    // (MSB first) each contribute one remainder, XOR-folded. This produces the
    // CCITT-16 (poly 0x1021) table indexed by the high CRC byte.
    private static ushort[] BuildCrcTable()
    {
        ReadOnlySpan<ushort> bitRemainders =
            [0x9188, 0x48C4, 0x2462, 0x1231, 0x8108, 0x4084, 0x2042, 0x1021];

        var table = new ushort[256];
        for (var n = 0; n < 256; n++)
        {
            var r = 0;
            var mask = 0x80;
            for (var m = 0; m < 8; m++, mask >>= 1)
            {
                if ((n & mask) != 0)
                {
                    r ^= bitRemainders[m];
                }
            }

            table[n] = (ushort)(r & 0xFFFF);
        }

        return table;
    }
}
