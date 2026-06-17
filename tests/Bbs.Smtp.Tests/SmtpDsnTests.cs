using Bbs.Core;
using MailKit;
using MailKit.Net.Smtp;
using MimeKit;

namespace Bbs.Smtp.Tests;

/// <summary>
/// Tests for SMTP delivery status notifications (RFC 3461 the extension, RFC 3464 the report). The
/// submission server advertises DSN, honours NOTIFY= on a recipient, and — when a recipient asked for
/// <c>NOTIFY=SUCCESS</c> — stores a "relayed" DSN back into the submitter's own mailbox (LocalOnly, so it
/// never forwards). The end-to-end tests drive a real MailKit client that requests the DSN over the wire,
/// then assert the RENDERED report, not just that a row exists.
/// </summary>
public sealed class SmtpDsnTests
{
    // ---------------------------------------------------------------- SmtpDsn pure helpers

    [Fact]
    public void ParseNotify_NoParameter_DefaultsToFailureOnly()
        => Assert.Equal(SmtpDsnNotify.Failure, SmtpDsn.ParseNotify("<M0LTE@pdn>"));

    [Fact]
    public void ParseNotify_Success_IsSuccessOnly()
        => Assert.Equal(SmtpDsnNotify.Success, SmtpDsn.ParseNotify("<M0LTE@pdn> NOTIFY=SUCCESS"));

    [Fact]
    public void ParseNotify_SuccessFailureDelay_IsAllThree()
    {
        SmtpDsnNotify n = SmtpDsn.ParseNotify("<M0LTE@pdn> NOTIFY=SUCCESS,FAILURE,DELAY");
        Assert.True(n.HasFlag(SmtpDsnNotify.Success));
        Assert.True(n.HasFlag(SmtpDsnNotify.Failure));
        Assert.True(n.HasFlag(SmtpDsnNotify.Delay));
    }

    [Fact]
    public void ParseNotify_Never_IsNone_AndWinsOverOtherTokens()
    {
        Assert.Equal(SmtpDsnNotify.None, SmtpDsn.ParseNotify("<M0LTE@pdn> NOTIFY=NEVER"));
        Assert.Equal(SmtpDsnNotify.None, SmtpDsn.ParseNotify("<M0LTE@pdn> NOTIFY=SUCCESS,NEVER"));
    }

    [Fact]
    public void ExtractOrcpt_DecodesAddrTypeAndXtext()
    {
        // ORCPT is "rfc822;<xtext-address>"; +40 is an xtext-escaped '@'.
        Assert.Equal("M0LTE@pdn", SmtpDsn.ExtractOrcpt("<x@pdn> ORCPT=rfc822;M0LTE+40pdn"));
        Assert.Null(SmtpDsn.ExtractOrcpt("<x@pdn>"));
    }

    [Fact]
    public void ExtractEnvId_ReturnsValue_OrNull()
    {
        Assert.Equal("abc-123", SmtpDsn.ExtractEnvId("<x@pdn> ENVID=abc-123"));
        Assert.Null(SmtpDsn.ExtractEnvId("<x@pdn> RET=HDRS"));
    }

    [Fact]
    public void BuildRelayedReport_CarriesPerRecipientStatusFields()
    {
        var now = new DateTimeOffset(2026, 6, 17, 9, 0, 0, TimeSpan.Zero);
        string report = SmtpDsn.BuildRelayedReport(
            reportingMta: "pdn (pdn-bbs SMTP submission)",
            originalSubject: "Weekly net",
            envId: "env-9",
            recipients: ["M0LTE@GB7RDG.GBR.EURO", "G0ABC@pdn"],
            now: now);

        // Human-readable preamble names the message + each recipient.
        Assert.Contains("delivery status notification", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Subject: Weekly net", report, StringComparison.Ordinal);

        // Machine-readable per-message + per-recipient fields (RFC 3464).
        Assert.Contains("Reporting-MTA: dns;pdn (pdn-bbs SMTP submission)", report, StringComparison.Ordinal);
        Assert.Contains("Original-Envelope-Id: env-9", report, StringComparison.Ordinal);
        Assert.Contains("Final-Recipient: rfc822;M0LTE@GB7RDG.GBR.EURO", report, StringComparison.Ordinal);
        Assert.Contains("Final-Recipient: rfc822;G0ABC@pdn", report, StringComparison.Ordinal);
        Assert.Contains("Action: relayed", report, StringComparison.Ordinal);
        Assert.Contains("Status: 2.0.0", report, StringComparison.Ordinal);

        // Two recipients ⇒ two relayed actions.
        Assert.Equal(2, CountOccurrences(report, "Action: relayed"));
    }

    [Fact]
    public void BuildRelayedReport_OmitsEnvelopeId_WhenAbsent()
    {
        string report = SmtpDsn.BuildRelayedReport(
            reportingMta: "pdn", originalSubject: "x", envId: null,
            recipients: ["M0LTE@pdn"], now: DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("Original-Envelope-Id", report, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- end-to-end over the wire

    [Fact]
    public async Task Ehlo_AdvertisesDsn()
    {
        using var test = new TestStore();
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        await using var raw = await RawSmtpClient.ConnectAsync(harness.Port);
        string ehlo = await raw.CommandAsync("EHLO test.example");
        Assert.Contains("DSN", ehlo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_WithNotifySuccess_StoresRelayedDsnToSubmitter()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "dsn password");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = new DsnRequestingSmtpClient(DeliveryStatusNotification.Success, "env-42");
        await client.ConnectAsync("127.0.0.1", harness.Port, MailKit.Security.SecureSocketOptions.None);
        await client.AuthenticateAsync("M0LTE", "dsn password");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("G0ABC@pdn"));
        message.Subject = "needs a receipt";
        message.Body = new TextPart("plain") { Text = "did this arrive?" };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        // The submission itself is stored to G0ABC...
        IReadOnlyList<Message> personals = test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal });

        // ...AND a DSN was stored back to the submitter M0LTE.
        Message dsn = Assert.Single(personals, m => string.Equals(m.From, SmtpDsn.ReportFrom, StringComparison.Ordinal));
        Assert.Equal(SmtpDsn.RelayedSubject, dsn.Subject);
        Assert.True(dsn.LocalOnly, "the DSN must be LocalOnly so it never forwards off-BBS");
        Assert.Contains(dsn.Recipients, r => Callsigns.BaseEquals(r.ToCall, "M0LTE"));

        // The rendered report names the original recipient and carries the relayed status.
        string report = PacketText.DecodeBody(dsn.Body.Span);
        Assert.Contains("G0ABC@pdn", report, StringComparison.Ordinal);
        Assert.Contains("Action: relayed", report, StringComparison.Ordinal);
        Assert.Contains("Status: 2.0.0", report, StringComparison.Ordinal);
        Assert.Contains("Original-Envelope-Id: env-42", report, StringComparison.Ordinal);
        Assert.Contains("Subject: needs a receipt", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_WithoutNotify_StoresNoDsn()
    {
        // The default NOTIFY is FAILURE-only and a failure here is a synchronous 5xx, so a plain submission
        // produces exactly one stored message (the submission) and no DSN.
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "no dsn pw");
        await using SmtpServerHarness harness = await SmtpServerHarness.StartAsync(test.Store, test.Time);

        using SmtpClient client = await harness.ConnectAsync();
        await client.AuthenticateAsync("M0LTE", "no dsn pw");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("M0LTE@pdn"));
        message.To.Add(MailboxAddress.Parse("G0ABC@pdn"));
        message.Subject = "no receipt please";
        message.Body = new TextPart("plain") { Text = "fire and forget" };
        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);

        Message only = Assert.Single(test.Store.ListMessages(new MessageQuery { Type = MessageType.Personal }));
        Assert.NotEqual(SmtpDsn.ReportFrom, only.From);
        Assert.Single(harness.Stored); // only the submission nudged onStored — no DSN
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }

        return count;
    }
}

/// <summary>
/// A MailKit <see cref="SmtpClient"/> that requests a given delivery-status notification for every
/// recipient and an envelope id — so the test drives a real DSN request over the wire (MailKit only
/// emits NOTIFY=/ENVID= because the server advertises the DSN extension).
/// </summary>
internal sealed class DsnRequestingSmtpClient(DeliveryStatusNotification notify, string envId) : SmtpClient
{
    protected override DeliveryStatusNotification? GetDeliveryStatusNotifications(MimeMessage message, MailboxAddress mailbox)
        => notify;

    protected override string GetEnvelopeId(MimeMessage message) => envId;
}
