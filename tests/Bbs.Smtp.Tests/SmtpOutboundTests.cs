using System.Reflection;
using Bbs.Core;
using Bbs.SevenPlus;
using MailKit.Net.Smtp;
using MimeKit;

namespace Bbs.Smtp.Tests;

/// <summary>
/// End-to-end tests for the two outbound deferrals closed in this slice, driving a real
/// <see cref="SmtpServer"/> with a real MailKit <see cref="SmtpClient"/> over loopback:
/// <list type="bullet">
/// <item>An attachment is 7plus-encoded into the stored body (the universal packet path), and the encoded
///   block round-trips byte-for-byte back to the original file.</item>
/// <item>A recipient addressed to a non-callsign token is stored as a <see cref="MessageType.Bulletin"/>
///   to that category; a callsign-shaped recipient is a <see cref="MessageType.Personal"/>; a submission
///   to both yields one of each.</item>
/// </list>
/// </summary>
public sealed class SmtpOutboundTests
{
    /// <summary>The shared fields.jpg image (embedded, see the csproj) used as the attachment payload.</summary>
    private static byte[] FieldsJpg()
    {
        Assembly asm = typeof(SmtpOutboundTests).Assembly;
        using Stream stream = asm.GetManifestResourceStream("Bbs.Smtp.Tests.Resources.fields.jpg")
            ?? throw new InvalidOperationException("embedded fields.jpg resource missing");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Send_WithAttachment_SevenPlusEncodesIntoBody_AndRoundTrips()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "attach pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        byte[] image = FieldsJpg();

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "attach pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.Subject = "photo";

        var builder = new BodyBuilder { TextBody = "here is the photo" };
        builder.Attachments.Add("fields.jpg", image, ContentType.Parse("image/jpeg"));
        message.Body = builder.ToMessageBody();

        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Message stored = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        string body = System.Text.Encoding.Latin1.GetString(stored.Body.Span);

        // The prose is kept and the 7plus block is appended (its header/footer markers carry "7+").
        Assert.Contains("here is the photo", body, StringComparison.Ordinal);
        Assert.Contains("7+", body, StringComparison.Ordinal);

        // The embedded 7plus block reassembles byte-for-byte back to the original image.
        IReadOnlyList<SevenPlusPart> parts = SevenPlusScanner.ExtractParts(stored.Body.Span);
        Assert.NotEmpty(parts);
        (bool complete, byte[]? content, _) = SevenPlusFile.TryAssemble(parts);
        Assert.True(complete);
        Assert.NotNull(content);
        Assert.True(image.AsSpan().SequenceEqual(content), "decoded attachment differs from the original image");
    }

    [Fact]
    public async Task Send_NoAttachment_BehavesAsTextOnly()
    {
        // A message with no attachments stores exactly the text body (no 7plus markers) — the prior behaviour.
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "plain pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "plain pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.Subject = "no file";
        message.Body = new TextPart("plain") { Text = "just words" };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Message stored = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        string body = System.Text.Encoding.Latin1.GetString(stored.Body.Span);
        Assert.Equal("just words", body);
        Assert.DoesNotContain("7+", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ToBulletinCategory_StoresAsBulletin()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "bulletin pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "bulletin pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("ALL@pdn")); // non-callsign token → bulletin category ALL
        message.Subject = "hello world";
        message.Body = new TextPart("plain") { Text = "a bulletin to all" };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        // Nothing personal; one bulletin, addressed to category ALL.
        Assert.Empty(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Message bulletin = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Bulletin }));
        Assert.Equal("M0LTE", bulletin.From);
        Assert.Contains(bulletin.Recipients, r => string.Equals(r.ToCall, "ALL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Send_ToCallsign_StoresAsPersonal()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "personal pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "personal pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.Subject = "personal";
        message.Body = new TextPart("plain") { Text = "for you" };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Assert.Empty(test.Store.ListMessages(new MessageQuery { Type = MessageType.Bulletin }));
    }

    [Fact]
    public async Task Send_ToCallsignAndCategory_StoresOnePersonalAndOneBulletin()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "both kinds pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "both kinds pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("G0ABC@pdn")); // callsign → personal
        message.To.Add(MailboxAddress.Parse("NEWS@pdn"));   // category → bulletin
        message.Subject = "split";
        message.Body = new TextPart("plain") { Text = "to one and to all" };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Message personal = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Assert.Contains(personal.Recipients, r => Callsigns.BaseEquals(r.ToCall, "G0ABC"));

        Message bulletin = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Bulletin }));
        Assert.Contains(bulletin.Recipients, r => string.Equals(r.ToCall, "NEWS", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, harness.Stored.Count); // onStored fired once per stored message
    }
}
