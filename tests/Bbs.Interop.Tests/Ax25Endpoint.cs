using System.Net;
using System.Threading.Channels;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Kiss;

namespace Bbs.Interop.Tests;

/// <summary>
/// One AX.25 station attached to a frame transport, built from the packet.net libraries:
/// an <see cref="IAx25Transport"/> — <see cref="KissTcpClient"/> (KISS-TCP to netsim, the
/// LinBPQ oracle) or <see cref="AxudpSocketTransport"/> (AXUDP to the real-F6FBB VM) —
/// fronting an <see cref="Ax25Listener"/>, the same engine a real pdn node fronts for the
/// BBS. Surfaces sessions as <see cref="Ax25ByteSession"/>, a byte-stream handle shaped like
/// the host's RhpChildConnection so the FBB pump transcribes 1:1.
/// </summary>
internal sealed class Ax25Endpoint : IAsyncDisposable
{
    /// <summary>
    /// Per-SendData chunk cap (instance, set per transport). The engine rejects an over-N1
    /// (256) payload on a link without the segmenter; netsim runs PACLEN 120, F6FBB 236 —
    /// feed the I-frame queue in chunks the channel is sized for. FBB is a byte stream;
    /// frame boundaries are irrelevant to it.
    /// </summary>
    private readonly int _chunkSize;

    private readonly IAx25Transport _transport;
    private readonly string _myCall;

    /// <summary>null = use the listener's PreferExtendedConnect default (KISS/LinBPQ path,
    /// byte-identical to before); false = force a v2.0 SABM (mod-8) connect (AXUDP/F6FBB:
    /// xfbbd over kernel-AX.25 answers SABM, as the proven linbpq→Q0FBB-1 path did).</summary>
    private readonly bool? _extended;

    private readonly Ax25Listener _listener;
    private readonly Channel<Ax25ByteSession> _accepted = Channel.CreateUnbounded<Ax25ByteSession>();
    private readonly Dictionary<Ax25Session, Ax25ByteSession> _current = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    private Ax25Endpoint(IAx25Transport transport, string myCall, int chunkSize, bool? extended)
    {
        _transport = transport;
        _myCall = myCall;
        _chunkSize = chunkSize;
        _extended = extended;
        _listener = new Ax25Listener(transport, new Ax25ListenerOptions
        {
            MyCall = Callsign.Parse(myCall),
            // ConfigureSession runs before any event flows into a newly built session
            // (inbound SABM included), so the signal tap can never miss bytes.
            ConfigureSession = session => session.DataLinkSignalEmitted +=
                (_, signal) => OnSignal(session, signal),
        });
    }

    /// <summary>Connects KISS-TCP (netsim node a, PACLEN 120) and starts the inbound pump.</summary>
    public static async Task<Ax25Endpoint> AttachAsync(
        string host, int port, string myCall, CancellationToken cancellationToken)
    {
        KissTcpClient kiss = await KissTcpClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var endpoint = new Ax25Endpoint(kiss, myCall, chunkSize: 120, extended: null);
        await endpoint._listener.StartAsync(cancellationToken).ConfigureAwait(false);
        return endpoint;
    }

    /// <summary>
    /// Binds an AXUDP transport (every frame → <paramref name="remote"/>, receive on
    /// <paramref name="localPort"/>) and starts the listener — the real-F6FBB VM path
    /// (host 192.168.76.1 ↔ VM 192.168.76.2:10093 over the f6fbbr0 bridge). Forces v2.0
    /// SABM and chunks I-frames to F6FBB's PACLEN (236).
    /// </summary>
    public static async Task<Ax25Endpoint> AttachAxudpAsync(
        IPEndPoint remote, int localPort, string myCall, CancellationToken cancellationToken)
    {
        var transport = new AxudpSocketTransport(remote, localPort);
        var endpoint = new Ax25Endpoint(transport, myCall, chunkSize: 236, extended: false);
        await endpoint._listener.StartAsync(cancellationToken).ConfigureAwait(false);
        return endpoint;
    }

    /// <summary>Dials <paramref name="remote"/>; returns the byte-stream handle once connected.</summary>
    public async Task<Ax25ByteSession> ConnectAsync(string remote, CancellationToken cancellationToken)
    {
        Callsign r = Callsign.Parse(remote);
        Ax25Session session = _extended is bool ext
            ? await _listener.ConnectAsync(r, Callsign.Parse(_myCall), ext, cancellationToken).ConfigureAwait(false)
            : await _listener.ConnectAsync(r, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return Handle(session);
        }
    }

    /// <summary>
    /// Awaits the next inbound link-up (DL-CONNECT-indication — the UA has gone out).
    /// </summary>
    public async Task<Ax25ByteSession> AcceptAsync(CancellationToken cancellationToken) =>
        await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Sends bytes on the session, chunked to the channel's frame size.</summary>
    internal void Send(Ax25Session session, ReadOnlyMemory<byte> data)
    {
        for (int offset = 0; offset < data.Length; offset += _chunkSize)
        {
            int length = Math.Min(_chunkSize, data.Length - offset);
            _listener.SendData(session, data.Slice(offset, length));
        }
    }

    /// <summary>
    /// Requests link teardown (DL-DISCONNECT-request) and waits briefly for the
    /// handshake; tolerates a peer that beat us to the DISC or simply vanished.
    /// </summary>
    internal static async Task CloseAsync(Ax25Session session, CancellationToken cancellationToken)
    {
        if (session.CurrentState == "Disconnected")
        {
            return;
        }

        session.PostEvent(new DlDisconnectRequest());
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (session.CurrentState != "Disconnected" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Tear live links down before vanishing: a listener that just disappears leaves the
        // peer holding a dead session, which can poison the next test.
        bool anyLive = false;
        foreach (Packet.Ax25.Session.Ax25Session session in _listener.ActiveSessions)
        {
            if (session.CurrentState != "Disconnected")
            {
                session.PostEvent(new DlDisconnectRequest());
                anyLive = true;
            }
        }

        if (anyLive)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false); // let the DISC/UA hit the wire
        }

        await _listener.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSignal(Ax25Session session, DataLinkSignal signal)
    {
        // Raised synchronously from the listener's inbound pump (and, for confirms, the
        // ConnectAsync path) — keep it allocation-light and route by session identity.
        lock (_gate)
        {
            switch (signal)
            {
                case DataLinkConnectIndication:
                    // A fresh inbound connect (incl. a peer re-dialling a cached session).
                    _accepted.Writer.TryWrite(Reset(session));
                    break;

                case DataLinkConnectConfirm:
                    // Outbound link-up: fresh byte stream; ConnectAsync hands it out.
                    Reset(session);
                    break;

                case DataLinkDataIndication data:
                    Handle(session).Push(data.Info.ToArray());
                    break;

                case DataLinkDisconnectIndication:
                case DataLinkDisconnectConfirm:
                    Handle(session).Complete();
                    break;

                default:
                    break;
            }
        }
    }

    private Ax25ByteSession Handle(Ax25Session session)
    {
        if (!_current.TryGetValue(session, out Ax25ByteSession? handle))
        {
            handle = new Ax25ByteSession(this, session);
            _current[session] = handle;
        }

        return handle;
    }

    private Ax25ByteSession Reset(Ax25Session session)
    {
        if (_current.TryGetValue(session, out Ax25ByteSession? stale))
        {
            stale.Complete();
        }

        var fresh = new Ax25ByteSession(this, session);
        _current[session] = fresh;
        return fresh;
    }
}

/// <summary>
/// One AX.25 connection lifecycle as a byte stream — the same shape as the host's
/// RhpChildConnection (<c>ReceiveAsync</c> null-on-close, <c>SendAsync</c>,
/// <c>CloseAsync</c>, <c>RemoteCallsign</c>) so the FBB session pump is a direct
/// transcription of FbbSessionRunner.
/// </summary>
internal sealed class Ax25ByteSession : Bbs.Host.Rhp.IFbbConnection
{
    private readonly Ax25Endpoint _endpoint;
    private readonly Ax25Session _session;
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    internal Ax25ByteSession(Ax25Endpoint endpoint, Ax25Session session)
    {
        _endpoint = endpoint;
        _session = session;
    }

    /// <summary>The far station's callsign (SSID included).</summary>
    public string RemoteCallsign => _session.Context.Remote.ToString();

    /// <summary>Awaits the next inbound chunk; null when the link has closed.</summary>
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Sends bytes to the far station.</summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _endpoint.Send(_session, data);
        return Task.CompletedTask;
    }

    /// <summary>Tears the link down (best-effort, tolerant of a peer-initiated DISC).</summary>
    public Task CloseAsync(CancellationToken cancellationToken) =>
        Ax25Endpoint.CloseAsync(_session, cancellationToken);

    internal void Push(byte[] data) => _inbound.Writer.TryWrite(data);

    internal void Complete() => _inbound.Writer.TryComplete();
}
