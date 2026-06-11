using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Interop.Tests;

/// <summary>
/// In-session reverse forwarding ("turnaround", compat spec §3.11) against the live
/// oracle: ONE FBB session must drain BOTH directions.
/// Caller side: we dial holding mail and collect the oracle's queued traffic in the
/// same session (a caller that runs out of proposals hands the turn over; the oracle
/// then proposes what it holds for us). Answerer side: the oracle dials holding mail
/// and collects ours in the session IT initiated — stock BPQ (RequestReverse=0; that
/// flag only governs dialling with an empty queue) accepts reverse traffic because
/// per-partner DoReverse defaults TRUE (spec §3.11).
/// </summary>
[Trait("Category", "Interop")]
[Collection(OracleCollection.Name)]
public class BidirectionalForwardingInteropTests
{
    /// <remarks>
    /// FALSIFIED against the stock oracle — kept as the executable witness, skipped to
    /// keep the shared-oracle lane stable. Verified live (wire transcript) and in the
    /// pinned LinBPQ source: inbound AX.25 connects are SSID-stripped before the user
    /// lookup (BBSUtilities.c, <c>strlop(callsign, '-')</c> ahead of LookupCall), so our
    /// dial lands on auto-user PDNBBS (F_Temp_B2_BBS), while the queued message's
    /// forward bit is keyed to the F_BBS partner record PDNBBS-1 (BBSNumber 1). The
    /// answerer-side reverse scan (FindMessagestoForwardLoop:
    /// <c>check_fwd_bit(Msg-&gt;fbbs, user-&gt;BBSNumber)</c>) therefore finds nothing and
    /// the oracle answers FF→our FQ instead of proposing — the README §"Reverse" claim
    /// ("inherent to the FBB block flow ... needs no extra config") does not hold for
    /// the us-dials direction with SSID-keyed partner records. Worse, the same identity
    /// split means BPQ's one-forward-session-per-partner serialisation does not see our
    /// dial-in session, so its dialler fires mid-session for the still-queued traffic
    /// and the SABM/close collision can leak a half-dead PDNBBS stream that bounces the
    /// next inbound test with "Already Connected" (observed). Caller-side turnaround
    /// needed the oracle's BBSUsers/BBSForwarding partner records re-keyed to the
    /// SSID-stripped base call (PDNBBS) — done in docker/oracle/linmail.cfg together
    /// with the docs/smoke contract; base-keying is also how real BPQ deployments
    /// configure AX.25 partners (the GB7RDG snapshot base-keys all 24 records).
    /// </remarks>
    [Fact]
    public async Task CallerDial_OneSessionDrainsBothDirections()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost();

        long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonceA = string.Create(
            CultureInfo.InvariantCulture, $"{seconds}-{Environment.ProcessId}-a"); // oracle → us
        string nonceB = string.Create(
            CultureInfo.InvariantCulture, $"{seconds}-{Environment.ProcessId}-b"); // us → oracle
        string bodyA = $"oracle to pdn-bbs reverse body {nonceA}";
        string bodyB = $"pdn-bbs to oracle outbound body {nonceB}";
        string bid = string.Create(CultureInfo.InvariantCulture, $"{seconds % 100000:D5}_PDNBBS");

        // 1. Post the oracle-side personal FIRST: @ PDNBBS routes it onto the PDNBBS-1
        //    partner queue (linmail.cfg ATCalls). The oracle dials PDNBBS-1 within
        //    seconds of acceptance (FWDNewImmediately + FwdInterval=2) and — measured
        //    live — keeps SABMing the channel near-continuously (~2.7 s cadence,
        //    FRACK=3000/RETRIES=10 cycles back-to-back) until something answers. We are
        //    not listening yet, so those dials fail and the message stays queued.
        using (var telnet = await TelnetBbsClient.ConnectAsync(OracleFixture.KissHost, OracleFixture.TelnetPort, ct))
        {
            await telnet.LoginAndEnterBbsAsync(ct);
            await telnet.PostMessageAsync("S M0LTE @ PDNBBS", $"pdn-bidi-rev {nonceA}", bodyA, ct);
            await telnet.SignOffAsync(ct);
        }

        // 2. Our outbound, queued for the GB7BPQ partner (the outbound-test pattern:
        //    TO GB7BPQ @ GB7BPQ stores for the oracle's seeded sysop user).
        var partner = new Partner { Call = "GB7BPQ", AtCalls = ["GB7BPQ"] };
        host.Store.UpsertPartner(partner);
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["GB7BPQ"],
            At = "GB7BPQ",
            Bid = bid,
            Subject = $"pdn-bidi-out {nonceB}",
            Body = Encoding.Latin1.GetBytes(bodyB + "\r"),
        });
        host.Routing.RouteMessage(stored);
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);

        // 3. Wait ~2 s, then attach and dial — landing our SABM inside the window
        //    before the oracle's first dial fires (~8 s after acceptance, measured), so
        //    we own the GB7BPQ-1↔PDNBBS-1 address pair and its own dialler finds the
        //    pair busy. If our SABM crosses one of its dial cycles anyway, retry.
        //
        //    NO AcceptAsync anywhere in this test: the caller pump below is the only
        //    thing that ever delivers into this host's store (the endpoint auto-answers
        //    inbound SABMs at L2, but an unserved link carries no FBB session), so
        //    nonce A present in the store after RunCallerAsync proves it arrived on the
        //    SAME session/byte-stream as our dial — not via an oracle-initiated connect.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await using var endpoint = await Ax25Endpoint.AttachAsync(
            OracleFixture.KissHost, OracleFixture.KissPort, InteropBbsHost.AxCall, ct);
        try
        {
            Ax25ByteSession link = await DialOracleWithRetryAsync(endpoint, ct);
            IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
                host.Store.GetForwardQueue(partner.Call), partner, host.Identity,
                TimeProvider.System, NullLogger.Instance);
            InteropFbbResult result = await host.Runner.RunCallerAsync(link, partner, outbound, ct);
            await link.CloseAsync(ct);

            // The ONE session ended FF/FQ-clean and our proposal got FS '+'.
            Assert.True(result.Completed, $"session did not complete; errors: {string.Join(" | ", result.ProtocolErrors)}");
            Assert.True(result.Graceful, $"session did not close FF/FQ; errors: {string.Join(" | ", result.ProtocolErrors)}");
            Assert.Equal(FsAnswerKind.Accept, result.Verdicts[stored.Number]);

            // (a) Our message reached the oracle's on-disk store (store writes lag —
            //     poll with a deadline).
            string mes = await OracleFixture.WaitForMailFileAsync(nonceB, TimeSpan.FromSeconds(20), ct);
            Assert.Contains(bodyB, mes, StringComparison.Ordinal);

            // (c) ... and is marked Forwarded in ours, queue cleared.
            Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
            Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);

            // (b) THE turnaround claim (spec §3.11 / docker README "Reverse" note): the
            //     oracle proposed its queued PDNBBS-1 traffic inside the session WE
            //     dialled, and it landed in our store.
            Message? received = host.Store
                .ListMessages(new MessageQuery { IncludeHeld = true })
                .FirstOrDefault(m => m.Subject.Contains(nonceA, StringComparison.Ordinal));
            if (received is null)
            {
                string? oracleLog = await OracleFixture.OracleShellAsync(
                    "tail -40 /data/logs/log_*_BBS.txt 2>/dev/null", CancellationToken.None);
                Assert.Fail(
                    "the oracle did NOT hand over its queued PDNBBS-1 traffic in the session we dialled " +
                    "(in-session reverse / turnaround). Session: " +
                    $"Completed={result.Completed} Graceful={result.Graceful} " +
                    $"errors=[{string.Join(" | ", result.ProtocolErrors)}]. Oracle BBS log tail:\n{oracleLog}");
            }

            Assert.Equal(MessageType.Personal, received.Type);
            Assert.Equal(MessageStatus.Unread, received.Status);
            Assert.Contains(received.Recipients, r => r.ToCall == "M0LTE");
            Assert.Equal("GB7BPQ", received.From); // the admin telnet user's callsign
            Assert.EndsWith("_GB7BPQ-1", received.Bid, StringComparison.Ordinal);
            Assert.Contains(bodyA, Encoding.Latin1.GetString(received.Body.Span), StringComparison.Ordinal);
        }
        finally
        {
            // Leave the shared, stateful oracle quiet: if the turnaround did NOT drain
            // its queue, the oracle keeps SABMing the channel indefinitely and would
            // poison the next test in the collection.
            await DrainOracleRedialsAsync(endpoint, host);
        }
    }

    [Fact]
    public async Task OracleDial_OneSessionDrainsBothDirections()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost();

        long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonceC = string.Create(
            CultureInfo.InvariantCulture, $"{seconds}-{Environment.ProcessId}-c"); // us → oracle
        string nonceD = string.Create(
            CultureInfo.InvariantCulture, $"{seconds}-{Environment.ProcessId}-d"); // oracle → us
        string bodyC = $"pdn-bbs to oracle reverse body {nonceC}";
        string bodyD = $"oracle to pdn-bbs inbound body {nonceD}";
        string bid = string.Create(CultureInfo.InvariantCulture, $"{seconds % 100000:D5}_PDNBBS");

        // 1. Queue ours for the partner identity the oracle's dial presents: it dials
        //    as GB7BPQ-1 (netsim frame trace: GB7BPQ-1>PDNBBS-1), and RunAnswererAsync
        //    keys the partner/queue lookup by the exact remote callsign — so the
        //    partner record here is GB7BPQ-1, not GB7BPQ. Do NOT dial.
        var partner = new Partner { Call = "GB7BPQ-1", AtCalls = ["GB7BPQ"] };
        host.Store.UpsertPartner(partner);
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["GB7BPQ"],
            At = "GB7BPQ",
            Bid = bid,
            Subject = $"pdn-bidi-fwd {nonceC}",
            Body = Encoding.Latin1.GetBytes(bodyC + "\r"),
        });
        host.Routing.RouteMessage(stored);
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ-1")).Number);

        // 2. Listen as PDNBBS-1 BEFORE posting, so the oracle's first ~2 s dial finds us.
        await using var endpoint = await Ax25Endpoint.AttachAsync(
            OracleFixture.KissHost, OracleFixture.KissPort, InteropBbsHost.AxCall, ct);

        // 3. Post @ PDNBBS — the oracle dials us within seconds.
        using (var telnet = await TelnetBbsClient.ConnectAsync(OracleFixture.KissHost, OracleFixture.TelnetPort, ct))
        {
            await telnet.LoginAndEnterBbsAsync(ct);
            await telnet.PostMessageAsync("S M0LTE @ PDNBBS", $"pdn-bidi-in {nonceD}", bodyD, ct);
            await telnet.SignOffAsync(ct);
        }

        // 4. Accept-and-serve until ONE oracle-initiated session BOTH delivered nonce D
        //    into our store AND collected nonce C (FS '+') — the answerer runner loads
        //    the GB7BPQ-1 forward queue per session, so the same session that takes the
        //    oracle's batch proposes ours back on our turn. The loop tolerates a first
        //    cycle the oracle abandons (each redial gets a fresh answerer session); a
        //    fully-aborted cycle drains neither direction, so the both-in-one check
        //    still pins both transfers to a single session. We never dial — every
        //    accepted session here was initiated by the oracle.
        InteropFbbResult? last = null;
        InteropFbbResult? both = null;
        while (both is null)
        {
            Ax25ByteSession link;
            try
            {
                link = await endpoint.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    "no single oracle-initiated session drained both directions; " +
                    $"last session: {Describe(last)}");
            }

            bool dBefore = FindBySubject(host, nonceD) is not null;
            last = await host.Runner.RunAnswererAsync(link, ct);
            await link.CloseAsync(ct);

            bool dDeliveredThisSession = !dBefore && FindBySubject(host, nonceD) is not null;
            bool cAcceptedThisSession =
                last.Verdicts.TryGetValue(stored.Number, out FsAnswerKind kind) && kind == FsAnswerKind.Accept;
            if (dDeliveredThisSession && cAcceptedThisSession)
            {
                both = last;
            }
        }

        Assert.True(both.Completed && both.Graceful, $"the both-ways session was not clean: {Describe(both)}");

        // (a) The oracle's message landed in our store (the W5 receive path: local
        //     delivery for TO M0LTE @ our own call).
        Message received = FindBySubject(host, nonceD)!;
        Assert.Equal(MessageType.Personal, received.Type);
        Assert.Equal(MessageStatus.Unread, received.Status);
        Assert.Contains(received.Recipients, r => r.ToCall == "M0LTE");
        Assert.Equal("GB7BPQ", received.From); // the admin telnet user's callsign
        Assert.EndsWith("_GB7BPQ-1", received.Bid, StringComparison.Ordinal);
        Assert.Contains(bodyD, Encoding.Latin1.GetString(received.Body.Span), StringComparison.Ordinal);

        // (b) Ours reached the oracle's on-disk store — with our send-time R: line at
        //     the head — and is marked Forwarded in ours: stock BPQ (RequestReverse=0)
        //     collected our mail inside the session IT initiated.
        string mes = await OracleFixture.WaitForMailFileAsync(nonceC, TimeSpan.FromSeconds(20), ct);
        Assert.Contains(bodyC, mes, StringComparison.Ordinal);
        IReadOnlyList<RLine> chain = RLine.ExtractLeadingRLines(mes.ReplaceLineEndings("\n").Split('\n'));
        Assert.NotEmpty(chain);
        Assert.Contains(chain, r => r.Raw.Contains("PDNBBS.#23.GBR.EURO", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ-1"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);
    }

    private static Message? FindBySubject(InteropBbsHost host, string nonce) =>
        host.Store
            .ListMessages(new MessageQuery { IncludeHeld = true })
            .FirstOrDefault(m => m.Subject.Contains(nonce, StringComparison.Ordinal));

    private static string Describe(InteropFbbResult? result) =>
        result is null
            ? "(none accepted)"
            : $"Completed={result.Completed} Graceful={result.Graceful} " +
              $"verdicts=[{string.Join(", ", result.Verdicts.Select(v => $"{v.Key}:{v.Value}"))}] " +
              $"errors=[{string.Join(" | ", result.ProtocolErrors)}]";

    /// <summary>
    /// Dials the oracle BBS, retrying (bounded) if our SABM crosses one of the oracle's
    /// own dial cycles for the same AX.25 address pair.
    /// </summary>
    private static async Task<Ax25ByteSession> DialOracleWithRetryAsync(
        Ax25Endpoint endpoint, CancellationToken cancellationToken)
    {
        const int Attempts = 4;
        for (int attempt = 1; ; attempt++)
        {
            using var perDial = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            perDial.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                return await endpoint.ConnectAsync(OracleFixture.OracleBbsCall, perDial.Token);
            }
            catch (Exception ex) when (
                attempt < Attempts && !cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Serves any pending oracle redials with plain answerer sessions until the channel
    /// stays quiet for 15 s, leaving the shared oracle's PDNBBS-1 queue empty for the
    /// next test (the oracle redials indefinitely while traffic is queued — measured).
    /// Runs in a finally block, so it must never throw.
    /// </summary>
    private static async Task DrainOracleRedialsAsync(Ax25Endpoint endpoint, InteropBbsHost host)
    {
        try
        {
            using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (true)
            {
                Ax25ByteSession link;
                using var accept = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                accept.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    link = await endpoint.AcceptAsync(accept.Token);
                }
                catch (OperationCanceledException)
                {
                    return; // quiet — nothing (left) queued
                }

                await host.Runner.RunAnswererAsync(link, overall.Token);
                await link.CloseAsync(overall.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 90 s cap; anything left is the next test's accept-loop tolerance / a recycle.
        }
    }
}
