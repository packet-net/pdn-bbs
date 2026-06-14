using Bbs.Core;

namespace Bbs.Core.Tests;

/// <summary>
/// Unit tests for <see cref="Callsigns.IsCallsignShaped"/> — the conservative amateur-callsign shape test
/// that tells a personal recipient from a bulletin category. It is a SHAPE test (does it look like a
/// callsign), not a registry lookup: it never confirms a callsign is licensed/issued.
/// </summary>
public sealed class CallsignsTests
{
    [Theory]
    [InlineData("M0LTE")]   // 1-char prefix, digit, 3 letters
    [InlineData("G0ABC")]   // 1-char prefix, digit, 3 letters
    [InlineData("2E0XYZ")]  // 2-char prefix (digit allowed), digit, 3 letters
    [InlineData("VK2ABC")]  // 2-letter prefix, digit, 3 letters
    [InlineData("m0lte")]   // case-insensitive (normalised to upper)
    [InlineData("  G0ABC ")] // trimmed
    [InlineData("M0LTE-7")] // SSID stripped before the shape test
    public void IsCallsignShaped_RealCallsigns_True(string call)
    {
        Assert.True(Callsigns.IsCallsignShaped(call));
    }

    [Theory]
    [InlineData("ALL")]    // bulletin category — no digit in the callsign position
    [InlineData("NEWS")]
    [InlineData("SALE")]
    [InlineData("DX")]
    [InlineData("WANTED")] // 6 letters, no digit
    [InlineData("")]       // empty is never callsign-shaped
    [InlineData("   ")]    // whitespace only
    [InlineData("12345")]  // all digits, no trailing letters
    public void IsCallsignShaped_BulletinCategoriesAndJunk_False(string token)
    {
        Assert.False(Callsigns.IsCallsignShaped(token));
    }

    [Theory]
    [InlineData("M9YYY", 1, "M9YYY-1")]      // bare node call + default BBS SSID
    [InlineData("M9YYY-2", 1, "M9YYY-1")]    // node's own SSID stripped before deriving
    [InlineData(" m9yyy ", 1, "M9YYY-1")]    // trimmed + upper-cased
    [InlineData("M9YYY", 8, "M9YYY-8")]      // honours the requested SSID
    public void DeriveFromNode_DerivesBaseDashSsid(string nodeCall, int ssid, string expected)
    {
        Assert.Equal(expected, Callsigns.DeriveFromNode(nodeCall, ssid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-2")] // base is empty after stripping the SSID
    public void DeriveFromNode_NoUsableNodeCall_Null(string? nodeCall)
    {
        Assert.Null(Callsigns.DeriveFromNode(nodeCall, 1));
    }

    [Fact]
    public void SsidProbeCandidates_StartsAtDerivation_ThenWalksSkippingZeroAndNodeSsid()
    {
        // Node is M9YYY-2 (own SSID 2); the BBS derived M9YYY-1. The probe order is: -1 first,
        // then -3..-15 (skipping -2 = the node's own SSID), never -0.
        IReadOnlyList<string> order = Callsigns.SsidProbeCandidates("M9YYY-1", "M9YYY-2");

        Assert.Equal("M9YYY-1", order[0]);
        Assert.DoesNotContain("M9YYY-0", order);
        Assert.DoesNotContain("M9YYY-2", order); // the node's own SSID is skipped
        Assert.Contains("M9YYY-3", order);
        Assert.Contains("M9YYY-15", order);
        Assert.Equal(order.Count, order.Distinct().Count()); // no duplicates
        Assert.All(order, c => Assert.StartsWith("M9YYY-", c));
    }

    [Fact]
    public void SsidProbeCandidates_NoNodeSsid_SkipsOnlyZero()
    {
        // Bare node call (SSID 0). The walk after -1 is -2..-15; only -0 is excluded.
        IReadOnlyList<string> order = Callsigns.SsidProbeCandidates("M9YYY-1", "M9YYY");

        Assert.Equal("M9YYY-1", order[0]);
        Assert.Equal("M9YYY-2", order[1]);
        Assert.Equal(15, order.Count); // -1 … -15
        Assert.DoesNotContain("M9YYY-0", order);
    }

    [Fact]
    public void SsidProbeCandidates_NodeOwnsTheDefaultSsid_FirstCandidateIsNotTheNodeCall()
    {
        // The node runs at -1 and the BBS default SSID is also 1: the FIRST candidate must NOT be
        // the node's own on-air identity (M9YYY-1) — the skip applies to candidate[0] too.
        IReadOnlyList<string> order = Callsigns.SsidProbeCandidates("M9YYY-1", "M9YYY-1");

        Assert.NotEqual("M9YYY-1", order[0]);
        Assert.Equal("M9YYY-2", order[0]); // -1 is the node's own → first free is -2
        Assert.DoesNotContain("M9YYY-1", order);
        Assert.DoesNotContain("M9YYY-0", order);
        Assert.Equal(14, order.Count); // -2 … -15
    }
}
