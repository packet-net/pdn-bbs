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

/// <summary>The <c>PAUSE n</c> directive (spec §4.4): wait before the next step.</summary>
public sealed record PauseStep(TimeSpan Delay) : ConnectScriptStep;

/// <summary>The resolved dial plan for one forwarding cycle.</summary>
/// <param name="Target">The callsign/alias the RHP <c>open</c>(Active) dials.</param>
/// <param name="Port">The node port for the open, when the <c>C</c> line named one (<c>C &lt;port&gt; &lt;call&gt;</c>); null = any.</param>
/// <param name="Steps">Post-connect steps, run by <see cref="ConnectScriptRunner"/> after the open succeeds.</param>
/// <param name="Warnings">Script lines we do not honour (logged each cycle, never silently dropped).</param>
public sealed record ConnectPlan(
    string Target,
    string? Port,
    IReadOnlyList<ConnectScriptStep> Steps,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Connect-script interpretation (compat spec §4.4) with an expect/send step model ported
/// from pdn-bpqchat. The node-level dialogue starts with the RHP <c>open</c> itself, so:
///
/// <list type="bullet">
/// <item>The FIRST step being <c>C [port] &lt;target&gt;</c> names the open (port optional,
/// digits — extra tokens such as digipeaters are warned, no via support over RHP). No
/// leading <c>C</c> → the open dials the partner callsign itself.</item>
/// <item>Every line AFTER the connect step is an <see cref="ExpectSendStep"/>. The
/// <c>EXPECT=SEND</c> form (split on the FIRST <c>=</c>, whitespace trimmed around each)
/// waits for EXPECT then sends SEND — e.g. <c>GB7RDG&gt;=BBS</c> waits for the node prompt
/// then sends <c>BBS</c>. A line with NO <c>=</c> is send-only (Expect empty): it goes out
/// verbatim with an inter-line response wait, exactly the legacy behaviour (e.g. <c>C GB7RDG</c>
/// is a node-level connect typed at the remote prompt, <c>BBS</c> enters the BBS app).</item>
/// <item><c>PAUSE n</c> is honoured as a delay. The remaining §4.4 directives (TIMES,
/// ELSE, MSGTYPE, INTERLOCK, SKIPPROMPT, SKIPCON, TEXTFORWARDING, SETCALLTOSENDER,
/// ATTACH, RADIO, FILE, IMPORT, RMS, SendWL2KFW) are recognised so they are never sent to
/// a node, and warned as unsupported — named deviations, not silent drops.</item>
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
        "TIMES", "ELSE", "MSGTYPE", "INTERLOCK", "SKIPPROMPT", "SKIPCON", "TEXTFORWARDING",
        "SETCALLTOSENDER", "ATTACH", "RADIO", "FILE", "IMPORT", "RMS", "SENDWL2KFW",
    ];

    /// <summary>Resolves a partner's script. An empty script dials the partner callsign with no steps.</summary>
    public static ConnectPlan Resolve(Partner partner)
    {
        ArgumentNullException.ThrowIfNull(partner);

        string? target = null;
        string? port = null;
        var steps = new List<ConnectScriptStep>();
        var warnings = new List<string>();
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
                if (tokens.Length == 2 && int.TryParse(tokens[1], out int seconds) && seconds > 0)
                {
                    steps.Add(new PauseStep(TimeSpan.FromSeconds(seconds)));
                }
                else
                {
                    warnings.Add($"malformed PAUSE line \"{line}\" ignored — expected PAUSE <seconds>");
                }

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
                // expect/send step whose SEND happens to be a connect command.
                (port, target) = ParseConnectTokens(tokens, line, warnings);
                continue;
            }

            steps.Add(ParseStep(line));
            sawStep = true;
        }

        return new ConnectPlan(target ?? partner.Call, port, steps, warnings);
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
