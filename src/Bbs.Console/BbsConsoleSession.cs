using System.Globalization;
using Bbs.Core;

namespace Bbs.Console;

/// <summary>
/// The terse RF user surface (compat spec §1) as a sans-IO session engine: one instance
/// drives one connected user's whole lifetime — connect greeting (§1.1), command loop
/// (§1.3) and sign-off (§1.2) — over an <see cref="IBbsTerminal"/>. All persistence goes
/// through <see cref="BbsStore"/> (plus the <see cref="IUserSettingsStore"/> sidecar for
/// console-only preferences); all timestamps come from the supplied <see cref="TimeProvider"/>.
/// </summary>
public sealed partial class BbsConsoleSession
{
    private readonly IBbsTerminal _terminal;
    private readonly BbsStore _store;
    private readonly BbsConsoleConfig _config;
    private readonly IUserSettingsStore _settings;
    private readonly CancellationToken _ct;

    /// <summary>The BBS name used in prompts and sign-off.</summary>
    private readonly string _bbsName;

    /// <summary>User-facing identity: base call, SSID stripped, ≤6 chars (compat spec §1.5/§2.4).</summary>
    private readonly string _userCall;

    private readonly bool _isSysop;
    private readonly bool _isBbs;

    private bool _expert;
    private int _pageLength;
    private int _errorCount;

    /// <summary>
    /// The active command surface (the plain-language mandate). Resolved at connect from the
    /// user's saved <see cref="UserSettings.InterfaceMode"/> (config default = Plain), and
    /// flipped in-session by the plain <c>classic</c> / classic <c>plain</c> commands — the
    /// new surface takes effect from the next prompt and persists via the settings store.
    /// </summary>
    private InterfaceMode _mode;

    // Note: every timestamp the session causes (logins, message receipt, read/kill stamps)
    // is recorded by BbsStore against the TimeProvider it was opened with; the session itself
    // never reads a wall clock. RunAsync still takes the TimeProvider so the composition
    // contract is explicit (and future console behaviours that need "now" don't change shape).
    private BbsConsoleSession(
        IBbsTerminal terminal,
        BbsStore store,
        BbsConsoleConfig config,
        IUserSettingsStore settings,
        CancellationToken ct)
    {
        _terminal = terminal;
        _store = store;
        _config = config;
        _settings = settings;
        _ct = ct;
        _bbsName = Callsigns.Normalize(config.BbsCallsign);
        _userCall = Callsigns.NormalizeAddressee(terminal.RemoteCallsign);

        // Sysop status is by configured callsign, SSID-insensitive (compat spec §1.4 lists
        // console/AUTH paths too — those are the Host's concern; config is the seam here).
        _isSysop = config.SysopCallsigns.Any(s => Callsigns.BaseEquals(s, terminal.RemoteCallsign));

        // The BBS user flag (compat spec §2.5) gates `< from` and the MBL-style S responses
        // (§1.5/§3.10). A caller is BBS-flagged when a partner record exists for its exact
        // connected callsign incl. SSID ("matched by exact source callsign including SSID").
        _isBbs = store.GetPartner(terminal.RemoteCallsign) is not null;

        UserSettings settingsRecord = settings.Load(_userCall);
        _expert = settingsRecord.Expert ?? config.ExpertDefault;
        _pageLength = settingsRecord.PageLength ?? config.DefaultPageLength;
        _mode = settingsRecord.InterfaceMode ?? config.DefaultInterfaceMode;
    }

    /// <summary>
    /// Runs a session with per-call (non-persistent) user preferences. See the main overload.
    /// </summary>
    public static Task<BbsSessionEndReason> RunAsync(
        IBbsTerminal terminal,
        BbsStore store,
        BbsConsoleConfig config,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => RunAsync(terminal, store, config, timeProvider, new InMemoryUserSettingsStore(), cancellationToken);

    /// <summary>
    /// Runs one user session to completion: greeting (compat spec §1.1), the command loop
    /// (§1.3) and sign-off (§1.2). Returns why the session ended so the Host can disconnect
    /// (<see cref="BbsSessionEndReason.Bye"/>), hand the link back to the node
    /// (<see cref="BbsSessionEndReason.Node"/>) or clean up a vanished caller
    /// (<see cref="BbsSessionEndReason.Drop"/>).
    /// </summary>
    /// <param name="terminal">The line-oriented session seam (Host implements over RHP).</param>
    /// <param name="store">The message/user/partner store.</param>
    /// <param name="config">Static console configuration.</param>
    /// <param name="timeProvider">Clock for every timestamp the session records.</param>
    /// <param name="userSettings">Persistent per-user console preferences (X/OP/Q/Z state).</param>
    /// <param name="cancellationToken">Cancels the session (throws <see cref="OperationCanceledException"/>).</param>
    public static async Task<BbsSessionEndReason> RunAsync(
        IBbsTerminal terminal,
        BbsStore store,
        BbsConsoleConfig config,
        TimeProvider timeProvider,
        IUserSettingsStore userSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(userSettings);

        var session = new BbsConsoleSession(terminal, store, config, userSettings, cancellationToken);
        try
        {
            return await session.RunCoreAsync().ConfigureAwait(false);
        }
        catch (BbsTerminalClosedException)
        {
            return BbsSessionEndReason.Drop;
        }
    }

    // ---------------------------------------------------------------- lifetime

    private async Task<BbsSessionEndReason> RunCoreAsync()
    {
        _store.TouchLastLogin(_userCall);

        // A BBS-flagged caller (a forwarding partner that fell through to the console, or an
        // automated peer) always gets the terse classic surface regardless of preference — its
        // client pattern-matches the legacy prompts and never types `plain`/`classic`. The
        // mandate is about *humans*: the plain default applies to interactive callers.
        if (_isBbs)
        {
            _mode = InterfaceMode.Classic;
        }

        if (!_isBbs)
        {
            await GreetAsync().ConfigureAwait(false);
        }

        while (true)
        {
            await WritePromptAsync().ConfigureAwait(false);
            string line = await ReadLineAsync().ConfigureAwait(false);
            SessionAction action = _mode == InterfaceMode.Plain
                ? await DispatchPlainAsync(line).ConfigureAwait(false)
                : await DispatchAsync(line).ConfigureAwait(false);
            switch (action)
            {
                case SessionAction.Bye:
                    return BbsSessionEndReason.Bye;
                case SessionAction.Node:
                    return BbsSessionEndReason.Node;
                case SessionAction.Continue:
                    break;
                default:
                    throw new InvalidOperationException("Unreachable.");
            }
        }
    }

    /// <summary>
    /// The connect greeting (compat spec §1.1): the new-user name prompt
    /// (<c>Please enter your Name\r&gt;\r</c>), the welcome banner (default
    /// <c>Hello $I. Latest Message is $L, Last listed is $Z</c> — opaque text that MUST NOT
    /// end in '&gt;', §1.2), and the no-home-BBS nag. BBS-flagged callers skip all of this
    /// (§1.1: a BBS caller "gets the SID instead of the chatty banner path" — the SID is the
    /// Fbb/Host side's job; the console just stays terse).
    /// </summary>
    private async Task GreetAsync()
    {
        if (_mode == InterfaceMode.Plain)
        {
            await GreetPlainAsync().ConfigureAwait(false);
            return;
        }

        User user = _store.GetUser(_userCall)!;

        if (user.Name is null)
        {
            await WriteAsync("Please enter your Name\r>\r").ConfigureAwait(false);
            string name = (await ReadLineAsync().ConfigureAwait(false)).Trim();
            if (name.Length > 0)
            {
                // Source truncates at 17 (compat spec §1.3 N command; doc says 12 — source wins).
                user = user with { Name = name.Length <= 17 ? name : name[..17] };
                _store.UpsertUser(user);
            }
        }

        string firstName = FirstToken(user.Name) ?? _userCall;
        long latest = _store.GetLatestMessageNumber();
        await WriteLineAsync(
            Inv($"Hello {firstName}. Latest Message is {latest}, Last listed is {user.LastListedNumber}")).ConfigureAwait(false);

        if (user.HomeBbs is null)
        {
            // Compat spec §1.1, verbatim two lines.
            await WriteLineAsync("Please enter your Home BBS using the Home command.").ConfigureAwait(false);
            await WriteLineAsync("You may also enter your QTH and ZIP/Postcode using qth and zip commands.").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The prompt, sent after every command completion: <c>de &lt;BBSNAME&gt;&gt;</c> + CR LF
    /// (compat spec §1.2, <c>sprintf(Prompt, "de %s&gt;\r\n", BBSName)</c>). The source default
    /// is the same for normal/new/expert users ([VERIFY-ORACLE #6]) so expert mode does not
    /// change it here.
    /// </summary>
    private ValueTask WritePromptAsync() => _mode == InterfaceMode.Plain
        ? WritePlainPromptAsync()
        : WriteAsync(Inv($"de {_bbsName}>\r\n"));

    /// <summary>Sign-off line for B/Bye/NODE: <c>73 de &lt;BBSNAME&gt;</c> + CR (compat spec §1.2).</summary>
    private ValueTask WriteSignOffAsync() => WriteLineAsync(Inv($"73 de {_bbsName}"));

    private enum SessionAction
    {
        Continue,
        Bye,
        Node,
    }

    // ---------------------------------------------------------------- dispatch

    /// <summary>Commands are case-insensitive (compat spec §1.3).</summary>
    private async Task<SessionAction> DispatchAsync(string line)
    {
        string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return SessionAction.Continue;
        }

        string verb = tokens[0].ToUpperInvariant();
        string[] args = tokens[1..];
        string remainder = AfterFirstToken(line);

        switch (verb)
        {
            case "?":
            case "H":
            case "HELP":
                await HandleHelpAsync().ConfigureAwait(false);
                return SessionAction.Continue;

            case "A":
            case "ABORT":
                // §1.3: Abort paged output. Output is written synchronously here, so outside a
                // paging prompt there is nothing queued — the response shape is still sent.
                await WriteAsync("\rOutput aborted\r").ConfigureAwait(false);
                return SessionAction.Continue;

            case "B":
            case "BYE":
                await WriteSignOffAsync().ConfigureAwait(false);
                return SessionAction.Bye;

            case "NODE":
                await WriteSignOffAsync().ConfigureAwait(false);
                return SessionAction.Node;

            case "X":
            case "XPERT":
                await HandleExpertToggleAsync().ConfigureAwait(false);
                return SessionAction.Continue;

            case "V":
                await HandleVersionAsync().ConfigureAwait(false);
                return SessionAction.Continue;

            case "N":
            case "NAME":
                await HandleNameAsync(remainder).ConfigureAwait(false);
                return SessionAction.Continue;

            case "Q":
            case "QTH":
                await HandleQthAsync(remainder).ConfigureAwait(false);
                return SessionAction.Continue;

            case "Z":
            case "ZIP":
                await HandleZipAsync(remainder).ConfigureAwait(false);
                return SessionAction.Continue;

            case "HOME":
            case "HOMEBBS":
                await HandleHomeAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case "I":
            case "INFO":
                await HandleInfoAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case "D":
            case "DELIVERED":
                await HandleDeliveredAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case "OP":
                await HandlePageLengthAsync(args).ConfigureAwait(false);
                return SessionAction.Continue;

            case "PLAIN":
                // The escape hatch back to the plain-language surface (the plain-language
                // mandate): a classic user types `plain` to flip + persist. Effective from the
                // next prompt. Classic stays byte-identical otherwise — `plain` was never a
                // W0RLI verb, so this adds a command without changing any existing one's bytes.
                await SwitchToPlainAsync().ConfigureAwait(false);
                return SessionAction.Continue;

            default:
                break;
        }

        if (verb[0] == 'L')
        {
            await HandleListAsync(verb, args).ConfigureAwait(false);
            return SessionAction.Continue;
        }

        if (verb[0] == 'R')
        {
            await HandleReadAsync(verb, args).ConfigureAwait(false);
            return SessionAction.Continue;
        }

        if (verb[0] == 'S')
        {
            await HandleSendAsync(verb, args).ConfigureAwait(false);
            return SessionAction.Continue;
        }

        if (verb[0] == 'K')
        {
            await HandleKillAsync(verb, args).ConfigureAwait(false);
            return SessionAction.Continue;
        }

        // §1.3: unknown command → "Invalid Command"; >4 in a session → close.
        await WriteLineAsync("Invalid Command").ConfigureAwait(false);
        _errorCount++;
        if (_errorCount > 4)
        {
            await WriteLineAsync("Too many errors - closing").ConfigureAwait(false);
            return SessionAction.Bye;
        }

        return SessionAction.Continue;
    }

    // ---------------------------------------------------------------- simple commands

    /// <summary>
    /// ?/H help (compat spec §1.3): config text (the help.txt replacement) when present, else
    /// a built-in summary. Help text never ends in '&gt;' (§1.2 prompt-faking guard).
    /// </summary>
    private async Task HandleHelpAsync()
    {
        string text = _config.HelpText ??
            "?/H Help  A Abort  B Bye  X Expert  V Version  NODE Exit to node\r" +
            "L List new  LR oldest-first  LM mine  LB/LP/LT by type  LL n last n  L n-m range\r" +
            "L< call from  L> call to  L@ bbs via\r" +
            "R n Read  RM Read mine  K n Kill  KM Kill mine read\r" +
            "S[P|B|T] call [@ bbs] Send  SR n Reply  SC n call Copy  (end text with /ex or ctrl/z)\r" +
            "N name  Q qth  Z zip  Home bbs  I Info  I call User lookup  OP n Page length  D n Delivered\r";
        await WritePagedAsync(SplitConfigText(text), listing: false).ConfigureAwait(false);
    }

    /// <summary>
    /// The classic <c>plain</c> command: flip the surface back to plain, persist it and confirm
    /// in plain English (the plain-language mandate). Effective from the next prompt — so this
    /// reply is the last classic-surface output. Not a W0RLI verb, so it never collides with one.
    /// </summary>
    private async Task SwitchToPlainAsync()
    {
        _mode = InterfaceMode.Plain;
        _settings.Save(_userCall, _settings.Load(_userCall) with { InterfaceMode = InterfaceMode.Plain });
        await WriteLineAsync("Switched to the plain-language surface. Type classic to come back here.").ConfigureAwait(false);
    }

    /// <summary>
    /// X toggles expert mode — "Expert Mode" / "Expert Mode off" (compat spec §1.3) — and
    /// persists it on the user's settings record.
    /// </summary>
    private async Task HandleExpertToggleAsync()
    {
        _expert = !_expert;
        _settings.Save(_userCall, _settings.Load(_userCall) with { Expert = _expert });
        await WriteLineAsync(_expert ? "Expert Mode" : "Expert Mode off").ConfigureAwait(false);
    }

    /// <summary>V — "BBS Version %s" then "Node Version %s" (compat spec §1.3; node line omitted when unconfigured).</summary>
    private async Task HandleVersionAsync()
    {
        await WriteLineAsync(Inv($"BBS Version {_config.Version}")).ConfigureAwait(false);
        if (_config.NodeVersion is not null)
        {
            await WriteLineAsync(Inv($"Node Version {_config.NodeVersion}")).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// N name — sets the user's name, truncated at 17 (compat spec §1.3: "source truncates at
    /// 17; doc says 12" — source wins). Bare N shows the current name. The reply shape
    /// mirrors the Q/Z pattern (the spec pins "QTH is %s"/"ZIP is %s" but not N's reply).
    /// </summary>
    private async Task HandleNameAsync(string remainder)
    {
        User user = _store.GetUser(_userCall)!;
        string name = remainder.Trim();
        if (name.Length > 0)
        {
            user = user with { Name = name.Length <= 17 ? name : name[..17] };
            _store.UpsertUser(user);
        }

        await WriteLineAsync(Inv($"Name is {user.Name}")).ConfigureAwait(false);
    }

    /// <summary>Q qth — reply "QTH is %s" (compat spec §1.3); stored ≤30 chars (§5 parser caps); bare Q shows.</summary>
    private async Task HandleQthAsync(string remainder)
    {
        string qth = remainder.Trim();
        UserSettings settings = _settings.Load(_userCall);
        if (qth.Length > 0)
        {
            settings = settings with { Qth = qth.Length <= 30 ? qth : qth[..30] };
            _settings.Save(_userCall, settings);
        }

        await WriteLineAsync(Inv($"QTH is {settings.Qth}")).ConfigureAwait(false);
    }

    /// <summary>Z zip — reply "ZIP is %s" (compat spec §1.3); stored ≤8 chars (§5 parser caps); bare Z shows.</summary>
    private async Task HandleZipAsync(string remainder)
    {
        string zip = remainder.Trim();
        UserSettings settings = _settings.Load(_userCall);
        if (zip.Length > 0)
        {
            settings = settings with { Zip = zip.Length <= 8 ? zip : zip[..8] };
            _settings.Save(_userCall, settings);
        }

        await WriteLineAsync(Inv($"ZIP is {settings.Zip}")).ConfigureAwait(false);
    }

    /// <summary>
    /// Home [bbs] / HOMEBBS (compat spec §1.3): bare shows; '.' deletes; a value sets. A bare
    /// call (no '.') still sets but draws the verbatim warning ("Please enter HA with HomeBBS
    /// eg g8bpq.gbr.eu - this will help message routing"). Reply "HomeBBS is %s" in all cases.
    /// </summary>
    private async Task HandleHomeAsync(string[] args)
    {
        User user = _store.GetUser(_userCall)!;
        if (args.Length > 0)
        {
            string value = args[0].Trim();
            if (value == ".")
            {
                user = user with { HomeBbs = null };
            }
            else
            {
                if (!value.Contains('.', StringComparison.Ordinal))
                {
                    await WriteLineAsync(
                        "Please enter HA with HomeBBS eg g8bpq.gbr.eu - this will help message routing").ConfigureAwait(false);
                }

                user = user with { HomeBbs = value.ToUpperInvariant() };
            }

            _store.UpsertUser(user);
        }

        await WriteLineAsync(Inv($"HomeBBS is {user.HomeBbs}")).ConfigureAwait(false);
    }

    /// <summary>
    /// I — the sysop info text, else the verbatim "SYSOP has not created an INFO file"
    /// (compat spec §1.3). I call — user lookup. The spec's I-family is WP-backed (§1.3, §5,
    /// a SHOULD per §8); this is the user-table stub: exact-call match only (no wildcards,
    /// no I@/IH/IZ), output shape local pending the WP build.
    /// </summary>
    private async Task HandleInfoAsync(string[] args)
    {
        if (args.Length == 0)
        {
            if (_config.InfoText is null)
            {
                await WriteLineAsync("SYSOP has not created an INFO file").ConfigureAwait(false);
                return;
            }

            await WritePagedAsync(SplitConfigText(_config.InfoText), listing: false).ConfigureAwait(false);
            return;
        }

        string call = Callsigns.NormalizeAddressee(args[0]);
        User? user = _store.GetUser(call);
        if (user is null)
        {
            await WriteLineAsync(Inv($"No information on {call}")).ConfigureAwait(false);
            return;
        }

        UserSettings settings = _settings.Load(call);
        await WriteLineAsync(Inv($"Callsign: {user.Callsign}")).ConfigureAwait(false);
        await WriteLineAsync(Inv($"Name: {user.Name}")).ConfigureAwait(false);
        await WriteLineAsync(Inv($"QTH: {settings.Qth}")).ConfigureAwait(false);
        await WriteLineAsync(Inv($"HomeBBS: {user.HomeBbs}")).ConfigureAwait(false);
    }

    /// <summary>
    /// D n / Delivered n — flags an NTS (T) message delivered (compat spec §1.3): "Message
    /// #%d Flagged as Delivered" / "Message %d not an NTS Message". T messages may be flagged
    /// by anyone (§2.2 gives T kill rights to anyone; delivery is the lesser act).
    /// </summary>
    private async Task HandleDeliveredAsync(string[] args)
    {
        if (args.Length == 0 || !TryParseNumber(args[0], out long number))
        {
            await WriteLineAsync("*** Error: Invalid Format").ConfigureAwait(false);
            return;
        }

        Message? message = _store.GetMessage(number);
        if (message is null || !IsVisibleToMe(message))
        {
            await WriteLineAsync(Inv($"Message {number} not found")).ConfigureAwait(false);
            return;
        }

        if (message.Type != MessageType.Traffic)
        {
            await WriteLineAsync(Inv($"Message {number} not an NTS Message")).ConfigureAwait(false);
            return;
        }

        _store.MarkDelivered(number);
        await WriteLineAsync(Inv($"Message #{number} Flagged as Delivered")).ConfigureAwait(false);
    }

    /// <summary>
    /// OP n — page length (compat spec §1.3/§1.7): 0 = off; 1–9 rejected "Page Length %d is
    /// too short"; echo "Page Length is %d". Bare OP shows. Persisted per user.
    /// </summary>
    private async Task HandlePageLengthAsync(string[] args)
    {
        if (args.Length > 0)
        {
            if (!TryParseNumber(args[0], out long n) || n > int.MaxValue)
            {
                await WriteLineAsync("*** Error: Invalid Format").ConfigureAwait(false);
                return;
            }

            if (n is >= 1 and <= 9)
            {
                await WriteLineAsync(Inv($"Page Length {n} is too short")).ConfigureAwait(false);
                return;
            }

            _pageLength = (int)n;
            _settings.Save(_userCall, _settings.Load(_userCall) with { PageLength = _pageLength });
        }

        await WriteLineAsync(Inv($"Page Length is {_pageLength}")).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- paging

    /// <summary>
    /// Writes lines through the per-user pager (compat spec §1.7). Continue prompts are the
    /// verbatim shapes — generic <c>&lt;A&gt;bort, &lt;CR&gt; Continue..&gt;</c>, listings
    /// <c>&lt;A&gt;bort, &lt;R Msg(s)&gt;, &lt;CR&gt; = Continue..&gt;</c> — A aborts with
    /// "\rOutput aborted\r", and in listings <c>R nnn</c> reads mid-list then the list
    /// resumes. Returns false when the user aborted.
    /// </summary>
    private async Task<bool> WritePagedAsync(List<string> lines, bool listing)
    {
        int sincePrompt = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            await WriteLineAsync(lines[i]).ConfigureAwait(false);
            sincePrompt++;

            if (_pageLength > 0 && sincePrompt >= _pageLength && i < lines.Count - 1)
            {
                await WriteLineAsync(listing
                    ? "<A>bort, <R Msg(s)>, <CR> = Continue..>"
                    : "<A>bort, <CR> Continue..>").ConfigureAwait(false);

                string response = (await ReadLineAsync().ConfigureAwait(false)).Trim();
                if (response.Equals("A", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync("\rOutput aborted\r").ConfigureAwait(false);
                    return false;
                }

                if (listing && response.Length > 1
                    && char.ToUpperInvariant(response[0]) == 'R')
                {
                    foreach (string arg in response[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (TryParseNumber(arg, out long number))
                        {
                            // Mid-list reads are unpaged so the listing pager state stays simple.
                            await ReadOneMessageAsync(number, paged: false).ConfigureAwait(false);
                        }
                    }
                }

                sincePrompt = 0;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- plumbing

    private ValueTask WriteAsync(string text) => _terminal.WriteAsync(text, _ct);

    /// <summary>CR-terminated output discipline (compat spec §1.2; the seam owns any translation).</summary>
    private ValueTask WriteLineAsync(string line) => WriteAsync(line + "\r");

    private async ValueTask<string> ReadLineAsync()
    {
        string? line = await _terminal.ReadLineAsync(_ct).ConfigureAwait(false);
        return line ?? throw new BbsTerminalClosedException();
    }

    private bool IsVisibleToMe(Message message) =>
        MessageRules.IsVisibleInLists(message, _isSysop);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static bool TryParseNumber(string text, out long number) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static string? FirstToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 ? tokens[0] : null;
    }

    private static string AfterFirstToken(string line)
    {
        string trimmed = line.TrimStart();
        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? "" : trimmed[(space + 1)..];
    }

    private static List<string> SplitConfigText(string text)
    {
        string normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        var lines = new List<string>(normalized.Split('\r'));
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
