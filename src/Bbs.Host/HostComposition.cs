using System.Net;
using System.Reflection;
using Bbs.Console;
using Bbs.Core;
using Bbs.Host.Forwarding;
using Bbs.Host.Rhp;
using Bbs.Host.Sessions;
using Bbs.Host.Web;

namespace Bbs.Host;

/// <summary>
/// Builds the composed BBS host — RHPv2 attachment + inbound demux + forwarding scheduler +
/// webmail + housekeeping over the Fbb/Core/Console libraries (design.md src/Bbs.Host).
/// Extracted from Program so the tests can boot the exact production wiring against a fake
/// node (see <c>HostCompositionTests</c>): the composition itself is load-bearing and once
/// shipped a bug no component test could see.
/// </summary>
public static class HostComposition
{
    /// <summary>
    /// Composes the application. State (db + config) lives in <c>$PDN_APP_STATE</c>; the
    /// RHP endpoint falls back to <c>PDN_RHP_HOST</c>/<c>PDN_RHP_PORT</c>. The caller owns
    /// the returned app (<c>Run</c> in Program, <c>StartAsync</c>/<c>DisposeAsync</c> in tests).
    /// </summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        string stateDir = Environment.GetEnvironmentVariable("PDN_APP_STATE") is { Length: > 0 } s
            ? s
            : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(stateDir);

        (BbsHostConfig config, bool createdDefault) =
            BbsHostConfigFile.LoadOrCreate(stateDir, Environment.GetEnvironmentVariable);

        string version = typeof(HostComposition).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } iv
            ? iv.Split('+')[0]
            : "0.1.0";

        string bindCallsign = Callsigns.Normalize(config.Callsign);
        string baseCallsign = Callsigns.StripSsid(bindCallsign);
        var time = TimeProvider.System;

        // Webmail binds exactly what the config says (loopback per the app-gateway contract).
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Parse(config.Web.Bind), config.Web.Port));

        var store = BbsStore.Open(Path.Combine(stateDir, "bbs.db"), bindCallsign, time);
        builder.Services.AddSingleton(store); // owned by the host: disposed on shutdown

        // Partners: config is the source of truth (v1) — upsert everything configured, prune the rest.
        HostStartup.SyncPartners(store, config);

        var identity = new BbsIdentity
        {
            Callsign = baseCallsign,
            HRoute = config.HRoute,
            SoftwareVersion = "PDN" + version,
        };

        var engine = new RoutingEngine(bindCallsign, config.HRoute);

        var consoleConfig = new BbsConsoleConfig
        {
            // The console prompt is `de <CALL>>` with the FULL bound callsign incl. SSID —
            // the oracle's own transcript shows `de GB7BPQ-1>` (compat spec §1.2). The base
            // form stays for R-lines / hierarchical routing, which never carry SSIDs.
            BbsCallsign = bindCallsign,
            SysopCallsigns = string.IsNullOrWhiteSpace(config.Sysop) ? [] : [config.Sysop],
            Version = version,
        };

        var linkOptions = new RhpLinkOptions
        {
            Host = config.Rhp.Host!,
            Port = config.Rhp.Port!.Value,
            BindCallsign = bindCallsign,
            User = config.Rhp.User,
            Pass = config.Rhp.Pass,
        };

        builder.Services.AddSingleton(sp =>
            new RhpNodeLink(linkOptions, time, sp.GetRequiredService<ILogger<RhpNodeLink>>()));
        builder.Services.AddSingleton(sp =>
            new RoutingService(store, engine, sp.GetRequiredService<ILogger<RoutingService>>()));
        builder.Services.AddSingleton(sp => new InboundMessageReceiver(
            store, sp.GetRequiredService<RoutingService>(), baseCallsign, time,
            sp.GetRequiredService<ILogger<InboundMessageReceiver>>()));
        builder.Services.AddSingleton(sp => new FbbSessionRunner(
            store, sp.GetRequiredService<InboundMessageReceiver>(), identity, version, time,
            sp.GetRequiredService<ILogger<FbbSessionRunner>>()));
        builder.Services.AddSingleton(sp => new ForwardingScheduler(
            sp.GetRequiredService<RhpNodeLink>(), sp.GetRequiredService<FbbSessionRunner>(), store, identity, time,
            sp.GetRequiredService<ILogger<ForwardingScheduler>>()));
        builder.Services.AddSingleton<IUserSettingsStore>(
            new JsonUserSettingsStore(Path.Combine(stateDir, "user-settings.json")));
        builder.Services.AddSingleton(sp => new InboundDemux(
            sp.GetRequiredService<RhpNodeLink>(), store, sp.GetRequiredService<FbbSessionRunner>(),
            sp.GetRequiredService<RoutingService>(), consoleConfig, sp.GetRequiredService<IUserSettingsStore>(), time,
            TimeSpan.FromSeconds(Math.Max(1, config.DemuxFirstLineWaitSeconds)),
            sp.GetRequiredService<ILogger<InboundDemux>>()));
        builder.Services.AddSingleton(sp => new HousekeepingRunner(
            store, new HousekeepingPolicy(), time, sp.GetRequiredService<ILogger<HousekeepingRunner>>()));

        // One ComponentService<T> per component: AddHostedService registers through
        // TryAddEnumerable, which de-duplicates by implementation type — with a single
        // non-generic ComponentService only the FIRST of these four registrations survived,
        // so the demux/forwarding/housekeeping loops silently never ran (inbound connects
        // were accepted by the link and then sat unread forever). The closed generic types
        // are distinct, so every loop is registered. Pinned by HostCompositionTests.
        builder.Services.AddHostedService(sp => new ComponentService<RhpNodeLink>("rhp-link",
            sp.GetRequiredService<RhpNodeLink>(), static (link, ct) => link.RunAsync(ct)));
        builder.Services.AddHostedService(sp => new ComponentService<InboundDemux>("demux",
            sp.GetRequiredService<InboundDemux>(), static (demux, ct) => demux.RunAsync(ct)));
        builder.Services.AddHostedService(sp => new ComponentService<ForwardingScheduler>("forwarding",
            sp.GetRequiredService<ForwardingScheduler>(), static (scheduler, ct) => scheduler.RunAsync(ct)));
        builder.Services.AddHostedService(sp => new ComponentService<HousekeepingRunner>("housekeeping",
            sp.GetRequiredService<HousekeepingRunner>(), static (runner, ct) => runner.RunAsync(ct)));

        WebApplication app = builder.Build();

        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bbs.Host");
        if (createdDefault)
        {
            log.CreatedDefaultConfig(Path.Combine(stateDir, BbsHostConfigFile.FileName));
        }

        if (string.Equals(baseCallsign, BbsHostConfig.PlaceholderCallsign, StringComparison.OrdinalIgnoreCase))
        {
            log.PlaceholderCallsign(BbsHostConfigFile.FileName);
        }

        // Wire the routing → scheduler nudge and sweep the startup backlog (messages stored
        // before a restart that never reached a queue; idempotent for the rest).
        RoutingService routing = app.Services.GetRequiredService<RoutingService>();
        routing.NudgePartner = app.Services.GetRequiredService<ForwardingScheduler>().Nudge;
        routing.RouteStartupBacklog();

        Webmail.Map(app, new WebmailOptions
        {
            Store = store,
            Routing = routing,
            BbsCallsign = baseCallsign,
            SysopCallsign = config.Sysop,
        });

        log.Starting(version, bindCallsign, config.Rhp.Host!, config.Rhp.Port!.Value, config.Web.Bind, config.Web.Port);
        return app;
    }
}

/// <summary>
/// Hosts one named component loop as an <see cref="IHostedService"/>. Generic over the
/// component because <c>AddHostedService</c> de-duplicates registrations by implementation
/// type (it uses <c>TryAddEnumerable</c>) — distinct closed generics keep one service per
/// component where a shared non-generic class would collapse to the first registration.
/// </summary>
internal sealed class ComponentService<TComponent>(
    string name, TComponent component, Func<TComponent, CancellationToken, Task> run) : BackgroundService
    where TComponent : class
{
    /// <summary>The component name (diagnostics).</summary>
    public string Name { get; } = name;

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => run(component, stoppingToken);
}

/// <summary>Startup log messages.</summary>
internal static partial class ProgramLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Created a default {Path} — edit it (callsign, partners) and restart")]
    public static partial void CreatedDefaultConfig(this ILogger logger, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "The BBS callsign is still the N0CALL placeholder — set callsign in {File}")]
    public static partial void PlaceholderCallsign(this ILogger logger, string file);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "pdn-bbs {Version}: callsign {Callsign}, RHP {RhpHost}:{RhpPort}, webmail {WebBind}:{WebPort}")]
    public static partial void Starting(
        this ILogger logger, string version, string callsign, string rhpHost, int rhpPort, string webBind, int webPort);
}
