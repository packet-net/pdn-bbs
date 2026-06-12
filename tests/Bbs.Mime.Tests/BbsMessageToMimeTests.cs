using System.Text;
using Bbs.Core;
using Bbs.Mime;
using MimeKit;

namespace Bbs.Mime.Tests;

public sealed class BbsMessageToMimeTests
{
    private const string MailDomain = "pkt.gb7pdn";

    private static Message PersonalWithRecipientsAndAttachment()
    {
        // A captured-shape personal with a '#' in its AT-route, two To, one Cc, one attachment.
        return new Message
        {
            Number = 7,
            Type = MessageType.Personal,
            Status = MessageStatus.Unread,
            From = "M0LTE",
            At = "GB7RDG.#42.GBR.EURO", // the '#'-bearing hierarchical route
            Bid = "3331_GM8BPQ",
            Subject = "Antenna party Saturday",
            Body = Encoding.Latin1.GetBytes("Bring 50 ohm coax.\r\nTalk soon, John.\r\n"),
            CreatedAt = new DateTimeOffset(2026, 6, 12, 9, 30, 0, TimeSpan.Zero),
            Recipients =
            [
                new MessageRecipient("G4ABC", null),
                new MessageRecipient("G7XYZ", null),
                new MessageRecipient("M0AAA", null, Cc: true),
            ],
            Attachments =
            [
                // A binary attachment (octet-stream) with NUL/control bytes — proves byte-exactness
                // through MIME encode/serialise/parse, and that it reparses as a plain MimePart.
                new MessageAttachment("party.bin", Encoding.ASCII.GetBytes("see attached map\x00\x01\x02")),
            ],
        };
    }

    [Fact]
    public void ToMimeMessage_Personal_HasFromToCcDecodingBackToPacketAddresses()
    {
        Message message = PersonalWithRecipientsAndAttachment();

        MimeMessage mime = BbsMessageToMime.ToMimeMessage(message, MailDomain);

        // From decodes back to the sender's packet address.
        MailboxAddress from = Assert.IsType<MailboxAddress>(Assert.Single(mime.From));
        Assert.True(PacketAddressCodec.TryDecode(from.Address, MailDomain, out string fromPacket));
        Assert.Equal("M0LTE", fromPacket);
        Assert.Equal("M0LTE", from.Name); // display name = the real address

        // To: two primary recipients, each decoding to call@route (the '#' survives).
        string[] toPackets = mime.To.Mailboxes
            .Select(m =>
            {
                Assert.True(PacketAddressCodec.TryDecode(m.Address, MailDomain, out string p));
                return p;
            })
            .ToArray();
        Assert.Equal(["G4ABC@GB7RDG.#42.GBR.EURO", "G7XYZ@GB7RDG.#42.GBR.EURO"], toPackets);

        // Cc: the one carbon copy.
        MailboxAddress cc = Assert.Single(mime.Cc.Mailboxes);
        Assert.True(PacketAddressCodec.TryDecode(cc.Address, MailDomain, out string ccPacket));
        Assert.Equal("M0AAA@GB7RDG.#42.GBR.EURO", ccPacket);
        Assert.Equal("M0AAA@GB7RDG.#42.GBR.EURO", cc.Name); // faithful display name keeps the '#'
    }

    [Fact]
    public void ToMimeMessage_Personal_MessageIdDerivedFromBid()
    {
        Message message = PersonalWithRecipientsAndAttachment();

        MimeMessage mime = BbsMessageToMime.ToMimeMessage(message, MailDomain);

        // Deterministic: same BID + domain => same Message-Id (so clients thread/dedup).
        Assert.Equal($"3331_GM8BPQ@{MailDomain}", mime.MessageId);
        Assert.Equal(BbsMessageToMime.MessageIdFromBid(message.Bid, MailDomain), mime.MessageId);
    }

    [Fact]
    public void ToMimeMessage_Personal_XPacketAddressVerbatim()
    {
        Message message = PersonalWithRecipientsAndAttachment();

        MimeMessage mime = BbsMessageToMime.ToMimeMessage(message, MailDomain);

        string? header = mime.Headers[BbsMessageToMime.PacketAddressHeader];
        Assert.NotNull(header);
        // Verbatim real addresses, '#' intact: sender then every recipient.
        Assert.Equal(
            "M0LTE, G4ABC@GB7RDG.#42.GBR.EURO, G7XYZ@GB7RDG.#42.GBR.EURO, M0AAA@GB7RDG.#42.GBR.EURO",
            header);
    }

    [Fact]
    public void ToMimeMessage_Personal_SubjectAndDate()
    {
        Message message = PersonalWithRecipientsAndAttachment();

        MimeMessage mime = BbsMessageToMime.ToMimeMessage(message, MailDomain);

        Assert.Equal("Antenna party Saturday", mime.Subject);
        Assert.Equal(message.CreatedAt, mime.Date);
    }

    [Fact]
    public void ToMimeMessage_WithAttachment_IsMultipartMixed_AttachmentByteExact()
    {
        Message message = PersonalWithRecipientsAndAttachment();
        byte[] originalBytes = message.Attachments[0].Content.ToArray();

        MimeMessage mime = BbsMessageToMime.ToMimeMessage(message, MailDomain);

        var multipart = Assert.IsType<Multipart>(mime.Body);
        Assert.Equal("multipart/mixed", multipart.ContentType.MimeType);

        // First part: the text/plain body. MimeKit's TextPart.Text getter normalises CRLF to LF
        // (a display convenience); the canonical wire form keeps CRLF.
        var textPart = Assert.IsType<TextPart>(multipart[0]);
        Assert.Equal("Bring 50 ohm coax.\nTalk soon, John.\n", textPart.Text);

        // Second part: the attachment, name + bytes preserved.
        var attachmentPart = Assert.IsType<MimePart>(multipart[1]);
        Assert.Equal("party.bin", attachmentPart.FileName);
        Assert.True(attachmentPart.IsAttachment);

        using var ms = new MemoryStream();
        Assert.NotNull(attachmentPart.Content);
        attachmentPart.Content.DecodeTo(ms);
        Assert.Equal(originalBytes, ms.ToArray()); // byte-exact through encode/decode
    }

    [Fact]
    public void ToMimeMessage_NoAttachment_IsSingleTextPlain()
    {
        Message message = PersonalWithRecipientsAndAttachment() with { Attachments = [] };

        MimeMessage mime = BbsMessageToMime.ToMimeMessage(message, MailDomain);

        var textPart = Assert.IsType<TextPart>(mime.Body);
        Assert.Equal("text/plain", textPart.ContentType.MimeType);
        Assert.Equal("Bring 50 ohm coax.\nTalk soon, John.\n", textPart.Text); // .Text is LF-normalised
    }

    [Fact]
    public void ToMimeMessage_ReParsedWithMimeKit_PreservesStructureAndAddresses()
    {
        Message message = PersonalWithRecipientsAndAttachment();
        byte[] originalBytes = message.Attachments[0].Content.ToArray();

        MimeMessage produced = BbsMessageToMime.ToMimeMessage(message, MailDomain);

        // Serialise and re-parse with the same library the IMAP FETCH/BODYSTRUCTURE will use.
        using var stream = new MemoryStream();
        produced.WriteTo(stream);
        stream.Position = 0;
        MimeMessage reparsed = MimeMessage.Load(stream);

        Assert.Equal($"3331_GM8BPQ@{MailDomain}", reparsed.MessageId);
        Assert.Equal("Antenna party Saturday", reparsed.Subject);
        Assert.Equal(2, reparsed.To.Mailboxes.Count());
        Assert.Single(reparsed.Cc.Mailboxes);

        // Addresses still decode through the codec end-to-end (the '#' survived the wire).
        MailboxAddress to0 = reparsed.To.Mailboxes.First();
        Assert.True(PacketAddressCodec.TryDecode(to0.Address, MailDomain, out string p0));
        Assert.Equal("G4ABC@GB7RDG.#42.GBR.EURO", p0);

        // X-Packet-Address verbatim survived too.
        Assert.Equal(
            "M0LTE, G4ABC@GB7RDG.#42.GBR.EURO, G7XYZ@GB7RDG.#42.GBR.EURO, M0AAA@GB7RDG.#42.GBR.EURO",
            reparsed.Headers[BbsMessageToMime.PacketAddressHeader]);

        // Attachment bytes byte-exact after a full serialise + reparse.
        var body = Assert.IsType<Multipart>(reparsed.Body);
        var attachment = Assert.IsType<MimePart>(body[1]);
        using var ms = new MemoryStream();
        Assert.NotNull(attachment.Content);
        attachment.Content.DecodeTo(ms);
        Assert.Equal(originalBytes, ms.ToArray());
    }

    [Fact]
    public void ToMimeMessage_Bulletin_CategoryEncodedThroughCodec()
    {
        var bulletin = new Message
        {
            Number = 12,
            Type = MessageType.Bulletin,
            Status = MessageStatus.Unread,
            From = "G4ABC",
            At = null,
            Bid = "12_GB7RDG",
            Subject = "Net tonight",
            Body = Encoding.Latin1.GetBytes("2m net at 8pm.\r\n"),
            CreatedAt = new DateTimeOffset(2026, 6, 12, 18, 0, 0, TimeSpan.Zero),
            Recipients = [new MessageRecipient("ALL", null)],
            Attachments = [],
        };

        MimeMessage mime = BbsMessageToMime.ToMimeMessage(bulletin, MailDomain);

        // The bulletin's "To" (its category) rides the same codec — decodes back to "ALL".
        MailboxAddress to = Assert.Single(mime.To.Mailboxes);
        Assert.True(PacketAddressCodec.TryDecode(to.Address, MailDomain, out string category));
        Assert.Equal("ALL", category);
        Assert.Equal("ALL", to.Name);

        // Same shape as a personal: a single text/plain (no attachments).
        var textPart = Assert.IsType<TextPart>(mime.Body);
        Assert.Equal("text/plain", textPart.ContentType.MimeType);
    }

    [Fact]
    public void MessageIdFromBid_SanitisesNonTokenCharacters()
    {
        // A BID with a space (pathological) is sanitised to a valid msg-id token.
        Assert.Equal("3331.GM8BPQ@pkt.gb7pdn",
            BbsMessageToMime.MessageIdFromBid("3331 GM8BPQ", MailDomain));
    }

    [Fact]
    public void ToMimeMessage_Latin1BodyByte_SurvivesCharacterFaithfully()
    {
        // An 8-bit Latin-1 body byte (0xA3 = '£') must render the same character webmail shows
        // (GetBodyText decodes Latin-1); MimeKit re-encodes as UTF-8 on the wire, so the byte
        // changes but the CHARACTER is faithful through a full serialise + reparse.
        Message message = PersonalWithRecipientsAndAttachment() with
        {
            Attachments = [],
            Body = new byte[] { 0xA3, (byte)'5', (byte)'0' }, // "£50" in Latin-1
        };

        MimeMessage produced = BbsMessageToMime.ToMimeMessage(message, MailDomain);
        using var stream = new MemoryStream();
        produced.WriteTo(stream);
        stream.Position = 0;
        MimeMessage reparsed = MimeMessage.Load(stream);

        var textPart = Assert.IsType<TextPart>(reparsed.Body);
        // '£50' — the same character the user sees in webmail (MimeKit appends a canonical newline).
        Assert.Equal("£50", textPart.Text.TrimEnd('\n'));
    }
}
