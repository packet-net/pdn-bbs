namespace Bbs.Fbb;

/// <summary>An input event for <see cref="FbbSession.Advance"/>.</summary>
public abstract record FbbInput;

/// <summary>
/// Begins the session. An answerer emits its SID + prompt (spec §3.1 step 2);
/// a caller starts waiting for the peer's SID.
/// </summary>
public sealed record FbbStart : FbbInput;

/// <summary>
/// Raw bytes received from the peer. The session does its own framing:
/// CR/LF/CRLF-tolerant lines in the text phases (spec §3.13.2),
/// SOH/STX/EOT blocks during message transfer.
/// </summary>
public sealed record FbbPeerData(ReadOnlyMemory<byte> Data) : FbbInput;

/// <summary>
/// The host's accept/reject/defer policy answers for the proposal block
/// surfaced by <see cref="FbbProposalsReceived"/> — one
/// <see cref="FsAnswer"/> per proposal, in order (spec §3.4). The session
/// turns them into the <c>FS</c> line.
/// </summary>
public sealed record FbbProposalDecisions(IReadOnlyList<FsAnswer> Answers) : FbbInput;
