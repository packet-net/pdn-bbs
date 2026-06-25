using System.Text;
using System.Text.Json;
using Bbs.Core;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>The outcome of one sysop "test connect" probe (the <c>/forwarding/test-connect</c> JSON shape).</summary>
/// <param name="Partner">The partner callsign that was probed.</param>
/// <param name="Target">The callsign/alias the RHP open dialled (from the resolved <see cref="ConnectPlan"/>), or null for an inbound-only partner (empty script — nothing was dialled).</param>
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
    string? Target,
    string? Port,
    bool Ok,
    string? Sid,
    IReadOnlyList<string> Transcript,
    string? Error,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes);

/// <summary>One event of the live "test to here" probe stream (the SSE payload): a transcript <c>line</c>,
/// a live received <c>chunk</c>, a prefix <c>error</c>, or the <c>end</c> of the hold window.</summary>
public sealed record ProbeEvent(string Type, string Text);

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
    public async Task<ConnectTestResult> TestConnectAsync(string partnerCall, IReadOnlyList<ConnectStep>? draftSteps, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partnerCall);
        Partner? stored = _store.GetPartner(partnerCall);
        if (draftSteps is null && stored is null)
        {
            throw new InvalidOperationException($"No such forwarding partner \"{partnerCall}\".");
        }

        // When the editor passes its UNSAVED draft, validate THAT — so "Test connect" tests the script you
        // see in the editor, not the last-saved one (a blank saved script would otherwise read inbound-only).
        // Otherwise validate the stored partner's script. An unsaved partner borrows the default ConTimeout.
        Partner partner = draftSteps is null
            ? stored!
            : (stored ?? new Partner { Call = partnerCall }) with { ConnectScript = [.. draftSteps] };

        ConnectPlan plan = ConnectScript.Resolve(partner);
        IReadOnlyList<string> steps = plan.Steps.Select(RenderStep).ToList();
        var transcript = new List<string>();
        TimeSpan wait = TimeSpan.FromSeconds(Math.Max(1, partner.ConTimeoutSeconds));

        if (plan.Target is null)
        {
            // Inbound-only (empty connect script): this partner dials US and polls for its mail — there
            // is nothing to dial. Not a fault: it should still be ENABLED so we reverse-forward to it on
            // its inbound poll. Report that, having contacted no one (no open, no mail moved).
            return new ConnectTestResult(
                partner.Call, Target: null, Port: null, Ok: true, Sid: null,
                ["inbound-only — this partner connects to us; there is nothing to dial. " +
                 "Enable it so its queued mail forwards when it next polls."],
                Error: null, steps, plan.Warnings, plan.Notes);
        }

        string target = plan.Target;
        RhpChildConnection child;
        try
        {
            child = await _link.OpenAsync(target, plan.Port, cancellationToken).ConfigureAwait(false);
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
                partner.Call, target, plan.Port, Ok: false, Sid: null,
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
                partner.Call, target, plan.Port, Ok: true, sid,
                transcript, Error: null, steps, plan.Warnings, plan.Notes);
        }
        catch (ConnectScriptException ex)
        {
            // Node failure text, an expect that never arrived, or a timeout — the partner is
            // reachable-but-unusable per the script. The transcript captured so far rides along.
            return new ConnectTestResult(
                partner.Call, target, plan.Port, Ok: false, Sid: null,
                transcript, ex.Message, steps, plan.Warnings, plan.Notes);
        }
        finally
        {
            // Close the child WITHOUT ever having run an FBB caller session — this tool cannot move mail.
            await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static readonly JsonSerializerOptions ResultJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The streaming whole-script "Test connect": same validation as <see cref="TestConnectAsync"/> (the
    /// UNSAVED draft when provided, else the stored script) but emits each transcript line LIVE via
    /// <paramref name="emit"/> as the dialogue happens, then a final <c>result</c> event whose text is the
    /// JSON verdict (<c>ok</c>/<c>target</c>/<c>port</c>/<c>sid</c>/<c>error</c>). Moves no mail; the child
    /// is always closed.
    /// </summary>
    public async Task TestConnectStreamAsync(
        string partnerCall,
        IReadOnlyList<ConnectStep>? draftSteps,
        Func<ProbeEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partnerCall);
        ArgumentNullException.ThrowIfNull(emit);

        Partner? stored = _store.GetPartner(partnerCall);
        if (draftSteps is null && stored is null)
        {
            await emit(new ProbeEvent("result", ResultText(false, null, null, null, $"No such forwarding partner \"{partnerCall}\"."))).ConfigureAwait(false);
            return;
        }

        Partner partner = draftSteps is null
            ? stored!
            : (stored ?? new Partner { Call = partnerCall }) with { ConnectScript = [.. draftSteps] };
        ConnectPlan plan = ConnectScript.Resolve(partner);
        TimeSpan wait = TimeSpan.FromSeconds(Math.Max(1, partner.ConTimeoutSeconds));

        foreach (string warning in plan.Warnings)
        {
            await emit(new ProbeEvent("line", "! " + warning)).ConfigureAwait(false);
        }

        if (plan.Target is null)
        {
            await emit(new ProbeEvent("line", "inbound-only — this partner connects to us; there is nothing to dial. Enable it so its queued mail forwards when it next polls.")).ConfigureAwait(false);
            await emit(new ProbeEvent("result", ResultText(true, null, null, null, null))).ConfigureAwait(false);
            return;
        }

        RhpChildConnection child;
        try
        {
            child = await _link.OpenAsync(plan.Target, plan.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await emit(new ProbeEvent("result", ResultText(false, plan.Target, plan.Port, null, ex.Message))).ConfigureAwait(false);
            return;
        }

        try
        {
            (bool ok, string? sid, string? error) = await ConnectScriptRunner
                .RunStreamAsync(child, plan, wait, _time, _logger, [], emit, cancellationToken)
                .ConfigureAwait(false);
            await emit(new ProbeEvent("result", ResultText(ok, plan.Target, plan.Port, sid, error))).ConfigureAwait(false);
        }
        finally
        {
            await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string ResultText(bool ok, string? target, string? port, string? sid, string? error) =>
        JsonSerializer.Serialize(new { ok, target, port, sid, error }, ResultJson);

    /// <summary>
    /// The live "test to here" probe (step editor): dial the DRAFT script's target, replay its steps up to
    /// <paramref name="stopBefore"/>, then STREAM every byte the node emits at that point via
    /// <paramref name="emit"/> so the operator can watch and judge when the prompt has settled. Uses the
    /// UNSAVED <paramref name="draftSteps"/> (so it works mid-edit); the stored partner supplies only the
    /// ConTimeout (default 60 if not yet saved). Moves no mail. Streaming ends when the operator closes the
    /// stream (<paramref name="cancellationToken"/>) or the hold window lapses; the child is always closed.
    /// </summary>
    public async Task ProbeStreamToAsync(
        string partnerCall,
        IReadOnlyList<ConnectStep> draftSteps,
        int stopBefore,
        Func<ProbeEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partnerCall);
        ArgumentNullException.ThrowIfNull(draftSteps);
        ArgumentNullException.ThrowIfNull(emit);

        Partner? partner = _store.GetPartner(partnerCall);
        int conTimeout = partner?.ConTimeoutSeconds ?? 60;
        Partner probePartner = (partner ?? new Partner { Call = partnerCall }) with { ConnectScript = [.. draftSteps] };
        ConnectPlan plan = ConnectScript.Resolve(probePartner);
        TimeSpan wait = TimeSpan.FromSeconds(Math.Max(1, conTimeout));
        var transcript = new List<string>();

        if (plan.Target is null)
        {
            await emit(new ProbeEvent("error", "no dial target — add an open step (the call/alias to connect to) at the top of the script before probing")).ConfigureAwait(false);
            return;
        }

        RhpChildConnection child;
        try
        {
            child = await _link.OpenAsync(plan.Target, plan.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // operator closed the stream during the dial
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await emit(new ProbeEvent("error", ex.Message)).ConfigureAwait(false);
            return;
        }

        try
        {
            // A generous hold so the operator can deliberate; they close the stream (press the button) once
            // the prompt has settled, otherwise it auto-closes after this.
            TimeSpan maxHold = TimeSpan.FromSeconds(Math.Clamp(conTimeout * 2, 30, 120));
            await ConnectScriptRunner
                .ProbeStreamAsync(child, plan, stopBefore, wait, _time, _logger, transcript, emit, maxHold, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
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
        ExpectSendStep { Expect.Length: > 0 } es => $"{es.Expect}={es.Send}",
        ExpectSendStep es => es.Send,
        _ => step.ToString() ?? "",
    };
}
