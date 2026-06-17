using System.Net;
using System.Net.Sockets;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Rhp;
using Bbs.Host.Sessions;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>
/// The inbound FBB-over-TCP forwarding listener — BPQ's <c>FBBPORT</c> equivalent (issue #40). It
/// accepts raw-TCP connections from internet partner BBSes and hands each to the SAME
/// <see cref="FbbSessionRunner"/> the AX.25/RHP inbound path uses, so the FBB protocol FSM is shared,
/// not duplicated. Default off; started only when <c>fbbTcp.enabled</c> is set (see
/// <see cref="HostComposition"/>).
///
/// <para><b>Partner identity over connectionless TCP.</b> A TCP socket carries no link-layer
/// callsign, so identity comes from the IN-PROTOCOL FBB login handshake: on connect we send a
/// <c>Callsign :</c> prompt (BPQ's FBBPORT login shape) and read the partner's callsign line (an
/// optional <c>Password :</c> line is tolerated and ignored — callsign-is-identity is the authorization
/// model here). That callsign is looked up against the configured forwarding partners by BASE call
/// (as the reverse-forward queue join is); a caller that does not match an <c>enabled</c> partner is
/// rejected with a short notice and the socket is closed before any forwarding session begins. An
/// authorized caller is then greeted with our SID (advertising B2 only if the partner is B2-enabled)
/// and driven through <see cref="FbbSessionRunner.RunAnswererAsync"/> in continue-mode — identical to
/// the demux's greet-first handoff, so receive/route/loop-guard/restart-granting all apply unchanged.</para>
///
/// <para><b>Limits.</b> A semaphore bounds concurrent sessions (<c>fbbTcp.maxConnections</c>);
/// excess connections are accepted and immediately closed so a flood cannot exhaust resources. The
/// login read is bounded by a timeout so a silent connection cannot hold a slot.</para>
/// </summary>
public sealed class FbbTcpListener
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(60);

    private readonly FbbTcpConfig _config;
    private readonly BbsStore _store;
    private readonly FbbSessionRunner _runner;
    private readonly string _sidVersion;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _maxConnections;
    private int _active;

    /// <summary>Creates the listener.</summary>
    public FbbTcpListener(
        FbbTcpConfig config,
        BbsStore store,
        FbbSessionRunner runner,
        string sidVersion,
        TimeProvider time,
        ILogger<FbbTcpListener> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(sidVersion);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _store = store;
        _runner = runner;
        _sidVersion = sidVersion;
        _time = time;
        _logger = logger;
        _maxConnections = Math.Max(1, config.MaxConnections);
    }

    /// <summary>The accept loop. Runs until cancelled; each accepted socket runs concurrently.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Parse(_config.Bind), _config.Port);
        listener.Start();
        LogListening(_logger, _config.Bind, _config.Port, null);

        var sessions = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                sessions.RemoveAll(t => t.IsCompleted);
                sessions.Add(HandleClientAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutdown
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(sessions).ConfigureAwait(false);
        }
    }

    /// <summary>One client's whole lifetime: login handshake, authorize, greet, run, cleanup. Never throws.</summary>
    internal async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";

        // Resource bound: refuse a new session past the limit rather than queueing it. Reserve a slot
        // atomically; back it out immediately if we are over the cap.
        if (Interlocked.Increment(ref _active) > _maxConnections)
        {
            Interlocked.Decrement(ref _active);
            LogRefusedBusy(_logger, remote, null);
            client.Dispose();
            return;
        }

        try
        {
            client.NoDelay = true;
            using Stream stream = client.GetStream();

            (string? callsign, byte[] tail) = await ReadLoginAsync(stream, cancellationToken).ConfigureAwait(false);
            if (callsign is null)
            {
                LogRejected(_logger, remote, "no callsign presented", null);
                await WriteLineAsync(stream, "*** No callsign - bye", cancellationToken).ConfigureAwait(false);
                return;
            }

            // Authorization is by partner identity (the in-protocol login callsign). Unknown or
            // disabled callers never start a forwarding session.
            Partner? partner = _store.FindPartnerByBaseCall(callsign);
            if (partner is null || !partner.Enabled)
            {
                LogRejected(_logger, callsign, partner is null ? "not a configured partner" : "partner disabled", null);
                await WriteLineAsync(stream, "*** Not authorised - bye", cancellationToken).ConfigureAwait(false);
                return;
            }

            // Greet exactly like the demux: our SID first (B2 only if this partner is B2-enabled), then
            // hand the connection — identified by the IN-PROTOCOL callsign — to the shared runner in
            // continue-mode. Any bytes read past the login line are fed in so nothing is lost.
            var conn = new TcpFbbConnection(stream, callsign);
            string sid = Sid.Build(_sidVersion, offerB2: partner.AllowB2F);
            await conn.SendAsync(Encoding.Latin1.GetBytes(sid + "\r\n"), cancellationToken).ConfigureAwait(false);

            LogSession(_logger, callsign, remote, null);
            await _runner.RunAnswererAsync(conn, tail, cancellationToken).ConfigureAwait(false);
            await conn.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            LogDropped(_logger, remote, null);
        }
        catch (OperationCanceledException)
        {
            // shutdown / login timeout
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            client.Dispose();
        }
    }

    /// <summary>
    /// Reads the FBBPORT login: prompt for the callsign, read the first line as the callsign,
    /// tolerate (and ignore) a following <c>Password :</c> line if the client sends one. Returns the
    /// normalized callsign (or null when none arrived / timed out) and any bytes already read past the
    /// callsign line (so a client that pipelines its SID after the login loses nothing).
    /// </summary>
    private async Task<(string? Callsign, byte[] Tail)> ReadLoginAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(LoginTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await WriteLineAsync(stream, "Callsign :", linked.Token).ConfigureAwait(false);

            var assembler = new LineAssembler();
            var pending = new Queue<string>();
            var buffer = new byte[1024];
            var afterFirstLine = new List<byte>();
            string? callsign = null;

            while (callsign is null)
            {
                int n = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (n <= 0)
                {
                    return (null, []); // closed before a line arrived
                }

                foreach (string line in assembler.Feed(buffer.AsSpan(0, n)))
                {
                    pending.Enqueue(line);
                }

                if (pending.Count > 0)
                {
                    string first = pending.Dequeue().Trim();
                    callsign = Callsigns.Normalize(first);
                    // Anything the assembler buffered past the first line is the start of the FBB
                    // exchange (e.g. a pipelined SID); preserve it for the runner.
                    // (The LineAssembler holds no partial-line bytes we can recover here, so we only
                    // carry already-completed extra lines forward.)
                    while (pending.Count > 0)
                    {
                        afterFirstLine.AddRange(Encoding.Latin1.GetBytes(pending.Dequeue() + "\r\n"));
                    }
                }
            }

            return (callsign.Length > 0 ? callsign : null, [.. afterFirstLine]);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return (null, []); // login timed out
        }
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static readonly Action<ILogger, string, int, Exception?> LogListening =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(1, "FbbTcpListening"),
            "Inbound FBB-over-TCP listener on {Bind}:{Port}");

    private static readonly Action<ILogger, string, string, Exception?> LogSession =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(2, "FbbTcpSession"),
            "Inbound FBB-over-TCP forwarding session from {Callsign} ({Remote})");

    private static readonly Action<ILogger, string, string, Exception?> LogRejected =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(3, "FbbTcpRejected"),
            "Rejected inbound FBB-over-TCP connection from {Callsign}: {Reason}");

    private static readonly Action<ILogger, string, Exception?> LogRefusedBusy =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "FbbTcpBusy"),
            "Refused inbound FBB-over-TCP connection from {Remote}: at connection limit");

    private static readonly Action<ILogger, string, Exception?> LogDropped =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(5, "FbbTcpDropped"),
            "Inbound FBB-over-TCP connection from {Remote} dropped");
}
