namespace Bbs.SevenPlus;

/// <summary>
/// The outcome of assembling a set of 7plus parts: whether the file is complete,
/// which parts are missing, and the per-line CRC accounting. 7plus has no
/// error-correction — a CRC-failed code line leaves zeros in the output and is
/// reported as corrupt; a wholly absent part is reported as missing. The report
/// is the host's signal for whether to surface the file or wait/ask for resends.
/// </summary>
public sealed class AssemblyReport
{
    internal AssemblyReport(
        bool isComplete,
        string fileName,
        int fileSize,
        int totalParts,
        IReadOnlyList<int> receivedParts,
        IReadOnlyList<int> missingParts,
        long? timestamp,
        int totalCodeLines,
        int corruptedLines,
        int missingLines)
    {
        IsComplete = isComplete;
        FileName = fileName;
        FileSize = fileSize;
        TotalParts = totalParts;
        ReceivedParts = receivedParts;
        MissingParts = missingParts;
        Timestamp = timestamp;
        TotalCodeLines = totalCodeLines;
        CorruptedLines = corruptedLines;
        MissingLines = missingLines;
    }

    /// <summary>
    /// True when every part is present <b>and</b> no code line was missing or
    /// corrupt — i.e. the returned content is the original file byte-for-byte.
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>The recovered original filename (the long name when available, else the DOS name).</summary>
    public string FileName { get; }

    /// <summary>The original file size in bytes.</summary>
    public int FileSize { get; }

    /// <summary>The total number of parts the file was split into.</summary>
    public int TotalParts { get; }

    /// <summary>The part numbers that were supplied, ascending.</summary>
    public IReadOnlyList<int> ReceivedParts { get; }

    /// <summary>The part numbers still absent, ascending. Empty when all parts are present.</summary>
    public IReadOnlyList<int> MissingParts { get; }

    /// <summary>The footer timestamp (unix seconds), when any part carried one.</summary>
    public long? Timestamp { get; }

    /// <summary>The total number of code lines the complete file occupies.</summary>
    public int TotalCodeLines { get; }

    /// <summary>Code lines present but failing their CRC (left as zeros in the output).</summary>
    public int CorruptedLines { get; }

    /// <summary>Code lines wholly absent (a missing line within a present part, or a missing part).</summary>
    public int MissingLines { get; }
}
