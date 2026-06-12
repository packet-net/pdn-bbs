using Bbs.Core;
using Bbs.Fbb;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bbs.Host.Tests;

/// <summary>
/// Pins the composed host itself — the exact production wiring via
/// <see cref="HostComposition.Build"/> — against the wire-faithful <see cref="FakeRhpServer"/>.
/// Regression (lab, 2026-06-11): <c>AddHostedService</c> registers through
/// <c>TryAddEnumerable</c>, which de-duplicates by implementation type, so four registrations
/// of one non-generic <c>ComponentService</c> silently collapsed to the first — only the
/// rhp-link loop ever ran. Inbound connections were accepted (and acked at the AX.25 layer)
/// but the demux never dequeued them: no greeting, no FBB session, nothing sent back.
/// No component-level test could see it because every harness started the loops by hand.
/// </summary>
public sealed class HostCompositionTests
{
    [Fact]
    public async Task ComposedHost_RegistersEveryComponentLoop()
    {
        await using var host = await ComposedHost.BuildAsync(start: false);

        List<IHostedService> components = [.. host.App.Services.GetServices<IHostedService>()
            .Where(s => s.GetType().Name.StartsWith("ComponentService", StringComparison.Ordinal))];

        // rhp-link + demux + forwarding + housekeeping. Before the fix this was 1 (rhp-link).
        Assert.Equal(4, components.Count);
        Assert.Equal(4, components.Select(s => s.GetType()).Distinct().Count());
    }

    [Fact]
    public async Task ComposedHost_InboundConsoleSession_GreetingReachesPeerOverTheWire()
    {
        await using var host = await ComposedHost.BuildAsync(start: true);
        host.Store.UpsertUser(new User { Callsign = "M0ABC", Name = "Alice", HomeBbs = "GB7PDN" });

        // The lab flow: accept push + child Connected status push, then the caller's first
        // I-frame — through the real RhpNodeLink + InboundDemux as Program composes them.
        // The production default surface is plain (the plain-language mandate), so this pins
        // the plain greeting reaching the wire end-to-end and the plain `quit` signing off.
        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");
        await peer.SendLineAsync("quit");

        // Greet-immediately (compat spec §1.1/§3.1): the SID line leads. The composed
        // host's version is the assembly informational version, so pin the shape only.
        string sidLine = await peer.ReadLineAsync();
        Assert.True(Sid.IsSidShaped(sidLine), $"First line was not SID-shaped: \"{sidLine}\"");
        Assert.StartsWith("[PDN-", sidLine, StringComparison.Ordinal);

        Assert.Equal("Hello and welcome to the GB7PDN mailbox.", await peer.ReadLineAsync());
        Assert.Equal("You have no new mail. Type help if you'd like a hand.", await peer.ReadLineAsync());
        Assert.Equal("GB7PDN ready, what next? 73 - thanks for calling GB7PDN. See you next time.", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }
}

/// <summary>
/// The production composition booted for a test: a temp state dir with a <c>bbs.yaml</c>
/// pointing at a <see cref="FakeRhpServer"/>, built through <see cref="HostComposition.Build"/>
/// (webmail on an ephemeral loopback port). Dispose stops the host and cleans up.
/// </summary>
internal sealed class ComposedHost : IAsyncDisposable
{
    private readonly DirectoryInfo _dir;
    private readonly bool _started;

    private ComposedHost(FakeRhpServer server, WebApplication app, DirectoryInfo dir, bool started)
    {
        Server = server;
        App = app;
        _dir = dir;
        _started = started;
    }

    public FakeRhpServer Server { get; }

    public WebApplication App { get; }

    public BbsStore Store => App.Services.GetRequiredService<BbsStore>();

    /// <summary>Builds (and with <paramref name="start"/>, starts) the composed host.</summary>
    public static async Task<ComposedHost> BuildAsync(bool start)
    {
        var server = new FakeRhpServer();
        server.Start();
        DirectoryInfo dir = Directory.CreateTempSubdirectory("bbs-composed-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "bbs.yaml"), $"""
            callsign: GB7PDN
            sysop: M0LTE
            hRoute: "#23.GBR.EURO"
            web:
              bind: 127.0.0.1
              port: 0
            rhp:
              host: 127.0.0.1
              port: {server.Port}
            partners: []
            demuxFirstLineWaitSeconds: 30
            """);

        // HostComposition.Build reads PDN_APP_STATE synchronously; restore straight after.
        string? previous = Environment.GetEnvironmentVariable("PDN_APP_STATE");
        WebApplication app;
        try
        {
            Environment.SetEnvironmentVariable("PDN_APP_STATE", dir.FullName);
            app = HostComposition.Build([]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDN_APP_STATE", previous);
        }

        var host = new ComposedHost(server, app, dir, start);
        if (start)
        {
            await app.StartAsync();
            await server.WaitForListenAsync();
        }

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await App.StopAsync(cts.Token);
        }

        await App.DisposeAsync();
        await Server.DisposeAsync();
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
