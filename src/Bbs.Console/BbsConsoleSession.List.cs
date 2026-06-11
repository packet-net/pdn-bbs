using System.Globalization;
using Bbs.Core;

namespace Bbs.Console;

/// <summary>The L family (compat spec §1.3) and the §1.6 listing output format.</summary>
public sealed partial class BbsConsoleSession
{
    /// <summary>
    /// Dispatches the L family (compat spec §1.3): bare L = new since last L (LR = same,
    /// oldest first); LM mine; LB/LP/LT by type; LN/LY/LF/L$/LD by status (LH/LK sysop-only);
    /// LL n last n; L n / L n- / L n-m ranges; L&lt; from, L&gt; to, L@ via-prefix. Option
    /// letters combine (LMP, LB&lt; G8BPQ, …). Bad letter → the verbatim
    /// "*** Error: Invalid List option %c".
    /// </summary>
    private async Task HandleListAsync(string verb, string[] args)
    {
        var spec = new ListSpec();
        var queue = new Queue<string>(args);

        string? parseError = ParseListOptions(verb, queue, spec);
        if (parseError is not null)
        {
            await WriteLineAsync(parseError).ConfigureAwait(false);
            return;
        }

        if (spec.Status is MessageStatus.Held or MessageStatus.Killed && !_isSysop)
        {
            // §1.3 verbatim: "LH or LK can only be used by SYSOP".
            await WriteLineAsync("LH or LK can only be used by SYSOP").ConfigureAwait(false);
            return;
        }

        // Bare L (only ordering chosen, nothing else): list new since last L (§1.3) — the
        // listed-pointer semantics behind "$Z" in the banner (§1.1).
        bool sinceLast = !spec.HasFilters;
        if (sinceLast)
        {
            User user = _store.GetUser(_userCall)!;
            spec.MinNumber = user.LastListedNumber + 1;
        }

        var query = new MessageQuery
        {
            Type = spec.Type,
            Status = spec.Status,
            MinNumber = spec.MinNumber,
            MaxNumber = spec.MaxNumber,
            ToCall = spec.Mine ? _userCall : spec.ToCall,
            FromCall = spec.FromCall,
            AtPrefix = spec.AtPrefix,
            Limit = spec.LastN,
            OldestFirst = spec.OldestFirst,
            IncludeHeld = _isSysop && spec.Status == MessageStatus.Held,
            IncludeKilled = _isSysop && spec.Status == MessageStatus.Killed,
        };

        IReadOnlyList<Message> messages = _store.ListMessages(query);
        if (messages.Count == 0)
        {
            // §1.6: "No Messages found" / "No New Messages".
            await WriteLineAsync(sinceLast ? "No New Messages" : "No Messages found").ConfigureAwait(false);
            return;
        }

        var lines = new List<string>(messages.Count);
        foreach (Message message in messages)
        {
            lines.Add(FormatListLine(message));
        }

        await WritePagedAsync(lines, listing: true).ConfigureAwait(false);

        if (sinceLast)
        {
            // Advance the listed pointer to the latest message ($Z, §1.1). Judgment call:
            // advanced even on a paging abort — the messages were offered.
            _store.SetLastListed(_userCall, _store.GetLatestMessageNumber());
        }
    }

    /// <summary>
    /// One §1.6 listing line, byte-for-byte the [BPQ-SRC ListMessage] shape:
    /// <c>"%-6d %s %c%c   %5d %-7s@%-6s %-6s %-s\r"</c> — number, dd-MMM date, type+status
    /// letters, size, TO@VIA (VIA = first dotted element of the AT), FROM, title. No header
    /// row ([VERIFY-ORACLE #9] — omitted as the safer default for pattern-matching clients).
    /// </summary>
    private static string FormatListLine(Message message)
    {
        string to = message.Recipients.Count > 0 ? message.Recipients[0].ToCall : "";
        string via = FirstAtElement(message.At);
        string date = message.CreatedAt.ToString("dd-MMM", CultureInfo.InvariantCulture);
        return Inv($"{message.Number,-6} {date} {message.Type.ToCode()}{message.Status.ToCode()}   {message.Body.Length,5} {to,-7}@{via,-6} {message.From,-6} {message.Subject}");
    }

    /// <summary>"VIA shows only the first dotted element" (compat spec §1.6).</summary>
    private static string FirstAtElement(string? at)
    {
        if (string.IsNullOrEmpty(at))
        {
            return "";
        }

        int dot = at.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? at : at[..dot];
    }

    private sealed class ListSpec
    {
        public bool OldestFirst { get; set; }

        public bool Mine { get; set; }

        public MessageType? Type { get; set; }

        public MessageStatus? Status { get; set; }

        public int? LastN { get; set; }

        public long? MinNumber { get; set; }

        public long? MaxNumber { get; set; }

        public string? ToCall { get; set; }

        public string? FromCall { get; set; }

        public string? AtPrefix { get; set; }

        /// <summary>Anything beyond bare-L ordering disables the since-last default (§1.3).</summary>
        public bool HasFilters =>
            Mine || Type is not null || Status is not null || LastN is not null
            || MinNumber is not null || MaxNumber is not null
            || ToCall is not null || FromCall is not null || AtPrefix is not null;
    }

    /// <summary>
    /// Parses the option-letter run and trailing arguments. Returns an error line to emit,
    /// or null on success.
    /// </summary>
    private static string? ParseListOptions(string verb, Queue<string> args, ListSpec spec)
    {
        // Option letters attached to the L (compat spec §1.3 "Options combine: LMP, LMN,
        // LB< G8BPQ, LNT etc.").
        for (int i = 1; i < verb.Length; i++)
        {
            char c = verb[i];
            switch (c)
            {
                case 'R':
                    spec.OldestFirst = true;
                    break;
                case 'M':
                    spec.Mine = true;
                    break;
                case 'B':
                    spec.Type = MessageType.Bulletin;
                    break;
                case 'P':
                    spec.Type = MessageType.Personal;
                    break;
                case 'T':
                    spec.Type = MessageType.Traffic;
                    break;
                case 'N':
                    spec.Status = MessageStatus.Unread;
                    break;
                case 'Y':
                    spec.Status = MessageStatus.Read;
                    break;
                case 'F':
                    spec.Status = MessageStatus.Forwarded;
                    break;
                case '$':
                    spec.Status = MessageStatus.BulletinQueued;
                    break;
                case 'H':
                    spec.Status = MessageStatus.Held;
                    break;
                case 'K':
                    spec.Status = MessageStatus.Killed;
                    break;
                case 'D':
                    spec.Status = MessageStatus.Delivered;
                    break;
                case 'L':
                {
                    // LL n — count attached (LL5) or as the next argument.
                    string rest = verb[(i + 1)..];
                    string? count = rest.Length > 0 ? rest : (args.Count > 0 ? args.Dequeue() : null);
                    if (count is null || !TryParseNumber(count, out long n) || n <= 0 || n > int.MaxValue)
                    {
                        return "*** Error: Invalid Format";
                    }

                    spec.LastN = (int)n;
                    return ParseListArguments(args, spec);
                }

                case '<':
                case '>':
                case '@':
                {
                    string rest = verb[(i + 1)..];
                    string? value = rest.Length > 0 ? rest : (args.Count > 0 ? args.Dequeue() : null);
                    if (value is null)
                    {
                        return "*** Error: Invalid Format";
                    }

                    ApplyCallFilter(spec, c, value);
                    return ParseListArguments(args, spec);
                }

                default:
                    if (char.IsAsciiDigit(c))
                    {
                        return ParseRange(verb[i..], spec) ? ParseListArguments(args, spec) : "*** Error: Invalid Format";
                    }

                    // §1.3 verbatim shape.
                    return Inv($"*** Error: Invalid List option {c}");
            }
        }

        return ParseListArguments(args, spec);
    }

    /// <summary>Trailing tokens: ranges (n / n- / n-m) and detached &lt;/&gt;/@ filters.</summary>
    private static string? ParseListArguments(Queue<string> args, ListSpec spec)
    {
        while (args.Count > 0)
        {
            string token = args.Dequeue();
            char sigil = token[0];
            if (sigil is '<' or '>' or '@')
            {
                string? value = token.Length > 1 ? token[1..] : (args.Count > 0 ? args.Dequeue() : null);
                if (value is null)
                {
                    return "*** Error: Invalid Format";
                }

                ApplyCallFilter(spec, sigil, value);
                continue;
            }

            if (!ParseRange(token, spec))
            {
                return "*** Error: Invalid Format";
            }
        }

        return null;
    }

    private static void ApplyCallFilter(ListSpec spec, char sigil, string value)
    {
        switch (sigil)
        {
            case '<':
                spec.FromCall = value;
                break;
            case '>':
                spec.ToCall = value;
                break;
            case '@':
                spec.AtPrefix = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sigil), sigil, "List filter sigils are < > @.");
        }
    }

    /// <summary>L n = that message; L n- = from n up; L n-m = the range (compat spec §1.3).</summary>
    private static bool ParseRange(string token, ListSpec spec)
    {
        int dash = token.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            if (!TryParseNumber(token, out long single))
            {
                return false;
            }

            spec.MinNumber = single;
            spec.MaxNumber = single;
            return true;
        }

        if (!TryParseNumber(token[..dash], out long min))
        {
            return false;
        }

        spec.MinNumber = min;
        string maxPart = token[(dash + 1)..];
        if (maxPart.Length > 0)
        {
            if (!TryParseNumber(maxPart, out long max))
            {
                return false;
            }

            spec.MaxNumber = max;
        }

        return true;
    }
}
