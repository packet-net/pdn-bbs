using System.Text;
using Bbs.Console;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Forwarding;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Sessions;

/// <summary>
/// The inbound session demultiplexer (design decision 1): one RHP-bound callsign serves
/// both users and partner BBSes, and the BBS speaks FIRST — the greet-immediately flow.
///
/// On accept the demux instantly sends our SID line (<see cref="Sid.Build"/>, the same
/// <c>[PDN-&lt;ver&gt;-B1FHM$]</c> the FBB answerer would emit — compat spec §1.1/§3.1:
/// the called BBS always sends its SID first) and starts the
/// <see cref="BbsConsoleSession"/> so its greeting/prompt flows immediately. Human
/// callers ignore the SID line (real BPQ BBSes show exactly this); a partner caller
/// parses it and answers with its own SID. The console's input is held behind a
/// first-line gate (see <see cref="RhpTerminal"/>) while the demux peeks the first
/// inbound line — a forwarding opener (<see cref="Sid.IsSidShaped"/>, or a Winlink-style
/// <c>;FW:</c> line, spec §1.1) hands the stream to the FBB answerer in continue-mode
/// (<see cref="FbbSessionConfig.SidAlreadySent"/> — our SID is already on the wire);
/// anything else releases the gate and the line is the console's first input (the
/// new-user name prompt can therefore never eat a partner's SID — the SID check happens
/// before any console input consumption). A SILENT caller sees the greeting while the
/// demux waits; at <c>demuxFirstLineWaitSeconds</c> expiry the session is the console's
/// and input flows normally from then on (a SID arriving after expiry is treated as
/// console input).
///
/// On the FBB handoff the console task is aborted via the gate and awaited to completion
/// BEFORE the answerer pumps, so every console write (greeting/prompt) is on the wire
/// ahead of any FBB output; FBB callers tolerate banner text around the SID exchange
/// (spec §3.1 — and for a known partner the console writes only the <c>de CALL&gt;</c>
/// prompt, reproducing the classic SID+prompt transcript).
///
/// Console end reasons: Bye and too-many-errors close the child; NODE also closes it
/// (v1 — an RHP child has no return-to-node hand-back); Drop just cleans up.
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
    private readonly string _sidVersion;
    private readonly ILogger _logger;

    /// <summary>Creates the demux. <paramref name="sidVersion"/> feeds the greet-immediately SID line.</summary>
    public InboundDemux(
        RhpNodeLink link,
        BbsStore store,
        FbbSessionRunner fbbRunner,
        RoutingService routing,
        BbsConsoleConfig consoleConfig,
        IUserSettingsStore userSettings,
        string sidVersion,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(sidVersion);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _link = link;
        _store = store;
        _fbbRunner = fbbRunner;
        _routing = routing;
        _consoleConfig = consoleConfig;
        _userSettings = userSettings;
        _sidVersion = sidVersion;
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

    /// <summary>One child's whole lifetime: greet, peek, dispatch, cleanup. Never throws.</summary>
    internal async Task HandleChildAsync(RhpChildConnection child, CancellationToken cancellationToken)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<BbsSessionEndReason>? console = null;
        try
        {
            // Greet immediately: our SID is the first thing on the wire — a real LinBPQ
            // caller (dialling us to forward) sends nothing until it has seen it, so the
            // old silent peek deadlocked both sides into the timeout (compat spec §1.1).
            //
            // The caller is known at accept time, so we advertise B2F ('2') in this greeting
            // SID ONLY when this caller matches a partner record we enabled B2 on
            // (Partner.AllowB2F — keyed by call as in RunAnswererAsync). To every other caller
            // we send the B1-only SID, so we never accept FC from a partner we didn't offer B2
            // to (the answerer's B2 gate is consistent with this advertised SID). The greet must
            // precede the peek, hence the lookup here rather than inside the FBB runner.
            Partner? partner = _store.GetPartner(child.RemoteCallsign);
            string sidLine = Sid.Build(_sidVersion, offerB2: partner?.AllowB2F ?? false);
            await child.SendAsync(Encoding.Latin1.GetBytes(sidLine + "\r\n"), cancellationToken).ConfigureAwait(false);

            // Start the console greeting flow now; its input parks on the gate.
            var assembler = new LineAssembler();
            var pending = new Queue<string>();
            var terminal = new RhpTerminal(child, assembler, pending, gate.Task);
            console = BbsConsoleSession.RunAsync(terminal, _store, _consoleConfig, _time, _userSettings, cancellationToken);

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
                    // Silence: a human caller who has already seen the greeting — the
                    // session is the console's from here on.
                }
            }

            if (closedDuringPeek)
            {
                gate.TrySetResult(false);
                await console.ConfigureAwait(false); // unwinds as Drop, nothing consumed
                await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (pending.Count > 0 && IsForwardingOpener(pending.Peek()))
            {
                // Hand off to the FBB answerer. Abort the console first and wait for it
                // to finish so its greeting/prompt writes are all on the wire before any
                // FBB output; it has consumed no input (the gate never opened).
                gate.TrySetResult(false);
                await console.ConfigureAwait(false);

                LogFbbSession(_logger, child.RemoteCallsign, null);
                await _fbbRunner.RunAnswererAsync(child, [.. consumed], cancellationToken).ConfigureAwait(false);
                await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // A console caller (first line was ordinary input, or the wait expired).
            gate.TrySetResult(true);
            LogConsoleSession(_logger, child.RemoteCallsign, null);
            BbsSessionEndReason reason = await console.ConfigureAwait(false);

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
            await CleanUpAsync(gate, console, child).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSessionFailed(_logger, child.RemoteCallsign, ex);
            await CleanUpAsync(gate, console, child).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A first line that announces a forwarding partner: a SID, or the Winlink-style
    /// <c>;FW:</c> preamble that precedes one (compat spec §1.1 — LinBPQ classifies on
    /// exactly these two shapes; the FBB answerer skips <c>;</c> comment lines while
    /// awaiting the SID proper).
    /// </summary>
    private static bool IsForwardingOpener(string line) =>
        Sid.IsSidShaped(line) || line.TrimStart().StartsWith(";FW:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Failure-path cleanup: release the console (as an abort) and close the child.</summary>
    private static async Task CleanUpAsync(
        TaskCompletionSource<bool> gate, Task<BbsSessionEndReason>? console, RhpChildConnection child)
    {
        gate.TrySetResult(false);
        if (console is not null)
        {
            try
            {
                await console.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled with the session — expected during shutdown.
            }
        }

        await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
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
