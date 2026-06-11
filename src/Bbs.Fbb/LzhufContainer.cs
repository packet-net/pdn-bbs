using System.Buffers.Binary;

namespace Bbs.Fbb;

/// <summary>
/// Which on-the-wire wrapper surrounds the LZHUF compressed object —
/// spec §3.7 payload-layout table.
/// </summary>
public enum LzhufContainerKind
{
    /// <summary>FBB compressed V0 ("B"): <c>[u32 LE uncompressed length][bitstream]</c>.</summary>
    B = 0,

    /// <summary>
    /// FBB compressed V1 ("B1"/"B2F", lzhuf_1 option <c>e1</c>):
    /// <c>[CRC16-XMODEM LE over length+bitstream][u32 LE length][bitstream]</c>.
    /// </summary>
    B1 = 1,
}

/// <summary>
/// Encodes/decodes the two FBB compressed-message containers around the
/// <see cref="Lzhuf"/> codec — spec §3.7: "In case of forwarding with a BBS
/// using version 0, only the part from offset 2 will be sent" [IW3FQG], the
/// CRC is "computed for the full binary file including the length of the
/// uncompressed file" and stored little-endian.
/// </summary>
public static class LzhufContainer
{
    /// <summary>Byte length of the CRC16 prefix present in the <see cref="LzhufContainerKind.B1"/> container.</summary>
    public const int CrcPrefixLength = 2;

    /// <summary>Compresses <paramref name="plaintext"/> into the requested container.</summary>
    public static byte[] Encode(LzhufContainerKind kind, ReadOnlySpan<byte> plaintext)
    {
        var body = Lzhuf.Encode(plaintext);
        if (kind == LzhufContainerKind.B)
        {
            return body;
        }

        var result = new byte[CrcPrefixLength + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, Crc16Xmodem.Compute(body));
        body.CopyTo(result.AsSpan(CrcPrefixLength));
        return result;
    }

    /// <summary>
    /// Decompresses a container of the given kind. For
    /// <see cref="LzhufContainerKind.B1"/> the CRC16 is verified over the
    /// length header plus bitstream before decoding ("the only mean to be
    /// sure that the part of the already received file matches" — spec §3.8).
    /// </summary>
    /// <exception cref="LzhufCrcMismatchException">The B1 container's CRC16 does not match its contents.</exception>
    /// <exception cref="LzhufFormatException">The container is structurally truncated.</exception>
    public static byte[] Decode(
        LzhufContainerKind kind,
        ReadOnlySpan<byte> container,
        int maxDecodedLength = Lzhuf.DefaultMaxDecodedLength)
    {
        if (kind == LzhufContainerKind.B)
        {
            return Lzhuf.Decode(container, maxDecodedLength);
        }

        if (container.Length < CrcPrefixLength + 4)
        {
            throw new LzhufFormatException(
                $"B1 container is {container.Length} bytes; the CRC16 + length header alone require 6.");
        }

        var stored = BinaryPrimitives.ReadUInt16LittleEndian(container);
        var computed = Crc16Xmodem.Compute(container[CrcPrefixLength..]);
        if (stored != computed)
        {
            throw new LzhufCrcMismatchException(stored, computed);
        }

        return Lzhuf.Decode(container[CrcPrefixLength..], maxDecodedLength);
    }
}

/// <summary>
/// A structurally invalid LZHUF compressed object (truncated header or a
/// length that exceeds the configured decode cap).
/// </summary>
public class LzhufFormatException : FbbProtocolException
{
    /// <summary>Creates the exception with a default message.</summary>
    public LzhufFormatException()
    {
    }

    /// <summary>Creates the exception with a diagnostic message.</summary>
    public LzhufFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    public LzhufFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The CRC16 stored in a B1 ("e1") container did not match the CRC computed
/// over its length header + bitstream (spec §3.7). The corresponding wire
/// failure class is <c>*** Message Checksum Error</c> (spec §3.12).
/// </summary>
public sealed class LzhufCrcMismatchException : LzhufFormatException
{
    /// <summary>Creates the exception with a default message.</summary>
    public LzhufCrcMismatchException()
    {
    }

    /// <summary>Creates the exception with a diagnostic message.</summary>
    public LzhufCrcMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    public LzhufCrcMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception carrying the conflicting CRC values.</summary>
    public LzhufCrcMismatchException(ushort stored, ushort computed)
        : base($"B1 container CRC16 mismatch: stored 0x{stored:X4}, computed 0x{computed:X4}.")
    {
        StoredCrc = stored;
        ComputedCrc = computed;
    }

    /// <summary>The CRC carried in the container's first two bytes (little-endian).</summary>
    public ushort StoredCrc { get; }

    /// <summary>The CRC computed over the container's length header + bitstream.</summary>
    public ushort ComputedCrc { get; }
}
