using System.Text;
using Bbs.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Host.Tests;

/// <summary>
/// The host wiring of "local delivery beats forwarding" (design.md "The home-BBS requirement"
/// rule #1): <see cref="RoutingService"/> must ask the user store whether each recipient is a
/// known local user and suppress all forward targets accordingly. The dangerous case is the
/// silent one — a wildcard-AT partner (GB7RDG @WW/* is already live on the lab) swallowing a
/// local user's personal mail. These drive the store→routing→forward-queue path end to end.
/// </summary>
public sealed class LocalDeliveryRoutingTests : IAsyncDisposable
{
    private const string OwnCall = "GB7PDN";
    private const string HRoute = "#23.GBR.EURO";

    private readonly DirectoryInfo _dir;
    private readonly BbsStore _store;
    private readonly RoutingService _routing;

    public LocalDeliveryRoutingTests()
    {
        _dir = Directory.CreateTempSubdirectory("bbs-localdelivery-test-");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));
        _store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), OwnCall, time);
        _routing = new RoutingService(
            _store, new RoutingEngine(OwnCall, HRoute), NullLogger<RoutingService>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        _dir.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    private Message StorePersonal(string to, string? at)
        => _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = [to],
            At = at,
            Subject = "Hi",
            Body = Encoding.Latin1.GetBytes("body\r"),
        });

    [Fact]
    public void NoAt_LocalUser_NeverLeaksToWildcardPartner()
    {
        // THE leak test. Without the rule, this no-AT personal for a known local user matches
        // GB7RDG's wildcard AT and is queued for forwarding — the personal mail leak.
        _store.UpsertUser(new User { Callsign = "M0LTE", Name = "Tom" });
        _store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["WW", "*"] });

        Message stored = StorePersonal("M0LTE", null);
        _routing.RouteMessage(stored);

        Assert.Empty(_store.GetForwardQueue("GB7RDG"));
    }

    [Fact]
    public void NoAt_NonLocalUser_StillForwardsToWildcardPartner()
    {
        // The rule does not over-suppress: a no-AT personal for a station who is NOT a local
        // user still hits the wildcard default route.
        _store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["WW", "*"] });

        Message stored = StorePersonal("G4XYZ", null);
        _routing.RouteMessage(stored);

        Assert.Equal(stored.Number, Assert.Single(_store.GetForwardQueue("GB7RDG")).Number);
    }

    [Fact]
    public void AtIsOurOwnCall_StaysLocal_EvenWithWildcardPartner()
    {
        _store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });

        Message stored = StorePersonal("M0LTE", OwnCall);
        _routing.RouteMessage(stored);

        Assert.Empty(_store.GetForwardQueue("GB7RDG"));
    }

    [Fact]
    public void ExplicitRemoteAt_StillForwards_EvenWhenToIsLocalUser()
    {
        // Explicit AT to a remote BBS wins over local-user status.
        _store.UpsertUser(new User { Callsign = "M0LTE", Name = "Tom" });
        _store.UpsertPartner(new Partner { Call = "GB7BSK", AtCalls = ["GB7BSK"] });

        Message stored = StorePersonal("M0LTE", "GB7BSK");
        _routing.RouteMessage(stored);

        Assert.Equal(stored.Number, Assert.Single(_store.GetForwardQueue("GB7BSK")).Number);
    }
}
