using Bbs.Core;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Bbs.Smtp.Tests;

/// <summary>
/// Spins a real <see cref="SmtpServer"/> on <c>127.0.0.1</c> (ephemeral ports) over a caller-supplied
/// store, captures the <c>onStored</c> nudges, and hands out connected MailKit
/// <see cref="SmtpClient"/>s — MailKit being the strict correctness oracle (it throws on any malformed
/// response). In-process over loopback, like the IMAP suite's harness, so it runs in normal CI.
/// </summary>
/// <remarks>
/// Two start modes:
/// <list type="bullet">
/// <item><see cref="StartAsync"/> — a single plaintext listener (no TLS, no STARTTLS) on an ephemeral
///   port. The original SMTP-suite harness; keeps the existing tests untouched.</item>
/// <item><see cref="StartWithTlsAsync"/> — an implicit-TLS listener AND a STARTTLS listener, each on its
///   own ephemeral port, sharing a generated self-signed cert. Exposes both bound ports.</item>
/// </list>
/// </remarks>
internal sealed class SmtpServerHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _serverTask;
    private readonly SmtpServer _server;
    private readonly DirectoryInfo? _certDir;

    private SmtpServerHarness(
        SmtpServer server, Task serverTask, CancellationTokenSource cts, IReadOnlyList<Message> stored, DirectoryInfo? certDir)
    {
        _server = server;
        _serverTask = serverTask;
        _cts = cts;
        _certDir = certDir;
        Stored = stored;
    }

    /// <summary>The TCP port the implicit (or plaintext) listener bound.</summary>
    public int Port => _server.BoundPort;

    /// <summary>The TCP port the STARTTLS listener bound (0 when this harness has no STARTTLS listener).</summary>
    public int StartTlsPort => _server.BoundStartTlsPort;

    /// <summary>Every message the server reported via the <c>onStored</c> callback, in order.</summary>
    public IReadOnlyList<Message> Stored { get; }

    /// <summary>Starts a plaintext server over <paramref name="store"/> and waits until it has bound a port.</summary>
    public static async Task<SmtpServerHarness> StartAsync(BbsStore store, TimeProvider time)
    {
        var stored = new List<Message>();
        var options = new SmtpServerOptions { Bind = "127.0.0.1", Port = 0, TlsEnabled = false };
        var server = new SmtpServer(options, store, msg => stored.Add(msg), time);
        var cts = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cts.Token);

        await WaitForBindAsync(server, requireStartTls: false, cts).ConfigureAwait(false);
        return new SmtpServerHarness(server, serverTask, cts, stored, certDir: null);
    }

    /// <summary>
    /// Starts a server with BOTH an implicit-TLS listener and a STARTTLS listener, each on its own
    /// ephemeral port, sharing a generated self-signed cert. Waits until both have bound.
    /// </summary>
    public static async Task<SmtpServerHarness> StartWithTlsAsync(BbsStore store, TimeProvider time)
    {
        var stored = new List<Message>();
        var certDir = Directory.CreateTempSubdirectory("bbs-smtp-harness-cert-");
        var options = new SmtpServerOptions
        {
            Bind = "127.0.0.1",
            Port = 0,                 // ephemeral implicit-TLS port
            StartTlsEphemeral = true, // ephemeral STARTTLS port (StartTlsPort stays 0)
            TlsEnabled = true,
            GenerateSelfSigned = true,
            SelfSignedCertPath = Path.Combine(certDir.FullName, "smtp-cert.pfx"),
        };
        var server = new SmtpServer(options, store, msg => stored.Add(msg), time);
        var cts = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cts.Token);

        await WaitForBindAsync(server, requireStartTls: true, cts).ConfigureAwait(false);
        return new SmtpServerHarness(server, serverTask, cts, stored, certDir);
    }

    /// <summary>Connects a MailKit client to the plaintext/implicit listener (no STARTTLS).</summary>
    public async Task<SmtpClient> ConnectAsync()
    {
        var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", Port, SecureSocketOptions.None).ConfigureAwait(false);
        return client;
    }

    /// <summary>Connects a MailKit client to the STARTTLS listener (upgrades in band), accepting the self-signed cert.</summary>
    public async Task<SmtpClient> ConnectStartTlsAsync()
    {
        var client = NewCertAcceptingClient();
        await client.ConnectAsync("127.0.0.1", StartTlsPort, SecureSocketOptions.StartTls).ConfigureAwait(false);
        return client;
    }

    private static SmtpClient NewCertAcceptingClient()
    {
        var client = new SmtpClient();

        // The generated cert is self-signed/untrusted; accept it for THIS loopback test (the channel is
        // still TLS-encrypted — we are proving the protocol, not certificate trust). Mirrors SmtpTlsTests.
#pragma warning disable CA5359 // accept-any cert is intentional for a self-signed loopback test
        client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        return client;
    }

    private static async Task WaitForBindAsync(SmtpServer server, bool requireStartTls, CancellationTokenSource cts)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while ((server.BoundPort == 0 || (requireStartTls && server.BoundStartTlsPort == 0)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        if (server.BoundPort == 0 || (requireStartTls && server.BoundStartTlsPort == 0))
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
            throw new InvalidOperationException("SMTP server did not bind its port(s) in time.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _cts.Dispose();
        _certDir?.Delete(recursive: true);
    }
}
