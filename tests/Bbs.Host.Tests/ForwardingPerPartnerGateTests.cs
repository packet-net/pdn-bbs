using System.Text;
using Bbs.Core;
using Bbs.Host.Forwarding;
using Bbs.Host.Rhp;

namespace Bbs.Host.Tests;

/// <summary>
/// The per-partner forwarding gate is the existing <see cref="Partner.Enabled"/> flag, and disabling a
/// partner stops it in BOTH directions: it dials no one (the scheduler skips/aborts its loop) AND
/// refuses inbound FBB sessions. This is the controlled-cutover gate — the importer lands every
/// partner disabled, and the sysop test-connects then enables each. Disabling a partner whose loop is
/// already parked aborts before it dials one more time (the after-wait re-check).
/// </summary>
public class ForwardingPerPartnerGateTests
{
    private const string PeerSid = "[BPQ-6.0.24.44-B1FHM$]";

    private static Partner Partner(string call, bool enabled) => new()
    {
        Call = call,
        Enabled = enabled,
        AtCalls = ["*"],
        ConnectScript = [$"C {call}-1"],
        ForwardNewImmediately = true,
    };

    private static void QueueTo(HostHarness host, string subject)
    {
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = subject,
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        host.Routing.RouteMessage(stored);
    }

    [Fact]
    public async Task Inbound_ConfiguredButDisabledPartner_Refused_NoMailMoves()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(Partner("GB7XYZ", enabled: false)); // configured, disabled
        var conn = new RecordingFbbConn("GB7XYZ-1");                 // base-matches GB7XYZ
        FbbSessionResult result = await host.Runner.RunAnswererAsync(conn, [], host.Token);

        Assert.False(result.Completed);
        Assert.Equal(0, conn.SendCount);    // refused before any exchange — both directions gated
        Assert.Equal(0, conn.ReceiveCount);
    }

    [Fact]
    public async Task Inbound_EnabledPartner_Engages()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(Partner("GB7XYZ", enabled: true));
        var conn = new RecordingFbbConn("GB7XYZ-1");
        await host.Runner.RunAnswererAsync(conn, [], host.Token);
        Assert.True(conn.SendCount > 0 || conn.ReceiveCount > 0); // enabled → it engaged the wire
    }

    [Fact]
    public async Task Inbound_UnknownTemporaryCaller_StillEngages()
    {
        // The per-partner gate is scoped to KNOWN partners: an unknown caller (no partner record) keeps
        // its LinBPQ "temporary BBS" behaviour. (The whole-BBS hold is the lever for refusing those.)
        await using var host = new HostHarness();
        var conn = new RecordingFbbConn("GB0UNK-1"); // no partner record
        await host.Runner.RunAnswererAsync(conn, [], host.Token);
        Assert.True(conn.SendCount > 0 || conn.ReceiveCount > 0);
    }

    [Fact]
    public async Task Disable_WhileLoopParked_AbortsBeforeDialling()
    {
        await using var host = new HostHarness();
        await host.StartLinkAsync();
        host.StartScheduler(enabled: true);

        // Enabled with NO queued mail: the loop spins and PARKS at its wait (no dial — empty queue).
        host.Store.UpsertPartner(Partner("GB7DIS", enabled: true));
        host.Scheduler!.Reconcile();
        await Task.Delay(40); // let the loop spin up and reach its parked wait

        // DISABLE while parked, THEN queue mail + nudge: the after-wait gate re-check must abort before
        // dialling. The top-of-loop check alone would miss a disable that lands during the wait — the
        // nudge would wake the loop straight into a dial. Clean per-partner abort.
        host.Store.UpsertPartner(Partner("GB7DIS", enabled: false));
        host.Scheduler!.Reconcile();
        QueueTo(host, "after-disable");
        host.Scheduler!.Nudge("GB7DIS");
        for (int i = 0; i < 5; i++)
        {
            host.Time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(10);
        }

        Assert.Equal(0, host.Server.OpenAttempts); // never dialled — disable aborted the parked loop
    }

    [Fact]
    public async Task MasterOff_AtRuntime_AbortsAParkedLoopBeforeDialling()
    {
        await using var host = new HostHarness();
        await host.StartLinkAsync();
        host.StartScheduler(enabled: true); // master ON

        // Enabled partner, no mail → its loop spins and parks.
        host.Store.UpsertPartner(Partner("GB7MAS", enabled: true));
        host.Scheduler!.Reconcile();
        await Task.Delay(40);

        // Flip the whole-BBS MASTER off at runtime, then queue mail + nudge: the loop's after-wait
        // re-check reads the live master (store-backed) and aborts before dialling. The whole-BBS hold
        // beats per-partner enable.
        host.Store.SetForwardingMaster(false);
        host.Scheduler!.Reconcile();
        QueueTo(host, "after-master-off");
        host.Scheduler!.Nudge("GB7MAS");
        for (int i = 0; i < 5; i++)
        {
            host.Time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(10);
        }

        Assert.Equal(0, host.Server.OpenAttempts); // never dialled — master-off held everything
    }

    [Fact]
    public async Task MasterOff_RefusesInbound_EvenForAnEnabledPartner()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(Partner("GB7XYZ", enabled: true)); // partner enabled...
        host.Store.SetForwardingMaster(false);                      // ...but the master is OFF
        var conn = new RecordingFbbConn("GB7XYZ-1");
        FbbSessionResult result = await host.Runner.RunAnswererAsync(conn, [], host.Token);

        Assert.False(result.Completed);
        Assert.Equal(0, conn.SendCount);    // master-off refuses inbound regardless of per-partner enable
        Assert.Equal(0, conn.ReceiveCount);
    }

    private sealed class RecordingFbbConn(string remote) : IFbbConnection
    {
        public string RemoteCallsign { get; } = remote;
        public int SendCount { get; private set; }
        public int ReceiveCount { get; private set; }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.CompletedTask;
        }

        public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveCount++;
            return ValueTask.FromResult<byte[]?>(null); // stream closed → ends the session
        }

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
