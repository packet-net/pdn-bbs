namespace Bbs.Smtp;

/// <summary>
/// One group of recipients that share the same AT route — the shape of one <c>MessageDraft</c>: the
/// to-calls plus the route they all forward via (<see cref="At"/> null = local / no route).
/// </summary>
/// <param name="At">The AT (@BBS) route the group's calls forward via, or null for none.</param>
/// <param name="Calls">The recipient to-calls in this group, in first-seen order.</param>
public sealed record SmtpRecipientGroup(string? At, IReadOnlyList<string> Calls);

/// <summary>
/// Splits a decoded packet address into its to-call and AT route, and groups a recipient list by route.
/// A decoded address is <c>CALL@ROUTE</c> (e.g. <c>M0LTE@GB7RDG.GBR.EURO</c>) or a bare <c>CALL</c>;
/// the split is on the FIRST <c>@</c> (the call before, the route after, or null when there is no
/// <c>@</c>). Because <see cref="MessageDraft.At"/> is message-level but recipients may carry different
/// routes, recipients are grouped by their AT value so one <c>MessageDraft</c> is created per distinct
/// route (the common case — one recipient — is one group). Pure and static so it is unit-testable
/// without a server.
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
    /// Groups the decoded packet addresses by their AT route, preserving first-seen order of both the
    /// groups and the calls within each group. Each group becomes one <c>MessageDraft</c>.
    /// </summary>
    public static IReadOnlyList<SmtpRecipientGroup> Group(IEnumerable<string> packetAddresses)
    {
        ArgumentNullException.ThrowIfNull(packetAddresses);

        // Order-preserving group-by: a null route is its own group (keyed by a sentinel).
        var order = new List<string?>();
        var byRoute = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        const string NullKey = "\0"; // a packet route can never be the NUL char

        foreach (string addr in packetAddresses)
        {
            (string call, string? route) = Split(addr);
            string key = route ?? NullKey;
            if (!byRoute.TryGetValue(key, out List<string>? calls))
            {
                calls = [];
                byRoute[key] = calls;
                order.Add(route);
            }

            if (!calls.Contains(call))
            {
                calls.Add(call);
            }
        }

        var groups = new List<SmtpRecipientGroup>(order.Count);
        foreach (string? route in order)
        {
            groups.Add(new SmtpRecipientGroup(route, byRoute[route ?? NullKey]));
        }

        return groups;
    }
}
