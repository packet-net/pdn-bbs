using System.Collections.Concurrent;
using System.Threading.Channels;
using Bbs.Core;
using Microsoft.Extensions.Logging;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace Bbs.Host.Rhp;

/// <summary>Static configuration for the node link.</summary>
public sealed record RhpLinkOptions
{
    /// <summary>RHP server host.</summary>
    public required string Host { get; init; }

    /// <summary>RHP server port.</summary>
    public required int Port { get; init; }

    /// <summary>
    /// The BBS callsign bound (and listened) on every node port. When <see cref="ProbeSsid"/> is
    /// set this is the FIRST candidate of a free-SSID walk (a callsign derived from the node — see
    /// <see cref="BindCallsign"/>); otherwise it is bound verbatim.
    /// </summary>
    public required string BindCallsign { get; init; }

    /// <summary>
    /// Walk to the next free SSID when the node refuses the primary listen with errCode 9
    /// "Duplicate socket". Set ONLY for a callsign derived from <c>PDN_NODE_CALLSIGN</c>; an
    /// explicit configured callsign and a standalone placeholder are bound verbatim (no probe).
    /// </summary>
    public bool ProbeSsid { get; init; }

    /// <summary>
    /// The node's own callsign (<c>PDN_NODE_CALLSIGN</c>), whose SSID the probe skips. Null when
    /// there is no node (standalone) or no probe.
    /// </summary>
    public string? NodeCallsign { get; init; }

    /// <summary>
    /// A friendly service alias bound IN ADDITION to <see cref="BindCallsign"/> (e.g. <c>BBS</c>),
    /// so users can <c>C BBS</c> to reach the mailbox. Inbound connects to it route to the same
    /// session handler (every accept flows through <see cref="RhpNodeLink.Accepted"/> regardless of
    /// the dialled local callsign). Null/empty binds no alias. The alias does NOT probe: if the node
    /// refuses it (duplicate), the link logs and carries on with just the primary callsign.
    /// </summary>
    public string? ServiceCallsign { get; init; }

    /// <summary>Auth user, when the node requires auth.</summary>
    public string? User { get; init; }

    /// <summary>Auth password.</summary>
    public string? Pass { get; init; }

    /// <summary>First reconnect delay after a connection loss.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Reconnect backoff cap.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The resilient RHPv2 attachment to the node (design.md: "RHPv2 client … binding the BBS
/// callsign"). Owns one <see cref="RhpClient"/> at a time: connect → optional
/// <c>auth</c> → <c>socket</c>/<c>bind</c>(all ports)/<c>listen</c> for the BBS callsign,
/// then surfaces accepted children on <see cref="Accepted"/> and routes <c>recv</c>/server
/// <c>close</c> pushes to their <see cref="RhpChildConnection"/>s. When the node restarts,
/// every child faults, and the link reconnects with TimeProvider-driven exponential
/// backoff, re-binding on every reconnect.
/// </summary>
public sealed class RhpNodeLink : IAsyncDisposable
{
    private readonly RhpLinkOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly Channel<RhpChildConnection> _accepted = Channel.CreateUnbounded<RhpChildConnection>();
    private readonly ConcurrentDictionary<int, RhpChildConnection> _children = new();
    private readonly Dictionary<int, List<byte[]>> _pendingRecv = [];
    private readonly object _gate = new();

    private volatile RhpClient? _client;
    private volatile TaskCompletionSource _up = NewTcs();

    /// <summary>
    /// The primary callsign actually bound. Starts at <see cref="RhpLinkOptions.BindCallsign"/>;
    /// once a bind succeeds it holds the callsign in use. Read by callers for outbound opens and
    /// diagnostics.
    /// </summary>
    private volatile string _boundCallsign;

    /// <summary>
    /// The callsign a successful bind has PINNED. Null until the first successful primary bind; once
    /// set, every reconnect binds it DIRECTLY with no re-probe — so the BBS's on-air identity is
    /// stable across node outages even if some other station claims the original SSID during one.
    /// </summary>
    private volatile string? _pinnedCallsign;

    /// <summary>Creates the link (call <see cref="RunAsync"/> to start it).</summary>
    public RhpNodeLink(RhpLinkOptions options, TimeProvider time, ILogger<RhpNodeLink> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _time = time;
        _logger = logger;
        _boundCallsign = options.BindCallsign;
    }

    /// <summary>Inbound connections accepted for the bound BBS callsign(s).</summary>
    public ChannelReader<RhpChildConnection> Accepted => _accepted.Reader;

    /// <summary>
    /// The primary callsign currently bound (after any free-SSID probe). Equal to
    /// <see cref="RhpLinkOptions.BindCallsign"/> until a probe walks it.
    /// </summary>
    public string BoundCallsign => _boundCallsign;

    /// <summary>Whether the link is currently connected and bound.</summary>
    public bool IsUp => _up.Task.IsCompletedSuccessfully;

    /// <summary>Completes when the link is connected and bound (a new wait per outage).</summary>
    public Task WaitForUpAsync(CancellationToken cancellationToken) => _up.Task.WaitAsync(cancellationToken);

    /// <summary>The connect/bind/dispatch loop; runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan backoff = _options.InitialBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            RhpClient? client = null;
            var lost = NewTcs();
            try
            {
                client = await RhpClient.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);
                client.Received += (_, e) => OnReceived(e.Message);
                client.Accepted += (_, e) => OnAccepted(e.Message);
                client.Closed += (_, e) => OnClosed(e.Handle);
                client.Disconnected += (_, _) => lost.TrySetResult();

                if (_options.User is { Length: > 0 } user)
                {
                    await client.AuthenticateAsync(user, _options.Pass ?? "", cancellationToken).ConfigureAwait(false);
                }

                // Bind + listen the primary BBS callsign (with a free-SSID probe when it was derived
                // from the node), then ALSO bind the friendly service alias ("BBS"). Both listens are
                // on the one client, so every accept — for either callsign — flows through Accepted.
                string bound = await BindPrimaryAsync(client, cancellationToken).ConfigureAwait(false);
                _boundCallsign = bound;
                await BindServiceAliasAsync(client, cancellationToken).ConfigureAwait(false);

                _client = client;
                _up.TrySetResult();
                backoff = _options.InitialBackoff;
                LogBound(_logger, bound, _options.Host, _options.Port, null);

                await using CancellationTokenRegistration registration =
                    cancellationToken.Register(() => lost.TrySetResult());
                await lost.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogConnectFailed(_logger, _options.Host, _options.Port, ex.Message, null);
            }
            finally
            {
                if (_up.Task.IsCompleted)
                {
                    _up = NewTcs();
                }

                _client = null;
                FaultAllChildren();
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            LogReconnectWait(_logger, backoff.TotalSeconds, null);
            try
            {
                await Task.Delay(backoff, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = backoff * 2 > _options.MaxBackoff ? _options.MaxBackoff : backoff * 2;
        }

        _accepted.Writer.TryComplete();
    }

    /// <summary>
    /// Opens an outbound connection (RHP <c>open</c>, Active) from the BBS callsign to
    /// <paramref name="remote"/> — the forwarding scheduler's dial path.
    /// <paramref name="port"/> pins the node port (a connect script's
    /// <c>C &lt;port&gt; &lt;call&gt;</c>); null lets the node choose.
    /// </summary>
    /// <exception cref="InvalidOperationException">The link is down.</exception>
    public async Task<RhpChildConnection> OpenAsync(string remote, string? port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        RhpClient client = _client ?? throw new InvalidOperationException("The RHP link is down.");
        int handle = await client.OpenAsync(
            ProtocolFamily.Ax25,
            SocketMode.Stream,
            port,
            local: _boundCallsign,
            remote: remote,
            OpenFlags.Active,
            cancellationToken).ConfigureAwait(false);
        var child = new RhpChildConnection(this, handle, remote);
        RegisterChild(child);
        return child;
    }

    /// <summary>
    /// socket+bind+listen the primary BBS callsign, returning the callsign actually bound. When
    /// <see cref="RhpLinkOptions.ProbeSsid"/> is set AND we have not yet pinned a winner, a listen
    /// refused with errCode 9 ("Duplicate socket") walks to the next free SSID
    /// (<see cref="Callsigns.SsidProbeCandidates"/>, skipping 0 and the node's own SSID) and keeps
    /// the first that binds, PINNING it (<see cref="_pinnedCallsign"/>) so every later reconnect
    /// binds that winner directly with no re-probe — the on-air identity is stable across outages.
    /// A non-probing callsign is bound verbatim; a duplicate then propagates (the link reconnects
    /// and retries — the configured identity is the operator's to fix).
    /// </summary>
    private async Task<string> BindPrimaryAsync(RhpClient client, CancellationToken cancellationToken)
    {
        // Once a bind has pinned a callsign, bind it directly — never re-probe (so a station that
        // grabbed our original SSID during an outage cannot shift our identity on reconnect).
        IReadOnlyList<string> candidates = _pinnedCallsign is { } pinned
            ? [pinned]
            : _options.ProbeSsid
                ? Callsigns.SsidProbeCandidates(_options.BindCallsign, _options.NodeCallsign)
                : [_options.BindCallsign];

        var taken = new List<string>();
        for (int i = 0; i < candidates.Count; i++)
        {
            string candidate = candidates[i];
            int handle = await client.SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream, cancellationToken).ConfigureAwait(false);
            try
            {
                // A null port means "all ports" for bind (rhp2-server.md V1 scope).
                await client.BindAsync(handle, candidate, port: null, cancellationToken).ConfigureAwait(false);
                await client.ListenAsync(handle, OpenFlags.Passive, cancellationToken).ConfigureAwait(false);
            }
            catch (RhpServerException ex)
            {
                // Always release the refused socket handle (no finally — the success path KEEPS the
                // listener handle open).
                await TryCloseAsync(client, handle, cancellationToken).ConfigureAwait(false);

                // While probing, a duplicate-socket on a non-final candidate walks to the next SSID.
                if (candidates.Count > 1 && ex.ErrorCode == RhpErrorCode.DuplicateSocket && i < candidates.Count - 1)
                {
                    taken.Add(candidate);
                    continue;
                }

                // Probe exhausted every SSID (or a non-probing callsign was refused): log a clear,
                // distinct diagnostic, then propagate so the link reconnects and retries.
                if (candidates.Count > 1)
                {
                    LogSsidExhausted(_logger, _options.BindCallsign, ex);
                }

                throw;
            }

            if (taken.Count > 0)
            {
                LogProbed(_logger, candidate, string.Join(", ", taken), null);
            }

            _pinnedCallsign = candidate;
            return candidate;
        }

        // Unreachable (the loop returns or throws on the last candidate); satisfy the compiler.
        return _boundCallsign;
    }

    /// <summary>
    /// Binds the friendly service alias (<see cref="RhpLinkOptions.ServiceCallsign"/>, e.g.
    /// <c>BBS</c>) as a SECOND listen on the same client, so <c>C BBS</c> reaches the mailbox.
    /// No-op when no alias is configured, or when it equals the primary callsign. The alias does
    /// not probe: a duplicate (or any bind/listen refusal) is logged and the link carries on with
    /// just the primary callsign — losing the convenience alias must never take the BBS off the air.
    /// </summary>
    private async Task BindServiceAliasAsync(RhpClient client, CancellationToken cancellationToken)
    {
        if (_options.ServiceCallsign is not { Length: > 0 } alias
            || string.Equals(alias, _boundCallsign, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int handle = await client.SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.BindAsync(handle, alias, port: null, cancellationToken).ConfigureAwait(false);
            await client.ListenAsync(handle, OpenFlags.Passive, cancellationToken).ConfigureAwait(false);
            LogAliasBound(_logger, alias, null);
        }
        catch (RhpServerException ex)
        {
            await TryCloseAsync(client, handle, cancellationToken).ConfigureAwait(false);
            LogAliasBindFailed(_logger, alias, ex.ErrorCode, ex.ErrorText ?? "", null);
        }
    }

    private static async Task TryCloseAsync(RhpClient client, int handle, CancellationToken cancellationToken)
    {
        try
        {
            await client.CloseAsync(handle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RhpProtocolException or RhpServerException)
        {
            // A refused handle may already be gone server-side — nothing to clean up.
        }
    }

    internal async Task SendOnChildAsync(int handle, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        RhpClient client = _client ?? throw new InvalidOperationException("The RHP link is down.");
        SendReplyMessage reply = await client.SendOnHandleAsync(handle, data.Span, cancellationToken).ConfigureAwait(false);
        if (reply.ErrCode != 0)
        {
            throw new IOException($"RHP send on handle {handle} failed: {reply.ErrCode} {reply.ErrText}");
        }
    }

    internal async Task CloseChildAsync(int handle, CancellationToken cancellationToken)
    {
        if (_children.TryRemove(handle, out RhpChildConnection? child))
        {
            child.MarkClosed();
        }

        RhpClient? client = _client;
        if (client is null)
        {
            return;
        }

        try
        {
            await client.CloseAsync(handle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCloseFailed(_logger, handle, ex.Message, null);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        RhpClient? client = _client;
        _client = null;
        FaultAllChildren();
        _accepted.Writer.TryComplete();
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnAccepted(AcceptMessage message)
    {
        var child = new RhpChildConnection(this, message.Child, message.Remote ?? "");
        RegisterChild(child);
        LogAccepted(_logger, child.RemoteCallsign, child.Handle, null);
        _accepted.Writer.TryWrite(child);
    }

    private void OnReceived(RecvMessage message)
    {
        byte[] data = RhpDataEncoding.FromWireString(message.Data ?? "");
        lock (_gate)
        {
            if (_children.TryGetValue(message.Handle, out RhpChildConnection? child))
            {
                child.Deliver(data);
                return;
            }

            // recv raced ahead of our handle registration (e.g. between openReply and
            // RegisterChild) — stash until the child exists.
            if (!_pendingRecv.TryGetValue(message.Handle, out List<byte[]>? pending))
            {
                pending = [];
                _pendingRecv[message.Handle] = pending;
            }

            pending.Add(data);
        }
    }

    private void OnClosed(int handle)
    {
        lock (_gate)
        {
            _pendingRecv.Remove(handle);
        }

        if (_children.TryRemove(handle, out RhpChildConnection? child))
        {
            child.MarkClosed();
        }
    }

    private void RegisterChild(RhpChildConnection child)
    {
        lock (_gate)
        {
            _children[child.Handle] = child;
            if (_pendingRecv.Remove(child.Handle, out List<byte[]>? pending))
            {
                foreach (byte[] chunk in pending)
                {
                    child.Deliver(chunk);
                }
            }
        }
    }

    private void FaultAllChildren()
    {
        lock (_gate)
        {
            _pendingRecv.Clear();
        }

        foreach (int handle in _children.Keys)
        {
            if (_children.TryRemove(handle, out RhpChildConnection? child))
            {
                child.MarkClosed();
            }
        }
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly Action<ILogger, string, string, int, Exception?> LogBound =
        LoggerMessage.Define<string, string, int>(LogLevel.Information, new EventId(1, "RhpBound"),
            "Bound {Callsign} on RHP at {Host}:{Port}");

    private static readonly Action<ILogger, string, string, Exception?> LogProbed =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(6, "RhpSsidProbed"),
            "Derived BBS callsign {Callsign} — these SSIDs were already claimed on the node: {Taken}");

    private static readonly Action<ILogger, string, Exception?> LogSsidExhausted =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(9, "RhpSsidExhausted"),
            "No free SSID for the derived BBS callsign {Callsign} — every candidate is claimed on the node; retrying after backoff. Set callsign in bbs.yaml to pin a free identity");

    private static readonly Action<ILogger, string, Exception?> LogAliasBound =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(7, "RhpAliasBound"),
            "Bound service alias {Alias} on RHP (users can connect to it to reach the mailbox)");

    private static readonly Action<ILogger, string, int, string, Exception?> LogAliasBindFailed =
        LoggerMessage.Define<string, int, string>(LogLevel.Warning, new EventId(8, "RhpAliasBindFailed"),
            "Service alias {Alias} could not be bound (errCode {ErrCode} {ErrText}); continuing without it");

    private static readonly Action<ILogger, string, int, string, Exception?> LogConnectFailed =
        LoggerMessage.Define<string, int, string>(LogLevel.Warning, new EventId(2, "RhpConnectFailed"),
            "RHP connection to {Host}:{Port} failed: {Reason}");

    private static readonly Action<ILogger, double, Exception?> LogReconnectWait =
        LoggerMessage.Define<double>(LogLevel.Information, new EventId(3, "RhpReconnectWait"),
            "Reconnecting to RHP in {Seconds}s");

    private static readonly Action<ILogger, string, int, Exception?> LogAccepted =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(4, "RhpAccepted"),
            "Inbound connection from {Remote} (handle {Handle})");

    private static readonly Action<ILogger, int, string, Exception?> LogCloseFailed =
        LoggerMessage.Define<int, string>(LogLevel.Debug, new EventId(5, "RhpCloseFailed"),
            "RHP close of handle {Handle} failed: {Reason}");
}
