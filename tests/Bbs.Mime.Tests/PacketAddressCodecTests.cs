using Bbs.Mime;

namespace Bbs.Mime.Tests;

public sealed class PacketAddressCodecTests
{
    private const string MailDomain = "pkt.gb7pdn";

    [Theory]
    [InlineData("M0LTE@GB7RDG.#42.GBR.EURO")] // the # case — illegal in a real domain
    [InlineData("M0LTE")]                       // bare callsign
    [InlineData("M0LTE-7")]                      // SSID
    [InlineData("G4ABC@GB7BSK.#23.GBR.EURO")]   // a longer hierarchical route
    [InlineData("ALL")]                          // a bulletin category
    [InlineData("NEWS")]                         // another bulletin category
    public void Decode_OfEncode_RoundTripsByteExact(string packetAddress)
    {
        string addrSpec = PacketAddressCodec.Encode(packetAddress, MailDomain);

        Assert.EndsWith("@" + MailDomain, addrSpec, StringComparison.Ordinal);
        Assert.True(PacketAddressCodec.TryDecode(addrSpec, MailDomain, out string decoded));
        Assert.Equal(packetAddress, decoded); // byte-exact (ordinal)
    }

    [Fact]
    public void Encode_HashAddress_ProducesCharsetSafeLocalPart()
    {
        string addrSpec = PacketAddressCodec.Encode("M0LTE@GB7RDG.#42.GBR.EURO", MailDomain);
        string localPart = addrSpec[..addrSpec.LastIndexOf('@')];

        // No '#', and only RFC 4648 base32 characters (A-Z2-7) — safe in a local part.
        Assert.All(localPart, c => Assert.True(
            (c >= 'A' && c <= 'Z') || (c >= '2' && c <= '7'),
            $"local part char '{c}' is not base32"));
    }

    [Fact]
    public void TryDecode_LowercasedLocalPart_StillRoundTrips()
    {
        const string packetAddress = "M0LTE@GB7RDG.#42.GBR.EURO";
        string addrSpec = PacketAddressCodec.Encode(packetAddress, MailDomain);

        // Simulate a client that lower-cases the whole addr-spec.
        string lowered = addrSpec.ToLowerInvariant();

        Assert.True(PacketAddressCodec.TryDecode(lowered, MailDomain, out string decoded));
        Assert.Equal(packetAddress, decoded);
    }

    [Fact]
    public void TryDecode_DomainCaseInsensitive()
    {
        string addrSpec = PacketAddressCodec.Encode("M0LTE", MailDomain);
        string upperDomain = addrSpec[..(addrSpec.LastIndexOf('@') + 1)] + MailDomain.ToUpperInvariant();

        Assert.True(PacketAddressCodec.TryDecode(upperDomain, MailDomain, out string decoded));
        Assert.Equal("M0LTE", decoded);
    }

    [Fact]
    public void TryDecode_ExternalAddress_ReturnsFalse()
    {
        Assert.False(PacketAddressCodec.TryDecode("someone@gmail.com", MailDomain, out string decoded));
        Assert.Equal(string.Empty, decoded);
    }

    [Fact]
    public void TryDecode_OurDomainButNonBase32LocalPart_ReturnsFalse()
    {
        // '!' is not in the base32 alphabet — not one of ours even though the domain matches.
        Assert.False(PacketAddressCodec.TryDecode("not!base32@" + MailDomain, MailDomain, out _));
    }

    [Fact]
    public void TryDecode_NoAtSign_ReturnsFalse()
    {
        Assert.False(PacketAddressCodec.TryDecode("JUSTALOCALPART", MailDomain, out _));
    }

    [Fact]
    public void Encode_At64OctetLocalPartEdge_Succeeds()
    {
        // 40 octets => exactly 64 base32 chars => exactly the RFC 5321 cap.
        string longest = new('X', PacketAddressCodec.MaxPacketAddressLength);
        Assert.Equal(40, longest.Length);

        string addrSpec = PacketAddressCodec.Encode(longest, MailDomain);
        string localPart = addrSpec[..addrSpec.LastIndexOf('@')];
        Assert.Equal(PacketAddressCodec.MaxLocalPartLength, localPart.Length); // 64, the cap

        Assert.True(PacketAddressCodec.TryDecode(addrSpec, MailDomain, out string decoded));
        Assert.Equal(longest, decoded);
    }

    [Fact]
    public void Encode_OverLongAddress_Throws()
    {
        string tooLong = new('X', PacketAddressCodec.MaxPacketAddressLength + 1); // 41 octets => 66 base32 chars

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => PacketAddressCodec.Encode(tooLong, MailDomain));
        Assert.Contains("64-octet", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_RealisticLongHierarchicalRoute_StaysUnderCap()
    {
        // A realistic worst-case route is well under 40 octets, so it always fits.
        const string addr = "G4ABC@GB7BSK.#23.GBR.EURO";
        Assert.True(addr.Length <= PacketAddressCodec.MaxPacketAddressLength);

        string addrSpec = PacketAddressCodec.Encode(addr, MailDomain);
        Assert.True(PacketAddressCodec.TryDecode(addrSpec, MailDomain, out string decoded));
        Assert.Equal(addr, decoded);
    }

    [Fact]
    public void DisplayName_KeepsRealAddressFaithful()
    {
        Assert.Equal("M0LTE@GB7RDG.#42.GBR.EURO",
            PacketAddressCodec.DisplayName("M0LTE@GB7RDG.#42.GBR.EURO"));
    }

    [Fact]
    public void Example_HashAddress_EncodesToKnownLocalPart()
    {
        // Documents the exact base32 mapping for the headline example (a regression pin).
        string addrSpec = PacketAddressCodec.Encode("M0LTE@GB7RDG.#42.GBR.EURO", MailDomain);
        Assert.Equal("JUYEYVCFIBDUEN2SIRDS4IZUGIXEOQSSFZCVKUSP@pkt.gb7pdn", addrSpec);
    }

    [Fact]
    public void Example_BareCallsign_EncodesToKnownLocalPart()
    {
        Assert.Equal("JUYEYVCF@pkt.gb7pdn", PacketAddressCodec.Encode("M0LTE", MailDomain));
    }
}
