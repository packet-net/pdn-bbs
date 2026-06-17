using System.Text;
using Bbs.Core;
using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;

namespace Bbs.Mime;

/// <summary>
/// Synthesises a stored BBS <see cref="Message"/> into a <see cref="MimeMessage"/> an email client
/// (later: the IMAP server's FETCH/BODYSTRUCTURE) can read. Pure and sans-IO — it builds the object
/// graph only; nothing is sent or parsed off a socket here.
/// </summary>
/// <remarks>
/// Every packet address (sender, each recipient, a bulletin's category/distribution) is carried as a
/// synthetic addr-spec via <see cref="PacketAddressCodec.Encode"/> with the verbatim address as the
/// display name, so a client renders the real address and a strict MTA still sees a valid mailbox. The
/// verbatim address is additionally preserved on an <c>X-Packet-Address</c> header so nothing is lost
/// even if a client ignores display names. Personals (P) and bulletins (B/T) synthesise identically;
/// folder mapping (INBOX vs Bulletins/*) is a later slice's job.
/// </remarks>
public static class BbsMessageToMime
{
    /// <summary>The header carrying each verbatim real packet address (sender + recipients).</summary>
    public const string PacketAddressHeader = "X-Packet-Address";

    /// <summary>
    /// Renders <paramref name="message"/> into a <see cref="MimeMessage"/>.
    /// </summary>
    /// <param name="message">The stored BBS message (with its recipients and attachments).</param>
    /// <param name="mailDomain">The synthetic mail domain (e.g. <c>pdn</c>) — not hardcoded.</param>
    /// <param name="extraAttachments">
    /// Synthesised attachments to add beyond the message's stored ones — e.g. a 7plus file decoded
    /// from the body by the caller, surfaced as an accessible attachment. Null/empty adds nothing.
    /// </param>
    /// <param name="reflowText">
    /// When true, render the text body as RFC 3676 <c>format=flowed</c> (<see cref="TextReflow"/>) so a
    /// client reflows the sender's fixed-width wrap to the screen — ASCII art / tables / headers are
    /// left as hard breaks. When false, the body is plain fixed text exactly as stored.
    /// </param>
    /// <param name="bodyText">
    /// Overrides the rendered text body when non-null (else the stored body is used). The caller uses
    /// this to clean the body before rendering — chiefly to strip an inline 7plus blob that has been
    /// surfaced as a decoded attachment (see <paramref name="extraAttachments"/>), so the reader sees
    /// the prose + the attachment instead of a wall of 7plus code.
    /// </param>
    public static MimeMessage ToMimeMessage(
        Message message, string mailDomain, IReadOnlyList<MessageAttachment>? extraAttachments = null,
        bool reflowText = false, string? bodyText = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(mailDomain);

        var mime = new MimeMessage();

        // --- From: the sender's packet address (callsign, no @-route on a stored sender) ---
        string fromPacket = SenderAddress(message);
        mime.From.Add(Mailbox(fromPacket, mailDomain));

        // --- To / Cc: each recipient's full packet address (call @ AT-route when present) ---
        foreach (MessageRecipient recipient in message.Recipients)
        {
            string recipientPacket = RecipientAddress(recipient, message.At);
            MailboxAddress mailbox = Mailbox(recipientPacket, mailDomain);
            if (recipient.Cc)
            {
                mime.Cc.Add(mailbox);
            }
            else
            {
                mime.To.Add(mailbox);
            }
        }

        mime.Subject = message.Subject;
        // MimeKit normalises Date to the message's own offset; CreatedAt is the stored UTC time.
        mime.Date = message.CreatedAt;

        // Stable, deterministic Message-Id from the BID so a client threads/dedups across fetches.
        mime.MessageId = MessageIdFromBid(message.Bid, mailDomain);

        // Verbatim real packet address(es): the sender, then every recipient — one folded header so
        // nothing is lost even if a client ignores the display names.
        var verbatim = new List<string> { fromPacket };
        verbatim.AddRange(message.Recipients.Select(r => RecipientAddress(r, message.At)));
        mime.Headers.Add(PacketAddressHeader, string.Join(", ", verbatim));

        mime.Body = BuildBody(message, extraAttachments, reflowText, bodyText);
        return mime;
    }

    /// <summary>The sender's packet address. A stored sender is a bare callsign (SSID-stripped, §1.5).</summary>
    private static string SenderAddress(Message message) => message.From;

    /// <summary>
    /// A recipient's full packet address: the addressee callsign, plus the message's AT-route
    /// (<c>@&lt;route&gt;</c>) when the message carries one. A bulletin's "To" is its category/
    /// distribution — that is just the addressee string and rides the same path.
    /// </summary>
    private static string RecipientAddress(MessageRecipient recipient, string? at) =>
        string.IsNullOrEmpty(at) ? recipient.ToCall : $"{recipient.ToCall}@{at}";

    private static MailboxAddress Mailbox(string packetAddress, string mailDomain)
    {
        string addrSpec = PacketAddressCodec.Encode(packetAddress, mailDomain);
        return MailboxAddress.Parse(addrSpec) is MailboxAddress parsed
            ? new MailboxAddress(PacketAddressCodec.DisplayName(packetAddress), parsed.Address)
            : new MailboxAddress(PacketAddressCodec.DisplayName(packetAddress), addrSpec);
    }

    /// <summary>
    /// Derives a stable, deterministic Message-Id from the BID: <c>&lt;sanitised-bid@mailDomain&gt;</c>.
    /// The BID (e.g. <c>3331_GM8BPQ</c>) is already a compact token, but we sanitise any character
    /// outside the msg-id <c>dot-atom</c> set to <c>.</c> so the result is always a valid addr-spec id.
    /// </summary>
    public static string MessageIdFromBid(string bid, string mailDomain)
    {
        ArgumentException.ThrowIfNullOrEmpty(bid);
        ArgumentException.ThrowIfNullOrEmpty(mailDomain);

        var sb = new StringBuilder(bid.Length);
        foreach (char c in bid)
        {
            // RFC 5322 dot-atom-text: letters, digits and a small set of specials. Keep the common
            // BID characters (alnum, '_', '-') verbatim; map anything else to '.'.
            bool safe = char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';
            sb.Append(safe ? c : '.');
        }

        // MimeKit emits "<id>"; we hand it the bare token@domain and let it wrap.
        return $"{sb}@{mailDomain}";
    }

    /// <summary>
    /// The text body part. Plain fixed text by default; an RFC 3676 <c>format=flowed</c> part when
    /// <paramref name="reflowText"/>, so a client reflows prose to the screen (see <see cref="TextReflow"/>).
    /// </summary>
    private static TextPart BuildTextPart(Message message, bool reflowText, string? bodyText)
    {
        // The body is stored byte-transparent; render it the same way webmail does (UTF-8 when the bytes
        // are valid UTF-8 — an SMTP-submitted Unicode body, a gateway's smart quotes — else Latin-1) so
        // the reader sees the intended text rather than mojibake. A caller-supplied bodyText wins (e.g.
        // the inline-7plus-stripped body — already a decoded string). MimeKit picks the charset and a
        // safe transfer-encoding for whatever the resulting string needs.
        string body = bodyText ?? PacketText.DecodeBody(message.Body.Span);
        if (!reflowText)
        {
            return new TextPart(TextFormat.Plain) { Text = body };
        }

        var flowed = new TextPart(TextFormat.Plain) { Text = TextReflow.ToFormatFlowed(body) };
        flowed.ContentType.Parameters["format"] = "flowed";
        // Trailing spaces mark soft lines and are load-bearing; quoted-printable encodes a trailing
        // space as =20 so it survives the wire (a 7bit/8bit writer may drop it).
        flowed.ContentTransferEncoding = ContentEncoding.QuotedPrintable;
        return flowed;
    }

    private static MimeEntity BuildBody(
        Message message, IReadOnlyList<MessageAttachment>? extraAttachments, bool reflowText, string? bodyText)
    {
        TextPart textPart = BuildTextPart(message, reflowText, bodyText);

        IReadOnlyList<MessageAttachment> extras = extraAttachments ?? [];
        if (message.Attachments.Count == 0 && extras.Count == 0)
        {
            return textPart; // a plain text/plain message
        }

        var multipart = new Multipart("mixed") { textPart };
        foreach (MessageAttachment attachment in message.Attachments)
        {
            multipart.Add(BuildAttachment(attachment));
        }

        foreach (MessageAttachment attachment in extras)
        {
            multipart.Add(BuildAttachment(attachment));
        }

        return multipart;
    }

    private static MimePart BuildAttachment(MessageAttachment attachment)
    {
        // application/octet-stream unless a name gives an obvious type; the bytes are carried verbatim.
        ContentType contentType = GuessContentType(attachment.Name);
        var part = new MimePart(contentType)
        {
            Content = new MimeContent(new MemoryStream(attachment.Content.ToArray())),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = attachment.Name,
        };
        return part;
    }

    private static ContentType GuessContentType(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".txt" => new ContentType("text", "plain"),
            ".png" => new ContentType("image", "png"),
            ".jpg" or ".jpeg" => new ContentType("image", "jpeg"),
            ".gif" => new ContentType("image", "gif"),
            ".pdf" => new ContentType("application", "pdf"),
            ".zip" => new ContentType("application", "zip"),
            _ => new ContentType("application", "octet-stream"),
        };
    }
}
