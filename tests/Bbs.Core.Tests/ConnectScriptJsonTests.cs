using Bbs.Core;

namespace Bbs.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ConnectScriptJson"/> — the connect_script column (de)serialiser. The
/// load-bearing contract: anything that is not a well-formed JSON array of steps reads as a BLANK
/// script (so a legacy blob or a corrupt row makes the partner inbound-only, never crashes a read).
/// </summary>
public class ConnectScriptJsonTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NETROM\nC GB7BPQ-1")]      // a retired legacy newline-joined flat blob
    [InlineData("C GB7RDG")]
    [InlineData("[1,2,3]")]                 // JSON, but wrong shape
    [InlineData("[{\"open\":")]             // truncated / malformed JSON
    [InlineData("[NODE] welcome")]          // starts with '[' but is not JSON
    public void Deserialize_NonStructuredOrMalformed_ReadsAsBlank(string? stored)
        => Assert.Empty(ConnectScriptJson.Deserialize(stored));

    [Fact]
    public void Deserialize_JsonArray_RoundTripsTheSteps()
    {
        IReadOnlyList<ConnectStep> steps = ConnectScriptJson.Deserialize(" [{\"open\":\"GB7RDG\"},{\"expect\":\"=> \",\"send\":\"BBS\"}]");
        Assert.Equal(2, steps.Count);
        Assert.Equal("GB7RDG", steps[0].Open);
        Assert.Equal("=> ", steps[1].Expect);   // leading whitespace tolerated; trailing space preserved
        Assert.Equal("BBS", steps[1].Send);
    }

    [Fact]
    public void Serialize_EmptyScript_IsTheEmptyString()
        => Assert.Equal(string.Empty, ConnectScriptJson.Serialize([]));

    [Fact]
    public void Serialize_ThenDeserialize_RoundTripsEveryOptionField()
    {
        ConnectStep[] original =
        [
            new() { Open = "GB7BPQ-1", Port = "2" },
            new()
            {
                Expect = "=> ", ExpectAny = ["a", "b"], Send = @"\x1a",
                TimeoutSeconds = 30, Match = "regex", IgnoreCase = false,
                Eol = "crlf", Raw = true, Name = "enter",
            },
        ];
        string json = ConnectScriptJson.Serialize(original);
        IReadOnlyList<ConnectStep> reloaded = ConnectScriptJson.Deserialize(json);
        Assert.Equal(json, ConnectScriptJson.Serialize([.. reloaded])); // byte-stable round-trip

        // Field-by-field — record equality can't be used for the step carrying ExpectAny (List<string>
        // compares by reference, so two equal-content lists are not record-equal).
        Assert.Equal(new ConnectStep { Open = "GB7BPQ-1", Port = "2" }, reloaded[0]);
        ConnectStep s = reloaded[1];
        Assert.Equal("=> ", s.Expect);
        Assert.Equal(["a", "b"], s.ExpectAny);
        Assert.Equal(@"\x1a", s.Send);
        Assert.Equal(30, s.TimeoutSeconds);
        Assert.Equal("regex", s.Match);
        Assert.False(s.IgnoreCase);
        Assert.Equal("crlf", s.Eol);
        Assert.True(s.Raw);
        Assert.Equal("enter", s.Name);
    }
}
