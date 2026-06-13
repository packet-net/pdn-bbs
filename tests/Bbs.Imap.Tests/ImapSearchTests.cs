using Bbs.Core;
using Bbs.Imap;

namespace Bbs.Imap.Tests;

/// <summary>
/// The <c>SEARCH</c> / <c>UID SEARCH</c> evaluator (<see cref="ImapSearch"/>) — what a real client
/// (iPhone Mail) uses to enumerate a mailbox. Drives the evaluator directly over a mailbox snapshot.
/// </summary>
public sealed class ImapSearchTests
{
    /// <summary>Tokenises a criteria string the way the command parser feeds the SEARCH handler.</summary>
    private static IReadOnlyList<ImapToken> Crit(string criteria)
    {
        Assert.True(ImapCommandParser.TryTokenize(criteria, out IReadOnlyList<ImapToken> tokens));
        return tokens;
    }

    private static (ImapBackend Backend, ImapMailbox Inbox, TestStore Store) Inbox()
    {
        var store = new TestStore();
        // Three personals to M0LTE; the middle one read.
        store.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "alpha"));
        Message mid = store.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "bravo report"));
        store.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "charlie"));
        store.Store.MarkRead(mid.Number, "M0LTE");

        var backend = new ImapBackend(store.Store);
        ImapMailbox inbox = backend.OpenMailbox("M0LTE", backend.ResolveFolder("M0LTE", "INBOX")!)!;
        return (backend, inbox, store);
    }

    [Fact]
    public void All_ReturnsEverySequence_AndUidVariantReturnsUids()
    {
        (_, ImapMailbox inbox, TestStore store) = Inbox();
        using (store)
        {
            Assert.True(ImapSearch.TryEvaluate(Crit("ALL"), inbox, byUid: false, out IReadOnlyList<long> seqs));
            Assert.Equal([1L, 2L, 3L], seqs);

            Assert.True(ImapSearch.TryEvaluate(Crit("ALL"), inbox, byUid: true, out IReadOnlyList<long> uids));
            Assert.Equal(inbox.Messages.Select(m => m.Uid).Order().ToArray(), uids);
        }
    }

    [Fact]
    public void Flags_SeenAndUnseen()
    {
        (_, ImapMailbox inbox, TestStore store) = Inbox();
        using (store)
        {
            Assert.True(ImapSearch.TryEvaluate(Crit("UNSEEN"), inbox, byUid: false, out IReadOnlyList<long> unseen));
            Assert.Equal([1L, 3L], unseen); // the middle one was read

            Assert.True(ImapSearch.TryEvaluate(Crit("SEEN"), inbox, byUid: false, out IReadOnlyList<long> seen));
            Assert.Equal([2L], seen);
        }
    }

    [Fact]
    public void SequenceSet_AndUidSet()
    {
        (_, ImapMailbox inbox, TestStore store) = Inbox();
        using (store)
        {
            Assert.True(ImapSearch.TryEvaluate(Crit("2:3"), inbox, byUid: false, out IReadOnlyList<long> set));
            Assert.Equal([2L, 3L], set);

            long firstUid = inbox.Messages[0].Uid;
            Assert.True(ImapSearch.TryEvaluate(Crit($"UID {firstUid}"), inbox, byUid: true, out IReadOnlyList<long> byUid));
            Assert.Equal([firstUid], byUid);
        }
    }

    [Fact]
    public void Subject_Substring_CaseInsensitive()
    {
        (_, ImapMailbox inbox, TestStore store) = Inbox();
        using (store)
        {
            Assert.True(ImapSearch.TryEvaluate(Crit("SUBJECT report"), inbox, byUid: false, out IReadOnlyList<long> hit));
            Assert.Equal([2L], hit); // "bravo report"
        }
    }

    [Fact]
    public void Date_Since_And_Before()
    {
        (_, ImapMailbox inbox, TestStore store) = Inbox();
        using (store)
        {
            // The test clock is 2026-06-11, so everything is SINCE 2020 and nothing is BEFORE 2020.
            Assert.True(ImapSearch.TryEvaluate(Crit("SINCE 1-Jan-2020"), inbox, byUid: false, out IReadOnlyList<long> since));
            Assert.Equal([1L, 2L, 3L], since);

            Assert.True(ImapSearch.TryEvaluate(Crit("BEFORE 1-Jan-2020"), inbox, byUid: false, out IReadOnlyList<long> before));
            Assert.Empty(before);
        }
    }

    [Fact]
    public void Not_And_Or_Combinators()
    {
        (_, ImapMailbox inbox, TestStore store) = Inbox();
        using (store)
        {
            Assert.True(ImapSearch.TryEvaluate(Crit("NOT SEEN"), inbox, byUid: false, out IReadOnlyList<long> notSeen));
            Assert.Equal([1L, 3L], notSeen);

            // OR SEEN SUBJECT alpha → the read one (2) plus "alpha" (1).
            Assert.True(ImapSearch.TryEvaluate(Crit("OR SEEN SUBJECT alpha"), inbox, byUid: false, out IReadOnlyList<long> either));
            Assert.Equal([1L, 2L], either);
        }
    }

    [Fact]
    public void ImpliedAnd_OfTwoKeys()
    {
        (_, ImapMailbox inbox, TestStore store) = Inbox();
        using (store)
        {
            // UNSEEN SUBJECT charlie → only message 3.
            Assert.True(ImapSearch.TryEvaluate(Crit("UNSEEN SUBJECT charlie"), inbox, byUid: false, out IReadOnlyList<long> both));
            Assert.Equal([3L], both);
        }
    }

    [Fact]
    public void Charset_Prefix_IsAccepted()
    {
        (_, ImapMailbox inbox, TestStore store) = Inbox();
        using (store)
        {
            Assert.True(ImapSearch.TryEvaluate(Crit("CHARSET UTF-8 ALL"), inbox, byUid: false, out IReadOnlyList<long> all));
            Assert.Equal([1L, 2L, 3L], all);
        }
    }

    [Fact]
    public void Malformed_ReturnsFalse()
    {
        (_, ImapMailbox inbox, TestStore store) = Inbox();
        using (store)
        {
            Assert.False(ImapSearch.TryEvaluate(Crit(""), inbox, byUid: false, out _));          // empty program
            Assert.False(ImapSearch.TryEvaluate(Crit("LARGER notanumber"), inbox, byUid: false, out _));
            Assert.False(ImapSearch.TryEvaluate(Crit("SINCE not-a-date"), inbox, byUid: false, out _));
            Assert.False(ImapSearch.TryEvaluate(Crit("0"), inbox, byUid: false, out _));           // 0 is not a valid seq
        }
    }
}
