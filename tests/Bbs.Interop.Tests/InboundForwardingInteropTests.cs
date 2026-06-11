using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;

namespace Bbs.Interop.Tests;

/// <summary>
/// Oracle → us over the full simulated-RF path: a message posted on BPQMail
/// <c>@ PDNBBS</c> makes the oracle dial PDNBBS-1 over netsim (FWDNewImmediately, ~2 s,
/// then ~30 s redial cycles); our listener answers, the FBB answerer takes delivery, and
/// the message must land in our store with the oracle's R: chain intact and the BID
/// recorded.
/// </summary>
[Trait("Category", "Interop")]
[Collection(OracleCollection.Name)]
public class InboundForwardingInteropTests
{
    [Fact]
    public async Task InboundCycle_OracleDialsAndDeliversIntoOurStore()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost();

        // Listen as PDNBBS-1 BEFORE posting, so the oracle's first ~2 s dial finds us.
        await using var endpoint = await Ax25Endpoint.AttachAsync(
            OracleFixture.KissHost, OracleFixture.KissPort, InteropBbsHost.AxCall, ct);

        string nonce = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}");
        string title = $"pdn-in {nonce}";
        string bodyText = $"oracle to pdn-bbs inbound body {nonce}";

        using (var telnet = await TelnetBbsClient.ConnectAsync(OracleFixture.KissHost, OracleFixture.TelnetPort, ct))
        {
            await telnet.LoginAndEnterBbsAsync(ct);
            await telnet.PostMessageAsync("S M0LTE @ PDNBBS", title, bodyText, ct);
            await telnet.SignOffAsync(ct);
        }

        // Accept-and-serve until our nonce lands: tolerates a first cycle that the
        // oracle abandons (its dial may have started against a half-attached listener)
        // and any stale queue from an earlier aborted run — each redial gets a fresh
        // answerer session. Every wait is deadline-bounded.
        Message? received = null;
        InteropFbbResult? last = null;
        while (received is null)
        {
            Ax25ByteSession link;
            try
            {
                link = await endpoint.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"the oracle never completed a forwarding dial to {InteropBbsHost.AxCall}; " +
                    $"last session: {Describe(last)}");
            }

            last = await host.Runner.RunAnswererAsync(link, ct);
            await link.CloseAsync(ct);
            received = host.Store
                .ListMessages(new MessageQuery { IncludeHeld = true })
                .FirstOrDefault(m => m.Subject.Contains(nonce, StringComparison.Ordinal));
        }

        Assert.True(last!.Completed && last.Graceful, $"delivering session was not clean: {Describe(last)}");

        // The W5 receive path stored it for local delivery (TO M0LTE @ our own call).
        Assert.Equal(MessageType.Personal, received.Type);
        Assert.Equal(MessageStatus.Unread, received.Status);
        Assert.Contains(received.Recipients, r => r.ToCall == "M0LTE");
        Assert.Equal("GB7BPQ", received.From); // the admin telnet user's callsign (bpq32.cfg USER line)

        // ReceivedFrom is the dialling station as seen on the AX.25 connect.
        Assert.NotNull(received.ReceivedFrom);
        Assert.StartsWith("GB7BPQ", received.ReceivedFrom, StringComparison.Ordinal);

        // The oracle's BID (auto `<msgno>_GB7BPQ-1`, spec §2.3) travelled in the FA
        // proposal and is recorded in our dedup store — re-offering it must now dedup.
        Assert.EndsWith("_GB7BPQ-1", received.Bid, StringComparison.Ordinal);
        Assert.Equal(
            BidDisposition.RejectDuplicate,
            host.Store.CheckInboundBid(received.Bid, MessageType.Personal, "M0LTE"));

        // Body verbatim: the oracle's R: chain intact at the head, our nonce body after it.
        string text = Encoding.Latin1.GetString(received.Body.Span);
        Assert.Contains(bodyText, text, StringComparison.Ordinal);
        IReadOnlyList<RLine> chain = RLine.ExtractLeadingRLines(text.ReplaceLineEndings("\n").Split('\n'));
        Assert.NotEmpty(chain);
        Assert.Contains(chain, r => r.Raw.Contains("GB7BPQ", StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(InteropFbbResult? result) =>
        result is null
            ? "(none accepted)"
            : $"Completed={result.Completed} Graceful={result.Graceful} errors=[{string.Join(" | ", result.ProtocolErrors)}]";
}
