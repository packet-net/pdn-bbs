using System.Text.RegularExpressions;

namespace Bbs.Core;

/// <summary>
/// Callsign normalisation helpers shared by the message model, store and routing.
/// Compat spec §1.5: "TO/FROM truncated to 6 chars, SSID stripped ('Remove any (illegal) ssid')";
/// §2.4: "TO ≤ 6 chars, callsign-shaped, SSID stripped".
/// </summary>
public static partial class Callsigns
{
    /// <summary>Maximum length of a TO/FROM callsign field (compat spec §1.5 / §2.4).</summary>
    public const int MaxAddresseeLength = 6;

    /// <summary>Trims and upper-cases a callsign. Does not strip the SSID.</summary>
    public static string Normalize(string call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Trim().ToUpperInvariant();
    }

    /// <summary>Returns the base callsign with any -SSID suffix removed.</summary>
    public static string StripSsid(string call)
    {
        ArgumentNullException.ThrowIfNull(call);
        int dash = call.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? call : call[..dash];
    }

    /// <summary>
    /// Normalises a TO/FROM addressee per compat spec §1.5: upper-case, SSID stripped,
    /// truncated to <see cref="MaxAddresseeLength"/> characters.
    /// </summary>
    public static string NormalizeAddressee(string call)
    {
        string normalized = StripSsid(Normalize(call));
        return normalized.Length <= MaxAddresseeLength ? normalized : normalized[..MaxAddresseeLength];
    }

    /// <summary>
    /// Case-insensitive equality of the base (SSID-stripped) callsigns. Used by the routing
    /// loop guards, where R: chain entries carry the bare BBS call while partner identities
    /// may carry an SSID (compat spec §2.5, §3.14).
    /// </summary>
    public static bool BaseEquals(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return string.Equals(StripSsid(a.Trim()), StripSsid(b.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="call"/> has the conservative shape of an amateur callsign — used to tell a
    /// personal recipient from a bulletin category when classifying an SMTP/console addressee. After
    /// <see cref="Normalize"/> + <see cref="StripSsid"/> it must match
    /// <c>^[A-Z0-9]{1,2}[0-9][A-Z]{1,4}$</c>: an optional 1–2 char prefix, the call's digit, then 1–4
    /// letters (covers M0LTE, G0ABC, 2E0XYZ, VK2ABC). Bulletin categories (ALL, NEWS, SALE, DX, WANTED)
    /// have no digit in that position, so they are NOT callsign-shaped and read as a category instead.
    /// This is a shape test, not a registry lookup — it never confirms a callsign is licensed/issued.
    /// </summary>
    public static bool IsCallsignShaped(string call)
    {
        ArgumentNullException.ThrowIfNull(call);
        string normalized = StripSsid(Normalize(call));
        return normalized.Length > 0 && CallsignShapeRegex().IsMatch(normalized);
    }

    /// <summary>
    /// Derives the BBS's on-air callsign from the node's own callsign (the supervisor's
    /// <c>PDN_NODE_CALLSIGN</c>): the base of <paramref name="nodeCallsign"/> — upper-cased,
    /// any SSID stripped — joined to <paramref name="ssid"/> as <c>&lt;base&gt;-&lt;ssid&gt;</c>.
    /// Matches the sibling-app convention (DAPPS, bpqchat, convers all do
    /// <c>&lt;node-base&gt;-&lt;ssid&gt;</c>). Returns null when <paramref name="nodeCallsign"/>
    /// is null/blank (a standalone deployment with no node) — the caller then keeps its
    /// configured/placeholder callsign.
    /// </summary>
    public static string? DeriveFromNode(string? nodeCallsign, int ssid)
    {
        if (string.IsNullOrWhiteSpace(nodeCallsign))
        {
            return null;
        }

        string baseCall = StripSsid(Normalize(nodeCallsign));
        return baseCall.Length == 0 ? null : $"{baseCall}-{ssid}";
    }

    /// <summary>
    /// The full SSID-probe order for a callsign derived from the node (the free-SSID walk the RHP
    /// link runs when a listen is refused with errCode 9 "Duplicate socket"). Walks SSIDs starting
    /// at the derivation's own SSID and wrapping (start … 15, then 1 … start−1), each as
    /// <c>&lt;base&gt;-&lt;ssid&gt;</c>. BOTH skip rules apply to EVERY candidate including the first:
    /// SSID 0 (the node's bare callsign) is never produced, and the SSID the node itself uses (parsed
    /// off <paramref name="nodeCallsign"/>) is skipped — so even when the preferred SSID collides with
    /// the node's own (e.g. the node runs at -1 and the BBS default is also 1) the FIRST candidate is
    /// already a free, non-node SSID, never the node's own on-air identity. Matches the DAPPS probe
    /// order so the sibling apps tile the SSID space the same way.
    /// </summary>
    public static IReadOnlyList<string> SsidProbeCandidates(string derivedCallsign, string? nodeCallsign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(derivedCallsign);

        int dash = derivedCallsign.LastIndexOf('-');
        string baseCall = dash > 0 ? derivedCallsign[..dash] : derivedCallsign;
        int start = dash > 0 && int.TryParse(
            derivedCallsign[(dash + 1)..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int s) ? s : 0;

        // The walk lives in 1..15, so anchor an out-of-range/zero preferred SSID at 1.
        if (start is < 1 or > 15)
        {
            start = 1;
        }

        int nodeSsid = ParseSsid(nodeCallsign);

        var candidates = new List<string>(15);
        for (int offset = 0; offset < 15; offset++)
        {
            int ssid = ((start - 1 + offset) % 15) + 1; // start, start+1 … 15, then 1 … start−1; never 0
            if (ssid == nodeSsid)
            {
                continue; // skip the node's own SSID — including as the very first candidate
            }

            candidates.Add($"{baseCall}-{ssid}");
        }

        return candidates;
    }

    /// <summary>The SSID parsed off a callsign's <c>-NN</c> suffix, or 0 when there is none.</summary>
    private static int ParseSsid(string? call)
    {
        if (string.IsNullOrWhiteSpace(call))
        {
            return 0;
        }

        int dash = call.LastIndexOf('-');
        return dash > 0 && int.TryParse(
            call[(dash + 1)..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int ssid)
            ? ssid
            : 0;
    }

    [GeneratedRegex("^[A-Z0-9]{1,2}[0-9][A-Z]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex CallsignShapeRegex();
}
