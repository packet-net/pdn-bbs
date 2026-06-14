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

    /// <summary>
    /// Regression (lab, 2026-06-13): under pdn the BBS derives + binds + answers as
    /// <c>&lt;node-base&gt;-1</c> (e.g. M9YYY-1), but the webmail header showed the bare
    /// SSID-stripped node call (M9YYY), reading as the node's own identity (-0). The
    /// user-visible identity must match what the BBS binds — the FULL bound callsign incl.
    /// SSID — while the SSID-less base stays at the FBB wire layer (R: lines/BIDs/Mbo).
    /// This pins the rendered webmail identity to the bound callsign through the exact
    /// production composition with the callsign DERIVED from PDN_NODE_CALLSIGN.
    /// </summary>
    [Fact]
    public async Task ComposedHost_WebmailIdentity_IsTheBoundCallsignWithSsid_WhenDerivedUnderPdn()
    {
        await using var host = await ComposedHost.BuildAsync(start: true, nodeCallsign: "M9YYY");

        // The BBS identity header renders on the callsign-mapped surfaces (the inbox), so link
        // the pdn user to a callsign first — an unmapped user only sees the claim form.
        host.Store.UpsertUser(new User { Callsign = "M0ABC", PdnUsername = "tom" });

        using var client = new HttpClient { BaseAddress = new Uri(host.App.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Pdn-Gateway", "1");
        client.DefaultRequestHeaders.Add("X-Pdn-User", "tom");

        string html = await client.GetStringAsync("/");

        // The webmail header + title present the BBS identity. It must be the bound callsign
        // (derived <node-base>-1 = M9YYY-1, including the SSID) — never the bare node call.
        Assert.Contains("M9YYY-1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("M9YYY <span class=\"dim\">webmail</span>", html, StringComparison.Ordinal);
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

    /// <summary>
    /// Builds (and with <paramref name="start"/>, starts) the composed host. With
    /// <paramref name="nodeCallsign"/> set, the yaml omits an explicit callsign and
    /// <c>PDN_NODE_CALLSIGN</c> is exported so the BBS DERIVES its identity (the pdn path,
    /// <c>&lt;node-base&gt;-1</c>); otherwise it pins <c>GB7PDN</c> directly.
    /// </summary>
    public static async Task<ComposedHost> BuildAsync(bool start, string? nodeCallsign = null)
    {
        var server = new FakeRhpServer();
        server.Start();
        DirectoryInfo dir = Directory.CreateTempSubdirectory("bbs-composed-test-");
        // Under derivation (nodeCallsign set) omit the explicit callsign so ResolveCallsign
        // takes the PDN_NODE_CALLSIGN path; otherwise pin GB7PDN as before.
        string callsignLine = nodeCallsign is null ? "callsign: GB7PDN\n" : "";
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "bbs.yaml"), $"""
            {callsignLine}sysop: M0LTE
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

        // HostComposition.Build reads PDN_APP_STATE + PDN_NODE_CALLSIGN synchronously; restore straight after.
        string? previous = Environment.GetEnvironmentVariable("PDN_APP_STATE");
        string? previousNode = Environment.GetEnvironmentVariable("PDN_NODE_CALLSIGN");
        WebApplication app;
        try
        {
            Environment.SetEnvironmentVariable("PDN_APP_STATE", dir.FullName);
            Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", nodeCallsign);
            app = HostComposition.Build([]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDN_APP_STATE", previous);
            Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", previousNode);
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
