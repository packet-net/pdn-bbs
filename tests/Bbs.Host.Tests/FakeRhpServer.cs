using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Bbs.Host.Sessions;

namespace Bbs.Host.Tests;

/// <summary>A `bind` the host performed.</summary>
internal sealed record BindRecord(int Handle, string? Local, string? Port);

/// <summary>An `open`(Active) the host performed.</summary>
internal sealed record OpenRecord(int Handle, string? Local, string? Remote, string? Port = null);

/// <summary>
/// A fake RHPv2 node serving the documented wire (packet.net docs/rhp2-server.md) over a
/// real loopback TcpListener: 2-byte big-endian length frames, JSON with `type` first,
/// capital `errCode`/`errText`, `id` echoed on replies, `seqno` (per connection, from 0)
/// on pushes, `data` as a Latin-1 string. Handles socket/bind/listen/open/send/close/auth;
/// test code injects accept/recv/close pushes and captures everything the host sends.
/// Stop/Start on the same port exercises the host's reconnect-and-rebind path.
/// </summary>
internal sealed class FakeRhpServer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<Conn> _conns = [];
    private readonly ConcurrentDictionary<int, Channel<byte[]>> _hostBytes = new();
    private readonly ConcurrentDictionary<int, Conn> _handleConns = new();
    private readonly ConcurrentDictionary<int, byte> _disconnectedHandles = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _nextHandle = 100;
    private volatile Conn? _activeListenerConn;

    /// <summary>The loopback port — stable across <see cref="StopAsync"/>/<see cref="Start"/>.</summary>
    public int Port { get; private set; }

    /// <summary>Every `bind` observed, in order.</summary>
    public Channel<BindRecord> Binds { get; } = Channel.CreateUnbounded<BindRecord>();

    /// <summary>Every `open`(Active) observed, in order (after the reply was sent).</summary>
    public Channel<OpenRecord> Opens { get; } = Channel.CreateUnbounded<OpenRecord>();

    /// <summary>Every `close` request observed.</summary>
    public Channel<int> Closes { get; } = Channel.CreateUnbounded<int>();

    /// <summary>Decides the errCode for each `open` (default 0 = connected).</summary>
    public Func<OpenRecord, int> OpenResult { get; set; } = _ => 0;

    private int _openAttempts;

    /// <summary>How many `open` requests arrived (successful or refused).</summary>
    public int OpenAttempts => Volatile.Read(ref _openAttempts);

    internal void CountOpenAttempt() => Interlocked.Increment(ref _openAttempts);

    /// <summary>Starts (or restarts) listening.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(_listener, _cts.Token);
        }
    }

    /// <summary>Stops listening and drops every connection (the host sees a dead node).</summary>
    public Task StopAsync()
    {
        List<Conn> conns;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            _listener?.Stop();
            _listener = null;
            conns = [.. _conns];
            _conns.Clear();
        }

        foreach (Conn conn in conns)
        {
            conn.Kill();
        }

        _activeListenerConn = null;
        return Task.CompletedTask;
    }

    /// <summary>Awaits the next `bind`.</summary>
    public async Task<BindRecord> WaitForBindAsync(TimeSpan? timeout = null) =>
        await Binds.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? TestTimeout.Default).ConfigureAwait(false);

    /// <summary>Awaits the host completing socket+bind+listen (the listener is then accept-ready).</summary>
    public async Task WaitForListenAsync(TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TestTimeout.Default;
        DateTime deadline = DateTime.UtcNow + limit;
        while (_activeListenerConn is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The host never reached listen.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Injects an inbound connection: the accept push (listener handle + new child +
    /// caller callsign + port "1") followed by the child's Connected status push, exactly
    /// the pdn accept lifecycle. Returns the far-station test peer.
    /// </summary>
    public async Task<FakeRhpPeer> AcceptChildAsync(string remoteCallsign)
    {
        await WaitForListenAsync().ConfigureAwait(false);
        Conn conn = _activeListenerConn!;
        int child = NextHandle();
        FakeRhpPeer peer = CreatePeer(conn, child, local: conn.BoundLocal, remote: remoteCallsign);
        await conn.PushAsync(new JsonObject
        {
            ["type"] = "accept",
            ["handle"] = conn.ListenerHandle,
            ["child"] = child,
            ["remote"] = remoteCallsign,
            ["local"] = conn.BoundLocal,
            ["port"] = "1",
        }).ConfigureAwait(false);
        await conn.PushAsync(new JsonObject
        {
            ["type"] = "status",
            ["handle"] = child,
            ["flags"] = 3, // ConOk | Connected — the pdn child-status push (RhpServer.OnInboundAcceptedAsync)
        }).ConfigureAwait(false);
        return peer;
    }

    /// <summary>Awaits the next host `open` and returns the peer playing the dialled station.</summary>
    public async Task<FakeRhpPeer> NextOpenAsync(TimeSpan? timeout = null)
    {
        OpenRecord record = await Opens.Reader.ReadAsync().AsTask()
            .WaitAsync(timeout ?? TestTimeout.Default).ConfigureAwait(false);
        Conn conn = _handleConns[record.Handle];
        FakeRhpPeer peer = CreatePeer(conn, record.Handle, record.Local, record.Remote);
        peer.Port = record.Port;
        return peer;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    internal int NextHandle() => Interlocked.Increment(ref _nextHandle);

    internal Channel<byte[]> HostBytes(int handle) =>
        _hostBytes.GetOrAdd(handle, _ => Channel.CreateUnbounded<byte[]>());

    internal void OnListen(Conn conn) => _activeListenerConn = conn;

    internal void OnHandleOwned(int handle, Conn conn) => _handleConns[handle] = conn;

    /// <summary>
    /// Marks a child handle as far-end-disconnected: a subsequent host `send` on it is
    /// refused with errCode 17 ("Not connected"), exactly as the real node reports a write
    /// to a stream whose peer has already dropped the AX.25 link.
    /// </summary>
    internal void MarkHandleDisconnected(int handle) => _disconnectedHandles[handle] = 1;

    internal bool IsHandleDisconnected(int handle) => _disconnectedHandles.ContainsKey(handle);

    internal void OnClosedByHost(int handle)
    {
        Closes.Writer.TryWrite(handle);
        HostBytes(handle).Writer.TryComplete();
    }

    private FakeRhpPeer CreatePeer(Conn conn, int handle, string? local, string? remote) =>
        new(conn, handle, local, remote, HostBytes(handle));

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var conn = new Conn(this, tcp);
                lock (_gate)
                {
                    _conns.Add(conn);
                }

                _ = conn.RunAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>One host TCP connection: frame reader, request dispatch, push writer.</summary>
    internal sealed class Conn(FakeRhpServer server, TcpClient tcp) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly NetworkStream _stream = tcp.GetStream();
        private int _seqno;

        public int? ListenerHandle { get; private set; }

        public string? BoundLocal { get; private set; }

        /// <summary>Drops the TCP connection (the host sees a dead node).</summary>
        public void Kill() => tcp.Dispose();

        /// <summary>Marks one child handle far-end-disconnected (sends on it then fail).</summary>
        public void MarkDisconnected(int handle) => server.MarkHandleDisconnected(handle);

        /// <inheritdoc/>
        public void Dispose()
        {
            tcp.Dispose();
            _writeLock.Dispose();
        }

        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    byte[]? frame = await ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                    if (frame is null)
                    {
                        return;
                    }

                    var message = JsonNode.Parse(frame)!.AsObject();
                    await HandleAsync(message).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Connection torn down — fine for a test double.
            }
        }

        /// <summary>Sends an async push (gets the per-connection seqno, never an id).</summary>
        public async Task PushAsync(JsonObject push)
        {
            push["seqno"] = _seqno++;
            await WriteAsync(push).ConfigureAwait(false);
        }

        private async Task HandleAsync(JsonObject request)
        {
            string type = request["type"]!.GetValue<string>();
            JsonNode? id = request["id"];
            int handle = request["handle"]?.GetValue<int>() ?? 0;

            switch (type)
            {
                case "auth":
                    await ReplyAsync("authReply", id, 0).ConfigureAwait(false);
                    break;

                case "socket":
                {
                    int allocated = server.NextHandle();
                    server.OnHandleOwned(allocated, this);
                    await ReplyAsync("socketReply", id, 0, allocated).ConfigureAwait(false);
                    break;
                }

                case "bind":
                    BoundLocal = request["local"]?.GetValue<string>();
                    server.Binds.Writer.TryWrite(new BindRecord(handle, BoundLocal, request["port"]?.GetValue<string>()));
                    await ReplyAsync("bindReply", id, 0, handle).ConfigureAwait(false);
                    break;

                case "listen":
                    ListenerHandle = handle;
                    server.OnListen(this);
                    await ReplyAsync("listenReply", id, 0, handle).ConfigureAwait(false);
                    break;

                case "open":
                {
                    server.CountOpenAttempt();
                    var record = new OpenRecord(
                        0,
                        request["local"]?.GetValue<string>(),
                        request["remote"]?.GetValue<string>(),
                        request["port"]?.GetValue<string>());
                    int errCode = server.OpenResult(record);
                    if (errCode != 0)
                    {
                        await ReplyAsync("openReply", id, errCode).ConfigureAwait(false);
                        break;
                    }

                    int child = server.NextHandle();
                    server.OnHandleOwned(child, this);
                    await ReplyAsync("openReply", id, 0, child).ConfigureAwait(false);
                    await PushAsync(new JsonObject { ["type"] = "status", ["handle"] = child, ["flags"] = 3 })
                        .ConfigureAwait(false);
                    server.Opens.Writer.TryWrite(record with { Handle = child });
                    break;
                }

                case "send":
                {
                    if (server.IsHandleDisconnected(handle))
                    {
                        // A write to a stream whose far end has dropped: errCode 17 "Not
                        // connected" (the real node's reply — RhpChildConnection surfaces it
                        // as a throw from SendAsync).
                        await ReplyAsync("sendReply", id, 17, handle, "Not connected").ConfigureAwait(false);
                        break;
                    }

                    byte[] data = FromWireString(request["data"]?.GetValue<string>() ?? "");
                    server.HostBytes(handle).Writer.TryWrite(data);
                    await ReplyAsync("sendReply", id, 0, handle).ConfigureAwait(false);
                    break;
                }

                case "close":
                    server.OnClosedByHost(handle);
                    await ReplyAsync("closeReply", id, 0, handle).ConfigureAwait(false);
                    break;

                default:
                    // Unknown type → `{type}Reply` errCode 2 (wire-fidelity row 5).
                    await ReplyAsync(type + "Reply", id, 2).ConfigureAwait(false);
                    break;
            }
        }

        private Task ReplyAsync(string type, JsonNode? id, int errCode, int? handle = null, string? errText = null)
        {
            var reply = new JsonObject
            {
                ["type"] = type,
                ["errCode"] = errCode,
                ["errText"] = errText ?? (errCode == 0 ? "Ok" : "Error"),
            };
            if (id is not null)
            {
                reply["id"] = id.DeepClone();
            }

            if (handle is not null)
            {
                reply["handle"] = handle.Value;
            }

            return WriteAsync(reply);
        }

        private async Task WriteAsync(JsonObject message)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString(
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            byte[] framed = new byte[payload.Length + 2];
            framed[0] = (byte)(payload.Length >> 8);
            framed[1] = (byte)payload.Length;
            payload.CopyTo(framed, 2);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(framed).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
        {
            byte[] header = new byte[2];
            if (!await FillAsync(stream, header, ct).ConfigureAwait(false))
            {
                return null;
            }

            int length = (header[0] << 8) | header[1];
            byte[] payload = new byte[length];
            return await FillAsync(stream, payload, ct).ConfigureAwait(false) ? payload : null;
        }

        private static async Task<bool> FillAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
                if (n == 0)
                {
                    return false;
                }

                read += n;
            }

            return true;
        }
    }

    /// <summary>Latin-1 wire string → bytes (wire-fidelity row 7).</summary>
    internal static byte[] FromWireString(string s)
    {
        byte[] bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            bytes[i] = (byte)s[i];
        }

        return bytes;
    }

    /// <summary>Bytes → Latin-1 wire string.</summary>
    internal static string ToWireString(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            sb.Append((char)b);
        }

        return sb.ToString();
    }
}

/// <summary>
/// The far station of one fake AX.25 stream: injects `recv`/server-`close` pushes toward
/// the host and reads what the host sent on the handle.
/// </summary>
internal sealed class FakeRhpPeer
{
    private readonly FakeRhpServer.Conn _conn;
    private readonly ChannelReader<byte[]> _fromHost;
    private readonly LineAssembler _lines = new();
    private readonly Queue<string> _pendingLines = new();
    private readonly List<byte> _pendingBytes = [];

    internal FakeRhpPeer(FakeRhpServer.Conn conn, int handle, string? local, string? remote, Channel<byte[]> fromHost)
    {
        _conn = conn;
        Handle = handle;
        Local = local;
        Remote = remote;
        _fromHost = fromHost.Reader;
    }

    /// <summary>The RHP handle of this stream.</summary>
    public int Handle { get; }

    /// <summary>The host-side callsign of the stream.</summary>
    public string? Local { get; }

    /// <summary>This peer's callsign.</summary>
    public string? Remote { get; }

    /// <summary>The node port the host's `open` named, when it named one.</summary>
    public string? Port { get; set; }

    /// <summary>Sends text to the host (a `recv` push).</summary>
    public Task SendTextAsync(string text) => SendBytesAsync(Encoding.Latin1.GetBytes(text));

    /// <summary>Sends a CR-terminated line to the host.</summary>
    public Task SendLineAsync(string line) => SendTextAsync(line + "\r");

    /// <summary>Sends raw bytes to the host (a `recv` push).</summary>
    public Task SendBytesAsync(byte[] data) => _conn.PushAsync(new JsonObject
    {
        ["type"] = "recv",
        ["handle"] = Handle,
        ["data"] = FakeRhpServer.ToWireString(data),
    });

    /// <summary>Announces a far-end disconnect (a server `close` push).</summary>
    public Task PushCloseAsync() => _conn.PushAsync(new JsonObject
    {
        ["type"] = "close",
        ["handle"] = Handle,
    });

    /// <summary>
    /// Simulates the far end dropping the AX.25 link without a tidy close: subsequent host
    /// sends on this handle are refused ("17 Not connected"). Models a LinBPQ that disconnects
    /// the instant it has received everything — before our closing <c>FQ</c> reaches the wire.
    /// </summary>
    public void MarkDisconnected() => _conn.MarkDisconnected(Handle);

    /// <summary>Non-blocking line read: drains whatever the host already sent.</summary>
    public bool TryReadLine(out string line)
    {
        while (_pendingLines.Count == 0 && _fromHost.TryRead(out byte[]? chunk))
        {
            foreach (string l in _lines.Feed(chunk))
            {
                _pendingLines.Enqueue(l);
            }
        }

        if (_pendingLines.Count > 0)
        {
            line = _pendingLines.Dequeue();
            return true;
        }

        line = "";
        return false;
    }

    /// <summary>Reads the next raw chunk the host sent (for binary transfer parsing).</summary>
    public Task<byte[]> ReadChunkRawAsync(TimeSpan? timeout = null) =>
        ReadChunkAsync(timeout ?? TestTimeout.Default);

    /// <summary>Feeds leftover bytes (the tail of a binary read) back into the line reader.</summary>
    public void PushBackForLines(ReadOnlySpan<byte> data)
    {
        foreach (string l in _lines.Feed(data))
        {
            _pendingLines.Enqueue(l);
        }
    }

    /// <summary>Reads the next line the host sent (CR/CRLF/LF tolerant).</summary>
    public async Task<string> ReadLineAsync(TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TestTimeout.Default;
        while (_pendingLines.Count == 0)
        {
            byte[] chunk = await ReadChunkAsync(limit).ConfigureAwait(false);
            foreach (string line in _lines.Feed(chunk))
            {
                _pendingLines.Enqueue(line);
            }
        }

        return _pendingLines.Dequeue();
    }

    /// <summary>
    /// Reads host bytes until <paramref name="complete"/> says the accumulated run is
    /// done (e.g. an FbbBlockReader reaching Complete), returning everything read.
    /// </summary>
    public async Task<byte[]> ReadBytesUntilAsync(Func<IReadOnlyList<byte>, bool> complete, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TestTimeout.Default;
        while (!complete(_pendingBytes))
        {
            byte[] chunk = await ReadChunkAsync(limit).ConfigureAwait(false);
            _pendingBytes.AddRange(chunk);
        }

        byte[] result = [.. _pendingBytes];
        _pendingBytes.Clear();
        return result;
    }

    /// <summary>Whether the host has closed the stream (drains nothing).</summary>
    public async Task WaitForHostCloseAsync(TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TestTimeout.Default;
        try
        {
            while (true)
            {
                byte[] chunk = await ReadChunkAsync(limit).ConfigureAwait(false);
                foreach (string line in _lines.Feed(chunk))
                {
                    _pendingLines.Enqueue(line); // tolerate trailing protocol lines
                }
            }
        }
        catch (ChannelClosedException)
        {
            // Closed — what we wanted.
        }
    }

    private async Task<byte[]> ReadChunkAsync(TimeSpan timeout) =>
        await _fromHost.ReadAsync().AsTask().WaitAsync(timeout).ConfigureAwait(false);
}

/// <summary>Default real-time guard for awaiting fake-server observations.</summary>
internal static class TestTimeout
{
    /// <summary>Generous enough for CI, far below the suite budget.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);
}
