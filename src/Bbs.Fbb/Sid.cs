using System.Globalization;

namespace Bbs.Fbb;

/// <summary>
/// The FBB System IDentifier exchanged as the first protocol line of a
/// forwarding session — spec §3.2: <c>[</c> author <c>-</c> version
/// <c>-</c> features <c>]</c>, "at least two, maximum three" hyphen-separated
/// fields [FBB-SID], features taken from after the <em>last</em> hyphen,
/// unknown feature letters ignored silently.
/// </summary>
public sealed record Sid
{
    private Sid(string raw)
    {
        Raw = raw;
    }

    /// <summary>The SID exactly as received (terminator stripped).</summary>
    public string Raw { get; }

    /// <summary>The author field (before the first hyphen).</summary>
    public string Author { get; private init; } = "";

    /// <summary>
    /// The version field(s) between the first and last hyphen, or
    /// <see langword="null"/> for a two-field SID.
    /// </summary>
    public string? Version { get; private init; }

    /// <summary>The raw feature field (after the last hyphen, inside the brackets).</summary>
    public string Features { get; private init; } = "";

    /// <summary>FBB compressed protocol offered at any version (<c>B</c>) — spec §3.2 table.</summary>
    public bool SupportsCompression { get; private init; }

    /// <summary>
    /// FBB compressed protocol V1 (<c>B1</c>, the CRC16-prefixed container).
    /// Also set when <c>B2</c> is present, because "B2 uses B1 mode (crc on
    /// front of file)" [BPQ-SRC Parse_SID, spec §3.2].
    /// </summary>
    public bool SupportsB1 { get; private init; }

    /// <summary>Winlink B2F (<c>B2</c>) — spec §3.2 table.</summary>
    public bool SupportsB2 { get; private init; }

    /// <summary>FBB basic ("blocked") protocol (<c>F</c>) — spec §3.2 table.</summary>
    public bool SupportsBlockedFbb { get; private init; }

    /// <summary>Hierarchical location designators (<c>H</c>) — spec §3.2 table.</summary>
    public bool SupportsHierarchical { get; private init; }

    /// <summary>Message identifiers on personal messages (<c>M</c>) — spec §3.2 table.</summary>
    public bool SupportsMid { get; private init; }

    /// <summary>BID support (<c>$</c>, canonically the last feature character) — spec §3.2 table.</summary>
    public bool SupportsBid { get; private init; }

    /// <summary>
    /// Whether the SID contains the substring <c>BPQ</c> anywhere. LinBPQ
    /// sniffs the whole SID string for it and enables BPQ↔BPQ extensions
    /// (spec §3.2) — exposed so a host can recognise (never trigger) them.
    /// </summary>
    public bool MentionsBpq { get; private init; }

    /// <summary>
    /// Builds our own SID, <c>[PDN-&lt;version&gt;-B1FHM$]</c> (design
    /// decision 5; spec §3.15.1), optionally adding the <c>2</c> feature
    /// digit (<c>B12FHM$</c>) once B2F lands.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The version would corrupt the SID shape, or the resulting SID would
    /// contain <c>BPQ</c> — the substring that flips LinBPQ into BPQ↔BPQ
    /// extension mode (spec §3.2's gating trap, [VERIFY-ORACLE #1]).
    /// </exception>
    public static string Build(string version, bool offerB2 = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (version.AsSpan().IndexOfAny("[]- ") >= 0)
        {
            throw new ArgumentException("SID version must not contain '[', ']', '-' or spaces.", nameof(version));
        }

        var sid = string.Create(
            CultureInfo.InvariantCulture,
            $"[PDN-{version}-{(offerB2 ? "B12FHM$" : "B1FHM$")}]");
        return sid.Contains("BPQ", StringComparison.OrdinalIgnoreCase)
            ? throw new ArgumentException("SID must never contain \"BPQ\" (spec §3.2).", nameof(version))
            : sid;
    }

    /// <summary>Whether a received line is SID-shaped (<c>[…-…]</c>) — the session-demux test (design decision 1).</summary>
    public static bool IsSidShaped(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var t = line.Trim();
        return t.Length >= 4 && t[0] == '[' && t[^1] == ']' && t.Contains('-', StringComparison.Ordinal);
    }

    /// <summary>Parses a SID line per spec §3.2.</summary>
    /// <exception cref="FbbProtocolException">The line is not a well-formed SID.</exception>
    public static Sid Parse(string line)
    {
        return TryParse(line, out var sid)
            ? sid
            : throw new FbbProtocolException($"Not a valid SID: \"{line}\"");
    }

    /// <summary>Attempts to parse a SID line per spec §3.2.</summary>
    public static bool TryParse(string line, out Sid sid)
    {
        ArgumentNullException.ThrowIfNull(line);
        sid = null!;
        var t = line.Trim();
        if (t.Length < 4 || t[0] != '[' || t[^1] != ']')
        {
            return false;
        }

        var inner = t[1..^1];
        var firstDash = inner.IndexOf('-', StringComparison.Ordinal);
        var lastDash = inner.LastIndexOf('-');
        if (firstDash < 0)
        {
            return false; // "at least two fields" [FBB-SID]
        }

        var author = inner[..firstDash];
        var features = inner[(lastDash + 1)..];
        var version = lastDash > firstDash ? inner[(firstDash + 1)..lastDash] : null;

        var compression = false;
        var b1 = false;
        var b2 = false;
        var blocked = false;
        var hier = false;
        var mid = false;
        var bid = false;
        for (var i = 0; i < features.Length; i++)
        {
            var c = char.ToUpperInvariant(features[i]);
            if (c == '$')
            {
                bid = true;
                continue;
            }

            if (!char.IsAsciiLetter(c))
            {
                continue; // tolerate unknown characters silently (spec §3.2)
            }

            // "optionally followed by a version digit; if no digit is given,
            // version 0 is assumed" [FBB-SID] — collect adjacent digits.
            var digitsStart = i + 1;
            var digitsEnd = digitsStart;
            while (digitsEnd < features.Length && char.IsAsciiDigit(features[digitsEnd]))
            {
                digitsEnd++;
            }

            switch (c)
            {
                case 'B':
                    compression = true;
                    for (var d = digitsStart; d < digitsEnd; d++)
                    {
                        if (features[d] == '1')
                        {
                            b1 = true;
                        }
                        else if (features[d] == '2')
                        {
                            b1 = true; // "B2 uses B1 mode" [BPQ-SRC]
                            b2 = true;
                        }
                    }

                    break;
                case 'F':
                    blocked = true;
                    break;
                case 'H':
                    hier = true;
                    break;
                case 'M':
                    mid = true;
                    break;
                default:
                    break; // A I J L W X and anything else: tolerated, inert (spec §3.2 table)
            }

            i = digitsEnd - 1;
        }

        sid = new Sid(t)
        {
            Author = author,
            Version = version,
            Features = features,
            SupportsCompression = compression,
            SupportsB1 = b1,
            SupportsB2 = b2,
            SupportsBlockedFbb = blocked,
            SupportsHierarchical = hier,
            SupportsMid = mid,
            SupportsBid = bid,
            MentionsBpq = t.Contains("BPQ", StringComparison.Ordinal),
        };
        return true;
    }
}
