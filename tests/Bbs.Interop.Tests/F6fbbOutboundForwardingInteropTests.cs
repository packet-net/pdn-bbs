using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Interop.Tests;

/// <summary>
/// Us → a REAL LinFBB (F6FBB) BBS over TCP/telnet forwarding: a personal message queued for the
/// F6FBB partner is forwarded over a real FBB B1 session driven by the production FSM
/// (<see cref="FbbSession"/> via <see cref="Ax25FbbSessionRunner"/>) across a live TCP connection to
/// the LinFBB oracle (docker/compose.f6fbb.yml), and must land in that BBS's on-disk message store.
/// This is the live-instance counterpart of the fast transcript suite (Bbs.Fbb.Tests/F6fbbInteropTests):
/// no captured/simulated traffic — the bytes are exchanged with Jean-Paul Roubelat's own FBB software,
/// the implementation the FBB forwarding protocol is named after.
///
/// <para>Tagged <c>Category=InteropF6fbb</c> (NOT <c>Interop</c>) so it runs in its own CI job that
/// stands up the LinFBB stack — the LinBPQ <c>Category=Interop</c> job never tries to run it, and the
/// unit lane (<c>Category!=Interop&amp;Category!=InteropF6fbb</c>) skips it.</para>
/// </summary>
[Trait("Category", "InteropF6fbb")]
[Collection(F6fbbOracleCollection.Name)]
public class F6fbbOutboundForwardingInteropTests
{
    [Fact]
    public async Task OutboundCycle_ForwardsMessageIntoLinFbbStore()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost();

        // Unique per run: the nonce keys every assertion; the BID must be fresh so the oracle answers
        // '+' (a repeated BID would be deduped to '-' and nothing new would be stored). 12-char cap
        // (compat spec §2.3): 5 time digits + "_PDNBBS".
        string nonce = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}");
        string bid = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000:D5}_PDNBBS");
        string title = $"pdn-f6fbb-out {nonce}";
        string bodyText = $"pdn-bbs to f6fbb outbound interop body {nonce}";

        // TO F6FBB @ F6FBB — routes to the partner; on the oracle side AT is its own call, so it
        // stores the message locally for user F6FBB.
        var partner = new Partner { Call = F6fbbOracleFixture.OracleBbsCall, AtCalls = [F6fbbOracleFixture.OracleBbsCall] };
        host.Store.UpsertPartner(partner);
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = [F6fbbOracleFixture.OracleBbsCall],
            At = F6fbbOracleFixture.OracleBbsCall,
            Bid = bid,
            Subject = title,
            Body = Encoding.Latin1.GetBytes(bodyText + "\r"),
        });
        host.Routing.RouteMessage(stored);
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue(partner.Call)).Number);

        // Dial the LinFBB oracle's TCP/telnet forward port, navigate its login as PDNBBS, and run the
        // real FBB caller session over that socket.
        await using var link = await TcpByteSession.ConnectAsync(
            F6fbbOracleFixture.Host,
            F6fbbOracleFixture.TcpPort,
            partner.Call,
            F6fbbOracleFixture.PartnerLogin,
            F6fbbOracleFixture.PartnerPassword,
            ct);

        IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
            host.Store.GetForwardQueue(partner.Call), partner, host.Identity,
            TimeProvider.System, NullLogger.Instance);
        InteropFbbResult result = await host.Runner.RunCallerAsync(link, partner, outbound, ct);

        // On the wire: a real LinFBB SID came back, the session reached FF/FQ, and our proposal got '+'.
        Assert.True(result.Completed, $"session did not complete; errors: {string.Join(" | ", result.ProtocolErrors)}");
        Assert.True(result.Graceful, $"session did not close FF/FQ; errors: {string.Join(" | ", result.ProtocolErrors)}");
        Assert.NotNull(result.PeerSidRaw);
        Assert.StartsWith("[FBB-", result.PeerSidRaw, StringComparison.Ordinal); // the LinFBB SID author token
        Assert.Equal(FsAnswerKind.Accept, result.Verdicts[stored.Number]);

        // Our store: queue cleared, single-partner message goes F.
        Assert.Empty(host.Store.GetForwardQueue(partner.Call));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);

        // The oracle's on-disk store has the body, with our send-time R: line at the head of the text
        // (spec §3.7/§3.14) — proof the message crossed and decompressed cleanly on real F6FBB.
        string mes = await F6fbbOracleFixture.WaitForStoredMessageAsync(nonce, TimeSpan.FromSeconds(30), ct);
        Assert.Contains(bodyText, mes, StringComparison.Ordinal);
        IReadOnlyList<RLine> chain = RLine.ExtractLeadingRLines(mes.ReplaceLineEndings("\n").Split('\n'));
        Assert.NotEmpty(chain);
        Assert.Contains(chain, r => r.Raw.Contains("PDNBBS", StringComparison.OrdinalIgnoreCase));
    }
}
