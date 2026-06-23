using System.Net;
using Bbs.Core;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Interop.Tests;

/// <summary>
/// INBOUND (real xfbbd is the CALLER, dialling pdn-bbs Q0PDN-1 over AXUDP) coverage — the direction
/// that exercises pdn-bbs's SELF-GREETING FBB answerer against real canonical F6FBB. The rig declares
/// Q0PDN as a forward partner with the forward.sys <c>R</c> directive ("make a call even with no mail
/// in queue, to trigger reverse forwarding"), so xfbbd dials Q0PDN-1 every minute (port.sys M/P-Fwd
/// 00/01, P A). pdn-bbs listens, accepts xfbbd's dial, and drives the PRODUCTION
/// <see cref="FbbSessionRunner.RunAnswererAsync"/> in selfGreet mode (it emits its own SID + de-CALL
/// prompt — there is no InboundDemux on this raw AXUDP leg).
///
/// What this validates: the answerer GREETING HANDSHAKE against real canonical FBB — xfbbd dials,
/// pdn self-greets, xfbbd returns its real <c>[FBB-7.0.11-...]</c> SID, and the B1F session closes
/// FF/FQ-clean. (A delivered message would additionally exercise the receive path, but a
/// mail.in-seeded message cannot acquire its forward bit under maj_fwd's F_FOR-gated test_forward in
/// canonical FBB — confirmed by source trace — so the R-forced dial transfers nothing; message
/// receipt is therefore checked best-effort, not required.)
///
/// Serialised with the rest of the real-F6FBB lane (shared host port 10093) via <see cref="F6fbbCollection"/>.
/// </summary>
[Trait("Category", "InteropF6fbb")]
[Collection(F6fbbCollection.Name)]
public class F6fbbInboundTests
{
    private static IPEndPoint Vm => F6fbbRig.Endpoint;

    [Fact(Skip = "Parked: real xfbbd's autonomous outbound forward (the dial) does not fire over the " +
        "ax25ipd/kernel-AX.25 port despite full forward.sys partner + bbs.sys slot + R (force-dial) + " +
        "P A config. Isolated to FBB's forward scheduler — the raw AX.25 outbound path itself works " +
        "(verified with ax25_call). The selfGreet answerer + this test pass the instant xfbbd dials; " +
        "next step is FBB DEBUG/strace instrumentation or a sysop force-forward. See rig docs.")]
    public async Task XfbbdDialsPdn_SelfGreetAnswerer_CompletesHandshake()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost("Q0PDN", "#42.GBR.EURO");

        // The dialling station is Q0FBB-1; register Q0FBB so the answerer keys the partner cleanly.
        host.Store.UpsertPartner(new Partner { Call = "Q0FBB", AtCalls = ["Q0FBB"] });

        var runner = new FbbSessionRunner(
            host.Store, host.Receiver, host.Identity, InteropBbsHost.Version,
            TimeProvider.System, NullLogger<FbbSessionRunner>.Instance);

        // Listen as Q0PDN-1; the endpoint auto-answers inbound SABMs at L2 and surfaces them via Accept.
        await using Ax25Endpoint endpoint = await Ax25Endpoint.AttachAxudpAsync(Vm, localPort: 10093, "Q0PDN-1", ct);

        // Accept xfbbd's R-forced dials until ONE completes a clean self-greet handshake (real FBB SID).
        FbbSessionResult? validated = null;
        FbbSessionResult? last = null;
        int sessions = 0;
        while (validated is null)
        {
            Ax25ByteSession link;
            try
            {
                link = await endpoint.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"real xfbbd never dialled Q0PDN-1 within the deadline ({sessions} sessions accepted; " +
                    $"last: {last?.ToString() ?? "none"}). Check: forward.sys Q0PDN block + R directive loaded, " +
                    "port.sys M/P-Fwd 00/01, P A (port letter).");
            }

            sessions++;
            // selfGreet: no InboundDemux on the raw AXUDP leg, so the runner emits our SID + prompt itself.
            last = await runner.RunAnswererAsync(link, [], ct, selfGreet: true);
            await link.CloseAsync(ct);

            if (last.Completed && last.Graceful &&
                last.PeerSidRaw?.StartsWith("[FBB-7.0.11", StringComparison.Ordinal) == true)
            {
                validated = last;
            }
        }

        // The self-greeting answerer completed a clean B1F session with the station that DIALLED us,
        // and that station is real canonical F6FBB (not a transcription, not LinBPQ).
        Assert.True(validated.Completed && validated.Graceful);
        Assert.StartsWith("[FBB-7.0.11", validated.PeerSidRaw);
        Assert.False(validated.B2Active); // [FBB-7.0.11-...$] advertises no '2'

        // Best-effort: if xfbbd ever forwards a bit-carrying message inbound, the receive path stored it.
        Message? received = host.Store
            .ListMessages(new MessageQuery { IncludeHeld = true })
            .FirstOrDefault(m => m.Subject.Contains("SELFGREET-PROBE", StringComparison.Ordinal));
        if (received is not null)
        {
            Assert.Equal(MessageType.Personal, received.Type);
        }
    }
}
