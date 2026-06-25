using System.Net;
using System.Reflection;
using Bbs.Console;
using Bbs.Core;
using Bbs.Host.Diagnostics;
using Bbs.Host.Forwarding;
using Bbs.Host.Rhp;
using Bbs.Host.Sessions;
using Bbs.Host.Web;
using Bbs.Imap;
using Bbs.Smtp;

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

        // The runtime log-level switch (a singleton consulted at log time). Default = empty, so logging
        // is exactly as appsettings configures it until the sysop raises a category live via /loglevel.
        var logLevelOverrides = new LogLevelOverrides();
        builder.Services.AddSingleton(logLevelOverrides);
        ConfigureDynamicLogging(builder, logLevelOverrides);

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

        // Resolve the primary bind callsign (brief change #1): an explicit non-placeholder
        // callsign wins; otherwise, under pdn (PDN_NODE_CALLSIGN set) derive <node-base>-1 and
        // mark it to probe for a free SSID; standalone keeps the placeholder. The actual bound
        // callsign (after any probe) lives on the link as BoundCallsign — what the link reports
        // when it logs "Bound …". The prompt/identity below use the derived default; the base
        // callsign (routing/R-lines) is SSID-insensitive, so a probe-walked SSID never affects it.
        BbsHostConfigFile.ResolvedCallsign resolved =
            BbsHostConfigFile.ResolveCallsign(config, Environment.GetEnvironmentVariable);
        string bindCallsign = resolved.Callsign;
        string baseCallsign = Callsigns.StripSsid(bindCallsign);
        var time = TimeProvider.System;

        // Webmail binds exactly what the config says (loopback per the app-gateway contract).
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Parse(config.Web.Bind), config.Web.Port));

        // The store's BbsCallsign is the MAIL namespace identity (it generates BIDs, which carry
        // the BBS call SSID-less per compat spec §2.3). A mail address never carries an SSID — the
        // SSID is a connect-level detail of the partner relationship, not the mail identity — so
        // feed the SSID-less base, NOT the SSID'd bind callsign. (BidGenerator strips the SSID
        // internally too; this keeps the layer's own identity correct at the source.)
        var store = BbsStore.Open(Path.Combine(stateDir, "bbs.db"), baseCallsign, time);
        builder.Services.AddSingleton(store); // owned by the host: disposed on shutdown

        // Partners: config is the source of truth (v1) — upsert everything configured, prune the rest.
        HostStartup.SyncPartners(store, config);

        var identity = new BbsIdentity
        {
            Callsign = baseCallsign,
            HRoute = config.HRoute,
            SoftwareVersion = "PDN" + version,
        };

        // The routing engine's own-call is the MAIL namespace identity: it forms our hierarchical
        // @home address (own-call leaf element) and the R-line own-call, both SSID-less (a mail
        // address never carries an SSID — the SSID is a connect-level partner detail). Feeding the
        // SSID'd bind callsign would make our @home leaf `M9YYY-1`, which HierarchicalAddress
        // compares ordinally (no SSID-stripping) — local mail addressed @M9YYY would then miss the
        // local-delivery test and wrongly forward. Routing comparisons still use BaseEquals, so the
        // SSID-less own-call resolves the same partners.
        var engine = new RoutingEngine(baseCallsign, config.HRoute);

        var consoleConfig = new BbsConsoleConfig
        {
            // The console prompt is `de <CALL>>` with the FULL bound callsign incl. SSID —
            // the oracle's own transcript shows `de GB7BPQ-1>` (compat spec §1.2). The base
            // form stays for R-lines / hierarchical routing, which never carry SSIDs.
            BbsCallsign = bindCallsign,
            SysopCallsigns = string.IsNullOrWhiteSpace(config.Sysop) ? [] : [config.Sysop],
            Version = version,
            // For the plain sysop `route` explain — the same own-call + H-Route the live
            // RoutingEngine above is built from, so the trace matches real routing.
            HRoute = config.HRoute,
        };

        var linkOptions = new RhpLinkOptions
        {
            Host = config.Rhp.Host!,
            Port = config.Rhp.Port!.Value,
            BindCallsign = bindCallsign,
            ProbeSsid = resolved.Probe,
            NodeCallsign = resolved.NodeCallsign,
            // Brief change #2: additionally bind the friendly service alias ("BBS" by default; empty
            // disables it) so users can `C BBS`. Inbound to either callsign routes to the same demux.
            ServiceCallsign = string.IsNullOrWhiteSpace(config.ServiceCallsign)
                ? null
                : Callsigns.Normalize(config.ServiceCallsign),
            User = config.Rhp.User,
            Pass = config.Rhp.Pass,
        };

        builder.Services.AddSingleton(sp =>
            new RhpNodeLink(linkOptions, time, sp.GetRequiredService<ILogger<RhpNodeLink>>()));
        builder.Services.AddSingleton(sp =>
            new RoutingService(store, engine, sp.GetRequiredService<ILogger<RoutingService>>()));
        builder.Services.AddSingleton(sp => new SevenPlusAssembler(
            store, sp.GetRequiredService<ILogger<SevenPlusAssembler>>()));
        builder.Services.AddSingleton(sp => new WhitePagesConsumer(
            store, sp.GetRequiredService<ILogger<WhitePagesConsumer>>()));
        builder.Services.AddSingleton(sp => new InboundMessageReceiver(
            store, sp.GetRequiredService<RoutingService>(), engine,
            sp.GetRequiredService<SevenPlusAssembler>(), sp.GetRequiredService<WhitePagesConsumer>(),
            baseCallsign, time,
            sp.GetRequiredService<ILogger<InboundMessageReceiver>>()));
        // Durable scratch area for receiver-side restart granting (issue #38). Sweep abandoned
        // partials at startup so the area cannot grow unbounded across restarts.
        var partialStore = new FileInboundPartialStore(stateDir, time);
        partialStore.CollectStale();
        builder.Services.AddSingleton(partialStore);
        // The whole-BBS forwarding master switch is persisted in the store and read live by the
        // scheduler + inbound answerer (so the sysop's runtime toggle survives a restart). Seed it from
        // bbs.yaml's forwarding.enabled on first start ONLY — once set, the persisted value wins, like
        // partners do (a later bbs.yaml edit doesn't silently clobber a runtime toggle).
        if (store.GetForwardingMaster() is null)
        {
            store.SetForwardingMaster(config.Forwarding.Enabled);
        }

        builder.Services.AddSingleton(sp => new FbbSessionRunner(
            store, sp.GetRequiredService<InboundMessageReceiver>(), identity, version, time,
            sp.GetRequiredService<ILogger<FbbSessionRunner>>(), partialStore));
        builder.Services.AddSingleton(sp => new ForwardingScheduler(
            sp.GetRequiredService<RhpNodeLink>(), sp.GetRequiredService<FbbSessionRunner>(), store, identity, time,
            sp.GetRequiredService<ILogger<ForwardingScheduler>>()));
        // The sysop "test connect" tool — same RHP open path as the scheduler cycle, but no FBB
        // session and no queue (so it cannot move mail). Reuses the live link.
        builder.Services.AddSingleton(sp => new ForwardingTester(
            sp.GetRequiredService<RhpNodeLink>(), store, time, sp.GetRequiredService<ILogger<ForwardingTester>>()));
        builder.Services.AddSingleton<IUserSettingsStore>(
            new JsonUserSettingsStore(Path.Combine(stateDir, "user-settings.json")));
        builder.Services.AddSingleton(sp => new InboundDemux(
            sp.GetRequiredService<RhpNodeLink>(), store, sp.GetRequiredService<FbbSessionRunner>(),
            sp.GetRequiredService<RoutingService>(), consoleConfig, sp.GetRequiredService<IUserSettingsStore>(),
            version, time,
            TimeSpan.FromSeconds(Math.Max(1, config.DemuxFirstLineWaitSeconds)),
            sp.GetRequiredService<ILogger<InboundDemux>>()));
        builder.Services.AddSingleton(sp => new HousekeepingRunner(
            store, config.Housekeeping.ToPolicy(), time, sp.GetRequiredService<ILogger<HousekeepingRunner>>()));
        builder.Services.AddSingleton(sp => new PendingSendReleaser(
            store, sp.GetRequiredService<RoutingService>(), time, sp.GetRequiredService<ILogger<PendingSendReleaser>>()));

        // The optional IMAP server (default off): registered only when enabled, so a node that does
        // not configure it constructs nothing and behaves exactly as before. The self-signed TLS cert
        // (if used) persists alongside the db in the state dir.
        if (config.Imap.Enabled)
        {
            var imapOptions = new ImapServerOptions
            {
                Bind = config.Imap.Bind,
                Port = config.Imap.Port,
                TlsEnabled = config.Imap.Tls.Enabled,
                CertificatePath = config.Imap.Tls.CertificatePath,
                CertificatePassword = config.Imap.Tls.CertificatePassword,
                GenerateSelfSigned = config.Imap.Tls.GenerateSelfSigned,
                SelfSignedCertPath = Path.Combine(stateDir, "imap-cert.pfx"),
            };
            builder.Services.AddSingleton(sp => new ImapServer(
                imapOptions, store, time, sp.GetRequiredService<ILogger<ImapServer>>()));
        }

        // The optional SMTP submission server (default off): registered only when enabled, so a node
        // that does not configure it constructs nothing and behaves exactly as before. The library is
        // host-agnostic, so the host supplies the post-store nudge — RoutingService.RouteMessage, the
        // same routing path webmail compose uses — as the onStored callback. The self-signed TLS cert
        // (if used) persists alongside the db in the state dir.
        if (config.Smtp.Enabled)
        {
            var smtpOptions = new SmtpServerOptions
            {
                Bind = config.Smtp.Bind,
                Port = config.Smtp.Port,
                // STARTTLS on a second port (587 by default, 0 to disable) — the single SmtpServer runs both
                // accept loops; we do NOT register a second SmtpServer (ComponentService<T> de-dups by type).
                StartTlsPort = config.Smtp.StartTlsPort,
                TlsEnabled = config.Smtp.Tls.Enabled,
                CertificatePath = config.Smtp.Tls.CertificatePath,
                CertificatePassword = config.Smtp.Tls.CertificatePassword,
                GenerateSelfSigned = config.Smtp.Tls.GenerateSelfSigned,
                SelfSignedCertPath = Path.Combine(stateDir, "smtp-cert.pfx"),
            };
            builder.Services.AddSingleton(sp =>
            {
                RoutingService smtpRouting = sp.GetRequiredService<RoutingService>();
                return new SmtpServer(
                    smtpOptions, store, smtpRouting.RouteMessage, time, sp.GetRequiredService<ILogger<SmtpServer>>());
            });
        }

        // The inbound FBB-over-TCP listener (BPQ FBBPORT, issue #40): registered only when the
        // (default-off) feature is enabled, so a node that does not configure it constructs nothing
        // and behaves exactly as before. It shares the FbbSessionRunner (and thus the FBB FSM) with
        // the AX.25/RHP inbound path — no protocol duplication.
        if (config.FbbTcp.Enabled)
        {
            builder.Services.AddSingleton(sp => new FbbTcpListener(
                config.FbbTcp, store, sp.GetRequiredService<FbbSessionRunner>(), version, time,
                sp.GetRequiredService<ILogger<FbbTcpListener>>()));
        }

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
        builder.Services.AddHostedService(sp => new ComponentService<PendingSendReleaser>("pending-send",
            sp.GetRequiredService<PendingSendReleaser>(), static (releaser, ct) => releaser.RunAsync(ct)));

        // The IMAP accept loop is hosted only when the (default-off) IMAP server was registered above.
        if (config.Imap.Enabled)
        {
            builder.Services.AddHostedService(sp => new ComponentService<ImapServer>("imap",
                sp.GetRequiredService<ImapServer>(), static (server, ct) => server.RunAsync(ct)));
        }

        // The SMTP accept loop is hosted only when the (default-off) SMTP server was registered above.
        if (config.Smtp.Enabled)
        {
            builder.Services.AddHostedService(sp => new ComponentService<SmtpServer>("smtp",
                sp.GetRequiredService<SmtpServer>(), static (server, ct) => server.RunAsync(ct)));
        }

        // The inbound FBB-over-TCP accept loop is hosted only when the (default-off) listener was
        // registered above (issue #40).
        if (config.FbbTcp.Enabled)
        {
            builder.Services.AddHostedService(sp => new ComponentService<FbbTcpListener>("fbb-tcp",
                sp.GetRequiredService<FbbTcpListener>(), static (listener, ct) => listener.RunAsync(ct)));
        }

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
        else if (resolved.Probe)
        {
            // The callsign was derived from the node (PDN_NODE_CALLSIGN). Log it clearly; the link
            // logs the FINAL bound callsign when it binds (after any free-SSID probe).
            log.DerivedCallsign(bindCallsign, resolved.NodeCallsign ?? "", BbsHostConfigFile.FileName);
        }

        // Wire the routing → scheduler nudge and sweep the startup backlog (messages stored
        // before a restart that never reached a queue; idempotent for the rest).
        RoutingService routing = app.Services.GetRequiredService<RoutingService>();
        ForwardingScheduler scheduler = app.Services.GetRequiredService<ForwardingScheduler>();
        ForwardingTester forwardingTester = app.Services.GetRequiredService<ForwardingTester>();
        routing.NudgePartner = scheduler.Nudge;
        routing.RouteStartupBacklog();

        Webmail.Map(app, new WebmailOptions
        {
            Store = store,
            Routing = routing,
            // Store-first forwarding editor: after any partner mutation (forms or YAML) ask the
            // scheduler to (re)scan so a new/enabled partner gets a loop immediately and a
            // deleted/disabled one is reaped — without waiting for the periodic re-sweep.
            OnPartnersChanged = scheduler.Reconcile,
            // "Forward now" reuses the existing nudge seam (RoutingService.NudgePartner == Nudge).
            OnForwardNow = scheduler.Nudge,
            // Sysop "test connect": validate a partner WITHOUT moving mail (no FBB session, no queue).
            TestConnect = forwardingTester.TestConnectAsync,
            // The live "test to here" step-editor probe (SSE) — dial the draft, replay to a step, stream.
            ProbeStream = forwardingTester.ProbeStreamToAsync,
            // The same per-user settings singleton the console session uses — a webmail
            // interface-mode flip is the persisted choice the next console connect reads.
            Settings = app.Services.GetRequiredService<IUserSettingsStore>(),
            // The webmail title/header shows the STATION identity — the SSID'd connect callsign you
            // connect to (e.g. M9YYY-1), the same identity as the console prompt and RHP bind. It is
            // NOT the mail-namespace own-call: users' mail addresses (M0LTE@M9YYY.#42.GBR.EURO), BIDs
            // and R-lines stay SSID-less and come from the store/engine own-call (baseCallsign) above.
            StationCallsign = bindCallsign,
            SysopCallsign = config.Sysop,
            // Machine-readable health (/healthz, /status.json): the running version + an uptime clock.
            Version = version,
            StartedUtc = time.GetUtcNow(),
            Clock = time,
            // The runtime log-level switch backing /loglevel (the same singleton the provider reads).
            LogLevels = logLevelOverrides,
        });

        log.Starting(version, bindCallsign, config.Rhp.Host!, config.Rhp.Port!.Value, config.Web.Bind, config.Web.Port);
        return app;
    }

    /// <summary>
    /// Wires the runtime log-level switch into the logging pipeline as a live filter that consults
    /// <paramref name="overrides"/> on every <c>IsEnabled</c>/<c>Log</c> decision. This is the
    /// "<c>LoggingLevelSwitch</c>"-style mechanism: a singleton holding category→level overrides read
    /// at log time, so raising <c>Bbs.Host.*</c> to Debug/Trace takes effect immediately — no restart
    /// and no <c>appsettings.json</c> edit (read-only under <c>ProtectSystem=strict</c> in production).
    /// </summary>
    /// <remarks>
    /// Implemented as a global filter delegate rather than a provider decorator: the standard
    /// <see cref="LoggerFactory"/> evaluates filters when computing <c>ILogger.IsEnabled</c>, so an
    /// override flips <c>IsEnabled</c> (and thus actual delivery) for every provider at once, live. The
    /// delegate is the LEAST-specific rule, so any more-specific <c>Logging:LogLevel</c> config entry
    /// (e.g. <c>Microsoft.AspNetCore: Warning</c>) still wins for its categories; we only supply the
    /// baseline (the configured default minimum) PLUS the override raise. With an empty override set the
    /// baseline alone reproduces today's behaviour exactly.
    /// </remarks>
    internal static void ConfigureDynamicLogging(WebApplicationBuilder builder, LogLevelOverrides overrides)
    {
        // The configured default minimum (Logging:LogLevel:Default), so the filter's baseline matches
        // appsettings; absent/unparseable falls back to Information (the framework default).
        LogLevel defaultMinimum =
            Enum.TryParse(builder.Configuration["Logging:LogLevel:Default"], ignoreCase: true, out LogLevel configured)
                ? configured
                : LogLevel.Information;

        builder.Logging.AddFilter((category, level) =>
        {
            // An override RAISES verbosity for a matching category prefix (longest match wins); it never
            // silences below the baseline. None means "off".
            if (level != LogLevel.None && category is { } cat && overrides.ResolveFor(cat) is { } min && level >= min)
            {
                return true;
            }

            // Baseline: the configured default minimum. More-specific Logging:LogLevel rules are
            // more-specific than this delegate and so still take precedence for their categories.
            return level != LogLevel.None && level >= defaultMinimum;
        });
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

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "BBS callsign {Callsign} derived from the node ({NodeCallsign}); the RHP link probes for a free SSID if it is taken. Set callsign in {File} to pin a different identity")]
    public static partial void DerivedCallsign(this ILogger logger, string callsign, string nodeCallsign, string file);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "pdn-bbs {Version}: callsign {Callsign}, RHP {RhpHost}:{RhpPort}, webmail {WebBind}:{WebPort}")]
    public static partial void Starting(
        this ILogger logger, string version, string callsign, string rhpHost, int rhpPort, string webBind, int webPort);
}
