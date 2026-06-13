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

    /// <summary>A safe attachment filename: the leaf name of the 7plus header/extended name, or a fallback.</summary>
    private static string SafeName(string fileName)
    {
        string leaf = Path.GetFileName(fileName.Trim());
        return string.IsNullOrWhiteSpace(leaf) ? "decoded.bin" : leaf;
    }
}
