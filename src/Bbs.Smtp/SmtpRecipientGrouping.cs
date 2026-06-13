using Bbs.Core;

namespace Bbs.Smtp;

/// <summary>
/// One group of recipients that share the same message <see cref="Type"/> and AT route — the shape of one
/// <c>MessageDraft</c>: the to-calls (or bulletin categories) plus the route they all forward via
/// (<see cref="At"/> null = local / no route).
/// </summary>
/// <param name="Type">Whether this group becomes a personal or a bulletin draft.</param>
/// <param name="At">The AT (@BBS) route the group's calls forward via, or null for none.</param>
/// <param name="Calls">The recipient to-calls (or bulletin categories) in this group, in first-seen order.</param>
public sealed record SmtpRecipientGroup(MessageType Type, string? At, IReadOnlyList<string> Calls);

/// <summary>
/// Splits a decoded packet address into its to-call and AT route, classifies it as a personal or a
/// bulletin, and groups a recipient list by (type, route). A decoded address is <c>CALL@ROUTE</c> (e.g.
/// <c>M0LTE@GB7RDG.GBR.EURO</c>) or a bare <c>CALL</c>; the split is on the FIRST <c>@</c> (the call
/// before, the route after, or null when there is no <c>@</c>). The call token is then classified by
/// shape: a callsign-shaped token (<see cref="Callsigns.IsCallsignShaped"/>) is a
/// <see cref="MessageType.Personal"/> recipient; anything else (ALL, NEWS, SALE, …) is the category of a
/// <see cref="MessageType.Bulletin"/>. Because <see cref="MessageDraft.Type"/> and
/// <see cref="MessageDraft.At"/> are message-level but recipients may differ in both, recipients are
/// grouped by their (type, AT) so one <c>MessageDraft</c> is created per distinct (type, route) — so a
/// single submission addressed to both a callsign and a category yields one personal draft and one
/// bulletin draft. The common case — one recipient — is one group. Pure and static so it is
/// unit-testable without a server.
/// </summary>
public static class SmtpRecipientGrouping
{
    /// <summary>
    /// Splits a decoded packet address on the first <c>@</c> into (to-call, AT route). A bare callsign
    /// has a null route. The route is empty-normalised to null.
    /// </summary>
    public static (string Call, string? At) Split(string packetAddress)
    {
        ArgumentNullException.ThrowIfNull(packetAddress);
        int at = packetAddress.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            return (packetAddress, null);
        }

        string call = packetAddress[..at];
        string route = packetAddress[(at + 1)..];
        return (call, route.Length == 0 ? null : route);
    }

    /// <summary>
    /// Classifies a (decoded) call token: a callsign-shaped token is a personal recipient, anything else
    /// is a bulletin category (compat spec — a non-callsign addressee is a bulletin).
    /// </summary>
    public static MessageType Classify(string call)
        => Callsigns.IsCallsignShaped(call) ? MessageType.Personal : MessageType.Bulletin;

    /// <summary>
    /// Groups the decoded packet addresses by their (message type, AT route), preserving first-seen order
    /// of both the groups and the calls within each group. Each group becomes one <c>MessageDraft</c>: a
    /// callsign-shaped recipient lands in a personal group, a non-callsign token in a bulletin group of
    /// that category. A submission to both kinds therefore produces a personal draft AND a bulletin draft.
    /// </summary>
    public static IReadOnlyList<SmtpRecipientGroup> Group(IEnumerable<string> packetAddresses)
    {
        ArgumentNullException.ThrowIfNull(packetAddresses);

        // Order-preserving group-by: the key is (type, route). A null route folds into the key with a
        // sentinel (a packet route can never be the NUL char) so it groups distinctly.
        var order = new List<(MessageType Type, string? At)>();
        var byKey = new Dictionary<(MessageType, string), List<string>>();
        const string NullKey = "\0";

        foreach (string addr in packetAddresses)
        {
            (string call, string? route) = Split(addr);
            MessageType type = Classify(call);
            (MessageType, string) key = (type, route ?? NullKey);
            if (!byKey.TryGetValue(key, out List<string>? calls))
            {
                calls = [];
                byKey[key] = calls;
                order.Add((type, route));
            }

            if (!calls.Contains(call))
            {
                calls.Add(call);
            }
        }

        var groups = new List<SmtpRecipientGroup>(order.Count);
        foreach ((MessageType type, string? route) in order)
        {
            groups.Add(new SmtpRecipientGroup(type, route, byKey[(type, route ?? NullKey)]));
        }

        return groups;
    }
}
