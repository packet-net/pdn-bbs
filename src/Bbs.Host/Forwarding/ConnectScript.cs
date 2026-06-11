using Bbs.Core;

namespace Bbs.Host.Forwarding;

/// <summary>The resolved dial plan for one forwarding cycle.</summary>
/// <param name="Target">The callsign/alias the RHP <c>open</c>(Active) dials.</param>
/// <param name="Warnings">Script lines v1 does not honour (logged each cycle).</param>
public sealed record ConnectPlan(string Target, IReadOnlyList<string> Warnings);

/// <summary>
/// Connect-script interpretation, v1 (compat spec §4.4): full BPQ scripts are "verbatim
/// lines sent and answered" through the node; with RHP the node-level dialogue is the
/// <c>open</c> itself, so only <c>C &lt;target&gt;</c> lines are meaningful — "a bare
/// C &lt;call&gt; line means the open target itself". The LAST <c>C</c> line names the
/// open remote; everything else (preceding C hops, TIMES/ELSE/PAUSE/ATTACH directives)
/// is unsupported and warned about, never silently dropped.
/// </summary>
public static class ConnectScript
{
    /// <summary>Resolves a partner's script. An empty script dials the partner callsign itself.</summary>
    public static ConnectPlan Resolve(Partner partner)
    {
        ArgumentNullException.ThrowIfNull(partner);

        string? target = null;
        var warnings = new List<string>();
        foreach (string raw in partner.ConnectScript)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if ((line.Length > 2 && (line[0] is 'C' or 'c') && line[1] == ' ') && line[2..].Trim().Length > 0)
            {
                if (target is not null)
                {
                    warnings.Add($"connect-script line \"C {target}\" superseded — v1 dials only the last C <target>");
                }

                target = line[2..].Trim();
            }
            else
            {
                warnings.Add($"unsupported connect-script line \"{line}\" ignored — v1 supports only C <target>");
            }
        }

        return new ConnectPlan(target ?? partner.Call, warnings);
    }
}
