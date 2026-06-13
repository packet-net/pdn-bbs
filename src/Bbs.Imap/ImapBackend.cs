using Bbs.Core;

namespace Bbs.Imap;

/// <summary>
/// The store-facing backend the IMAP protocol engine consumes: it authenticates a login against the
/// BBS mail-password, enumerates the folders a logged-in callsign can see, and opens a stable
/// <see cref="ImapMailbox"/> snapshot of one folder for a session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Folder model.</b> Packet mail maps onto two kinds of folder:
/// <list type="bullet">
/// <item><b>INBOX</b> — the personals (<see cref="MessageType.Personal"/>) addressed to the logged-in
///   callsign. Per-message <c>\Seen</c> is the user's own recipient-row read-state, which the store
///   tracks (<see cref="BbsStore.MarkRead"/>).</item>
/// <item><b>Bulletins/&lt;CATEGORY&gt;</b> — the bulletins (<see cref="MessageType.Bulletin"/>) whose
///   recipient category is <c>CATEGORY</c> (e.g. <c>ALL</c>, <c>NEWS</c>, <c>SALE</c>). A bulletin's
///   "recipient" is the category, not the user, so its per-user <c>\Seen</c> is tracked in the
///   <c>message_read</c> table (<see cref="BbsStore.IsReadByUser"/>/<see cref="BbsStore.SetReadByUser"/>)
///   keyed by the reader's callsign — each user has their own unread bulletins.</item>
/// </list>
/// Raw 7plus part-bulletins are hidden everywhere (matching webmail), as the user only ever sees the
/// assembled file.
/// </para>
/// <para>
/// <b>UID model.</b> The IMAP UID is the store's monotonic <see cref="Message.Number"/> — globally
/// stable and never reused — so <see cref="UidValidity"/> is a constant <c>1</c> for every folder and
/// a client's cached UIDs stay valid across sessions and restarts.
/// </para>
/// </remarks>
public sealed class ImapBackend
{
    /// <summary>The hierarchy delimiter between a folder's path components (RFC 3501 §5.1.1).</summary>
    public const char HierarchyDelimiter = '/';

    /// <summary>The synthetic mail domain every packet address is rendered under (see <see cref="BbsMessageToMime"/>).</summary>
    public const string MailDomain = "pdn";

    /// <summary>The parent folder under which each bulletin category hangs (a <c>\Noselect</c> container).</summary>
    public const string BulletinsRoot = "Bulletins";

    /// <summary>
    /// The single constant UIDVALIDITY for every folder. The UID is the store's global, never-reused
    /// <see cref="Message.Number"/>, so the UID space is permanently stable and a client never has to
    /// resynchronise (RFC 3501 §2.3.1.1: a stable UID space ⇒ a constant UIDVALIDITY).
    /// </summary>
    public const uint UidValidity = 1;

    private readonly BbsStore _store;

    /// <summary>Creates the backend over the BBS <paramref name="store"/>.</summary>
    public ImapBackend(BbsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Verifies an IMAP login: the <paramref name="callsign"/> (the IMAP user) plus its BBS
    /// mail-password (<see cref="BbsStore.VerifyMailPassword"/> — fixed-time, false for an unknown or
    /// password-less callsign). Returns the normalised base callsign the session operates as on success,
    /// or null on failure.
    /// </summary>
    public string? Authenticate(string callsign, string password)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(password);

        if (!_store.VerifyMailPassword(callsign, password))
        {
            return null;
        }

        return Callsigns.StripSsid(Callsigns.Normalize(callsign));
    }

    /// <summary>
    /// The folders <paramref name="callsign"/> can see: INBOX, the <c>Bulletins</c> parent
    /// (a <c>\Noselect</c> container), and one <c>Bulletins/&lt;category&gt;</c> for each bulletin
    /// category currently present in the store. Categories are discovered by listing all (non-7plus)
    /// bulletins and collecting their distinct recipient categories, ordered case-insensitively for a
    /// stable enumeration.
    /// </summary>
    public IReadOnlyList<ImapFolder> ListFolders(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);

        var folders = new List<ImapFolder>
        {
            new(ImapFolderKind.Inbox, "INBOX", Category: null, Selectable: true),
            new(ImapFolderKind.BulletinsRoot, BulletinsRoot, Category: null, Selectable: false),
        };

        foreach (string category in BulletinCategories())
        {
            folders.Add(new ImapFolder(
                ImapFolderKind.BulletinCategory,
                $"{BulletinsRoot}{HierarchyDelimiter}{category}",
                category,
                Selectable: true));
        }

        return folders;
    }

    /// <summary>
    /// Resolves a mailbox name (as a client sends it on <c>SELECT</c>/<c>STATUS</c>) to the folder it
    /// names, or null when no such folder exists. <c>INBOX</c> is matched case-insensitively (RFC 3501
    /// §5.1: "INBOX" is case-insensitive); a <c>Bulletins/&lt;cat&gt;</c> name resolves when that
    /// category is present, with the category compared case-insensitively.
    /// </summary>
    public ImapFolder? ResolveFolder(string callsign, string mailboxName)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(mailboxName);

        if (string.Equals(mailboxName, "INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return new ImapFolder(ImapFolderKind.Inbox, "INBOX", Category: null, Selectable: true);
        }

        if (string.Equals(mailboxName, BulletinsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new ImapFolder(ImapFolderKind.BulletinsRoot, BulletinsRoot, Category: null, Selectable: false);
        }

        string prefix = BulletinsRoot + HierarchyDelimiter;
        if (mailboxName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string requested = mailboxName[prefix.Length..];
            foreach (string category in BulletinCategories())
            {
                if (string.Equals(category, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return new ImapFolder(
                        ImapFolderKind.BulletinCategory,
                        $"{BulletinsRoot}{HierarchyDelimiter}{category}",
                        category,
                        Selectable: true);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Opens a stable snapshot of <paramref name="folder"/> for the session <paramref name="callsign"/>:
    /// the ascending list of (sequence number → UID → message) with per-message flags, fixed at this
    /// moment. The IMAP sequence-number space is frozen here so it does not shift under the session as
    /// new mail arrives (RFC 3501 §2.3.1.2 — sequence numbers are SELECT-stable). Returns null for a
    /// non-selectable folder (the <c>Bulletins</c> container).
    /// </summary>
    public ImapMailbox? OpenMailbox(string callsign, ImapFolder folder)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(folder);

        if (!folder.Selectable)
        {
            return null;
        }

        IReadOnlySet<long> sevenPlusParts = _store.GetSevenPlusPartMessageNumbers();
        IReadOnlyList<Message> messages = folder.Kind switch
        {
            ImapFolderKind.Inbox => _store.ListMessages(new MessageQuery
            {
                Type = MessageType.Personal,
                ToCall = callsign,
                OldestFirst = true,
            }),
            ImapFolderKind.BulletinCategory => _store.ListMessages(new MessageQuery
            {
                Type = MessageType.Bulletin,
                ToCall = folder.Category,
                OldestFirst = true,
            }),
            _ => [],
        };

        var handles = new List<ImapMessageHandle>(messages.Count);
        int seq = 0;
        foreach (Message message in messages)
        {
            if (sevenPlusParts.Contains(message.Number))
            {
                continue; // raw 7plus part-bulletins are hidden (webmail parity)
            }

            seq++;
            bool seen = IsSeen(folder, message, callsign);
            handles.Add(new ImapMessageHandle(seq, message.Number, seen, message));
        }

        // UIDNEXT is the next number the store will assign — stable for the session (RFC 3501 §2.3.1.1).
        long uidNext = _store.GetLatestMessageNumber() + 1;
        return new ImapMailbox(folder, callsign, handles, (uint)uidNext, _store);
    }

    /// <summary>
    /// The <c>\Seen</c> flag for one message in one folder, per the session callsign. Personals carry
    /// the user's own recipient-row read-state (<see cref="MessageRecipient.ReadAt"/>); bulletins carry
    /// per-user read-state in the <c>message_read</c> table (<see cref="BbsStore.IsReadByUser"/>) — the
    /// reader is not a named recipient of a bulletin, so its read-state can't live on a recipient row.
    /// </summary>
    private bool IsSeen(ImapFolder folder, Message message, string callsign)
    {
        if (folder.Kind != ImapFolderKind.Inbox)
        {
            return _store.IsReadByUser(callsign, message.Number); // bulletins: real per-user read-state
        }

        foreach (MessageRecipient recipient in message.Recipients)
        {
            if (Callsigns.BaseEquals(recipient.ToCall, callsign))
            {
                return recipient.ReadAt is not null;
            }
        }

        return false;
    }

    /// <summary>
    /// The distinct bulletin categories present in the store (the recipient categories of every
    /// non-7plus bulletin), ordered case-insensitively. A bulletin may have several recipient
    /// categories; each becomes its own <c>Bulletins/&lt;cat&gt;</c> folder.
    /// </summary>
    private IReadOnlyList<string> BulletinCategories()
    {
        IReadOnlySet<long> sevenPlusParts = _store.GetSevenPlusPartMessageNumbers();
        var categories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Message bulletin in _store.ListMessages(new MessageQuery { Type = MessageType.Bulletin }))
        {
            if (sevenPlusParts.Contains(bulletin.Number))
            {
                continue;
            }

            foreach (MessageRecipient recipient in bulletin.Recipients)
            {
                if (!string.IsNullOrWhiteSpace(recipient.ToCall))
                {
                    categories.Add(recipient.ToCall);
                }
            }
        }

        return [.. categories];
    }
}

/// <summary>The kind of an IMAP folder in the packet-mail folder model.</summary>
public enum ImapFolderKind
{
    /// <summary>INBOX — personals addressed to the logged-in callsign.</summary>
    Inbox,

    /// <summary>The <c>Bulletins</c> parent — a <c>\Noselect</c> container, not itself a mailbox.</summary>
    BulletinsRoot,

    /// <summary>A <c>Bulletins/&lt;category&gt;</c> leaf — bulletins of one recipient category.</summary>
    BulletinCategory,
}

/// <summary>
/// One folder in the packet-mail folder model: its IMAP <see cref="Name"/> (full path, e.g.
/// <c>INBOX</c> or <c>Bulletins/NEWS</c>), its <see cref="Kind"/>, the bulletin <see cref="Category"/>
/// (for a <see cref="ImapFolderKind.BulletinCategory"/>, else null), and whether it is
/// <see cref="Selectable"/> (the <c>Bulletins</c> container is <c>\Noselect</c>).
/// </summary>
/// <param name="Kind">The folder kind.</param>
/// <param name="Name">The full IMAP mailbox name.</param>
/// <param name="Category">The bulletin recipient category, or null for INBOX / the container.</param>
/// <param name="Selectable">False for the <c>\Noselect</c> <c>Bulletins</c> container.</param>
public sealed record ImapFolder(ImapFolderKind Kind, string Name, string? Category, bool Selectable);
