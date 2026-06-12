using Bbs.Core;

namespace Bbs.Console;

/// <summary>
/// The plain-language command surface (the plain-language mandate, design.md). Canonical
/// commands are words; <b>any unambiguous prefix of a word works</b> (so a die-hard's
/// fingers still find <c>l</c>, <c>r 3</c>, <c>q</c>); an ambiguous prefix gets a friendly
/// "did you mean …?". Each command maps onto the SAME underlying store operations the classic
/// verbs call (the <c>.List</c>/<c>.ReadKill</c>/<c>.Send</c> handlers) — this file re-skins
/// the surface, it does not reimplement mail behaviour. Sentence rendering lives in
/// <c>.PlainRender.cs</c>.
/// </summary>
public sealed partial class BbsConsoleSession
{
    /// <summary>
    /// The plain command table: the canonical word, what it does, and the help sentence. Order
    /// is the help-listing order. Prefix matching (see <see cref="ResolvePlainCommand"/>) runs
    /// over <see cref="PlainCommand.Word"/>; the existing classic word-aliases (qth/zip/info/
    /// bye/abort/homebbs/delivered/xpert) are folded in so a classic-era user's words still work.
    /// </summary>
    private enum PlainCommand
    {
        Help,
        List,
        Read,
        Send,
        Reply,
        Forward,
        Delete,
        Bulletins,
        Topics,
        Name,
        Qth,
        Zip,
        Home,
        Info,
        Delivered,
        Pagelength,
        Expert,
        Classic,
        Plain,
        Quit,
        Node,
    }

    private readonly record struct PlainCommandInfo(PlainCommand Command, string Word, string Help);

    /// <summary>
    /// The canonical plain vocabulary. The words a user can type (and abbreviate by any
    /// unambiguous prefix); the help sentences read in this order. Words that share a prefix
    /// (<c>read</c>/<c>reply</c>; <c>list</c>; <c>delete</c>/<c>delivered</c>) make that prefix
    /// ambiguous — the resolver then asks which one.
    /// </summary>
    private static readonly PlainCommandInfo[] PlainCommands =
    [
        new(PlainCommand.Help, "help", "help - show this list of what you can type."),
        new(PlainCommand.List, "list", "list - show your new mail (most recent first)."),
        new(PlainCommand.Read, "read", "read <n> - read message number n (e.g. read 3)."),
        new(PlainCommand.Send, "send", "send <call> - write a new message to a station (e.g. send g4abc)."),
        new(PlainCommand.Reply, "reply", "reply <n> - reply to message number n."),
        new(PlainCommand.Forward, "forward", "forward <n> to <call> - send a copy of message n to someone."),
        new(PlainCommand.Delete, "delete", "delete <n> - delete message number n."),
        new(PlainCommand.Bulletins, "bulletins", "bulletins - show the latest bulletins everyone can read."),
        new(PlainCommand.Topics, "topics", "topics - show the bulletin topics in use."),
        new(PlainCommand.Name, "name", "name <your name> - tell me your name."),
        new(PlainCommand.Qth, "qth", "qth <your town> - tell me where you are."),
        new(PlainCommand.Zip, "zip", "zip <postcode> - tell me your postcode."),
        new(PlainCommand.Home, "home", "home <bbs> - set your home mailbox so people can reach you."),
        new(PlainCommand.Info, "info", "info [call] - read the system notice, or look up a station."),
        new(PlainCommand.Delivered, "delivered", "delivered <n> - mark a traffic message as delivered."),
        new(PlainCommand.Pagelength, "pagelength", "pagelength <n> - how many lines to show before pausing (0 = never)."),
        new(PlainCommand.Expert, "expert", "expert - turn the shorter, less chatty replies on or off."),
        new(PlainCommand.Classic, "classic", "classic - switch to the old-style terse command surface."),
        new(PlainCommand.Plain, "plain", "plain - stay on this plain-language surface (you are here)."),
        new(PlainCommand.Quit, "quit", "quit - sign off and disconnect."),
        new(PlainCommand.Node, "node", "node - leave the mailbox and go back to the node."),
    ];

    // ---------------------------------------------------------------- dispatch

    /// <summary>
    /// One plain-surface command. Splits the verb from the rest, resolves the verb (exact word
    /// or unambiguous prefix), and routes to the plain handler. An empty line just reprompts; an
    /// unknown verb or an ambiguous prefix gets a friendly sentence (no W0RLI "Invalid Command").
    /// </summary>
    private async Task<SessionAction> DispatchPlainAsync(string line)
    {
        string verb = FirstToken(line) ?? "";
        if (verb.Length == 0)
        {
            return SessionAction.Continue;
        }

        string remainder = AfterFirstToken(line);
        string[] args = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        PlainResolution resolution = ResolvePlainCommand(verb);
        if (resolution.Kind == PlainResolutionKind.Unknown)
        {
            await WritePlainLineAsync(Inv(
                $"Sorry, I don't know \"{verb}\". Type help to see what you can do.")).ConfigureAwait(false);
            return SessionAction.Continue;
        }

        if (resolution.Kind == PlainResolutionKind.Ambiguous)
        {
            await WritePlainLineAsync(PlainAmbiguityHint(verb, resolution.Candidates)).ConfigureAwait(false);
            return SessionAction.Continue;
        }

        switch (resolution.Command)
        {
            case PlainCommand.Help:
                await HandlePlainHelpAsync().ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.List:
                await HandlePlainListAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Read:
                await HandlePlainReadAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Send:
                await HandlePlainSendAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Reply:
                await HandlePlainReplyAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Forward:
                await HandlePlainForwardAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Delete:
                await HandlePlainDeleteAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Bulletins:
                await HandlePlainBulletinsAsync().ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Topics:
                await HandlePlainTopicsAsync().ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Name:
                await HandlePlainNameAsync(remainder).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Qth:
                await HandlePlainQthAsync(remainder).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Zip:
                await HandlePlainZipAsync(remainder).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Home:
                await HandlePlainHomeAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Info:
                await HandlePlainInfoAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Delivered:
                await HandlePlainDeliveredAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Pagelength:
                await HandlePlainPageLengthAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Expert:
                await HandlePlainExpertAsync().ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Classic:
                await SwitchToClassicAsync().ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Plain:
                await WritePlainLineAsync("You are already using the plain-language surface.").ConfigureAwait(false);
                return SessionAction.Continue;

            case PlainCommand.Quit:
                await WritePlainSignOffAsync().ConfigureAwait(false);
                return SessionAction.Bye;

            case PlainCommand.Node:
                await WritePlainSignOffAsync().ConfigureAwait(false);
                return SessionAction.Node;

            default:
                throw new InvalidOperationException("Unreachable plain command.");
        }
    }

    /// <summary>
    /// The muscle-memory shortcuts (the plain-language mandate's examples: <c>l</c>, <c>r 3</c>,
    /// <c>q</c>, <c>re</c>…). These resolve a handful of short prefixes that the bare
    /// unambiguous-prefix rule would otherwise call ambiguous — <c>r</c> is read (not read/reply),
    /// <c>q</c> is quit (not quit/qth), <c>h</c> is help (not help/home), <c>re</c> is reply — to
    /// the command a packet op's fingers expect. They are checked before prefix matching; any
    /// other prefix (or a longer, naturally-unambiguous one) falls through to the general rule.
    /// </summary>
    private static readonly Dictionary<string, PlainCommand> PlainShortcuts = new(StringComparer.Ordinal)
    {
        ["l"] = PlainCommand.List,
        ["r"] = PlainCommand.Read,
        ["re"] = PlainCommand.Reply,
        ["s"] = PlainCommand.Send,
        ["q"] = PlainCommand.Quit,
        ["h"] = PlainCommand.Help,
        ["d"] = PlainCommand.Delete,
        ["b"] = PlainCommand.Bulletins,
        ["n"] = PlainCommand.Name,
    };

    // ---------------------------------------------------------------- prefix matching

    private enum PlainResolutionKind
    {
        Exact,
        Prefix,
        Ambiguous,
        Unknown,
    }

    private readonly record struct PlainResolution(
        PlainResolutionKind Kind, PlainCommand Command, IReadOnlyList<string> Candidates);

    /// <summary>
    /// Resolves a typed verb to a command (the plain-language mandate's prefix rule). An exact
    /// word wins outright; otherwise any word the verb is a prefix of is a candidate — exactly
    /// one ⇒ that command, several ⇒ ambiguous (the caller asks "did you mean …?"), none ⇒
    /// unknown. Case-insensitive. The exact-match-wins rule means a full word that is also a
    /// prefix of a longer one (none today, but e.g. <c>read</c>) never reports ambiguous.
    /// </summary>
    private static PlainResolution ResolvePlainCommand(string verb)
    {
        string v = verb.ToLowerInvariant();

        foreach (PlainCommandInfo info in PlainCommands)
        {
            if (string.Equals(info.Word, v, StringComparison.Ordinal))
            {
                return new PlainResolution(PlainResolutionKind.Exact, info.Command, []);
            }
        }

        // The muscle-memory shortcuts win over the bare ambiguous-prefix rule (the mandate's
        // `l`/`r 3`/`q`/`re` examples). A full word is still an exact match above, so this only
        // ever rescues an otherwise-ambiguous short prefix.
        if (PlainShortcuts.TryGetValue(v, out PlainCommand shortcut))
        {
            return new PlainResolution(PlainResolutionKind.Prefix, shortcut, []);
        }

        var matches = new List<PlainCommandInfo>();
        foreach (PlainCommandInfo info in PlainCommands)
        {
            if (info.Word.StartsWith(v, StringComparison.Ordinal))
            {
                matches.Add(info);
            }
        }

        if (matches.Count == 0)
        {
            return new PlainResolution(PlainResolutionKind.Unknown, default, []);
        }

        if (matches.Count == 1)
        {
            return new PlainResolution(PlainResolutionKind.Prefix, matches[0].Command, []);
        }

        return new PlainResolution(
            PlainResolutionKind.Ambiguous, default, [.. matches.Select(m => m.Word)]);
    }

    /// <summary>The "did you mean …?" sentence for an ambiguous prefix (e.g. <c>re</c> → read/reply).</summary>
    private static string PlainAmbiguityHint(string verb, IReadOnlyList<string> candidates)
    {
        string list = candidates.Count == 2
            ? Inv($"{candidates[0]} or {candidates[1]}")
            : Inv($"{string.Join(", ", candidates.Take(candidates.Count - 1))} or {candidates[^1]}");
        return Inv($"Did you mean {list}? \"{verb}\" could be any of those - type a bit more.");
    }

    // ---------------------------------------------------------------- mode toggle

    /// <summary>
    /// The plain <c>classic</c> command: flip the surface to classic, persist it (so it sticks
    /// across reconnect via the settings store) and confirm in plain English. The change takes
    /// effect from the next prompt — this command's own reply is the last plain output.
    /// </summary>
    private async Task SwitchToClassicAsync()
    {
        _mode = InterfaceMode.Classic;
        _settings.Save(_userCall, _settings.Load(_userCall) with { InterfaceMode = InterfaceMode.Classic });
        await WritePlainLineAsync(
            "Switched to the classic terse surface. Type plain to come back here.").ConfigureAwait(false);
    }
}
