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
    /// The TCP port the listener actually bound. Equals <see cref="SmtpServerOptions.Port"/>, except
    /// when that is 0 (an ephemeral port for tests) — then it is the OS-assigned port, available once
    /// the listener has started.
    /// </summary>
    public int BoundPort { get; private set; }

    /// <summary>
    /// Runs the accept loop until <paramref name="cancellationToken"/> fires. Resolves the TLS cert
    /// up front (when TLS is on); if the cert can't be produced the TLS listener is not started and the
    /// method returns without binding — a clean opt-out, never a crash.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_options.TlsEnabled)
        {
            _certificate = SmtpTlsCertificate.Resolve(
                _options.CertificatePath, _options.CertificatePassword, _options.GenerateSelfSigned,
                _options.SelfSignedCertPath, _options.Bind, _time, _logger);
            if (_certificate is null)
            {
                LogTlsNoCert(_logger);
                return;
            }
        }

        var listener = new TcpListener(IPAddress.Parse(_options.Bind), _options.Port);
        listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        LogListening(_logger, _options.Bind, BoundPort, _options.TlsEnabled ? "implicit TLS" : "plaintext");

        var sessions = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket socket = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
                sessions.RemoveAll(t => t.IsCompleted);
                sessions.Add(HandleConnectionAsync(socket, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        finally
        {
            listener.Stop();
            _certificate?.Dispose();
        }

        await Task.WhenAll(sessions).ConfigureAwait(false);
    }

    /// <summary>One connection's whole lifetime: optional TLS handshake, then the session. Never throws.</summary>
    private async Task HandleConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        try
        {
            using var networkStream = new NetworkStream(socket, ownsSocket: true);
            Stream stream = networkStream;
            SslStream? ssl = null;
            try
            {
                if (_options.TlsEnabled && _certificate is not null)
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
                var session = new SmtpSession(connection, _store, _onStored, _options.MaxMessageBytes, _logger);
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
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

    /// <summary>Whether each accepted socket is wrapped in implicit TLS.</summary>
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
