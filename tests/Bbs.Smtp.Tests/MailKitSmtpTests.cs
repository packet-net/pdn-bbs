using Bbs.Core;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Bbs.Smtp.Tests;

/// <summary>
/// End-to-end tests driving a real <see cref="SmtpServer"/> with a real MailKit <see cref="SmtpClient"/>
/// over loopback — MailKit is the strict correctness oracle (it throws on any malformed response). These
/// run in normal CI (in-process, loopback; like the IMAP suite's MailKit tests). They prove the
/// submission contract: EHLO advertises AUTH, AUTH is required before mail (no open relay), a sent
/// message lands in the store as a Personal From the authenticated callsign, routed addresses split into
/// To + At, and multi-recipient submissions group by route into one message per distinct At.
/// </summary>
public sealed class MailKitSmtpTests
{
    [Fact]
    public async Task Ehlo_AdvertisesAuth()
    {
        using var test = new TestStore();
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        Assert.True(client.IsConnected);
        Assert.True(client.AuthenticationMechanisms.Contains("PLAIN") || client.AuthenticationMechanisms.Contains("LOGIN"));

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Authenticate_SucceedsWithRightPassword_FailsWithWrong()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "correct horse battery");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using (SmtpClient ok = await harness.ConnectAsync())
        {
            await ok.AuthenticateAsync("M0LTE", "correct horse battery");
            Assert.True(ok.IsAuthenticated);
            await ok.DisconnectAsync(quit: true);
        }

        using SmtpClient bad = await harness.ConnectAsync();
        await Assert.ThrowsAsync<AuthenticationException>(
            () => bad.AuthenticateAsync("M0LTE", "wrong password"));
        await bad.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Authenticate_AcceptsEmailFormUsername()
    {
        // iPhone Mail commonly sends the username as the full email address; the @domain must be stripped
        // so it authenticates as the bare callsign (the same tolerance as ImapBackend.Authenticate).
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "email form pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE@pdn", "email form pw");
        Assert.True(client.IsAuthenticated);

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Send_LandsInStore_AsPersonalFromAuthCallsign_AndFiresOnStored()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "send password");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "send password");

        var message = new MimeMessage();
        // The MAIL FROM identity is deliberately something else — the stored From must be the AUTH call.
        message.From.Add(new MailboxAddress("Whoever", "whoever@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "M0LTE@pdn"));
        message.Subject = "Hello from SMTP";
        message.Body = new TextPart("plain") { Text = "this is the body" };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Message stored = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Assert.Equal("M0LTE", stored.From); // From is the authenticated callsign, NOT the MAIL FROM
        Assert.Equal("Hello from SMTP", stored.Subject);
        Assert.Contains("this is the body", System.Text.Encoding.Latin1.GetString(stored.Body.Span), StringComparison.Ordinal);
        Assert.Contains(stored.Recipients, r => Callsigns.BaseEquals(r.ToCall, "M0LTE"));
        Assert.Null(stored.At); // a bare callsign recipient has no route

        // The onStored callback fired exactly once (the host wires this to RoutingService.RouteMessage).
        Message nudged = Assert.Single(harness.Stored);
        Assert.Equal(stored.Number, nudged.Number);
    }

    [Fact]
    public async Task Send_ToRoutedAddress_StoresWithToCallAndAt()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "routed pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "routed pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(new MailboxAddress("G0ABC", "G0ABC@gb7rdg.gbr.euro.pdn"));
        message.Subject = "routed";
        message.Body = new TextPart("plain") { Text = "via gb7rdg" };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Message stored = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Assert.Equal("M0LTE", stored.From);
        Assert.Equal("GB7RDG.GBR.EURO", stored.At);
        Assert.Contains(stored.Recipients, r => Callsigns.BaseEquals(r.ToCall, "G0ABC"));
    }

    [Fact]
    public async Task Send_MultipleRecipientsDifferentRoutes_GroupsIntoOneMessagePerAt()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "multi pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "multi pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("G0ABC@gb7rdg.gbr.euro.pdn")); // At = GB7RDG.GBR.EURO
        message.To.Add(MailboxAddress.Parse("G0DEF@gb7rdg.gbr.euro.pdn")); // same At
        message.To.Add(MailboxAddress.Parse("G0XYZ@pdn"));                 // no At (local)
        message.Subject = "fan-out";
        message.Body = new TextPart("plain") { Text = "to three" };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        IReadOnlyList<Message> stored = test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal, OldestFirst = true });
        Assert.Equal(2, stored.Count); // one per distinct At (GB7RDG.GBR.EURO, and null)

        Message routed = Assert.Single(stored, m => m.At == "GB7RDG.GBR.EURO");
        Assert.Equal(2, routed.Recipients.Count); // G0ABC + G0DEF grouped together
        Assert.Contains(routed.Recipients, r => Callsigns.BaseEquals(r.ToCall, "G0ABC"));
        Assert.Contains(routed.Recipients, r => Callsigns.BaseEquals(r.ToCall, "G0DEF"));

        Message local = Assert.Single(stored, m => m.At is null);
        Assert.Contains(local.Recipients, r => Callsigns.BaseEquals(r.ToCall, "G0XYZ"));

        Assert.Equal(2, harness.Stored.Count); // onStored fired once per stored message
    }

    [Fact]
    public async Task SendWithoutAuth_IsRejected_NoOpenRelay()
    {
        using var test = new TestStore();
        // No mail-password set: there is nobody who could authenticate, and a sender must not be able to
        // submit anyway. MailKit refuses to send on a server that requires auth when not authenticated.
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        Assert.False(client.IsAuthenticated);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.Subject = "should not relay";
        message.Body = new TextPart("plain") { Text = "open relay attempt" };

        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(message));

        // Nothing was stored and the onStored nudge never fired.
        Assert.Empty(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Assert.Empty(harness.Stored);

        await client.DisconnectAsync(quit: true);
    }
}
