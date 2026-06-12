namespace Bbs.Console;

/// <summary>
/// Static configuration for the console engine. Kept minimal per design.md; everything a
/// sysop edits at runtime lives elsewhere (the store, <see cref="IUserSettingsStore"/>).
/// </summary>
public sealed record BbsConsoleConfig
{
    /// <summary>
    /// The BBS callsign/name shown in the <c>de CALL&gt;</c> prompt (compat spec §1.2), the
    /// sign-off line and the V output.
    /// </summary>
    public required string BbsCallsign { get; init; }

    /// <summary>
    /// Callsigns with sysop rights (compat spec §1.4; here it gates LH/LK, reading/killing
    /// held messages — §2.2, and the plain sysop diagnostics forwarding/queue/route). Matched
    /// on the base call, SSID-insensitively.
    /// </summary>
    public IReadOnlyList<string> SysopCallsigns { get; init; } = [];

    /// <summary>
    /// Our H-Route <b>without</b> the callsign (e.g. <c>#23.GBR.EURO</c>), or null. Used only by
    /// the plain sysop <c>route</c> diagnostic to build a <see cref="Bbs.Core.RoutingEngine"/> that
    /// explains where a hypothetical message would go — the same engine + config the Host's live
    /// routing uses (<c>HostComposition</c> passes <c>config.HRoute</c>). Null = no hierarchy
    /// configured (the engine then treats our bare callsign as the leaf).
    /// </summary>
    public string? HRoute { get; init; }

    /// <summary>
    /// Default page length for users who have not set one with OP (compat spec §1.7).
    /// 0 disables paging.
    /// </summary>
    public int DefaultPageLength { get; init; }

    /// <summary>Default expert-mode state for users who have not toggled X (compat spec §1.3).</summary>
    public bool ExpertDefault { get; init; }

    /// <summary>
    /// The command surface a user sees when they have never chosen one (the plain-language
    /// mandate, design.md). Defaults to <see cref="InterfaceMode.Plain"/> — plain is the
    /// default; classic is opt-in. A sysop can pin a deployment to classic by setting this.
    /// </summary>
    public InterfaceMode DefaultInterfaceMode { get; init; } = InterfaceMode.Plain;

    /// <summary>BBS version string for the V command (compat spec §1.3 "BBS Version %s").</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Node version string for the V command's second line (compat spec §1.3
    /// "Node Version %s"), or null to omit that line.
    /// </summary>
    public string? NodeVersion { get; init; }

    /// <summary>
    /// Replacement help text for ?/H (compat spec §1.3 "If help.txt exists it replaces the
    /// built-in text"), or null for the built-in summary. CR-terminated lines.
    /// </summary>
    public string? HelpText { get; init; }

    /// <summary>
    /// The sysop info text for the I command, or null for
    /// "SYSOP has not created an INFO file" (compat spec §1.3). CR-terminated lines.
    /// </summary>
    public string? InfoText { get; init; }
}
