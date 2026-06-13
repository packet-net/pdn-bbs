using System.Net.Sockets;
using System.Text;
using Bbs.Core;

namespace Bbs.Smtp.Tests;

/// <summary>
/// Protocol-level tests driven over a raw TCP client, for behaviours MailKit's high-level API will not
/// produce — chiefly issuing MAIL/RCPT/DATA before AUTH (MailKit refuses to send unauthenticated on a
/// server that requires auth, so the no-open-relay rejection must be exercised at the wire). Confirms
/// the EHLO advertisement lines and that mail commands before AUTH are rejected with 530.
/// </summary>
public sealed class RawSmtpProtocolTests
{
    [Fact]
    public async Task Ehlo_AdvertisesAuthSizeAnd8BitMime()
    {
        using var test = new TestStore();
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        await using var raw = await RawSmtpClient.ConnectAsync(harness.Port);
        string ehlo = await raw.CommandAsync("EHLO test.example");
        Assert.Contains("AUTH PLAIN LOGIN", ehlo, StringComparison.Ordinal);
        Assert.Contains("8BITMIME", ehlo, StringComparison.Ordinal);
        Assert.Contains("SIZE ", ehlo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MailBeforeAuth_IsRejected_530()
    {
        using var test = new TestStore();
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        await using var raw = await RawSmtpClient.ConnectAsync(harness.Port);
        await raw.CommandAsync("EHLO test.example");
        string mail = await raw.CommandAsync("MAIL FROM:<M0LTE@pdn>");
        Assert.StartsWith("530", mail, StringComparison.Ordinal);

        // Nothing was stored and no nudge fired.
        Assert.Empty(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Assert.Empty(harness.Stored);
    }

    [Fact]
    public async Task DataBeforeAuth_IsRejected_530()
    {
        using var test = new TestStore();
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        await using var raw = await RawSmtpClient.ConnectAsync(harness.Port);
        await raw.CommandAsync("EHLO test.example");
        string data = await raw.CommandAsync("DATA");
        Assert.StartsWith("530", data, StringComparison.Ordinal);
        Assert.Empty(harness.Stored);
    }

    [Fact]
    public async Task RcptToUndecodableAddress_IsRejected_550()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "raw password");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        await using var raw = await RawSmtpClient.ConnectAsync(harness.Port);
        await raw.CommandAsync("EHLO test.example");

        // AUTH PLAIN with an inline SASL initial response: base64(\0 M0LTE \0 raw password).
        string ir = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0M0LTE\0raw password"));
        string auth = await raw.CommandAsync($"AUTH PLAIN {ir}");
        Assert.StartsWith("235", auth, StringComparison.Ordinal);

        await raw.CommandAsync("MAIL FROM:<M0LTE@pdn>");
        // A genuine external address is not in our scheme, so it does not decode → 550.
        string rcpt = await raw.CommandAsync("RCPT TO:<someone@gmail.com>");
        Assert.StartsWith("550", rcpt, StringComparison.Ordinal);
    }
}

/// <summary>A minimal raw SMTP client: sends a command line and reads the (possibly multiline) reply.</summary>
internal sealed class RawSmtpClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _buffer = new byte[16384];
    private readonly StringBuilder _pending = new();

    private RawSmtpClient(TcpClient tcp, NetworkStream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public static async Task<RawSmtpClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
        var client = new RawSmtpClient(tcp, tcp.GetStream());
        await client.ReadReplyAsync().ConfigureAwait(false); // consume the 220 greeting
        return client;
    }

    /// <summary>Sends <c>command CRLF</c> and returns the whole reply (multiline replies are concatenated).</summary>
    public async Task<string> CommandAsync(string command)
    {
        byte[] bytes = Encoding.ASCII.GetBytes($"{command}\r\n");
        await _stream.WriteAsync(bytes).ConfigureAwait(false);
        await _stream.FlushAsync().ConfigureAwait(false);
        return await ReadReplyAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a complete SMTP reply. A reply is one or more lines; a continuation line is
    /// <c>NNN-text</c>, the final line <c>NNN text</c> (a space after the code). Returns all lines joined.
    /// </summary>
    private async Task<string> ReadReplyAsync()
    {
        var reply = new StringBuilder();
        while (true)
        {
            string? line = await ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                return reply.ToString();
            }

            reply.Append(line).Append('\n');

            // The final line of a reply has a space (not a hyphen) as the 4th char.
            if (line.Length < 4 || line[3] != '-')
            {
                return reply.ToString();
            }
        }
    }

    private async Task<string?> ReadLineAsync()
    {
        while (true)
        {
            string buffered = _pending.ToString();
            int newline = buffered.IndexOf('\n', StringComparison.Ordinal);
            if (newline >= 0)
            {
                string line = buffered[..newline].TrimEnd('\r');
                _pending.Clear();
                _pending.Append(buffered[(newline + 1)..]);
                return line;
            }

            int read = await _stream.ReadAsync(_buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return _pending.Length > 0 ? _pending.ToString() : null;
            }

            _pending.Append(Encoding.Latin1.GetString(_buffer, 0, read));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _tcp.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
