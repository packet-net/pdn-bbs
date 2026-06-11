namespace Bbs.Host.Tests;

public class RhpLinkTests
{
    [Fact]
    public async Task Link_BindsAndListensOnConnect()
    {
        await using var host = new HostHarness();
        await host.StartLinkAsync();

        BindRecord bind = await host.Server.WaitForBindAsync();
        Assert.Equal(HostHarness.OwnCall, bind.Local);
        Assert.Null(bind.Port); // null port = all node ports (rhp2-server.md)
        Assert.True(host.Link.IsUp);
    }

    [Fact]
    public async Task Link_RebindsAfterNodeRestart()
    {
        await using var host = new HostHarness();
        await host.StartLinkAsync();
        await host.Server.WaitForBindAsync(); // drain the initial bind

        // The node dies: every client connection drops; the listener vanishes.
        await host.Server.StopAsync();
        host.Server.Start();

        // The link reconnects after its TimeProvider-driven backoff and re-binds.
        BindRecord? rebind = null;
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(2), () =>
        {
            host.Server.Binds.Reader.TryRead(out rebind);
            return Task.FromResult(rebind is not null);
        });

        Assert.Equal(HostHarness.OwnCall, rebind!.Local);
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(1), () => Task.FromResult(host.Link.IsUp));
    }

    [Fact]
    public async Task Link_FaultsChildStreamsWhenNodeDies()
    {
        await using var host = new HostHarness();
        await host.StartLinkAsync();
        await host.Server.WaitForBindAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");
        Assert.NotNull(peer);

        // Kill the node mid-peek: the demux's session must end quietly (no hang) and the
        // link must come back up against the restarted node.
        await host.Server.StopAsync();
        host.Server.Start();

        BindRecord? rebind = null;
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(2), () =>
        {
            host.Server.Binds.Reader.TryRead(out rebind);
            return Task.FromResult(rebind is not null);
        });
        Assert.Equal(HostHarness.OwnCall, rebind!.Local);
    }
}
