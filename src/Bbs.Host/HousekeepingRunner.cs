using Bbs.Core;
using Microsoft.Extensions.Logging;

namespace Bbs.Host;

/// <summary>
/// Runs the Core housekeeping pass (compat spec §6) once at startup and then daily,
/// TimeProvider-driven.
/// </summary>
public sealed class HousekeepingRunner
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly BbsStore _store;
    private readonly HousekeepingPolicy _policy;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Creates the runner with the default lifetimes.</summary>
    public HousekeepingRunner(BbsStore store, HousekeepingPolicy policy, TimeProvider time, ILogger<HousekeepingRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _policy = policy;
        _time = time;
        _logger = logger;
    }

    /// <summary>The daily loop.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                HousekeepingSummary summary = Housekeeping.Run(_store, _policy);
                LogRun(_logger, summary.KilledMessagesPurged, summary.MessagesKilledByAge, summary.BidsPurged, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(_logger, ex);
            }

            try
            {
                await Task.Delay(Interval, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static readonly Action<ILogger, int, int, int, Exception?> LogRun =
        LoggerMessage.Define<int, int, int>(LogLevel.Information, new EventId(1, "HousekeepingRun"),
            "Housekeeping: purged {Purged} killed, killed {Killed} by age, dropped {Bids} expired BIDs");

    private static readonly Action<ILogger, Exception?> LogFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2, "HousekeepingFailed"),
            "Housekeeping run failed");
}
