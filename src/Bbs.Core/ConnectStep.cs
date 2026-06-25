namespace Bbs.Core;

/// <summary>
/// One structured step of a connect script (compat spec §4.4). This is the v2 store model that
/// replaced the flat <c>EXPECT=SEND</c> string form: a step is either the OPEN (the one RHP dial we
/// make — <see cref="Open"/> set) or an EXPECT/SEND step (every later hop, typed verbatim at a remote
/// node prompt). Because expect and send are separate fields, a step can carry arbitrary bytes — a
/// node prompt like <c>"=&gt; "</c> (equals, greater-than, space, trailing space significant) that the
/// flat form could not represent (the <c>=</c> was the field delimiter and the trailing space was
/// trimmed) is just a quoted scalar here. See <c>docs/connect-script-v2.md</c>.
/// </summary>
/// <remarks>
/// A single flat record (nullable fields) rather than a polymorphic hierarchy, so it binds cleanly to
/// YAML maps and JSON (the persisted form) without a discriminator, and the web step-editor has one
/// card shape. Null means "not set / use the default" for every field; the runner applies the defaults
/// (substring match, case-insensitive, CR terminator, the partner's ConTimeout) so an absent field
/// behaves exactly as the legacy form did.
/// </remarks>
public sealed record ConnectStep
{
    /// <summary>
    /// The RHP open target (callsign/alias) for the dial. Set on the OPEN step ONLY, which must be the
    /// first step; null on every expect/send step. A script with no <see cref="Open"/> step at all is
    /// INBOUND-ONLY (the partner dials us; we never dial it).
    /// </summary>
    public string? Open { get; init; }

    /// <summary>
    /// Optional node port for the open (the <c>&lt;port&gt;</c> in BPQ's <c>C &lt;port&gt; &lt;call&gt;</c>),
    /// digits only. Null = any. Only meaningful together with <see cref="Open"/>.
    /// </summary>
    public string? Port { get; init; }

    /// <summary>
    /// Substring (or, per <see cref="Match"/>, exact-line / regex pattern) to wait for on the node
    /// stream before sending. Null or empty = don't wait (send-only step — the legacy bare line).
    /// Carries arbitrary bytes verbatim: leading/trailing spaces are significant and preserved.
    /// </summary>
    public string? Expect { get; init; }

    /// <summary>
    /// Alternatives to <see cref="Expect"/>: wait for whichever appears first (e.g. a node prompt OR a
    /// "try later" banner). When set and non-empty it takes precedence over <see cref="Expect"/>. Each
    /// alternative is matched per <see cref="Match"/> / <see cref="IgnoreCase"/>.
    /// </summary>
    public List<string>? ExpectAny { get; init; }

    /// <summary>
    /// The line to send once the expect (if any) matches. Null or empty = don't send (a pure wait).
    /// Terminated per <see cref="Eol"/> (default CR). When <see cref="Raw"/> is set, C-style escapes
    /// (<c>\r \n \t \xNN \\</c>) in this string are expanded to bytes before sending.
    /// </summary>
    public string? Send { get; init; }

    /// <summary>Per-step wait timeout in seconds, overriding the partner's ConTimeout for this step. Null = use the partner default.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Match mode for <see cref="Expect"/>/<see cref="ExpectAny"/>: <c>substring</c> (default), <c>exact-line</c>, or <c>regex</c>. Null = substring.</summary>
    public string? Match { get; init; }

    /// <summary>Case-insensitive matching (the historical default). Null = true; set false for a case-sensitive match.</summary>
    public bool? IgnoreCase { get; init; }

    /// <summary>Line terminator for <see cref="Send"/>: <c>cr</c> (default), <c>lf</c>, <c>crlf</c>, or <c>none</c>. Null = cr.</summary>
    public string? Eol { get; init; }

    /// <summary>Expand C-style escapes (<c>\r \n \t \xNN \\</c>) in <see cref="Send"/> before transmitting (e.g. Ctrl-Z = <c>\x1a</c>). Null = false.</summary>
    public bool? Raw { get; init; }

    /// <summary>Optional label for this step, surfaced in the attempt transcript and failure messages so a failed cycle names the step.</summary>
    public string? Name { get; init; }
}
