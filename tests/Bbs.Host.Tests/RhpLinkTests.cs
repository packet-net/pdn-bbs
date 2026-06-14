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

    // ----- Brief change #2: the BBS service alias is bound ALONGSIDE the primary callsign. -----

    [Fact]
    public async Task Link_BindsServiceAliasAlongsidePrimary()
    {
        await using var host = new HostHarness(serviceCallsign: "BBS");
        await host.StartLinkAsync();

        // Both the primary callsign AND the alias successfully listen (order: primary first).
        var listened = new List<string>();
        await host.AdvanceUntilAsync(TimeSpan.FromMilliseconds(1), () =>
        {
            while (host.Server.Listened.Reader.TryRead(out string? c))
            {
                listened.Add(c);
            }

            return Task.FromResult(listened.Contains("BBS") && listened.Contains(HostHarness.OwnCall));
        });

        Assert.Contains(HostHarness.OwnCall, listened);
        Assert.Contains("BBS", listened);
        Assert.True(host.Link.IsUp);
    }

    [Fact]
    public async Task Link_InboundToAlias_RoutesToTheSameSessionHandler()
    {
        // An accept on the alias listener flows through Accepted exactly like the primary —
        // the demux serves it. (The fake's accept push uses whichever conn last listened; both
        // listens are on the one client, so accepts for either callsign reach the demux.)
        await using var host = new HostHarness(serviceCallsign: "BBS");
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");
        Assert.NotNull(peer);

        // The BBS greets immediately (its SID) on the accepted child — proof the alias-bound
        // listener's children reach the session handler, not a black hole.
        string sid = await peer.ReadLineAsync();
        Assert.Contains("PDN", sid, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_AliasDuplicate_IsLoggedButPrimaryStaysUp()
    {
        // The node already has BBS claimed → the alias listen is refused (errCode 9). The link
        // must carry on with just the primary callsign (losing the alias never takes us off air).
        await using var host = new HostHarness(serviceCallsign: "BBS");
        host.Server.ClaimedCallsigns.Add("BBS");
        await host.StartLinkAsync();

        Assert.True(host.Link.IsUp);
        string primary = await host.Server.WaitForListenedAsync();
        Assert.Equal(HostHarness.OwnCall, primary);
        // No second (alias) listen ever succeeds.
        Assert.False(host.Server.Listened.Reader.TryRead(out _));
    }

    // ----- Brief change #1: the free-SSID probe walks past a duplicate-socket refusal. -----

    [Fact]
    public async Task Link_ProbesNextFreeSsid_WhenDerivedSsidIsTaken()
    {
        // Node is M9YYY-2; the BBS derived M9YYY-1, but the node already has M9YYY-1 claimed.
        // The probe must skip -1 (and never try -2, the node's own SSID) and bind M9YYY-3.
        await using var host = new HostHarness(
            bindCallsign: "M9YYY-1", probeSsid: true, nodeCallsign: "M9YYY-2", serviceCallsign: null);
        host.Server.ClaimedCallsigns.Add("M9YYY-1");

        await host.StartLinkAsync();

        string bound = await host.Server.WaitForListenedAsync();
        Assert.Equal("M9YYY-3", bound); // -1 taken, -2 skipped (node's own), -3 wins
        Assert.Equal("M9YYY-3", host.Link.BoundCallsign);
        Assert.True(host.Link.IsUp);
    }

    [Fact]
    public async Task Link_ProbeWinner_IsPinnedAndReboundDirectlyAfterReconnect()
    {
        // After a probe walks to M9YYY-3, a node restart must rebind M9YYY-3 DIRECTLY (no re-probe),
        // even though M9YYY-1 is now free — the on-air identity is pinned, stable across the outage.
        await using var host = new HostHarness(
            bindCallsign: "M9YYY-1", probeSsid: true, nodeCallsign: "M9YYY-2", serviceCallsign: null);
        host.Server.ClaimedCallsigns.Add("M9YYY-1");
        await host.StartLinkAsync();
        Assert.Equal("M9YYY-3", await host.Server.WaitForListenedAsync());

        // The node restarts; -1 is now free. The link must STILL rebind M9YYY-3.
        host.Server.ClaimedCallsigns.Clear();
        await host.Server.StopAsync();
        host.Server.Start();

        string? rebound = null;
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(2), () =>
        {
            host.Server.Listened.Reader.TryRead(out rebound);
            return Task.FromResult(rebound is not null);
        });
        Assert.Equal("M9YYY-3", rebound);
        Assert.Equal("M9YYY-3", host.Link.BoundCallsign);
    }

    [Fact]
    public async Task Demux_PromptFollowsTheProbeWalkedCallsign()
    {
        // The console prompt must advertise the ACTUALLY-bound callsign, not the pre-probe default:
        // when the probe walks M9YYY-1 → M9YYY-3, a caller sees `de M9YYY-3>`, never `de M9YYY-1>`.
        await using var host = new HostHarness(
            bindCallsign: "M9YYY-1", probeSsid: true, nodeCallsign: "M9YYY-2", serviceCallsign: null);
        host.Server.ClaimedCallsigns.Add("M9YYY-1");
        await host.StartLinkAsync();
        Assert.Equal("M9YYY-3", host.Link.BoundCallsign);
        host.StartDemux();

        // A known partner gets the bare `de CALL>` prompt right after the SID (DemuxTests pattern).
        host.Store.UpsertPartner(new Bbs.Core.Partner { Call = "GB7BPQ" });
        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.ReadLineAsync(); // SID
        Assert.Equal("de M9YYY-3>", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task Link_NonProbingCallsign_DoesNotWalk()
    {
        // An explicit (non-probing) callsign that is taken is NOT walked — the link simply
        // never comes up (the operator must fix the configured identity). We assert it does not
        // silently bind a different SSID.
        await using var host = new HostHarness(
            bindCallsign: "GB7ABC", probeSsid: false, serviceCallsign: null);
        host.Server.ClaimedCallsigns.Add("GB7ABC");

        host.StartLinkInBackground();

        // Give the link a moment; it must not bind any other callsign.
        await Task.Delay(300);
        Assert.False(host.Server.Listened.Reader.TryRead(out _));
        Assert.False(host.Link.IsUp);
    }
}
