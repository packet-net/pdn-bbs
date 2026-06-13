using Bbs.Core;
using Bbs.SevenPlus;

namespace Bbs.Imap;

/// <summary>
/// Decodes any complete 7plus file carried <b>inline</b> in a message body into accessible
/// attachment bytes, for the IMAP renderer to surface as a real (e.g. image) attachment instead of a
/// wall of 7plus code-lines. This is the render-time complement to the inbound
/// <c>SevenPlusAssembler</c>: the assembler handles a file that arrives <em>across several
/// part-messages</em> (synthesising a decoded message and hiding the raw parts), whereas this handles
/// a file fully contained in <em>one</em> visible message — chiefly historical bulletins stored before
/// the assembler shipped, and small single-part files. A body that carries only some parts of a
/// multi-part file (so it can't be assembled standalone) yields nothing here; it stays the assembler's
/// job. A body with no 7plus markers scans cheaply to empty.
/// </summary>
public static class SevenPlusDecode
{
    /// <summary>
    /// The decoded attachments for every complete 7plus file found inline in <paramref name="message"/>'s
    /// body (one per distinct file identity present). Empty when the body carries no complete 7plus file.
    /// </summary>
    public static IReadOnlyList<MessageAttachment> DecodedAttachments(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        IReadOnlyList<SevenPlusPart> parts = SevenPlusScanner.ExtractParts(message.Body.Span);
        if (parts.Count == 0)
        {
            return [];
        }

        var decoded = new List<MessageAttachment>();
        foreach (IGrouping<SevenPlusPartIdentity, SevenPlusPart> group in parts.GroupBy(p => p.Identity))
        {
            (bool complete, byte[]? content, AssemblyReport report) = SevenPlusFile.TryAssemble([.. group]);
            if (complete && content is not null)
            {
                decoded.Add(new MessageAttachment(SafeName(report.FileName), content));
            }
        }

        return decoded;
    }

    /// <summary>
    /// Removes every complete inline 7plus block — a <c>go_7+.</c> header line through its matching
    /// <c>stop_7+.</c> footer line, inclusive (with the extended-name and code lines between) — from
    /// <paramref name="body"/>, leaving the surrounding prose. The caller pairs this with
    /// <see cref="DecodedAttachments"/>: once the 7plus file is surfaced as a real attachment, the raw
    /// code-line wall is just noise, so it is stripped from the rendered text body. A header with no
    /// matching footer (truncated/corrupt) drops to end-of-body — that trailing 7plus code is
    /// unreadable anyway. A body with no 7plus header returns unchanged.
    /// </summary>
    public static string StripInlineSevenPlus(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        // The 7plus block delimiters (the leading space of the magic header/footer absorbed by TrimStart):
        // header " go_7+. ", footer " stop_7+." (Bbs.SevenPlus.SevenPlusLines, internal there).
        const string headerToken = "go_7+.";
        const string footerToken = "stop_7+.";
        if (!body.Contains(headerToken, StringComparison.Ordinal))
        {
            return body;
        }

        string[] lines = body.ReplaceLineEndings("\n").Split('\n');
        var kept = new List<string>(lines.Length);
        bool inBlock = false;
        foreach (string line in lines)
        {
            string lead = line.TrimStart();
            if (!inBlock)
            {
                if (lead.StartsWith(headerToken, StringComparison.Ordinal))
                {
                    inBlock = true; // drop the header line and everything until the footer
                    continue;
                }

                kept.Add(line);
            }
            else if (lead.StartsWith(footerToken, StringComparison.Ordinal))
            {
                inBlock = false; // drop the footer line too, then resume keeping prose
            }
        }

        // Trim the trailing blank lines the removed block leaves behind so the body ends cleanly.
        return string.Join('\n', kept).TrimEnd('\n', '\r', ' ', '\t');
    }

    /// <summary>A safe attachment filename: the leaf name of the 7plus header/extended name, or a fallback.</summary>
    private static string SafeName(string fileName)
    {
        string leaf = Path.GetFileName(fileName.Trim());
        return string.IsNullOrWhiteSpace(leaf) ? "decoded.bin" : leaf;
    }
}
