using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Bbs.Mime;

/// <summary>
/// Reversible, stateless codec between a packet-radio mail address (e.g.
/// <c>M0LTE@GB7RDG.#42.GBR.EURO</c>, a bare <c>M0LTE</c>, an SSID <c>M0LTE-1</c>, or a bulletin
/// category like <c>NEWS</c>/<c>ALL</c>) and a synthetic RFC 5322 addr-spec an email client can carry.
/// </summary>
/// <remarks>
/// <para>
/// The hard case: a packet address can contain <c>#</c> (the area marker, e.g. <c>#42</c>), which is
/// illegal in an email domain, and the full hierarchical route <b>cannot be re-derived</b> from a
/// fragment — so the whole address must survive a round-trip <b>losslessly and statelessly</b>
/// inside an addr-spec. But most packet addresses are perfectly DNS-shaped, and forcing those through
/// an opaque blob makes for an ugly, unrecognisable <c>To:</c>. So the codec is a <b>readable-first
/// hybrid</b>:
/// <list type="bullet">
/// <item><b>Readable form</b> — when every element is a valid DNS label, the address maps to ordinary
///   labels: <c>M0LTE</c> → <c>M0LTE@&lt;mailDomain&gt;</c>; <c>M0LTE@GB7RDG.GBR.EURO</c> →
///   <c>M0LTE@gb7rdg.gbr.euro.&lt;mailDomain&gt;</c>. The route rides the domain in lower case and is
///   upper-cased on decode, surviving a client that lower-cases what it echoes back.</item>
/// <item><b>Base32 escape</b> — only when an element can't be a domain label (chiefly <c>#</c>) does the
///   whole address go base32 (RFC 4648, <c>A-Z2-7</c>, no padding) into the local part, tagged with a
///   reserved subdomain: <c>M0LTE@GB7RDG.#42.GBR.EURO</c> →
///   <c>&lt;base32&gt;@b32.&lt;mailDomain&gt;</c>.</item>
/// </list>
/// </para>
/// <para>
/// Base32 (not base64) for the escape because it is collision-free and exactly reversible over the raw
/// bytes, charset-safe in a local part (no <c>+ / =</c>), and <b>case-insensitive-decodable</b> — its
/// single-case alphabet survives a client lower-casing the local part, which base64 would not.
/// </para>
/// <para>
/// Either form decodes back to the exact canonical (upper-cased) address, so
/// <c>Decode(Encode(x)) == x</c> for every input. <see cref="TryDecode"/> returns false for anything
/// not in this scheme (a genuine external address such as <c>someone@gmail.com</c>).
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
    /// The reserved subdomain label that tags a base32-escaped address (<c>&lt;base32&gt;@b32.&lt;mailDomain&gt;</c>),
    /// so the decoder knows to base32-decode the local part rather than read it as a readable route.
    /// </summary>
    private const string EncodedMarkerLabel = "b32";

    /// <summary>
    /// Encodes a packet address into a synthetic addr-spec, <b>readable</b> wherever the address can be
    /// expressed as ordinary domain labels:
    /// <list type="bullet">
    /// <item><c>M0LTE</c> → <c>M0LTE@&lt;mailDomain&gt;</c></item>
    /// <item><c>M0LTE@GB7RDG</c> → <c>M0LTE@gb7rdg.&lt;mailDomain&gt;</c></item>
    /// <item><c>M0LTE@GB7RDG.GBR.EURO</c> → <c>M0LTE@gb7rdg.gbr.euro.&lt;mailDomain&gt;</c> (no hash → fully readable)</item>
    /// </list>
    /// It falls back to the lossless <b>base32 escape</b> (<c>&lt;base32(address)&gt;@b32.&lt;mailDomain&gt;</c>)
    /// ONLY when a route element can't be a domain label — chiefly the <c>#</c> area marker
    /// (<c>M0LTE@GB7RDG.#42.GBR.EURO</c>), which is illegal in a domain. Either form decodes back to the
    /// exact (upper-cased canonical) address. Callsigns/regions are case-insensitive, so the address is
    /// canonicalised to upper case; the readable route rides the domain in lower case and upper-cases on
    /// decode, surviving a client that lower-cases what it echoes back.
    /// </summary>
    /// <param name="packetAddress">The full packet address (e.g. <c>M0LTE@GB7RDG.#42.GBR.EURO</c>, a bare <c>M0LTE</c>, a category <c>NEWS</c>).</param>
    /// <param name="mailDomain">The BBS's synthetic mail domain (e.g. <c>pdn</c>).</param>
    /// <exception cref="ArgumentException">
    /// Thrown only on the base32-escape path when the address is so long its base32 would exceed the
    /// 64-octet local-part cap (see <see cref="MaxPacketAddressLength"/>).
    /// </exception>
    public static string Encode(string packetAddress, string mailDomain)
    {
        ArgumentNullException.ThrowIfNull(packetAddress);
        ArgumentException.ThrowIfNullOrEmpty(mailDomain);

        // Canonicalise to upper case (callsigns/regions are case-insensitive) so the round-trip is
        // stable even when a client lower-cases the address it echoes back.
        string addr = packetAddress.Trim().ToUpperInvariant();

        if (TryReadableForm(addr, mailDomain, out string? readable))
        {
            return readable;
        }

        // Escape: a route element is not a valid domain label (the '#' area marker, or another
        // unrepresentable char), so the WHOLE address goes base32 into the local part, tagged with the
        // reserved subdomain.
        byte[] bytes = Encoding.UTF8.GetBytes(addr);
        if (bytes.Length > MaxPacketAddressLength)
        {
            throw new ArgumentException(
                $"Packet address '{packetAddress}' is {bytes.Length} octets; its base32 local part would " +
                $"exceed the RFC 5321 64-octet cap (max encodable address is {MaxPacketAddressLength} octets).",
                nameof(packetAddress));
        }

        return $"{Base32Encode(bytes)}@{EncodedMarkerLabel}.{mailDomain}";
    }

    /// <summary>
    /// Attempts to decode a synthetic addr-spec produced by <see cref="Encode"/> back to the exact
    /// canonical (upper-cased) packet address — readable form or base32 escape.
    /// </summary>
    /// <param name="addrSpec">A candidate addr-spec (e.g. from an inbound email To/From header).</param>
    /// <param name="mailDomain">The same synthetic mail domain used to encode.</param>
    /// <param name="packetAddress">On success, the original packet address (canonical upper case).</param>
    /// <returns>
    /// <c>true</c> if <paramref name="addrSpec"/> is in our scheme (its domain is <paramref name="mailDomain"/>
    /// or ends <c>.&lt;mailDomain&gt;</c>) and decoded; <c>false</c> for a genuine external address.
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

        string local = addrSpec[..at];
        string lowerDomain = addrSpec[(at + 1)..].ToLowerInvariant();
        string lowerMail = mailDomain.ToLowerInvariant();

        // Bare readable: <call>@<mailDomain>.
        if (string.Equals(lowerDomain, lowerMail, StringComparison.Ordinal))
        {
            string bare = local.ToUpperInvariant();
            if (!IsLabel(bare))
            {
                return false;
            }

            packetAddress = bare;
            return true;
        }

        // Everything else must sit under our domain: <middle>.<mailDomain>.
        if (!lowerDomain.EndsWith("." + lowerMail, StringComparison.Ordinal))
        {
            return false;
        }

        string middle = lowerDomain[..^(lowerMail.Length + 1)];

        // Base32 escape: <base32>@b32.<mailDomain>.
        if (string.Equals(middle, EncodedMarkerLabel, StringComparison.Ordinal))
        {
            if (!TryBase32Decode(local.ToUpperInvariant(), out byte[]? bytes))
            {
                return false;
            }

            packetAddress = Encoding.UTF8.GetString(bytes);
            return true;
        }

        // Readable route: <call>@<gb7rdg.gbr.euro>.<mailDomain> → CALL@GB7RDG.GBR.EURO.
        string upLocal = local.ToUpperInvariant();
        string upRoute = middle.ToUpperInvariant();
        if (!IsLabel(upLocal) || !upRoute.Split('.').All(IsLabel))
        {
            return false;
        }

        packetAddress = $"{upLocal}@{upRoute}";
        return true;
    }

    /// <summary>
    /// Builds the readable addr-spec when every element is a valid domain label, else returns false so
    /// the caller takes the base32 escape. The local part is the callsign/category; the route (if any)
    /// becomes lower-case domain labels.
    /// </summary>
    private static bool TryReadableForm(string addr, string mailDomain, [NotNullWhen(true)] out string? readable)
    {
        readable = null;

        int at = addr.IndexOf('@');
        string local = at < 0 ? addr : addr[..at];
        string route = at < 0 ? string.Empty : addr[(at + 1)..];

        if (!IsLabel(local))
        {
            return false; // the callsign/category part isn't a clean single label
        }

        if (route.Length == 0)
        {
            readable = $"{local}@{mailDomain}";
            return true;
        }

        // Every route element must be a valid DNS label — a '#' area marker fails here → base32 escape.
        if (!route.Split('.').All(IsLabel))
        {
            return false;
        }

        // Reserved-marker collision guard: a single-label route that equals the encoded marker would
        // alias the base32 subdomain — push it through base32 instead (vanishingly rare).
        if (string.Equals(route, EncodedMarkerLabel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        readable = $"{local}@{route.ToLowerInvariant()}.{mailDomain}";
        return true;
    }

    /// <summary>A valid DNS/local label: 1–63 chars of [A-Za-z0-9-], not leading or trailing with a hyphen.</summary>
    private static bool IsLabel(string s)
    {
        if (s.Length is 0 or > 63 || s[0] == '-' || s[^1] == '-')
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

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
