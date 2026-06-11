using Bbs.Core;

namespace Bbs.Console;

/// <summary>R/RM/RMR reading and K/KM killing (compat spec §1.3, rights per §2.2).</summary>
public sealed partial class BbsConsoleSession
{
    /// <summary>
    /// R n [n...] reads messages by number; RM reads new (unread-by-me) messages to me; RMR =
    /// RM oldest-first (compat spec §1.3). Errors: "Message %d not for you" /
    /// "Message %d not found"; a bad option letter draws the §1.3
    /// "*** Error: Invalid Read option %c" shape.
    /// </summary>
    private async Task HandleReadAsync(string verb, string[] args)
    {
        switch (verb)
        {
            case "R":
            {
                if (args.Length == 0)
                {
                    await WriteLineAsync("*** Error: Invalid Format").ConfigureAwait(false);
                    return;
                }

                foreach (string arg in args)
                {
                    if (!TryParseNumber(arg, out long number))
                    {
                        await WriteLineAsync("*** Error: Invalid Format").ConfigureAwait(false);
                        return;
                    }

                    await ReadOneMessageAsync(number, paged: true).ConfigureAwait(false);
                }

                return;
            }

            case "RM":
            case "RMR":
                await ReadMineAsync(oldestFirst: verb == "RMR").ConfigureAwait(false);
                return;

            default:
            {
                // First letter beyond a valid R/RM/RMR run is the bad option (§1.3 shape).
                char bad = verb.Length > 1 && verb[1] == 'M' && verb.Length > 2 ? verb[2] : verb[1];
                await WriteLineAsync(Inv($"*** Error: Invalid Read option {bad}")).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// Reads one message: visibility first (held/killed are invisible to non-sysops, §2.2 —
    /// they read as "not found"), then read permission (others' P → "Message %d not for you",
    /// §1.3 / <see cref="MessageRules.CanRead"/>), then output and the §2.2 N→Y transition
    /// via <see cref="BbsStore.MarkRead"/> (addressees only; T never goes Y).
    /// </summary>
    private async Task ReadOneMessageAsync(long number, bool paged)
    {
        Message? message = _store.GetMessage(number);
        if (message is null || !IsVisibleToMe(message))
        {
            await WriteLineAsync(Inv($"Message {number} not found")).ConfigureAwait(false);
            return;
        }

        if (!MessageRules.CanRead(message, _userCall, _isSysop))
        {
            await WriteLineAsync(Inv($"Message {number} not for you")).ConfigureAwait(false);
            return;
        }

        List<string> lines = RenderMessage(message);
        if (paged)
        {
            await WritePagedAsync(lines, listing: false).ConfigureAwait(false);
        }
        else
        {
            foreach (string line in lines)
            {
                await WriteLineAsync(line).ConfigureAwait(false);
            }
        }

        _store.MarkRead(number, _userCall);
    }

    /// <summary>
    /// The read output: a header block then the body. The spec pins R's side-effects and
    /// errors but not the header shape — this one mirrors the BPQ web/terminal field set
    /// (From/To/Type-Status/Date-Time/Bid/Title); treat as oracle-checkable, not wire-load-bearing.
    /// </summary>
    private static List<string> RenderMessage(Message message)
    {
        string to = string.Join(';', message.Recipients.Select(r => r.ToCall));
        string at = message.At is null ? "" : Inv($" @ {message.At}");
        var lines = new List<string>
        {
            Inv($"From: {message.From}"),
            Inv($"To: {to}{at}"),
            Inv($"Type/Status: {message.Type.ToCode()}{message.Status.ToCode()}"),
            Inv($"Date/Time: {message.CreatedAt.ToString("dd-MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"),
            Inv($"Bid: {message.Bid}"),
            Inv($"Title: {message.Subject}"),
            "",
        };
        lines.AddRange(BodyLines(message));
        return lines;
    }

    /// <summary>Body bytes → CR-discipline lines, Latin-1, byte-transparent (compat spec §2 model).</summary>
    private static List<string> BodyLines(Message message)
    {
        string text = message.GetBodyText()
            .Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace('\n', '\r');
        var lines = new List<string>(text.Split('\r'));
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>RM — new messages to me: addressed to me and not yet read by me (compat spec §1.3).</summary>
    private async Task ReadMineAsync(bool oldestFirst)
    {
        IReadOnlyList<Message> messages = _store.ListMessages(new MessageQuery
        {
            ToCall = _userCall,
            OldestFirst = oldestFirst,
        });

        var unread = new List<Message>();
        foreach (Message message in messages)
        {
            MessageRecipient? me = message.Recipients.FirstOrDefault(r => Callsigns.BaseEquals(r.ToCall, _userCall));
            if (me is { ReadAt: null })
            {
                unread.Add(message);
            }
        }

        if (unread.Count == 0)
        {
            await WriteLineAsync("No New Messages").ConfigureAwait(false);
            return;
        }

        foreach (Message message in unread)
        {
            await ReadOneMessageAsync(message.Number, paged: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// K n kills by number — "Message #%d Killed" / "Not your message" / "Message %d not
    /// found" (compat spec §1.3), rights per §2.2 <see cref="MessageRules.CanKill"/> (held is
    /// sysop-only and invisible → "not found"). KM kills my READ personal messages (§1.3:
    /// the source kills status Y; the doc's "haven't yet read" is the known typo,
    /// [VERIFY-ORACLE #7]). Bad option letter → "*** Error: Invalid Kill option %c".
    /// </summary>
    private async Task HandleKillAsync(string verb, string[] args)
    {
        switch (verb)
        {
            case "K":
            {
                if (args.Length == 0)
                {
                    await WriteLineAsync("*** Error: Invalid Format").ConfigureAwait(false);
                    return;
                }

                foreach (string arg in args)
                {
                    if (!TryParseNumber(arg, out long number))
                    {
                        await WriteLineAsync("*** Error: Invalid Format").ConfigureAwait(false);
                        return;
                    }

                    await KillOneMessageAsync(number).ConfigureAwait(false);
                }

                return;
            }

            case "KM":
            {
                IReadOnlyList<Message> mine = _store.ListMessages(new MessageQuery
                {
                    Type = MessageType.Personal,
                    Status = MessageStatus.Read,
                    ToCall = _userCall,
                    OldestFirst = true,
                });

                if (mine.Count == 0)
                {
                    await WriteLineAsync("No Messages found").ConfigureAwait(false);
                    return;
                }

                foreach (Message message in mine)
                {
                    _store.Kill(message.Number);
                    await WriteLineAsync(Inv($"Message #{message.Number} Killed")).ConfigureAwait(false);
                }

                return;
            }

            default:
                await WriteLineAsync(Inv($"*** Error: Invalid Kill option {verb[1]}")).ConfigureAwait(false);
                return;
        }
    }

    private async Task KillOneMessageAsync(long number)
    {
        Message? message = _store.GetMessage(number);
        if (message is null || !IsVisibleToMe(message) || message.Status == MessageStatus.Killed)
        {
            await WriteLineAsync(Inv($"Message {number} not found")).ConfigureAwait(false);
            return;
        }

        if (!MessageRules.CanKill(message, _userCall, _isSysop))
        {
            await WriteLineAsync("Not your message").ConfigureAwait(false);
            return;
        }

        _store.Kill(number);
        await WriteLineAsync(Inv($"Message #{number} Killed")).ConfigureAwait(false);
    }
}
