using System.Net.Sockets;
using Bbs.Core;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Bbs.Smtp.Tests;

/// <summary>
/// Exercises the implicit-TLS path end to end: a server with a generated self-signed cert, a MailKit
/// client connecting over SSL (accepting the untrusted self-signed cert via a callback), and an
/// authenticated send. Plus the handshake-timeout guard: a plaintext client on the TLS port must be
/// closed fast, not left hanging. Mirrors the IMAP suite's ImapTlsTests.
/// </summary>
public sealed class SmtpTlsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bbs-smtp-tls-test-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task ImplicitTls_GeneratesCert_AndSendsOverSsl()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "tls passphrase");

        var stored = new List<Message>();
        string certPath = Path.Combine(_dir.FullName, "smtp-cert.pfx");
        var server = new SmtpServer(
            new SmtpServerOptions
            {
                Bind = "127.0.0.1",
                Port = 0,
                TlsEnabled = true,
                GenerateSelfSigned = true,
                SelfSignedCertPath = certPath,
            },
            test.Store,
            msg => stored.Add(msg),
            test.Time);

        using var cts = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cts.Token);
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (server.BoundPort == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.NotEqual(0, server.BoundPort);
            Assert.True(File.Exists(certPath)); // the self-signed cert was generated and persisted

            using var client = new SmtpClient();

            // The generated cert is self-signed/untrusted; accept it for THIS loopback test (the channel
            // is still TLS-encrypted — we are proving the handshake, not certificate trust).
#pragma warning disable CA5359 // accept-any cert is intentional for a self-signed loopback test
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
            await client.ConnectAsync("127.0.0.1", server.BoundPort, SecureSocketOptions.SslOnConnect);
            Assert.True(client.IsSecure);

            await client.AuthenticateAsync("M0LTE", "tls passphrase");

            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
            message.To.Add(MailboxAddress.Parse("M0LTE@pdn"));
            message.Subject = "over tls";
            message.Body = new TextPart("plain") { Text = "secret body" };
            await client.SendAsync(message);

            await client.DisconnectAsync(quit: true);

            Message m = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
            Assert.Equal("over tls", m.Subject);
            Assert.Single(stored);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    [Fact]
    public async Task PlaintextClientOnTlsPort_IsClosedFast_NotLeftHanging()
    {
        // A plaintext/STARTTLS client mis-pointed at the implicit-TLS port completes TCP and then waits
        // for a server greeting that never comes, while the server waits for a ClientHello — an
        // indefinite deadlock. The handshake timeout must turn that into a fast close so neither hangs.
        using var test = new TestStore();
        string certPath = Path.Combine(_dir.FullName, "smtp-cert.pfx");
        var server = new SmtpServer(
            new SmtpServerOptions
            {
                Bind = "127.0.0.1",
                Port = 0,
                TlsEnabled = true,
                GenerateSelfSigned = true,
                SelfSignedCertPath = certPath,
                TlsHandshakeTimeout = TimeSpan.FromMilliseconds(500),
            },
            test.Store,
            static _ => { },
            test.Time);

        using var cts = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cts.Token);
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (server.BoundPort == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.NotEqual(0, server.BoundPort);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", server.BoundPort);
            using NetworkStream ns = tcp.GetStream();

            // Send no ClientHello. The server must close within the handshake timeout: a read returns 0
            // (graceful close) or faults (reset) — both mean "closed". A hang would time out the wait.
            var buf = new byte[16];
            int read;
            try
            {
                read = await ns.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (IOException)
            {
                read = 0; // connection reset is also a close, not a hang
            }

            Assert.Equal(0, read);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }
}
