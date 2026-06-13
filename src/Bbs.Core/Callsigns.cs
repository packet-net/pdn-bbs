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

    [GeneratedRegex("^[A-Z0-9]{1,2}[0-9][A-Z]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex CallsignShapeRegex();
}
