using System.Text;
using Bbs.Core;

namespace Bbs.Console;

/// <summary>The S family — message entry per compat spec §1.5 (grammar, prompts, acceptance).</summary>
public sealed partial class BbsConsoleSession
{
    /// <summary>
    /// S / SP / SB / ST send (S = SP, §1.5 step 1), SR n reply, SC n call copy (§1.3).
    /// Bad type letter → the §1.3 "*** Error: Invalid Send option %c" shape.
    /// </summary>
    private async Task HandleSendAsync(string verb, string[] args)
    {
        switch (verb)
        {
            case "S":
            case "SP":
                await SendNewAsync(MessageType.Personal, args).ConfigureAwait(false);
                return;
            case "SB":
                await SendNewAsync(MessageType.Bulletin, args).ConfigureAwait(false);
                return;
            case "ST":
                await SendNewAsync(MessageType.Traffic, args).ConfigureAwait(false);
                return;
            case "SR":
                await SendReplyAsync(args).ConfigureAwait(false);
                return;
            case "SC":
                await SendCopyAsync(args).ConfigureAwait(false);
                return;
            default:
                await WriteLineAsync(Inv($"*** Error: Invalid Send option {verb[1]}")).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// The §1.5 flow: parse the S line, run the dup-BID check (step 3), auto-complete a
    /// missing @ from the recipient's Home BBS (step 2), then either the interactive
    /// title/text prompts (steps 4–6) or, for a BBS-flagged caller, the MBL-style
    /// OK-then-title-then-body path (§1.5 step 4 parenthetical, §3.10).
    /// </summary>
    private async Task SendNewAsync(MessageType type, string[] args)
    {
        SendLine? send = await ParseSendLineAsync(args).ConfigureAwait(false);
        if (send is null)
        {
            return;
        }

        // §1.5: "< from only from BBS-flagged peers".
        if (send.From is not null && !_isBbs)
        {
            await WriteLineAsync("*** < can only be used by a BBS").ConfigureAwait(false);
            return;
        }

        // §1.5 step 3 dup-BID check: interactive refusal "*** Error- Duplicate BID";
        // BBS peers get "NO - BID" (§3.10).
        if (send.Bid is not null
            && _store.CheckInboundBid(send.Bid, type, send.Recipients[0]) == BidDisposition.RejectDuplicate)
        {
            await WriteLineAsync(_isBbs ? "NO - BID" : "*** Error- Duplicate BID").ConfigureAwait(false);
            return;
        }

        string? at = send.At ?? await AutoCompleteAtAsync(type, send.Recipients[0]).ConfigureAwait(false);

        string from = send.From ?? _userCall;

        if (_isBbs)
        {
            // §3.10 (MBL receive): "OK" + the prompt, then title line and text follow with
            // no Enter-Title/Enter-Message prompts (§1.5 step 4 parenthetical) and no
            // acceptance line — the next prompt is the acknowledgement.
            await WriteLineAsync("OK").ConfigureAwait(false);
            await WritePromptAsync().ConfigureAwait(false);
            string bbsTitle = await ReadLineAsync().ConfigureAwait(false);
            ReadOnlyMemory<byte> bbsBody = await ReadMessageBodyAsync().ConfigureAwait(false);
            _store.AddMessage(new MessageDraft
            {
                Type = type,
                From = from,
                Recipients = send.Recipients,
                At = at,
                Bid = send.Bid,
                Subject = bbsTitle.Trim(),
                Body = bbsBody,
                ReceivedFrom = _terminal.RemoteCallsign,
            });
            return;
        }

        // §1.5 step 4, verbatim prompt; empty title cancels.
        await WriteLineAsync("Enter Title (only):").ConfigureAwait(false);
        string title = (await ReadLineAsync().ConfigureAwait(false)).Trim();
        if (title.Length == 0)
        {
            await WriteLineAsync("*** Message Cancelled").ConfigureAwait(false);
            return;
        }

        Message stored = await PromptForTextAndStoreAsync(new MessageDraft
        {
            Type = type,
            From = from,
            Recipients = send.Recipients,
            At = at,
            Bid = send.Bid,
            Subject = title,
        }).ConfigureAwait(false);
        await WriteAcceptanceAsync(stored).ConfigureAwait(false);
    }

    /// <summary>
    /// SR n — reply to message n: TO = its sender, title auto "Re:…", no title prompt
    /// (compat spec §1.3), then the normal text flow. Visibility/read rights as for R.
    /// </summary>
    private async Task SendReplyAsync(string[] args)
    {
        Message? original = await ResolveReadableMessageAsync(args).ConfigureAwait(false);
        if (original is null)
        {
            return;
        }

        string? at = await AutoCompleteAtAsync(MessageType.Personal, original.From).ConfigureAwait(false);
        Message stored = await PromptForTextAndStoreAsync(new MessageDraft
        {
            Type = MessageType.Personal,
            From = _userCall,
            Recipients = [original.From],
            At = at,
            Subject = "Re:" + original.Subject,
        }).ConfigureAwait(false);
        await WriteAcceptanceAsync(stored).ConfigureAwait(false);
    }

    /// <summary>
    /// SC n call [@ bbs] — copy message n to a new recipient: title auto "Fwd:…" (compat
    /// spec §1.3), body copied verbatim. Judgment call: no text prompt — the copy is the
    /// message (the spec pins only the auto-title).
    /// </summary>
    private async Task SendCopyAsync(string[] args)
    {
        Message? original = await ResolveReadableMessageAsync(args).ConfigureAwait(false);
        if (original is null)
        {
            return;
        }

        SendLine? send = await ParseSendLineAsync(args[1..]).ConfigureAwait(false);
        if (send is null)
        {
            return;
        }

        if (send.From is not null && !_isBbs)
        {
            await WriteLineAsync("*** < can only be used by a BBS").ConfigureAwait(false);
            return;
        }

        string? at = send.At ?? await AutoCompleteAtAsync(MessageType.Personal, send.Recipients[0]).ConfigureAwait(false);
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = send.From ?? _userCall,
            Recipients = send.Recipients,
            At = at,
            Bid = send.Bid,
            Subject = "Fwd:" + original.Subject,
            Body = original.Body,
        });
        await WriteAcceptanceAsync(stored).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- shared pieces

    /// <summary>Resolves args[0] as a message number with R-style visibility/permission errors (§1.3).</summary>
    private async Task<Message?> ResolveReadableMessageAsync(string[] args)
    {
        if (args.Length == 0 || !TryParseNumber(args[0], out long number))
        {
            await WriteLineAsync("*** Error: Invalid Format").ConfigureAwait(false);
            return null;
        }

        Message? message = _store.GetMessage(number);
        if (message is null || !IsVisibleToMe(message))
        {
            await WriteLineAsync(Inv($"Message {number} not found")).ConfigureAwait(false);
            return null;
        }

        if (!MessageRules.CanRead(message, _userCall, _isSysop))
        {
            await WriteLineAsync(Inv($"Message {number} not for you")).ConfigureAwait(false);
            return null;
        }

        return message;
    }

    /// <summary>
    /// §1.5 step 2: a missing @ is auto-completed from the recipient's Home BBS with the
    /// verbatim "Address @%s added from HomeBBS" (the WP variant arrives with the WP build,
    /// §8 SHOULD). Personals only — bulletins are area-addressed.
    /// </summary>
    private async Task<string?> AutoCompleteAtAsync(MessageType type, string recipient)
    {
        if (type != MessageType.Personal)
        {
            return null;
        }

        User? user = _store.GetUser(Callsigns.NormalizeAddressee(recipient));
        if (user?.HomeBbs is not { Length: > 0 } homeBbs)
        {
            return null;
        }

        await WriteLineAsync(Inv($"Address @{homeBbs} added from HomeBBS")).ConfigureAwait(false);
        return homeBbs;
    }

    /// <summary>§1.5 steps 5–7: the verbatim text prompt, the body loop, then store.</summary>
    private async Task<Message> PromptForTextAndStoreAsync(MessageDraft draft)
    {
        await WriteLineAsync("Enter Message Text (end with /ex or ctrl/z)").ConfigureAwait(false);
        ReadOnlyMemory<byte> body = await ReadMessageBodyAsync().ConfigureAwait(false);
        return _store.AddMessage(draft with { Body = body });
    }

    /// <summary>
    /// The text-entry loop (§1.5 step 6): terminated by a line beginning Ctrl-Z (0x1A), the
    /// line "/ex" (case-insensitive), or the AEA-TNC artifact "/E&lt;0x1A&gt;&gt;". There is
    /// no body-stage abort ([VERIFY-ORACLE #8]) — only the empty title cancels. Lines are
    /// stored CR-terminated, Latin-1 (byte-transparent).
    /// </summary>
    private async Task<ReadOnlyMemory<byte>> ReadMessageBodyAsync()
    {
        var text = new StringBuilder();
        while (true)
        {
            string line = await ReadLineAsync().ConfigureAwait(false);
            if (IsBodyTerminator(line))
            {
                break;
            }

            text.Append(line).Append('\r');
        }

        return Encoding.Latin1.GetBytes(text.ToString());
    }

    private static bool IsBodyTerminator(string line)
    {
        if (line.Length > 0 && line[0] == '\x1A')
        {
            return true;
        }

        if (line.Trim().Equals("/ex", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // AEA TNC artifact "/E<0x1A>>" (§1.5 step 6) — parse-tolerated.
        return line.Length >= 3 && line[0] == '/' && char.ToUpperInvariant(line[1]) == 'E' && line[2] == '\x1A';
    }

    /// <summary>
    /// §1.5 step 7 acceptance, byte-exact: <c>Message: %d Bid:  %s Size: %d</c> — note the
    /// two spaces after "Bid:" [BPQ-SRC ~6503]. Size is the stored body byte count.
    /// </summary>
    private ValueTask WriteAcceptanceAsync(Message stored) =>
        WriteLineAsync(Inv($"Message: {stored.Number} Bid:  {stored.Bid} Size: {stored.Body.Length}"));

    private sealed record SendLine(IReadOnlyList<string> Recipients, string? At, string? From, string? Bid);

    /// <summary>
    /// The §1.5 S-line grammar: <c>S&lt;type&gt; TO [@ AT] [&lt; FROM] [$BID]</c> in any
    /// order after TO; <c>call@bbs</c> without spaces accepted; multiple recipients
    /// ';'-separated. Errors are the verbatim "*** Error: The 'TO' callsign is missing" and
    /// "*** Error: Invalid Format". Returns null after emitting an error.
    /// </summary>
    private async Task<SendLine?> ParseSendLineAsync(string[] args)
    {
        string? to = null;
        string? at = null;
        string? from = null;
        string? bid = null;

        var queue = new Queue<string>(args);
        bool invalid = false;
        while (queue.Count > 0 && !invalid)
        {
            string token = queue.Dequeue();
            char sigil = token[0];

            if (to is null)
            {
                if (sigil is '@' or '<' or '$')
                {
                    await WriteLineAsync("*** Error: The 'TO' callsign is missing").ConfigureAwait(false);
                    return null;
                }

                int atSign = token.IndexOf('@', StringComparison.Ordinal);
                if (atSign >= 0)
                {
                    to = token[..atSign];
                    at = token[(atSign + 1)..];
                    invalid = to.Length == 0 || at.Length == 0;
                }
                else
                {
                    to = token;
                }

                continue;
            }

            switch (sigil)
            {
                case '@':
                    at = token.Length > 1 ? token[1..] : Dequeue(queue);
                    invalid = at is null;
                    break;
                case '<':
                    from = token.Length > 1 ? token[1..] : Dequeue(queue);
                    invalid = from is null;
                    break;
                case '$':
                    bid = token.Length > 1 ? token[1..] : Dequeue(queue);
                    invalid = bid is null;
                    break;
                default:
                    invalid = true;
                    break;
            }
        }

        if (to is null)
        {
            await WriteLineAsync("*** Error: The 'TO' callsign is missing").ConfigureAwait(false);
            return null;
        }

        string[] recipients = to.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (invalid || recipients.Length == 0)
        {
            await WriteLineAsync(invalid ? "*** Error: Invalid Format" : "*** Error: The 'TO' callsign is missing").ConfigureAwait(false);
            return null;
        }

        return new SendLine(recipients, at, from, bid);

        static string? Dequeue(Queue<string> queue) => queue.Count > 0 ? queue.Dequeue() : null;
    }
}
