using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Imap;

/// <summary>
/// One IMAP connection's protocol engine: the state machine (NotAuthenticated → Authenticated →
/// Selected) and command dispatch over an <see cref="ImapConnection"/>, backed by an
/// <see cref="ImapBackend"/>. Read-mostly MVP per RFC 3501 — enough for iPhone Mail and MailKit to
/// read packet mail end to end: CAPABILITY, LOGIN/AUTHENTICATE PLAIN, LIST/LSUB/STATUS,
/// SELECT/EXAMINE, FETCH/UID FETCH, STORE/UID STORE (<c>\Seen</c>), SEARCH/UID SEARCH,
/// NOOP/CHECK/CLOSE/LOGOUT/EXPUNGE, and IDLE (RFC 2177 — live new-mail push for iPhone Mail).
/// </summary>
public sealed partial class ImapSession
{
    /// <summary>
    /// The capability list advertised in the greeting and by <c>CAPABILITY</c>. <c>IDLE</c> (RFC 2177)
    /// tells iPhone Mail it can hold the connection open and be pushed new-mail notifications, instead
    /// of falling back to its slow scheduled fetch.
    /// </summary>
    public const string Capabilities = "IMAP4rev1 AUTH=PLAIN IDLE";

    /// <summary>The ceiling on a single IDLE before the server ends it (RFC 2177's ~29-minute note).</summary>
    private static readonly TimeSpan MaxIdleDuration = TimeSpan.FromMinutes(29);

    private readonly ImapConnection _connection;
    private readonly ImapBackend _backend;
    private readonly TimeSpan _idlePollInterval;
    private readonly ILogger _logger;

    private string? _callsign;
    private ImapMailbox? _mailbox;
    private bool _readOnly;
    private bool _loggedOut;

    /// <summary>Creates a session over <paramref name="connection"/> backed by <paramref name="backend"/>.</summary>
    /// <param name="connection">The line-and-literal transport.</param>
    /// <param name="backend">The store-facing backend.</param>
    /// <param name="idlePollInterval">How often an IDLE-ing session re-checks for new mail; default 5s.</param>
    /// <param name="logger">Logs auth outcomes (never the password); null = no-op.</param>
    public ImapSession(ImapConnection connection, ImapBackend backend, TimeSpan? idlePollInterval = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(backend);
        _connection = connection;
        _backend = backend;
        _idlePollInterval = idlePollInterval is { } i && i > TimeSpan.Zero ? i : TimeSpan.FromSeconds(5);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Runs the session to completion: sends the greeting, then reads and dispatches commands until
    /// the client logs out, the connection ends, or cancellation. Never throws for protocol errors —
    /// they are answered as tagged <c>BAD</c>/<c>NO</c>; only I/O faults propagate.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _connection.WriteAsync(
            $"* OK [CAPABILITY {Capabilities}] pdn-bbs ready\r\n", cancellationToken).ConfigureAwait(false);

        while (!_loggedOut && !cancellationToken.IsCancellationRequested)
        {
            string? line = await _connection.ReadCommandAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break; // client closed the connection
            }

            await DispatchAsync(line, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(string line, CancellationToken cancellationToken)
    {
        if (!ImapCommandParser.TryTokenize(line, out IReadOnlyList<ImapToken> tokens) || tokens.Count == 0)
        {
            await _connection.WriteAsync("* BAD Unparseable command\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        string tag = tokens[0].Value;
        if (tokens.Count < 2)
        {
            await Tagged(tag, "BAD", "Missing command", cancellationToken).ConfigureAwait(false);
            return;
        }

        string command = tokens[1].Value.ToUpperInvariant();
        IReadOnlyList<ImapToken> args = [.. tokens.Skip(2)];

        switch (command)
        {
            case "CAPABILITY":
                await _connection.WriteAsync($"* CAPABILITY {Capabilities}\r\n", cancellationToken).ConfigureAwait(false);
                await Tagged(tag, "OK", "CAPABILITY completed", cancellationToken).ConfigureAwait(false);
                break;
            case "ID":
                // RFC 2971: clients (iPhone Mail) send ID on connect; answer with our name, then OK.
                await _connection.WriteAsync("* ID (\"name\" \"pdn-bbs\")\r\n", cancellationToken).ConfigureAwait(false);
                await Tagged(tag, "OK", "ID completed", cancellationToken).ConfigureAwait(false);
                break;
            case "NAMESPACE":
                await HandleNamespaceAsync(tag, cancellationToken).ConfigureAwait(false);
                break;
            case "SEARCH":
                await HandleSearchAsync(tag, args, byUid: false, cancellationToken).ConfigureAwait(false);
                break;
            case "NOOP":
                await ReportNewMailAsync(cancellationToken).ConfigureAwait(false);
                await Tagged(tag, "OK", "NOOP completed", cancellationToken).ConfigureAwait(false);
                break;
            case "IDLE":
                await HandleIdleAsync(tag, cancellationToken).ConfigureAwait(false);
                break;
            case "LOGOUT":
                await _connection.WriteAsync("* BYE pdn-bbs signing off\r\n", cancellationToken).ConfigureAwait(false);
                await Tagged(tag, "OK", "LOGOUT completed", cancellationToken).ConfigureAwait(false);
                _loggedOut = true;
                break;
            case "LOGIN":
                await HandleLoginAsync(tag, args, cancellationToken).ConfigureAwait(false);
                break;
            case "AUTHENTICATE":
                await HandleAuthenticateAsync(tag, args, cancellationToken).ConfigureAwait(false);
                break;
            case "LIST":
                await HandleListAsync(tag, args, lsub: false, cancellationToken).ConfigureAwait(false);
                break;
            case "LSUB":
                await HandleListAsync(tag, args, lsub: true, cancellationToken).ConfigureAwait(false);
                break;
            case "STATUS":
                await HandleStatusAsync(tag, args, cancellationToken).ConfigureAwait(false);
                break;
            case "SELECT":
                await HandleSelectAsync(tag, args, examine: false, cancellationToken).ConfigureAwait(false);
                break;
            case "EXAMINE":
                await HandleSelectAsync(tag, args, examine: true, cancellationToken).ConfigureAwait(false);
                break;
            case "FETCH":
                await HandleFetchAsync(tag, args, byUid: false, cancellationToken).ConfigureAwait(false);
                break;
            case "STORE":
                await HandleStoreAsync(tag, args, byUid: false, cancellationToken).ConfigureAwait(false);
                break;
            case "UID":
                await HandleUidAsync(tag, args, cancellationToken).ConfigureAwait(false);
                break;
            case "CHECK":
                if (_mailbox is null)
                {
                    await Tagged(tag, "NO", "No mailbox selected", cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ReportNewMailAsync(cancellationToken).ConfigureAwait(false);
                    await Tagged(tag, "OK", "CHECK completed", cancellationToken).ConfigureAwait(false);
                }

                break;
            case "EXPUNGE":
                // Nothing is deletable this slice; EXPUNGE is a successful no-op (no untagged EXPUNGE).
                await RequireSelected(tag, "EXPUNGE completed", cancellationToken).ConfigureAwait(false);
                break;
            case "CLOSE":
                await HandleCloseAsync(tag, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await Tagged(tag, "BAD", $"Unsupported command {command}", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    // ------------------------------------------------------------------ auth

    private async Task HandleLoginAsync(string tag, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        if (_callsign is not null)
        {
            await Tagged(tag, "BAD", "Already authenticated", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (args.Count < 2)
        {
            await Tagged(tag, "BAD", "LOGIN requires a user and password", cancellationToken).ConfigureAwait(false);
            return;
        }

        string? callsign = _backend.Authenticate(args[0].Value, args[1].Value);
        if (callsign is null)
        {
            LogAuthFailed(_logger, "LOGIN", args[0].Value, args[1].Value.Length);
            await Tagged(tag, "NO", "[AUTHENTICATIONFAILED] Invalid credentials", cancellationToken).ConfigureAwait(false);
            return;
        }

        _callsign = callsign;
        LogAuthOk(_logger, "LOGIN", callsign);
        await Tagged(tag, "OK", $"[CAPABILITY {Capabilities}] LOGIN completed", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleAuthenticateAsync(string tag, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        if (_callsign is not null)
        {
            await Tagged(tag, "BAD", "Already authenticated", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (args.Count < 1 || !string.Equals(args[0].Value, "PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            await Tagged(tag, "NO", "Only AUTH=PLAIN is supported", cancellationToken).ConfigureAwait(false);
            return;
        }

        // The client may inline the initial response (SASL-IR) as a second arg, else we prompt for it.
        string base64;
        if (args.Count >= 2)
        {
            base64 = args[1].Value;
        }
        else
        {
            await _connection.WriteAsync("+ \r\n", cancellationToken).ConfigureAwait(false);
            string? response = await _connection.ReadCommandAsync(cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                _loggedOut = true;
                return;
            }

            base64 = response.Trim();
            if (base64 == "*")
            {
                await Tagged(tag, "BAD", "Authentication cancelled", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (!TryDecodePlain(base64, out string user, out string password))
        {
            LogAuthMalformed(_logger, "AUTHENTICATE PLAIN");
            await Tagged(tag, "BAD", "Malformed AUTH=PLAIN response", cancellationToken).ConfigureAwait(false);
            return;
        }

        string? callsign = _backend.Authenticate(user, password);
        if (callsign is null)
        {
            LogAuthFailed(_logger, "AUTHENTICATE PLAIN", user, password.Length);
            await Tagged(tag, "NO", "[AUTHENTICATIONFAILED] Invalid credentials", cancellationToken).ConfigureAwait(false);
            return;
        }

        _callsign = callsign;
        LogAuthOk(_logger, "AUTHENTICATE PLAIN", callsign);
        await Tagged(tag, "OK", $"[CAPABILITY {Capabilities}] AUTHENTICATE completed", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decodes a SASL PLAIN response: <c>base64(authzid NUL authcid NUL passwd)</c> (RFC 4616).</summary>
    private static bool TryDecodePlain(string base64, out string user, out string password)
    {
        user = string.Empty;
        password = string.Empty;
        try
        {
            byte[] raw = Convert.FromBase64String(base64);
            string decoded = Encoding.UTF8.GetString(raw);
            string[] parts = decoded.Split('\0');
            if (parts.Length != 3)
            {
                return false;
            }

            user = parts[1]; // authcid (the authzid in parts[0] is ignored)
            password = parts[2];
            return user.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ folders

    private async Task HandleListAsync(string tag, IReadOnlyList<ImapToken> args, bool lsub, CancellationToken cancellationToken)
    {
        if (_callsign is null)
        {
            await Tagged(tag, "NO", "Authenticate first", cancellationToken).ConfigureAwait(false);
            return;
        }

        string verb = lsub ? "LSUB" : "LIST";
        if (args.Count < 2)
        {
            await Tagged(tag, "BAD", $"{verb} requires a reference and pattern", cancellationToken).ConfigureAwait(false);
            return;
        }

        // The reference (args[0]) is unused (we have a flat-ish single namespace); the pattern is
        // args[1]. The empty pattern with an empty reference is the "list the hierarchy delimiter" probe.
        string pattern = args[1].Value;
        if (pattern.Length == 0 && args[0].Value.Length == 0)
        {
            await _connection.WriteAsync(
                $"* {verb} (\\Noselect) \"{ImapBackend.HierarchyDelimiter}\" \"\"\r\n", cancellationToken).ConfigureAwait(false);
            await Tagged(tag, "OK", $"{verb} completed", cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (ImapFolder folder in _backend.ListFolders(_callsign))
        {
            if (!MatchesPattern(folder.Name, pattern))
            {
                continue;
            }

            string attributes = folder.Selectable ? "" : "\\Noselect";
            await _connection.WriteAsync(
                $"* {verb} ({attributes}) \"{ImapBackend.HierarchyDelimiter}\" {Quote(folder.Name)}\r\n",
                cancellationToken).ConfigureAwait(false);
        }

        await Tagged(tag, "OK", $"{verb} completed", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleStatusAsync(string tag, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        if (_callsign is null)
        {
            await Tagged(tag, "NO", "Authenticate first", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (args.Count < 2 || args[1].Kind != ImapTokenKind.List)
        {
            await Tagged(tag, "BAD", "STATUS requires a mailbox and item list", cancellationToken).ConfigureAwait(false);
            return;
        }

        ImapFolder? folder = _backend.ResolveFolder(_callsign, args[0].Value);
        if (folder is null || !folder.Selectable)
        {
            await Tagged(tag, "NO", "No such mailbox", cancellationToken).ConfigureAwait(false);
            return;
        }

        ImapMailbox? mailbox = _backend.OpenMailbox(_callsign, folder);
        if (mailbox is null)
        {
            await Tagged(tag, "NO", "Mailbox is not selectable", cancellationToken).ConfigureAwait(false);
            return;
        }

        string[] requested = args[1].Value.Trim('(', ')').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        foreach (string item in requested)
        {
            switch (item.ToUpperInvariant())
            {
                case "MESSAGES": parts.Add($"MESSAGES {mailbox.Count}"); break;
                case "RECENT": parts.Add("RECENT 0"); break;
                case "UIDNEXT": parts.Add($"UIDNEXT {mailbox.UidNext}"); break;
                case "UIDVALIDITY": parts.Add($"UIDVALIDITY {mailbox.UidValidity}"); break;
                case "UNSEEN": parts.Add($"UNSEEN {mailbox.UnseenCount}"); break;
                default: break; // ignore unknown items
            }
        }

        await _connection.WriteAsync(
            $"* STATUS {Quote(folder.Name)} ({string.Join(' ', parts)})\r\n", cancellationToken).ConfigureAwait(false);
        await Tagged(tag, "OK", "STATUS completed", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSelectAsync(string tag, IReadOnlyList<ImapToken> args, bool examine, CancellationToken cancellationToken)
    {
        if (_callsign is null)
        {
            await Tagged(tag, "NO", "Authenticate first", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (args.Count < 1)
        {
            await Tagged(tag, "BAD", "SELECT requires a mailbox", cancellationToken).ConfigureAwait(false);
            return;
        }

        ImapFolder? folder = _backend.ResolveFolder(_callsign, args[0].Value);
        if (folder is null || !folder.Selectable)
        {
            // De-select any previously selected mailbox on a failed SELECT (RFC 3501 §6.3.1).
            _mailbox = null;
            await Tagged(tag, "NO", "No such mailbox", cancellationToken).ConfigureAwait(false);
            return;
        }

        ImapMailbox? mailbox = _backend.OpenMailbox(_callsign, folder);
        if (mailbox is null)
        {
            _mailbox = null;
            await Tagged(tag, "NO", "Mailbox is not selectable", cancellationToken).ConfigureAwait(false);
            return;
        }

        _mailbox = mailbox;
        _readOnly = examine;

        var sb = new StringBuilder();
        sb.Append($"* {mailbox.Count} EXISTS\r\n");
        sb.Append("* 0 RECENT\r\n");
        sb.Append($"* OK [UIDVALIDITY {mailbox.UidValidity}] UIDs valid\r\n");
        sb.Append($"* OK [UIDNEXT {mailbox.UidNext}] Predicted next UID\r\n");
        sb.Append("* FLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft)\r\n");
        sb.Append("* OK [PERMANENTFLAGS (\\Seen)] Limited\r\n");
        if (mailbox.FirstUnseenSequence is { } unseen)
        {
            sb.Append($"* OK [UNSEEN {unseen}] First unseen\r\n");
        }

        await _connection.WriteAsync(sb.ToString(), cancellationToken).ConfigureAwait(false);

        string access = examine ? "READ-ONLY" : "READ-WRITE";
        await Tagged(tag, "OK", $"[{access}] {(examine ? "EXAMINE" : "SELECT")} completed", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCloseAsync(string tag, CancellationToken cancellationToken)
    {
        if (_mailbox is null)
        {
            await Tagged(tag, "BAD", "No mailbox selected", cancellationToken).ConfigureAwait(false);
            return;
        }

        _mailbox = null;
        _readOnly = false;
        await Tagged(tag, "OK", "CLOSE completed", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The single flat namespace (personal mailboxes under a "/" hierarchy; no shared/other users).</summary>
    private async Task HandleNamespaceAsync(string tag, CancellationToken cancellationToken)
    {
        if (_callsign is null)
        {
            await Tagged(tag, "NO", "Authenticate first", cancellationToken).ConfigureAwait(false);
            return;
        }

        await _connection.WriteAsync(
            $"* NAMESPACE ((\"\" \"{ImapBackend.HierarchyDelimiter}\")) NIL NIL\r\n", cancellationToken).ConfigureAwait(false);
        await Tagged(tag, "OK", "NAMESPACE completed", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>SEARCH</c> / <c>UID SEARCH</c> (RFC 3501 §6.4.4) over the selected snapshot — what a client
    /// (iPhone Mail) uses to enumerate a mailbox. Emits <c>* SEARCH n1 n2 …</c> (message sequence
    /// numbers, or UIDs when <paramref name="byUid"/>) then a tagged OK; a malformed program is BAD.
    /// </summary>
    private async Task HandleSearchAsync(string tag, IReadOnlyList<ImapToken> args, bool byUid, CancellationToken cancellationToken)
    {
        if (_mailbox is null)
        {
            await Tagged(tag, "NO", "No mailbox selected", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!ImapSearch.TryEvaluate(args, _mailbox, byUid, out IReadOnlyList<long> matches))
        {
            await Tagged(tag, "BAD", "Invalid SEARCH criteria", cancellationToken).ConfigureAwait(false);
            return;
        }

        string list = matches.Count == 0 ? string.Empty : " " + string.Join(' ', matches);
        await _connection.WriteAsync($"* SEARCH{list}\r\n", cancellationToken).ConfigureAwait(false);
        await Tagged(tag, "OK", "SEARCH completed", cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ fetch / store / uid

    private async Task HandleUidAsync(string tag, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        if (args.Count < 1)
        {
            await Tagged(tag, "BAD", "UID requires a subcommand", cancellationToken).ConfigureAwait(false);
            return;
        }

        string sub = args[0].Value.ToUpperInvariant();
        IReadOnlyList<ImapToken> rest = [.. args.Skip(1)];
        switch (sub)
        {
            case "FETCH":
                await HandleFetchAsync(tag, rest, byUid: true, cancellationToken).ConfigureAwait(false);
                break;
            case "STORE":
                await HandleStoreAsync(tag, rest, byUid: true, cancellationToken).ConfigureAwait(false);
                break;
            case "SEARCH":
                await HandleSearchAsync(tag, rest, byUid: true, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await Tagged(tag, "BAD", $"Unsupported UID subcommand {sub}", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleFetchAsync(string tag, IReadOnlyList<ImapToken> args, bool byUid, CancellationToken cancellationToken)
    {
        if (_mailbox is null)
        {
            await Tagged(tag, "NO", "No mailbox selected", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (args.Count < 2)
        {
            await Tagged(tag, "BAD", "FETCH requires a set and item list", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryResolveHandles(args[0].Value, byUid, out IReadOnlyList<ImapMessageHandle> handles))
        {
            await Tagged(tag, "BAD", "Invalid sequence set", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryParseFetchItems(args[1], out IReadOnlyList<string> items))
        {
            await Tagged(tag, "BAD", "Invalid FETCH item list", cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (ImapMessageHandle handle in handles)
        {
            var fetch = ImapFetch.Build(handle, items, alwaysIncludeUid: byUid);

            // A non-PEEK body fetch sets \Seen — mark it before writing so the FLAGS we report (and a
            // re-FETCH) reflect the change; never on an EXAMINE-opened (read-only) mailbox.
            bool flagChanged = false;
            if (fetch.TouchedBody && !_readOnly)
            {
                flagChanged = _mailbox.MarkSeen(handle);
            }

            await fetch.WriteResponseAsync(_connection, handle.Sequence, cancellationToken).ConfigureAwait(false);

            // If the fetch changed \Seen but the client didn't ask for FLAGS, report the new flags as a
            // separate untagged FETCH so the client learns of the change (RFC 3501 §7.4.2 note).
            if (flagChanged && !items.Any(i => i.Equals("FLAGS", StringComparison.OrdinalIgnoreCase)))
            {
                string uidSuffix = byUid ? $" UID {handle.Uid}" : string.Empty;
                await _connection.WriteAsync(
                    $"* {handle.Sequence} FETCH (FLAGS (\\Seen){uidSuffix})\r\n", cancellationToken).ConfigureAwait(false);
            }
        }

        await Tagged(tag, "OK", $"{(byUid ? "UID FETCH" : "FETCH")} completed", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleStoreAsync(string tag, IReadOnlyList<ImapToken> args, bool byUid, CancellationToken cancellationToken)
    {
        if (_mailbox is null)
        {
            await Tagged(tag, "NO", "No mailbox selected", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (args.Count < 3)
        {
            await Tagged(tag, "BAD", "STORE requires a set, an action and flags", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryResolveHandles(args[0].Value, byUid, out IReadOnlyList<ImapMessageHandle> handles))
        {
            await Tagged(tag, "BAD", "Invalid sequence set", cancellationToken).ConfigureAwait(false);
            return;
        }

        string action = args[1].Value.ToUpperInvariant();
        bool silent = action.EndsWith(".SILENT", StringComparison.Ordinal);
        bool add = action.StartsWith('+');
        bool remove = action.StartsWith('-');
        bool seenFlag = args[2].Value.Contains("\\Seen", StringComparison.OrdinalIgnoreCase);

        foreach (ImapMessageHandle handle in handles)
        {
            // Only \Seen is actionable; other flags are accepted-but-ignored (no permanent store).
            // -FLAGS (\Seen) can't un-read a personal in the current schema, so we leave it as-is and
            // simply report the current flags; +FLAGS/FLAGS (\Seen) marks read.
            if (seenFlag && (add || (!add && !remove)) && !_readOnly)
            {
                _mailbox.MarkSeen(handle);
            }

            if (!silent)
            {
                string uidSuffix = byUid ? $" UID {handle.Uid}" : string.Empty;
                await _connection.WriteAsync(
                    $"* {handle.Sequence} FETCH (FLAGS ({(handle.Seen ? "\\Seen" : string.Empty)}){uidSuffix})\r\n",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await Tagged(tag, "OK", $"{(byUid ? "UID STORE" : "STORE")} completed", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves a sequence-set (message-seq or UID) to the snapshot handles it selects, in order.</summary>
    private bool TryResolveHandles(string set, bool byUid, out IReadOnlyList<ImapMessageHandle> handles)
    {
        handles = [];
        ImapMailbox mailbox = _mailbox!;
        long star = byUid ? mailbox.MaxUid : mailbox.MaxSequence;
        if (!ImapSequenceSet.TryParse(set, star, out IReadOnlyList<long> values))
        {
            return false;
        }

        var result = new List<ImapMessageHandle>();
        foreach (long value in values)
        {
            ImapMessageHandle? handle = byUid ? mailbox.ByUid(value) : mailbox.BySequence(value);
            if (handle is not null)
            {
                result.Add(handle);
            }
        }

        handles = result;
        return true;
    }

    /// <summary>
    /// Parses a FETCH item argument into the flat list of items, expanding the macros <c>ALL</c>,
    /// <c>FAST</c> and <c>FULL</c> (RFC 3501 §6.4.5) and splitting a parenthesised list. Returns false
    /// for an empty list.
    /// </summary>
    private static bool TryParseFetchItems(ImapToken token, out IReadOnlyList<string> items)
    {
        items = [];
        string raw = token.Kind == ImapTokenKind.List ? token.Value.Trim('(', ')') : token.Value;
        string single = raw.Trim().ToUpperInvariant();

        switch (single)
        {
            case "ALL":
                items = ["FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE"];
                return true;
            case "FAST":
                items = ["FLAGS", "INTERNALDATE", "RFC822.SIZE"];
                return true;
            case "FULL":
                items = ["FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE", "BODY"];
                return true;
        }

        var list = SplitFetchItems(raw);
        if (list.Count == 0)
        {
            return false;
        }

        items = list;
        return true;
    }

    /// <summary>
    /// Splits a FETCH item list on spaces, but keeps a <c>BODY[...]</c>/<c>BODY.PEEK[...]</c> section
    /// (which can contain spaces inside the brackets, e.g. <c>BODY[HEADER.FIELDS (DATE FROM)]</c>) as
    /// one item by balancing brackets.
    /// </summary>
    private static List<string> SplitFetchItems(string raw)
    {
        var items = new List<string>();
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == ' ')
            {
                i++;
                continue;
            }

            int start = i;
            int bracket = 0;
            while (i < raw.Length && (raw[i] != ' ' || bracket > 0))
            {
                if (raw[i] == '[')
                {
                    bracket++;
                }
                else if (raw[i] == ']')
                {
                    bracket--;
                }

                i++;
            }

            items.Add(raw[start..i]);
        }

        return items;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// If a mailbox is selected and new mail has arrived since the snapshot, append it and send an
    /// untagged <c>* n EXISTS</c> so a polling client (iPhone Mail NOOPing on a held connection) learns
    /// of it and fetches the new messages. No-op when no mailbox is selected or nothing is new.
    /// </summary>
    private async Task ReportNewMailAsync(CancellationToken cancellationToken)
    {
        if (_mailbox is not null && _mailbox.CheckForNewMessages() > 0)
        {
            await _connection.WriteAsync($"* {_mailbox.Count} EXISTS\r\n", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>IDLE</c> (RFC 2177): the client parks on the selected mailbox and the server pushes untagged
    /// updates until the client sends <c>DONE</c>. We answer the continuation (<c>+ idling</c>), then a
    /// single pending read waits for <c>DONE</c> while a poll loop re-checks the folder every
    /// <see cref="_idlePollInterval"/> and emits a fresh <c>* n EXISTS</c> whenever mail has arrived
    /// (the same new-mail check as NOOP). This is what lets iPhone Mail receive new packet mail live
    /// rather than on its own slow fetch schedule. One reader + one writer on a duplex stream is safe,
    /// and <c>DONE</c> carries no literal so the reader never writes a continuation. A
    /// <see cref="MaxIdleDuration"/> ceiling bounds a half-open mobile connection that never sends DONE.
    /// </summary>
    private async Task HandleIdleAsync(string tag, CancellationToken cancellationToken)
    {
        if (_mailbox is null)
        {
            await Tagged(tag, "BAD", "No mailbox selected", cancellationToken).ConfigureAwait(false);
            return;
        }

        await _connection.WriteAsync("+ idling\r\n", cancellationToken).ConfigureAwait(false);

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<string?> readTask = _connection.ReadCommandAsync(idleCts.Token);

        long maxTicks = Math.Max(1, (long)(MaxIdleDuration / _idlePollInterval));
        long ticks = 0;
        bool timedOut = false;
        try
        {
            while (true)
            {
                Task tick = Task.Delay(_idlePollInterval, idleCts.Token);
                Task finished = await Task.WhenAny(readTask, tick).ConfigureAwait(false);
                if (finished == readTask)
                {
                    break; // the client sent a line (DONE) or closed the connection
                }

                await ReportNewMailAsync(cancellationToken).ConfigureAwait(false);
                if (++ticks >= maxTicks)
                {
                    timedOut = true;
                    break;
                }
            }
        }
        finally
        {
            idleCts.Cancel(); // unblock the pending read and any in-flight delay
        }

        // Observe the read exactly once (it completed with DONE, or was cancelled by the lines above).
        string? line;
        try
        {
            line = await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            line = null;
        }

        if (timedOut)
        {
            await _connection.WriteAsync("* BYE Idle timeout\r\n", cancellationToken).ConfigureAwait(false);
            await Tagged(tag, "OK", "IDLE terminated", cancellationToken).ConfigureAwait(false);
            _loggedOut = true;
            return;
        }

        if (line is null)
        {
            _loggedOut = true; // the client closed the connection during IDLE
            return;
        }

        // RFC 2177: the only valid continuation is DONE; we end IDLE on any line and report OK.
        await Tagged(tag, "OK", "IDLE completed", cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireSelected(string tag, string okText, CancellationToken cancellationToken)
    {
        if (_mailbox is null)
        {
            await Tagged(tag, "BAD", "No mailbox selected", cancellationToken).ConfigureAwait(false);
            return;
        }

        await Tagged(tag, "OK", okText, cancellationToken).ConfigureAwait(false);
    }

    private Task Tagged(string tag, string status, string text, CancellationToken cancellationToken)
        => _connection.WriteAsync($"{tag} {status} {text}\r\n", cancellationToken);

    // Auth logging — operational/audit. The password is NEVER logged; only its length, which is enough
    // to tell e.g. a fat-fingered passphrase or an iOS-AutoFilled "strong password" from the real one.
    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP auth ok: {Callsign} via {Mechanism}")]
    private static partial void LogAuthOk(ILogger logger, string mechanism, string callsign);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IMAP auth FAILED via {Mechanism}: username '{User}' (password length {PasswordLength}) — no matching callsign+mail-password")]
    private static partial void LogAuthFailed(ILogger logger, string mechanism, string user, int passwordLength);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IMAP auth: malformed {Mechanism} response")]
    private static partial void LogAuthMalformed(ILogger logger, string mechanism);

    /// <summary>
    /// IMAP mailbox-name quoting: a name with no special chars may go bare, but a quoted-string is
    /// always safe — our names (<c>INBOX</c>, <c>Bulletins/NEWS</c>) are clean, so we always quote for
    /// simplicity and a strict client accepts it.
    /// </summary>
    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    /// <summary>
    /// Matches a folder name against an IMAP LIST pattern (RFC 3501 §6.3.8): <c>*</c> matches any
    /// sequence including the hierarchy delimiter, <c>%</c> matches any sequence not crossing it. A
    /// minimal glob is enough — clients send <c>*</c>, <c>%</c>, or a literal prefix.
    /// </summary>
    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern == "*" || pattern.Length == 0)
        {
            return true;
        }

        return MatchGlob(name, 0, pattern, 0);
    }

    private static bool MatchGlob(string name, int ni, string pattern, int pi)
    {
        while (pi < pattern.Length)
        {
            char p = pattern[pi];
            if (p == '*')
            {
                for (int k = ni; k <= name.Length; k++)
                {
                    if (MatchGlob(name, k, pattern, pi + 1))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (p == '%')
            {
                for (int k = ni; k <= name.Length; k++)
                {
                    if (k > ni && name[k - 1] == ImapBackend.HierarchyDelimiter)
                    {
                        break; // '%' does not cross the hierarchy delimiter
                    }

                    if (MatchGlob(name, k, pattern, pi + 1))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (ni >= name.Length || char.ToUpperInvariant(name[ni]) != char.ToUpperInvariant(p))
            {
                return false;
            }

            ni++;
            pi++;
        }

        return ni == name.Length;
    }
}
