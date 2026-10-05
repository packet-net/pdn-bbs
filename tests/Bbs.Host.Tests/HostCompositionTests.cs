using Bbs.Core;
using Packet.Fbb;
using Bbs.Host.Forwarding;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

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

        // rhp-link + demux + forwarding + housekeeping + pending-send. Before the fix this was 1 (rhp-link).
        Assert.Equal(5, components.Count);
        Assert.Equal(5, components.Select(s => s.GetType()).Distinct().Count());
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
    /// The callsign-SSID split through the exact production composition (fix, 2026-06-14): a packet
    /// MAIL address never carries an SSID — the SSID is purely a connect-level detail of the partner
    /// relationship. Under pdn the BBS derives bind = <c>&lt;node-base&gt;-1</c> (M9YYY-1) from
    /// PDN_NODE_CALLSIGN, but the MAIL namespace (the store's BbsCallsign → BIDs, the routing
    /// engine's own-call → @home/R-lines) must be the SSID-LESS base (M9YYY), while the CONNECT
    /// identity (RHP bind + interactive console prompt) keeps the SSID (M9YYY-1).
    ///
    /// v0.2.1 fed bind (SSID'd) to BbsStore.Open + new RoutingEngine, leaking the SSID into the mail
    /// namespace; the fix feeds baseCallsign there. This pins both halves end-to-end: mail SSID-less,
    /// connect SSID'd.
    /// </summary>
    [Fact]
    public async Task ComposedHost_MailNamespaceIsSsidLess_WhileConnectIdentityKeepsSsid_WhenDerivedUnderPdn()
    {
        await using var host = await ComposedHost.BuildAsync(start: true, nodeCallsign: "M9YYY");

        // --- Mail namespace: SSID-LESS base (M9YYY, never M9YYY-1) ---

        // The store's own mail identity (the BID suffix source) is the base callsign.
        Assert.Equal("M9YYY", host.Store.BbsCallsign);

        // A locally-originated message gets an auto-BID <n>_<BBSCALL>; the BBS call in it is SSID-less.
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "Hi",
            Body = System.Text.Encoding.Latin1.GetBytes("body\r"),
        });
        Assert.EndsWith("_M9YYY", stored.Bid, StringComparison.Ordinal);
        Assert.DoesNotContain("-1", stored.Bid, StringComparison.Ordinal);

        // The R-line own-call + hierarchical @home leaf the composition feeds the mail layer is the
        // SSID-less base. The outbound builder stamps that own-call into the R: line it prepends; an
        // identity built from the store's mail call (the same source the composition uses) produces
        // an SSID-less R: line @M9YYY (never @M9YYY-1).
        var identity = new BbsIdentity
        {
            Callsign = host.Store.BbsCallsign,
            HRoute = "#23.GBR.EURO",
            SoftwareVersion = "PDN0.1.0",
        };
        var rTime = new FakeTimeProvider(new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero));
        OutboundItem item = Assert.Single(OutboundBuilder.Build(
            [stored], new Partner { Call = "GB7BPQ", MaxTxSize = 99999 }, identity, rTime, NullLogger.Instance));
        string rLine = System.Text.Encoding.Latin1.GetString(item.Wire.Body.Span).Split("\r\n")[0];
        Assert.Equal("R:260614/1200Z 1@M9YYY.#23.GBR.EURO PDN0.1.0", rLine);
        Assert.DoesNotContain("M9YYY-1", rLine, StringComparison.Ordinal);

        // --- Connect identity: SSID'd (M9YYY-1) ---

        // The RHP bind / node listen is the SSID'd connect identity (default SSID 1, node is at 0).
        string bound = await host.Server.WaitForListenedAsync();
        Assert.Equal("M9YYY-1", bound);

        // The interactive console greeting advertises the SSID'd connect identity over the wire:
        // the welcome banner names "M9YYY-1" (the bound callsign), never the bare mail base.
        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");
        string sidLine = await peer.ReadLineAsync();
        Assert.True(Sid.IsSidShaped(sidLine), $"First line was not SID-shaped: \"{sidLine}\"");
        Assert.Equal("Hello and welcome to the M9YYY-1 mailbox.", await peer.ReadLineAsync());
        await peer.SendLineAsync("quit");
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
    /// bind = <c>&lt;node-base&gt;-1</c>, mail = the SSID-less base); otherwise it pins
    /// <c>GB7PDN</c> directly.
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
