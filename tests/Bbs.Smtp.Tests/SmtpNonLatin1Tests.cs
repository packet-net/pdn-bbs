using System.Text;
using Bbs.Core;
using Bbs.Mime;
using MailKit.Net.Smtp;
using MimeKit;

namespace Bbs.Smtp.Tests;

/// <summary>
/// End-to-end tests that a body carrying characters outside Latin-1 (€, CJK, emoji) submitted over SMTP
/// is no longer lossy: the prior path (<c>Encoding.Latin1.GetBytes</c>) silently mapped such characters
/// to <c>'?'</c>. The fix stores the body as UTF-8 when it carries an above-U+00FF character, and the
/// display/render path reads it back UTF-8-or-Latin-1. These assert the RENDERED result (the decoded
/// body and the MIME text part a client receives), not just the persisted bytes.
/// </summary>
public sealed class SmtpNonLatin1Tests
{
    [Fact]
    public async Task Send_BodyWithNonLatin1Characters_SurvivesLosslessly()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "unicode pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        // €, CJK and an emoji — none representable in Latin-1.
        const string body = "Price is 5€ for 日本語 books 😀";

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "unicode pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.Subject = "unicode";
        message.Body = new TextPart("plain") { Text = body };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Message stored = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));

        // The lossy proof: the OLD path (Latin-1) would have destroyed these characters.
        string lossy = Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(body));
        Assert.Contains("?", lossy, StringComparison.Ordinal);
        Assert.NotEqual(body, lossy);

        // The stored bytes are the lossless UTF-8 form, and the display decode recovers the exact text.
        Assert.Equal(Encoding.UTF8.GetBytes(body), stored.Body.ToArray());
        Assert.Equal(body, PacketText.DecodeBody(stored.Body.Span));
        Assert.DoesNotContain("?", PacketText.DecodeBody(stored.Body.Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_BodyWithNonLatin1Characters_RendersBackToClientIntact()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "render pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        const string body = "Grüße — 你好 — café — €100";

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "render pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.Subject = "render";
        message.Body = new TextPart("plain") { Text = body };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Message stored = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));

        // Render the stored message back into a MimeMessage the way the IMAP FETCH path does, SERIALISE it
        // to wire bytes and REPARSE — so the assertion is over what actually crosses the wire to a client
        // (the chosen charset + transfer-encoding), not the in-memory string. (reflowText off so the
        // assertion is over the exact text, not a format=flowed re-wrap.)
        MimeMessage rendered = BbsMessageToMime.ToMimeMessage(stored, SmtpSession.MailDomain, reflowText: false);
        using var wire = new MemoryStream();
        rendered.WriteTo(wire);
        wire.Position = 0;
        MimeMessage reparsed = MimeMessage.Load(wire);
        string renderedBody = reparsed.TextBody ?? string.Empty;

        Assert.Equal(body, renderedBody.TrimEnd('\r', '\n'));
        Assert.DoesNotContain("?", renderedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_PlainAsciiBody_StaysByteTransparent()
    {
        // Regression guard: a body with no above-U+00FF character is byte-identical to the historical
        // Latin-1 store, so forwarding fidelity and the existing wire are unchanged.
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "ascii pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        const string body = "Just plain ASCII text.";

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "ascii pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.Subject = "ascii";
        message.Body = new TextPart("plain") { Text = body };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Message stored = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Assert.Equal(Encoding.Latin1.GetBytes(body), stored.Body.ToArray());
    }
}
