namespace Bbs.Fbb;

/// <summary>
/// One message queued for forwarding to the peer. The session proposes it
/// as an <c>FA</c> line (spec §3.3) and, on acceptance, compresses
/// <see cref="Body"/> into the negotiated container and frames it
/// (spec §3.6/§3.7).
/// </summary>
public sealed record FbbOutboundMessage
{
    /// <summary>Message type: <c>P</c>, <c>B</c> or <c>T</c> (spec §2.1).</summary>
    public required char MessageType { get; init; }

    /// <summary>Originating callsign (≤6 chars, SSID-stripped — spec §3.3).</summary>
    public required string From { get; init; }

    /// <summary>The @BBS field; when the message has no AT, senders use the partner's own callsign (spec §3.3).</summary>
    public required string AtBbs { get; init; }

    /// <summary>Destination callsign (≤6 chars — spec §3.3).</summary>
    public required string To { get; init; }

    /// <summary>The BID/MID (≤12 chars — spec §2.3).</summary>
    public required string Bid { get; init; }

    /// <summary>The subject — travels uncompressed in the SOH header only (spec §3.6/§3.7).</summary>
    public required string Title { get; init; }

    /// <summary>
    /// The uncompressed plaintext: the R: line(s) the host has already
    /// prepended (spec §3.14), the blank separator when originating, then
    /// the body. Its length is the advisory FA size field — "UNCOMPRESSED
    /// message size in bytes, including the R: line" (spec §3.3).
    /// </summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>
    /// The pre-built B2F object for this message (spec §3.9) — the host fills it (via
    /// <c>B2Message.Encode</c>) for partners it has enabled B2 on. The session ships it as an
    /// <c>FC EM</c> proposal + B1-framed transfer ONLY when B2F was negotiated for the session
    /// (<see cref="FbbSession.B2Active"/> = our offer ∩ the peer's SID); otherwise — including
    /// the B2-allowed-but-peer-is-B1-only fallback — it proposes <see cref="Body"/> as FA. Null
    /// when the host built no B2 object (a B1-only partner), so the FSM always falls back to FA.
    /// </summary>
    public ReadOnlyMemory<byte>? B2Object { get; init; }
}
