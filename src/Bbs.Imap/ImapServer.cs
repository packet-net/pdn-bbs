using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Bbs.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Imap;

/// <summary>
/// The IMAP4rev1 TCP server: a <see cref="TcpListener"/> accept loop that spawns one
/// <see cref="ImapSession"/> per connection (mirroring the host's inbound-demux pattern — accept,
/// spawn a per-connection task, prune completed, <c>WhenAll</c> on cancel). Plaintext by default; when
/// TLS is enabled each accepted socket is wrapped in an <see cref="SslStream"/> with server auth before
/// the session runs. Default-off at the config layer: a node that does not enable IMAP never
/// constructs this.
/// </summary>
public sealed partial class ImapServer
{
    private readonly ImapServerOptions _options;
    private readonly ImapBackend _backend;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private X509Certificate2? _certificate;

    /// <summary>Creates the server over <paramref name="store"/> with the given <paramref name="options"/>.</summary>
    public ImapServer(ImapServerOptions options, BbsStore store, TimeProvider time, ILogger<ImapServer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _backend = new ImapBackend(store);
        _time = time;
        _logger = logger ?? NullLogger<ImapServer>.Instance;
    }

    /// <summary>
    /// The TCP port the listener actually bound. Equals <see cref="ImapServerOptions.Port"/>, except
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
            _certificate = ImapTlsCertificate.Resolve(
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

                using var connection = new ImapConnection(stream);
                var session = new ImapSession(connection, _backend, _options.IdlePollInterval, _logger);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "IMAP: TLS is enabled but no certificate could be resolved; the IMAP listener is not started.")]
    private static partial void LogTlsNoCert(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP listening on {Bind}:{Port} ({Mode})")]
    private static partial void LogListening(ILogger logger, string bind, int port, string mode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "IMAP connection ended with an I/O fault")]
    private static partial void LogConnectionFault(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP: a connection completed TCP but did not start TLS within the handshake timeout; closing (a client not configured for implicit SSL).")]
    private static partial void LogTlsHandshakeTimeout(ILogger logger);
}

/// <summary>
/// The IMAP server's runtime options — the host maps its <c>ImapConfig</c> onto this so the
/// <c>Bbs.Imap</c> library carries no dependency on the host's configuration types.
/// </summary>
public sealed record ImapServerOptions
{
    /// <summary>Bind address (default loopback).</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>TCP port; 0 binds an OS-assigned ephemeral port (used by tests). 993 = standard implicit-TLS IMAP.</summary>
    public int Port { get; init; } = 993;

    /// <summary>Whether each accepted socket is wrapped in implicit TLS.</summary>
    public bool TlsEnabled { get; init; }

    /// <summary>Operator-supplied PKCS#12 path; wins over self-signed when set.</summary>
    public string? CertificatePath { get; init; }

    /// <summary>Password for <see cref="CertificatePath"/>.</summary>
    public string? CertificatePassword { get; init; }

    /// <summary>Generate + persist a self-signed cert when no <see cref="CertificatePath"/> is given.</summary>
    public bool GenerateSelfSigned { get; init; } = true;

    /// <summary>Where a generated self-signed cert is persisted (under the state dir).</summary>
    public string SelfSignedCertPath { get; init; } = "imap-cert.pfx";

    /// <summary>
    /// How often an <c>IDLE</c>-ing session re-checks the selected folder for new mail to push
    /// (RFC 2177). The default trades a few seconds of worst-case new-mail latency for a single cheap
    /// store read per interval; tests inject a small value.
    /// </summary>
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait for a TLS ClientHello before closing a connection that completed TCP but never
    /// started the handshake (a plaintext/STARTTLS client mis-pointed at the implicit-TLS port). Bounds
    /// the deadlock to a fast close instead of an indefinite hang.
    /// </summary>
    public TimeSpan TlsHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
