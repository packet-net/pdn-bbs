using Bbs.Host.Forwarding;

namespace Bbs.Host.Tests;

/// <summary>Unit tests for the connect-script send transforms (escape expansion + line ending).</summary>
public class ConnectScriptTransformTests
{
    [Theory]
    [InlineData("BBS", "BBS")]
    [InlineData(@"a\rb", "a\rb")]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"\\", "\\")]
    [InlineData(@"\x1a", "\x1a")]              // Ctrl-Z
    [InlineData(@"\x1A", "\x1a")]              // hex is case-insensitive
    [InlineData(@"\x04\x04", "\x04\x04")]
    [InlineData(@"\xff", "ÿ")]            // full Latin1 byte
    [InlineData(@"\q", "q")]                   // unknown escape keeps the char verbatim
    [InlineData(@"end\", @"end\")]             // a trailing backslash is literal
    public void ExpandEscapes_ExpandsCStyleSequences(string input, string expected)
        => Assert.Equal(expected, ConnectScriptRunner.ExpandEscapes(input));

    [Theory]
    [InlineData("cr", "\r")]
    [InlineData("lf", "\n")]
    [InlineData("crlf", "\r\n")]
    [InlineData("none", "")]
    [InlineData("anything-else", "\r")]        // defaults to CR
    public void EolBytes_MapsTheTerminator(string eol, string expected)
        => Assert.Equal(expected, ConnectScriptRunner.EolBytes(eol));
}
