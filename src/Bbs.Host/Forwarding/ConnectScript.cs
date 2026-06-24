using Bbs.Core;

namespace Bbs.Host.Forwarding;

/// <summary>One post-connect step of a resolved connect script (spec §4.4).</summary>
public abstract record ConnectScriptStep;

/// <summary>
/// An expect/send step (modelled on pdn-bpqchat's <c>ScriptStep</c>): wait until
/// <see cref="Expect"/> is seen on the node stream (a case-insensitive substring; empty =
/// don't wait), then send <see cref="Send"/> as a CR-terminated line (empty = don't send).
/// This is expect-then-send — not pacing — so each hop is confirmed (its node prompt seen)
/// before the next command is issued, which is what makes a multi-hop walk reliable when
/// round-trip times vary. A bare script line (no <c>=</c>) parses to <c>Expect=""</c>, which
/// preserves the legacy verbatim-send behaviour ("Script lines are sent verbatim to the node
/// … the software knows what to look for", spec §4.4).
/// </summary>
public sealed record ExpectSendStep(string Expect, string Send) : ConnectScriptStep;

/// <summary>The resolved dial plan for one forwarding cycle.</summary>
/// <param name="Target">The callsign/alias the RHP <c>open</c>(Active) dials, or <c>null</c> when the
/// partner has no connect step at all (an empty script) — an INBOUND-ONLY partner that connects to
/// <em>us</em> (a sporadically-on-air station that polls for its mail); we never dial it. See
/// <see cref="IsInboundOnly"/>.</param>
/// <param name="Port">The node port for the open, when the <c>C</c> line named one (<c>C &lt;port&gt; &lt;call&gt;</c>); null = any.</param>
/// <param name="Steps">Post-connect steps, run by <see cref="ConnectScriptRunner"/> after the open succeeds.</param>
/// <param name="Warnings">Script lines we do not honour but probably should have (logged at Warning each
/// cycle, never silently dropped) — a real deferral the operator may want to act on.</param>
/// <param name="Notes">Recognised lines we deliberately do not honour <em>here</em> because the function
/// is owned by another layer (e.g. <c>INTERLOCK</c>, whose per-port serialization belongs to the
/// node/port layer, not the connect script), or are superseded (e.g. <c>PAUSE</c>, replaced by the
/// runner's expect-then-send prompt gating). Named — never silently dropped — but informational, not a
/// warning: logged at Debug per cycle, surfaced on demand by the sysop test-connect tool.</param>
public sealed record ConnectPlan(
    string? Target,
    string? Port,
    IReadOnlyList<ConnectScriptStep> Steps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// True when there is no connect step (an empty script): the partner dials US and polls for its
    /// mail (reverse-forwarded during its inbound session) — we never dial it. The scheduler starts no
    /// outbound loop for such a partner; the sysop test-connect tool has nothing to dial.
    /// </summary>
    public bool IsInboundOnly => Target is null;
}

/// <summary>
/// Connect-script interpretation (compat spec §4.4) with an expect/send step model ported
/// from pdn-bpqchat. The node-level dialogue starts with the RHP <c>open</c> itself, so:
///
/// <list type="bullet">
/// <item>The FIRST step being <c>C [port] &lt;target&gt;</c> names the open (port optional,
/// digits — extra tokens such as digipeaters are warned, no via support over RHP). A script with
/// NO <c>C</c> line at all is INBOUND-ONLY (<see cref="ConnectPlan.IsInboundOnly"/>): the partner
/// dials us and polls for its mail; we never dial it. (BPQ node-command dialects — the <c>NC</c>
/// verb, the <c>!</c> direct flag — are normalised to plain <c>C</c> at IMPORT by the migrator, not
/// tolerated here; pdn's <c>C</c> already negotiates.)</item>
/// <item>Every line AFTER the connect step is an <see cref="ExpectSendStep"/>. The
/// <c>EXPECT=SEND</c> form (split on the FIRST <c>=</c>, whitespace trimmed around each)
/// waits for EXPECT then sends SEND — e.g. <c>GB7RDG&gt;=BBS</c> waits for the node prompt
/// then sends <c>BBS</c>. A line with NO <c>=</c> is send-only (Expect empty): it goes out
/// verbatim with an inter-line response wait, exactly the legacy behaviour (e.g. <c>C GB7RDG</c>
/// is a node-level connect typed at the remote prompt, <c>BBS</c> enters the BBS app).</item>
/// <item><c>PAUSE n</c> is recognised but <em>superseded</em>, not honoured: it was BPQ's crude
/// fixed-delay stand-in for "wait until the remote is ready", which the runner now does properly by
/// gating every step on the node's actual prompt (expect-then-send). It is recorded as a note and
/// kept off the wire — no timed pacing is reintroduced. The remaining §4.4 directives (TIMES,
/// ELSE, MSGTYPE, SKIPPROMPT, SKIPCON, TEXTFORWARDING, SETCALLTOSENDER,
/// ATTACH, RADIO, FILE, IMPORT, RMS, SendWL2KFW) are recognised so they are never sent to
/// a node, and warned as unsupported — named deviations, not silent drops.</item>
/// <item><c>INTERLOCK n</c> is recognised as <em>superseded</em>, not unsupported: its job —
/// one forwarding session at a time on a shared radio — is owned by the node/port layer (per-port
/// TX serialization, e.g. <c>kiss.ackMode</c>), and an app-level forwarding interlock is a planned
/// fast-follow. It is kept off the wire and recorded as a <see cref="ConnectPlan.Notes">note</see>
/// (Debug, not a Warning) so the imported GB7-* scripts that carry it don't log a benign warning
/// every cycle. The interlock <em>number</em> is not honoured (BPQ's group id does not map to a
/// pdn RHP port slot).</item>
/// </list>
/// </summary>
public static class ConnectScript
{
    /// <summary>
    /// Directives BPQ interprets locally (spec §4.4) that we recognise but do not honour:
    /// recognising them keeps them off the wire; warning keeps the deferral named.
    /// </summary>
    private static readonly string[] UnsupportedDirectives =
    [
        "TIMES", "ELSE", "MSGTYPE", "SKIPPROMPT", "SKIPCON", "TEXTFORWARDING",
        "SETCALLTOSENDER", "ATTACH", "RADIO", "FILE", "IMPORT", "RMS", "SENDWL2KFW",
    ];

    /// <summary>
    /// Directives we recognise as <em>superseded</em> rather than unsupported: the function exists,
    /// but is owned by another layer, so honouring the directive here would be redundant. Recorded as
    /// an informational <see cref="ConnectPlan.Notes">note</see> (never silently dropped), not a warning.
    /// Currently just <c>INTERLOCK</c> — per-port forwarding serialization is the node/port layer's job.
    /// </summary>
    private static readonly string[] SupersededDirectives = ["INTERLOCK"];

    /// <summary>Resolves a partner's script. An empty script (no <c>C</c> line) is INBOUND-ONLY:
    /// <see cref="ConnectPlan.Target"/> is null and the partner is never dialled.</summary>
    public static ConnectPlan Resolve(Partner partner)
    {
        ArgumentNullException.ThrowIfNull(partner);

        string? target = null;
        string? port = null;
        var steps = new List<ConnectScriptStep>();
        var warnings = new List<string>();
        var notes = new List<string>();
        bool sawStep = false;

        foreach (string raw in partner.ConnectScript)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string verb = tokens[0].ToUpperInvariant();

            if (verb == "PAUSE")
            {
                // Superseded, not honoured: PAUSE was BPQ's fixed-delay stand-in for "wait until the
                // remote settles" before the next line. The runner now gates every step on the node's
                // actual prompt (expect-then-send), so a timed pause is redundant — recognised, named
                // as a note, and dropped from the wire rather than reintroducing time-based pacing.
                notes.Add(
                    $"connect-script directive \"{line}\" recognised but superseded — readiness is now " +
                    "gated by waiting for the node prompt (expect-then-send), not a fixed delay");
                continue;
            }

            if (SupersededDirectives.Contains(verb, StringComparer.Ordinal))
            {
                notes.Add(
                    $"connect-script directive \"{line}\" recognised but not honoured here — per-port " +
                    "forwarding serialization is owned by the node/port layer (e.g. kiss.ackMode), not the " +
                    "connect script; an app-level forwarding interlock is a planned fast-follow");
                continue;
            }

            if (UnsupportedDirectives.Contains(verb, StringComparer.Ordinal))
            {
                warnings.Add($"unsupported connect-script directive \"{line}\" ignored (spec §4.4 deferral)");
                continue;
            }

            if (verb == "C" && tokens.Length >= 2 && target is null && !sawStep && !line.Contains('='))
            {
                // The connect step (only valid first — an RHP open is the one node-level
                // connect our bearer model can make). A "C …=…" line is NOT an open; it's an
                // expect/send step whose SEND happens to be a connect command. BPQ node-command
                // dialects (NC, the "!" direct flag) are normalised to plain "C" at IMPORT time
                // (the migrator), not tolerated here — pdn's "C" already negotiates.
                (port, target) = ParseConnectTokens(tokens, line, warnings);
                continue;
            }

            steps.Add(ParseStep(line));
            sawStep = true;
        }

        // No C line at all → Target null → INBOUND-ONLY (the partner dials us; we never dial it).
        return new ConnectPlan(target, port, steps, warnings, notes);
    }

    /// <summary>
    /// Parses one post-connect line into an <see cref="ExpectSendStep"/>. Split on the FIRST
    /// <c>=</c>: the left is EXPECT (case-insensitive substring to wait for), the right is SEND
    /// (the line to send). A line with no <c>=</c> is send-only (Expect empty) — the legacy
    /// verbatim form. Whitespace around EXPECT and SEND is trimmed.
    /// </summary>
    private static ExpectSendStep ParseStep(string line)
    {
        int eq = line.IndexOf('=');
        if (eq < 0)
        {
            return new ExpectSendStep(string.Empty, line);
        }

        string expect = line[..eq].Trim();
        string send = line[(eq + 1)..].Trim();
        return new ExpectSendStep(expect, send);
    }

    private static (string? Port, string Target) ParseConnectTokens(
        string[] tokens, string line, List<string> warnings)
    {
        string? port = null;
        int targetIndex = 1;
        if (tokens.Length >= 3 && tokens[1].All(char.IsAsciiDigit))
        {
            // BPQ's C <port> <call> shape — the port rides the RHP open.
            port = tokens[1];
            targetIndex = 2;
        }

        if (tokens.Length > targetIndex + 1)
        {
            warnings.Add(
                $"connect-script line \"{line}\": tokens after the target are ignored " +
                "(digipeater paths are not supported over an RHP open)");
        }

        return (port, tokens[targetIndex]);
    }
}
