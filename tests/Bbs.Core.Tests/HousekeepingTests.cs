namespace Bbs.Core.Tests;

/// <summary>Housekeeping (compat spec §6): K-purge order/grace, the kill-by-age matrix, BID lifetime.</summary>
public sealed class HousekeepingTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    private static readonly HousekeepingPolicy Defaults = new();

    [Fact]
    public void KilledMessages_PurgedAtNextRun_ByDefault()
    {
        // §6: "first physically remove K-status messages" — grace 0 = next run, like LinBPQ.
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.Kill(m.Number);

        HousekeepingSummary summary = Housekeeping.Run(_ts.Store, Defaults);

        Assert.Equal(1, summary.KilledMessagesPurged);
        Assert.Null(_ts.Store.GetMessage(m.Number));
    }

    [Fact]
    public void KilledMessages_RespectPurgeGrace()
    {
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.Kill(m.Number);

        var policy = new HousekeepingPolicy { KilledPurgeGraceDays = 7 };

        Assert.Equal(0, Housekeeping.Run(_ts.Store, policy).KilledMessagesPurged);
        Assert.NotNull(_ts.Store.GetMessage(m.Number)); // still on disk, status K

        _ts.Time.AdvanceDays(8);
        Assert.Equal(1, Housekeeping.Run(_ts.Store, policy).KilledMessagesPurged);
        Assert.Null(_ts.Store.GetMessage(m.Number));
    }

    [Fact]
    public void KillByAge_RunsAfterPurge_SoAgedMessagesSurviveOneRunOnDisk()
    {
        // LinBPQ order: purge K first, then kill expired — a message killed by age this run is
        // physically removed at a later run (§6, §2.2 "remains on disk until housekeeping
        // removes it").
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Time.AdvanceDays(31);

        HousekeepingSummary firstRun = Housekeeping.Run(_ts.Store, Defaults);
        Assert.Equal(1, firstRun.MessagesKilledByAge);
        Assert.Equal(0, firstRun.KilledMessagesPurged);
        Assert.Equal(MessageStatus.Killed, _ts.Store.GetMessage(m.Number)!.Status);

        HousekeepingSummary secondRun = Housekeeping.Run(_ts.Store, Defaults);
        Assert.Equal(1, secondRun.KilledMessagesPurged);
        Assert.Null(_ts.Store.GetMessage(m.Number));
    }

    [Fact]
    public void KillByAge_NotBeforeLifetimeElapses()
    {
        _ts.Store.AddMessage(Drafts.Personal());
        _ts.Time.AdvanceDays(30); // exactly the lifetime: "strictly older" required

        Assert.Equal(0, Housekeeping.Run(_ts.Store, Defaults).MessagesKilledByAge);
    }

    public static TheoryData<MessageType, string, int> AgeMatrix()
    {
        // type, scenario, lifetime-days-field exercised (all defaults 30; we vary per case to
        // prove each category keys off its own knob).
        return new TheoryData<MessageType, string, int>
        {
            { MessageType.Personal, "read", 10 },
            { MessageType.Personal, "unread", 11 },
            { MessageType.Personal, "forwarded", 12 },
            { MessageType.Personal, "unforwarded", 13 },
            { MessageType.Bulletin, "forwarded", 14 },
            { MessageType.Bulletin, "unforwarded", 15 },
            { MessageType.Traffic, "delivered", 16 },
            { MessageType.Traffic, "forwarded", 17 },
            { MessageType.Traffic, "unforwarded", 18 },
        };
    }

    [Theory]
    [MemberData(nameof(AgeMatrix))]
    public void KillByAge_PerTypeAndStateMatrix(MessageType type, string scenario, int lifetimeDays)
    {
        Message m = _ts.Store.AddMessage(type switch
        {
            MessageType.Personal => Drafts.Personal(to: "G8BPQ"),
            MessageType.Bulletin => Drafts.Bulletin(),
            _ => Drafts.Traffic(to: "32118"),
        });

        switch (scenario)
        {
            case "read":
                _ts.Store.MarkRead(m.Number, m.Recipients[0].ToCall);
                break;
            case "forwarded":
                _ts.Store.EnqueueForwards(m.Number, ["GB7BPQ"]);
                _ts.Store.MarkForwarded(m.Number, "GB7BPQ");
                break;
            case "unforwarded":
                _ts.Store.EnqueueForwards(m.Number, ["GB7BPQ"]);
                break;
            case "delivered":
                _ts.Store.MarkDelivered(m.Number);
                break;
            case "unread":
                break;
            default:
                throw new InvalidOperationException(scenario);
        }

        // Give every category a distinct lifetime; only the exercised one is short.
        var policy = new HousekeepingPolicy
        {
            PersonalReadDays = Pick(type, MessageType.Personal, scenario, "read", lifetimeDays),
            PersonalUnreadDays = Pick(type, MessageType.Personal, scenario, "unread", lifetimeDays),
            PersonalForwardedDays = Pick(type, MessageType.Personal, scenario, "forwarded", lifetimeDays),
            PersonalUnforwardedDays = Pick(type, MessageType.Personal, scenario, "unforwarded", lifetimeDays),
            BulletinForwardedDays = Pick(type, MessageType.Bulletin, scenario, "forwarded", lifetimeDays),
            BulletinUnforwardedDays = Pick(type, MessageType.Bulletin, scenario, "unforwarded", lifetimeDays),
            NtsDeliveredDays = Pick(type, MessageType.Traffic, scenario, "delivered", lifetimeDays),
            NtsForwardedDays = Pick(type, MessageType.Traffic, scenario, "forwarded", lifetimeDays),
            NtsUnforwardedDays = Pick(type, MessageType.Traffic, scenario, "unforwarded", lifetimeDays),
        };

        // Just before the lifetime: survives.
        _ts.Time.AdvanceDays(lifetimeDays - 1);
        Assert.Equal(0, Housekeeping.Run(_ts.Store, policy).MessagesKilledByAge);

        // Just after: killed.
        _ts.Time.AdvanceDays(2);
        HousekeepingSummary summary = Housekeeping.Run(_ts.Store, policy);
        Assert.Equal(1, summary.MessagesKilledByAge);
        Assert.Equal(MessageStatus.Killed, _ts.Store.GetMessage(m.Number)!.Status);

        static int Pick(MessageType actual, MessageType wanted, string scenario, string wantedScenario, int shortDays)
            => actual == wanted && scenario == wantedScenario ? shortDays : 1000;
    }

    [Fact]
    public void HeldMessages_ExemptFromAgeKill()
    {
        // Documented judgment: H sits in the sysop's queue (§2.2) and never expires silently.
        Message held = _ts.Store.AddMessage(Drafts.Personal(hold: true));
        _ts.Time.AdvanceDays(400);

        Assert.Equal(0, Housekeeping.Run(_ts.Store, Defaults).MessagesKilledByAge);
        Assert.Equal(MessageStatus.Held, _ts.Store.GetMessage(held.Number)!.Status);
    }

    [Fact]
    public void BulletinQueuedStatus_AgesAsUnforwarded()
    {
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin());
        _ts.Store.EnqueueForwards(bull.Number, ["GB7BPQ"]);
        Assert.Equal(MessageStatus.BulletinQueued, _ts.Store.GetMessage(bull.Number)!.Status);

        var policy = new HousekeepingPolicy { BulletinUnforwardedDays = 5 };
        _ts.Time.AdvanceDays(6);
        Assert.Equal(1, Housekeeping.Run(_ts.Store, policy).MessagesKilledByAge);
    }

    [Fact]
    public void Summary_CountsAllThreeBuckets()
    {
        Message killed = _ts.Store.AddMessage(Drafts.Personal(subject: "kill me"));
        _ts.Store.Kill(killed.Number);
        _ts.Store.AddMessage(Drafts.Personal(subject: "age me"));
        _ts.Store.RecordBid("STALE_BID");

        _ts.Time.AdvanceDays(61);

        HousekeepingSummary summary = Housekeeping.Run(_ts.Store, Defaults);

        Assert.Equal(1, summary.KilledMessagesPurged);
        Assert.Equal(1, summary.MessagesKilledByAge);
        // Both the explicit record and the messages' own BIDs are >60 days old now.
        Assert.True(summary.BidsPurged >= 1);
        Assert.Null(_ts.Store.LookupBid("STALE_BID"));
    }

    // ---------------------------------------------------------------- issue #39: per-class defaults

    [Fact]
    public void Default_Bulletins_AgeOutAtSevenDays_PersonalsAtThirty()
    {
        // The headline per-class default change (issue #39): with the BUILT-IN policy a bulletin
        // ages out at ~a week while a personal still lives a month.
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin(subject: "weekly news"));
        Message personal = _ts.Store.AddMessage(Drafts.Personal(subject: "keep me a month"));

        // Day 8: the bulletin is past its 7-day default; the personal is nowhere near its 30.
        _ts.Time.AdvanceDays(8);
        Assert.Equal(1, Housekeeping.Run(_ts.Store, Defaults).MessagesKilledByAge);
        Assert.Equal(MessageStatus.Killed, _ts.Store.GetMessage(bull.Number)!.Status);
        Assert.Equal(MessageStatus.Unread, _ts.Store.GetMessage(personal.Number)!.Status);

        // Day 31 total: now the personal is past 30 too.
        _ts.Time.AdvanceDays(23);
        Assert.Equal(1, Housekeeping.Run(_ts.Store, Defaults).MessagesKilledByAge);
        Assert.Equal(MessageStatus.Killed, _ts.Store.GetMessage(personal.Number)!.Status);
    }

    [Fact]
    public void DefaultPolicy_ExposesTheDocumentedClassConstants()
    {
        Assert.Equal(7, HousekeepingPolicy.DefaultBulletinDays);
        Assert.Equal(30, HousekeepingPolicy.DefaultPersonalDays);
        var defaults = new HousekeepingPolicy();
        Assert.Equal(7, defaults.BulletinForwardedDays);
        Assert.Equal(7, defaults.BulletinUnforwardedDays);
        Assert.Equal(30, defaults.PersonalReadDays);
        Assert.Equal(30, defaults.NtsUnforwardedDays);
        Assert.Equal(0, defaults.MaxMsgno); // renumbering off unless opted in
    }

    // ---------------------------------------------------------------- issue #39: MaxMsgno renumber

    [Fact]
    public void MaxMsgno_Zero_NeverRenumbers()
    {
        Message a = _ts.Store.AddMessage(Drafts.Personal(subject: "a"));
        Message b = _ts.Store.AddMessage(Drafts.Personal(subject: "b"));
        // The default policy has MaxMsgno=0: even a tiny high-water mark must not renumber.
        HousekeepingSummary summary = Housekeeping.Run(_ts.Store, Defaults);
        Assert.Equal(0, summary.MessagesRenumbered);
        Assert.NotNull(_ts.Store.GetMessage(a.Number));
        Assert.NotNull(_ts.Store.GetMessage(b.Number));
    }

    [Fact]
    public void MaxMsgno_BelowCeiling_DoesNotRenumber()
    {
        _ts.Store.AddMessage(Drafts.Personal(subject: "a"));
        _ts.Store.AddMessage(Drafts.Personal(subject: "b")); // high-water mark = 2
        var policy = new HousekeepingPolicy { MaxMsgno = 100, BulletinForwardedDays = 1000, PersonalReadDays = 1000 };
        Assert.Equal(0, Housekeeping.Run(_ts.Store, policy).MessagesRenumbered);
    }

    [Fact]
    public void MaxMsgno_AtCeiling_CompactsSurvivorsFromOne_AndBidsAreStable()
    {
        // Three messages (numbers 1,2,3). Kill+purge #2, then trip the ceiling: the survivors compact
        // to 1,2 (old 1 stays 1; old 3 becomes 2). The wire identity (BID) must NOT move.
        Message m1 = _ts.Store.AddMessage(Drafts.Personal(subject: "one"));
        Message m2 = _ts.Store.AddMessage(Drafts.Personal(subject: "two"));
        Message m3 = _ts.Store.AddMessage(Drafts.Personal(subject: "three"));
        string bid1 = m1.Bid, bid3 = m3.Bid;

        _ts.Store.Kill(m2.Number); // purged on the next run (grace 0)

        var policy = new HousekeepingPolicy
        {
            MaxMsgno = 3, // high-water mark is exactly 3 → fires (>=)
            PersonalReadDays = 1000, PersonalUnreadDays = 1000, // keep the survivors alive
        };

        HousekeepingSummary summary = Housekeeping.Run(_ts.Store, policy);

        Assert.Equal(1, summary.KilledMessagesPurged); // #2 gone
        Assert.Equal(1, summary.MessagesRenumbered);   // only old#3→new#2 actually moves (old#1 stays #1)
        // Survivors are dense 1..2; the BIDs are unchanged and now point at the NEW numbers.
        Assert.Equal("one", _ts.Store.GetMessage(1)!.Subject);
        Assert.Equal("three", _ts.Store.GetMessage(2)!.Subject);
        Assert.Null(_ts.Store.GetMessage(3));
        Assert.Equal(1, _ts.Store.LookupBid(bid1)!.MessageNumber);
        Assert.Equal(2, _ts.Store.LookupBid(bid3)!.MessageNumber);

        // A NEW message after the renumber continues from the dense max, never reissuing a number.
        Message m4 = _ts.Store.AddMessage(Drafts.Personal(subject: "four"));
        Assert.Equal(3, m4.Number);
    }

    [Fact]
    public void MaxMsgno_Renumber_PreservesRecipientForwardAndReadReferences()
    {
        // Referential-integrity proof: recipients, the forward queue, per-recipient read-state and a
        // per-user (bulletin) read marker all follow their message across a renumber.
        Message gap = _ts.Store.AddMessage(Drafts.Personal(subject: "gap")); // becomes the purged hole
        Message keep = _ts.Store.AddMessage(Drafts.Personal(to: "G8BPQ", subject: "keep"));
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin(subject: "bull"));
        string keepBid = keep.Bid, bullBid = bull.Bid;

        _ts.Store.MarkRead(keep.Number, "G8BPQ");               // recipient read-state
        _ts.Store.EnqueueForwards(keep.Number, ["GB7XYZ"]);     // a pending forward
        _ts.Store.SetReadByUser("M0ABC", bull.Number);          // per-user bulletin read marker

        _ts.Store.Kill(gap.Number);

        var policy = new HousekeepingPolicy
        {
            MaxMsgno = 3,
            PersonalReadDays = 1000, PersonalUnreadDays = 1000, PersonalForwardedDays = 1000,
            BulletinForwardedDays = 1000, BulletinUnforwardedDays = 1000,
        };
        Assert.Equal(2, Housekeeping.Run(_ts.Store, policy).MessagesRenumbered);

        long newKeep = _ts.Store.LookupBid(keepBid)!.MessageNumber!.Value;
        long newBull = _ts.Store.LookupBid(bullBid)!.MessageNumber!.Value;
        Assert.Equal(1, newKeep); // gap was #1; keep was #2 → compacts to #1
        Assert.Equal(2, newBull);

        // The recipient row + its read-state moved with the message.
        Message keepNow = _ts.Store.GetMessage(newKeep)!;
        Assert.Equal("G8BPQ", Assert.Single(keepNow.Recipients).ToCall);
        Assert.NotNull(Assert.Single(keepNow.Recipients).ReadAt);

        // The pending forward moved with the message — still queued for GB7XYZ at the NEW number.
        Assert.Contains(_ts.Store.GetForwardQueue("GB7XYZ"), m => m.Number == newKeep);
        Assert.Equal("GB7XYZ", Assert.Single(_ts.Store.GetMessageForwards(newKeep)).PartnerCall);

        // The per-user bulletin read marker moved with the bulletin.
        Assert.True(_ts.Store.IsReadByUser("M0ABC", newBull));
        Assert.False(_ts.Store.IsReadByUser("M0ABC", 99));
    }

    [Fact]
    public void MaxMsgno_Renumber_RemapsLastListedWatermark()
    {
        // A user who had listed up to #3 must still see "everything up to here is seen" after a
        // renumber compacts the numbers. #2 is the purged hole; survivors 1,3 → 1,2.
        _ts.Store.AddMessage(Drafts.Personal(subject: "one"));
        Message two = _ts.Store.AddMessage(Drafts.Personal(subject: "two"));
        _ts.Store.AddMessage(Drafts.Personal(subject: "three"));
        _ts.Store.SetLastListed("M0LTE", 3);

        _ts.Store.Kill(two.Number);

        var policy = new HousekeepingPolicy { MaxMsgno = 3, PersonalReadDays = 1000, PersonalUnreadDays = 1000 };
        Housekeeping.Run(_ts.Store, policy);

        // The watermark maps to the new number of the highest survivor at/below the old watermark (#3 → new #2).
        Assert.Equal(2, _ts.Store.GetUser("M0LTE")!.LastListedNumber);
    }

    [Fact]
    public void MaxMsgno_Renumber_SurvivesReopen()
    {
        // Crash-safety surrogate: the renumber commits, then we reopen the db on the same file —
        // the commit is the atomic boundary, so the new numbering must be durable. A purged hole
        // (#2) forces real movement (old#4→#3) so this exercises a true renumber, not a no-op.
        Message a = _ts.Store.AddMessage(Drafts.Personal(subject: "a"));   // #1
        Message gap = _ts.Store.AddMessage(Drafts.Personal(subject: "gap")); // #2 (purged)
        _ts.Store.AddMessage(Drafts.Personal(subject: "c"));               // #3
        Message d = _ts.Store.AddMessage(Drafts.Personal(subject: "d"));   // #4
        string aBid = a.Bid, dBid = d.Bid;
        _ts.Store.Kill(gap.Number);

        var policy = new HousekeepingPolicy { MaxMsgno = 4, PersonalReadDays = 1000, PersonalUnreadDays = 1000 };
        Assert.Equal(2, Housekeeping.Run(_ts.Store, policy).MessagesRenumbered); // old#3→#2 and old#4→#3 move

        BbsStore reopened = _ts.Reopen();
        // Dense 1,2,3 preserved; BIDs still resolve; next number continues from the max.
        Assert.Equal("a", reopened.GetMessage(1)!.Subject);
        Assert.Equal("c", reopened.GetMessage(2)!.Subject);
        Assert.Equal("d", reopened.GetMessage(3)!.Subject);
        Assert.Equal(1, reopened.LookupBid(aBid)!.MessageNumber);
        Assert.Equal(3, reopened.LookupBid(dBid)!.MessageNumber);
        Message e = reopened.AddMessage(Drafts.Personal(subject: "e"));
        Assert.Equal(4, e.Number);
    }

    // ----- issue #36: White Pages directory aging sweep -----

    [Fact]
    public void WhitePages_NotSeenWithinLifetime_Pruned_RecentlySeenKept()
    {
        // The sweep keys on last-seen, not record content. AA1AA is ingested then NOT seen for >90 days;
        // BB2BB is ingested just before the run. With a 90-day lifetime the unseen one is pruned.
        _ts.Store.UpsertWhitePages(new WhitePagesRecord(
            "AA1AA", WhitePagesType.Guessed, new DateOnly(2025, 1, 1), "AA1AA.EURO", "Alice", "Place", null));
        _ts.Time.Advance(TimeSpan.FromDays(120));
        _ts.Store.UpsertWhitePages(new WhitePagesRecord(
            "BB2BB", WhitePagesType.Guessed, new DateOnly(2026, 6, 1), "BB2BB.EURO", "Bob", "Burgh", null));

        var policy = new HousekeepingPolicy { WhitePagesDays = 90 };
        HousekeepingSummary summary = Housekeeping.Run(_ts.Store, policy);

        Assert.Equal(1, summary.WhitePagesPruned);
        Assert.Null(_ts.Store.GetWhitePages("AA1AA"));    // unseen for >90 days → pruned
        Assert.NotNull(_ts.Store.GetWhitePages("BB2BB")); // just seen → kept
    }

    [Fact]
    public void WhitePages_ActiveStation_OldRecordButRecentlyReseen_IsKept()
    {
        // An active station re-announces each cycle. Its record content (record_date) stays old, but the
        // re-sighting bumps last_seen, so the sweep keeps it even past the lifetime measured from the date.
        _ts.Store.UpsertWhitePages(new WhitePagesRecord(
            "AA1AA", WhitePagesType.Guessed, new DateOnly(2025, 1, 1), "AA1AA.EURO", "Alice", "Place", null));
        _ts.Time.Advance(TimeSpan.FromDays(120));
        _ts.Store.UpsertWhitePages(new WhitePagesRecord( // re-seen (stale no-op upsert still bumps last_seen)
            "AA1AA", WhitePagesType.Guessed, new DateOnly(2025, 1, 1), "AA1AA.EURO", "Alice", "Place", null));

        var policy = new HousekeepingPolicy { WhitePagesDays = 90 };
        Assert.Equal(0, Housekeeping.Run(_ts.Store, policy).WhitePagesPruned);
        Assert.NotNull(_ts.Store.GetWhitePages("AA1AA"));
    }

    [Fact]
    public void WhitePages_DefaultLifetime_KeepsRecentlySeenEntries()
    {
        // The 180-day default keeps a station seen now.
        _ts.Store.UpsertWhitePages(new WhitePagesRecord(
            "AA1AA", WhitePagesType.Guessed, new DateOnly(2026, 6, 1), "AA1AA.EURO", "Alice", "Place", null));

        Assert.Equal(0, Housekeeping.Run(_ts.Store, Defaults).WhitePagesPruned);
        Assert.NotNull(_ts.Store.GetWhitePages("AA1AA"));
    }
}
