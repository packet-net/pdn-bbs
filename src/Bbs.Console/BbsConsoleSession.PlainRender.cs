using System.Globalization;
using Bbs.Core;

namespace Bbs.Console;

/// <summary>
/// The plain-language renderers and handlers (the plain-language mandate, design.md). Sentence
/// listings, plain help, plain prompts and the <c>more? (yes/no)</c> pager — and the plain
/// command handlers, each of which drives the SAME store operations the classic verbs use (so
/// mail behaviour, visibility and rights are identical; only the words on the wire differ).
/// </summary>
public sealed partial class BbsConsoleSession
{
    // ---------------------------------------------------------------- greeting / prompt / sign-off

    /// <summary>
    /// The plain connect greeting: a friendly welcome, the new-user name ask in a sentence, the
    /// "make this your home mailbox?" offer (sentences, not Z/QTH/Home command trivia), and a
    /// one-line "you have N new messages" / "no new mail" so the user knows where they stand.
    /// </summary>
    private async Task GreetPlainAsync()
    {
        User user = _store.GetUser(_userCall)!;

        await WritePlainLineAsync(Inv($"Hello and welcome to the {_bbsName} mailbox.")).ConfigureAwait(false);

        if (user.Name is null)
        {
            await WritePlainLineAsync("I don't think we've met - what's your name?").ConfigureAwait(false);
            string name = (await ReadLineAsync().ConfigureAwait(false)).Trim();
            if (name.Length > 0)
            {
                user = user with { Name = name.Length <= 17 ? name : name[..17] };
                _store.UpsertUser(user);
                await WritePlainLineAsync(Inv($"Thanks, {FirstToken(user.Name)}. Good to meet you.")).ConfigureAwait(false);
            }
        }

        if (user.HomeBbs is null)
        {
            await WritePlainLineAsync(
                "If you'd like people to be able to send you mail here, set this as your home")
                .ConfigureAwait(false);
            await WritePlainLineAsync(
                Inv($"mailbox by typing: home {_bbsName}.  You can also tell me your town with qth."))
                .ConfigureAwait(false);
        }

        int newCount = CountNewMail();
        await WritePlainLineAsync(newCount switch
        {
            0 => "You have no new mail. Type help if you'd like a hand.",
            1 => "You have 1 new message - type list to see it.",
            _ => Inv($"You have {newCount} new messages - type list to see them."),
        }).ConfigureAwait(false);
    }

    /// <summary>The plain prompt: a friendly, paclen-short line that never ends in '&gt;' (banner guard).</summary>
    private ValueTask WritePlainPromptAsync() => WriteAsync(Inv($"{_bbsName} ready, what next? "));

    /// <summary>The plain sign-off for quit/node.</summary>
    private ValueTask WritePlainSignOffAsync() =>
        WritePlainLineAsync(Inv($"73 - thanks for calling {_bbsName}. See you next time."));

    // ---------------------------------------------------------------- help

    /// <summary>
    /// Plain help: full sentences explaining everything, in the command-table order, paged with
    /// the plain pager. This is the mandate's "help explains everything in sentences".
    /// </summary>
    private async Task HandlePlainHelpAsync()
    {
        var lines = new List<string>
        {
            "Here's what you can type. You only need the first few letters of any word.",
            "",
        };
        lines.AddRange(PlainCommands.Select(c => "  " + c.Help));
        lines.Add("");
        lines.Add("Addressing is just a callsign - send g4abc - I find where they are.");
        await WritePlainPagedAsync(lines).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- list (sentence listings)

    /// <summary>
    /// <c>list</c> - the user's new mail as sentences, newest first, advancing the listed pointer
    /// exactly as classic bare-L does (same store query + <see cref="BbsStore.SetLastListed"/>).
    /// An optional count (<c>list 20</c>) shows the last N of everything visible instead.
    /// </summary>
    private async Task HandlePlainListAsync(string[] args)
    {
        if (args.Length > 0 && TryParseNumber(args[0], out long lastN) && lastN > 0 && lastN <= int.MaxValue)
        {
            IReadOnlyList<Message> recent = _store.ListMessages(new MessageQuery
            {
                Limit = (int)lastN,
                IncludeHeld = _isSysop,
                IncludeKilled = false,
            });
            await RenderMessageListAsync(recent, "your recent mail", emptyLine: "There's no mail to show.")
                .ConfigureAwait(false);
            return;
        }

        User user = _store.GetUser(_userCall)!;
        IReadOnlyList<Message> messages = _store.ListMessages(new MessageQuery
        {
            MinNumber = user.LastListedNumber + 1,
            IncludeHeld = _isSysop,
            IncludeKilled = false,
        });

        await RenderMessageListAsync(messages, "new", emptyLine: "No new mail right now.").ConfigureAwait(false);
        _store.SetLastListed(_userCall, _store.GetLatestMessageNumber());
    }

    /// <summary><c>bulletins</c> - the latest bulletins anyone can read, newest first, as sentences.</summary>
    private async Task HandlePlainBulletinsAsync()
    {
        IReadOnlyList<Message> bulletins = _store.ListMessages(new MessageQuery
        {
            Type = MessageType.Bulletin,
            Limit = 20,
        });
        await RenderMessageListAsync(bulletins, "bulletin", emptyLine: "There are no bulletins at the moment.")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>topics</c> - the bulletin categories in use (the plain word for TO-distribution / the
    /// bulletin addressees), each with how many bulletins carry it. Pure presentation over the
    /// existing bulletin store; no new persistence.
    /// </summary>
    private async Task HandlePlainTopicsAsync()
    {
        IReadOnlyList<Message> bulletins = _store.ListMessages(new MessageQuery { Type = MessageType.Bulletin });
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Message b in bulletins)
        {
            string topic = b.Recipients.Count > 0 ? b.Recipients[0].ToCall : "(none)";
            counts[topic] = counts.TryGetValue(topic, out int n) ? n + 1 : 1;
        }

        if (counts.Count == 0)
        {
            await WritePlainLineAsync("No bulletin topics yet - none have been posted.").ConfigureAwait(false);
            return;
        }

        var lines = new List<string> { "Bulletin topics in use right now:" };
        lines.AddRange(counts
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Inv($"  {kv.Key} - {kv.Value} bulletin{(kv.Value == 1 ? "" : "s")}")));
        lines.Add("Type bulletins to read them.");
        await WritePlainPagedAsync(lines).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders a message list as paclen-friendly sentences and pages them. <paramref name="noun"/>
    /// shapes the heading ("3 new messages", "2 bulletins", "your recent mail"). Each line reads
    /// e.g. "1) from G4ABC, 12 Jun: Antenna party" with an unread marker, no status letters.
    /// </summary>
    private async Task RenderMessageListAsync(IReadOnlyList<Message> messages, string noun, string emptyLine)
    {
        if (messages.Count == 0)
        {
            await WritePlainLineAsync(emptyLine).ConfigureAwait(false);
            return;
        }

        var lines = new List<string> { PlainListHeading(messages.Count, noun) };
        lines.AddRange(messages.Select(FormatPlainListLine));
        lines.Add("Type read and a number to read one (e.g. read " +
            messages[0].Number.ToString(CultureInfo.InvariantCulture) + ").");
        await WritePlainPagedAsync(lines).ConfigureAwait(false);
    }

    private static string PlainListHeading(int count, string noun) => noun switch
    {
        "new" => count == 1 ? "You have 1 new message:" : Inv($"You have {count} new messages:"),
        "bulletin" => count == 1 ? "There is 1 bulletin:" : Inv($"There are {count} bulletins:"),
        _ => Inv($"Showing {noun} ({count} message{(count == 1 ? "" : "s")}):"),
    };

    /// <summary>
    /// One sentence listing line (the mandate's example shape): number, who it's from, the date,
    /// and the subject — with a leading "*" when it's unread by this caller. No type/status
    /// letters, no fixed columns; the subject is trimmed so the line stays paclen-friendly.
    /// </summary>
    private string FormatPlainListLine(Message message)
    {
        string mark = IsUnreadByMe(message) ? "* " : "  ";
        string date = message.CreatedAt.ToString("d MMM", CultureInfo.InvariantCulture);
        string subject = TrimForPaclen(message.Subject, 40);
        return Inv($"{mark}{message.Number}) from {message.From}, {date}: {subject}");
    }

    /// <summary>True when this caller is an addressee who hasn't read the message yet (drives the "*").</summary>
    private bool IsUnreadByMe(Message message)
    {
        MessageRecipient? me = message.Recipients.FirstOrDefault(r => Callsigns.BaseEquals(r.ToCall, _userCall));
        return me is { ReadAt: null };
    }

    // ---------------------------------------------------------------- read

    /// <summary>
    /// <c>read &lt;n&gt;</c> - read message(s) by number. Reuses the classic
    /// <see cref="ReadOneMessageAsync"/> path (visibility, "not for you" rights, the N→Y
    /// transition) but renders the friendly errors in sentences. With no number it reads the
    /// caller's unread mail like classic RM.
    /// </summary>
    private async Task HandlePlainReadAsync(string[] args)
    {
        if (args.Length == 0)
        {
            await PlainReadMineAsync().ConfigureAwait(false);
            return;
        }

        foreach (string arg in args)
        {
            if (!TryParseNumber(arg, out long number))
            {
                await WritePlainLineAsync(Inv(
                    $"\"{arg}\" isn't a message number. Try read and a number, like read 3.")).ConfigureAwait(false);
                return;
            }

            await PlainReadOneAsync(number).ConfigureAwait(false);
        }
    }

    /// <summary>Reads one message with sentence errors; reuses <see cref="RenderMessage"/> + the store side-effects.</summary>
    private async Task PlainReadOneAsync(long number)
    {
        Message? message = _store.GetMessage(number);
        if (message is null || !IsVisibleToMe(message))
        {
            await WritePlainLineAsync(Inv($"There's no message {number}.")).ConfigureAwait(false);
            return;
        }

        if (!MessageRules.CanRead(message, _userCall, _isSysop))
        {
            await WritePlainLineAsync(Inv(
                $"Message {number} is private to someone else, so I can't show it to you.")).ConfigureAwait(false);
            return;
        }

        await WritePlainPagedAsync(RenderPlainMessage(message)).ConfigureAwait(false);
        _store.MarkRead(number, _userCall);
    }

    /// <summary>Reads the caller's unread mail (newest first), sentence-wrapped — the plain RM.</summary>
    private async Task PlainReadMineAsync()
    {
        IReadOnlyList<Message> messages = _store.ListMessages(new MessageQuery { ToCall = _userCall });
        var unread = messages.Where(IsUnreadByMe).ToList();
        if (unread.Count == 0)
        {
            await WritePlainLineAsync("You have no new mail to read.").ConfigureAwait(false);
            return;
        }

        foreach (Message message in unread)
        {
            await PlainReadOneAsync(message.Number).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A read message in plain form: a short sentence header (from / date / subject) then the
    /// body. Same body bytes as classic (Latin-1, byte-transparent) via <see cref="BodyLines"/>.
    /// </summary>
    private static List<string> RenderPlainMessage(Message message)
    {
        string to = string.Join(", ", message.Recipients.Select(r => r.ToCall));
        string date = message.CreatedAt.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            Inv($"Message {message.Number}, from {message.From} to {to}, {date}."),
            Inv($"Subject: {message.Subject}"),
            "",
        };
        lines.AddRange(BodyLines(message));
        return lines;
    }

    // ---------------------------------------------------------------- reply / forward / delete

    /// <summary>
    /// <c>reply &lt;n&gt;</c> - reply to message n. Reuses <see cref="SendReplyAsync"/>'s exact
    /// behaviour (TO = the sender, auto "Re:" subject, the @ auto-completed from the sender's
    /// home), but with the plain text-entry flow and a plain confirmation.
    /// </summary>
    private async Task HandlePlainReplyAsync(string[] args)
    {
        Message? original = await ResolvePlainReadableAsync(args).ConfigureAwait(false);
        if (original is null)
        {
            return;
        }

        string? at = await PlainAutoCompleteAtAsync(original.From).ConfigureAwait(false);
        await WritePlainLineAsync(Inv($"Replying to {original.From}. Type your message;")).ConfigureAwait(false);
        Message stored = await PlainPromptForTextAndStoreAsync(new MessageDraft
        {
            Type = MessageType.Personal,
            From = _userCall,
            Recipients = [original.From],
            At = at,
            Subject = "Re:" + original.Subject,
        }).ConfigureAwait(false);
        await WritePlainSentAsync(stored, original.From).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>forward &lt;n&gt; to &lt;call&gt;</c> (or <c>forward &lt;n&gt; &lt;call&gt;</c>) - copy
    /// message n to another station. Reuses the classic copy behaviour (auto "Fwd:" subject, body
    /// copied verbatim, @ auto-completed) through <see cref="BbsStore.AddMessage"/>.
    /// </summary>
    private async Task HandlePlainForwardAsync(string[] args)
    {
        Message? original = await ResolvePlainReadableAsync(args).ConfigureAwait(false);
        if (original is null)
        {
            return;
        }

        // Accept "forward 3 to G4ABC" and "forward 3 G4ABC"; an optional "@ bbs" power form.
        string[] rest = args[1..].Where(a => !a.Equals("to", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rest.Length == 0)
        {
            await WritePlainLineAsync("Who should I forward it to? Try: forward 3 to G4ABC.").ConfigureAwait(false);
            return;
        }

        (string recipient, string? at) = SplitCallAndAt(rest);
        at ??= await PlainAutoCompleteAtAsync(recipient).ConfigureAwait(false);
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = _userCall,
            Recipients = [Callsigns.NormalizeAddressee(recipient)],
            At = at,
            Subject = "Fwd:" + original.Subject,
            Body = original.Body,
        });
        await WritePlainSentAsync(stored, recipient).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>delete &lt;n&gt;</c> - delete message(s). Reuses the classic kill path
    /// (<see cref="MessageRules.CanKill"/> rights, held-invisible) with plain wording.
    /// </summary>
    private async Task HandlePlainDeleteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            await WritePlainLineAsync("Which message? Try delete and a number, like delete 3.").ConfigureAwait(false);
            return;
        }

        foreach (string arg in args)
        {
            if (!TryParseNumber(arg, out long number))
            {
                await WritePlainLineAsync(Inv(
                    $"\"{arg}\" isn't a message number. Try delete and a number, like delete 3.")).ConfigureAwait(false);
                return;
            }

            await PlainDeleteOneAsync(number).ConfigureAwait(false);
        }
    }

    private async Task PlainDeleteOneAsync(long number)
    {
        Message? message = _store.GetMessage(number);
        if (message is null || !IsVisibleToMe(message) || message.Status == MessageStatus.Killed)
        {
            await WritePlainLineAsync(Inv($"There's no message {number} to delete.")).ConfigureAwait(false);
            return;
        }

        if (!MessageRules.CanKill(message, _userCall, _isSysop))
        {
            await WritePlainLineAsync(Inv(
                $"Message {number} isn't yours to delete.")).ConfigureAwait(false);
            return;
        }

        _store.Kill(number);
        await WritePlainLineAsync(Inv($"Deleted message {number}.")).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- send

    /// <summary>
    /// <c>send &lt;call&gt;</c> - write a new message. Addressing is just a callsign (the power
    /// form <c>send g4abc@gb7bsk</c> exists); the @ is auto-completed from the recipient's home.
    /// Then a plain title ask and the plain text-entry flow, ending in a plain confirmation. Same
    /// store path as classic SP (<see cref="BbsStore.AddMessage"/>).
    /// </summary>
    private async Task HandlePlainSendAsync(string[] args)
    {
        if (args.Length == 0)
        {
            await WritePlainLineAsync(
                "Who's it for? Type send and a callsign, like send g4abc.").ConfigureAwait(false);
            return;
        }

        (string recipient, string? at) = SplitCallAndAt(args);
        if (recipient.Length == 0)
        {
            await WritePlainLineAsync(
                "I didn't catch a callsign. Type send and a callsign, like send g4abc.").ConfigureAwait(false);
            return;
        }

        string normalized = Callsigns.NormalizeAddressee(recipient);
        at ??= await PlainAutoCompleteAtAsync(recipient).ConfigureAwait(false);

        await WritePlainLineAsync(Inv($"New message to {normalized}. What's it about?")).ConfigureAwait(false);
        string title = (await ReadLineAsync().ConfigureAwait(false)).Trim();
        if (title.Length == 0)
        {
            await WritePlainLineAsync("No subject given, so I've thrown that one away.").ConfigureAwait(false);
            return;
        }

        await WritePlainLineAsync(
            "Now type your message. End it with a line containing just /ex (or press Ctrl-Z).")
            .ConfigureAwait(false);
        Message stored = await PlainPromptForTextAndStoreAsync(new MessageDraft
        {
            Type = MessageType.Personal,
            From = _userCall,
            Recipients = [normalized],
            At = at,
            Subject = title,
        }).ConfigureAwait(false);
        await WritePlainSentAsync(stored, normalized).ConfigureAwait(false);
    }

    /// <summary>The plain text-entry: same body-terminator rules as classic (reuses <see cref="ReadMessageBodyAsync"/>).</summary>
    private async Task<Message> PlainPromptForTextAndStoreAsync(MessageDraft draft)
    {
        ReadOnlyMemory<byte> body = await ReadMessageBodyAsync().ConfigureAwait(false);
        return _store.AddMessage(draft with { Body = body });
    }

    /// <summary>The plain "your message is on its way" confirmation (no "Message: n Bid: …" wire shape).</summary>
    private ValueTask WritePlainSentAsync(Message stored, string recipient) =>
        WritePlainLineAsync(Inv($"Sent - message {stored.Number} is on its way to {recipient}."));

    /// <summary>
    /// Resolves a message number for reply/forward with sentence errors, reusing the classic
    /// visibility + read-rights checks. Returns null after emitting a friendly message.
    /// </summary>
    private async Task<Message?> ResolvePlainReadableAsync(string[] args)
    {
        if (args.Length == 0 || !TryParseNumber(args[0], out long number))
        {
            await WritePlainLineAsync("Which message? Give me a number, like 3.").ConfigureAwait(false);
            return null;
        }

        Message? message = _store.GetMessage(number);
        if (message is null || !IsVisibleToMe(message))
        {
            await WritePlainLineAsync(Inv($"There's no message {number}.")).ConfigureAwait(false);
            return null;
        }

        if (!MessageRules.CanRead(message, _userCall, _isSysop))
        {
            await WritePlainLineAsync(Inv(
                $"Message {number} is private to someone else.")).ConfigureAwait(false);
            return null;
        }

        return message;
    }

    /// <summary>
    /// Auto-completes the @ from the recipient's home mailbox, announced in a sentence (the
    /// plain twin of classic's "Address @… added from HomeBBS"). Personals only; null when the
    /// recipient has no known home.
    /// </summary>
    private async Task<string?> PlainAutoCompleteAtAsync(string recipient)
    {
        User? user = _store.GetUser(Callsigns.NormalizeAddressee(recipient));
        if (user?.HomeBbs is not { Length: > 0 } home)
        {
            return null;
        }

        await WritePlainLineAsync(Inv($"(I'll route that via their home mailbox, {home}.)")).ConfigureAwait(false);
        return home;
    }

    /// <summary>Splits "G4ABC@GB7BSK" or "G4ABC @ GB7BSK" tokens into (call, at). Power form only.</summary>
    private static (string Call, string? At) SplitCallAndAt(string[] tokens)
    {
        var filtered = new Queue<string>(tokens);
        string first = filtered.Count > 0 ? filtered.Dequeue() : "";
        int at = first.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            string call = first[..at];
            string tail = first[(at + 1)..];
            return tail.Length > 0 ? (call, tail) : (call, NextAt(filtered));
        }

        // "G4ABC @ GB7BSK" — the @ is its own token.
        if (filtered.Count > 0 && filtered.Peek() == "@")
        {
            filtered.Dequeue();
            return (first, filtered.Count > 0 ? filtered.Dequeue() : null);
        }

        return (first, null);

        static string? NextAt(Queue<string> q)
        {
            if (q.Count == 0)
            {
                return null;
            }

            string next = q.Dequeue();
            return next == "@" && q.Count > 0 ? q.Dequeue() : next;
        }
    }

    // ---------------------------------------------------------------- profile setters

    /// <summary><c>name</c> - set or show your name (same store field + 17-char cap as classic).</summary>
    private async Task HandlePlainNameAsync(string remainder)
    {
        User user = _store.GetUser(_userCall)!;
        string name = remainder.Trim();
        if (name.Length > 0)
        {
            user = user with { Name = name.Length <= 17 ? name : name[..17] };
            _store.UpsertUser(user);
            await WritePlainLineAsync(Inv($"Thanks - I'll call you {user.Name}.")).ConfigureAwait(false);
            return;
        }

        await WritePlainLineAsync(user.Name is null
            ? "I don't have your name yet. Type name followed by your name."
            : Inv($"I have your name as {user.Name}.")).ConfigureAwait(false);
    }

    /// <summary><c>qth</c> - set or show your town (same ≤30 cap + settings field as classic Q).</summary>
    private async Task HandlePlainQthAsync(string remainder)
    {
        UserSettings settings = _settings.Load(_userCall);
        string qth = remainder.Trim();
        if (qth.Length > 0)
        {
            settings = settings with { Qth = qth.Length <= 30 ? qth : qth[..30] };
            _settings.Save(_userCall, settings);
            await WritePlainLineAsync(Inv($"Got it - you're in {settings.Qth}.")).ConfigureAwait(false);
            return;
        }

        await WritePlainLineAsync(settings.Qth is null
            ? "I don't know your town yet. Type qth followed by where you are."
            : Inv($"I have you in {settings.Qth}.")).ConfigureAwait(false);
    }

    /// <summary><c>zip</c> - set or show your postcode (same ≤8 cap + settings field as classic Z).</summary>
    private async Task HandlePlainZipAsync(string remainder)
    {
        UserSettings settings = _settings.Load(_userCall);
        string zip = remainder.Trim();
        if (zip.Length > 0)
        {
            settings = settings with { Zip = zip.Length <= 8 ? zip : zip[..8] };
            _settings.Save(_userCall, settings);
            await WritePlainLineAsync(Inv($"Thanks - postcode {settings.Zip} noted.")).ConfigureAwait(false);
            return;
        }

        await WritePlainLineAsync(settings.Zip is null
            ? "I don't have your postcode. Type zip followed by it."
            : Inv($"I have your postcode as {settings.Zip}.")).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>home</c> - set, show or clear your home mailbox (same store field as classic Home; a
    /// dot clears it; a bare call still sets but gets a plain hint about the network address).
    /// </summary>
    private async Task HandlePlainHomeAsync(string[] args)
    {
        User user = _store.GetUser(_userCall)!;
        if (args.Length == 0)
        {
            await WritePlainLineAsync(user.HomeBbs is null
                ? Inv($"You don't have a home mailbox set. Type home {_bbsName} to use this one.")
                : Inv($"Your home mailbox is {user.HomeBbs}.")).ConfigureAwait(false);
            return;
        }

        string value = args[0].Trim();
        if (value == ".")
        {
            _store.UpsertUser(user with { HomeBbs = null });
            await WritePlainLineAsync("Cleared your home mailbox.").ConfigureAwait(false);
            return;
        }

        if (!value.Contains('.', StringComparison.Ordinal))
        {
            await WritePlainLineAsync(
                "Tip: a full network address (like gb7pdn.gbr.eu) helps people's mail reach you.")
                .ConfigureAwait(false);
        }

        _store.UpsertUser(user with { HomeBbs = value.ToUpperInvariant() });
        await WritePlainLineAsync(Inv($"Your home mailbox is now {value.ToUpperInvariant()}.")).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>info</c> - the system notice (or its absence), or <c>info &lt;call&gt;</c> to look a
    /// station up. Same data as classic I, in sentences.
    /// </summary>
    private async Task HandlePlainInfoAsync(string[] args)
    {
        if (args.Length == 0)
        {
            if (_config.InfoText is null)
            {
                await WritePlainLineAsync("The sysop hasn't left a system notice here.").ConfigureAwait(false);
                return;
            }

            await WritePlainPagedAsync(SplitConfigText(_config.InfoText)).ConfigureAwait(false);
            return;
        }

        string call = Callsigns.NormalizeAddressee(args[0]);
        User? user = _store.GetUser(call);
        if (user is null)
        {
            await WritePlainLineAsync(Inv($"I don't have anything on file for {call}.")).ConfigureAwait(false);
            return;
        }

        UserSettings settings = _settings.Load(call);
        var parts = new List<string> { Inv($"{user.Callsign}") };
        if (user.Name is not null)
        {
            parts.Add(Inv($"is {user.Name}"));
        }

        if (settings.Qth is not null)
        {
            parts.Add(Inv($"in {settings.Qth}"));
        }

        if (user.HomeBbs is not null)
        {
            parts.Add(Inv($"with home mailbox {user.HomeBbs}"));
        }

        await WritePlainLineAsync(string.Join(", ", parts) + ".").ConfigureAwait(false);
    }

    /// <summary><c>delivered</c> - mark a traffic message delivered (same store call as classic D).</summary>
    private async Task HandlePlainDeliveredAsync(string[] args)
    {
        if (args.Length == 0 || !TryParseNumber(args[0], out long number))
        {
            await WritePlainLineAsync("Which message? Try delivered and a number.").ConfigureAwait(false);
            return;
        }

        Message? message = _store.GetMessage(number);
        if (message is null || !IsVisibleToMe(message))
        {
            await WritePlainLineAsync(Inv($"There's no message {number}.")).ConfigureAwait(false);
            return;
        }

        if (message.Type != MessageType.Traffic)
        {
            await WritePlainLineAsync(Inv(
                $"Message {number} isn't a traffic message, so there's nothing to mark.")).ConfigureAwait(false);
            return;
        }

        _store.MarkDelivered(number);
        await WritePlainLineAsync(Inv($"Marked message {number} as delivered.")).ConfigureAwait(false);
    }

    /// <summary><c>pagelength</c> - set or show the pause-after-N-lines value (same field + rules as classic OP).</summary>
    private async Task HandlePlainPageLengthAsync(string[] args)
    {
        if (args.Length == 0)
        {
            await WritePlainLineAsync(_pageLength == 0
                ? "I show everything without pausing (page length 0)."
                : Inv($"I pause every {_pageLength} lines.")).ConfigureAwait(false);
            return;
        }

        if (!TryParseNumber(args[0], out long n) || n > int.MaxValue)
        {
            await WritePlainLineAsync("That's not a number. Try pagelength and a number, like pagelength 20.")
                .ConfigureAwait(false);
            return;
        }

        if (n is >= 1 and <= 9)
        {
            await WritePlainLineAsync(Inv($"{n} is a bit short - pick 0 (never pause) or 10 or more.")).ConfigureAwait(false);
            return;
        }

        _pageLength = (int)n;
        _settings.Save(_userCall, _settings.Load(_userCall) with { PageLength = _pageLength });
        await WritePlainLineAsync(_pageLength == 0
            ? "I'll show everything without pausing now."
            : Inv($"I'll pause every {_pageLength} lines now.")).ConfigureAwait(false);
    }

    /// <summary><c>expert</c> - toggle terser replies (same persisted field as classic X).</summary>
    private async Task HandlePlainExpertAsync()
    {
        _expert = !_expert;
        _settings.Save(_userCall, _settings.Load(_userCall) with { Expert = _expert });
        await WritePlainLineAsync(_expert
            ? "Expert mode on - I'll keep replies short."
            : "Expert mode off - I'll be my usual chatty self.").ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- plain paging + plumbing

    /// <summary>
    /// The plain pager (the mandate's "paged with a plain more? (yes/no)"). Writes lines through
    /// the per-user page length; at each page break it asks <c>more? (yes/no)</c> and a "no"
    /// (or "n") stops. Anything else continues — a friendly default. Page length 0 = no paging.
    /// </summary>
    private async Task WritePlainPagedAsync(List<string> lines)
    {
        int sincePrompt = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            await WritePlainLineAsync(lines[i]).ConfigureAwait(false);
            sincePrompt++;

            if (_pageLength > 0 && sincePrompt >= _pageLength && i < lines.Count - 1)
            {
                await WriteAsync("more? (yes/no) ").ConfigureAwait(false);
                string response = (await ReadLineAsync().ConfigureAwait(false)).Trim();
                if (response.StartsWith("n", StringComparison.OrdinalIgnoreCase))
                {
                    await WritePlainLineAsync("OK, stopping there.").ConfigureAwait(false);
                    return;
                }

                sincePrompt = 0;
            }
        }
    }

    /// <summary>A plain line: CR-terminated like the classic surface (the seam owns transport translation).</summary>
    private ValueTask WritePlainLineAsync(string line) => WriteAsync(line + "\r");

    /// <summary>Count of the caller's new mail (messages numbered after their listed pointer, visible to them).</summary>
    private int CountNewMail()
    {
        User user = _store.GetUser(_userCall)!;
        return _store.ListMessages(new MessageQuery
        {
            MinNumber = user.LastListedNumber + 1,
            ToCall = _userCall,
            IncludeHeld = false,
            IncludeKilled = false,
        }).Count(IsUnreadByMe);
    }

    /// <summary>Trims a subject to <paramref name="max"/> chars with an ellipsis so listing lines stay short.</summary>
    private static string TrimForPaclen(string text, int max)
    {
        string oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "…";
    }
}
