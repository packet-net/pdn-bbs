using System.Globalization;
using System.Text;

namespace Bbs.Fbb.Tests;

/// <summary>
/// Golden vectors. The primary vector is the spec's verified-research
/// "Hello World!\r\n" pin (compat spec §6 / design.md decision 2); the rest
/// were generated for this suite with the built reference binary
/// /tmp/lztest/lzhuf_1 (lzhuf_1.c, the canonical FBB compressor) on
/// 2026-06-11 and are pinned here byte-for-byte.
/// </summary>
internal static class Vectors
{
    /// <summary>"Hello World!\r\n" → e1 container — the spec's pinned vector.</summary>
    public const string HelloE1 = "b5660e000000ea7c7f187fbd678f0bfd7fe1adcce580";

    public const string HelloText = "Hello World!\r\n";

    /// <summary>A BPQ-shaped forwarded message (R: line + blank + body), e1 container.</summary>
    public const string BpqMsgE1 =
        "090c75000000ef71b7dc2de616f7037783c5dfdcf36b2e61ddf33a7cee1f73a1dadd6beb55afb5b4c5a7d1f0d0eddbc6" +
        "042f54b999cb50d0753e3f891fdcdf97fa73f33403e93d5f0c3d224f7dd890fc12bf5ffc14402f6c32d0219c452f09bc" +
        "073f9aeaf73bf802bc0ed9d553d34becabd88ca7911c";

    /// <summary>The same plaintext in the CRC-less "B" (V0) container — reference binary, option e.</summary>
    public const string BpqMsgB =
        "75000000ef71b7dc2de616f7037783c5dfdcf36b2e61ddf33a7cee1f73a1dadd6beb55afb5b4c5a7d1f0d0eddbc6042f" +
        "54b999cb50d0753e3f891fdcdf97fa73f33403e93d5f0c3d224f7dd890fc12bf5ffc14402f6c32d0219c452f09bc073f" +
        "9aeaf73bf802bc0ed9d553d34becabd88ca7911c";

    public const string BpqMsgText =
        "R:260611/0930Z 123@GB7PDN.#23.GBR.EURO PDN0.1\r\n\r\n" +
        "Hello from the PDN BBS.\r\nThis is a test message body.\r\n73 de M0LTE\r\n";

    /// <summary>6840 bytes of repetitive text — exercises the N=2048 window wrap several times over.</summary>
    public const string WrapE1 =
        "57a8b81a0000ec7d7f5f1d66f23001c6c1f07d30aeff40713dff7bbeeff7ec0f0eaf9598255f6b8fcff3ff55202a3695" +
        "005f87bf1ed06058fc29fcf7599cb408e37bac1c6fa5871bf8838e0491c705d1c70a11c70e31c71231c7177838976bb9" +
        "66d7730dcee587bb971eee607bb985eee570772b4772d47732aaee592bb978aee515772c2bb9755dcbacee4ecee50cee" +
        "52cee6519dc9d6772e59dc9aeee510ee570ee580ee4a1dc983b938773225dc8c2ee5717721177229772b1772b177202e" +
        "e405dc84bb9902ee7fcbb9565dcfe2ee422ee542ee552ee7e4ee7e4ee409dcc5a773fc9dc984ee7da773f93b92d3b92f" +
        "3b9f33b9f73b9fb3b98acee7c73b92b9dcf7cee7de7724e77256773d6773de773e6773139dcf867724b3b9e99dcf7cee" +
        "48cee48cee79cee79cee7acee6233b9e3cee47f77383bb9c9ddc8dddc8fddcdfddcdfddce395773a50613b915d8c7722" +
        "0f71dc8ade07722be13b9a821bb9b0553b9b535bb9b5573b9b57ddcdbbb9b77736eee77ddcefbb9df773beee77ddc877" +
        "72051c5680";

    /// <summary>256 × 0x20 — degenerate matches against the space-prefilled window.</summary>
    public const string SpacesE1 = "744d00010000c51de28030803100264000";

    /// <summary>512 deterministic pseudo-random bytes (incompressible: output is longer than the input).</summary>
    public const string LcgE1 =
        "e8a40002000015c53101d6747f221ed0df0679e98107798397436f25b91a3cfd6ec0dd60c87fa8d5ee290fe8b779f1f0" +
        "4b235d25d61b84ef10333f2c307876bbf7ee7bb33ffa87f484de6bbb03733f5c8e3372f76951af12f577f5c9d4f53efa" +
        "bdf13f008f9950d1b7dce559123b46bd65a02c16c6b4eec6de16fb5d3185bf3acaffc4fb24a1b703f0fb4f7b8f85df1f" +
        "451cd0ede45fe39ea7d1e4d8eb176951e6aac48833d279fce25f1664fa2a558373d5fc9922ff8d79cccd54e72ae7e9e2" +
        "ca688dadd7dd7f774e82b0b5a56470abdb82f8f7b1c7b63fb4b6bfaf22828df00916080096d99da686b216b4c45bf16b" +
        "323317f336a2a36fa3a7636e8979d259106816fa960bf87abe89abcce40549221f3633a76f102b1cf6052c4aa4d5059e" +
        "58ea1fa0133c121f40c39963ad5e0e408874866d411bbb4e55cba35c0366fbe1fe9c9f2256f6bfc694ed866600dfc3e7" +
        "57f8531e7669e8cd09821e0e7563c382048d7f1b7a7adccbfd53f0ee5dac9e6f98bbf9357103c4230e2c71ec18e3443f" +
        "f4d0dc07ae77650e3816fcc9591342e839576e49a3354117d51670aba8182f43efb2b79c48a8fe7f46e2e056cc4296d0" +
        "35607b7dd40e580df4a58decef112ae59e5f330cba94b8e57faa02a4279d11a1cce182fbd0cf77816680376c0591a478" +
        "82ab7da4865575f37d055bbb624ccf74042f3f3aa5dcbfb3b3ee81dc2aca0c6882e0600849af43fb958787652dd523d3" +
        "55a9d8b4f1c37934cf3920";

    /// <summary>Empty input → e1 is CRC(0x0000) + zero length, six zero bytes.</summary>
    public const string EmptyE1 = "000000000000";

    public static byte[] Hex(string hex) => Convert.FromHexString(hex);

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>The window-wrap plaintext: 120 × "Line %04d: The quick brown fox jumps over the lazy dog.\r\n".</summary>
    public static byte[] WrapText()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 120; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Line {i:D4}: The quick brown fox jumps over the lazy dog.\r\n");
        }

        return Ascii(sb.ToString());
    }

    /// <summary>The deterministic LCG byte stream used for the incompressible vector (seed 42).</summary>
    public static byte[] LcgBytes(int count, uint seed = 42)
    {
        var result = new byte[count];
        var x = (ulong)seed;
        for (var i = 0; i < count; i++)
        {
            x = ((x * 1103515245UL) + 12345UL) & 0x7FFFFFFF;
            result[i] = (byte)((x >> 16) & 0xFF);
        }

        return result;
    }
}
