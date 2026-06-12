using System.Globalization;
using System.Text;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Interop.Tests;

/// <summary>
/// The oracle gate for B2F (design.md load-bearing decision #7): B2 compressed forwarding,
/// both directions, asserted against the LIVE LinBPQ container — not a transcript fake. The
/// per-partner <c>AllowB2F</c> is set TRUE here (the B1 interop tests leave it off, so they
/// stay on the FA/B1 path untouched). Each test pins the wire evidence that FC/B2 — not a
/// silent FA/B1 fallback — was negotiated: the runner surfaces
/// <see cref="InteropFbbResult.B2Active"/> (our offer ∩ the peer's SID) and the peer's raw
/// SID, and the outbound test decodes the FC <c>usize</c> off the proposal the oracle ACCEPTS.
/// </summary>
/// <remarks>
/// ORACLE-SIDE CONFIG (the gate this lane needed; corrects docker/README deltas 6 + 8):
/// LIVE-VERIFIED, the oracle's forwarding-session SID tracks the <c>BBSForwarding.PDNBBS</c>
/// partner record's <c>UseB2Protocol</c> in BOTH directions — not just inbound, and NOT the
/// compile-time auto-user shape delta 8 claimed. With the seed's original <c>UseB2Protocol = 0</c>
/// the oracle advertised <c>[BPQ-6.0.25.23-B1FWIHJM$]</c> (B1 only) even on the us→oracle dial, so
/// our B2 offer found no B2 to intersect and the session fell back to FA. Flipping the record to
/// <c>UseB2Protocol = 1</c> (now the committed docker/oracle/linmail.cfg seed) makes the oracle
/// advertise <c>…-B12FWIHJM$</c> and propose/accept <c>FC EM</c> in both directions. A fresh
/// <c>up -d --wait</c> seeds it; applying it to a running container is a SIGKILL+edit+start dance
/// because BPQMail rewrites the config from memory on a clean shutdown (docker/README "B2F oracle
/// gate"). The B1F lane is unaffected: those tests leave our side B1-only, so the SID intersection
/// drops B2 and the oracle negotiates down to FA (verified — the full suite stays green).
/// </remarks>
[Trait("Category", "Interop")]
[Collection(OracleCollection.Name)]
public class B2ForwardingInteropTests
{
    /// <summary>
    /// Outbound: pdn dials the live LinBPQ oracle with a queued personal and a B2-enabled
    /// partner. Both ends advertise B2, so the session proposes <c>FC EM</c> + ships the B2
    /// object; the oracle ACCEPTS (FS '+') and STORES it. Asserts the negotiation selected B2
    /// (B2Active true, the oracle's SID carried '2') and the body landed in the oracle store.
    /// </summary>
    [Fact]
    public async Task OutboundB2Cycle_NegotiatesFc_OracleAcceptsAndStores()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost();

        string nonce = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}-b2o");
        string bid = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000:D5}_PDNB2");
        string title = $"pdn-b2-out {nonce}";
        string bodyText = $"pdn-bbs B2 outbound interop body {nonce}";

        // AllowB2F = true is the ONLY delta from the B1 OutboundForwardingInteropTests partner:
        // it makes OutboundBuilder build a B2 object AND makes the runner offer '2' in our SID.
        var partner = new Partner { Call = "GB7BPQ", AtCalls = ["GB7BPQ"], AllowB2F = true };
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

        await using var endpoint = await Ax25Endpoint.AttachAsync(
            OracleFixture.KissHost, OracleFixture.KissPort, InteropBbsHost.AxCall, ct);
        Ax25ByteSession link = await endpoint.ConnectAsync(OracleFixture.OracleBbsCall, ct);

        IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
            host.Store.GetForwardQueue(partner.Call), partner, host.Identity,
            TimeProvider.System, NullLogger.Instance);
        InteropFbbResult result = await host.Runner.RunCallerAsync(link, partner, outbound, ct);
        await link.CloseAsync(ct);

        // THE oracle-gate evidence: the live LinBPQ SID advertised B2 and the session
        // negotiated FC — not a silent FA/B1 fallback.
        Assert.True(
            result.PeerSidRaw is { } sid && Sid.Parse(sid).SupportsB2,
            $"the live oracle's SID did not advertise B2: '{result.PeerSidRaw}'");
        Assert.True(
            result.B2Active,
            $"B2 was NOT negotiated (would mean a silent B1 fallback); peer SID='{result.PeerSidRaw}'");

        // The session reached FF/FQ and the oracle accepted our FC proposal (FS '+').
        Assert.True(result.Completed, $"session did not complete; errors: {string.Join(" | ", result.ProtocolErrors)}");
        Assert.True(result.Graceful, $"session did not close FF/FQ; errors: {string.Join(" | ", result.ProtocolErrors)}");
        Assert.Equal(FsAnswerKind.Accept, result.Verdicts[stored.Number]);

        // Our store: queue cleared, single-partner message goes F.
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);

        // The oracle decoded our FC proposal, received the B2 object, and stored it — store
        // writes lag the session, so poll with a deadline.
        //
        // ORACLE FINDING (B2 vs B1 storage): LinBPQ stores an inbound B2 (FC) arrival as the
        // RAW B2 object — the full §3.9 header (MID/Date/Type/From/To/Subject/Mbo/Content-*/Body)
        // at the head of the .mes, then the blank line, then the body. It does NOT strip the B2
        // envelope and re-head the file with a fresh R: line the way it does for a B1 (FA)
        // arrival (where the .mes starts at the R: chain). So our From/To/Subject/MID and our R:
        // line all land in the store, but the R: line is NESTED in the Body section, not leading.
        // We therefore assert the B2 header fields the oracle parsed off the wire + the body +
        // that our R: line survived — the proof the FC/B2 transfer decoded and stored correctly.
        string mes = await OracleFixture.WaitForMailFileAsync(nonce, TimeSpan.FromSeconds(20), ct);
        Assert.Contains(bodyText, mes, StringComparison.Ordinal);

        // The B2 header the oracle stored verbatim at the head (MID first per §3.9).
        Assert.Contains($"MID: {bid}", mes, StringComparison.Ordinal);
        Assert.Contains("From: M0LTE", mes, StringComparison.Ordinal);
        Assert.Contains("To: GB7BPQ", mes, StringComparison.Ordinal);
        Assert.Contains($"Subject: {title}", mes, StringComparison.Ordinal);
        Assert.Contains("Type: Private", mes, StringComparison.Ordinal); // BPQ stores B2 as Private/P

        // Our R: line travelled inside the B2 Body part (spec §3.7/§3.14) and survived.
        Assert.Contains("R:", mes, StringComparison.Ordinal);
        Assert.Contains("PDNBBS.#23.GBR.EURO", mes, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Outbound B2 COMPLETENESS: pdn proposes an FC for a message with TWO To-recipients, one Cc,
    /// and one File: attachment, and the live LinBPQ oracle accepts (FS '+') and stores it. This
    /// is the controllable direction (we author the exact B2 object), so it is the gold-standard
    /// evidence for what real BPQMail does with a multi-recipient + attachment B2 object.
    /// </summary>
    /// <remarks>
    /// ORACLE FINDING (multi-To / Cc / File on a received B2 — recorded from a LIVE run, .mes
    /// captured below). The oracle accepts the FC and stores the object, but — unlike the raw
    /// pass-through it does for a single-recipient B2 — it NORMALISES the recipient envelope on
    /// receipt:
    /// <list type="bullet">
    /// <item>It STRIPS ITS OWN CALL from the To: list. We sent <c>To: GB7BPQ</c> + <c>To: G4ABC</c>;
    /// the stored .mes keeps only <c>To: G4ABC</c> (GB7BPQ is the receiving BBS's own address — the
    /// implied-AT — so BPQMail removes it as "already here"). It does NOT split into per-recipient
    /// copies toward a single partner.</item>
    /// <item>It DROPS the <c>Cc:</c> line entirely — a received B2's Cc is not retained in the .mes.</item>
    /// <item>It FAITHFULLY PRESERVES the attachment: the <c>File: 45 PAYLOAD.TXT</c> header, the
    /// name, and the exact attachment bytes all land in the stored file.</item>
    /// </list>
    /// So our codec emits the full multi-To/Cc/File object correctly (asserted on the wire below,
    /// and end-to-end against another pdn in the host tests); a REAL LinBPQ then normalises To/Cc
    /// at receipt but keeps the attachment. We assert what the oracle ACTUALLY does (the surviving
    /// To, the dropped Cc, the preserved File + bytes) rather than forcing it to match our emission
    /// — the finding the brief asked us to surface. Captured .mes from the live run:
    /// <code>
    /// MID: 69972_PDNBM
    /// Type: Private
    /// From: M0LTE
    /// To: G4ABC            ← GB7BPQ (our own-call To) stripped; Cc: M0CCC dropped
    /// Subject: pdn-b2-multi …
    /// Body: 121
    /// File: 45 PAYLOAD.TXT ← attachment header + name + bytes preserved
    /// (R: line, body, then the attachment payload follow)
    /// </code>
    /// </remarks>
    [Fact]
    public async Task OutboundB2_MultiRecipientAndAttachment_OracleAcceptsAndStoresAllParts()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost();

        string nonce = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}-b2multi");
        string bid = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000:D5}_PDNBM");
        string title = $"pdn-b2-multi {nonce}";
        string bodyText = $"pdn-bbs B2 multi-recipient + attachment body {nonce}";
        string attachmentText = $"attachment-payload-{nonce}";

        var partner = new Partner { Call = "GB7BPQ", AtCalls = ["GB7BPQ"], AllowB2F = true };
        host.Store.UpsertPartner(partner);

        // 2 To + 1 Cc + 1 attachment. The primary To is GB7BPQ (routes onto this partner's queue);
        // the second To + Cc ride the same object (the F-1 per-home model — one hop, full list).
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["GB7BPQ", "G4ABC"],
            CcRecipients = ["M0CCC"],
            At = "GB7BPQ",
            Bid = bid,
            Subject = title,
            Body = Encoding.Latin1.GetBytes(bodyText + "\r"),
            Attachments = [new MessageAttachment("PAYLOAD.TXT", Encoding.ASCII.GetBytes(attachmentText))],
        });
        host.Routing.RouteMessage(stored);
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);

        await using var endpoint = await Ax25Endpoint.AttachAsync(
            OracleFixture.KissHost, OracleFixture.KissPort, InteropBbsHost.AxCall, ct);
        Ax25ByteSession link = await endpoint.ConnectAsync(OracleFixture.OracleBbsCall, ct);

        IReadOnlyList<OutboundItem> outbound = OutboundBuilder.Build(
            host.Store.GetForwardQueue(partner.Call), partner, host.Identity,
            TimeProvider.System, NullLogger.Instance);

        // Sanity on the object we put on the wire: all parts present before the oracle sees it.
        ReadOnlyMemory<byte>? wireObject = Assert.Single(outbound).Wire.B2Object;
        Assert.NotNull(wireObject);
        B2Message onWire = B2Message.Decode(wireObject.Value.Span);
        Assert.Equal(2, onWire.To.Count);
        Assert.Single(onWire.Cc);
        Assert.Single(onWire.Files);

        InteropFbbResult result = await host.Runner.RunCallerAsync(link, partner, outbound, ct);
        await link.CloseAsync(ct);

        Assert.True(
            result.PeerSidRaw is { } sid && Sid.Parse(sid).SupportsB2,
            $"the live oracle's SID did not advertise B2: '{result.PeerSidRaw}'");
        Assert.True(result.B2Active, $"B2 was NOT negotiated; peer SID='{result.PeerSidRaw}'");
        Assert.True(result.Completed, $"session did not complete; errors: {string.Join(" | ", result.ProtocolErrors)}");
        Assert.Equal(FsAnswerKind.Accept, result.Verdicts[stored.Number]);
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));

        // The oracle decoded the FC, received the B2 object and stored it — poll the .mes.
        string mes = await OracleFixture.WaitForMailFileAsync(nonce, TimeSpan.FromSeconds(20), ct);

        // What a REAL LinBPQ does with our multi-To/Cc/File B2 (see the remarks' captured .mes):
        Assert.Contains($"MID: {bid}", mes, StringComparison.Ordinal);
        Assert.Contains(bodyText, mes, StringComparison.Ordinal);         // body survived

        // The remaining (non-own-call) To survived; the oracle stripped its OWN call (GB7BPQ) from
        // the To list — proving the multi-To envelope was decoded, then normalised "already here".
        Assert.Contains("To: G4ABC", mes, StringComparison.Ordinal);
        Assert.DoesNotContain("To: GB7BPQ", mes, StringComparison.Ordinal);

        // The oracle dropped the Cc line at receipt (a documented BPQMail normalisation).
        Assert.DoesNotContain("Cc: M0CCC", mes, StringComparison.Ordinal);

        // The attachment is FAITHFULLY preserved — header, name, and exact bytes all on disk. This
        // is the load-bearing evidence: a real node accepted + stored our File: part intact.
        Assert.Contains("File: ", mes, StringComparison.Ordinal);
        Assert.Contains("PAYLOAD.TXT", mes, StringComparison.Ordinal);
        Assert.Contains(attachmentText, mes, StringComparison.Ordinal);
    }

    /// <summary>
    /// Inbound B2 receive path against the live node: the oracle dials pdn holding a B2 message and
    /// pdn decodes + stores it through the multi-recipient/attachment-capable receiver. This pins
    /// that the FC receive path a real LinBPQ drives lands a message intact; the genuine multi-To /
    /// attachment DECODE is pinned at the host level (see the ORACLE FINDING).
    /// </summary>
    /// <remarks>
    /// ORACLE FINDING (why inbound multi-To / attachment is NOT driven from a real LinBPQ here) —
    /// recorded from live runs:
    /// <list type="bullet">
    /// <item>BPQMail's user message-entry rejects space-separated recipients
    /// (<c>S M0LTE G4ABC @ …</c> → <c>*** Error: Invalid Format</c>).</item>
    /// <item>The comma form (<c>S M0LTE,G4ABC @ …</c>) is ACCEPTED for entry but is treated as ONE
    /// addressee token — it arrives over the wire as a single <c>To: M0LTE,</c> (truncated), NOT as
    /// two <c>To:</c> lines. So a real LinBPQ's user-entry surface cannot ORIGINATE a genuine
    /// multi-To B2 here.</item>
    /// <item>The telnet entry surface (S / Title / Text / /EX) likewise has no attachment-upload
    /// step, so it cannot originate a File:-bearing B2 either.</item>
    /// </list>
    /// Therefore the multi-To/Cc/File path is proven against a real node in the OUTBOUND direction
    /// (the test above: we author the full object, the oracle accepts + stores it — attachment bytes
    /// intact, To/Cc normalised per BPQMail), and the inbound DECODE of multi-To/Cc/File is pinned
    /// at the host level (B2ForwardingTests.FullInboundB2Cycle_MultiRecipientAndAttachment_* and
    /// InboundB2_MultiRecipientAndAttachments_*). This test confirms the live inbound FC receive +
    /// store works end-to-end; it does not pretend the oracle can originate what it cannot. Precise,
    /// surfaced boundary — not a silent gap.
    /// </remarks>
    [Fact]
    public async Task InboundB2_LiveReceive_PdnDecodesAndStores()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost();

        string nonce = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}-b2recv");
        string bodyText = $"oracle to pdn-bbs B2 inbound receive body {nonce}";

        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ-1", AtCalls = ["GB7BPQ"], AllowB2F = true });

        await using var endpoint = await Ax25Endpoint.AttachAsync(
            OracleFixture.KissHost, OracleFixture.KissPort, InteropBbsHost.AxCall, ct);

        using (var telnet = await TelnetBbsClient.ConnectAsync(OracleFixture.KissHost, OracleFixture.TelnetPort, ct))
        {
            await telnet.LoginAndEnterBbsAsync(ct);
            await telnet.PostMessageAsync("S M0LTE @ PDNBBS", $"pdn-b2-recv {nonce}", bodyText, ct);
            await telnet.SignOffAsync(ct);
        }

        InteropFbbResult? delivered = null;
        try
        {
            while (delivered is null)
            {
                Ax25ByteSession link;
                try
                {
                    link = await endpoint.AcceptAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("the oracle never dialled and delivered our B2 inbound message");
                }

                bool before = FindBySubject(host, nonce) is not null;
                InteropFbbResult result = await host.Runner.RunAnswererAsync(link, ct);
                await link.CloseAsync(ct);

                if (!before && FindBySubject(host, nonce) is not null)
                {
                    delivered = result;
                }
            }
        }
        finally
        {
            await DrainOracleRedialsAsync(endpoint, host);
        }

        Assert.Contains(delivered.InboundProposals, p => p is FcProposal); // FC, not a B1 fallback
        Assert.True(delivered.B2Active, "B2 was not negotiated for the oracle-dialled session");

        // pdn decoded the B2 object and stored it through the (now multi-recipient-capable) receiver.
        Message received = FindBySubject(host, nonce)!;
        Assert.Equal(MessageType.Personal, received.Type);
        Assert.Contains(received.Recipients, r => r.ToCall == "M0LTE" && !r.Cc);
        Assert.Contains(bodyText, Encoding.Latin1.GetString(received.Body.Span), StringComparison.Ordinal);
    }

    /// <summary>
    /// Inbound: the live LinBPQ oracle dials pdn (as <c>GB7BPQ-1</c>) holding a personal queued
    /// for us, and — because the partner record is now <c>UseB2Protocol = 1</c> AND our answerer
    /// SID advertises B2 — proposes it as <c>FC EM</c> (B2), not FA (B1). pdn accepts the FC,
    /// receives + decodes the B2 object, and stores it with the right From/To/Subject/Body.
    /// Asserts the inbound proposal on the wire was an <see cref="FcProposal"/> (the whole point
    /// of the oracle check — not a silent B1 fallback).
    /// </summary>
    /// <remarks>
    /// Requires the live oracle's <c>BBSForwarding.PDNBBS</c> record to carry
    /// <c>UseB2Protocol = 1</c> (the committed docker/oracle/linmail.cfg seed sets it; a
    /// container that pre-dates the change needs the SIGKILL+edit+start dance — see the class
    /// remarks + docker/README "B2F oracle gate"). Without it the oracle proposes FA and this
    /// test FALSIFIES (the FC assertion fails) — exactly the silent-B1-fallback regression the
    /// gate guards.
    /// </remarks>
    [Fact]
    public async Task InboundB2Cycle_OracleProposesFc_PdnDecodesAndStores()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = deadline.Token;
        using var host = new InteropBbsHost();

        string nonce = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Environment.ProcessId}-b2i");
        string bodyText = $"oracle to pdn-bbs B2 inbound body {nonce}";

        // The oracle dials as GB7BPQ-1 (netsim trace) and RunAnswererAsync keys the partner
        // lookup by the EXACT remote callsign — so the partner record is GB7BPQ-1, AllowB2F = true
        // so (a) our answerer SID advertises '2' (the oracle then proposes FC) and (b) the
        // receiver accepts the FC. Do NOT dial — the oracle dials us.
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ-1", AtCalls = ["GB7BPQ"], AllowB2F = true });

        // Listen as PDNBBS-1 BEFORE posting, so the oracle's first ~2 s dial finds us.
        await using var endpoint = await Ax25Endpoint.AttachAsync(
            OracleFixture.KissHost, OracleFixture.KissPort, InteropBbsHost.AxCall, ct);

        // Post @ PDNBBS on the oracle — routes onto the PDNBBS partner queue; the oracle dials us.
        using (var telnet = await TelnetBbsClient.ConnectAsync(OracleFixture.KissHost, OracleFixture.TelnetPort, ct))
        {
            await telnet.LoginAndEnterBbsAsync(ct);
            await telnet.PostMessageAsync("S M0LTE @ PDNBBS", $"pdn-b2-in {nonce}", bodyText, ct);
            await telnet.SignOffAsync(ct);
        }

        // Accept oracle-initiated sessions until one DELIVERS our message via an FC proposal.
        // Each redial gets a fresh answerer session; tolerate a cycle the oracle abandons.
        InteropFbbResult? delivered = null;
        try
        {
            while (delivered is null)
            {
                Ax25ByteSession link;
                try
                {
                    link = await endpoint.AcceptAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("the oracle never dialled and delivered our B2 inbound message");
                }

                bool before = FindBySubject(host, nonce) is not null;
                InteropFbbResult result = await host.Runner.RunAnswererAsync(link, ct);
                await link.CloseAsync(ct);

                if (!before && FindBySubject(host, nonce) is not null)
                {
                    delivered = result;
                }
            }
        }
        finally
        {
            // Leave the shared oracle quiet for the next test (it redials while traffic queued).
            await DrainOracleRedialsAsync(endpoint, host);
        }

        // THE oracle-gate evidence for the inbound direction: the proposal the oracle put on the
        // wire was FC (B2), not FA (B1). This is the assertion that fails on a silent B1 fallback.
        Assert.Contains(delivered.InboundProposals, p => p is FcProposal);
        Assert.True(
            delivered.PeerSidRaw is { } sid && Sid.Parse(sid).SupportsB2,
            $"the dialling oracle's SID did not advertise B2: '{delivered.PeerSidRaw}'");
        Assert.True(delivered.B2Active, "B2 was not negotiated for the oracle-dialled session");

        // pdn decoded the B2 object and stored the message with the right fields.
        Message received = FindBySubject(host, nonce)!;
        Assert.Equal(MessageType.Personal, received.Type);
        Assert.Equal(MessageStatus.Unread, received.Status);
        Assert.Contains(received.Recipients, r => r.ToCall == "M0LTE");
        Assert.Equal("GB7BPQ", received.From); // the admin telnet user's callsign
        Assert.Equal($"pdn-b2-in {nonce}", received.Subject);
        Assert.Contains(bodyText, Encoding.Latin1.GetString(received.Body.Span), StringComparison.Ordinal);
    }

    private static Message? FindBySubject(InteropBbsHost host, string nonce) =>
        host.Store
            .ListMessages(new MessageQuery { IncludeHeld = true })
            .FirstOrDefault(m => m.Subject.Contains(nonce, StringComparison.Ordinal));

    /// <summary>
    /// Serves any pending oracle redials with plain answerer sessions until the channel stays
    /// quiet for 15 s, leaving the shared oracle's PDNBBS queue empty for the next test. Runs in
    /// a finally block, so it must never throw. (Mirrors the bidirectional test's drainer.)
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
