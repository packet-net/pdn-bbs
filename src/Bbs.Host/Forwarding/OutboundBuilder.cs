using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Microsoft.Extensions.Logging;

namespace Bbs.Host.Forwarding;

/// <summary>One queued store message rendered into the Fbb layer's outbound shape.</summary>
/// <param name="Number">The store message number (for <see cref="BbsStore.MarkForwarded"/>).</param>
/// <param name="Wire">What <see cref="FbbSession"/> proposes and transmits.</param>
public sealed record OutboundItem(long Number, FbbOutboundMessage Wire);

/// <summary>Identity fields stamped into outbound messages.</summary>
public sealed record BbsIdentity
{
    /// <summary>Our base BBS callsign (SSID-stripped — R: lines and BIDs carry the base call).</summary>
    public required string Callsign { get; init; }

    /// <summary>Our hierarchical route without the callsign (config hRoute); empty falls back to WW.</summary>
    public required string HRoute { get; init; }

    /// <summary>The software-version token of our R: line (spec §3.14 BPQ shape).</summary>
    public required string SoftwareVersion { get; init; }

    /// <summary>The R:-line route element: hRoute, or WW when unset.</summary>
    public string RLineRoute => string.IsNullOrWhiteSpace(HRoute) ? "WW" : HRoute.Trim();
}

/// <summary>
/// Builds <see cref="FbbOutboundMessage"/>s from the per-partner forward queue. The
/// transmitted plaintext is exactly what <see cref="FbbOutboundMessage.Body"/> documents
/// (spec §3.7: subject travels only in the SOH header): our R: line, the blank separator
/// when this BBS originated the message, then the stored body. The FA size is therefore
/// "UNCOMPRESSED message size … including the R: line" (spec §3.3) by construction.
/// </summary>
public static class OutboundBuilder
{
    /// <summary>
    /// Renders the queue for one partner, skipping (with a warning) anything over the
    /// partner's MaxTxSize. v1 deviation, named: LinBPQ holds oversize local messages
    /// (compat spec §4.1 "bigger local → held"); we leave them queued and skip them.
    /// </summary>
    public static IReadOnlyList<OutboundItem> Build(
        IReadOnlyList<Message> queue,
        Partner partner,
        BbsIdentity identity,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(partner);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        var items = new List<OutboundItem>(queue.Count);
        foreach (Message message in queue)
        {
            byte[] payload = ComposePayload(message, identity, time.GetUtcNow());
            if (payload.Length > partner.MaxTxSize)
            {
                LogOversize(logger, message.Number, payload.Length, partner.Call, partner.MaxTxSize, null);
                continue;
            }

            // Multi-recipient messages queue once per partner; the proposal names the
            // first recipient (v1 — LinBPQ fans out per-recipient copies, named deferral).
            string to = message.Recipients.Count > 0 ? message.Recipients[0].ToCall : message.From;

            items.Add(new OutboundItem(message.Number, new FbbOutboundMessage
            {
                MessageType = message.Type.ToCode(),
                From = FaProposal.NormalizeCallsign(message.From),
                // "when the message has no AT, senders use the partner's own callsign"
                // (spec §3.3 / FbbOutboundMessage contract).
                AtBbs = message.At is { Length: > 0 } at ? at : FaProposal.NormalizeCallsign(partner.Call),
                To = FaProposal.NormalizeCallsign(to),
                Bid = message.Bid,
                Title = message.Subject,
                Body = payload,
            }));
        }

        return items;
    }

    /// <summary>
    /// The transmitted plaintext. Our own R: line is prepended at send time — its message
    /// number is this BBS's local number, which only exists post-store (and LinBPQ's FA
    /// size comment pins the same model: "including the R: line the sender will prepend",
    /// spec §3.3). A body that already starts with an R: chain was relayed to us (the
    /// upstream chain + separator are already in place); anything else originated here and
    /// gets the §3.7 "blank line if first hop" separator.
    /// </summary>
    internal static byte[] ComposePayload(Message message, BbsIdentity identity, DateTimeOffset now)
    {
        string rLine = RLine.Format(
            now,
            (int)Math.Min(message.Number, int.MaxValue),
            identity.Callsign,
            identity.RLineRoute,
            identity.SoftwareVersion);

        ReadOnlySpan<byte> body = message.Body.Span;
        bool relayed = body.Length >= 2 && body[0] == (byte)'R' && body[1] == (byte)':';
        string prefix = relayed ? rLine + "\r" : rLine + "\r\r";

        byte[] prefixBytes = Encoding.Latin1.GetBytes(prefix);
        byte[] payload = new byte[prefixBytes.Length + body.Length];
        prefixBytes.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefixBytes.Length));
        return payload;
    }

    private static readonly Action<ILogger, long, int, string, int, Exception?> LogOversize =
        LoggerMessage.Define<long, int, string, int>(LogLevel.Warning, new EventId(1, "OversizeSkipped"),
            "Message {Number} ({Bytes} bytes) exceeds MaxTxSize for {Partner} ({MaxTx}); left queued and skipped");
}
