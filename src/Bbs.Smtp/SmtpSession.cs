using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Bbs.Core;
using Bbs.Mime;
using Bbs.SevenPlus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace Bbs.Smtp;

/// <summary>
/// How a <see cref="SmtpSession"/>'s transport is secured, which drives the EHLO advertisement and the
/// STARTTLS handling.
/// </summary>
public enum SmtpSessionMode
{
    /// <summary>
    /// TLS was established on accept (implicit TLS, port 465). The channel is already secure: EHLO
    /// advertises AUTH; a <c>STARTTLS</c> command is answered <c>503 Already using TLS</c>.
    /// </summary>
    Implicit,

    /// <summary>
    /// The session starts plaintext (port 587). EHLO advertises STARTTLS but NOT AUTH (RFC 3207 §4.3 —
    /// never offer plaintext auth on an unencrypted link); mail commands are refused until the client
    /// issues <c>STARTTLS</c>, which upgrades the transport in place. After the upgrade the session resets
    /// and behaves exactly like <see cref="Implicit"/> (AUTH advertised, STARTTLS no longer offered).
    /// </summary>
    StartTls,
}

/// <summary>
/// One SMTP submission connection's protocol engine: the state machine (greeting → EHLO → AUTH →
/// MAIL/RCPT/DATA) and command dispatch over a <see cref="SmtpConnection"/>, backed by a
/// <see cref="BbsStore"/>. This is a <b>submission</b> server (RFC 6409), not a relay — AUTH (RFC 4954,
/// PLAIN + LOGIN) is required before any mail command, and the stored message's From is always the
/// authenticated callsign (the MAIL FROM identity is never trusted). A submitted message is parsed with
/// MimeKit, its text body taken, decoded to packet recipients, and stored + routed exactly like a
/// webmail compose.
/// </summary>
/// <remarks>
/// Two conventions match the webmail compose path:
/// <list type="bullet">
/// <item>Attachments are 7plus-encoded into the body — the universal packet path (exactly webmail's
///   default <c>fileMode=7plus</c>): each attachment is appended to the text body as a 7plus block, so a
///   phone that attaches a photo yields a stored message whose body is the prose + the 7plus block(s),
///   which our IMAP renderer (and any inbound assembler at a recipient) decodes back to a file.</item>
/// <item>A recipient addressed to a token that is NOT a valid callsign (<see cref="Callsigns.IsCallsignShaped"/>)
///   is treated as a BULLETIN to that category (ALL, NEWS, SALE, …) and stored as a
///   <see cref="MessageType.Bulletin"/>; a callsign-shaped recipient is a <see cref="MessageType.Personal"/>.
///   One submission addressed to both kinds produces one personal draft plus one bulletin draft.</item>
/// </list>
/// Still deferred:
/// <list type="bullet">
/// <item>TODO(v1+): DSN / extended-status niceties.</item>
/// </list>
/// </remarks>
public sealed partial class SmtpSession
{
    /// <summary>The synthetic mail domain every packet address is rendered under (mirrors <c>ImapBackend.MailDomain</c>).</summary>
    public const string MailDomain = "pdn";

    private readonly SmtpConnection _connection;
    private readonly BbsStore _store;
    private readonly Action<Message> _onStored;
    private readonly int _maxMessageBytes;
    private readonly ILogger _logger;

    // Transport security. _mode is the configured listener mode; _secured tracks whether the channel is
    // currently encrypted: true from the start for Implicit, and flips to true on a successful STARTTLS
    // upgrade for StartTls. AUTH (and therefore mail) is only ever permitted while _secured is true.
    private readonly SmtpSessionMode _mode;
    private readonly X509Certificate2? _startTlsCertificate;
    private readonly TimeSpan _startTlsHandshakeTimeout;
    private bool _secured;

    private string? _callsign;
    private bool _mailFromSet;
    private readonly List<string> _recipients = [];
    private bool _quit;

    /// <summary>
    /// Creates an <see cref="SmtpSessionMode.Implicit"/> session over <paramref name="connection"/> backed
    /// by the BBS <paramref name="store"/> — the channel is already secure (implicit TLS) or deliberately
    /// plaintext, and the session never performs an in-band upgrade.
    /// </summary>
    /// <param name="connection">The line transport.</param>
    /// <param name="store">The store — credential verification and message storage.</param>
    /// <param name="onStored">Invoked once per stored message (the host wires this to routing).</param>
    /// <param name="maxMessageBytes">The DATA size cap, advertised in the EHLO SIZE extension.</param>
    /// <param name="logger">Logs auth outcomes (never the password); null = no-op.</param>
    public SmtpSession(
        SmtpConnection connection, BbsStore store, Action<Message> onStored, int maxMessageBytes, ILogger? logger = null)
        : this(connection, store, onStored, maxMessageBytes, SmtpSessionMode.Implicit, certificate: null, startTlsHandshakeTimeout: default, logger)
    {
    }

    /// <summary>
    /// Creates a session in the given <paramref name="mode"/>. A <see cref="SmtpSessionMode.StartTls"/>
    /// session is handed the <paramref name="certificate"/> and <paramref name="startTlsHandshakeTimeout"/>
    /// it needs to perform the in-band TLS upgrade on <c>STARTTLS</c>.
    /// </summary>
    /// <param name="connection">The line transport.</param>
    /// <param name="store">The store — credential verification and message storage.</param>
    /// <param name="onStored">Invoked once per stored message (the host wires this to routing).</param>
    /// <param name="maxMessageBytes">The DATA size cap, advertised in the EHLO SIZE extension.</param>
    /// <param name="mode">Whether the transport is already secure (implicit) or upgrades in band (STARTTLS).</param>
    /// <param name="certificate">The server cert for the STARTTLS upgrade; required for StartTls, else null.</param>
    /// <param name="startTlsHandshakeTimeout">Bounds the STARTTLS handshake (the implicit path's timeout).</param>
    /// <param name="logger">Logs auth outcomes (never the password); null = no-op.</param>
    public SmtpSession(
        SmtpConnection connection, BbsStore store, Action<Message> onStored, int maxMessageBytes,
        SmtpSessionMode mode, X509Certificate2? certificate, TimeSpan startTlsHandshakeTimeout, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(onStored);
        if (mode == SmtpSessionMode.StartTls)
        {
            ArgumentNullException.ThrowIfNull(certificate);
        }

        _connection = connection;
        _store = store;
        _onStored = onStored;
        _maxMessageBytes = maxMessageBytes;
        _mode = mode;
        _startTlsCertificate = certificate;
        _startTlsHandshakeTimeout = startTlsHandshakeTimeout;
        // Implicit-TLS is secure from the first byte; a StartTls session is plaintext until it upgrades.
        _secured = mode == SmtpSessionMode.Implicit;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Runs the session to completion: sends the greeting, then reads and dispatches commands until the
    /// client QUITs, the connection ends, or cancellation. Protocol errors are answered with status
    /// codes; only I/O faults propagate.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _connection.WriteAsync("220 pdn-bbs SMTP submission ready\r\n", cancellationToken).ConfigureAwait(false);

        while (!_quit && !cancellationToken.IsCancellationRequested)
        {
            string? line = await _connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break; // client closed the connection
            }

            await DispatchAsync(line, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(string line, CancellationToken cancellationToken)
    {
        // The verb is the first token, case-insensitive; the remainder is the argument (kept verbatim
        // for addresses, which are case-/spacing-sensitive inside the angle brackets).
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        string verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
        string rest = space < 0 ? string.Empty : line[(space + 1)..];

        switch (verb)
        {
            case "EHLO":
                await HandleEhloAsync(extended: true, cancellationToken).ConfigureAwait(false);
                break;
            case "HELO":
                await HandleEhloAsync(extended: false, cancellationToken).ConfigureAwait(false);
                break;
            case "STARTTLS":
                await HandleStartTlsAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "AUTH":
                if (await RequireSecureChannelAsync(cancellationToken).ConfigureAwait(false))
                {
                    await HandleAuthAsync(rest, cancellationToken).ConfigureAwait(false);
                }

                break;
            case "MAIL":
                if (await RequireSecureChannelAsync(cancellationToken).ConfigureAwait(false))
                {
                    await HandleMailAsync(rest, cancellationToken).ConfigureAwait(false);
                }

                break;
            case "RCPT":
                if (await RequireSecureChannelAsync(cancellationToken).ConfigureAwait(false))
                {
                    await HandleRcptAsync(rest, cancellationToken).ConfigureAwait(false);
                }

                break;
            case "DATA":
                if (await RequireSecureChannelAsync(cancellationToken).ConfigureAwait(false))
                {
                    await HandleDataAsync(cancellationToken).ConfigureAwait(false);
                }

                break;
            case "RSET":
                ResetTransaction();
                await _connection.WriteAsync("250 2.0.0 Ok\r\n", cancellationToken).ConfigureAwait(false);
                break;
            case "NOOP":
                await _connection.WriteAsync("250 2.0.0 Ok\r\n", cancellationToken).ConfigureAwait(false);
                break;
            case "VRFY":
                // We never confirm or deny a local address (RFC 5321 §3.5.3 permits 252).
                await _connection.WriteAsync("252 2.1.5 Cannot VRFY user\r\n", cancellationToken).ConfigureAwait(false);
                break;
            case "QUIT":
                await _connection.WriteAsync("221 2.0.0 Bye\r\n", cancellationToken).ConfigureAwait(false);
                _quit = true;
                break;
            default:
                await _connection.WriteAsync("500 5.5.2 Unrecognized command\r\n", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    // ------------------------------------------------------------------ greeting / capabilities

    private async Task HandleEhloAsync(bool extended, CancellationToken cancellationToken)
    {
        // EHLO/HELO resets any in-progress transaction (RFC 5321 §4.1.4) but not the authentication.
        ResetTransaction();

        if (!extended)
        {
            await _connection.WriteAsync("250 pdn-bbs\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Multiline EHLO: every line but the last uses "250-", the last "250 ". The capability set depends
        // on whether the channel is secure yet:
        //   * Secure (implicit TLS, or after a STARTTLS upgrade) → advertise AUTH; never advertise STARTTLS.
        //   * StartTls before the upgrade → advertise STARTTLS but NOT AUTH (RFC 3207 §4.3: never offer
        //     plaintext auth on an unencrypted link; iOS will not auth pre-TLS either).
        var sb = new StringBuilder();
        sb.Append("250-pdn-bbs\r\n");
        if (_secured)
        {
            sb.Append("250-AUTH PLAIN LOGIN\r\n");
        }
        else
        {
            sb.Append("250-STARTTLS\r\n");
        }

        sb.Append("250-8BITMIME\r\n");
        sb.Append($"250 SIZE {_maxMessageBytes}\r\n");
        await _connection.WriteAsync(sb.ToString(), cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ STARTTLS (RFC 3207)

    /// <summary>
    /// Gate for AUTH/MAIL/RCPT/DATA on a STARTTLS session: returns true when the command may proceed (the
    /// channel is secure), otherwise writes <c>530 Must issue a STARTTLS command first</c> and returns
    /// false. On an implicit session the channel is always secure, so this is a pass-through.
    /// </summary>
    private async Task<bool> RequireSecureChannelAsync(CancellationToken cancellationToken)
    {
        if (_secured)
        {
            return true;
        }

        await _connection.WriteAsync("530 5.7.0 Must issue a STARTTLS command first\r\n", cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Handles the <c>STARTTLS</c> command (RFC 3207). On an already-secure channel (implicit TLS, or a
    /// second STARTTLS after upgrade) it answers <c>503 Already using TLS</c>. Otherwise it answers
    /// <c>220 Ready to start TLS</c>, upgrades the connection's transport in place, and — on success —
    /// RESETS the whole session (any prior EHLO/HELO + MAIL/RCPT, and there is no auth to discard): the
    /// client MUST send a fresh EHLO over the encrypted channel (RFC 3207 §4.2). The connection's upgrade
    /// also discards any input buffered before the handshake, so a pipelined pre-STARTTLS command is never
    /// honoured. On TLS failure the session ends and the connection closes.
    /// </summary>
    private async Task HandleStartTlsAsync(CancellationToken cancellationToken)
    {
        if (_secured)
        {
            await _connection.WriteAsync("503 5.5.1 Already using TLS\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        // We only reach here on a StartTls-mode session, which always carries a certificate.
        if (_startTlsCertificate is null)
        {
            await _connection.WriteAsync("454 4.7.0 TLS not available\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        await _connection.WriteAsync("220 2.0.0 Ready to start TLS\r\n", cancellationToken).ConfigureAwait(false);

        try
        {
            await _connection.UpgradeToServerTlsAsync(_startTlsCertificate, _startTlsHandshakeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The handshake failed or timed out; close the connection (RFC 3207 §4.1 — no plaintext recovery).
            LogStartTlsFailed(_logger, ex);
            _quit = true;
            return;
        }

        // Upgrade succeeded — reset ALL prior state (RFC 3207 §4.2). There is no AUTH to clear (we forbid it
        // pre-TLS); discard the in-progress transaction and require a fresh EHLO over the encrypted channel.
        _secured = true;
        _callsign = null;
        ResetTransaction();
        LogStartTlsUpgraded(_logger);
    }

    // ------------------------------------------------------------------ auth (RFC 4954)

    private async Task HandleAuthAsync(string rest, CancellationToken cancellationToken)
    {
        if (_callsign is not null)
        {
            await _connection.WriteAsync("503 5.5.1 Already authenticated\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        int space = rest.IndexOf(' ', StringComparison.Ordinal);
        string mechanism = (space < 0 ? rest : rest[..space]).ToUpperInvariant();
        string initial = space < 0 ? string.Empty : rest[(space + 1)..].Trim();

        switch (mechanism)
        {
            case "PLAIN":
                await HandleAuthPlainAsync(initial, cancellationToken).ConfigureAwait(false);
                break;
            case "LOGIN":
                await HandleAuthLoginAsync(initial, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await _connection.WriteAsync("504 5.5.4 Unsupported authentication mechanism\r\n", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleAuthPlainAsync(string initial, CancellationToken cancellationToken)
    {
        // The client may inline the SASL initial response, else we prompt with an empty challenge.
        string base64 = initial;
        if (base64.Length == 0)
        {
            await _connection.WriteAsync("334 \r\n", cancellationToken).ConfigureAwait(false);
            string? response = await _connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                _quit = true;
                return;
            }

            base64 = response.Trim();
            if (base64 == "*")
            {
                await _connection.WriteAsync("501 5.7.0 Authentication cancelled\r\n", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (!TryDecodePlain(base64, out string user, out string password))
        {
            LogAuthMalformed(_logger, "PLAIN");
            await _connection.WriteAsync("501 5.5.2 Malformed AUTH PLAIN response\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        await CompleteAuthAsync("PLAIN", user, password, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleAuthLoginAsync(string initial, CancellationToken cancellationToken)
    {
        // AUTH LOGIN: base64("Username:") then base64("Password:") prompts. The client may inline the
        // username as the initial response; otherwise we prompt for it.
        string userBase64 = initial;
        if (userBase64.Length == 0)
        {
            await _connection.WriteAsync("334 VXNlcm5hbWU6\r\n", cancellationToken).ConfigureAwait(false); // "Username:"
            string? line = await _connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                _quit = true;
                return;
            }

            userBase64 = line.Trim();
        }

        await _connection.WriteAsync("334 UGFzc3dvcmQ6\r\n", cancellationToken).ConfigureAwait(false); // "Password:"
        string? passLine = await _connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (passLine is null)
        {
            _quit = true;
            return;
        }

        if (!TryDecodeBase64(userBase64, out string user) || !TryDecodeBase64(passLine.Trim(), out string password))
        {
            LogAuthMalformed(_logger, "LOGIN");
            await _connection.WriteAsync("501 5.5.2 Malformed AUTH LOGIN response\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        await CompleteAuthAsync("LOGIN", user, password, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a decoded username/password with the same logic as <c>ImapBackend.Authenticate</c>:
    /// strip a trailing <c>@domain</c> from the email-form username (callsigns never contain <c>@</c>),
    /// then <see cref="BbsStore.VerifyMailPassword"/>. On success the session operates as the normalised
    /// base callsign. Logs the outcome with the username + mechanism + password length (never the
    /// password itself).
    /// </summary>
    private async Task CompleteAuthAsync(string mechanism, string user, string password, CancellationToken cancellationToken)
    {
        int at = user.IndexOf('@', StringComparison.Ordinal);
        string bareUser = at >= 0 ? user[..at] : user;

        if (bareUser.Length == 0 || !_store.VerifyMailPassword(bareUser, password))
        {
            LogAuthFailed(_logger, mechanism, user, password.Length);
            await _connection.WriteAsync("535 5.7.8 Authentication credentials invalid\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        _callsign = Callsigns.StripSsid(Callsigns.Normalize(bareUser));
        LogAuthOk(_logger, mechanism, _callsign);
        await _connection.WriteAsync("235 2.7.0 Authentication successful\r\n", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decodes a SASL PLAIN response: <c>base64(authzid NUL authcid NUL passwd)</c> (RFC 4616).</summary>
    private static bool TryDecodePlain(string base64, out string user, out string password)
    {
        user = string.Empty;
        password = string.Empty;
        if (!TryDecodeBase64Raw(base64, out byte[] raw))
        {
            return false;
        }

        string decoded = Encoding.UTF8.GetString(raw);
        string[] parts = decoded.Split('\0');
        if (parts.Length != 3)
        {
            return false;
        }

        user = parts[1]; // authcid (the authzid in parts[0] is ignored)
        password = parts[2];
        return user.Length > 0;
    }

    private static bool TryDecodeBase64(string base64, out string text)
    {
        text = string.Empty;
        if (!TryDecodeBase64Raw(base64, out byte[] raw))
        {
            return false;
        }

        text = Encoding.UTF8.GetString(raw);
        return true;
    }

    private static bool TryDecodeBase64Raw(string base64, out byte[] raw)
    {
        raw = [];
        try
        {
            raw = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ mail / rcpt / data

    private async Task HandleMailAsync(string rest, CancellationToken cancellationToken)
    {
        if (_callsign is null)
        {
            await _connection.WriteAsync("530 5.7.0 Authentication required\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        // "MAIL FROM:<addr> [params]". We accept (and ignore) the address and any SIZE= param — the
        // stored From is ALWAYS the authenticated callsign, never the trusted-blind MAIL FROM identity.
        if (!rest.StartsWith("FROM:", StringComparison.OrdinalIgnoreCase))
        {
            await _connection.WriteAsync("501 5.5.4 Syntax: MAIL FROM:<address>\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        _mailFromSet = true;
        _recipients.Clear();
        await _connection.WriteAsync("250 2.1.0 Ok\r\n", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRcptAsync(string rest, CancellationToken cancellationToken)
    {
        if (_callsign is null)
        {
            await _connection.WriteAsync("530 5.7.0 Authentication required\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_mailFromSet)
        {
            await _connection.WriteAsync("503 5.5.1 Need MAIL before RCPT\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!rest.StartsWith("TO:", StringComparison.OrdinalIgnoreCase))
        {
            await _connection.WriteAsync("501 5.5.4 Syntax: RCPT TO:<address>\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        string? addrSpec = ExtractAddrSpec(rest["TO:".Length..]);
        if (addrSpec is null || !PacketAddressCodec.TryDecode(addrSpec, MailDomain, out string packet))
        {
            await _connection.WriteAsync("550 5.1.3 Bad recipient address\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_recipients.Contains(packet))
        {
            _recipients.Add(packet);
        }

        await _connection.WriteAsync("250 2.1.5 Ok\r\n", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleDataAsync(CancellationToken cancellationToken)
    {
        if (_callsign is null)
        {
            await _connection.WriteAsync("530 5.7.0 Authentication required\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_mailFromSet)
        {
            await _connection.WriteAsync("503 5.5.1 Need MAIL before DATA\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_recipients.Count == 0)
        {
            await _connection.WriteAsync("554 5.5.1 No valid recipients\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        await _connection.WriteAsync("354 End data with <CR><LF>.<CR><LF>\r\n", cancellationToken).ConfigureAwait(false);

        byte[] raw;
        try
        {
            raw = await _connection.ReadDataAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The message exceeded the size cap (RFC 1870 §6.3).
            await _connection.WriteAsync("552 5.3.4 Message exceeds maximum size\r\n", cancellationToken).ConfigureAwait(false);
            ResetTransaction();
            return;
        }

        string subject;
        byte[] body;
        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            MimeMessage parsed = MimeMessage.Load(stream, cancellationToken);
            subject = parsed.Subject ?? string.Empty;

            // The text body; if there is none, flatten to empty. (An inline text/plain part is the body,
            // never an attachment — only true attachments below are 7plus-encoded.)
            string text = parsed.TextBody ?? string.Empty;

            // Attachments are 7plus-encoded into the body — the universal packet path (exactly webmail
            // compose's default fileMode=7plus). The 7plus parts are appended VERBATIM: their wire-faithful
            // CRLF separators are NOT run through any CR line discipline, because a 7plus code line can
            // legitimately contain byte 0x85 which string.ReplaceLineEndings would treat as the Unicode
            // line break U+0085 NEL and rewrite — corrupting the line and breaking reassembly. The body is
            // shared by every recipient group, then encoded for MessageDraft.Body via PacketText.EncodeBody:
            // ASCII / Latin-1 stays byte-transparent (unchanged on the wire, and the only branch a body
            // carrying a 7plus blob can take — its alphabet is <= 0xFC), while a body whose prose carries a
            // character above U+00FF (€, emoji, CJK) is stored as UTF-8 so it survives losslessly instead of
            // being mapped to '?' (the prior Encoding.Latin1.GetBytes was lossy). The display path
            // (PacketText.DecodeBody / webmail) reads it back UTF-8-or-Latin-1.
            body = PacketText.EncodeBody(BuildBody(text, parsed));
        }
        catch (Exception ex) when (ex is FormatException or System.IO.IOException)
        {
            LogParseFailed(_logger, ex);
            await _connection.WriteAsync("554 5.6.0 Could not parse message\r\n", cancellationToken).ConfigureAwait(false);
            ResetTransaction();
            return;
        }

        int stored = 0;
        // One MessageDraft per distinct (Type, At): recipients may carry different routes (MessageDraft.At
        // is message-level) AND different kinds — a callsign-shaped recipient is a Personal, a non-callsign
        // token (ALL, NEWS, …) is a Bulletin to that category. A submission addressed to both kinds yields
        // one Personal draft plus one Bulletin draft; the common case — one recipient — is one draft.
        foreach (SmtpRecipientGroup group in SmtpRecipientGrouping.Group(_recipients))
        {
            var draft = new MessageDraft
            {
                Type = group.Type,
                From = _callsign!,
                Recipients = group.Calls,
                At = group.At,
                Subject = subject,
                Body = body,
            };

            Message message = _store.AddMessage(draft);
            _onStored(message);
            stored++;
        }

        if (stored == 0)
        {
            await _connection.WriteAsync("554 5.5.1 No deliverable recipients\r\n", cancellationToken).ConfigureAwait(false);
            ResetTransaction();
            return;
        }

        LogQueued(_logger, _callsign!, _recipients.Count, stored);
        ResetTransaction();
        await _connection.WriteAsync("250 2.0.0 Ok: queued\r\n", cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Builds the stored body string from the parsed message: the text body, then each true attachment
    /// 7plus-encoded and appended after a blank-line separator (mirrors webmail compose's default
    /// <c>fileMode=7plus</c>). <see cref="MimeMessage.Attachments"/> yields the entities MimeKit flagged as
    /// attachments (Content-Disposition <c>attachment</c>); we only encode <see cref="MimePart"/>s (parts
    /// with content), and skip any zero-length part. The 7plus parts are appended VERBATIM (their own CRLF
    /// separators) and are NOT subjected to any CR line discipline — a 7plus code line can carry byte 0x85
    /// which <see cref="string.ReplaceLineEndings()"/> would mangle as U+0085 NEL. The overall message-size
    /// cap is already enforced by the DATA reader; 7plus expands the source ~12% but stays within it.
    /// </summary>
    private static string BuildBody(string text, MimeMessage parsed)
    {
        var sb = new StringBuilder(text);
        foreach (MimeEntity entity in parsed.Attachments)
        {
            if (entity is not MimePart { Content: { } content })
            {
                continue; // a message/rfc822 or multipart attachment (or a content-less part) has no byte blob
            }

            var part = (MimePart)entity;
            using var ms = new MemoryStream();
            content.DecodeTo(ms);
            byte[] bytes = ms.ToArray();
            if (bytes.Length == 0)
            {
                continue; // 7plus refuses a zero-length file; nothing to encode
            }

            // A bare leaf filename (defend against a path-shaped Content-Disposition name); the codec
            // handles the DOS-8.3 + extended long-name lines, so we just give it the real name.
            string fileName = SafeFileName(part.FileName);

            // A blank line separates the prose (or the prior block) from this 7plus block.
            if (sb.Length > 0)
            {
                sb.Append("\r\n");
            }

            // VERBATIM: append the encoded parts exactly as the codec emitted them (CRLF-separated, the
            // 7plus default the inbound scanner expects). Do NOT run these through ReplaceLineEndings.
            foreach (string encoded in SevenPlusEncoder.Encode(bytes, fileName))
            {
                sb.Append(encoded);
            }
        }

        return sb.ToString();
    }

    /// <summary>Strips any directory component from an attachment filename and falls back to a bare default.</summary>
    private static string SafeFileName(string? name)
    {
        string candidate = name ?? string.Empty;
        int slash = Math.Max(candidate.LastIndexOf('/'), candidate.LastIndexOf('\\'));
        string bare = (slash >= 0 ? candidate[(slash + 1)..] : candidate).Trim();
        return bare.Length == 0 ? "attachment.bin" : bare;
    }

    /// <summary>Clears the in-progress MAIL/RCPT transaction (RSET, EHLO, post-DATA). Auth is preserved.</summary>
    private void ResetTransaction()
    {
        _mailFromSet = false;
        _recipients.Clear();
    }

    /// <summary>
    /// Extracts the addr-spec from a <c>&lt;address&gt;</c> path (RFC 5321 §4.1.2), tolerating a trailing
    /// parameter list after the closing bracket and a bare unbracketed address. Returns null when no
    /// address is present.
    /// </summary>
    public static string? ExtractAddrSpec(string pathAndParams)
    {
        ArgumentNullException.ThrowIfNull(pathAndParams);
        string s = pathAndParams.Trim();
        int open = s.IndexOf('<', StringComparison.Ordinal);
        if (open >= 0)
        {
            int close = s.IndexOf('>', open + 1);
            if (close < 0)
            {
                return null;
            }

            string inner = s[(open + 1)..close].Trim();
            return inner.Length == 0 ? null : inner;
        }

        // No brackets: take the first whitespace-delimited token (an address possibly followed by params).
        int space = s.IndexOf(' ', StringComparison.Ordinal);
        string bare = (space < 0 ? s : s[..space]).Trim();
        return bare.Length == 0 ? null : bare;
    }

    // Auth logging — operational/audit. The password is NEVER logged; only its length, which is enough
    // to tell e.g. a fat-fingered passphrase or an iOS-AutoFilled "strong password" from the real one.
    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP auth ok: {Callsign} via {Mechanism}")]
    private static partial void LogAuthOk(ILogger logger, string mechanism, string callsign);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMTP auth FAILED via {Mechanism}: username '{User}' (password length {PasswordLength}) — no matching callsign+mail-password")]
    private static partial void LogAuthFailed(ILogger logger, string mechanism, string user, int passwordLength);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMTP auth: malformed {Mechanism} response")]
    private static partial void LogAuthMalformed(ILogger logger, string mechanism);

    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP: {Callsign} submitted a message to {Recipients} recipient(s), stored as {Messages} message(s)")]
    private static partial void LogQueued(ILogger logger, string callsign, int recipients, int messages);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMTP: failed to parse a submitted message")]
    private static partial void LogParseFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP: STARTTLS upgrade succeeded; awaiting a fresh EHLO over the encrypted channel.")]
    private static partial void LogStartTlsUpgraded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMTP: STARTTLS upgrade failed; closing the connection.")]
    private static partial void LogStartTlsFailed(ILogger logger, Exception ex);
}
