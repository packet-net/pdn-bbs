using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Interop.Tests;

/// <summary>
/// Us → oracle over the full simulated-RF path: a P message queued for the GB7BPQ partner
/// is forwarded over a real FBB B1 session (AX.25 via netsim, PDNBBS-1 → GB7BPQ-1) and
/// must land in the oracle's on-disk store.
/// </summary>
[Trait("Category", "Interop")]
[Collection(OracleCollection.Name)]
public class OutboundForwardingInteropTests
{
    [Fact]
    public async Task OutboundCycle_ForwardsMessageIntoOracleStore()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost();

        // Unique per run: the nonce keys every assertion; the BID must be fresh so the
        // oracle answers '+' (a repeated BID would be deduped to '-' and no new .mes
        // file would appear). 12 chars (compat spec §2.3 cap): 5 time digits + "_PDNBBS".
        string nonce = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}");
        string bid = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000:D5}_PDNBBS");
        string title = $"pdn-out {nonce}";
        string bodyText = $"pdn-bbs outbound interop body {nonce}";

        // TO GB7BPQ @ GB7BPQ — routes to the partner; on the oracle side AT is its own
        // call, so it stores for local user GB7BPQ (the seeded sysop record).
        var partner = new Partner { Call = "GB7BPQ", AtCalls = ["GB7BPQ"] };
        host.Store.UpsertPartner(partner);
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["GB7BPQ"],
            At = "GB7BPQ",
            Bid = bid,
            Subject = title,
            Body = Encoding.Latin1.GetBytes(bodyText + "\r"),
        });
        host.Routing.RouteMessage(stored);
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);

        // Attach to netsim node a as PDNBBS-1 and dial the oracle BBS directly (the
        // APPLICATION callsign answers over the node port). The F_BBS-flagged
        // BBSUsers.PDNBBS-1 record makes this a forwarding session (SID exchange).
        await using var endpoint = await Ax25Endpoint.AttachAsync(
            OracleFixture.KissHost, OracleFixture.KissPort, InteropBbsHost.AxCall, ct);
        Ax25ByteSession link = await endpoint.ConnectAsync(OracleFixture.OracleBbsCall, ct);

        IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
            host.Store.GetForwardQueue(partner.Call), partner, host.Identity,
            TimeProvider.System, NullLogger.Instance);
        InteropFbbResult result = await host.Runner.RunCallerAsync(link, partner, outbound, ct);
        await link.CloseAsync(ct);

        // On the wire: the session reached FF/FQ and our proposal got FS '+'.
        Assert.True(result.Completed, $"session did not complete; errors: {string.Join(" | ", result.ProtocolErrors)}");
        Assert.True(result.Graceful, $"session did not close FF/FQ; errors: {string.Join(" | ", result.ProtocolErrors)}");
        Assert.Equal(FsAnswerKind.Accept, result.Verdicts[stored.Number]);

        // Our store: queue cleared, single-partner message goes F.
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);

        // The oracle's on-disk store (spec §7.4): m_*.mes is the raw received text —
        // body plus the R: chain; TO/title live in DIRMES.SYS, asserted below via the
        // oracle's own listing. Store writes lag the session — poll with a deadline.
        string mes = await OracleFixture.WaitForMailFileAsync(nonce, TimeSpan.FromSeconds(20), ct);
        Assert.Contains(bodyText, mes, StringComparison.Ordinal);

        // Our send-time R: line travelled at the head of the text (spec §3.7/§3.14).
        IReadOnlyList<RLine> chain = RLine.ExtractLeadingRLines(mes.ReplaceLineEndings("\n").Split('\n'));
        Assert.NotEmpty(chain);
        Assert.Contains(chain, r => r.Raw.Contains("PDNBBS.#23.GBR.EURO", StringComparison.OrdinalIgnoreCase));

        // TO + title, from the oracle's own view of its header store (DIRMES.SYS is
        // binary; the L listing is the supported window onto it — smoke.sh precedent).
        using var telnet = await TelnetBbsClient.ConnectAsync(OracleFixture.KissHost, OracleFixture.TelnetPort, ct);
        await telnet.LoginAndEnterBbsAsync(ct);
        string listing = await telnet.CommandAsync("L", ct);
        string? line = listing.ReplaceLineEndings("\n").Split('\n')
            .FirstOrDefault(l => l.Contains(nonce, StringComparison.Ordinal));
        Assert.True(line is not null, $"forwarded message not in oracle listing: '{listing}'");
        Assert.Contains("GB7BPQ", line, StringComparison.Ordinal); // TO (6-char window — fits exactly)
        Assert.Contains("M0LTE", line, StringComparison.Ordinal);  // FROM
        await telnet.SignOffAsync(ct);
    }
}
