using System.Text;
using Bbs.Core;
using Bbs.Host.Forwarding;
using Bbs.Host.Rhp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Host.Tests;

/// <summary>
/// The forwarding-hold safe-abort window (BbsHostConfig.Forwarding.Enabled = false): a freshly
/// migrated node loads the full mailbox but must touch the network in NEITHER direction until
/// forwarding is deliberately enabled (this migration has no rollback once it forwards).
/// </summary>
public class ForwardingHoldTests
{
    private const string PeerSid = "[BPQ-6.0.24.44-B1FHM$]";

    [Fact]
    public async Task Outbound_Held_DoesNotDialEvenAnEnabledPartnerWithQueuedMail()
    {
        await using var host = new HostHarness();
        await host.StartLinkAsync();
        host.StartScheduler(enabled: false); // HELD

        // An enabled partner with mail queued to it: normally this dials immediately.
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7HLD",
            AtCalls = ["*"],
            ConnectScript = ["C GB7HLD-1"],
            ForwardNewImmediately = true,
        });
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "held",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        host.Routing.RouteMessage(stored);
        host.Scheduler!.Reconcile();        // the signal that would spin a dialing loop
        host.Scheduler!.Nudge("GB7HLD");    // and an explicit immediate nudge

        // Advance well past any forwarding interval; a running scheduler would have dialled by now.
        for (int i = 0; i < 5; i++)
        {
            host.Time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(10);
        }

        Assert.Equal(0, host.Server.OpenAttempts); // never dialled — outbound is held
    }

    [Fact]
    public async Task Inbound_Held_RefusesTheFbbAnswererWithoutEngaging()
    {
        await using var host = new HostHarness();
        var held = new FbbSessionRunner(
            host.Store, host.Receiver, host.Identity, "TEST", host.Time,
            NullLogger<FbbSessionRunner>.Instance, partialStore: null, forwardingEnabled: false);

        var conn = new RecordingFbbConnection("GB7XYZ-1");
        FbbSessionResult result = await held.RunAnswererAsync(conn, [], host.Token);

        Assert.False(result.Completed);     // no forwarding exchange happened
        Assert.Equal(0, conn.SendCount);    // we sent nothing into the session
        Assert.Equal(0, conn.ReceiveCount); // and consumed nothing — a clean refusal
    }

    [Fact]
    public async Task Inbound_Enabled_DoesNotRefuse()
    {
        // Control: with forwarding enabled the answerer engages (it reads/writes the session),
        // so the hold above is the cause of the refusal, not some unrelated short-circuit.
        await using var host = new HostHarness();
        var conn = new RecordingFbbConnection("GB7XYZ-1");
        // host.Runner is constructed enabled (default); a closed connection ends the session.
        FbbSessionResult result = await host.Runner.RunAnswererAsync(conn, [], host.Token);
        Assert.True(conn.SendCount > 0 || conn.ReceiveCount > 0); // it engaged the wire
    }

    private sealed class RecordingFbbConnection(string remote) : IFbbConnection
    {
        public string RemoteCallsign { get; } = remote;
        public int SendCount { get; private set; }
        public int ReceiveCount { get; private set; }
        public int CloseCount { get; private set; }

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

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            return Task.CompletedTask;
        }
    }
}
