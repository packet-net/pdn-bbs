using Bbs.Console;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Forwarding;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Sessions;

/// <summary>
/// The inbound session demultiplexer (design decision 1): one RHP-bound callsign serves
/// both users and partner BBSes. Every accepted child gets a bounded TimeProvider-driven
/// peek at its first inbound line — a SID-shaped line (<see cref="Sid.IsSidShaped"/>)
/// selects the Fbb answerer (forwarding partner); anything else, including silence
/// followed by a first command, gets a <see cref="BbsConsoleSession"/> over an
/// <see cref="RhpTerminal"/>. Console end reasons: Bye and too-many-errors close the
/// child; NODE also closes it (v1 — an RHP child has no return-to-node hand-back);
/// Drop just cleans up.
/// </summary>
public sealed class InboundDemux
{
    private readonly RhpNodeLink _link;
    private readonly BbsStore _store;
    private readonly FbbSessionRunner _fbbRunner;
    private readonly RoutingService _routing;
    private readonly BbsConsoleConfig _consoleConfig;
    private readonly IUserSettingsStore _userSettings;
    private readonly TimeProvider _time;
    private readonly TimeSpan _firstLineWait;
    private readonly ILogger _logger;

    /// <summary>Creates the demux.</summary>
    public InboundDemux(
        RhpNodeLink link,
        BbsStore store,
        FbbSessionRunner fbbRunner,
        RoutingService routing,
        BbsConsoleConfig consoleConfig,
        IUserSettingsStore userSettings,
        TimeProvider time,
        TimeSpan firstLineWait,
        ILogger<InboundDemux> logger)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fbbRunner);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(consoleConfig);
        ArgumentNullException.ThrowIfNull(userSettings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _link = link;
        _store = store;
        _fbbRunner = fbbRunner;
        _routing = routing;
        _consoleConfig = consoleConfig;
        _userSettings = userSettings;
        _time = time;
        _firstLineWait = firstLineWait;
        _logger = logger;
    }

    /// <summary>Accepts children until cancelled; each runs concurrently to completion.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var sessions = new List<Task>();
        try
        {
            await foreach (RhpChildConnection child in _link.Accepted.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                sessions.RemoveAll(t => t.IsCompleted);
                sessions.Add(HandleChildAsync(child, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }

        await Task.WhenAll(sessions).ConfigureAwait(false);
    }

    /// <summary>One child's whole lifetime: peek, dispatch, cleanup. Never throws.</summary>
    internal async Task HandleChildAsync(RhpChildConnection child, CancellationToken cancellationToken)
    {
        try
        {
            var assembler = new LineAssembler();
            var pending = new Queue<string>();
            var consumed = new List<byte>();
            bool closedDuringPeek = false;

            using (var timeout = new CancellationTokenSource(_firstLineWait, _time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                try
                {
                    while (pending.Count == 0)
                    {
                        byte[]? data = await child.ReceiveAsync(linked.Token).ConfigureAwait(false);
                        if (data is null)
                        {
                            closedDuringPeek = true;
                            break;
                        }

                        consumed.AddRange(data);
                        foreach (string line in assembler.Feed(data))
                        {
                            pending.Enqueue(line);
                        }
                    }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Silence: a human caller waiting for the BBS — run the console.
                }
            }

            if (closedDuringPeek)
            {
                await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (pending.Count > 0 && Sid.IsSidShaped(pending.Peek()))
            {
                LogFbbSession(_logger, child.RemoteCallsign, null);
                await _fbbRunner.RunAnswererAsync(child, [.. consumed], cancellationToken).ConfigureAwait(false);
                await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            LogConsoleSession(_logger, child.RemoteCallsign, null);
            var terminal = new RhpTerminal(child, assembler, pending);
            BbsSessionEndReason reason = await BbsConsoleSession
                .RunAsync(terminal, _store, _consoleConfig, _time, _userSettings, cancellationToken)
                .ConfigureAwait(false);

            // Messages entered during the session (S family) join the forward queues now.
            _routing.RouteNewMessages();

            if (reason == BbsSessionEndReason.Node)
            {
                // v1: the RHP child has no way back to the node command processor, so NODE
                // closes the link like Bye (noted in design — revisit if RHP grows a hand-back).
                LogNodeClose(_logger, child.RemoteCallsign, null);
            }

            await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSessionFailed(_logger, child.RemoteCallsign, ex);
            await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static readonly Action<ILogger, string, Exception?> LogFbbSession =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "FbbSession"),
            "SID-shaped first line from {Remote}: running the FBB answerer");

    private static readonly Action<ILogger, string, Exception?> LogConsoleSession =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "ConsoleSession"),
            "Console session for {Remote}");

    private static readonly Action<ILogger, string, Exception?> LogNodeClose =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(3, "NodeClose"),
            "{Remote} asked for NODE; closing (no return-to-node over an RHP child in v1)");

    private static readonly Action<ILogger, string, Exception?> LogSessionFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(4, "SessionFailed"),
            "Session for {Remote} failed");
}
