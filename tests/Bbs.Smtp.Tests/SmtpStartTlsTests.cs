using Bbs.Core;
using MailKit.Net.Smtp;
using MimeKit;

namespace Bbs.Smtp.Tests;

/// <summary>
/// Exercises the STARTTLS submission path (RFC 3207, port 587 — the endpoint iPhone Mail's default
/// "Add Mail Account" flow auto-probes for outgoing). One <see cref="SmtpServer"/> runs BOTH an
/// implicit-TLS listener and a STARTTLS listener (the harness binds each on its own ephemeral port,
/// sharing the generated self-signed cert). These prove: AUTH is offered only AFTER the in-band TLS
/// upgrade; an authenticated send over the upgraded channel lands in the store as a Personal From the
/// auth callsign and fires onStored; the pre-TLS EHLO advertises STARTTLS but NOT AUTH; AUTH/MAIL
/// before STARTTLS are rejected 530; and the implicit-TLS (465) session rejects STARTTLS with 503.
/// </summary>
public sealed class SmtpStartTlsTests
{
    [Fact]
    public async Task StartTls_OffersAuthOnlyAfterUpgrade_ThenSends()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "starttls passphrase");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartWithTlsAsync(test.Store, test.Time);
        Assert.NotEqual(0, harness.StartTlsPort);

        using SmtpClient client = await harness.ConnectStartTlsAsync();

        // MailKit's ConnectAsync(StartTls) performs the upgrade and re-EHLOs, so by the time it returns the
        // channel is secure and the capability set reflects the POST-upgrade EHLO: AUTH is now offered.
        Assert.True(client.IsConnected);
        Assert.True(client.IsSecure); // the in-band upgrade completed
        Assert.True(
            client.AuthenticationMechanisms.Contains("PLAIN") || client.AuthenticationMechanisms.Contains("LOGIN"),
            "AUTH must be advertised after the STARTTLS upgrade.");

        await client.AuthenticateAsync("M0LTE", "starttls passphrase");
        Assert.True(client.IsAuthenticated);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.Subject = "over starttls";
        message.Body = new TextPart("plain") { Text = "starttls body" };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Message stored = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Assert.Equal("over starttls", stored.Subject);
        Assert.Equal("M0LTE", stored.From); // From is the authenticated callsign
        Assert.Contains("starttls body", System.Text.Encoding.Latin1.GetString(stored.Body.Span), StringComparison.Ordinal);

        Message nudged = Assert.Single(harness.Stored);
        Assert.Equal(stored.Number, nudged.Number);
    }

    [Fact]
    public async Task StartTlsPort_PreTls_AdvertisesStartTls_NotAuth_AndRejectsMailCommandsWith530()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "raw starttls pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartWithTlsAsync(test.Store, test.Time);

        // The STARTTLS listener starts plaintext, so the raw client can read the greeting + speak EHLO.
        await using var raw = await RawSmtpClient.ConnectAsync(harness.StartTlsPort);

        string ehlo = await raw.CommandAsync("EHLO test.example");
        Assert.Contains("STARTTLS", ehlo, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTH", ehlo, StringComparison.Ordinal); // never offer auth pre-TLS (RFC 3207 §4.3)
        Assert.Contains("8BITMIME", ehlo, StringComparison.Ordinal);
        Assert.Contains("SIZE ", ehlo, StringComparison.Ordinal);

        // AUTH and MAIL before STARTTLS are refused with 530 (must issue STARTTLS first).
        string ir = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("\0M0LTE\0raw starttls pw"));
        string auth = await raw.CommandAsync($"AUTH PLAIN {ir}");
        Assert.StartsWith("530", auth, StringComparison.Ordinal);

        string mail = await raw.CommandAsync("MAIL FROM:<M0LTE@pdn>");
        Assert.StartsWith("530", mail, StringComparison.Ordinal);

        // Nothing was stored and no nudge fired.
        Assert.Empty(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Assert.Empty(harness.Stored);
    }

    [Fact]
    public async Task ImplicitTlsPort_RejectsStartTls_With503()
    {
        // On the already-secure implicit-TLS session (465), a STARTTLS command is a protocol error: 503.
        // Driven at the wire (a TLS-wrapped raw client) because MailKit will not send STARTTLS once secure.
        using var test = new TestStore();
        await using SmtpServerHarness harness = await SmtpServerHarness.StartWithTlsAsync(test.Store, test.Time);

        await using RawSmtpClient raw = await RawSmtpClient.ConnectImplicitTlsAsync(harness.Port);
        await raw.CommandAsync("EHLO test.example");

        string starttls = await raw.CommandAsync("STARTTLS");
        Assert.StartsWith("503", starttls, StringComparison.Ordinal);

        await raw.CommandAsync("QUIT");
    }
}
