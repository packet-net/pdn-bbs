using Packet.Fbb;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Host.Tests;

/// <summary>
/// The durable, crash-safe scratch store for receiver-side restart granting (issue #38):
/// round-trip, durability across a reopen (the daemon-restart surrogate), peer + mid keying, the
/// divergence guard, and TTL garbage collection.
/// </summary>
public sealed class FileInboundPartialStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bbs-partial-test-");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Delete(recursive: true);

    private FileInboundPartialStore NewStore(TimeSpan? ttl = null) =>
        new(_dir.FullName, _time, ttl);

    [Fact]
    public void SaveThenLoad_RoundTripsBytesAndExpectedSize()
    {
        IInboundResumeStore peer = NewStore().ForPeer("GB7RDG");
        byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        peer.Save("42_GB7RDG", bytes, expectedCompressedSize: 99);

        InboundPartial? maybe = peer.TryLoad("42_GB7RDG");
        Assert.NotNull(maybe);
        Assert.Equal(bytes, maybe.Value.Compressed);
        Assert.Equal(99, maybe.Value.ExpectedCompressedSize);
    }

    [Fact]
    public void Partial_SurvivesAReopen_TheRestartSurrogate()
    {
        byte[] bytes = [10, 20, 30, 40, 50, 60, 70, 80];
        NewStore().ForPeer("GB7RDG").Save("7_GB7RDG", bytes, 0);

        // A brand-new store instance on the SAME directory = a daemon restart.
        IInboundResumeStore reopened = NewStore().ForPeer("GB7RDG");
        InboundPartial? loaded = reopened.TryLoad("7_GB7RDG");
        Assert.NotNull(loaded);
        Assert.Equal(bytes, loaded.Value.Compressed);
    }

    [Fact]
    public void KeyedByPeerAndMid_NoCrossTalk()
    {
        FileInboundPartialStore store = NewStore();
        store.ForPeer("GB7RDG").Save("1_X", [1, 2, 3], 0);
        store.ForPeer("GB7CIP").Save("1_X", [9, 9, 9], 0);

        Assert.Equal([1, 2, 3], store.ForPeer("GB7RDG").TryLoad("1_X")!.Value.Compressed);
        Assert.Equal([9, 9, 9], store.ForPeer("GB7CIP").TryLoad("1_X")!.Value.Compressed);
        Assert.Null(store.ForPeer("GB7RDG").TryLoad("2_X"));
    }

    [Fact]
    public void PeerKeyedByBaseCallsign_SsidShares()
    {
        FileInboundPartialStore store = NewStore();
        store.ForPeer("GB7RDG-2").Save("3_X", [4, 5, 6], 0);

        // A later attempt under a different SSID (inbound SSID is indeterminate) still finds it.
        Assert.Equal([4, 5, 6], store.ForPeer("GB7RDG-7").TryLoad("3_X")!.Value.Compressed);
        Assert.Equal([4, 5, 6], store.ForPeer("GB7RDG").TryLoad("3_X")!.Value.Compressed);
    }

    [Fact]
    public void Discard_RemovesThePartial()
    {
        IInboundResumeStore peer = NewStore().ForPeer("GB7RDG");
        peer.Save("5_X", [1, 2, 3, 4], 0);
        Assert.NotNull(peer.TryLoad("5_X"));

        peer.Discard("5_X");
        Assert.Null(peer.TryLoad("5_X"));
        peer.Discard("5_X"); // idempotent
    }

    [Fact]
    public void CollectStale_RemovesPartialsOlderThanTtl_KeepsFresh()
    {
        FileInboundPartialStore store = NewStore(TimeSpan.FromDays(7));
        store.ForPeer("GB7RDG").Save("old_X", [1, 2, 3], 0);

        _time.Advance(TimeSpan.FromDays(8)); // the old partial is now past the 7-day TTL
        store.ForPeer("GB7RDG").Save("new_X", [4, 5, 6], 0); // written "now" — fresh

        int removed = store.CollectStale();

        Assert.Equal(1, removed);
        Assert.Null(store.ForPeer("GB7RDG").TryLoad("old_X"));
        Assert.NotNull(store.ForPeer("GB7RDG").TryLoad("new_X"));
    }

    [Fact]
    public void Save_OverwritesAGrowingPartialAtomically()
    {
        IInboundResumeStore peer = NewStore().ForPeer("GB7RDG");
        peer.Save("g_X", [1, 2], 0);
        peer.Save("g_X", [1, 2, 3, 4], 0); // a later, longer block

        Assert.Equal([1, 2, 3, 4], peer.TryLoad("g_X")!.Value.Compressed);
        // No stray .tmp left behind after a committed save.
        Assert.Empty(Directory.EnumerateFiles(_dir.FullName, "*.tmp", SearchOption.AllDirectories));
    }
}
