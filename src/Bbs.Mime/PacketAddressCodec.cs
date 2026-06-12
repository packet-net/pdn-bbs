using System.Text;

namespace Bbs.Mime;

/// <summary>
/// Reversible, stateless codec between a packet-radio mail address (e.g.
/// <c>M0LTE@GB7RDG.#42.GBR.EURO</c>, a bare <c>M0LTE</c>, an SSID <c>M0LTE-1</c>, or a bulletin
/// category like <c>NEWS</c>/<c>ALL</c>) and a synthetic RFC 5322 addr-spec an email client can carry.
/// </summary>
/// <remarks>
/// <para>
/// The problem: a packet address can contain <c>#</c> (the area marker, e.g. <c>#42</c>), which is
/// illegal in an email domain, and the full hierarchical route <b>cannot be re-derived</b> from a
/// fragment — so the whole address must survive a round-trip <b>losslessly and statelessly</b>
/// inside an addr-spec. We do that by base32-encoding the entire packet address into the local part:
/// <c>&lt;base32(packetAddress)&gt;@&lt;mailDomain&gt;</c>.
/// </para>
/// <para>
/// Why base32 (RFC 4648, alphabet <c>A-Z2-7</c>, no padding) and not base64:
/// <list type="bullet">
/// <item>Collision-free and exactly reversible over the raw address bytes.</item>
/// <item>Charset-safe in an email local part (no <c>+ / =</c> or other dot-atom-unsafe characters).</item>
/// <item><b>Case-insensitive-decodable</b>: some clients lower-case the local part. Base32's alphabet
///   is a single case, so we can upper-case before decoding and still recover the exact bytes — base64
///   is case-sensitive and would not survive a client lowercasing the local part.</item>
/// </list>
/// </para>
/// <para>
/// The encoding is the UTF-8 (== ASCII for any real packet address) bytes of the verbatim address, so
/// <c>Decode(Encode(x)) == x</c> byte-for-byte for every input. <see cref="TryDecode"/> returns false
/// for anything not in this scheme (a genuine external address such as <c>someone@gmail.com</c>).
/// </para>
/// </remarks>
public static class PacketAddressCodec
{
    // RFC 4648 base32 alphabet. Index = 5-bit value; value 0 => 'A' … 31 => '7'.
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// RFC 5321 §4.5.3.1.1 caps the local part at 64 octets. Base32 of an N-byte address is
    /// <c>ceil(N*8/5)</c> characters; 64 base32 chars decode to 40 bytes, so any packet address
    /// up to 40 octets fits. A pathological longer route would overflow the local part — we detect
    /// that on <see cref="Encode"/> and throw rather than emit an addr-spec a strict MTA rejects.
    /// </summary>
    public const int MaxLocalPartLength = 64;

    /// <summary>The longest packet address (in octets) whose base32 fits the 64-octet local part.</summary>
    public const int MaxPacketAddressLength = MaxLocalPartLength * 5 / 8; // 40

    /// <summary>
    /// Encodes a verbatim packet address into the synthetic addr-spec
    /// <c>&lt;base32(packetAddress)&gt;@&lt;mailDomain&gt;</c>.
    /// </summary>
    /// <param name="packetAddress">The full packet address, verbatim (e.g. <c>M0LTE@GB7RDG.#42.GBR.EURO</c>).</param>
    /// <param name="mailDomain">The BBS's synthetic mail domain (e.g. <c>pkt.gb7pdn</c>).</param>
    /// <returns>An RFC 5322 addr-spec whose local part losslessly carries the whole address.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the address is so long its base32 would exceed the 64-octet local-part cap
    /// (see <see cref="MaxPacketAddressLength"/>).
    /// </exception>
    public static string Encode(string packetAddress, string mailDomain)
    {
        ArgumentNullException.ThrowIfNull(packetAddress);
        ArgumentException.ThrowIfNullOrEmpty(mailDomain);

        byte[] bytes = Encoding.UTF8.GetBytes(packetAddress);
        if (bytes.Length > MaxPacketAddressLength)
        {
            throw new ArgumentException(
                $"Packet address '{packetAddress}' is {bytes.Length} octets; its base32 local part would " +
                $"exceed the RFC 5321 64-octet cap (max encodable address is {MaxPacketAddressLength} octets).",
                nameof(packetAddress));
        }

        string localPart = Base32Encode(bytes);
        return $"{localPart}@{mailDomain}";
    }

    /// <summary>
    /// Attempts to decode a synthetic addr-spec produced by <see cref="Encode"/> back to the exact
    /// original packet address.
    /// </summary>
    /// <param name="addrSpec">A candidate addr-spec (e.g. from an inbound email To/From header).</param>
    /// <param name="mailDomain">The same synthetic mail domain used to encode.</param>
    /// <param name="packetAddress">On success, the byte-exact original packet address.</param>
    /// <returns>
    /// <c>true</c> if <paramref name="addrSpec"/> is in our scheme and decoded; <c>false</c> for any
    /// address whose domain is not <paramref name="mailDomain"/> or whose local part is not valid base32
    /// (a genuine external address).
    /// </returns>
    public static bool TryDecode(string? addrSpec, string mailDomain, out string packetAddress)
    {
        ArgumentException.ThrowIfNullOrEmpty(mailDomain);
        packetAddress = string.Empty;

        if (string.IsNullOrEmpty(addrSpec))
        {
            return false;
        }

        int at = addrSpec.LastIndexOf('@');
        if (at < 0)
        {
            return false;
        }

        string domain = addrSpec[(at + 1)..];
        // Strip the domain case-insensitively — it is ours only if it matches the configured domain.
        if (!string.Equals(domain, mailDomain, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string localPart = addrSpec[..at];
        // Tolerate a client that lower-cased the local part: base32 is single-case, so upper-case first.
        if (!TryBase32Decode(localPart.ToUpperInvariant(), out byte[]? bytes))
        {
            return false;
        }

        packetAddress = Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>
    /// The human-readable display name for the MIME display name — the real packet address, kept
    /// faithful so the user recognises it. (Trimmed only; we deliberately do not rewrite the route.)
    /// </summary>
    public static string DisplayName(string packetAddress)
    {
        ArgumentNullException.ThrowIfNull(packetAddress);
        return packetAddress.Trim();
    }

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        // No padding (RFC 4648 §3.2 permits omission): ceil(bits/5) characters.
        int outputLength = (data.Length * 8 + 4) / 5;
        var sb = new StringBuilder(outputLength);

        int buffer = 0;
        int bitsInBuffer = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsInBuffer += 8;
            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                int index = (buffer >> bitsInBuffer) & 0x1F;
                sb.Append(Base32Alphabet[index]);
            }
        }

        if (bitsInBuffer > 0)
        {
            // Left-align the final partial group (low bits zero-filled).
            int index = (buffer << (5 - bitsInBuffer)) & 0x1F;
            sb.Append(Base32Alphabet[index]);
        }

        return sb.ToString();
    }

    private static bool TryBase32Decode(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0)
        {
            // The empty local part decodes to the empty address (Encode("") => "" local part).
            return true;
        }

        var output = new List<byte>(text.Length * 5 / 8 + 1);
        int buffer = 0;
        int bitsInBuffer = 0;
        foreach (char c in text)
        {
            int value = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                return false; // not base32 — a real external address
            }

            buffer = (buffer << 5) | value;
            bitsInBuffer += 5;
            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output.Add((byte)((buffer >> bitsInBuffer) & 0xFF));
            }
        }

        // Any leftover bits in a canonical no-pad encoding must be zero; reject a corrupt/foreign
        // local part whose trailing bits are set (it was not produced by Encode).
        if (bitsInBuffer > 0 && (buffer & ((1 << bitsInBuffer) - 1)) != 0)
        {
            return false;
        }

        bytes = [.. output];
        return true;
    }
}
