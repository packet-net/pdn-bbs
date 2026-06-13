using System.Text;

namespace Bbs.Mime;

/// <summary>
/// Converts a hard-wrapped plain-text body into RFC 3676 <c>format=flowed</c> so a mail client
/// (iPhone Mail) can <b>reflow prose to the screen width</b> instead of showing the sender's fixed
/// ~70-column wrap as ragged short lines — while leaving ASCII art, tables, R: headers and other
/// deliberately-laid-out lines exactly as they are.
/// </summary>
/// <remarks>
/// <para>
/// In <c>format=flowed</c> a line ending in a space is a <b>soft</b> break (the client may rejoin it
/// with the next line and rewrap); a line with no trailing space is a <b>hard</b> break (preserved).
/// We make a line soft only when it is confidently <i>wrapped prose</i>; everything else stays hard,
/// so layout-bearing text is never mangled. Lines starting with a space or <c>&gt;</c> are
/// space-stuffed (§4.4) so their leading whitespace survives — the receiver strips the one added space.
/// </para>
/// <para>
/// <b>The conservative "wrapped prose" test</b> — a line is made soft (joined to the next) only when
/// ALL hold: it is at least <see cref="MinFlowWidth"/> wide (a short line is a deliberate break — a
/// signature, list item, or the last line of a paragraph, never a mid-paragraph wrap); it starts with
/// a letter (not indented, not a bullet/quote/art edge); it has no run of two or more spaces (which
/// signals columns/alignment); it is not header-shaped (<c>R:</c>, <c>From:</c> …); its share of
/// art/box characters is low; and the <i>next</i> line is itself flowable prose to join to. The
/// trailing space then bridges the two fragments. ASCII art (leading spaces, double spaces, box
/// glyphs), tables, and short lines all fail the test and remain hard.
/// </para>
/// </remarks>
public static class TextReflow
{
    /// <summary>
    /// The minimum width for a line to be treated as a wrapped (soft) prose line. Hard-wrapped packet
    /// text wraps around 65–79 columns; a line shorter than this is a deliberate break (the end of a
    /// paragraph, a signature, a list item) and is left hard so it is never joined to its neighbour.
    /// </summary>
    public const int MinFlowWidth = 50;

    private const string ArtChars = "|/\\=+*_~<>#[]{}";

    /// <summary>
    /// Rewrites <paramref name="text"/> as <c>format=flowed</c> (CRLF line endings): soft-wraps
    /// confidently-prose lines, space-stuffs protected lines, and leaves everything else hard.
    /// </summary>
    public static string ToFormatFlowed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var sb = new StringBuilder(text.Length + 64);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool last = i == lines.Length - 1;

            // Space-stuffing (RFC 3676 §4.4): protect a leading space or '>' so it isn't mistaken for
            // flowed/quote structure; the receiver removes the one added space.
            string stuffed = line.StartsWith(' ') || line.StartsWith('>') ? " " + line : line;

            bool softWrap = !last
                && IsWrappedProse(line)
                && IsFlowableProse(NextNonStuffed(lines, i + 1));

            if (softWrap && !stuffed.EndsWith(' '))
            {
                sb.Append(stuffed).Append(' '); // trailing space ⇒ soft line
            }
            else
            {
                sb.Append(stuffed);
            }

            if (!last)
            {
                sb.Append("\r\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>The next line if present (for the join-target test), else null.</summary>
    private static string? NextNonStuffed(string[] lines, int index)
        => index < lines.Length ? lines[index] : null;

    /// <summary>A line that is a wrapped prose line: flowable prose AND wide enough to be a real wrap.</summary>
    private static bool IsWrappedProse(string line)
        => line.Length >= MinFlowWidth && IsFlowableProse(line);

    /// <summary>
    /// Whether a line looks like ordinary prose that may participate in flowing — the conservative
    /// gate that keeps art/tables/headers out. (Length is checked separately by the caller; this is
    /// the "shape" test, also used to qualify the join-target line.)
    /// </summary>
    private static bool IsFlowableProse(string? line)
    {
        if (string.IsNullOrEmpty(line) || !char.IsLetter(line[0]))
        {
            return false; // blank, indented, bullet/quote/art edge, or starts non-alpha
        }

        if (HasColumnGap(line))
        {
            return false; // a run of 2+ spaces not following sentence punctuation ⇒ columns / alignment
        }

        if (IsHeaderShaped(line))
        {
            return false; // R: / From: / Subject: … keep routing + headers hard
        }

        int art = 0;
        foreach (char c in line)
        {
            if (ArtChars.Contains(c, StringComparison.Ordinal))
            {
                art++;
            }
        }

        // A low absolute + relative art-character budget: a sentence has the odd dash/slash; a box or
        // separator line is dense with them.
        return art <= 3 && art * 10 <= line.Length;
    }

    /// <summary>
    /// Whether a line has a run of 2+ spaces used for <b>column alignment</b> (a table / ASCII layout)
    /// rather than ordinary prose spacing. The classic two-spaces-after-a-sentence (<c>"end.  Next"</c>)
    /// is allowed; any 2+-space run not following <c>. ? !</c> is treated as alignment and disqualifies
    /// the line from flowing.
    /// </summary>
    private static bool HasColumnGap(string line)
    {
        for (int i = 1; i < line.Length; i++)
        {
            if (line[i] != ' ' || line[i - 1] != ' ')
            {
                continue;
            }

            char beforeRun = i >= 2 ? line[i - 2] : '\0';
            if (beforeRun is not ('.' or '?' or '!'))
            {
                return true; // a non-sentence 2+-space gap ⇒ alignment
            }
        }

        return false;
    }

    /// <summary>A short leading token immediately followed by a colon (<c>R:</c>, <c>From:</c>) — a header/routing line.</summary>
    private static bool IsHeaderShaped(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon is < 1 or > 15)
        {
            return false;
        }

        for (int i = 0; i < colon; i++)
        {
            if (line[i] == ' ')
            {
                return false; // a space before the colon ⇒ it's prose with a mid-sentence colon
            }
        }

        return true;
    }
}
