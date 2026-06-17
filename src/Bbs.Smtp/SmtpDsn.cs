using System.Globalization;
using System.Text;

namespace Bbs.Smtp;

/// <summary>
/// Which delivery dispositions an SMTP client asked to be notified of, via <c>RCPT TO:&lt;…&gt; NOTIFY=…</c>
/// (RFC 3461 §4.1). A flags set so <c>NOTIFY=SUCCESS,DELAY</c> is both. <see cref="Never"/> is exclusive
/// (the client explicitly wants no DSN for this recipient) and is represented as <see cref="None"/> with
/// the other flags cleared. The wire default — no NOTIFY parameter at all — is <see cref="Failure"/> only
/// (RFC 3461 §5.1).
/// </summary>
[Flags]
public enum SmtpDsnNotify
{
    /// <summary>NOTIFY=NEVER, or no DSN wanted.</summary>
    None = 0,

    /// <summary>Notify on successful delivery/relay (the only disposition this submission server emits async).</summary>
    Success = 1,

    /// <summary>Notify on permanent failure (the wire default; here always a synchronous 5xx, so never async).</summary>
    Failure = 2,

    /// <summary>Notify on delay (this submission server has no queue/delay path, so accepted-and-ignored).</summary>
    Delay = 4,
}

/// <summary>
/// One recipient the session accepted at RCPT time: the decoded packet address that drives storage/
/// routing, the original recipient (ORCPT, or the addr-spec) to echo in a DSN, and the DSN dispositions
/// the client requested for it.
/// </summary>
/// <param name="Packet">The decoded packet address (e.g. <c>M0LTE@GB7RDG.GBR.EURO</c> or a bare <c>ALL</c>).</param>
/// <param name="Orcpt">The original recipient to report in a DSN (the <c>ORCPT=</c> value, else the RCPT addr-spec).</param>
/// <param name="Notify">Which dispositions the client asked to be notified of (RFC 3461 NOTIFY=).</param>
public sealed record SmtpAcceptedRecipient(string Packet, string Orcpt, SmtpDsnNotify Notify);

/// <summary>
/// Pure, static helpers for SMTP delivery-status notifications (RFC 3461 the extension, RFC 3464 the
/// report format): parsing the DSN parameters off MAIL/RCPT argument tails, and building the
/// human-readable + machine-readable "relayed" report body the submission server stores back to the
/// submitter when a recipient asked for <c>NOTIFY=SUCCESS</c>. Sans-IO and server-free so it is unit
/// testable on its own (mirrors <see cref="SmtpRecipientGrouping"/>).
/// </summary>
public static class SmtpDsn
{
    /// <summary>The From callsign every generated DSN carries (a reserved pseudo-call, like FBB's MAILER).</summary>
    public const string ReportFrom = "MAILER";

    /// <summary>The subject of a "relayed" success DSN.</summary>
    public const string RelayedSubject = "Delivery Status Notification (Relayed)";

    /// <summary>
    /// Parses the <c>NOTIFY=</c> parameter from a RCPT argument tail (everything after <c>TO:</c>). Returns
    /// the requested dispositions: <see cref="SmtpDsnNotify.Failure"/> when no NOTIFY is present (RFC 3461
    /// §5.1 default), <see cref="SmtpDsnNotify.None"/> for <c>NOTIFY=NEVER</c> (NEVER is exclusive and wins
    /// over any other token), else the union of SUCCESS / FAILURE / DELAY. Unknown tokens are ignored.
    /// </summary>
    public static SmtpDsnNotify ParseNotify(string rcptArgs)
    {
        ArgumentNullException.ThrowIfNull(rcptArgs);
        string? value = FindParam(rcptArgs, "NOTIFY");
        if (value is null)
        {
            return SmtpDsnNotify.Failure; // the wire default
        }

        SmtpDsnNotify result = SmtpDsnNotify.None;
        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(token, "NEVER", StringComparison.OrdinalIgnoreCase))
            {
                return SmtpDsnNotify.None; // NEVER is mutually exclusive (RFC 3461 §4.1)
            }

            result |= token.ToUpperInvariant() switch
            {
                "SUCCESS" => SmtpDsnNotify.Success,
                "FAILURE" => SmtpDsnNotify.Failure,
                "DELAY" => SmtpDsnNotify.Delay,
                _ => SmtpDsnNotify.None,
            };
        }

        return result;
    }

    /// <summary>Extracts the <c>ORCPT=</c> original-recipient value from a RCPT argument tail, or null if absent.</summary>
    public static string? ExtractOrcpt(string rcptArgs)
    {
        ArgumentNullException.ThrowIfNull(rcptArgs);
        string? value = FindParam(rcptArgs, "ORCPT");
        if (value is null)
        {
            return null;
        }

        // ORCPT is "addr-type;xtext-encoded-address" (RFC 3461 §4.2). We surface the address part,
        // xtext-decoded, for a friendly report; a bare value (no ';') is taken verbatim.
        int semi = value.IndexOf(';', StringComparison.Ordinal);
        string address = semi >= 0 ? value[(semi + 1)..] : value;
        return XtextDecode(address);
    }

    /// <summary>Extracts the <c>ENVID=</c> envelope id from a MAIL argument tail (everything after <c>FROM:</c>), or null.</summary>
    public static string? ExtractEnvId(string mailArgs)
    {
        ArgumentNullException.ThrowIfNull(mailArgs);
        string? value = FindParam(mailArgs, "ENVID");
        return value is null ? null : XtextDecode(value);
    }

    /// <summary>
    /// Builds a "relayed" delivery-status report body (RFC 3464 §2): a human-readable preamble naming each
    /// reported recipient, followed by the per-message and per-recipient delivery-status fields a DSN
    /// parser reads (Reporting-MTA, Original-Envelope-Id, then Final-Recipient / Action: relayed /
    /// Status: 2.0.0 per recipient). One body covering every SUCCESS-notify recipient of the submission.
    /// </summary>
    public static string BuildRelayedReport(
        string reportingMta, string originalSubject, string? envId, IEnumerable<string> recipients, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(reportingMta);
        ArgumentNullException.ThrowIfNull(originalSubject);
        ArgumentNullException.ThrowIfNull(recipients);

        string[] list = [.. recipients];
        var sb = new StringBuilder();

        // Human-readable part (RFC 3464 §2.2 first component): the plain-language notice.
        sb.Append("This is a delivery status notification.\r\n\r\n");
        sb.Append("Your message");
        if (originalSubject.Length > 0)
        {
            sb.Append(" (Subject: ").Append(originalSubject).Append(')');
        }

        sb.Append(" was relayed to the following recipient(s):\r\n");
        foreach (string r in list)
        {
            sb.Append("  ").Append(r).Append("\r\n");
        }

        // Machine-readable delivery-status part (RFC 3464 §2.3) — kept inline as text so it survives the
        // BBS's flat-body store and is readable over RF, while still carrying the structured fields.
        sb.Append("\r\n--- Delivery report follows ---\r\n");
        sb.Append("Reporting-MTA: dns;").Append(reportingMta).Append("\r\n");
        if (!string.IsNullOrEmpty(envId))
        {
            sb.Append("Original-Envelope-Id: ").Append(envId).Append("\r\n");
        }

        sb.Append("Arrival-Date: ").Append(now.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (string r in list)
        {
            sb.Append("\r\n");
            sb.Append("Final-Recipient: rfc822;").Append(r).Append("\r\n");
            sb.Append("Action: relayed\r\n");
            sb.Append("Status: 2.0.0\r\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Finds an <c>ESMTP</c> parameter (<c>NAME=value</c>) in a whitespace-separated argument tail,
    /// case-insensitively on the name, returning its value (empty string for a valueless <c>NAME=</c>) or
    /// null when absent. The leading <c>&lt;address&gt;</c>/bare-address token is skipped — only the
    /// trailing space-delimited params are scanned.
    /// </summary>
    private static string? FindParam(string args, string name)
    {
        foreach (string token in args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = token.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue; // the address token (or a flag param without a value we don't read)
            }

            if (string.Equals(token[..eq], name, StringComparison.OrdinalIgnoreCase))
            {
                return token[(eq + 1)..];
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes RFC 3461 <c>xtext</c>: a <c>+XX</c> hex escape becomes the byte 0xXX; all other characters
    /// are literal. Tolerant of a malformed escape (kept verbatim) so a quirky client never breaks the
    /// report.
    /// </summary>
    private static string XtextDecode(string xtext)
    {
        if (!xtext.Contains('+', StringComparison.Ordinal))
        {
            return xtext; // the common case — no escapes
        }

        var sb = new StringBuilder(xtext.Length);
        for (int i = 0; i < xtext.Length; i++)
        {
            if (xtext[i] == '+' && i + 2 < xtext.Length
                && Uri.IsHexDigit(xtext[i + 1]) && Uri.IsHexDigit(xtext[i + 2]))
            {
                int hi = Uri.FromHex(xtext[i + 1]);
                int lo = Uri.FromHex(xtext[i + 2]);
                sb.Append((char)((hi << 4) | lo));
                i += 2;
            }
            else
            {
                sb.Append(xtext[i]);
            }
        }

        return sb.ToString();
    }
}
