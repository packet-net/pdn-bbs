using System.Net;
using System.Reflection;
using Bbs.Console;
using Bbs.Core;
using Bbs.Host;
using Bbs.Host.Forwarding;
using Bbs.Host.Rhp;
using Bbs.Host.Sessions;
using Bbs.Host.Web;

// pdn-bbs — the deployable BBS app package (design.md src/Bbs.Host): RHPv2 attachment +
// inbound demux + forwarding scheduler + webmail + housekeeping, composed over the
// Fbb/Core/Console libraries. State (db + config) lives in $PDN_APP_STATE.

var builder = WebApplication.CreateBuilder(args);

string stateDir = Environment.GetEnvironmentVariable("PDN_APP_STATE") is { Length: > 0 } s
    ? s
    : Directory.GetCurrentDirectory();
Directory.CreateDirectory(stateDir);

(BbsHostConfig config, bool createdDefault) =
    BbsHostConfigFile.LoadOrCreate(stateDir, Environment.GetEnvironmentVariable);

string version = typeof(Program).Assembly
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
    BbsCallsign = baseCallsign,
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

builder.Services.AddHostedService(sp => new ComponentService("rhp-link",
    ct => sp.GetRequiredService<RhpNodeLink>().RunAsync(ct)));
builder.Services.AddHostedService(sp => new ComponentService("demux",
    ct => sp.GetRequiredService<InboundDemux>().RunAsync(ct)));
builder.Services.AddHostedService(sp => new ComponentService("forwarding",
    ct => sp.GetRequiredService<ForwardingScheduler>().RunAsync(ct)));
builder.Services.AddHostedService(sp => new ComponentService("housekeeping",
    ct => sp.GetRequiredService<HousekeepingRunner>().RunAsync(ct)));

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
app.Run();

/// <summary>Hosts one named component loop as an <see cref="IHostedService"/>.</summary>
internal sealed class ComponentService(string name, Func<CancellationToken, Task> run) : BackgroundService
{
    /// <summary>The component name (diagnostics).</summary>
    public string Name { get; } = name;

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => run(stoppingToken);
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
