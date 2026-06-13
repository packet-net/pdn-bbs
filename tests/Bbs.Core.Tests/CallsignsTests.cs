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
}
