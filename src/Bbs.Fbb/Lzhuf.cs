namespace Bbs.Fbb;

/// <summary>
/// The FBB variant of the Yoshizaki/Okumura LZHUF codec (LZSS + adaptive
/// Huffman) — spec §3.7: ring-buffer window <c>N = 2048</c> (the critical
/// delta from stock lzhuf's 4096), lookahead <c>F = 60</c>,
/// <c>THRESHOLD = 2</c>, window pre-filled with 0x20, adaptive Huffman
/// rebuild at <c>MAX_FREQ = 0x8000</c>, MSB-first 16-bit bit accumulators.
/// Ported field-faithfully from <c>lzhuf_1.c</c> (the canonical FBB
/// compressor, [LZHUF1]) and pinned by golden vectors generated with the
/// built reference binary.
/// </summary>
/// <remarks>
/// This class produces and consumes the bare compressed object
/// <c>[u32 LE uncompressed length][bitstream]</c> — i.e. exactly the output
/// of lzhuf_1's <c>Encode()</c>, which is also the FBB "B" (V0) container.
/// <see cref="LzhufContainer"/> adds/strips the CRC-16 prefix for the
/// B1/B2F ("e1") container.
/// </remarks>
public static class Lzhuf
{
    /// <summary>Ring-buffer window size (FBB uses 2048, not stock lzhuf's 4096) — spec §3.7.</summary>
    public const int WindowSize = 2048;

    /// <summary>Lookahead buffer size — spec §3.7.</summary>
    public const int Lookahead = 60;

    /// <summary>Minimum match length worth encoding as a position/length pair — spec §3.7.</summary>
    public const int Threshold = 2;

    /// <summary>
    /// Default cap on the decoded size accepted by <see cref="Decode"/>. The
    /// u32 length header is attacker-controlled; BPQMail's own MaxRXSize
    /// default is 99999 (spec §4.1), so 16 MiB is generous.
    /// </summary>
    public const int DefaultMaxDecodedLength = 1 << 24;

    private const int N = WindowSize;
    private const int F = Lookahead;
    private const int Nil = N;
    private const int NChar = 256 - Threshold + F;       // 314 kinds of characters
    private const int T = (NChar * 2) - 1;               // size of the Huffman table
    private const int Root = T - 1;                      // position of root
    private const int MaxFreq = 0x8000;

    // Tables for encoding/decoding the upper 6 bits of a match position,
    // verbatim from lzhuf_1.c [LZHUF1:253-344].
    private static readonly byte[] PLen =
    [
        0x03, 0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05,
        0x05, 0x05, 0x05, 0x05, 0x06, 0x06, 0x06, 0x06,
        0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
        0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
        0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
        0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
        0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08,
        0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08,
    ];

    private static readonly byte[] PCode =
    [
        0x00, 0x20, 0x30, 0x40, 0x50, 0x58, 0x60, 0x68,
        0x70, 0x78, 0x80, 0x88, 0x90, 0x94, 0x98, 0x9C,
        0xA0, 0xA4, 0xA8, 0xAC, 0xB0, 0xB4, 0xB8, 0xBC,
        0xC0, 0xC2, 0xC4, 0xC6, 0xC8, 0xCA, 0xCC, 0xCE,
        0xD0, 0xD2, 0xD4, 0xD6, 0xD8, 0xDA, 0xDC, 0xDE,
        0xE0, 0xE2, 0xE4, 0xE6, 0xE8, 0xEA, 0xEC, 0xEE,
        0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7,
        0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF,
    ];

    private static readonly byte[] DCode = BuildDCode();
    private static readonly byte[] DLen = BuildDLen();

    /// <summary>
    /// Compresses <paramref name="input"/> to the bare FBB compressed object
    /// <c>[u32 LE uncompressed length][bitstream]</c>, including the
    /// <c>EncodeEnd</c> flush of the final partial byte (spec §3.7).
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> input)
    {
        var encoder = new Encoder(input.Length);
        return encoder.Run(input);
    }

    /// <summary>
    /// Decompresses a bare FBB compressed object
    /// <c>[u32 LE uncompressed length][bitstream]</c>. Decoding is driven by
    /// the length header; a truncated bitstream is padded with zero bits,
    /// faithful to the reference decoder's EOF handling [LZHUF1].
    /// </summary>
    /// <exception cref="LzhufFormatException">
    /// The object is shorter than its 4-byte length header, or the header
    /// declares more than <paramref name="maxDecodedLength"/> bytes.
    /// </exception>
    public static byte[] Decode(ReadOnlySpan<byte> compressed, int maxDecodedLength = DefaultMaxDecodedLength)
    {
        if (compressed.Length < 4)
        {
            throw new LzhufFormatException("Compressed object is shorter than its 4-byte length header.");
        }

        var textSize = (uint)(compressed[0] | (compressed[1] << 8) | (compressed[2] << 16) | (compressed[3] << 24));
        if (textSize > (uint)maxDecodedLength)
        {
            throw new LzhufFormatException(
                $"Declared uncompressed length {textSize} exceeds the {maxDecodedLength}-byte limit.");
        }

        if (textSize == 0)
        {
            return [];
        }

        var decoder = new Decoder();
        return decoder.Run(compressed[4..], (int)textSize);
    }

    private static byte[] BuildDCode()
    {
        // d_code from lzhuf_1.c [LZHUF1:276-309]: each upper-6-bit value v
        // with code length L (per d_len) occupies 2^(8-L) consecutive table
        // slots — 1 value × 32 slots, 3 × 16, 8 × 8, 12 × 4, 24 × 2,
        // 16 × 1. Pinned against the reference binary by the golden vectors.
        var table = new byte[256];
        var i = 0;
        byte value = 0;
        foreach (var (count, run) in new[] { (1, 32), (3, 16), (8, 8), (12, 4), (24, 2), (16, 1) })
        {
            for (var v = 0; v < count; v++)
            {
                for (var r = 0; r < run; r++)
                {
                    table[i++] = value;
                }

                value++;
            }
        }

        return table;
    }

    private static byte[] BuildDLen()
    {
        // d_len from lzhuf_1.c [LZHUF1:311-344]: 32×3, 48×4, 64×5, 48×6,
        // 48×7, 16×8.
        var table = new byte[256];
        var i = 0;
        foreach (var (count, len) in new[] { (32, 3), (48, 4), (64, 5), (48, 6), (48, 7), (16, 8) })
        {
            for (var v = 0; v < count; v++)
            {
                table[i++] = (byte)len;
            }
        }

        return table;
    }

    /// <summary>MSB-first 16-bit bit writer + LZSS encoder state (one shot per message).</summary>
    private sealed class Encoder
    {
        private readonly byte[] _textBuf = new byte[N + F - 1];
        private readonly int[] _lson = new int[N + 1];
        private readonly int[] _rson = new int[N + 256 + 2];
        private readonly int[] _dad = new int[N + 1];
        private readonly HuffmanTree _tree = new();
        private readonly List<byte> _output;

        private int _matchPosition;
        private int _matchLength;
        private ushort _putBuf;
        private int _putLen;

        public Encoder(int inputLength)
        {
            _output = new List<byte>(4 + inputLength + (inputLength / 8) + 16);
        }

        public byte[] Run(ReadOnlySpan<byte> input)
        {
            var textSize = (uint)input.Length;
            _output.Add((byte)(textSize & 0xFF));
            _output.Add((byte)((textSize >> 8) & 0xFF));
            _output.Add((byte)((textSize >> 16) & 0xFF));
            _output.Add((byte)((textSize >> 24) & 0xFF));
            if (textSize == 0)
            {
                return [.. _output];
            }

            InitTree();
            var s = 0;
            var r = N - F;
            for (var i = 0; i < r; i++)
            {
                _textBuf[i] = 0x20; // window pre-fill, spec §3.7
            }

            var pos = 0;
            int len;
            for (len = 0; len < F && pos < input.Length; len++)
            {
                _textBuf[r + len] = input[pos++];
            }

            for (var i = 1; i <= F; i++)
            {
                InsertNode(r - i);
            }

            InsertNode(r);
            do
            {
                if (_matchLength > len)
                {
                    _matchLength = len;
                }

                if (_matchLength <= Threshold)
                {
                    _matchLength = 1;
                    EncodeChar(_textBuf[r]);
                }
                else
                {
                    EncodeChar((uint)(255 - Threshold + _matchLength));
                    EncodePosition((uint)_matchPosition);
                }

                var lastMatchLength = _matchLength;
                int i2;
                for (i2 = 0; i2 < lastMatchLength && pos < input.Length; i2++)
                {
                    var c = input[pos++];
                    DeleteNode(s);
                    _textBuf[s] = c;
                    if (s < F - 1)
                    {
                        _textBuf[s + N] = c;
                    }

                    s = (s + 1) & (N - 1);
                    r = (r + 1) & (N - 1);
                    InsertNode(r);
                }

                while (i2++ < lastMatchLength)
                {
                    DeleteNode(s);
                    s = (s + 1) & (N - 1);
                    r = (r + 1) & (N - 1);
                    if (--len != 0)
                    {
                        InsertNode(r);
                    }
                }
            }
            while (len > 0);

            EncodeEnd();
            return [.. _output];
        }

        private void InitTree()
        {
            for (var i = N + 1; i <= N + 256 + 1; i++)
            {
                _rson[i] = Nil;
            }

            for (var i = 0; i < N; i++)
            {
                _dad[i] = Nil;
            }
        }

        private void InsertNode(int r)
        {
            var cmp = 1;
            var p = N + 1 + _textBuf[r];
            _rson[r] = _lson[r] = Nil;
            _matchLength = 0;
            while (true)
            {
                if (cmp >= 0)
                {
                    if (_rson[p] != Nil)
                    {
                        p = _rson[p];
                    }
                    else
                    {
                        _rson[p] = r;
                        _dad[r] = p;
                        return;
                    }
                }
                else
                {
                    if (_lson[p] != Nil)
                    {
                        p = _lson[p];
                    }
                    else
                    {
                        _lson[p] = r;
                        _dad[r] = p;
                        return;
                    }
                }

                int i;
                for (i = 1; i < F; i++)
                {
                    cmp = _textBuf[r + i] - _textBuf[p + i];
                    if (cmp != 0)
                    {
                        break;
                    }
                }

                if (i > Threshold)
                {
                    if (i > _matchLength)
                    {
                        _matchPosition = ((r - p) & (N - 1)) - 1;
                        _matchLength = i;
                        if (_matchLength >= F)
                        {
                            break;
                        }
                    }

                    if (i == _matchLength)
                    {
                        var c = ((r - p) & (N - 1)) - 1;
                        if (c < _matchPosition)
                        {
                            _matchPosition = c;
                        }
                    }
                }
            }

            _dad[r] = _dad[p];
            _lson[r] = _lson[p];
            _rson[r] = _rson[p];
            _dad[_lson[p]] = r;
            _dad[_rson[p]] = r;
            if (_rson[_dad[p]] == p)
            {
                _rson[_dad[p]] = r;
            }
            else
            {
                _lson[_dad[p]] = r;
            }

            _dad[p] = Nil;
        }

        private void DeleteNode(int p)
        {
            if (_dad[p] == Nil)
            {
                return; // not registered
            }

            int q;
            if (_rson[p] == Nil)
            {
                q = _lson[p];
            }
            else if (_lson[p] == Nil)
            {
                q = _rson[p];
            }
            else
            {
                q = _lson[p];
                if (_rson[q] != Nil)
                {
                    do
                    {
                        q = _rson[q];
                    }
                    while (_rson[q] != Nil);

                    _rson[_dad[q]] = _lson[q];
                    _dad[_lson[q]] = _dad[q];
                    _lson[q] = _lson[p];
                    _dad[_lson[p]] = q;
                }

                _rson[q] = _rson[p];
                _dad[_rson[p]] = q;
            }

            _dad[q] = _dad[p];
            if (_rson[_dad[p]] == p)
            {
                _rson[_dad[p]] = q;
            }
            else
            {
                _lson[_dad[p]] = q;
            }

            _dad[p] = Nil;
        }

        private void Putcode(int length, ushort code)
        {
            _putBuf |= (ushort)(code >> _putLen);
            _putLen += length;
            if (_putLen >= 8)
            {
                _output.Add((byte)(_putBuf >> 8));
                _putLen -= 8;
                if (_putLen >= 8)
                {
                    _output.Add((byte)_putBuf);
                    _putLen -= 8;
                    _putBuf = (ushort)(code << (length - _putLen));
                }
                else
                {
                    _putBuf <<= 8;
                }
            }
        }

        private void EncodeChar(uint c)
        {
            uint code = 0;
            var bits = 0;
            var k = _tree.Parent((int)c + T);

            // Travel from leaf to root, building the code MSB-first.
            do
            {
                code >>= 1;
                if ((k & 1) != 0)
                {
                    code += 0x8000;
                }

                bits++;
                k = _tree.Parent(k);
            }
            while (k != Root);

            Putcode(bits, (ushort)code);
            _tree.Update((int)c);
        }

        private void EncodePosition(uint c)
        {
            var i = c >> 6;
            Putcode(PLen[i], (ushort)(PCode[i] << 8));
            Putcode(6, (ushort)((c & 0x3F) << 10));
        }

        private void EncodeEnd()
        {
            if (_putLen != 0)
            {
                _output.Add((byte)(_putBuf >> 8));
            }
        }
    }

    /// <summary>MSB-first 16-bit bit reader + LZSS decoder state (one shot per message).</summary>
    private sealed class Decoder
    {
        private readonly byte[] _textBuf = new byte[N + F - 1];
        private readonly HuffmanTree _tree = new();

        private ushort _getBuf;
        private int _getLen;
        private int _pos;

        public byte[] Run(ReadOnlySpan<byte> bitstream, int textSize)
        {
            var output = new List<byte>(textSize);
            for (var i = 0; i < N - F; i++)
            {
                _textBuf[i] = 0x20; // window pre-fill, spec §3.7
            }

            var r = N - F;
            var count = 0;
            while (count < textSize)
            {
                var c = DecodeChar(bitstream);
                if (c < 256)
                {
                    output.Add((byte)c);
                    _textBuf[r++] = (byte)c;
                    r &= N - 1;
                    count++;
                }
                else
                {
                    var i = (r - DecodePosition(bitstream) - 1) & (N - 1);
                    var j = c - 255 + Threshold;
                    for (var k = 0; k < j && count < textSize; k++)
                    {
                        var b = _textBuf[(i + k) & (N - 1)];
                        output.Add(b);
                        _textBuf[r++] = b;
                        r &= N - 1;
                        count++;
                    }
                }
            }

            return [.. output];
        }

        private int GetBit(ReadOnlySpan<byte> data)
        {
            FillBuffer(data);
            var v = _getBuf;
            _getBuf = (ushort)(_getBuf << 1);
            _getLen--;
            return (v & 0x8000) != 0 ? 1 : 0;
        }

        private int GetByte(ReadOnlySpan<byte> data)
        {
            FillBuffer(data);
            var v = _getBuf;
            _getBuf = (ushort)(_getBuf << 8);
            _getLen -= 8;
            return (v >> 8) & 0xFF;
        }

        private void FillBuffer(ReadOnlySpan<byte> data)
        {
            while (_getLen <= 8)
            {
                var i = _pos < data.Length ? data[_pos++] : 0; // EOF pads with zero bits [LZHUF1 GetBit]
                _getBuf |= (ushort)(i << (8 - _getLen));
                _getLen += 8;
            }
        }

        private int DecodeChar(ReadOnlySpan<byte> data)
        {
            var c = _tree.Son(Root);
            while (c < T)
            {
                c += GetBit(data);
                c = _tree.Son(c);
            }

            c -= T;
            _tree.Update(c);
            return c;
        }

        private int DecodePosition(ReadOnlySpan<byte> data)
        {
            var i = (uint)GetByte(data);
            var c = (uint)DCode[i] << 6;
            var j = DLen[i] - 2;
            while (j-- > 0)
            {
                i = (i << 1) + (uint)GetBit(data);
            }

            return (int)(c | (i & 0x3F));
        }
    }

    /// <summary>
    /// The adaptive Huffman tree shared by encoder and decoder: stock-lzhuf
    /// tables and the MAX_FREQ-0x8000 reconstruction, byte-identical to
    /// lzhuf_1.c's StartHuff/update/reconst [LZHUF1; spec §3.7].
    /// </summary>
    private sealed class HuffmanTree
    {
        private readonly uint[] _freq = new uint[T + 1];
        private readonly int[] _prnt = new int[T + NChar];
        private readonly int[] _son = new int[T];

        public HuffmanTree()
        {
            for (var i = 0; i < NChar; i++)
            {
                _freq[i] = 1;
                _son[i] = i + T;
                _prnt[i + T] = i;
            }

            var leaf = 0;
            var node = NChar;
            while (node <= Root)
            {
                _freq[node] = _freq[leaf] + _freq[leaf + 1];
                _son[node] = leaf;
                _prnt[leaf] = _prnt[leaf + 1] = node;
                leaf += 2;
                node++;
            }

            _freq[T] = 0xFFFF;
            _prnt[Root] = 0;
        }

        public int Parent(int index) => _prnt[index];

        public int Son(int index) => _son[index];

        public void Update(int c)
        {
            if (_freq[Root] == MaxFreq)
            {
                Reconstruct();
            }

            c = _prnt[c + T];
            do
            {
                var k = ++_freq[c];

                // If the order is disturbed, exchange nodes.
                var l = c + 1;
                if (k > _freq[l])
                {
                    while (k > _freq[++l])
                    {
                    }

                    l--;
                    _freq[c] = _freq[l];
                    _freq[l] = k;

                    var i = _son[c];
                    _prnt[i] = l;
                    if (i < T)
                    {
                        _prnt[i + 1] = l;
                    }

                    var j = _son[l];
                    _son[l] = i;

                    _prnt[j] = c;
                    if (j < T)
                    {
                        _prnt[j + 1] = c;
                    }

                    _son[c] = j;

                    c = l;
                }

                c = _prnt[c];
            }
            while (c != 0); // repeat up to root
        }

        private void Reconstruct()
        {
            // Collect leaf nodes in the first half of the table and replace
            // each freq by (freq + 1) / 2.
            var j = 0;
            for (var i = 0; i < T; i++)
            {
                if (_son[i] >= T)
                {
                    _freq[j] = (_freq[i] + 1) / 2;
                    _son[j] = _son[i];
                    j++;
                }
            }

            // Reconnect: build internal nodes in frequency order.
            var next = 0;
            for (j = NChar; j < T; next += 2, j++)
            {
                var k = next + 1;
                var first = _freq[j] = _freq[next] + _freq[k];
                for (k = j - 1; first < _freq[k]; k--)
                {
                }

                k++;
                var move = j - k;
                Array.Copy(_freq, k, _freq, k + 1, move);
                _freq[k] = first;
                Array.Copy(_son, k, _son, k + 1, move);
                _son[k] = next;
            }

            // Reconnect parent pointers.
            for (var i = 0; i < T; i++)
            {
                var k = _son[i];
                if (k >= T)
                {
                    _prnt[k] = i;
                }
                else
                {
                    _prnt[k] = _prnt[k + 1] = i;
                }
            }
        }
    }
}
