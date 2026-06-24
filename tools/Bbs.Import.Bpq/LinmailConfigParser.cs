using System.Globalization;

namespace Bbs.Import.Bpq;

/// <summary>
/// Parses BPQMail's <c>linmail.cfg</c> (libconfig text format) into a <see cref="BpqMailConfig"/>.
///
/// <para>
/// This is a deliberately small, tolerant parser for the subset BPQMail actually emits: nested
/// <c>name : { ... };</c> groups, and <c>key = value;</c> assignments where the value is a quoted
/// string, an integer (optionally with a trailing <c>L</c> for libconfig int64), a float, or a
/// boolean. Comment styles handled: <c>//</c> to end-of-line, <c>#</c> to end-of-line, and the
/// non-standard <c>;</c>-prefixed comment lines that appear in BPQ seed files. Unknown keys are
/// ignored. Callsign group keys that start with a digit are stored by BPQ with a leading <c>*</c>;
/// that prefix is stripped.
/// </para>
///
/// <para>
/// Field provenance: the BBSUsers caret('^')-delimited field order is verified against
/// <c>GetUserDatabase</c> (BBSUtilities.c:687–740). The ConnectScript/TOCalls/ATCalls/HRoutes
/// multi-value fields are pipe('|')-joined on disk (compat spec §4.1).
/// </para>
/// </summary>
internal static class LinmailConfigParser
{
    /// <summary>Reads and parses a linmail.cfg file from disk.</summary>
    public static BpqMailConfig Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses linmail.cfg text. Exposed for unit testing.</summary>
    public static BpqMailConfig Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ConfigGroup root = LibConfig.Parse(text);

        ConfigGroup main = root.Group("main") ?? new ConfigGroup();
        ConfigGroup housekeeping = root.Group("Housekeeping") ?? new ConfigGroup();
        ConfigGroup forwarding = root.Group("BBSForwarding") ?? new ConfigGroup();
        ConfigGroup users = root.Group("BBSUsers") ?? new ConfigGroup();

        return new BpqMailConfig
        {
            BbsName = main.String("BBSName") ?? string.Empty,
            SysopCall = main.String("SYSOPCall") ?? string.Empty,
            HRoute = main.String("H-Route") ?? string.Empty,
            BidLifetime = housekeeping.Int("BidLifetime") ?? 0,
            MaxMsgno = housekeeping.Int("MaxMsgno") ?? 0,
            MaxAge = housekeeping.Int("MaxAge") ?? 0,
            Partners = ParsePartners(forwarding),
            Users = ParseUsers(users),
        };
    }

    private static List<BpqPartner> ParsePartners(ConfigGroup forwarding)
    {
        var result = new List<BpqPartner>();
        foreach ((string name, ConfigGroup sub) in forwarding.SubGroups)
        {
            result.Add(new BpqPartner
            {
                Call = StripStar(name),
                ConnectScript = SplitPipe(sub.String("ConnectScript")),
                ToCalls = SplitPipe(sub.String("TOCalls")),
                AtCalls = SplitPipe(sub.String("ATCalls")),
                HRoutes = SplitPipe(sub.String("HRoutes")),
                HRoutesP = SplitPipe(sub.String("HRoutesP")),
                BbsHa = NullIfEmpty(sub.String("BBSHA")),
                Enabled = (sub.Int("Enabled") ?? 0) != 0,
                FwdInterval = sub.Int("FwdInterval") ?? 3600,
                FwdNewImmediately = (sub.Int("FWDNewImmediately") ?? 0) != 0,
                UseB2 = (sub.Int("UseB2Protocol") ?? 0) != 0,
                ConTimeout = sub.Int("ConTimeout") ?? 60,
            });
        }

        return result;
    }

    private static List<BpqUser> ParseUsers(ConfigGroup users)
    {
        var result = new List<BpqUser>();
        foreach ((string name, string value) in users.Scalars())
        {
            // Caret-delimited: keep empty trailing fields, so split with no removal.
            string[] f = value.Split('^');

            result.Add(new BpqUser
            {
                Call = StripStar(name),
                Name = Field(f, 0),
                HomeBbs = Field(f, 2),
                Qra = Field(f, 3),
                Zip = Field(f, 5),
                LastListed = FieldLong(f, 7),
                Flags = FieldInt(f, 8),
                BbsNumber = FieldInt(f, 10),
                TimeLastConnected = FieldLong(f, 13),
            });
        }

        return result;
    }

    private static string Field(string[] f, int i) => i < f.Length ? f[i].Trim() : string.Empty;

    private static int FieldInt(string[] f, int i)
        => int.TryParse(Field(f, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static long FieldLong(string[] f, int i)
        => long.TryParse(Field(f, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;

    private static string[] SplitPipe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        return value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string StripStar(string name) => name.StartsWith('*') ? name[1..] : name;
}
