using Bbs.Core;

namespace Bbs.Host.Forwarding;

/// <summary>One post-connect step of a resolved connect script (spec §4.4).</summary>
public abstract record ConnectScriptStep;

/// <summary>
/// An expect/send step (modelled on pdn-bpqchat's <c>ScriptStep</c>): wait until <see cref="Expect"/>
/// (or any of <see cref="ExpectAny"/>) is seen on the node stream, then send <see cref="Send"/>. This is
/// expect-then-send — not pacing — so each hop is confirmed (its node prompt seen) before the next
/// command is issued, which is what makes a multi-hop walk reliable when round-trip times vary. The
/// options carry the v2 per-step controls (see <c>docs/connect-script-v2.md</c>); the defaults reproduce
/// the historical behaviour (case-insensitive substring match, CR terminator, the partner's ConTimeout).
/// </summary>
/// <param name="Expect">Substring/pattern to wait for; empty (with no <see cref="ExpectAny"/>) = don't wait (send-only).</param>
/// <param name="Send">Line to send once the wait (if any) matches; empty = don't send (pure wait).</param>
public sealed record ExpectSendStep(string Expect, string Send) : ConnectScriptStep
{
    /// <summary>Alternatives to <see cref="Expect"/>; when non-empty, wait for whichever appears first.</summary>
    public IReadOnlyList<string>? ExpectAny { get; init; }

    /// <summary>Per-step wait timeout, overriding the partner ConTimeout for this step. Null = partner default.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Match mode: <c>substring</c> (default), <c>exact-line</c>, or <c>regex</c>.</summary>
    public string Match { get; init; } = "substring";

    /// <summary>Case-insensitive matching (default true).</summary>
    public bool IgnoreCase { get; init; } = true;

    /// <summary>Send terminator: <c>cr</c> (default), <c>lf</c>, <c>crlf</c>, or <c>none</c>.</summary>
    public string Eol { get; init; } = "cr";

    /// <summary>Expand C-style escapes (<c>\r \n \t \xNN \\</c>) in <see cref="Send"/> before transmitting.</summary>
    public bool Raw { get; init; }

    /// <summary>Optional label surfaced in the transcript and failure messages.</summary>
    public string? Name { get; init; }
}

/// <summary>The resolved dial plan for one forwarding cycle.</summary>
/// <param name="Target">The callsign/alias the RHP <c>open</c>(Active) dials, or <c>null</c> when the
/// partner has no open step at all (an empty script) — an INBOUND-ONLY partner that connects to
/// <em>us</em> (a sporadically-on-air station that polls for its mail); we never dial it. See
/// <see cref="IsInboundOnly"/>.</param>
/// <param name="Port">The node port for the open, when the open step named one; null = any.</param>
/// <param name="Steps">Post-connect steps, run by <see cref="ConnectScriptRunner"/> after the open succeeds.</param>
/// <param name="Warnings">Script entries we do not honour but probably should have (logged at Warning each
/// cycle, never silently dropped) — a real deferral the operator may want to act on.</param>
/// <param name="Notes">Recognised entries deliberately not honoured here because the function is owned by
/// another layer. Named — never silently dropped — but informational, not a warning.</param>
public sealed record ConnectPlan(
    string? Target,
    string? Port,
    IReadOnlyList<ConnectScriptStep> Steps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// True when there is no open step (an empty script): the partner dials US and polls for its mail —
    /// we never dial it. The scheduler starts no outbound loop for such a partner.
    /// </summary>
    public bool IsInboundOnly => Target is null;
}

/// <summary>
/// Resolves a partner's structured connect script (<see cref="ConnectStep"/>[]) into the runtime
/// <see cref="ConnectPlan"/> the <see cref="ConnectScriptRunner"/> executes. The store holds the
/// structured form directly (v2; the flat <c>EXPECT=SEND</c> string form is retired — see
/// <c>docs/connect-script-v2.md</c>), so resolution is a straight mapping: the OPEN step (the one with
/// <see cref="ConnectStep.Open"/> set, which must be first) names the dial; every later step is an
/// <see cref="ExpectSendStep"/>. No legacy parsing, no BPQ directives — those were handled when scripts
/// were authored in the flat form, which no longer exists.
/// </summary>
public static class ConnectScript
{
    private static readonly string[] MatchModes = ["substring", "exact-line", "regex"];
    private static readonly string[] EolModes = ["cr", "lf", "crlf", "none"];

    /// <summary>Resolves a partner's structured script. An empty script (no open step) is INBOUND-ONLY:
    /// <see cref="ConnectPlan.Target"/> is null and the partner is never dialled.</summary>
    public static ConnectPlan Resolve(Partner partner)
    {
        ArgumentNullException.ThrowIfNull(partner);

        string? target = null;
        string? port = null;
        var steps = new List<ConnectScriptStep>();
        var warnings = new List<string>();
        var notes = new List<string>();

        for (int i = 0; i < partner.ConnectScript.Count; i++)
        {
            ConnectStep step = partner.ConnectScript[i];

            if (step.Open is { Length: > 0 } open)
            {
                if (target is not null)
                {
                    warnings.Add($"connect step {i + 1}: a second dial (\"{open}\") is ignored — a script has one open");
                    continue;
                }

                if (steps.Count > 0)
                {
                    warnings.Add($"connect step {i + 1}: the dial (\"{open}\") must be the first step; ignored");
                    continue;
                }

                if (step.Expect is { Length: > 0 } || step.Send is { Length: > 0 } || step.ExpectAny is { Count: > 0 })
                {
                    warnings.Add($"connect step {i + 1}: the open step also carries expect/send, which is ignored — split it into a separate step");
                }

                target = open;
                string? openPort = string.IsNullOrWhiteSpace(step.Port) ? null : step.Port.Trim();
                if (openPort is not null && !openPort.All(char.IsAsciiDigit))
                {
                    warnings.Add($"connect step {i + 1}: port \"{openPort}\" is not numeric — ignored");
                    openPort = null;
                }

                port = openPort;
                continue;
            }

            steps.Add(ToExpectSend(step, i, warnings));
        }

        return new ConnectPlan(target, port, steps, warnings, notes);
    }

    private static ExpectSendStep ToExpectSend(ConnectStep step, int index, List<string> warnings)
    {
        string match = Normalize(step.Match, "substring");
        if (!MatchModes.Contains(match, StringComparer.Ordinal))
        {
            warnings.Add($"connect step {index + 1}: unknown match mode \"{step.Match}\" — using substring");
            match = "substring";
        }

        string eol = Normalize(step.Eol, "cr");
        if (!EolModes.Contains(eol, StringComparer.Ordinal))
        {
            warnings.Add($"connect step {index + 1}: unknown line-ending \"{step.Eol}\" — using cr");
            eol = "cr";
        }

        return new ExpectSendStep(step.Expect ?? string.Empty, step.Send ?? string.Empty)
        {
            ExpectAny = step.ExpectAny is { Count: > 0 } any ? [.. any] : null,
            Timeout = step.TimeoutSeconds is int s and > 0 ? TimeSpan.FromSeconds(s) : null,
            Match = match,
            IgnoreCase = step.IgnoreCase ?? true,
            Eol = eol,
            Raw = step.Raw ?? false,
            Name = string.IsNullOrWhiteSpace(step.Name) ? null : step.Name.Trim(),
        };
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
}
