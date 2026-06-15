namespace Bbs.Core;

/// <summary>
/// A partner's last forwarding-dial outcome, persisted in the <c>forwarding_status</c> table so the
/// status dashboard survives a node restart. <see cref="Ok"/> true means the last cycle reached the
/// partner and ran a session (the link works, even if it closed roughly); false carries the
/// <see cref="Error"/> and the running <see cref="ConsecutiveFailures"/> streak so the UI can say
/// "failing (N): …". Null (no row) means the partner has not been dialled.
/// </summary>
/// <param name="LastAttemptUtc">When the last dial was attempted.</param>
/// <param name="Ok">True if the last cycle reached the partner and ran without erroring.</param>
/// <param name="Error">The failure reason when <see cref="Ok"/> is false; null on success.</param>
/// <param name="ConsecutiveFailures">Consecutive failed cycles (0 when the last was a success).</param>
/// <param name="LastMode">The last negotiated protocol mode ("B2"/"B1"), or null if none has been observed.</param>
/// <param name="LastPeerSid">The peer's raw SID as last seen, or null if none has been observed.</param>
public sealed record PartnerForwardingState(
    DateTimeOffset LastAttemptUtc, bool Ok, string? Error, int ConsecutiveFailures,
    string? LastMode = null, string? LastPeerSid = null);
