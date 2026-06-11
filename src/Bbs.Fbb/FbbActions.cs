namespace Bbs.Fbb;

/// <summary>An effect the host must carry out, returned by <see cref="FbbSession.Advance"/>.</summary>
public abstract record FbbAction;

/// <summary>
/// Transmit a protocol line. <see cref="Line"/> carries no terminator; the
/// transport MUST append CRLF — bare-CR framing is misparsed by LinBPQ
/// under load (spec §3.13.2, [M0LTE-IT]).
/// </summary>
public sealed record FbbSendLine(string Line) : FbbAction;

/// <summary>Transmit raw bytes (one framed SOH/STX/EOT message transfer).</summary>
public sealed record FbbSendBytes(ReadOnlyMemory<byte> Data) : FbbAction;

/// <summary>
/// A complete proposal block arrived and verified. The host must decide
/// each proposal (BID dedup, size guards, …) and call
/// <see cref="FbbSession.Advance"/> with <see cref="FbbProposalDecisions"/>;
/// the session is parked until then.
/// </summary>
public sealed record FbbProposalsReceived(IReadOnlyList<Proposal> Proposals) : FbbAction;

/// <summary>
/// An accepted inbound message decoded cleanly. <see cref="Body"/> is the
/// uncompressed plaintext: for FA, R: lines + body (the subject travels
/// only in the SOH header — spec §3.7); for FC, the whole B2 object
/// (spec §3.9).
/// </summary>
public sealed record FbbMessageDelivered(Proposal Proposal, string Title, ReadOnlyMemory<byte> Body) : FbbAction;

/// <summary>
/// The peer's FS verdict for one of our proposed messages — the host marks
/// forwarded on <see cref="FsAnswerKind.AlreadyHave"/>, re-queues later on
/// <see cref="FsAnswerKind.Defer"/>, keeps routeable-elsewhere on
/// <see cref="FsAnswerKind.Reject"/> (spec §3.4). For
/// <see cref="FsAnswerKind.Accept"/> the transfer bytes follow as
/// <see cref="FbbSendBytes"/>.
/// </summary>
public sealed record FbbOutboundResult(FbbOutboundMessage Message, FsAnswer Answer) : FbbAction;

/// <summary>
/// The session ended. <see cref="Graceful"/> is <see langword="true"/> for
/// the FF/FQ close (spec §3.1 step 5) — the host should now disconnect.
/// </summary>
public sealed record FbbSessionOver(bool Graceful) : FbbAction;

/// <summary>
/// A fatal protocol failure. <see cref="ErrorLine"/> is the exact
/// <c>*** …</c> line where the spec pins one (§3.12), or the peer's own
/// <c>***</c> line when the failure was reported by the far end. Always
/// followed by <see cref="FbbSessionOver"/>.
/// </summary>
public sealed record FbbProtocolError(string ErrorLine) : FbbAction;
