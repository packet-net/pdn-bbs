using Bbs.Core;

namespace Bbs.Imap;

/// <summary>
/// A session's stable snapshot of one selected folder: the ascending list of
/// <see cref="ImapMessageHandle"/> (sequence number → UID → message + flags) frozen at
/// <see cref="ImapBackend.OpenMailbox"/> time, plus the folder identity and the live counts the
/// protocol engine reports. The sequence-number space does not change for the life of the snapshot
/// (RFC 3501 §2.3.1.2); a <c>\Seen</c> transition is applied in place to the handle so a re-FETCH in
/// the same session reflects it, but no message is added or removed (this slice never expunges).
/// </summary>
public sealed class ImapMailbox
{
    private readonly List<ImapMessageHandle> _handles;
    private readonly BbsStore _store;

    /// <summary>Creates the snapshot. <paramref name="handles"/> are in ascending sequence-number order.</summary>
    internal ImapMailbox(
        ImapFolder folder, string callsign, IReadOnlyList<ImapMessageHandle> handles, uint uidNext, BbsStore store)
    {
        Folder = folder;
        Callsign = callsign;
        _handles = [.. handles];
        UidNext = uidNext;
        _store = store;
    }

    /// <summary>The folder this snapshot is of.</summary>
    public ImapFolder Folder { get; }

    /// <summary>The session's callsign (the IMAP user — the read-mark actor for <c>\Seen</c>).</summary>
    public string Callsign { get; }

    /// <summary>The messages in this snapshot, in ascending sequence-number order.</summary>
    public IReadOnlyList<ImapMessageHandle> Messages => _handles;

    /// <summary>The number of messages — the <c>* n EXISTS</c> count (RFC 3501 §7.3.1).</summary>
    public int Count => _handles.Count;

    /// <summary>UIDNEXT — the next UID the store will assign (RFC 3501 §2.3.1.1).</summary>
    public uint UidNext { get; }

    /// <summary>The constant UIDVALIDITY (the UID is the never-reused message number).</summary>
    public uint UidValidity { get; } = ImapBackend.UidValidity;

    /// <summary>The count of unseen messages — the <c>UNSEEN</c> STATUS item / the <c>* OK [UNSEEN k]</c> hint.</summary>
    public int UnseenCount => _handles.Count(h => !h.Seen);

    /// <summary>
    /// The 1-based sequence number of the first unseen message, or null when all are seen — the
    /// <c>* OK [UNSEEN n]</c> response code on SELECT (RFC 3501 §7.1, where <c>n</c> is a message
    /// sequence number, not a count).
    /// </summary>
    public int? FirstUnseenSequence
    {
        get
        {
            foreach (ImapMessageHandle handle in _handles)
            {
                if (!handle.Seen)
                {
                    return handle.Sequence;
                }
            }

            return null;
        }
    }

    /// <summary>The largest sequence number in use (<c>*</c> for a message-sequence set), or 0 when empty.</summary>
    public long MaxSequence => _handles.Count;

    /// <summary>The largest UID in use (<c>*</c> for a UID set), or 0 when empty.</summary>
    public long MaxUid => _handles.Count == 0 ? 0 : _handles[^1].Uid;

    /// <summary>The handle at a 1-based message sequence number, or null when out of range.</summary>
    public ImapMessageHandle? BySequence(long sequence)
        => sequence >= 1 && sequence <= _handles.Count ? _handles[(int)(sequence - 1)] : null;

    /// <summary>The handle for a UID, or null when no message in the snapshot has that UID.</summary>
    public ImapMessageHandle? ByUid(long uid)
    {
        foreach (ImapMessageHandle handle in _handles)
        {
            if (handle.Uid == uid)
            {
                return handle;
            }
        }

        return null;
    }

    /// <summary>
    /// Marks <paramref name="handle"/> seen in the store and updates the snapshot flag in place
    /// (a non-PEEK body fetch, or a <c>STORE +FLAGS (\Seen)</c>). Personals stamp the recipient
    /// read-row via <see cref="BbsStore.MarkRead"/>; bulletins record per-user read-state in the
    /// <c>message_read</c> table via <see cref="BbsStore.SetReadByUser"/> (the reader is not a named
    /// recipient). Returns true when the flag changed from unseen to seen in this snapshot.
    /// </summary>
    public bool MarkSeen(ImapMessageHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (Folder.Kind == ImapFolderKind.Inbox)
        {
            _store.MarkRead(handle.Uid, Callsign);
        }
        else
        {
            _store.SetReadByUser(Callsign, handle.Uid);
        }

        if (handle.Seen)
        {
            return false;
        }

        handle.Seen = true;
        return true;
    }
}

/// <summary>
/// One message inside a mailbox snapshot: its 1-based <see cref="Sequence"/> number, its
/// <see cref="Uid"/> (the store's <see cref="Message.Number"/>), the mutable <see cref="Seen"/> flag,
/// and the underlying <see cref="Message"/>. The rendered MIME form is built once on first FETCH and
/// cached, so repeated <c>BODY[]</c>/<c>RFC822.SIZE</c> reads serve substrings of the same bytes.
/// </summary>
public sealed class ImapMessageHandle
{
    private ImapRenderedMessage? _rendered;

    /// <summary>Creates a handle.</summary>
    internal ImapMessageHandle(int sequence, long uid, bool seen, Message message)
    {
        Sequence = sequence;
        Uid = uid;
        Seen = seen;
        Message = message;
    }

    /// <summary>The 1-based message sequence number within the snapshot.</summary>
    public int Sequence { get; }

    /// <summary>The UID — the store's stable, never-reused <see cref="Message.Number"/>.</summary>
    public long Uid { get; }

    /// <summary>The <c>\Seen</c> flag; mutated in place by <see cref="ImapMailbox.MarkSeen"/>.</summary>
    public bool Seen { get; internal set; }

    /// <summary>The stored message.</summary>
    public Message Message { get; }

    /// <summary>
    /// The rendered MIME form (built once, cached): the serialized RFC 822 bytes plus the parsed
    /// header/body offsets the FETCH item formatters serve from.
    /// </summary>
    public ImapRenderedMessage Rendered => _rendered ??= ImapRenderedMessage.Render(Message);
}
