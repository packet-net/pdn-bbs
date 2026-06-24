using System.Net;
using System.Text;
using System.Text.Json;
using Bbs.Console;
using Bbs.Core;
using Bbs.Host;
using Bbs.Host.Diagnostics;
using Bbs.Host.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Host.Tests;

/// <summary>
/// The two observability surfaces: machine-readable health (/healthz, /status.json) and the runtime
/// log-level switch (/loglevel + the live filter wired by HostComposition.ConfigureDynamicLogging).
/// </summary>
public sealed class ObservabilityTests : IAsyncDisposable
{
    private const string Sysop = "G0SYS";

    private readonly DirectoryInfo _dir;
    private readonly FakeTimeProvider _time;
    private readonly BbsStore _store;
    private readonly RoutingService _routing;
    private readonly InMemoryUserSettingsStore _settings = new();
    private WebApplication? _app;

    public ObservabilityTests()
    {
        _dir = Directory.CreateTempSubdirectory("bbs-observability-test-");
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));
        _store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), "GB7PDN", _time);
        _routing = new RoutingService(
            _store, new RoutingEngine("GB7PDN", "#23.GBR.EURO"), Microsoft.Extensions.Logging.Abstractions.NullLogger<RoutingService>.Instance);
    }

    // --------------------------------------------------------------- /healthz

    [Fact]
    public async Task Healthz_Unauthenticated_ReturnsOkWithVersionAndUptime()
    {
        // Started 30s before "now" on the fake clock → uptimeSeconds == 30, deterministically.
        using HttpClient client = await StartAsync(
            startedUtc: _time.GetUtcNow() - TimeSpan.FromSeconds(30), version: "1.2.3", withGatewayHeader: false, withUser: false);

        HttpResponseMessage response = await client.GetAsync(new Uri("/healthz", UriKind.Relative));

        // No gateway header, no X-Pdn-User — yet 200: the probe deliberately skips the gateway gate.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal(30, root.GetProperty("uptimeSeconds").GetInt64());
    }

    // --------------------------------------------------------------- /status.json

    [Fact]
    public async Task StatusJson_NonSysop_Gets403()
    {
        ClaimCallsign("tom", "M0LTE"); // not the sysop
        using HttpClient client = await StartAsync(pdnUser: "tom");

        HttpResponseMessage response = await client.GetAsync(new Uri("/status.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StatusJson_Sysop_ReportsSeededPartnerQueueDepthAgeAndLastForwarded()
    {
        ClaimCallsign("tom", Sysop);
        _store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });

        // The fake clock only moves forward, so build the timeline forward from the harness base (12:00):
        //   12:00  forward a leg (success) → forwarded_utc = 12:00
        //   12:45  queue the OLDEST still-waiting leg
        //   13:20  queue a NEWER still-waiting leg + record a dial outcome
        //   13:30  "now" when /status.json is read
        // ⇒ oldestQueuedAgeMins = 13:30 - 12:45 = 45; lastForwardedUtc = 12:00; queueDepth = 2.
        Message done = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal, From = "M0LTE", Recipients = ["G8ABC"], At = "GB7RDG",
            Subject = "DONE", Body = Encoding.Latin1.GetBytes("y\r"),
        });
        _store.EnqueueForwards(done.Number, ["GB7RDG"]);
        _store.MarkForwarded(done.Number, "GB7RDG"); // forwarded_utc = 12:00

        _time.Advance(TimeSpan.FromMinutes(45)); // 12:45
        Message waitingOld = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal, From = "M0LTE", Recipients = ["M0LTE"], At = "GB7RDG",
            Subject = "OLD", Body = Encoding.Latin1.GetBytes("x\r"),
        });
        _store.EnqueueForwards(waitingOld.Number, ["GB7RDG"]);

        _time.Advance(TimeSpan.FromMinutes(35)); // 13:20
        Message waitingNew = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal, From = "M0LTE", Recipients = ["2E0AAA"], At = "GB7RDG",
            Subject = "NEW", Body = Encoding.Latin1.GetBytes("z\r"),
        });
        _store.EnqueueForwards(waitingNew.Number, ["GB7RDG"]);
        _store.RecordForwardingSuccess("GB7RDG", mode: "B2"); // forwarding_status: ok, 0 failures

        _time.Advance(TimeSpan.FromMinutes(10)); // 13:30 — the read instant

        using HttpClient client = await StartAsync(pdnUser: "tom", version: "9.9.9");
        HttpResponseMessage response = await client.GetAsync(new Uri("/status.json", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal("9.9.9", root.GetProperty("version").GetString());

        JsonElement partner = Assert.Single(root.GetProperty("partners").EnumerateArray());
        Assert.Equal("GB7RDG", partner.GetProperty("call").GetString());
        Assert.True(partner.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, partner.GetProperty("queueDepth").GetInt32());
        Assert.Equal(45, partner.GetProperty("oldestQueuedAgeMins").GetInt64());
        Assert.True(partner.GetProperty("ok").GetBoolean());
        Assert.Equal(0, partner.GetProperty("consecutiveFailures").GetInt32());

        // lastForwardedUtc is the SUCCESS timestamp (max forwarded_utc) = 12:00Z, ISO-8601.
        DateTime lastForwarded = partner.GetProperty("lastForwardedUtc").GetDateTime();
        Assert.Equal(new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc), lastForwarded.ToUniversalTime());
    }

    [Fact]
    public async Task StatusJson_Sysop_WaitingFieldsNullWhenQueueEmpty()
    {
        ClaimCallsign("tom", Sysop);
        _store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });

        using HttpClient client = await StartAsync(pdnUser: "tom");
        HttpResponseMessage response = await client.GetAsync(new Uri("/status.json", UriKind.Relative));

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement partner = Assert.Single(doc.RootElement.GetProperty("partners").EnumerateArray());
        Assert.Equal(0, partner.GetProperty("queueDepth").GetInt32());
        Assert.Equal(JsonValueKind.Null, partner.GetProperty("oldestQueuedAgeMins").ValueKind);
        Assert.Equal(JsonValueKind.Null, partner.GetProperty("lastForwardedUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, partner.GetProperty("lastAttemptUtc").ValueKind);
    }

    // --------------------------------------------------------------- /loglevel (HTTP)

    [Fact]
    public async Task LogLevel_NonSysop_Gets403()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync(pdnUser: "tom");

        HttpResponseMessage response = await client.GetAsync(new Uri("/loglevel", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LogLevel_Sysop_SetThenClear_RoundTripsThroughTheStore()
    {
        ClaimCallsign("tom", Sysop);
        var overrides = new LogLevelOverrides();
        using HttpClient client = await StartAsync(pdnUser: "tom", logLevels: overrides);

        // GET: no overrides yet.
        using (JsonDocument get0 = JsonDocument.Parse(await client.GetStringAsync(new Uri("/loglevel", UriKind.Relative))))
        {
            Assert.True(get0.RootElement.GetProperty("available").GetBoolean());
            Assert.Empty(get0.RootElement.GetProperty("overrides").EnumerateObject());
        }

        // POST set Bbs.Host -> Debug.
        HttpResponseMessage set = await client.PostAsync(new Uri("/loglevel", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["category"] = "Bbs.Host", ["level"] = "Debug" }));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(LogLevel.Debug, overrides.ResolveFor("Bbs.Host.Forwarding.X"));

        // GET reflects it.
        using (JsonDocument get1 = JsonDocument.Parse(await client.GetStringAsync(new Uri("/loglevel", UriKind.Relative))))
        {
            Assert.Equal("Debug", get1.RootElement.GetProperty("overrides").GetProperty("Bbs.Host").GetString());
        }

        // POST clear.
        HttpResponseMessage clear = await client.PostAsync(new Uri("/loglevel", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["category"] = "Bbs.Host", ["action"] = "clear" }));
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null(overrides.ResolveFor("Bbs.Host.Forwarding.X"));
    }

    [Fact]
    public async Task LogLevel_Sysop_RejectsInvalidLevel()
    {
        ClaimCallsign("tom", Sysop);
        using HttpClient client = await StartAsync(pdnUser: "tom", logLevels: new LogLevelOverrides());

        HttpResponseMessage response = await client.PostAsync(new Uri("/loglevel", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["category"] = "Bbs.Host", ["level"] = "Loud" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --------------------------------------------------------------- live IsEnabled (real wiring)

    [Fact]
    public void DynamicLogging_OverrideRaisesIsEnabledLive_ThenClears()
    {
        // Exercise the REAL production wiring: HostComposition.ConfigureDynamicLogging installs the
        // override filter into a standard logging pipeline. appsettings default = Information, so Debug
        // is off until an override raises Bbs.Host live. A capturing provider stands in for the console
        // one so we can also prove a raised message is actually DELIVERED, not just IsEnabled-true.
        var overrides = new LogLevelOverrides();
        var captured = new CapturingProvider();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(captured);
        HostComposition.ConfigureDynamicLogging(builder, overrides);
        using WebApplication app = builder.Build();

        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bbs.Host.Forwarding.ForwardingScheduler");

        // Baseline: Information on, Debug off.
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Emit(logger, LogLevel.Debug, "before");
        Assert.DoesNotContain("before", captured.Messages);

        // Raise Bbs.Host -> Trace live: same logger instance now reports Debug AND Trace enabled, and
        // a Debug record is delivered.
        overrides.Set("Bbs.Host", LogLevel.Trace);
        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Trace));
        Emit(logger, LogLevel.Debug, "after");
        Assert.Contains("after", captured.Messages);

        // An unrelated category is untouched by the Bbs.Host override.
        ILogger other = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Some.Other.Category");
        Assert.False(other.IsEnabled(LogLevel.Debug));

        // Clear: Debug/Trace go off again (back to the configured default).
        overrides.ClearAll();
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Information));
    }

    /// <summary>
    /// Emits a log record via the raw <see cref="ILogger.Log{TState}"/> entry point (not the
    /// <c>LogDebug</c>/<c>LogInformation</c> convenience extensions, which CA1848 forbids under
    /// warnings-as-errors). The factory's filters — and thus the live override — still gate delivery.
    /// </summary>
    private static void Emit(ILogger logger, LogLevel level, string message) =>
        logger.Log(level, new EventId(0), message, null, static (state, _) => state);

    /// <summary>A minimal in-memory logger provider that records rendered messages (stands in for console).</summary>
    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            // The factory applies the configured filters before calling Log; the provider itself is
            // permissive so the filter (and thus the override) is what decides delivery.
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add(formatter(state, exception));
                }
            }
        }
    }

    // --------------------------------------------------------------- harness

    private void ClaimCallsign(string pdnUser, string callsign) =>
        _store.UpsertUser(new User { Callsign = callsign, PdnUsername = pdnUser });

    private async Task<HttpClient> StartAsync(
        string pdnUser = "tom",
        bool withGatewayHeader = true,
        bool withUser = true,
        string version = "0.0.0",
        DateTimeOffset? startedUtc = null,
        LogLevelOverrides? logLevels = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        Webmail.Map(_app, new WebmailOptions
        {
            Store = _store,
            Routing = _routing,
            Settings = _settings,
            StationCallsign = "GB7PDN-1",
            SysopCallsign = Sysop,
            Version = version,
            StartedUtc = startedUtc ?? _time.GetUtcNow(),
            Clock = _time,
            LogLevels = logLevels,
        });
        await _app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        if (withGatewayHeader)
        {
            client.DefaultRequestHeaders.Add("X-Pdn-Gateway", "1");
        }

        if (withUser)
        {
            client.DefaultRequestHeaders.Add("X-Pdn-User", pdnUser);
        }

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _store.Dispose();
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
