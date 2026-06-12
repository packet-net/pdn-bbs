namespace Bbs.Core;

/// <summary>
/// The accumulation progress of one inbound 7plus file (schema v3 tracking, design.md "abstract
/// 7plus away from the user"). A 7plus file arrives as a run of part-bulletins trickling in over
/// time; the store tracks how many of the file's parts have been received so the webmail can show a
/// lightweight placeholder ("FIELDS.JPG — 3/5 parts received") until the set completes and a
/// synthesized assembled-file message replaces it.
/// </summary>
/// <param name="IdentityKey">The stable identity string grouping a file's parts (host-computed).</param>
/// <param name="HeaderName">The 7plus header file name, for display (e.g. <c>FIELDS.JPG</c>).</param>
/// <param name="ReceivedParts">How many distinct parts have been recorded so far.</param>
/// <param name="TotalParts">The total number of parts the file was split into (from the header).</param>
/// <param name="AssembledMessageNumber">
/// The synthesized <see cref="Message.LocalOnly"/> message once the file assembled, else null while
/// still accumulating (or if assembly was attempted and failed — the set then stays incomplete).
/// </param>
/// <param name="SourceType">
/// The message type (P/B/T) of the part-bulletins this file arrived as, so the webmail placeholder
/// shows in the matching list (inbox vs bulletins). Null when no source part is recorded yet.
/// </param>
public sealed record SevenPlusProgress(
    string IdentityKey,
    string HeaderName,
    int ReceivedParts,
    int TotalParts,
    long? AssembledMessageNumber,
    MessageType? SourceType = null)
{
    /// <summary>True once every part is present (the set is ready for an assembly attempt).</summary>
    public bool IsComplete => ReceivedParts >= TotalParts;
}
