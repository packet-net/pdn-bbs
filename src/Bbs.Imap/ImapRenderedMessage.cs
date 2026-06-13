using System.Text;
using Bbs.Core;
using Bbs.Mime;
using MimeKit;

namespace Bbs.Imap;

/// <summary>
/// A stored <see cref="Message"/> rendered once into the bytes and IMAP wire-forms a FETCH serves:
/// the full RFC 822 message (CRLF line endings), the header/text byte ranges, and the parenthesised
/// <c>ENVELOPE</c> and <c>BODYSTRUCTURE</c> strings (RFC 3501 §7.4.2). Rendering is done with MimeKit
/// (<see cref="BbsMessageToMime"/> for the object graph), then we emit the IMAP-specific parenthesised
/// syntax ourselves — MimeKit gives the parts and headers; the IMAP forms are hand-built here.
/// </summary>
public sealed class ImapRenderedMessage
{
    private ImapRenderedMessage(byte[] full, int headerLength, MimeMessage mime)
    {
        Full = full;
        HeaderLength = headerLength;
        Mime = mime;
    }

    /// <summary>The complete message as RFC 822 bytes, CRLF-terminated (what <c>BODY[]</c>/<c>RFC822</c> returns).</summary>
    public byte[] Full { get; }

    /// <summary>
    /// The header section length in bytes, including the blank CRLF that terminates the headers — so
    /// <c>Full[..HeaderLength]</c> is <c>BODY[HEADER]</c>/<c>RFC822.HEADER</c> and <c>Full[HeaderLength..]</c>
    /// is <c>BODY[TEXT]</c>/<c>RFC822.TEXT</c> (RFC 3501 §6.4.5).
    /// </summary>
    public int HeaderLength { get; }

    /// <summary>The parsed MIME message (for ENVELOPE/BODYSTRUCTURE field extraction).</summary>
    public MimeMessage Mime { get; }

    /// <summary>The total octet count of the message — <c>RFC822.SIZE</c> (RFC 3501 §7.4.2).</summary>
    public int Size => Full.Length;

    /// <summary>The header section bytes (<c>BODY[HEADER]</c> / <c>RFC822.HEADER</c>).</summary>
    public ReadOnlyMemory<byte> Header => Full.AsMemory(0, HeaderLength);

    /// <summary>The body/text section bytes (<c>BODY[TEXT]</c> / <c>RFC822.TEXT</c>).</summary>
    public ReadOnlyMemory<byte> Text => Full.AsMemory(HeaderLength);

    /// <summary>
    /// Renders <paramref name="message"/>: builds the MimeMessage, serialises it to CRLF bytes with
    /// MimeKit's default writer, and locates the header/body split. The serialised bytes are the single
    /// source of truth for every byte-range FETCH so sizes and substrings are always self-consistent.
    /// </summary>
    public static ImapRenderedMessage Render(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Surface any complete 7plus file carried inline in the body as a real decoded attachment (so a
        // client sees the image/file, not a wall of code-lines), and render the text as format=flowed so
        // the sender's fixed-width wrap reflows to the screen (ASCII art / tables stay hard). When we do
        // surface a decoded attachment, strip the raw 7plus block from the text body so the reader sees
        // the prose + the attachment, not the prose AND the wall of code.
        IReadOnlyList<MessageAttachment> decoded = SevenPlusDecode.DecodedAttachments(message);
        string? bodyOverride = decoded.Count > 0
            ? SevenPlusDecode.StripInlineSevenPlus(message.GetBodyText())
            : null;
        MimeMessage mime = BbsMessageToMime.ToMimeMessage(
            message, ImapBackend.MailDomain, decoded, reflowText: true, bodyText: bodyOverride);

        // MimeKit writes CRLF line endings by default (FormatOptions.Default.NewLineFormat == Dos).
        using var stream = new MemoryStream();
        mime.WriteTo(stream);
        byte[] full = stream.ToArray();

        int headerLength = HeaderSectionLength(full);
        return new ImapRenderedMessage(full, headerLength, mime);
    }

    /// <summary>
    /// The length of the header section including the terminating blank line: the byte index just past
    /// the first <c>CRLF CRLF</c>. RFC 822 separates headers from the body with one blank line; the
    /// header section a client expects (<c>BODY[HEADER]</c>) includes that blank line.
    /// </summary>
    private static int HeaderSectionLength(byte[] full)
    {
        for (int i = 0; i + 3 < full.Length; i++)
        {
            if (full[i] == '\r' && full[i + 1] == '\n' && full[i + 2] == '\r' && full[i + 3] == '\n')
            {
                return i + 4; // include the CRLF CRLF
            }
        }

        // A message with no body (headers only): the whole thing is the header section.
        return full.Length;
    }

    /// <summary>
    /// The IMAP <c>ENVELOPE</c> for this message (RFC 3501 §7.4.2): a parenthesised list of
    /// date, subject, the address structures (from, sender, reply-to, to, cc, bcc), in-reply-to and
    /// message-id. Each address is <c>(name adl mailbox host)</c>; an absent group is <c>NIL</c>.
    /// </summary>
    public string BuildEnvelope()
    {
        var sb = new StringBuilder();
        sb.Append('(');
        // The envelope date is the message's Date as an RFC 822 date-time string (MimeKit's
        // DateUtils renders the exact form the Date header carries).
        sb.Append(Nstring(MimeKit.Utils.DateUtils.FormatDate(Mime.Date)));
        sb.Append(' ');
        sb.Append(Nstring(Mime.Subject));
        sb.Append(' ');
        sb.Append(AddressList(Mime.From.Mailboxes));
        sb.Append(' ');
        sb.Append(AddressList(Mime.From.Mailboxes)); // sender == from (we set no distinct Sender)
        sb.Append(' ');
        sb.Append(AddressList(Mime.From.Mailboxes)); // reply-to == from
        sb.Append(' ');
        sb.Append(AddressList(Mime.To.Mailboxes));
        sb.Append(' ');
        sb.Append(AddressList(Mime.Cc.Mailboxes));
        sb.Append(' ');
        sb.Append("NIL"); // bcc
        sb.Append(' ');
        sb.Append("NIL"); // in-reply-to
        sb.Append(' ');
        sb.Append(Nstring(WrapMessageId(Mime.MessageId)));
        sb.Append(')');
        return sb.ToString();
    }

    private static string AddressList(IEnumerable<MailboxAddress> mailboxes)
    {
        MailboxAddress[] list = [.. mailboxes];
        if (list.Length == 0)
        {
            return "NIL";
        }

        var sb = new StringBuilder();
        sb.Append('(');
        foreach (MailboxAddress mailbox in list)
        {
            sb.Append(Address(mailbox));
        }

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>One IMAP address structure <c>(personal-name source-route mailbox-name host-name)</c>.</summary>
    private static string Address(MailboxAddress mailbox)
    {
        string addr = mailbox.Address;
        int at = addr.LastIndexOf('@');
        string local = at < 0 ? addr : addr[..at];
        string host = at < 0 ? string.Empty : addr[(at + 1)..];

        var sb = new StringBuilder();
        sb.Append('(');
        sb.Append(Nstring(mailbox.Name)); // personal name
        sb.Append(' ');
        sb.Append("NIL"); // source route (deprecated, always NIL)
        sb.Append(' ');
        sb.Append(Nstring(local));
        sb.Append(' ');
        sb.Append(host.Length == 0 ? "NIL" : Nstring(host));
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// The IMAP <c>BODYSTRUCTURE</c> (RFC 3501 §7.4.2) for the whole message. A single-part message is
    /// one body-type; a multipart is the concatenated child bodies followed by the subtype. The
    /// extension data (<c>BODY[0]</c> vs <c>BODYSTRUCTURE</c>) is included only when
    /// <paramref name="extended"/> (a BODYSTRUCTURE fetch); a plain BODY fetch omits it.
    /// </summary>
    public string BuildBodyStructure(bool extended)
        => Mime.Body is { } body ? BodyPart(body, extended) : EmptyTextPart();

    /// <summary>A degenerate body-structure for a message with no body part (defensive — BBS messages always carry text).</summary>
    private static string EmptyTextPart() => "(\"TEXT\" \"PLAIN\" NIL NIL NIL \"7BIT\" 0 0)";

    private static string BodyPart(MimeEntity entity, bool extended)
    {
        if (entity is Multipart multipart)
        {
            var sb = new StringBuilder();
            sb.Append('(');
            foreach (MimeEntity child in multipart)
            {
                sb.Append(BodyPart(child, extended));
            }

            sb.Append(' ');
            sb.Append(Qstring(multipart.ContentType.MediaSubtype));
            if (extended)
            {
                // multipart extension data: body parameter list, disposition, language, location.
                sb.Append(' ');
                sb.Append(ParameterList(multipart.ContentType.Parameters));
                sb.Append(" NIL NIL NIL");
            }

            sb.Append(')');
            return sb.ToString();
        }

        return SinglePart((MimePart)entity, extended);
    }

    private static string SinglePart(MimePart part, bool extended)
    {
        ContentType ct = part.ContentType;
        (byte[] bytes, int lines) = PartContent(part);

        var sb = new StringBuilder();
        sb.Append('(');
        sb.Append(Qstring(ct.MediaType));
        sb.Append(' ');
        sb.Append(Qstring(ct.MediaSubtype));
        sb.Append(' ');
        sb.Append(ParameterList(ct.Parameters));
        sb.Append(' ');
        sb.Append(Nstring(part.ContentId));
        sb.Append(' ');
        sb.Append(Nstring(part.ContentDescription));
        sb.Append(' ');
        sb.Append(Qstring(EncodingToken(part.ContentTransferEncoding)));
        sb.Append(' ');
        sb.Append(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // text/* parts additionally carry the line count (RFC 3501 §7.4.2 body-type-text).
        if (ct.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(' ');
            sb.Append(lines.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (extended)
        {
            // single-part extension data: MD5, disposition, language, location.
            sb.Append(" NIL ");
            sb.Append(Disposition(part));
            sb.Append(" NIL NIL");
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string Disposition(MimePart part)
    {
        if (part.ContentDisposition is null)
        {
            return "NIL";
        }

        var sb = new StringBuilder();
        sb.Append('(');
        sb.Append(Qstring(part.ContentDisposition.Disposition));
        sb.Append(' ');
        sb.Append(ParameterList(part.ContentDisposition.Parameters));
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// The encoded-content bytes and (for text) line count of a single part, measured on the same
    /// transfer-encoded form a client receives — so the BODYSTRUCTURE octet count matches what a
    /// <c>BODY[n]</c> fetch of that part would return.
    /// </summary>
    private static (byte[] Bytes, int Lines) PartContent(MimePart part)
    {
        using var stream = new MemoryStream();
        part.Content?.WriteTo(stream);
        byte[] bytes = stream.ToArray();

        int lines = 0;
        foreach (byte b in bytes)
        {
            if (b == '\n')
            {
                lines++;
            }
        }

        return (bytes, lines);
    }

    private static string ParameterList(ParameterList parameters)
    {
        if (parameters.Count == 0)
        {
            return "NIL";
        }

        var sb = new StringBuilder();
        sb.Append('(');
        bool first = true;
        foreach (Parameter parameter in parameters)
        {
            if (!first)
            {
                sb.Append(' ');
            }

            first = false;
            sb.Append(Qstring(parameter.Name));
            sb.Append(' ');
            sb.Append(Qstring(parameter.Value));
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string EncodingToken(ContentEncoding encoding) => encoding switch
    {
        ContentEncoding.SevenBit => "7BIT",
        ContentEncoding.EightBit => "8BIT",
        ContentEncoding.Binary => "BINARY",
        ContentEncoding.Base64 => "BASE64",
        ContentEncoding.QuotedPrintable => "QUOTED-PRINTABLE",
        _ => "7BIT",
    };

    /// <summary>Wraps a bare message-id token in angle brackets the way the Message-Id header carries it.</summary>
    private static string? WrapMessageId(string? messageId)
        => string.IsNullOrEmpty(messageId) ? null : $"<{messageId}>";

    /// <summary>An IMAP <c>nstring</c>: a quoted string, or <c>NIL</c> when null.</summary>
    private static string Nstring(string? value) => value is null ? "NIL" : Qstring(value);

    /// <summary>
    /// An IMAP <c>string</c> as a quoted-string. Backslash and double-quote are escaped (RFC 3501 §9
    /// <c>quoted</c>); a value containing CR or LF — which a quoted-string may not hold — falls back to
    /// a literal <c>{n}CRLF...</c>. BBS subjects/addresses are single-line, so the literal path is rare.
    /// </summary>
    private static string Qstring(string value)
    {
        if (value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            return $"{{{bytes.Length}}}\r\n{value}";
        }

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            if (c is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        sb.Append('"');
        return sb.ToString();
    }
}
