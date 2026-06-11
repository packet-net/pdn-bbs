using System.Globalization;

namespace Bbs.Fbb;

/// <summary>Outcome of the R:-chain date-sanity check — spec §3.14 loop prevention (c).</summary>
public enum RLineAgeStatus
{
    /// <summary>The oldest R: line's date is plausible.</summary>
    Ok = 0,

    /// <summary>More than the allowed window into the future (BPQ holds at &gt;7 days) — spec §3.14.</summary>
    FutureDated,

    /// <summary>Older than the supplied maximum age (BID lifetime / MaxAge) — spec §3.14.</summary>
    TooOld,

    /// <summary>No usable date — BPQ holds "Corrupt R: Line - can't determine age" — spec §3.14.</summary>
    Unparseable,
}

/// <summary>
/// The per-hop routing trace line — spec §3.14. Every hop prepends one,
/// newest first. We emit exactly LinBPQ's shape
/// (<c>R:yymmdd/hhmmZ msgnum@CALL.HIER VERSION</c>, e.g.
/// <c>R:120218/1023Z 8277@G8BPQ.#23.GBR.EU BPQ1.4.48</c>) "for maximal
/// parser compatibility" [VERIFY-ORACLE #4], and parse leniently enough to
/// also extract the classic FBB form's <c>@:CALL.HIER [QTH] #:num $:BID</c>
/// tokens.
/// </summary>
public sealed record RLine
{
    /// <summary>
    /// Two-digit years at or above this pivot are 19xx; below it 20xx.
    /// Chosen as 78 (packet radio predates nothing earlier); the spec gives
    /// no pivot — documented decision.
    /// </summary>
    public const int CenturyPivot = 78;

    private RLine(string raw)
    {
        Raw = raw;
    }

    /// <summary>The line exactly as supplied.</summary>
    public string Raw { get; }

    /// <summary>The hop timestamp (UTC), when the date token parsed.</summary>
    public DateTimeOffset? Timestamp { get; private init; }

    /// <summary>The hop's bare callsign (first element of the hierarchical address).</summary>
    public string? Callsign { get; private init; }

    /// <summary>The full <c>CALL.HIER</c> element from <c>n@CALL.HIER</c> or <c>@:CALL.HIER</c>.</summary>
    public string? HierarchicalAddress { get; private init; }

    /// <summary>The hop-local message number, when present.</summary>
    public int? MessageNumber { get; private init; }

    /// <summary>The BID (<c>$:</c> token, classic FBB form only).</summary>
    public string? Bid { get; private init; }

    /// <summary>The QTH (<c>[…]</c> token, classic FBB form only).</summary>
    public string? Qth { get; private init; }

    /// <summary>The trailing software-version token of the BPQ form.</summary>
    public string? Version { get; private init; }

    /// <summary>Whether a message line is an R: line (the leading-chain membership test).</summary>
    public static bool IsRLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.StartsWith("R:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Formats the BPQ-shape R: line we prepend on every forward — spec
    /// §3.14: <c>R:%02d%02d%02d/%02d%02dZ %d@%s.%s %s</c> [BPQ-SRC].
    /// </summary>
    public static string Format(
        DateTimeOffset timestamp,
        int messageNumber,
        string bbsCallsign,
        string hierarchicalRoute,
        string softwareVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(bbsCallsign);
        ArgumentException.ThrowIfNullOrEmpty(hierarchicalRoute);
        ArgumentException.ThrowIfNullOrEmpty(softwareVersion);
        var u = timestamp.UtcDateTime;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"R:{u:yyMMdd}/{u:HHmm}Z {messageNumber}@{bbsCallsign}.{hierarchicalRoute} {softwareVersion}");
    }

    /// <summary>
    /// Parses any R: line leniently, extracting whichever of date, callsign,
    /// message number, BID and QTH are present. Returns
    /// <see langword="null"/> only for a line that is not an R: line at all;
    /// an R:-prefixed line that yields nothing parseable still returns an
    /// instance (with null fields) so loop/age checks can see it.
    /// </summary>
    public static RLine? TryParse(string line)
    {
        if (line is null || !IsRLine(line))
        {
            return null;
        }

        var rest = line[2..].Trim();

        // QTH: classic-FBB bracketed token, may contain spaces.
        string? qth = null;
        var open = rest.IndexOf('[', StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = rest.IndexOf(']', open + 1);
            if (close > open)
            {
                qth = rest[(open + 1)..close];
                rest = rest.Remove(open, close - open + 1);
            }
        }

        DateTimeOffset? timestamp = null;
        string? ha = null;
        int? msgNum = null;
        string? bid = null;
        string? version = null;

        var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (timestamp is null && TryParseDateToken(token, out var ts))
            {
                timestamp = ts;
            }
            else if (token.StartsWith("@:", StringComparison.OrdinalIgnoreCase))
            {
                ha ??= token[2..];
            }
            else if (token.StartsWith("#:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(token[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    msgNum ??= n;
                }
            }
            else if (token.StartsWith("$:", StringComparison.OrdinalIgnoreCase))
            {
                bid ??= token[2..];
            }
            else if (token.Contains('@', StringComparison.Ordinal))
            {
                // BPQ form: msgnum@CALL.HIER
                var at = token.IndexOf('@', StringComparison.Ordinal);
                var left = token[..at];
                var right = token[(at + 1)..];
                if (left.Length > 0
                    && int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    msgNum ??= n;
                }

                if (right.Length > 0)
                {
                    ha ??= right;
                }
            }
            else if (ha is not null && version is null)
            {
                version = token; // BPQ form: trailing software version
            }
        }

        var dot = ha?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        var call = ha is null ? null : (dot > 0 ? ha[..dot] : ha);
        return new RLine(line)
        {
            Timestamp = timestamp,
            Callsign = call,
            HierarchicalAddress = ha,
            MessageNumber = msgNum,
            Bid = bid,
            Qth = qth,
            Version = version,
        };
    }

    /// <summary>
    /// Extracts the leading R: chain from a message body (newest first,
    /// stopping at the first non-R: line) — the input to the spec §3.14
    /// loop checks.
    /// </summary>
    public static IReadOnlyList<RLine> ExtractLeadingRLines(IEnumerable<string> messageLines)
    {
        ArgumentNullException.ThrowIfNull(messageLines);
        var result = new List<RLine>();
        foreach (var line in messageLines)
        {
            var parsed = TryParse(line);
            if (parsed is null)
            {
                break;
            }

            result.Add(parsed);
        }

        return result;
    }

    /// <summary>Counts how many hops in the chain carry <paramref name="ownCallsign"/> — spec §3.14 check (b).</summary>
    public static int CountCallsignOccurrences(IEnumerable<RLine> chain, string ownCallsign)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentException.ThrowIfNullOrEmpty(ownCallsign);
        var count = 0;
        foreach (var r in chain)
        {
            if (string.Equals(r.Callsign, ownCallsign, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The own-call loop check — spec §3.14: "OurCount &gt; 1 → hold
    /// 'Message may be looping' (one prior transit through self is
    /// tolerated)".
    /// </summary>
    public static bool IsLikelyLooping(IEnumerable<RLine> chain, string ownCallsign) =>
        CountCallsignOccurrences(chain, ownCallsign) > 1;

    /// <summary>
    /// Date-sanity check on the <em>last</em> (oldest) R: line — spec §3.14:
    /// &gt;7 days future → hold; older than BID-lifetime/MaxAge → hold;
    /// unparseable → hold.
    /// </summary>
    public static RLineAgeStatus CheckAge(IReadOnlyList<RLine> chain, DateTimeOffset now, TimeSpan maxAge)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0 || chain[^1].Timestamp is not { } oldest)
        {
            return RLineAgeStatus.Unparseable;
        }

        if (oldest > now + TimeSpan.FromDays(7))
        {
            return RLineAgeStatus.FutureDated;
        }

        return now - oldest > maxAge ? RLineAgeStatus.TooOld : RLineAgeStatus.Ok;
    }

    private static bool TryParseDateToken(string token, out DateTimeOffset timestamp)
    {
        // yymmdd/hhmm with an optional trailing Z.
        timestamp = default;
        var t = token.TrimEnd('Z', 'z');
        if (t.Length != 11 || t[6] != '/')
        {
            return false;
        }

        Span<int> parts = stackalloc int[5];
        ReadOnlySpan<(int Start, int Len)> slices = [(0, 2), (2, 2), (4, 2), (7, 2), (9, 2)];
        for (var i = 0; i < slices.Length; i++)
        {
            if (!int.TryParse(
                    t.AsSpan(slices[i].Start, slices[i].Len),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parts[i]))
            {
                return false;
            }
        }

        var year = parts[0] >= CenturyPivot ? 1900 + parts[0] : 2000 + parts[0];
        try
        {
            timestamp = new DateTimeOffset(year, parts[1], parts[2], parts[3], parts[4], 0, TimeSpan.Zero);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false; // month 13 etc. — lenient parse, caller sees Unparseable
        }
    }
}
