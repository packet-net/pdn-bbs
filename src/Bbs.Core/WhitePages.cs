using System.Globalization;

namespace Bbs.Core;

/// <summary>
/// The provenance/confidence of a White Pages record — NOT a station kind. Mirrors the
/// FBB/BPQ <c>/TYPE</c> token (BPQ <c>WPRoutines.c</c> <c>ProcessWPMsg</c>): how the home-BBS
/// information was learned, which drives the merge rule (a <see cref="User"/> record is
/// authoritative and overwrites unconditionally; the others only freshen by date).
/// </summary>
public enum WhitePagesType
{
    /// <summary>I — derived from an R: forwarding line (a routing-trace guess).</summary>
    RLine,

    /// <summary>G — guessed from a message header. The default when <c>/TYPE</c> is absent or unrecognised.</summary>
    Guessed,

    /// <summary>U — user-supplied, AUTHORITATIVE. Overwrites stored data regardless of freshness.</summary>
    User,
}

/// <summary>Wire-letter conversions for <see cref="WhitePagesType"/>.</summary>
public static class WhitePagesTypeExtensions
{
    /// <summary>The single-letter wire code (I/G/U).</summary>
    public static char ToCode(this WhitePagesType type) => type switch
    {
        WhitePagesType.RLine => 'I',
        WhitePagesType.Guessed => 'G',
        WhitePagesType.User => 'U',
        _ => 'G',
    };

    /// <summary>
    /// Parses the single-letter wire code (case-insensitive). An absent or UNRECOGNISED code maps
    /// to <see cref="WhitePagesType.Guessed"/> — BPQ's default — rather than rejecting the record.
    /// </summary>
    public static WhitePagesType FromCode(char code) => char.ToUpperInvariant(code) switch
    {
        'I' => WhitePagesType.RLine,
        'U' => WhitePagesType.User,
        _ => WhitePagesType.Guessed, // 'G' and anything unknown
    };
}

/// <summary>
/// One parsed White Pages (network directory) record: who a station is and where it is homed.
/// The wire form is one <c>On …</c> line in a WP-update message body; see
/// <see cref="WhitePagesParser"/> for the exact format and field semantics. <c>?</c> sentinels and
/// over-long optional fields are normalised to null at parse time, so a non-null
/// <see cref="HomeBbs"/>/<see cref="Name"/>/<see cref="Qth"/>/<see cref="Zip"/> is always real data.
/// </summary>
/// <param name="Callsign">Base callsign (SSID-stripped, upper-cased), 3–6 chars. The directory key.</param>
/// <param name="Type">Provenance/confidence (drives the merge rule).</param>
/// <param name="RecordDate">The <c>On YYMMDD</c> date — the freshness key (date-wins upsert).</param>
/// <param name="HomeBbs">Hierarchical home-BBS address (e.g. <c>AA1AA.#42.GBR.EURO</c>), or null.</param>
/// <param name="Name">Operator name, or null.</param>
/// <param name="Qth">Location (may contain spaces), or null.</param>
/// <param name="Zip">Postcode/locator, or null.</param>
public sealed record WhitePagesRecord(
    string Callsign,
    WhitePagesType Type,
    DateOnly RecordDate,
    string? HomeBbs,
    string? Name,
    string? Qth,
    string? Zip);

/// <summary>
/// One stored White Pages directory entry as read back from the store. Differs from
/// <see cref="WhitePagesRecord"/> (the parsed-from-wire form) by carrying the ingest metadata
/// (<see cref="LastSeen"/>, <see cref="Source"/>) the merge/aging logic maintains.
/// </summary>
/// <param name="Callsign">Base callsign (the directory key).</param>
/// <param name="Type">Provenance/confidence of the stored row.</param>
/// <param name="HomeBbs">Hierarchical home-BBS address, or null.</param>
/// <param name="Name">Operator name, or null.</param>
/// <param name="Qth">Location, or null.</param>
/// <param name="Zip">Postcode/locator, or null.</param>
/// <param name="RecordDate">The freshness key (the date-wins upsert compares against this).</param>
/// <param name="LastSeen">When an entry for this call was last ingested.</param>
/// <param name="Source">How the entry was learned: <c>wp</c> (a WP message) or <c>rline</c> (future R:-line harvest).</param>
public sealed record WhitePagesEntry(
    string Callsign,
    WhitePagesType Type,
    string? HomeBbs,
    string? Name,
    string? Qth,
    string? Zip,
    DateOnly RecordDate,
    DateTimeOffset LastSeen,
    string Source);

/// <summary>
/// Parser + recognition discriminator for FBB/BPQ White Pages ("WP", the network directory)
/// update messages (issue #36).
///
/// <para><b>Wire format.</b> A WP update is an ordinary FBB/B2F message whose BODY is one record
/// per line. The source of truth is BPQ's own emitter/parser (<c>m0lte/linbpq</c>
/// <c>WPRoutines.c</c> — the exact peer on the GB7RDG forwarding link) and FBB's
/// <c>docwp.htm</c>: each record line is</para>
/// <code>On &lt;YYMMDD&gt; &lt;CALL&gt;/&lt;TYPE&gt; @ &lt;HOME-HA&gt; zip &lt;ZIP&gt; &lt;NAME&gt; &lt;QTH...&gt;</code>
/// <para>with single-space separators. The literal <c>On </c> (3 bytes) is the line sentinel — only
/// lines beginning with it are records; anything else (the message's own R: lines, blank lines) is
/// skipped silently, not an error. Note the literal <c>zip</c> KEYWORD token sits between the
/// home-HA and the actual zip value (BPQ's emitter writes the literal word <c>zip</c>; its parser
/// reads it into a throwaway token, then the NEXT token is the real value). <c>?</c> is the explicit
/// unknown sentinel for ZIP/NAME/QTH ⇒ null. QTH takes the rest of the line. TYPE ∈ I/G/U.</para>
///
/// <para><b>Graceful degradation.</b> A malformed or short record line is dropped and parsing
/// continues with the remaining lines — we never abort the whole body on one bad line (BPQ
/// <c>return</c>s on a malformed line, truncating the rest; that is a bug we deliberately do not
/// replicate). Length caps (HA≤40, ZIP≤8, NAME≤12, QTH≤30), the call 3–6 + callsign-shape check, a
/// per-line 128-byte cap, and a parseable date are all enforced; a failing line is dropped, not
/// stored. An over-cap optional field is treated as unknown (null), not truncated.</para>
///
/// <para>This is line-oriented text: the B1/B2 container has already decompressed it by the time we
/// see the body — there is no compression or binary framing inside a WP body.</para>
/// </summary>
public static class WhitePagesParser
{
    /// <summary>The literal 3-byte line sentinel that marks a WP record.</summary>
    public const string RecordSentinel = "On ";

    /// <summary>The reserved 2-char pseudo-callsign that addresses a WP update (case-insensitive).</summary>
    public const string ReservedCallsign = "WP";

    /// <summary>Maximum length of a record line (BPQ rejects longer; we mirror that as a per-line guard).</summary>
    private const int MaxLineLength = 128;

    private const int MinCallLength = 3;
    private const int MaxCallLength = 6;
    private const int MaxHomeBbsLength = 40;
    private const int MaxZipLength = 8;
    private const int MaxNameLength = 12;
    private const int MaxQthLength = 30;

    /// <summary>
    /// Whether a delivered message's PRIMARY recipient addresses the network directory: its base
    /// callsign (SSID-stripped, case-insensitive) equals the reserved pseudo-call <c>WP</c>. Matches
    /// bare <c>WP</c>, <c>WP@GB7RDG</c>, <c>WP-1</c>, … — the AT/<c>@&lt;bbs&gt;</c> part is routing,
    /// not identity. This is BPQ's exact discriminator (<c>BBSUtilities.c</c>
    /// <c>strcmp(Msg-&gt;to,"WP")==0</c>). <c>WP</c> is a 2-char reserved pseudo-call that fails the
    /// 3–6-char callsign rule, so no human can be homed as <c>WP</c> and this cannot eat real mail.
    /// </summary>
    /// <remarks>
    /// This is only HALF the recognition rule — the belt-and-braces guard is that the body must also
    /// contain at least one parseable <c>On …</c> record (<see cref="Parse"/> returning ≥1 record).
    /// A genuine human message to a station literally called "WP" has no records and falls through to
    /// normal mail storage. Recognition keys on the RECIPIENT only, never the subject.
    /// </remarks>
    public static bool IsDirectoryRecipient(string? recipientCall)
    {
        if (string.IsNullOrWhiteSpace(recipientCall))
        {
            return false;
        }

        // The recipient may arrive as a bare TO ("WP") or with the routing AT attached ("WP@GB7RDG").
        // The AT/@<bbs> part is routing, not identity — strip it before the base-call compare.
        string call = recipientCall.Trim();
        int at = call.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            call = call[..at];
        }

        return string.Equals(
            Callsigns.StripSsid(Callsigns.Normalize(call)),
            ReservedCallsign,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a WP-update message body into directory records. Lines not beginning with the
    /// <c>On </c> sentinel are skipped silently; a malformed/short/over-cap record line is dropped and
    /// parsing continues. Tolerant of CRLF/CR/LF line endings. Returns one record per VALID line, in
    /// body order (a later duplicate-callsign line is returned too — date-wins resolution is the
    /// store's job, not the parser's).
    /// </summary>
    public static IReadOnlyList<WhitePagesRecord> Parse(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }

        var records = new List<WhitePagesRecord>();
        foreach (string rawLine in body.ReplaceLineEndings("\n").Split('\n'))
        {
            // Trim only the trailing CR the BPQ wire leaves; leading whitespace would break the
            // sentinel compare exactly as it does in BPQ (`memcmp(ptr,"On ",3)`).
            string line = rawLine.TrimEnd('\r', '\n');
            if (line.Length == 0 || line.Length > MaxLineLength)
            {
                continue;
            }

            if (!line.StartsWith(RecordSentinel, StringComparison.Ordinal))
            {
                continue; // not a record line — an R: line, blank line, free text, etc.
            }

            WhitePagesRecord? record = ParseLine(line);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    /// <summary>
    /// Parses one <c>On …</c> record line, or returns null if the line is too short to hold the
    /// required tokens, has an unparseable date, a bad callsign, or an over-cap mandatory field.
    /// </summary>
    private static WhitePagesRecord? ParseLine(string line)
    {
        // Tokenise on whitespace, but keep QTH as the remainder of the line. The fixed leading
        // tokens are: On  YYMMDD  CALL/TYPE  @  HA  zip  ZIP  NAME  QTH...
        //             [0] [1]     [2]        [3] [4] [5]  [6]  [7]   [8..]
        // The first SEVEN (through ZIP) are required; NAME and QTH are optional (a record may end at
        // the zip value). BPQ's emitter always writes all fields (using ? for unknowns), so the common
        // case is 8+ tokens; accepting a trailing-fields-absent record is graceful degradation.
        string[] tokens = line.Split(' ', 9, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 7)
        {
            return null; // not enough tokens for the required Date/Call/AT/HA/zip/ZIP
        }

        // tokens[0] == "On" (the sentinel; already matched).
        if (!TryParseDate(tokens[1], out DateOnly recordDate))
        {
            return null;
        }

        // CALL/TYPE — split at '/'. An absent /TYPE defaults to G.
        (string callPart, char typeCode) = SplitCallType(tokens[2]);
        string call = Callsigns.StripSsid(Callsigns.Normalize(callPart));
        if (!IsValidWpCall(call))
        {
            return null;
        }

        if (tokens[3] != "@")
        {
            return null; // the parser requires the literal '@' marker
        }

        string? homeBbs = NormalizeMandatory(tokens[4], MaxHomeBbsLength);
        if (homeBbs is null)
        {
            return null; // HA over the cap ⇒ skip the line (BPQ skips an HA > 40)
        }

        // tokens[5] is the literal `zip` keyword (a throwaway). tokens[6] is the REAL zip value.
        // NAME (token 7) and QTH (token 8, rest-of-line) are optional — absent ⇒ null.
        string? zip = NormalizeOptional(tokens[6], MaxZipLength);
        string? name = tokens.Length >= 8 ? NormalizeOptional(tokens[7], MaxNameLength) : null;
        string? qth = tokens.Length >= 9 ? NormalizeOptional(tokens[8], MaxQthLength) : null;

        return new WhitePagesRecord(
            call,
            WhitePagesTypeExtensions.FromCode(typeCode),
            recordDate,
            homeBbs,
            name,
            qth,
            zip);
    }

    /// <summary>Splits a <c>CALL/TYPE</c> token; a missing <c>/TYPE</c> defaults to <c>G</c>.</summary>
    private static (string Call, char Type) SplitCallType(string token)
    {
        int slash = token.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return (token, 'G');
        }

        string call = token[..slash];
        string rest = token[(slash + 1)..];
        return (call, rest.Length > 0 ? rest[0] : 'G');
    }

    /// <summary>
    /// Parses the <c>YYMMDD</c> record date. 2-digit year, <c>+2000</c> (matching BPQ's
    /// <c>+100</c>-into-a-tm-year, i.e. 20xx). Returns false on any non-numeric / out-of-range value.
    /// </summary>
    private static bool TryParseDate(string token, out DateOnly date)
    {
        date = default;
        if (token.Length != 6)
        {
            return false;
        }

        if (!int.TryParse(token.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int yy)
            || !int.TryParse(token.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int mm)
            || !int.TryParse(token.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int dd))
        {
            return false;
        }

        if (mm is < 1 or > 12)
        {
            return false;
        }

        int year = 2000 + yy;
        if (dd < 1 || dd > DateTime.DaysInMonth(year, mm))
        {
            return false;
        }

        date = new DateOnly(year, mm, dd);
        return true;
    }

    /// <summary>Whether a base callsign is a valid WP record key: 3–6 chars and callsign-shaped.</summary>
    private static bool IsValidWpCall(string call) =>
        call.Length is >= MinCallLength and <= MaxCallLength
        && !call.Contains(':', StringComparison.Ordinal)
        && Callsigns.IsCallsignShaped(call);

    /// <summary>A mandatory token: trimmed; null when empty or over the cap (caller skips the line).</summary>
    private static string? NormalizeMandatory(string token, int maxLength)
    {
        string trimmed = token.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// An optional token: the <c>?</c> unknown sentinel, an empty value, or one over the cap all map
    /// to null (don't store junk, and never overwrite a known stored value with <c>?</c>).
    /// </summary>
    private static string? NormalizeOptional(string token, int maxLength)
    {
        string trimmed = token.Trim();
        if (trimmed.Length == 0 || trimmed == "?" || trimmed.Length > maxLength)
        {
            return null;
        }

        return trimmed;
    }
}
