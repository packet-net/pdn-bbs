namespace Bbs.SevenPlus;

/// <summary>
/// One 7plus part extracted from a message: its header metadata plus the raw
/// 69-byte code lines that carry its slice of the file. A part is the unit a
/// BBS host accumulates — parts of one file may arrive across several messages
/// and are grouped by their <see cref="Identity"/>.
/// </summary>
public sealed class SevenPlusPart
{
    internal SevenPlusPart(
        SevenPlusPartIdentity identity,
        int partNumber,
        int blockLines,
        string headerName,
        string? extendedName,
        long? timestamp,
        IReadOnlyList<byte[]> codeLines)
    {
        Identity = identity;
        PartNumber = partNumber;
        BlockLines = blockLines;
        HeaderName = headerName;
        ExtendedName = extendedName;
        Timestamp = timestamp;
        CodeLines = codeLines;
    }

    /// <summary>The file-identity key this part belongs to (filename + size + geometry).</summary>
    public SevenPlusPartIdentity Identity { get; }

    /// <summary>The 1-based part number (<c>p</c> of <c>M</c>).</summary>
    public int PartNumber { get; }

    /// <summary>The total number of parts the file was split into (<c>M</c>).</summary>
    public int TotalParts => Identity.TotalParts;

    /// <summary>The original file size in bytes, from the header.</summary>
    public int FileSize => Identity.FileSize;

    /// <summary>Code lines per full part, from the header <c>%03X</c> field.</summary>
    public int BlockLines { get; }

    /// <summary>The 12-char DOS 8.3 header name (uppercase, space-padded — as on the wire).</summary>
    public string HeaderName { get; }

    /// <summary>The long original filename from the extended-name line, when present (part 1 only).</summary>
    public string? ExtendedName { get; }

    /// <summary>The footer timestamp (unix seconds), when present.</summary>
    public long? Timestamp { get; }

    /// <summary>The raw 69-byte code lines collected for this part, in wire order.</summary>
    public IReadOnlyList<byte[]> CodeLines { get; }
}

/// <summary>
/// The identity that groups parts of the same file together regardless of the
/// message they arrived in: the wire header name, the original file size, the
/// total part count, and the block geometry. Two parts with equal identities
/// belong to the same logical file.
/// </summary>
public readonly record struct SevenPlusPartIdentity(
    string HeaderName,
    int FileSize,
    int TotalParts,
    int BlockLines);
