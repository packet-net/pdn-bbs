namespace Bbs.Fbb;

/// <summary>
/// CRC-16/XMODEM (CCITT polynomial 0x1021, init 0) as used by the FBB "e1"
/// compressed-message container — spec §3.7: table-driven, the table
/// "calculated by Mark G. Mendel", update
/// <c>crc = (crc &lt;&lt; 8) ^ crctab[(byte ^ (crc &gt;&gt; 8)) &amp; 0xFF]</c>
/// [LZHUF1:61–97; BPQ-SRC lzhuf32.c].
/// </summary>
public static class Crc16Xmodem
{
    private static readonly ushort[] Table = BuildTable();

    /// <summary>Computes the CRC over <paramref name="data"/> with initial value 0.</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc = (ushort)((crc << 8) ^ Table[(b ^ (crc >> 8)) & 0xFF]);
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        // Generates the canonical Mendel table (polynomial 0x1021, MSB-first);
        // pinned against the literal table in lzhuf_1.c by the golden-vector
        // tests (spec §3.7 / Appendix A).
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }

            table[i] = crc;
        }

        return table;
    }
}
