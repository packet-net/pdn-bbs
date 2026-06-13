using System.Globalization;
using System.Text;

namespace Bbs.Imap;

/// <summary>
/// Evaluates an IMAP <c>SEARCH</c> / <c>UID SEARCH</c> program (RFC 3501 §6.4.4) against a selected
/// mailbox snapshot, returning the matching message sequence numbers (<c>SEARCH</c>) or UIDs
/// (<c>UID SEARCH</c>). Real clients — iPhone Mail among them — enumerate a mailbox by issuing
/// <c>UID SEARCH ALL</c> after SELECT, so this is load-bearing, not optional: without it a client
/// shows the folder but never lists its messages.
/// </summary>
/// <remarks>
/// <para>
/// Implemented search keys: <c>ALL</c>; the flag keys <c>SEEN/UNSEEN</c>, <c>ANSWERED/UNANSWERED</c>,
/// <c>FLAGGED/UNFLAGGED</c>, <c>DELETED/UNDELETED</c>, <c>DRAFT/UNDRAFT</c>, <c>NEW/OLD/RECENT</c>,
/// <c>KEYWORD/UNKEYWORD</c>; date keys <c>BEFORE/ON/SINCE</c> and <c>SENTBEFORE/SENTON/SENTSINCE</c>
/// (<c>d-MMM-yyyy</c>); size keys <c>LARGER/SMALLER</c>; the substring keys <c>SUBJECT/FROM/TO/CC/BCC/
/// BODY/TEXT</c> and <c>HEADER &lt;field&gt; &lt;str&gt;</c>; the combinators <c>NOT</c>, <c>OR</c> and a
/// parenthesised group; a bare sequence-set (message-seq for SEARCH, UID for UID SEARCH); and
/// <c>UID &lt;set&gt;</c>. A leading <c>CHARSET &lt;name&gt;</c> is accepted and ignored.
/// </para>
/// <para>
/// The BBS message model only tracks the <c>\Seen</c> flag (per the recipient read-row); the other
/// system flags are never set, so <c>ANSWERED/FLAGGED/DELETED/DRAFT/RECENT</c> match nothing and their
/// negations match everything. <c>INTERNALDATE</c> and the <c>Date:</c> header are both the message's
/// stored creation time, so the sent-* date keys behave identically to their plain counterparts.
/// </para>
/// </remarks>
public static class ImapSearch
{
    /// <summary>
    /// Parses and evaluates the search <paramref name="args"/> against <paramref name="mailbox"/>.
    /// Returns false (a clean protocol <c>BAD</c>) when the program is not well-formed; on success
    /// <paramref name="results"/> holds the matches in ascending order — sequence numbers when
    /// <paramref name="byUid"/> is false, UIDs when true.
    /// </summary>
    public static bool TryEvaluate(
        IReadOnlyList<ImapToken> args, ImapMailbox mailbox, bool byUid, out IReadOnlyList<long> results)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(mailbox);
        results = [];

        var cursor = new Cursor(args);

        // An optional leading CHARSET <name> (we render/search as Latin-1, so the charset is ignored).
        if (cursor.PeekAtom("CHARSET"))
        {
            cursor.Next();
            if (cursor.End)
            {
                return false;
            }

            cursor.Next(); // the charset name
        }

        if (cursor.End)
        {
            return false; // SEARCH requires at least one key
        }

        if (!TryParseProgram(cursor, mailbox, out Func<ImapMessageHandle, bool> predicate) || !cursor.End)
        {
            return false;
        }

        var matched = new List<long>();
        foreach (ImapMessageHandle handle in mailbox.Messages)
        {
            if (predicate(handle))
            {
                matched.Add(byUid ? handle.Uid : handle.Sequence);
            }
        }

        matched.Sort();
        results = matched;
        return true;
    }

    /// <summary>Parses a sequence of keys (implicit AND) until the cursor ends or a list closes.</summary>
    private static bool TryParseProgram(Cursor cursor, ImapMailbox mailbox, out Func<ImapMessageHandle, bool> predicate)
    {
        predicate = static _ => true;
        var keys = new List<Func<ImapMessageHandle, bool>>();
        while (!cursor.End)
        {
            if (!TryParseKey(cursor, mailbox, out Func<ImapMessageHandle, bool> key))
            {
                return false;
            }

            keys.Add(key);
        }

        if (keys.Count == 0)
        {
            return false;
        }

        predicate = h => keys.All(k => k(h));
        return true;
    }

    private static bool TryParseKey(Cursor cursor, ImapMailbox mailbox, out Func<ImapMessageHandle, bool> key)
    {
        key = static _ => true;
        if (cursor.End)
        {
            return false;
        }

        ImapToken token = cursor.Next();

        // A parenthesised group is its own AND-program (the token carries the outer parens).
        if (token.Kind == ImapTokenKind.List)
        {
            string inner = StripOuterParens(token.Value);
            if (!ImapCommandParser.TryTokenize(inner, out IReadOnlyList<ImapToken> innerTokens))
            {
                return false;
            }

            var sub = new Cursor(innerTokens);
            if (!TryParseProgram(sub, mailbox, out Func<ImapMessageHandle, bool> group) || !sub.End)
            {
                return false;
            }

            key = group;
            return true;
        }

        string word = token.Value.ToUpperInvariant();
        switch (word)
        {
            case "ALL": key = static _ => true; return true;
            case "SEEN": key = static h => h.Seen; return true;
            case "UNSEEN": key = static h => !h.Seen; return true;

            // Flags the BBS model never sets: present-keys match nothing, absent-keys match all.
            case "ANSWERED" or "FLAGGED" or "DELETED" or "DRAFT" or "RECENT" or "NEW":
                key = static _ => false; return true;
            case "UNANSWERED" or "UNFLAGGED" or "UNDELETED" or "UNDRAFT" or "OLD":
                key = static _ => true; return true;

            case "KEYWORD":
                if (cursor.End) { return false; }
                cursor.Next();
                key = static _ => false; // no keywords are ever set
                return true;
            case "UNKEYWORD":
                if (cursor.End) { return false; }
                cursor.Next();
                key = static _ => true;
                return true;

            case "SUBJECT" or "FROM" or "TO" or "CC" or "BCC" or "BODY" or "TEXT":
                return TryParseString(cursor, word, out key);

            case "HEADER":
                if (cursor.End) { return false; }
                string field = cursor.Next().Value;
                if (cursor.End) { return false; }
                string value = cursor.Next().Value;
                key = h => HeaderFieldContains(h, field, value);
                return true;

            case "BEFORE" or "ON" or "SINCE" or "SENTBEFORE" or "SENTON" or "SENTSINCE":
                return TryParseDate(cursor, word, out key);

            case "LARGER" or "SMALLER":
                return TryParseSize(cursor, word, out key);

            case "UID":
                if (cursor.End) { return false; }
                return TryParseSet(cursor.Next().Value, mailbox.MaxUid, useUid: true, mailbox, out key);

            case "NOT":
                if (!TryParseKey(cursor, mailbox, out Func<ImapMessageHandle, bool> negated)) { return false; }
                key = h => !negated(h);
                return true;

            case "OR":
                if (!TryParseKey(cursor, mailbox, out Func<ImapMessageHandle, bool> left)
                    || !TryParseKey(cursor, mailbox, out Func<ImapMessageHandle, bool> right))
                {
                    return false;
                }

                key = h => left(h) || right(h);
                return true;

            default:
                // The only remaining valid key is a bare sequence-set (message-seq numbers).
                return TryParseSet(token.Value, mailbox.MaxSequence, useUid: false, mailbox, out key);
        }
    }

    private static bool TryParseString(Cursor cursor, string field, out Func<ImapMessageHandle, bool> key)
    {
        key = static _ => true;
        if (cursor.End)
        {
            return false;
        }

        string needle = cursor.Next().Value;
        key = field switch
        {
            "BODY" => h => Contains(h.Rendered.Text.Span, needle),
            "TEXT" => h => Contains(h.Rendered.Full, needle),
            "SUBJECT" => h => h.Message.Subject.Contains(needle, StringComparison.OrdinalIgnoreCase)
                              || HeaderFieldContains(h, "Subject", needle),
            _ => h => HeaderFieldContains(h, field, needle), // FROM/TO/CC/BCC
        };
        return true;
    }

    private static bool TryParseDate(Cursor cursor, string key, out Func<ImapMessageHandle, bool> predicate)
    {
        predicate = static _ => true;
        if (cursor.End || !TryParseImapDate(cursor.Next().Value, out DateOnly date))
        {
            return false;
        }

        // INTERNALDATE and Date: are both the stored creation time, so SENT* == plain here.
        predicate = key switch
        {
            "BEFORE" or "SENTBEFORE" => h => DateOf(h) < date,
            "ON" or "SENTON" => h => DateOf(h) == date,
            _ => h => DateOf(h) >= date, // SINCE / SENTSINCE
        };
        return true;
    }

    private static bool TryParseSize(Cursor cursor, string key, out Func<ImapMessageHandle, bool> predicate)
    {
        predicate = static _ => true;
        if (cursor.End
            || !long.TryParse(cursor.Next().Value, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
        {
            return false;
        }

        predicate = key == "LARGER" ? h => h.Rendered.Size > n : h => h.Rendered.Size < n;
        return true;
    }

    private static bool TryParseSet(
        string text, long star, bool useUid, ImapMailbox mailbox, out Func<ImapMessageHandle, bool> key)
    {
        key = static _ => true;
        if (!ImapSequenceSet.TryParse(text, star, out IReadOnlyList<long> values))
        {
            return false;
        }

        var set = new HashSet<long>(values);
        key = useUid ? h => set.Contains(h.Uid) : h => set.Contains(h.Sequence);
        _ = mailbox;
        return true;
    }

    private static DateOnly DateOf(ImapMessageHandle handle)
        => DateOnly.FromDateTime(handle.Message.CreatedAt.UtcDateTime);

    private static bool TryParseImapDate(string text, out DateOnly date)
    {
        // RFC 3501 date: dd-MMM-yyyy (e.g. 1-Jan-2026 or 01-Jan-2026), month abbreviation, invariant.
        string trimmed = text.Trim('"');
        return DateOnly.TryParseExact(trimmed, ["d-MMM-yyyy", "dd-MMM-yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, string needle)
        => Encoding.Latin1.GetString(haystack).Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a header line for <paramref name="field"/> contains <paramref name="needle"/> (case-insensitive).</summary>
    private static bool HeaderFieldContains(ImapMessageHandle handle, string field, string needle)
    {
        string header = Encoding.Latin1.GetString(handle.Rendered.Header.Span);
        string prefix = field.TrimEnd(':') + ":";
        foreach (string line in header.Split("\r\n"))
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && line.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripOuterParens(string list)
        => list.Length >= 2 && list[0] == '(' && list[^1] == ')' ? list[1..^1] : list;

    /// <summary>A forward cursor over the search tokens.</summary>
    private sealed class Cursor(IReadOnlyList<ImapToken> tokens)
    {
        private int _i;

        public bool End => _i >= tokens.Count;

        public ImapToken Next() => tokens[_i++];

        public bool PeekAtom(string atom)
            => !End && tokens[_i].Kind == ImapTokenKind.Atom
               && string.Equals(tokens[_i].Value, atom, StringComparison.OrdinalIgnoreCase);
    }
}
