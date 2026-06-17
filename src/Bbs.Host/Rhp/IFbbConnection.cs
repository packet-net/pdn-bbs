namespace Bbs.Host.Rhp;

/// <summary>
/// A duplex byte stream carrying ONE FBB forwarding session, abstracted over its transport so the
/// shared <see cref="Bbs.Host.Forwarding.FbbSessionRunner"/> (and thus the single FBB protocol FSM)
/// can drive a session over either bearer without duplication (issue #40):
/// <list type="bullet">
///   <item>an accepted AX.25/RHP child (<see cref="RhpChildConnection"/>), or</item>
///   <item>a raw-TCP forwarding connection (BPQ <c>FBBPORT</c>) accepted by the TCP listener.</item>
/// </list>
///
/// <para><b>Partner identity.</b> <see cref="RemoteCallsign"/> is the identity the forwarding policy
/// is keyed by. Over AX.25/RHP it is the link-layer source callsign the node reports; over
/// connectionless TCP there is no link-layer callsign, so the TCP transport sets it to the callsign
/// presented by the IN-PROTOCOL FBB login handshake (the "literal login name" BPQ's FBBPORT uses).
/// Either way the runner resolves the partner by base callsign and applies that partner's config.</para>
/// </summary>
public interface IFbbConnection
{
    /// <summary>
    /// The far station's callsign for partner lookup — the link-layer callsign (AX.25/RHP) or the
    /// in-protocol login callsign (raw TCP). May carry an SSID; partner matching is on the base call.
    /// </summary>
    string RemoteCallsign { get; }

    /// <summary>Awaits the next inbound chunk; <see langword="null"/> once the stream has closed.</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Sends bytes to the far station.</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Closes the stream. Best-effort; idempotent.</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}
