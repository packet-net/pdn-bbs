using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bbs.Core;

/// <summary>
/// Persists a structured connect script (<see cref="ConnectStep"/>[]) as JSON for the <c>connect_script</c>
/// store column, and reads it back. The flat <c>EXPECT=SEND</c> form is retired and understood nowhere:
/// a legacy newline-joined blob (or any non-JSON value) reads as a BLANK script, so an upgraded node
/// comes up with the partner inbound-only until the sysop authors a structured script (see
/// <c>docs/connect-script-v2.md</c>). The column stays a plain TEXT field; an empty script is the empty
/// string (matching the column's <c>DEFAULT ''</c>).
/// </summary>
public static class ConnectScriptJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialises steps to a compact JSON array (null/omitted fields dropped). An empty script serialises to the empty string.</summary>
    public static string Serialize(IReadOnlyList<ConnectStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.Count == 0 ? string.Empty : JsonSerializer.Serialize(steps, Options);
    }

    /// <summary>
    /// Reads steps from the stored column value. A JSON array is the structured form; an empty value OR
    /// any non-JSON value (a retired legacy flat blob) reads as a blank script — the partner is then
    /// inbound-only until a structured script is authored.
    /// </summary>
    public static IReadOnlyList<ConnectStep> Deserialize(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || !stored.TrimStart().StartsWith('['))
        {
            return [];
        }

        // A malformed (or wrong-shape) JSON array degrades to blank exactly like a non-JSON value —
        // a corrupt connect_script row must never crash a partner read (which runs at startup via
        // ListPartners, and on every inbound-partner lookup), only make that partner inbound-only.
        try
        {
            return JsonSerializer.Deserialize<List<ConnectStep>>(stored, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// STRICT parse of an edited/posted JSON array (the web step editor's hidden field): a null/empty or
    /// non-array value is a blank script, but a MALFORMED JSON array throws <see cref="JsonException"/> so
    /// the form can reject the edit with a notice rather than silently saving a blank script. (Contrast
    /// <see cref="Deserialize"/>, which is deliberately tolerant for the durable store-read path.)
    /// </summary>
    public static IReadOnlyList<ConnectStep> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('['))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<ConnectStep>>(json, Options) ?? [];
    }
}
