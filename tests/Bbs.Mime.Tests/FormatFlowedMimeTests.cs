using System.Text;
using Bbs.Core;
using Bbs.Mime;
using MimeKit;

namespace Bbs.Mime.Tests;

/// <summary>
/// End-to-end check that <c>reflowText: true</c> produces a wire-correct RFC 3676 <c>format=flowed</c>
/// part: the <c>format=flowed</c> parameter is set, and the soft-line trailing spaces (load-bearing
/// for reflow) survive MIME serialisation — they are quoted-printable-encoded as <c>=20</c> so a
/// 7bit/8bit writer can't drop them.
/// </summary>
public sealed class FormatFlowedMimeTests
{
    private const string MailDomain = "pdn";

    [Fact]
    public void Reflow_ProducesFormatFlowed_TrailingSpacesSurviveSerialisation()
    {
        var message = new Message
        {
            Number = 1,
            Type = MessageType.Bulletin,
            Status = MessageStatus.Unread,
            From = "M0LTE",
            Bid = "1_GB7PDN",
            Subject = "long prose",
            Body = Encoding.Latin1.GetBytes(
                "Louis Varney conceived this antenna in 1946 while looking for a compact\r\n" +
                "multiband solution for his 100-foot garden in Stony Stratford, England.\r\n" +
                "done."),
            CreatedAt = new DateTimeOffset(2026, 6, 12, 9, 30, 0, TimeSpan.Zero),
            Recipients = [new MessageRecipient("ALL", null)],
            Attachments = [],
        };

        MimeMessage mime = BbsMessageToMime.ToMimeMessage(message, MailDomain, extraAttachments: null, reflowText: true);

        // Round-trip through the wire (serialise, reload) — what a client actually receives.
        using var ms = new MemoryStream();
        mime.WriteTo(ms);
        ms.Position = 0;
        MimeMessage reloaded = MimeMessage.Load(ms);

        var text = Assert.IsType<TextPart>(reloaded.Body);
        Assert.Equal("flowed", text.ContentType.Parameters["format"]);

        string[] lines = text.Text.Replace("\r\n", "\n").Split('\n');
        Assert.EndsWith(" ", lines[0]);                 // soft line — trailing space survived QP
        Assert.EndsWith(" ", lines[1]);                 // soft line
        Assert.Equal("done.", lines[2].TrimEnd('\n'));  // hard final line — no trailing space
    }

    [Fact]
    public void NoReflow_StaysPlainFixedText()
    {
        var message = new Message
        {
            Number = 2,
            Type = MessageType.Bulletin,
            Status = MessageStatus.Unread,
            From = "M0LTE",
            Bid = "2_GB7PDN",
            Subject = "fixed",
            Body = Encoding.Latin1.GetBytes("one full line of ordinary prose that is plenty long enough to wrap here\r\nand a second line that continues the same ordinary prose for a good while."),
            CreatedAt = new DateTimeOffset(2026, 6, 12, 9, 30, 0, TimeSpan.Zero),
            Recipients = [new MessageRecipient("ALL", null)],
            Attachments = [],
        };

        // Default (no reflow) keeps it plain — no format=flowed, no trailing-space rewrite.
        MimeMessage mime = BbsMessageToMime.ToMimeMessage(message, MailDomain);
        var text = Assert.IsType<TextPart>(mime.Body);
        Assert.False(text.ContentType.Parameters.Contains("format"));
        Assert.DoesNotContain(" \n", text.Text.Replace("\r\n", "\n")); // no soft-wrap trailing spaces
    }
}
