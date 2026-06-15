using Bbs.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bbs.Host.Web;

/// <summary>
/// Serialises store <see cref="Partner"/> records to — and parses them back from — the
/// <c>partners:</c> YAML block exactly as it appears in <c>bbs.yaml</c> (the
/// <see cref="PartnerConfig"/> shape). The forwarding editor's YAML tab round-trips through this:
/// the store is the source of truth, so the textarea is the store rendered to YAML and Save parses
/// the YAML and applies it back to the store. Both naming conventions and unknown-key tolerance
/// match <see cref="BbsHostConfigFile.Parse"/>, so YAML written here parses identically at startup.
/// </summary>
internal static class PartnerYaml
{
    /// <summary>The deserializer — camelCase keys, unknown keys ignored — IDENTICAL to <see cref="BbsHostConfigFile.Parse"/>.</summary>
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// The serializer — camelCase keys, only NULLs omitted (<see cref="DefaultValuesHandling.OmitNull"/>).
    /// We deliberately do NOT omit value-type defaults: several <see cref="PartnerConfig"/> properties
    /// default to a NON-<c>default(T)</c> value (<c>enabled</c>/<c>sendImmediately</c> true,
    /// <c>intervalMinutes</c>/<c>conTimeoutSeconds</c> 60, <c>maxRx</c>/<c>maxTx</c> 99999), so omitting
    /// defaults would drop e.g. <c>enabled: false</c> (it equals <c>default(bool)</c>) and the omitted key
    /// would re-materialise to <c>true</c> on parse — breaking the required serialise→parse→serialise
    /// stability. Emitting every value field keeps the round trip exact and the YAML self-documenting;
    /// OmitNull just drops the noise (the unused simple <c>connect:</c> and a null <c>bbsHa:</c>).
    /// </summary>
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Renders the store's partners as the <c>partners:</c> YAML block (the bbs.yaml shape). An empty
    /// store yields <c>partners: []</c>. The <see cref="PartnerConfig"/> projection mirrors
    /// <see cref="PartnerConfig.ToPartner"/> in reverse — the store collapses <c>connect</c> into a
    /// one-line <c>connectScript</c> and copies <c>hr</c> onto both HRoutes/HRoutesP, so we always emit
    /// <c>connectScript</c> (never <c>connect</c>) and read <c>hr</c> back off HRoutes.
    /// </summary>
    public static string Serialize(IReadOnlyList<Partner> partners)
    {
        ArgumentNullException.ThrowIfNull(partners);
        var doc = new PartnersDocument { Partners = [.. partners.Select(ToConfig)] };
        // SerializerBuilder always emits the key; an empty list serialises as "partners: []".
        return Serializer.Serialize(doc).TrimEnd('\n');
    }

    /// <summary>
    /// Parses a <c>partners:</c> YAML block back to store <see cref="Partner"/> records. Accepts
    /// either a document with a top-level <c>partners:</c> key (what <see cref="Serialize"/> emits) or
    /// a bare YAML sequence of partner maps. Throws <see cref="PartnerYamlException"/> with a
    /// human-readable message on malformed YAML or an invalid/empty partner call — the caller shows it
    /// and leaves the store untouched.
    /// </summary>
    public static IReadOnlyList<Partner> Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        List<PartnerConfig> configs;
        try
        {
            // The whole-document form first (partners: [...]); fall back to a bare sequence so a
            // sysop can paste just the list. A blank document is "no partners".
            string trimmed = yaml.Trim();
            if (trimmed.Length == 0)
            {
                return [];
            }

            PartnersDocument? doc = Deserializer.Deserialize<PartnersDocument>(yaml);
            configs = doc?.Partners ?? [];

            // A document that has no `partners:` key but IS a bare sequence binds to an empty
            // PartnersDocument above; try the bare-sequence shape in that case.
            if (configs.Count == 0 && LooksLikeSequence(trimmed))
            {
                configs = Deserializer.Deserialize<List<PartnerConfig>>(yaml) ?? [];
            }
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new PartnerYamlException(FriendlyYamlError(ex), ex);
        }

        var partners = new List<Partner>(configs.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PartnerConfig config in configs)
        {
            string call = Callsigns.Normalize(config.Call ?? "");
            if (call.Length == 0)
            {
                throw new PartnerYamlException("Every partner needs a 'call'. One entry has an empty or missing call.");
            }

            if (!Callsigns.IsCallsignShaped(call))
            {
                throw new PartnerYamlException($"'{call}' doesn't look like a callsign.");
            }

            if (!seen.Add(call))
            {
                throw new PartnerYamlException($"'{call}' appears more than once — partner calls must be unique.");
            }

            // Reuse the same config→store mapping the startup path uses, so the YAML tab and bbs.yaml
            // produce byte-identical store records (and the same defaults).
            partners.Add(config.ToPartner());
        }

        return partners;
    }

    /// <summary>True when the trimmed text looks like a bare YAML sequence (its first non-empty line starts with "-").</summary>
    private static bool LooksLikeSequence(string trimmed) => trimmed.StartsWith('-');

    /// <summary>A one-line, human-friendly version of a YamlDotNet parse error (the raw message is verbose + multi-line).</summary>
    private static string FriendlyYamlError(YamlDotNet.Core.YamlException ex)
    {
        string detail = ex.InnerException?.Message ?? ex.Message;
        // Collapse to a single line so it sits cleanly in the error banner.
        detail = detail.ReplaceLineEndings(" ").Trim();
        return ex.Start.Line > 0
            ? $"Couldn't parse the YAML (around line {ex.Start.Line}): {detail}"
            : $"Couldn't parse the YAML: {detail}";
    }

    /// <summary>
    /// The reverse of <see cref="PartnerConfig.ToPartner"/>: store record → config shape for emission.
    /// Always emits the full <c>connectScript</c> (the store has no separate simple <c>connect</c>);
    /// reads <c>hr</c> from HRoutes (ToPartner copies the one list onto both HRoutes/HRoutesP, so they
    /// are equal for anything created via config — HRoutes is the canonical source). <c>intervalMinutes</c>
    /// is the seconds value back to whole minutes (matching <c>IntervalMinutes * 60</c> on the way in).
    /// </summary>
    private static PartnerConfig ToConfig(Partner p) => new()
    {
        Call = p.Call,
        ConnectScript = [.. p.ConnectScript],
        ConTimeoutSeconds = p.ConTimeoutSeconds,
        IntervalMinutes = Math.Max(1, p.ForwardIntervalSeconds / 60),
        SendImmediately = p.ForwardNewImmediately,
        Collect = p.Collect,
        To = [.. p.ToCalls],
        At = [.. p.AtCalls],
        Hr = [.. p.HRoutes],
        BbsHa = p.BbsHa,
        MaxRx = p.MaxRxSize,
        MaxTx = p.MaxTxSize,
        Enabled = p.Enabled,
        AllowB2 = p.AllowB2F,
    };

    /// <summary>The document wrapper so YamlDotNet binds the top-level <c>partners:</c> key.</summary>
    private sealed class PartnersDocument
    {
        public List<PartnerConfig> Partners { get; set; } = [];
    }
}

/// <summary>
/// A user-facing parse/validation failure from the forwarding editor's YAML tab. Its
/// <see cref="Exception.Message"/> is shown verbatim in the error banner; the store is left untouched.
/// </summary>
public sealed class PartnerYamlException : Exception
{
    /// <summary>Creates the exception with a user-facing message.</summary>
    public PartnerYamlException(string message) : base(message) { }

    /// <summary>Creates the exception with a user-facing message and the underlying cause.</summary>
    public PartnerYamlException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Required by analyzers; not used (we always supply a message).</summary>
    public PartnerYamlException() { }
}
