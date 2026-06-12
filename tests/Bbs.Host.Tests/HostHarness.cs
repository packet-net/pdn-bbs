using Bbs.Console;
using Bbs.Core;
using Bbs.Host.Forwarding;
using Bbs.Host.Rhp;
using Bbs.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Host.Tests;

/// <summary>
/// The composed host under test: real components (link, demux, runner, scheduler,
/// routing) against a <see cref="FakeRhpServer"/>, a temp-dir store and a
/// <see cref="FakeTimeProvider"/>. Loops start on demand; dispose tears everything down.
/// </summary>
internal sealed class HostHarness : IAsyncDisposable
{
    public const string OwnCall = "GB7PDN";
    public const string SysopCall = "M0LTE";
    public const string HRoute = "#23.GBR.EURO";
    public const string Version = "0.1.0";

    private readonly DirectoryInfo _dir;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];

    public HostHarness(TimeSpan? firstLineWait = null)
    {
        _dir = Directory.CreateTempSubdirectory("bbs-host-test-");
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));
        Server = new FakeRhpServer();
        Server.Start();

        Store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), OwnCall, Time);
        Identity = new BbsIdentity { Callsign = OwnCall, HRoute = HRoute, SoftwareVersion = "PDN" + Version };
        Engine = new RoutingEngine(OwnCall, HRoute);
        Routing = new RoutingService(Store, Engine, NullLogger<RoutingService>.Instance);
        Receiver = new InboundMessageReceiver(Store, Routing, Engine, OwnCall, Time, NullLogger<InboundMessageReceiver>.Instance);
        Runner = new FbbSessionRunner(Store, Receiver, Identity, Version, Time, NullLogger<FbbSessionRunner>.Instance);
        Link = new RhpNodeLink(
            new RhpLinkOptions
            {
                Host = "127.0.0.1",
                Port = Server.Port,
                BindCallsign = OwnCall,
            },
            Time,
            NullLogger<RhpNodeLink>.Instance);
        UserSettings = new InMemoryUserSettingsStore();
        Demux = new InboundDemux(
            Link,
            Store,
            Runner,
            Routing,
            new BbsConsoleConfig
            {
                BbsCallsign = OwnCall,
                SysopCallsigns = [SysopCall],
                Version = Version,
            },
            UserSettings,
            Version,
            Time,
            firstLineWait ?? TimeSpan.FromSeconds(30),
            NullLogger<InboundDemux>.Instance);
    }

    public FakeTimeProvider Time { get; }

    public FakeRhpServer Server { get; }

    public BbsStore Store { get; }

    public BbsIdentity Identity { get; }

    public RoutingEngine Engine { get; }

    public RoutingService Routing { get; }

    public InboundMessageReceiver Receiver { get; }

    public FbbSessionRunner Runner { get; }

    public RhpNodeLink Link { get; }

    public InboundDemux Demux { get; }

    public IUserSettingsStore UserSettings { get; }

    public ForwardingScheduler? Scheduler { get; private set; }

    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Starts the link loop and waits until it has bound + listened on the fake node.
    /// The <see cref="FakeRhpServer.Binds"/> channel is left untouched for assertions.
    /// </summary>
    public async Task StartLinkAsync()
    {
        _loops.Add(Link.RunAsync(_cts.Token));
        await Server.WaitForListenAsync().ConfigureAwait(false);
    }

    /// <summary>Starts the inbound demux loop.</summary>
    public void StartDemux() => _loops.Add(Demux.RunAsync(_cts.Token));

    /// <summary>
    /// Creates and starts the forwarding scheduler (call after seeding partners — it
    /// snapshots the enabled-partner set, like Program does) and wires the routing nudge.
    /// </summary>
    public void StartScheduler()
    {
        Scheduler = new ForwardingScheduler(Link, Runner, Store, Identity, Time, NullLogger<ForwardingScheduler>.Instance);
        Routing.NudgePartner = Scheduler.Nudge;
        _loops.Add(Scheduler.RunAsync(_cts.Token));
    }

    /// <summary>
    /// Advances fake time in steps until <paramref name="condition"/> holds (each step
    /// also yields real time so continuations run). For timer-driven paths whose start
    /// races the advance.
    /// </summary>
    public async Task AdvanceUntilAsync(TimeSpan step, Func<Task<bool>> condition, int maxSteps = 200)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            Time.Advance(step);
            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("Condition not reached while advancing fake time.");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort teardown; loop faults surface in the tests themselves.
        }

        await Link.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
        Store.Dispose();
        _cts.Dispose();
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
