using Bbs.Core;

namespace Bbs.Host.Forwarding;

/// <summary>One post-connect step of a resolved connect script (spec §4.4).</summary>
public abstract record ConnectScriptStep;

/// <summary>
/// Send <see cref="Line"/> verbatim once connected, then (unless it is the final step)
/// wait for the node's response before the next step — "Script lines are sent verbatim to
/// the node … the software knows what to look for" (spec §4.4).
/// </summary>
public sealed record SendLineStep(string Line) : ConnectScriptStep;

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
/// Connect-script interpretation (compat spec §4.4). BPQ sends every non-directive script
/// line verbatim to the node it has ATTACHed; in our bearer model the node-level dialogue
/// starts with the RHP <c>open</c> itself, so:
///
/// <list type="bullet">
/// <item>The FIRST step being <c>C [port] &lt;target&gt;</c> names the open (port optional,
/// digits — extra tokens such as digipeaters are warned, no via support over RHP). No
/// leading <c>C</c> → the open dials the partner callsign itself.</item>
/// <item>Every line AFTER the connect step is a post-connect step: sent verbatim with an
/// inter-line response wait (a SECOND <c>C</c> is therefore a node-level command sent at
/// the remote node's prompt — exactly what BPQ's verbatim model does).</item>
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

            if (verb == "C" && tokens.Length >= 2)
            {
                if (target is null && !sawStep)
                {
                    // The connect step (only valid first — an RHP open is the one
                    // node-level connect our bearer model can make).
                    (port, target) = ParseConnectTokens(tokens, line, warnings);
                    continue;
                }

                // A later C is a node-level command at the connected node's prompt —
                // sent verbatim like any other post-connect line.
            }

            steps.Add(new SendLineStep(line));
            sawStep = true;
        }

        return new ConnectPlan(target ?? partner.Call, port, steps, warnings);
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
