using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Bbs.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Smtp;

/// <summary>
/// The SMTP submission TCP server (RFC 6409): a <see cref="TcpListener"/> accept loop that spawns one
/// <see cref="SmtpSession"/> per connection (mirroring the IMAP server's accept / per-connection-task /
/// prune-completed / <c>WhenAll</c>-on-cancel pattern). This is a <b>submission</b> server, not a relay:
/// a client must authenticate (a BBS callsign + its mail-password) before it may send, and the stored
/// message's From is always the authenticated callsign — never the trusted-blind MAIL FROM. A sent
/// message is stored as a <see cref="MessageType.Personal"/> and routed exactly like a webmail compose.
/// Plaintext by default; when TLS is enabled each accepted socket is wrapped in an
/// <see cref="SslStream"/> with server auth (implicit TLS, port 465) before the session runs.
/// The single server instance can additionally run a STARTTLS listener (port 587, the iOS-default
/// outgoing port): it accepts plaintext, advertises STARTTLS (but never AUTH) before the upgrade, and
/// on <c>STARTTLS</c> wraps the live connection in TLS server-side using the same certificate. Both
/// accept loops run concurrently from one <see cref="RunAsync"/> (one DI registration, since
/// <c>ComponentService&lt;T&gt;</c> de-dups by implementation type).
/// Default-off at the config layer: a node that does not enable SMTP never constructs this.
/// </summary>
public sealed partial class SmtpServer
{
    private readonly SmtpServerOptions _options;
    private readonly BbsStore _store;
    private readonly Action<Message> _onStored;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private X509Certificate2? _certificate;

    /// <summary>
    /// Creates the server over <paramref name="store"/> with the given <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The runtime options (bind, port, TLS, size cap).</param>
    /// <param name="store">The BBS store — for credential verification and message storage.</param>
    /// <param name="onStored">
    /// The post-store nudge: invoked once per stored message so the host can route it (wired to
    /// <c>RoutingService.RouteMessage</c> in composition). Keeps <c>Bbs.Smtp</c> host-agnostic — the
    /// library never references the host's routing types.
    /// </param>
    /// <param name="time">Clock (drives the self-signed cert validity window).</param>
    /// <param name="logger">Logs listen/auth/connection events; null = no-op.</param>
    public SmtpServer(
        SmtpServerOptions options, BbsStore store, Action<Message> onStored, TimeProvider time, ILogger<SmtpServer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(onStored);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _store = store;
        _onStored = onStored;
        _time = time;
        _logger = logger ?? NullLogger<SmtpServer>.Instance;
    }

    /// <summary>
    /// The TCP port the implicit-TLS listener actually bound. Equals <see cref="SmtpServerOptions.Port"/>,
    /// except when that is 0 (an ephemeral port for tests) — then it is the OS-assigned port, available
    /// once the listener has started. 0 while not bound (or when implicit TLS is off and no plaintext
    /// listener runs).
    /// </summary>
    public int BoundPort { get; private set; }

    /// <summary>
    /// The TCP port the STARTTLS listener actually bound, or 0 when the STARTTLS listener is not running.
    /// Equals <see cref="SmtpServerOptions.StartTlsPort"/>, except when binding an ephemeral port for tests
    /// (<see cref="SmtpServerOptions.StartTlsEphemeral"/>) — then it is the OS-assigned port, available once
    /// the listener has started.
    /// </summary>
    public int BoundStartTlsPort { get; private set; }

    /// <summary>Whether the STARTTLS listener is wanted (a non-zero port, or the test ephemeral flag).</summary>
    private bool StartTlsWanted => _options.StartTlsPort > 0 || _options.StartTlsEphemeral;

    /// <summary>
    /// Runs BOTH accept loops to completion (implicit TLS on <see cref="SmtpServerOptions.Port"/> when
    /// <see cref="SmtpServerOptions.TlsEnabled"/>, and STARTTLS on
    /// <see cref="SmtpServerOptions.StartTlsPort"/> when wanted), until <paramref name="cancellationToken"/>
    /// fires. The certificate is resolved once up front whenever EITHER listener needs TLS; if no cert can
    /// be produced the implicit listener returns (its existing clean opt-out) and the STARTTLS listener is
    /// skipped — never a crash. A STARTTLS port with no cert is logged and skipped while the implicit
    /// listener (if any) still runs.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Resolve the cert once if EITHER path needs TLS — both listeners share it.
        bool tlsNeeded = _options.TlsEnabled || StartTlsWanted;
        if (tlsNeeded)
        {
            _certificate = SmtpTlsCertificate.Resolve(
                _options.CertificatePath, _options.CertificatePassword, _options.GenerateSelfSigned,
                _options.SelfSignedCertPath, _options.Bind, _time, _logger);
        }

        // Implicit-TLS demands a cert; with none, that listener cannot run (today's clean opt-out). The
        // STARTTLS listener may still come up only if the cert resolved — see below.
        if (_options.TlsEnabled && _certificate is null)
        {
            LogTlsNoCert(_logger);
            return;
        }

        var loops = new List<Task>();

        // Implicit-TLS (or plaintext-when-TlsEnabled-is-false) listener on the primary port.
        TcpListener implicitListener = StartListener(_options.Port);
        BoundPort = ((IPEndPoint)implicitListener.LocalEndpoint).Port;
        LogListening(_logger, _options.Bind, BoundPort, _options.TlsEnabled ? "implicit TLS" : "plaintext");
        loops.Add(AcceptLoopAsync(implicitListener, SmtpSessionMode.Implicit, cancellationToken));

        // STARTTLS listener (starts plaintext, upgrades in-band). Requires a cert; with none, skip it but
        // leave the implicit listener running.
        TcpListener? startTlsListener = null;
        if (StartTlsWanted)
        {
            if (_certificate is null)
            {
                LogStartTlsNoCert(_logger);
            }
            else
            {
                startTlsListener = StartListener(_options.StartTlsEphemeral ? 0 : _options.StartTlsPort);
                BoundStartTlsPort = ((IPEndPoint)startTlsListener.LocalEndpoint).Port;
                LogListening(_logger, _options.Bind, BoundStartTlsPort, "STARTTLS");
                loops.Add(AcceptLoopAsync(startTlsListener, SmtpSessionMode.StartTls, cancellationToken));
            }
        }

        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        finally
        {
            implicitListener.Stop();
            startTlsListener?.Stop();
            _certificate?.Dispose();
        }
    }

    /// <summary>Binds and starts a TCP listener on <see cref="SmtpServerOptions.Bind"/>:<paramref name="port"/>.</summary>
    private TcpListener StartListener(int port)
    {
        var listener = new TcpListener(IPAddress.Parse(_options.Bind), port);
        listener.Start();
        return listener;
    }

    /// <summary>
    /// One listener's accept loop: spawns a per-connection task in <paramref name="mode"/> for each accepted
    /// socket, prunes completed sessions, and awaits all in-flight sessions on shutdown. Mirrors the single
    /// loop the server ran before STARTTLS, now parameterised by mode so both listeners share it.
    /// </summary>
    private async Task AcceptLoopAsync(TcpListener listener, SmtpSessionMode mode, CancellationToken cancellationToken)
    {
        var sessions = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket socket = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
                sessions.RemoveAll(t => t.IsCompleted);
                sessions.Add(HandleConnectionAsync(socket, mode, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }

        await Task.WhenAll(sessions).ConfigureAwait(false);
    }

    /// <summary>
    /// One connection's whole lifetime. For <see cref="SmtpSessionMode.Implicit"/> the socket is wrapped in
    /// TLS (the existing handshake-timeout-bounded path) before the session runs. For
    /// <see cref="SmtpSessionMode.StartTls"/> the session runs over plaintext and performs the in-band TLS
    /// upgrade itself (it holds the cert + timeout); the connection owns and disposes the SslStream it
    /// creates. Never throws.
    /// </summary>
    private async Task HandleConnectionAsync(Socket socket, SmtpSessionMode mode, CancellationToken cancellationToken)
    {
        try
        {
            using var networkStream = new NetworkStream(socket, ownsSocket: true);
            Stream stream = networkStream;
            SslStream? ssl = null;
            try
            {
                if (mode == SmtpSessionMode.Implicit && _options.TlsEnabled && _certificate is not null)
                {
                    ssl = new SslStream(networkStream, leaveInnerStreamOpen: false);

                    // Bound the handshake: a peer that completes the TCP connect but never sends a TLS
                    // ClientHello (a plaintext/STARTTLS client mis-pointed at this implicit-TLS port — e.g.
                    // an iOS Mail account-verify probe) would otherwise leave both sides waiting forever
                    // (ESTABLISHED, 0 bytes). Fail fast and close so the client moves on, never deadlock.
                    using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    handshakeCts.CancelAfter(_options.TlsHandshakeTimeout);
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificate = _certificate,
                                ClientCertificateRequired = false,
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            },
                            handshakeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        LogTlsHandshakeTimeout(_logger);
                        return;
                    }

                    stream = ssl;
                }

                using var connection = new SmtpConnection(stream, _options.MaxMessageBytes);

                // The StartTls session is handed the cert + handshake timeout so it can perform the in-band
                // upgrade on STARTTLS (the Implicit session is already secure and never upgrades).
                var session = mode == SmtpSessionMode.StartTls
                    ? new SmtpSession(connection, _store, _onStored, _options.MaxMessageBytes, mode, _certificate, _options.TlsHandshakeTimeout, _logger)
                    : new SmtpSession(connection, _store, _onStored, _options.MaxMessageBytes, _logger);
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Dispose the implicit-path SslStream we created here; the StartTls path's SslStream is owned
                // and disposed by the SmtpConnection (it swaps its own inner stream on upgrade).
                if (ssl is not null)
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or EndOfStreamException or InvalidOperationException)
        {
            LogConnectionFault(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "SMTP: TLS is enabled but no certificate could be resolved; the SMTP listener is not started.")]
    private static partial void LogTlsNoCert(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "SMTP: a STARTTLS port is configured but no certificate could be resolved; the STARTTLS listener is not started (the implicit-TLS listener, if any, still runs).")]
    private static partial void LogStartTlsNoCert(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP submission listening on {Bind}:{Port} ({Mode})")]
    private static partial void LogListening(ILogger logger, string bind, int port, string mode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SMTP connection ended with an I/O fault")]
    private static partial void LogConnectionFault(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP: a connection completed TCP but did not start TLS within the handshake timeout; closing (a client not configured for implicit SSL).")]
    private static partial void LogTlsHandshakeTimeout(ILogger logger);
}

/// <summary>
/// The SMTP submission server's runtime options — the host maps its <c>SmtpConfig</c> onto this so the
/// <c>Bbs.Smtp</c> library carries no dependency on the host's configuration types.
/// </summary>
public sealed record SmtpServerOptions
{
    /// <summary>Bind address (default loopback).</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>TCP port; 0 binds an OS-assigned ephemeral port (used by tests). 465 = implicit-TLS submission.</summary>
    public int Port { get; init; } = 465;

    /// <summary>
    /// The STARTTLS submission port; 0 disables the STARTTLS listener. 587 is the IANA submission port and
    /// the one iPhone Mail's "Add Mail Account" flow auto-probes for outgoing (STARTTLS, no port field), so
    /// offering it lets that default flow succeed unaided. The STARTTLS listener starts plaintext and the
    /// client upgrades to TLS in-band; it shares the same resolved certificate as the implicit-TLS path. As
    /// with <see cref="Port"/>, 0 here binds an OS-assigned ephemeral port only when set to a non-zero
    /// sentinel by tests — a literal 0 simply turns the listener off.
    /// </summary>
    public int StartTlsPort { get; init; }

    /// <summary>
    /// Test-only escape hatch: bind the STARTTLS listener on an OS-assigned ephemeral port even though
    /// <see cref="StartTlsPort"/> is 0 (a literal 0 otherwise turns the listener off). The harness sets this
    /// so it can run BOTH the implicit listener and the STARTTLS listener on distinct ephemeral ports in one
    /// process; production never sets it (it picks a real port via <see cref="StartTlsPort"/>).
    /// </summary>
    public bool StartTlsEphemeral { get; init; }

    /// <summary>Whether each accepted socket on <see cref="Port"/> is wrapped in implicit TLS.</summary>
    public bool TlsEnabled { get; init; }

    /// <summary>Operator-supplied PKCS#12 path; wins over self-signed when set.</summary>
    public string? CertificatePath { get; init; }

    /// <summary>Password for <see cref="CertificatePath"/>.</summary>
    public string? CertificatePassword { get; init; }

    /// <summary>Generate + persist a self-signed cert when no <see cref="CertificatePath"/> is given.</summary>
    public bool GenerateSelfSigned { get; init; } = true;

    /// <summary>Where a generated self-signed cert is persisted (under the state dir).</summary>
    public string SelfSignedCertPath { get; init; } = "smtp-cert.pfx";

    /// <summary>
    /// How long to wait for a TLS ClientHello before closing a connection that completed TCP but never
    /// started the handshake (a plaintext/STARTTLS client mis-pointed at the implicit-TLS port). Bounds
    /// the deadlock to a fast close instead of an indefinite hang.
    /// </summary>
    public TimeSpan TlsHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The largest DATA payload (the whole MIME message, after dot-unstuffing) the server will accept,
    /// in bytes; advertised in the EHLO <c>SIZE</c> extension and enforced by the DATA reader. Packet
    /// mail is small; this is a generous safety cap (default 25 MiB) so a runaway client can't drive the
    /// session out of memory.
    /// </summary>
    public int MaxMessageBytes { get; init; } = 25 * 1024 * 1024;
}
