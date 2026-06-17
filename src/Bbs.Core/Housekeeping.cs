namespace Bbs.Core;

/// <summary>
/// Lifetimes for the housekeeping run (compat spec §6): kill-by-age per type+state —
/// "Personals Read/Unread/Forwarded/Unforwarded, Bulls Forwarded/Unforwarded, NTS
/// Delivered/Forwarded/Unforwarded" — BID Lifetime (default 60), the grace before killed
/// messages are physically removed (default 0 = at the next run, matching LinBPQ's "first
/// physically remove K-status messages"), and the message-number ceiling
/// (<see cref="MaxMsgno"/>) that triggers a compacting renumber.
/// All values in days; a message is killed when strictly older than its lifetime.
///
/// <para><b>Per-class lifetime defaults (issue #39 / forwarding.md "Housekeeping lifetime
/// defaults"):</b> bulletins default to <b>7 days</b> (the GB7RDG convention — transient
/// broadcast traffic that should not inflate the store for a month); personals and NTS
/// traffic default to <b>30 days</b>. Every value is config-overridable per node through the
/// <c>housekeeping:</c> block in <c>bbs.yaml</c> (no code change), so an operator who wants
/// the old uniform-30 behaviour just sets the bulletin keys to 30. Held (H) messages are
/// exempt from every lifetime (they sit in the sysop's queue, §2.2) and a hold flag therefore
/// already protects a deliberately-kept message from the per-class defaults.</para>
/// </summary>
public sealed record HousekeepingPolicy
{
    /// <summary>The conventional lifetime for personal mail + NTS traffic, days.</summary>
    public const int DefaultPersonalDays = 30;

    /// <summary>
    /// The conventional lifetime for bulletins, days — markedly shorter than personals because a
    /// bulletin is transient broadcast traffic (the GB7RDG convention ~7 days; issue #39).
    /// </summary>
    public const int DefaultBulletinDays = 7;

    /// <summary>Lifetime of read (Y) personals, days.</summary>
    public int PersonalReadDays { get; init; } = DefaultPersonalDays;

    /// <summary>Lifetime of unread (N, no forwarding pending) personals, days.</summary>
    public int PersonalUnreadDays { get; init; } = DefaultPersonalDays;

    /// <summary>Lifetime of forwarded (F) personals, days.</summary>
    public int PersonalForwardedDays { get; init; } = DefaultPersonalDays;

    /// <summary>Lifetime of unforwarded personals (N with forwarding still pending), days.</summary>
    public int PersonalUnforwardedDays { get; init; } = DefaultPersonalDays;

    /// <summary>Lifetime of forwarded (F) bulletins, days. Defaults to <see cref="DefaultBulletinDays"/> (~a week).</summary>
    public int BulletinForwardedDays { get; init; } = DefaultBulletinDays;

    /// <summary>Lifetime of unforwarded bulletins (N, Y or $), days. Defaults to <see cref="DefaultBulletinDays"/> (~a week).</summary>
    public int BulletinUnforwardedDays { get; init; } = DefaultBulletinDays;

    /// <summary>Lifetime of delivered (D) NTS messages, days.</summary>
    public int NtsDeliveredDays { get; init; } = DefaultPersonalDays;

    /// <summary>Lifetime of forwarded (F) NTS messages, days.</summary>
    public int NtsForwardedDays { get; init; } = DefaultPersonalDays;

    /// <summary>Lifetime of unforwarded (N or Y) NTS messages, days.</summary>
    public int NtsUnforwardedDays { get; init; } = DefaultPersonalDays;

    /// <summary>BID dedup-record lifetime, days (compat spec §2.3/§6 "BID Lifetime default 60").</summary>
    public int BidLifetimeDays { get; init; } = 60;

    /// <summary>
    /// The conventional lifetime for White Pages directory entries, days (issue #36). WP entries go
    /// stale as stations move/retire, but a station that is active re-freshens its entry each
    /// forwarding cycle, so the default is long enough to survive seasonal gaps yet short enough to
    /// drop dead stations (BPQ re-announces only records changed within the last few days). A WP entry
    /// whose <c>record_date</c> is strictly older than this is pruned by the sweep.
    /// </summary>
    public const int DefaultWhitePagesDays = 180;

    /// <summary>White Pages directory-entry lifetime, days (issue #36). Defaults to <see cref="DefaultWhitePagesDays"/>.</summary>
    public int WhitePagesDays { get; init; } = DefaultWhitePagesDays;

    /// <summary>
    /// Grace between a kill and physical deletion, days. 0 purges at the next run — LinBPQ's
    /// behaviour ("remains on disk until housekeeping removes it", compat spec §2.2/§6).
    /// </summary>
    public int KilledPurgeGraceDays { get; init; }

    /// <summary>
    /// The message-number ceiling (BPQ <c>MaxMsgno</c>, compat spec §6). <b>0 disables renumbering</b>
    /// — the default, so an upgraded node behaves exactly as before until the operator opts in. When
    /// positive, a housekeeping run whose highest live message number is at or above this value
    /// triggers a compacting renumber AFTER the kill/purge passes: the surviving messages are
    /// renumbered densely from 1 (oldest → newest) so the next allocated number is well below the
    /// ceiling again. The renumber preserves referential integrity — the network-wide BID
    /// (<c>&lt;msgno&gt;_&lt;BBSCALL&gt;</c>, a frozen network identity) is NEVER rewritten; only the
    /// local message <c>number</c> and every local row that references it are remapped atomically
    /// (recipients, forwards, attachments, per-user read state, 7plus parts/files, the BID back-link,
    /// and each user's last-listed marker). The ceiling interacts with kill-by-age by running second:
    /// kill-by-age + the K-purge shrink the store first, so renumbering only ever compacts what
    /// genuinely survives. The interval check uses <c>&gt;=</c> so a ceiling exactly at the current
    /// high-water mark still fires.
    /// </summary>
    public long MaxMsgno { get; init; }
}

/// <summary>Counts from one housekeeping run, for the Host's log.</summary>
/// <param name="KilledMessagesPurged">K messages physically deleted.</param>
/// <param name="MessagesKilledByAge">Messages moved to K by the age matrix.</param>
/// <param name="BidsPurged">BID dedup records dropped by lifetime.</param>
/// <param name="MessagesRenumbered">Live messages remapped to a new dense number by the MaxMsgno renumber pass (0 when the ceiling did not fire).</param>
/// <param name="WhitePagesPruned">White Pages directory entries dropped by the WP-lifetime sweep (issue #36).</param>
public sealed record HousekeepingSummary(int KilledMessagesPurged, int MessagesKilledByAge, int BidsPurged, int MessagesRenumbered = 0, int WhitePagesPruned = 0);

/// <summary>
/// The housekeeping pass (compat spec §6), invoked by the Host on a timer (daily at
/// maintenance time, and on demand for a DOHOUSEKEEPING equivalent). Order matches LinBPQ:
/// "first physically remove K-status messages, then kill expired ones" — so a message killed
/// by age this run survives on disk until a later run, exactly like LinBPQ. BID purge runs
/// last; BID records deliberately outlive their messages (§2.3 dedup-survives-kill).
///
/// Status→lifetime mapping judgments (the spec names categories, not statuses): H messages are
/// exempt (they sit in the sysop's queue per §2.2 and cannot expire silently); "unforwarded"
/// personals are N with forwarding still pending, "unread" are N without; bulletins map F vs
/// everything-else (N/Y/$). The MaxMsgno renumber (§6) runs LAST, after the store has been
/// shrunk by the kill/purge passes, so it only ever compacts genuinely-surviving messages.
/// Named deferrals: per-From/To/At overrides ("ALL, 10" style), non-delivery notifications.
/// </summary>
public static class Housekeeping
{
    private const long SecondsPerDay = 86_400;

    /// <summary>Runs one housekeeping pass against <paramref name="store"/> using its TimeProvider.</summary>
    public static HousekeepingSummary Run(BbsStore store, HousekeepingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);

        long now = store.NowSeconds();

        // 1. Physically remove killed messages past the grace.
        int purgedKilled = store.PurgeKilledMessages(now - (policy.KilledPurgeGraceDays * SecondsPerDay));

        // 2. Kill by age, per type+state.
        int killedByAge = 0;
        killedByAge += store.KillByAge('P', "F", Cutoff(now, policy.PersonalForwardedDays));
        killedByAge += store.KillByAge('P', "Y", Cutoff(now, policy.PersonalReadDays));
        killedByAge += store.KillByAge('P', "N", Cutoff(now, policy.PersonalUnforwardedDays), hasPendingForwards: true);
        killedByAge += store.KillByAge('P', "N", Cutoff(now, policy.PersonalUnreadDays), hasPendingForwards: false);
        killedByAge += store.KillByAge('B', "F", Cutoff(now, policy.BulletinForwardedDays));
        killedByAge += store.KillByAge('B', "NY$", Cutoff(now, policy.BulletinUnforwardedDays));
        killedByAge += store.KillByAge('T', "D", Cutoff(now, policy.NtsDeliveredDays));
        killedByAge += store.KillByAge('T', "F", Cutoff(now, policy.NtsForwardedDays));
        killedByAge += store.KillByAge('T', "NY", Cutoff(now, policy.NtsUnforwardedDays));

        // 3. BID lifetime purge.
        int purgedBids = store.PurgeExpiredBids(now - (policy.BidLifetimeDays * SecondsPerDay));

        // 4. MaxMsgno renumber. Runs last so it compacts only what survived 1–3. Fires when the
        // ceiling is enabled (>0) AND the current high-water mark has reached it (>=). Renumbering
        // densely from 1 keeps the next allocated number well below the ceiling; the BID (the network
        // identity) is untouched, so referential integrity to partners is preserved (issue #39).
        int renumbered = 0;
        if (policy.MaxMsgno > 0 && store.GetLatestMessageNumber() >= policy.MaxMsgno)
        {
            renumbered = store.RenumberMessages();
        }

        // 5. White Pages directory aging (issue #36). Prune entries we have not SEEN within the WP
        // lifetime — stations that have moved/retired and stopped re-announcing (the sweep keys on
        // last_seen_utc, not record_date, so a long-stable but still-active station is kept). Independent
        // of the mail store: it touches only the whitepages table.
        int whitePagesPruned = store.SweepWhitePages(
            DateTimeOffset.FromUnixTimeSeconds(Cutoff(now, policy.WhitePagesDays)));

        return new HousekeepingSummary(purgedKilled, killedByAge, purgedBids, renumbered, whitePagesPruned);
    }

    private static long Cutoff(long now, int days) => now - (days * SecondsPerDay);
}
