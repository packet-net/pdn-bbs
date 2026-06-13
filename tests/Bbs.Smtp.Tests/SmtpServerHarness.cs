using Bbs.Core;
using MailKit.Net.Smtp;

namespace Bbs.Smtp.Tests;

/// <summary>
/// Spins a real <see cref="SmtpServer"/> on <c>127.0.0.1:0</c> (an ephemeral port, plaintext) over a
/// caller-supplied store, captures the <c>onStored</c> nudges, and hands out connected MailKit
/// <see cref="SmtpClient"/>s — MailKit being the strict correctness oracle (it throws on any malformed
/// response). In-process over loopback, like the IMAP suite's harness, so it runs in normal CI.
/// </summary>
internal sealed class SmtpServerHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _serverTask;
    private readonly SmtpServer _server;

    private SmtpServerHarness(SmtpServer server, Task serverTask, CancellationTokenSource cts, IReadOnlyList<Message> stored)
    {
        _server = server;
        _serverTask = serverTask;
        _cts = cts;
        Stored = stored;
    }

    /// <summary>The TCP port the server bound.</summary>
    public int Port => _server.BoundPort;

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

        // Wait for the listener to bind (BoundPort becomes non-zero once Start() ran).
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (server.BoundPort == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        if (server.BoundPort == 0)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
            throw new InvalidOperationException("SMTP server did not bind a port in time.");
        }

        return new SmtpServerHarness(server, serverTask, cts, stored);
    }

    /// <summary>Connects a MailKit client to the server (plaintext, no STARTTLS).</summary>
    public async Task<SmtpClient> ConnectAsync()
    {
        var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", Port, MailKit.Security.SecureSocketOptions.None).ConfigureAwait(false);
        return client;
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
    }
}
