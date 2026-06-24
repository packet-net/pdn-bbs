using System.Text;
using Bbs.Core;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>The outcome of one sysop "test connect" probe (the <c>/forwarding/test-connect</c> JSON shape).</summary>
/// <param name="Partner">The partner callsign that was probed.</param>
/// <param name="Target">The callsign/alias the RHP open dialled (from the resolved <see cref="ConnectPlan"/>).</param>
/// <param name="Port">The node port the open pinned, or null (any).</param>
/// <param name="Ok">True iff we opened, navigated the script, and saw the peer SID/prompt — without any failure text.</param>
/// <param name="Sid">The peer SID or prompt line actually observed (e.g. <c>[BPQ-6.0.25.30-B12FWIHJM$]</c> or <c>de GB7RDG&gt;</c>); null if none was seen.</param>
/// <param name="Transcript">The dialogue: each line we sent (<c>&gt; …</c>) and each line/match we saw (<c>&lt; …</c>).</param>
/// <param name="Error">The failure reason when <see cref="Ok"/> is false (node failure text, an expect that never arrived, a timeout, or a refused open); null on success.</param>
/// <param name="Steps">The resolved post-connect steps rendered for display (<c>EXPECT=SEND</c>, a bare send, or <c>PAUSE n</c>).</param>
/// <param name="Warnings">The plan's resolution warnings (e.g. an unsupported directive) — surfaced so the operator sees what was deferred.</param>
/// <param name="Notes">The plan's informational notes (recognised-but-superseded directives, e.g. <c>INTERLOCK</c>) — surfaced on demand here even though they don't warn in the cycle log.</param>
public sealed record ConnectTestResult(
    string Partner,
    string Target,
    string? Port,
    bool Ok,
    string? Sid,
    IReadOnlyList<string> Transcript,
    string? Error,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes);

/// <summary>
/// The sysop "test connect" tool: VALIDATE a partner connection — reachability AND the real peer
/// prompt — WITHOUT forwarding any mail. It reuses the exact dial path the
/// <see cref="ForwardingScheduler"/> cycle uses (resolve the <see cref="ConnectScript"/>, open the
/// RHP child to the target, run <see cref="ConnectScriptRunner"/> to the SID/prompt) and then
/// CLOSES — it runs NO <c>FbbSessionRunner.RunCallerAsync</c>, builds NO outbound batch, and never
/// touches the forward queue, so it is structurally incapable of moving mail or mutating state. The
/// observed prompt is what lets the operator then tighten the script's <c>EXPECT=</c> strings to the
/// partner's actual prompt before un-holding forwarding at cutover.
/// </summary>
public sealed class ForwardingTester
{
    private readonly RhpNodeLink _link;
    private readonly BbsStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ForwardingTester(RhpNodeLink link, BbsStore store, TimeProvider time, ILogger<ForwardingTester> logger)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _link = link;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Probes the partner named <paramref name="partnerCall"/> (resolved read-only from the store):
    /// resolve the connect script → open → run it to the SID/prompt → close. Bounded by the partner's
    /// ConTimeout (the same wait the live cycle uses). Never throws for an unreachable/failing partner —
    /// that is reported as <c>Ok=false</c> with the reason; it throws only on cancellation. No mail is
    /// moved (no FBB session, no queue mutation). Throws if the partner does not exist (the caller
    /// validates existence first, so this is a guard).
    /// </summary>
    public async Task<ConnectTestResult> TestConnectAsync(string partnerCall, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partnerCall);
        Partner partner = _store.GetPartner(partnerCall)
            ?? throw new InvalidOperationException($"No such forwarding partner \"{partnerCall}\".");

        ConnectPlan plan = ConnectScript.Resolve(partner);
        IReadOnlyList<string> steps = plan.Steps.Select(RenderStep).ToList();
        var transcript = new List<string>();
        TimeSpan wait = TimeSpan.FromSeconds(Math.Max(1, partner.ConTimeoutSeconds));

        RhpChildConnection child;
        try
        {
            child = await _link.OpenAsync(plan.Target, plan.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A refused/failed open (link down, node rejected the connect): reachability failed
            // before any dialogue. Report it, don't throw — this is a diagnostic tool.
            return new ConnectTestResult(
                partner.Call, plan.Target, plan.Port, Ok: false, Sid: null,
                transcript, ex.Message, steps, plan.Warnings, plan.Notes);
        }

        try
        {
            // Run the connect script (capturing the dialogue), then resolve the peer SID/prompt: for a
            // plan WITH steps the runner already waits for it and re-presents it in the returned bytes;
            // for a STEPLESS plan (a bare open dialling the BBS call directly) the runner returns
            // nothing, so we do the SID/prompt wait ourselves. Either way: NO FBB session is run.
            byte[] initial = await ConnectScriptRunner
                .RunAsync(child, plan, wait, _time, _logger, transcript, cancellationToken)
                .ConfigureAwait(false);

            string? sid = plan.Steps.Count == 0
                ? await ConnectScriptRunner
                    .WaitForPeerSidAsync(child, wait, _time, _logger, transcript, cancellationToken)
                    .ConfigureAwait(false)
                : FirstLine(initial);

            return new ConnectTestResult(
                partner.Call, plan.Target, plan.Port, Ok: true, sid,
                transcript, Error: null, steps, plan.Warnings, plan.Notes);
        }
        catch (ConnectScriptException ex)
        {
            // Node failure text, an expect that never arrived, or a timeout — the partner is
            // reachable-but-unusable per the script. The transcript captured so far rides along.
            return new ConnectTestResult(
                partner.Call, plan.Target, plan.Port, Ok: false, Sid: null,
                transcript, ex.Message, steps, plan.Warnings, plan.Notes);
        }
        finally
        {
            // Close the child WITHOUT ever having run an FBB caller session — this tool cannot move mail.
            await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The peer SID/prompt the step runner re-presented at the head of its returned bytes (CR-terminated).</summary>
    private static string? FirstLine(byte[] initial)
    {
        if (initial.Length == 0)
        {
            return null;
        }

        string text = Encoding.Latin1.GetString(initial);
        int cr = text.IndexOfAny(['\r', '\n']);
        string first = (cr < 0 ? text : text[..cr]).Trim();
        return first.Length == 0 ? null : first;
    }

    /// <summary>Renders one resolved step for the JSON <c>plan.steps</c> display.</summary>
    private static string RenderStep(ConnectScriptStep step) => step switch
    {
        PauseStep p => $"PAUSE {p.Delay.TotalSeconds:0}",
        ExpectSendStep { Expect.Length: > 0 } es => $"{es.Expect}={es.Send}",
        ExpectSendStep es => es.Send,
        _ => step.ToString() ?? "",
    };
}
