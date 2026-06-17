using System.Text;
using Bbs.Core;
using Bbs.Fbb;

namespace Bbs.Host.Tests;

/// <summary>
/// The host wiring of White Pages consume (issue #36): the recognition branch + disposition in
/// <see cref="InboundMessageReceiver.Deliver"/>. These drive the REAL receiver so they prove the
/// end-to-end behaviour — a WP update addressed to us is harvested into the directory and consumed
/// (not stored), a transit WP is harvested AND kept + forwarded, and a non-WP message addressed to a
/// station called "WP" with no records is stored as normal mail (the false-positive guard). All
/// callsigns/QTH are synthetic placeholders.
/// </summary>
public sealed class WhitePagesConsumeTests
{
    private const string WpBody =
        "On 260615 AA1AA/U @ AA1AA.#99.ZZZ.EURO zip ? Alice Placeville\r\n" +
        "On 260614 BB2BB/G @ BB2BB.#88.ZZZ.EURO zip ZZ99 ? ?\r\n";

    /// <summary>Feeds one inbound B1 (FA) delivery of the given shape through the real receiver.</summary>
    private static Message? Deliver(
        HostHarness host,
        char type,
        string to,
        string atBbs,
        string bid,
        string body,
        string from = "AA1AA",
        string fromPartner = "GB7BPQ")
    {
        byte[] bytes = Encoding.Latin1.GetBytes(body);
        var proposal = new FaProposal('A', type, from, atBbs, to, bid, bytes.Length);
        var delivered = new FbbMessageDelivered(proposal, "WP Update", bytes);
        return host.Receiver.Deliver(delivered, fromPartner);
    }

    [Fact]
    public async Task PersonalWpToUs_IsConsumed_HarvestedButNotStored()
    {
        await using var host = new HostHarness();

        Message? stored = Deliver(host, 'P', "WP", HostHarness.OwnCall, "100_GB7RDG", WpBody);

        // Consumed — no message row kept.
        Assert.Null(stored);
        Assert.Equal(0, host.Store.GetLatestMessageNumber());

        // Harvested into the directory.
        Assert.Equal(2, host.Store.CountWhitePages());
        WhitePagesEntry a = host.Store.GetWhitePages("AA1AA")!;
        Assert.Equal("AA1AA.#99.ZZZ.EURO", a.HomeBbs);
        Assert.Equal("Alice", a.Name);
        Assert.Equal("Placeville", a.Qth);
        Assert.NotNull(host.Store.GetWhitePages("BB2BB"));

        // The BID is recorded so a re-send is deduped even though we kept no message row.
        Assert.NotNull(host.Store.LookupBid("100_GB7RDG"));
    }

    [Fact]
    public async Task WpAddressedWithBareWp_NoExplicitAt_IsConsumedViaImpliedAt()
    {
        // The MOST COMMON real shape: a partner forwards a directory update as a bare "To: WP" with NO
        // @bbs — BPQ implies the AT is the receiving station (its "already here" implied-AT). An empty AT
        // therefore means us, so it is CONSUMED, not stored + forwarded onward as mail to "WP".
        await using var host = new HostHarness();

        Message? stored = Deliver(host, 'P', "WP", atBbs: "", bid: "101_GB7RDG", body: WpBody);

        Assert.Null(stored);                                       // consumed, not kept
        Assert.Equal(0, host.Store.GetLatestMessageNumber());      // no message row to forward onward
        Assert.Equal(2, host.Store.CountWhitePages());             // harvested
    }

    [Fact]
    public async Task TransitWp_ViaAnotherBbs_IsHarvested_AndKeptForForwarding()
    {
        await using var host = new HostHarness();

        // A personal WP routed via a DIFFERENT BBS (not us): transit traffic — harvest + keep.
        Message? stored = Deliver(host, 'P', "WP", "GB7OTH.#42.GBR.EURO", "102_GB7RDG", WpBody);

        // Kept (a message row exists for onward forwarding) AND harvested.
        Assert.NotNull(stored);
        Assert.True(host.Store.GetLatestMessageNumber() > 0);
        Assert.Equal(2, host.Store.CountWhitePages());
    }

    [Fact]
    public async Task BulletinWp_IsHarvested_AndKept()
    {
        await using var host = new HostHarness();

        // A bulletin WP is transit/broadcast — harvest its records but keep + forward it.
        Message? stored = Deliver(host, 'B', "WP", HostHarness.OwnCall, "103_GB7RDG", WpBody);

        Assert.NotNull(stored);
        Assert.Equal(MessageType.Bulletin, stored!.Type);
        Assert.Equal(2, host.Store.CountWhitePages());
    }

    [Fact]
    public async Task MessageToStationCalledWp_WithoutRecords_IsStoredAsNormalMail()
    {
        // The false-positive guard: addressed to "WP" but the body has NO `On …` records ⇒ it is a
        // genuine (if bizarre) message and must be stored, never silently eaten.
        await using var host = new HostHarness();

        Message? stored = Deliver(
            host, 'P', "WP", HostHarness.OwnCall, "104_GB7RDG",
            body: "Hi there, this is an ordinary message that just happens to be to WP.\r\n");

        Assert.NotNull(stored);
        Assert.True(host.Store.GetLatestMessageNumber() > 0);
        Assert.Equal(0, host.Store.CountWhitePages()); // nothing harvested
    }

    [Fact]
    public async Task NonWpRecipient_FallsThroughUnchanged_NotHarvested()
    {
        // A normal personal to a real call whose body coincidentally looks like WP records must NOT
        // be harvested or consumed — recognition keys on the recipient being WP.
        await using var host = new HostHarness();

        Message? stored = Deliver(host, 'P', "G0NEW", HostHarness.OwnCall, "105_GB7RDG", WpBody);

        Assert.NotNull(stored);
        Assert.Equal(0, host.Store.CountWhitePages());
    }

    [Fact]
    public async Task BulletinWp_ReSend_IsDedupedAtProposalTime()
    {
        await using var host = new HostHarness();

        // A bulletin WP harvested + kept records a live message row; the existing BID dedup then
        // rejects an identical re-send at proposal time (any known BID rejects for a bulletin).
        Deliver(host, 'B', "WP", HostHarness.OwnCall, "106_GB7RDG", WpBody);

        var proposal = new FaProposal('A', 'B', "AA1AA", HostHarness.OwnCall, "WP", "106_GB7RDG", 100);
        IReadOnlyList<FsAnswer> answers = host.Receiver.Decide([proposal], partner: null);
        Assert.Equal(FsAnswer.AlreadyHave, answers[0]); // '-' — never re-delivered
    }

    [Fact]
    public async Task ReSentConsumedWp_IsIdempotent_NoDuplicateDirectoryRows()
    {
        // A consumed personal WP keeps no message row, so the personal BID dedup (which keys on a
        // live copy) cannot reject a re-send at proposal time. The data-layer guarantee covers it:
        // re-ingesting the same records is a date-wins no-op — no duplicate rows, identical data.
        await using var host = new HostHarness();

        Deliver(host, 'P', "WP", HostHarness.OwnCall, "107_GB7RDG", WpBody);
        Assert.NotNull(host.Store.LookupBid("107_GB7RDG"));
        Assert.Equal(2, host.Store.CountWhitePages());
        WhitePagesEntry before = host.Store.GetWhitePages("AA1AA")!;

        // Re-deliver the identical WP — harmless idempotent no-op.
        Message? again = Deliver(host, 'P', "WP", HostHarness.OwnCall, "107_GB7RDG", WpBody);

        Assert.Null(again);
        Assert.Equal(2, host.Store.CountWhitePages()); // still exactly two — no duplicates
        WhitePagesEntry after = host.Store.GetWhitePages("AA1AA")!;
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.RecordDate, after.RecordDate);
        Assert.Equal(before.HomeBbs, after.HomeBbs);
    }
}
