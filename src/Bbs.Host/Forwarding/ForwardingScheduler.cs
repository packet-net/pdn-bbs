using System.Collections.Concurrent;
using System.Threading.Channels;
using Bbs.Core;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>
/// The outbound forwarding scheduler: one loop per enabled partner, woken by the partner's
/// FwdInterval timer or a send-immediately nudge (both TimeProvider-driven). A cycle with a
/// non-empty queue resolves the connect script (<see cref="ConnectScript"/>), opens an RHP
/// session to the target, and runs the Fbb caller role. One session at a time per partner
/// is structural (the loop is sequential); failed cycles retry with exponential backoff
/// capped at the partner interval.
///
/// Reverse collection (<see cref="Partner.Collect"/>, default off): a collect partner is
/// dialled on the FwdInterval cadence EVEN WITH AN EMPTY QUEUE — a deliberate poll. The poll
/// session opens with FF (nothing of ours) and the FBB in-session reverse (spec §3.11) drains
/// the partner's queue for us through the same Core receive path the answerer uses. "Nothing
/// to send AND nothing collected" is a clean graceful no-op (no error, no backoff). Without
/// collect, an empty queue never dials — a quiet link stays quiet.
/// </summary>
public sealed class ForwardingScheduler
{
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(60);

    private readonly RhpNodeLink _link;
    private readonly FbbSessionRunner _runner;
    private readonly BbsStore _store;
    private readonly BbsIdentity _identity;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Channel<bool>> _nudges = new(StringComparer.OrdinalIgnoreCase);

    // Store-first: partners are created/edited/deleted at runtime via the forwarding editor, so the
    // set of loops is dynamic. _loops tracks the running per-partner loops; the supervisor in
    // RunAsync spins a loop for any enabled partner that lacks one, on a Reconcile() signal or a
    // periodic re-sweep. A deleted/disabled partner's loop self-exits and reaps itself from _loops.
    private readonly ConcurrentDictionary<string, Task> _loops = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> _reconcile = NewNudgeChannel();
    private static readonly TimeSpan ReconcileTick = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Creates the scheduler. Nudge channels are created eagerly for every enabled partner
    /// so a nudge fired before <see cref="RunAsync"/> (the startup backlog sweep) is kept.
    /// </summary>
    public ForwardingScheduler(
        RhpNodeLink link,
        FbbSessionRunner runner,
        BbsStore store,
        BbsIdentity identity,
        TimeProvider time,
        ILogger<ForwardingScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _link = link;
        _runner = runner;
        _store = store;
        _identity = identity;
        _time = time;
        _logger = logger;

        foreach (Partner partner in store.ListPartners())
        {
            if (partner.Enabled)
            {
                _nudges[Callsigns.Normalize(partner.Call)] = NewNudgeChannel();
            }
        }
    }

    /// <summary>Wakes a partner's loop now (FWDNewImmediately). Unknown/disabled partners are ignored.</summary>
    public void Nudge(string partnerCall)
    {
        ArgumentNullException.ThrowIfNull(partnerCall);
        if (_nudges.TryGetValue(Callsigns.Normalize(partnerCall), out Channel<bool>? nudge))
        {
            nudge.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// Supervises the per-partner forwarding loops until cancelled. One loop runs per enabled
    /// partner; a deleted/disabled partner's loop self-exits (and reaps itself), and a partner
    /// created/enabled at runtime via the editor gets a fresh loop on the next reconcile — signalled
    /// by <see cref="Reconcile"/> (immediate) or a periodic re-sweep (a backstop). Store-first:
    /// the partner set is no longer fixed at startup.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                StartLoopsForEnabledPartners(cancellationToken);

                // Re-sweep on an explicit reconcile (a partner created/enabled in the editor) or a
                // periodic tick (a backstop against a missed signal). DropWrite coalesces signals.
                using var tick = new CancellationTokenSource(ReconcileTick, _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tick.Token);
                try
                {
                    await _reconcile.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The periodic tick fired — fall through and re-sweep.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }

        await Task.WhenAll(_loops.Values).ConfigureAwait(false);
    }

    /// <summary>
    /// Ask the supervisor to (re)scan for partners that need a loop — call after creating or
    /// enabling a partner in the editor so it starts forwarding immediately rather than waiting for
    /// the periodic re-sweep. Cheap, idempotent, and coalesces with other pending signals.
    /// </summary>
    public void Reconcile() => _reconcile.Writer.TryWrite(true);

    /// <summary>Start a loop for every enabled partner that has no running loop. Called only from the
    /// single supervisor loop, so the contains-then-add is race-free.</summary>
    private void StartLoopsForEnabledPartners(CancellationToken cancellationToken)
    {
        foreach (Partner partner in _store.ListPartners())
        {
            if (!partner.Enabled)
            {
                continue;
            }

            string call = Callsigns.Normalize(partner.Call);
            _nudges.TryAdd(call, NewNudgeChannel());
            if (!_loops.ContainsKey(call))
            {
                _loops[call] = RunPartnerLoopThenReapAsync(call, cancellationToken);
                // Wake the fresh loop so it checks its queue NOW rather than after a full interval —
                // a partner created in the editor with mail already queued (its FWDNewImmediately
                // nudge was lost, the channel/loop not existing yet), or a startup backlog. Harmless
                // for an empty, non-collect queue: the loop checks, finds nothing, waits as usual.
                _nudges[call].Writer.TryWrite(true);
            }
        }
    }

    /// <summary>Runs a partner loop, then reaps it from the running set so a later re-enable re-spins
    /// it (the loop self-exits when its partner is deleted or disabled).</summary>
    private async Task RunPartnerLoopThenReapAsync(string call, CancellationToken cancellationToken)
    {
        try
        {
            await RunPartnerLoopAsync(call, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loops.TryRemove(call, out _);
        }
    }

    private async Task RunPartnerLoopAsync(string call, CancellationToken cancellationToken)
    {
        Channel<bool> nudge = _nudges[call];
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Partner? partner = _store.GetPartner(call);
            if (partner is null || !partner.Enabled)
            {
                return; // config is fixed for the process lifetime (source-of-truth at startup)
            }

            var interval = TimeSpan.FromSeconds(Math.Max(1, partner.ForwardIntervalSeconds));
            TimeSpan wait = failures == 0 ? interval : RetryDelay(failures, interval);
            try
            {
                await WaitForTimerOrNudgeAsync(wait, nudge, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            IReadOnlyList<Message> queue = _store.GetForwardQueue(partner.Call);
            if (queue.Count == 0 && !partner.Collect)
            {
                // Nothing to do is not a failure. Without collect set we never dial an empty
                // queue (a quiet link stays quiet); a collect partner DOES dial here to poll.
                failures = 0;
                continue;
            }

            try
            {
                // An empty queue + collect is a deliberate reverse-collection POLL: dial with
                // nothing of ours to send, the session's in-session reverse (spec §3.11) drains
                // the partner's queue for us. "Nothing to send AND nothing collected" is a clean
                // graceful no-op (FF↔FQ) — not a failure, so it does not arm the backoff.
                bool graceful = await RunCycleAsync(partner, queue, cancellationToken).ConfigureAwait(false);
                failures = graceful ? 0 : failures + 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                LogCycleFailed(_logger, partner.Call, ex.Message, failures, null);
            }
        }
    }

    private async Task<bool> RunCycleAsync(Partner partner, IReadOnlyList<Message> queue, CancellationToken cancellationToken)
    {
        ConnectPlan plan = ConnectScript.Resolve(partner);
        foreach (string warning in plan.Warnings)
        {
            LogScriptWarning(_logger, partner.Call, warning, null);
        }

        IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(queue, partner, _identity, _time, _logger);
        if (outbound.Count == 0 && !partner.Collect)
        {
            // Nothing to send and not a collect partner: don't dial for nothing. A non-empty
            // queue that built to zero outbound is everything-skipped (oversize) — same verdict.
            return true;
        }

        if (outbound.Count == 0)
        {
            // A reverse-collection poll: dial with an empty outbound batch (we open with FF) and
            // let the session's in-session reverse pick up whatever the partner holds for us.
            LogCollectStart(_logger, partner.Call, plan.Target, null);
        }
        else
        {
            LogCycleStart(_logger, partner.Call, plan.Target, outbound.Count, null);
        }

        RhpChildConnection child = await _link.OpenAsync(plan.Target, plan.Port, cancellationToken).ConfigureAwait(false);
        try
        {
            // Navigate per the connect script (spec §4.4); a script failure (node
            // failure text / response timeout) fails the cycle and rides the same
            // backoff-retry path as a refused open.
            byte[] initial;
            try
            {
                initial = await ConnectScriptRunner.RunAsync(
                    child, plan, TimeSpan.FromSeconds(Math.Max(1, partner.ConTimeoutSeconds)),
                    _time, _logger, cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectScriptException ex)
            {
                LogScriptFailed(_logger, partner.Call, ex.Message, null);
                return false;
            }

            FbbSessionResult result = await _runner
                .RunCallerAsync(child, partner, outbound, cancellationToken, initial)
                .ConfigureAwait(false);
            return result.Graceful;
        }
        finally
        {
            await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task WaitForTimerOrNudgeAsync(TimeSpan wait, Channel<bool> nudge, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task timer = Task.Delay(wait, _time, cts.Token);
        Task<bool> woken = nudge.Reader.ReadAsync(cts.Token).AsTask();
        Task first = await Task.WhenAny(timer, woken).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(timer, woken).ConfigureAwait(false); // observe both
        }
        catch (OperationCanceledException)
        {
            // The losing wait's cancellation — expected.
        }

        cancellationToken.ThrowIfCancellationRequested();
        await first.ConfigureAwait(false); // propagate a genuine failure, if any
    }

    /// <summary>Backoff for retry n: 60 s doubling, capped at the partner interval.</summary>
    public static TimeSpan RetryDelay(int failures, TimeSpan interval)
    {
        double seconds = BaseRetryDelay.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 10));
        var delay = TimeSpan.FromSeconds(seconds);
        return delay > interval ? interval : delay;
    }

    private static Channel<bool> NewNudgeChannel() =>
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private static readonly Action<ILogger, string, string, Exception?> LogScriptWarning =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(1, "ConnectScriptWarning"),
            "Partner {Partner}: {Warning}");

    private static readonly Action<ILogger, string, string, int, Exception?> LogCycleStart =
        LoggerMessage.Define<string, string, int>(LogLevel.Information, new EventId(2, "CycleStart"),
            "Forwarding cycle to {Partner} via {Target}: {Count} message(s) queued");

    private static readonly Action<ILogger, string, string, Exception?> LogCollectStart =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(5, "CollectStart"),
            "Reverse-collection poll to {Partner} via {Target}: empty queue — collecting only");

    private static readonly Action<ILogger, string, string, int, Exception?> LogCycleFailed =
        LoggerMessage.Define<string, string, int>(LogLevel.Warning, new EventId(3, "CycleFailed"),
            "Forwarding cycle to {Partner} failed ({Reason}); retry with backoff (failure #{Failures})");

    private static readonly Action<ILogger, string, string, Exception?> LogScriptFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(4, "ConnectScriptFailed"),
            "Partner {Partner}: {Reason}; cycle abandoned, will retry with backoff");
}
