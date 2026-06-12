using System.Text;
using Bbs.Core;
using Bbs.Fbb;

namespace Bbs.Host.Tests;

/// <summary>
/// The host wiring of "auto-create the user on first inbound personal" (design.md "The home-BBS
/// requirement" rule #2). Building on rule #1's local-delivery signal: an inbound personal whose
/// AT resolves to us (our own call, or a WW-rooted address under our own HA) is for a mailbox
/// homed here; if its addressee isn't yet a known user, <see cref="InboundMessageReceiver"/> must
/// create a skeletal record so the mail is listable on the owner's first connect. These drive the
/// real receiver (<see cref="InboundMessageReceiver.Deliver"/>) — the inbound-only path — so the
/// negative cases (bulletin / NTS / no-AT-existing-user / explicit-remote-AT) prove the trigger is
/// exactly the homed-personal case and nothing else.
/// </summary>
public sealed class AutoCreateHomedUserTests
{
    /// <summary>Feeds one inbound delivery of the given shape through the real receiver.</summary>
    private static Message Deliver(
        HostHarness host,
        char type,
        string from,
        string atBbs,
        string to,
        string bid,
        string fromPartner = "GB7BPQ")
    {
        const string bodyText = "Hello, your mail is waiting.\r";
        byte[] body = Encoding.Latin1.GetBytes(bodyText);
        var proposal = new FaProposal('A', type, from, atBbs, to, bid, body.Length);
        var delivered = new FbbMessageDelivered(proposal, "Test subject", body);
        return host.Receiver.Deliver(delivered, fromPartner)!;
    }

    [Fact]
    public async Task InboundPersonal_AtIsOurOwnCall_UnknownTo_CreatesUser_AndMailIsListable()
    {
        await using var host = new HostHarness();
        Assert.False(host.Store.UserExists("G0NEW"));

        Message stored = Deliver(host, 'P', "M0XYZ", HostHarness.OwnCall, "G0NEW", "1_GB7BPQ");

        // Rule #2: the skeletal user now exists and the just-delivered message is listable for it.
        Assert.True(host.Store.UserExists("G0NEW"));
        Message listed = Assert.Single(host.Store.ListMessages(new MessageQuery { ToCall = "G0NEW" }));
        Assert.Equal(stored.Number, listed.Number);
        Assert.Equal(MessageType.Personal, listed.Type);

        // Skeletal: callsign only — the console's first-connect persistence fills the rest.
        User user = host.Store.GetUser("G0NEW")!;
        Assert.Equal("G0NEW", user.Callsign);
        Assert.Null(user.Name);
        Assert.Null(user.HomeBbs);
    }

    [Fact]
    public async Task InboundPersonal_AtUnderOurHa_UnknownTo_CreatesUser()
    {
        // The other rule-#1 local signal: a WW-rooted AT sitting under our own H-Route/HA names
        // us just as surely as our bare call (GB7PDN.#23.GBR.EURO ⊂ our HA). It homes here too.
        await using var host = new HostHarness();
        Assert.False(host.Store.UserExists("G0HOME"));

        Deliver(host, 'P', "M0XYZ", $"{HostHarness.OwnCall}.{HostHarness.HRoute}", "G0HOME", "2_GB7BPQ");

        Assert.True(host.Store.UserExists("G0HOME"));
        Assert.Single(host.Store.ListMessages(new MessageQuery { ToCall = "G0HOME" }));
    }

    [Fact]
    public async Task SecondInboundForSameTo_IsIdempotent_StillOneUser()
    {
        await using var host = new HostHarness();

        Deliver(host, 'P', "M0XYZ", HostHarness.OwnCall, "G0NEW", "1_GB7BPQ");
        Deliver(host, 'P', "G3ABC", HostHarness.OwnCall, "G0NEW", "2_GB7BPQ"); // same TO again

        // No duplicate-user error; exactly one user, both messages listable for them.
        Assert.True(host.Store.UserExists("G0NEW"));
        Assert.Single(host.Store.ListUsers());
        Assert.Equal(2, host.Store.ListMessages(new MessageQuery { ToCall = "G0NEW" }).Count);
    }

    [Fact]
    public async Task InboundPersonal_NoAt_ExistingLocalUser_DeliversButCreatesNoNewUser()
    {
        // No AT, TO is an existing local user → delivered (rule #1 keeps it local), but there is
        // nothing to auto-create; the user count is unchanged and no error is raised.
        await using var host = new HostHarness();
        host.Store.UpsertUser(new User { Callsign = "M0LTE", Name = "Tom" });
        Assert.Single(host.Store.ListUsers());

        Deliver(host, 'P', "G3ABC", atBbs: "", to: "M0LTE", "3_GB7BPQ");

        Assert.Single(host.Store.ListUsers()); // still just the one, name intact
        Assert.Equal("Tom", host.Store.GetUser("M0LTE")!.Name);
        Assert.Single(host.Store.ListMessages(new MessageQuery { ToCall = "M0LTE" }));
    }

    [Fact]
    public async Task InboundPersonal_ExplicitRemoteAt_DoesNotAutoCreate()
    {
        // An explicit remote AT (@GB7BSK) forwards onward per rule #1 — it is not homed here, so
        // there must be no auto-create, even for an unknown TO.
        await using var host = new HostHarness();

        Deliver(host, 'P', "M0XYZ", atBbs: "GB7BSK", to: "G4XYZ", "4_GB7BPQ");

        Assert.False(host.Store.UserExists("G4XYZ"));
        Assert.Empty(host.Store.ListUsers());
    }

    [Fact]
    public async Task InboundBulletin_AddressedLocally_DoesNotAutoCreate()
    {
        // A bulletin (even one whose AT names us) is never a personal mailbox — no auto-create.
        await using var host = new HostHarness();

        Deliver(host, 'B', "M0XYZ", HostHarness.OwnCall, "ALL", "5_GB7BPQ");

        Assert.False(host.Store.UserExists("ALL"));
        Assert.Empty(host.Store.ListUsers());
    }

    [Fact]
    public async Task InboundTraffic_AddressedLocally_DoesNotAutoCreate()
    {
        // NTS/traffic is never a personal mailbox either — no auto-create.
        await using var host = new HostHarness();

        Deliver(host, 'T', "M0XYZ", HostHarness.OwnCall, "07950", "6_GB7BPQ");

        Assert.False(host.Store.UserExists("07950"));
        Assert.Empty(host.Store.ListUsers());
    }
}
