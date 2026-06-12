namespace Bbs.SevenPlus;

/// <summary>
/// Assembles a complete original file from the 7plus parts collected for it.
/// The host accumulates <see cref="SevenPlusPart"/>s (possibly across several
/// messages, possibly out of order, possibly with gaps) and calls
/// <see cref="TryAssemble"/> to attempt reconstruction. There is no
/// error-correction: a CRC-failed line leaves zeros and is reported corrupt; a
/// missing part is reported, not recovered. The reassembly is byte-exact when
/// the report says <see cref="AssemblyReport.IsComplete"/>.
/// </summary>
public static class SevenPlusFile
{
    private const int LineLength = SevenPlusCrc.LineLength;
    private const int PayloadBytesPerLine = 62;

    /// <summary>
    /// Attempts to reassemble the original file from <paramref name="parts"/>.
    /// All parts are expected to share one <see cref="SevenPlusPartIdentity"/>
    /// (group them with <see cref="SevenPlusScanner"/> output first); parts with
    /// a foreign identity are ignored. Returns whether the file is complete, the
    /// reconstructed bytes (zero-filled where data is missing/corrupt), and a
    /// report. When no usable part is supplied, returns
    /// <c>(false, null, …)</c>.
    /// </summary>
    public static (bool Complete, byte[]? Content, AssemblyReport Report) TryAssemble(
        IReadOnlyCollection<SevenPlusPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        // Adopt the identity of the most-represented part group; ignore strays.
        var identity = ChooseIdentity(parts);
        if (identity is null)
        {
            return (false, null, EmptyReport());
        }

        var id = identity.Value;
        var byPart = new Dictionary<int, SevenPlusPart>();
        string? extendedName = null;
        long? timestamp = null;
        foreach (var part in parts)
        {
            if (!part.Identity.Equals(id) || part.PartNumber < 1 || part.PartNumber > id.TotalParts)
            {
                continue;
            }

            // First-wins per part number; keep the richest extended name / timestamp.
            byPart.TryAdd(part.PartNumber, part);
            extendedName ??= part.ExtendedName;
            timestamp ??= part.Timestamp;
        }

        var fileSize = id.FileSize;
        var blockLines = id.BlockLines;
        var content = new byte[fileSize];

        var receivedParts = new List<int>();
        var missingParts = new List<int>();
        var totalCodeLines = 0;
        var corrupted = 0;
        var missingLines = 0;

        Span<byte> payload = stackalloc byte[PayloadBytesPerLine];

        for (var p = 1; p <= id.TotalParts; p++)
        {
            var partStart = (p - 1) * blockLines * PayloadBytesPerLine;
            var bytesThisPart = Math.Min(blockLines * PayloadBytesPerLine, fileSize - partStart);
            if (bytesThisPart < 0)
            {
                bytesThisPart = 0;
            }

            var linesThisPart = CeilDiv(bytesThisPart, PayloadBytesPerLine);
            totalCodeLines += linesThisPart;

            if (!byPart.TryGetValue(p, out var part))
            {
                missingParts.Add(p);
                missingLines += linesThisPart;
                continue;
            }

            receivedParts.Add(p);

            // Index this part's code lines by their declared line number; a line
            // failing its CRC is dropped (counted corrupt). Later duplicates for
            // a line number are ignored.
            var received = new Dictionary<int, byte[]>();
            foreach (var codeLine in part.CodeLines)
            {
                if (codeLine.Length != LineLength)
                {
                    continue;
                }

                if (!SevenPlusLines.TryDecodeCodeLine(codeLine, payload, out var lineNumber))
                {
                    corrupted++;
                    continue;
                }

                if (lineNumber >= 0 && !received.ContainsKey(lineNumber))
                {
                    received[lineNumber] = payload.ToArray();
                }
            }

            for (var l = 0; l < linesThisPart; l++)
            {
                var globalOff = partStart + (l * PayloadBytesPerLine);
                var remaining = fileSize - globalOff;
                var lineBytes = Math.Min(PayloadBytesPerLine, remaining);
                if (received.TryGetValue(l, out var data))
                {
                    data.AsSpan(0, lineBytes).CopyTo(content.AsSpan(globalOff));
                }
                else
                {
                    missingLines++;
                    // leave zeros
                }
            }
        }

        var fileName = ResolveFileName(extendedName, byPart, id);
        var complete = missingParts.Count == 0 && corrupted == 0 && missingLines == 0;
        var report = new AssemblyReport(
            complete, fileName, fileSize, id.TotalParts,
            receivedParts, missingParts, timestamp,
            totalCodeLines, corrupted, missingLines);
        return (complete, content, report);
    }

    private static string ResolveFileName(
        string? extendedName, Dictionary<int, SevenPlusPart> byPart, SevenPlusPartIdentity id)
    {
        if (!string.IsNullOrEmpty(extendedName))
        {
            return extendedName;
        }

        // Prefer part 1's header name; otherwise any part's (they share identity).
        if (byPart.TryGetValue(1, out var first))
        {
            return first.HeaderName.Trim();
        }

        return byPart.Count > 0
            ? byPart.Values.First().HeaderName.Trim()
            : id.HeaderName.Trim();
    }

    private static SevenPlusPartIdentity? ChooseIdentity(IReadOnlyCollection<SevenPlusPart> parts)
    {
        SevenPlusPartIdentity? best = null;
        var bestCount = 0;
        var counts = new Dictionary<SevenPlusPartIdentity, int>();
        foreach (var part in parts)
        {
            counts.TryGetValue(part.Identity, out var c);
            c++;
            counts[part.Identity] = c;
            if (c > bestCount)
            {
                bestCount = c;
                best = part.Identity;
            }
        }

        return best;
    }

    private static AssemblyReport EmptyReport()
        => new(false, string.Empty, 0, 0, [], [], null, 0, 0, 0);

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
