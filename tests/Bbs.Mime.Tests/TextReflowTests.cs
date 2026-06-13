using Bbs.Mime;

namespace Bbs.Mime.Tests;

/// <summary>
/// The RFC 3676 <c>format=flowed</c> reflow heuristic (<see cref="TextReflow"/>): wrapped prose is
/// soft-wrapped (trailing space ⇒ the client rejoins + reflows), while ASCII art, tables, headers,
/// signatures and short lines stay hard. A soft line ends with a space; a hard line does not.
/// </summary>
public sealed class TextReflowTests
{
    private static string[] Lines(string flowed) => flowed.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void WrappedProseParagraph_BecomesSoftWrapped_LastLineHard()
    {
        // Tom's example — note the two spaces after "England." (a prose convention, not a column).
        const string body =
            "Louis Varney (G5RV) conceived this antenna in 1946 while looking for a\r\n" +
            "compact multiband solution for his 100-foot garden in Stony Stratford,\r\n" +
            "England.  He published the design in the RSGB Bulletin in July 1958.\r\n" +
            "Varney was a former Captain in the Royal Corps of Signals, specializing\r\n" +
            "in HF interception and direction finding during WWII -- so he knew his\r\n" +
            "antenna theory cold.";

        string[] outLines = Lines(TextReflow.ToFormatFlowed(body));

        // The five full wrapped lines flow (trailing space); the short final line is a hard break.
        for (int i = 0; i < 5; i++)
        {
            Assert.EndsWith(" ", outLines[i]);
        }

        Assert.Equal("antenna theory cold.", outLines[5]); // short final line ⇒ hard break
        Assert.EndsWith(" ", outLines[2]); // "England.  He …" — two spaces after the period is prose, so it flows
        Assert.Contains("England.  He", outLines[2]); // and that internal double-space is left intact
    }

    [Fact]
    public void AsciiArt_IsPreserved_NoSoftWraps()
    {
        const string art =
            "   +----------+\r\n" +
            "   | G5RV ant |\r\n" +
            "   +----------+";

        string[] outLines = Lines(TextReflow.ToFormatFlowed(art));
        Assert.All(outLines, l => Assert.False(l.EndsWith(' '))); // never soft-wrapped (leading-space ⇒ not prose)
        Assert.Equal("    +----------+", outLines[0]);             // space-stuffed (one extra leading space)
        Assert.Equal("    | G5RV ant |", outLines[1]);
        Assert.Equal("    +----------+", outLines[2]);
    }

    [Fact]
    public void Table_WithColumnAlignment_IsPreserved()
    {
        const string table =
            "Band    SWR    Notes here to make the line long enough to exceed the width\r\n" +
            "40m     1.2    fine\r\n" +
            "20m     1.5    fine";

        string[] outLines = Lines(TextReflow.ToFormatFlowed(table));
        Assert.All(outLines, l => Assert.False(l.EndsWith(' '))); // column gaps ⇒ never flowed
    }

    [Fact]
    public void ShortLinesAndSignature_ArePreserved()
    {
        const string sig = "Thanks for the info, much appreciated and now I understand it all.\r\n73 de M0LTE\r\nTom";
        string[] outLines = Lines(TextReflow.ToFormatFlowed(sig));

        // Line 0 is full prose but its join-target "73 de M0LTE" starts with a digit (not prose), so it
        // stays hard; the signature lines are short and hard regardless.
        Assert.All(outLines, l => Assert.False(l.EndsWith(' ')));
        Assert.Equal("73 de M0LTE", outLines[1]);
        Assert.Equal("Tom", outLines[2]);
    }

    [Fact]
    public void RoutingHeaderLines_ArePreserved()
    {
        const string text = "R:260613/0259Z 12345@GB7RDG.#42.GBR.EURO and a long enough line to exceed width\r\nNext continuation line that is also quite long and full of ordinary prose words.";
        string[] outLines = Lines(TextReflow.ToFormatFlowed(text));
        Assert.False(outLines[0].EndsWith(' ')); // R: header is header-shaped ⇒ hard
    }

    [Fact]
    public void BlankLine_BreaksParagraphs_AndPrecedingLineStaysHard()
    {
        const string two =
            "First paragraph long enough to exceed the minimum flow width threshold here\r\n" +
            "and its continuation line that is also quite long and ordinary prose words.\r\n" +
            "\r\n" +
            "Second paragraph also long enough to exceed the minimum flow width over here\r\n" +
            "and its own continuation line that is again long and ordinary prose as well.";
        string[] outLines = Lines(TextReflow.ToFormatFlowed(two));

        Assert.EndsWith(" ", outLines[0]);          // flows into line 1
        Assert.False(outLines[1].EndsWith(' '));    // last line before the blank ⇒ hard
        Assert.Equal("", outLines[2]);              // blank preserved
        Assert.EndsWith(" ", outLines[3]);          // second paragraph flows
        Assert.False(outLines[4].EndsWith(' '));    // final line hard
    }

    [Fact]
    public void LeadingSpaceOrQuote_IsSpaceStuffed()
    {
        const string text = " indented line\r\n>quoted line\r\nnormal";
        string[] outLines = Lines(TextReflow.ToFormatFlowed(text));
        Assert.Equal("  indented line", outLines[0]); // space-stuffed (extra leading space)
        Assert.Equal(" >quoted line", outLines[1]);   // space-stuffed
        Assert.Equal("normal", outLines[2]);
    }

    [Fact]
    public void EmptyAndSingleLine_AreUnchanged()
    {
        Assert.Equal("", TextReflow.ToFormatFlowed(""));
        Assert.Equal("just one line", TextReflow.ToFormatFlowed("just one line"));
    }
}
