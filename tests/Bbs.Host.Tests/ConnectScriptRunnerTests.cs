using System.Text;
using Bbs.Core;
using Bbs.Fbb;

namespace Bbs.Host.Tests;

/// <summary>
/// Connect-script execution at the scheduler level (compat spec §4.4): post-open steps run in order
/// (an explicit EXPECT= waits for its prompt before sending; a bare line is sent verbatim), the
/// hand-to-FBB wait accepts ONLY a real FBB SID (never an intermediate node prompt), node chatter is
/// absorbed before the FBB caller session starts, failure text and silent nodes fail the cycle onto
/// the backoff-retry path, and a <c>C &lt;port&gt; &lt;target&gt;</c> connect pins the port on the RHP open.
/// </summary>
public class ConnectScriptRunnerTests
{
    private const string PeerSid = "[BPQ-6.0.24.44-B1FHM$]";
    private const string OwnSid = "[PDN-0.1.0-B1FHM$]";

    [Fact]
    public async Task ScriptedCycle_NavigatesTheNodeThenRunsTheFullForward()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ-1" }, new() { Send = "BBS" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        Message stored = QueueOne(host, "Hi");

        // The first C names the open (spec §4.4); the remaining line is a step.
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ-1", peer.Remote);
        Assert.Equal("BBS", await peer.ReadLineAsync());

        // Node chatter before the SID — including a "*** Connected" progress line the
        // FBB FSM would treat as fatal (spec §3.12) — is consumed by the script layer.
        await peer.SendLineAsync("GB7BPQ pdn-bbs oracle");
        await peer.SendLineAsync("*** Connected to GB7BPQ-1");
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7BPQ-1>\r");

        // The FBB caller session starts on the SID line the script handed over: our
        // SID, then the proposal block (spec §3.1 step 3).
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
        string fa = await peer.ReadLineAsync();
        Assert.StartsWith("FA P M0LTE GB7BPQ G8ABC 1_GB7PDN ", fa, StringComparison.Ordinal);
        string terminator = await peer.ReadLineAsync();
        Assert.True(ProposalBlock.TryParseTerminator(terminator, out byte? checksum));
        Assert.Equal(ProposalBlock.ComputeChecksum([fa]), checksum);

        await peer.SendLineAsync("FS +");

        var reader = new FbbBlockReader();
        byte[] leftover = await InboundForwardingTests.ReadOneTransferAsync(peer, reader);
        peer.PushBackForLines(leftover);
        Assert.Equal("Hi", reader.Title);

        // Peer has nothing for us → FF; we are done → FQ; the host hangs up.
        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();

        // FS '+' → MarkForwarded (spec §3.4): queue cleared, single-partner message F.
        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);
    }

    [Fact]
    public async Task ExpectStep_SendsOnlyAfterTheExpectedPromptArrives()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            // EXPECT=SEND: wait for the node's prompt, THEN send "BBS" (spec §4.4 / bpqchat).
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "GB7BPQ>", Send = "BBS" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ", peer.Remote);

        // Nothing is sent until the awaited prompt arrives.
        await Task.Delay(200);
        Assert.False(peer.TryReadLine(out string early), $"Sent before the prompt arrived: \"{early}\"");

        // The prompt has no line terminator (a node prompt is a bare "<call>>"); the expect
        // matches it as an accumulated-bytes substring, then releases the send.
        await peer.SendTextAsync("GB7BPQ>");
        Assert.Equal("BBS", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task ExpectThatNeverArrives_FailsAtConTimeoutAndRetries()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "NEVER-COMES", Send = "BBS" }],
            ForwardNewImmediately = true,
            ForwardIntervalSeconds = 3600,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        Message stored = QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ", peer.Remote);

        // The awaited substring never appears: the expect is bounded by ConTimeout (default
        // 60 s, TimeProvider-driven — spec §4.1). The failed cycle closes the child and a
        // second dial follows after the 60 s backoff; nothing was ever sent.
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(15), () => Task.FromResult(host.Server.OpenAttempts >= 2));
        await peer.WaitForHostCloseAsync();
        Assert.False(peer.TryReadLine(out _));
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
    }

    [Fact]
    public async Task ExpectMatch_IsCaseInsensitive()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            // The expect is upper-case; the node emits lower-case — the match is still made.
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "GB7BPQ>", Send = "BBS" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await peer.SendTextAsync("welcome to gb7bpq> ");
        Assert.Equal("BBS", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task ScriptedExpectCycle_NavigatesTheNodeThenRunsTheFullForward()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ-1" }, new() { Expect = "GB7BPQ-1>", Send = "BBS" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();

        Message stored = QueueOne(host, "Hi");

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ-1", peer.Remote);

        // The prompt releases the BBS send; the SID + tail then hand over to the FBB session.
        await peer.SendTextAsync("Welcome\rGB7BPQ-1>");
        Assert.Equal("BBS", await peer.ReadLineAsync());

        await peer.SendLineAsync("*** Connected to GB7BPQ-1");
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7BPQ-1>\r");

        Assert.Equal(OwnSid, await peer.ReadLineAsync());
        string fa = await peer.ReadLineAsync();
        Assert.StartsWith("FA P M0LTE GB7BPQ G8ABC 1_GB7PDN ", fa, StringComparison.Ordinal);
        string terminator = await peer.ReadLineAsync();
        Assert.True(ProposalBlock.TryParseTerminator(terminator, out byte? checksum));
        Assert.Equal(ProposalBlock.ComputeChecksum([fa]), checksum);

        await peer.SendLineAsync("FS +");
        var reader = new FbbBlockReader();
        byte[] leftover = await InboundForwardingTests.ReadOneTransferAsync(peer, reader);
        peer.PushBackForLines(leftover);
        Assert.Equal("Hi", reader.Title);

        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();

        Assert.Empty(host.Store.GetForwardQueue("GB7BPQ"));
        Assert.Equal(MessageStatus.Forwarded, host.Store.GetMessage(stored.Number)!.Status);
    }

    [Fact]
    public async Task IntermediateStep_WaitsForNodeProgressBeforeTheNextSend()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Send = "C GB7RDG" }, new() { Send = "BBS" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ", peer.Remote);

        // The second C is a node-level command, sent verbatim (spec §4.4).
        Assert.Equal("C GB7RDG", await peer.ReadLineAsync());

        // Nothing more goes out until the node reports progress.
        await Task.Delay(200);
        Assert.False(peer.TryReadLine(out string early), $"Sent before the node answered: \"{early}\"");

        // "the software knows what to look for" (spec §4.4): a CONNECTED line releases
        // the next step.
        await peer.SendLineAsync("*** Connected to GB7RDG");
        Assert.Equal("BBS", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task MultiHopBareStep_SkipsGatewayBanners_AndReturnsThePartnerSidNotTheGatewayBanner()
    {
        // The GB7CIP shape: dial the WEM gateway (port 3), then a BARE onward command. WEM is a
        // URONode whose prompts END in '>'. The old terminal accept took WEM's '>'-banner AS GB7CIP's
        // SID (the wrong-banner capture). The fix: the hand-to-FBB wait accepts ONLY a real FBB SID,
        // so it skips every gateway banner and holds out for GB7CIP's actual SID. (A bare line still
        // sends verbatim; an EXPECT= would be set to ALSO wait for WEM's prompt before sending — node
        // prompts are not standardised, so only an explicit expect can gate a bare command.)
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7CIP",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7WEM-7", Port = "3" }, new() { Send = "C uhf gb7cip" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host, "Hi");

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7WEM-7", peer.Remote);
        Assert.Equal("3", peer.Port);

        // The bare onward command goes out verbatim (the single step; no standardised prompt to gate on).
        Assert.Equal("C uhf gb7cip", await peer.ReadLineAsync());

        // WEM emits node banners that END in '>'. The OLD code captured the first as GB7CIP's SID and
        // started FBB on it; the fix skips them and keeps waiting for a real FBB SID.
        await peer.SendLineAsync("URONode v2.15 - Welcome to GB7WEM-1");
        await peer.SendLineAsync("URONode GB7WEM in IO91un Help: ? <command>");
        await Task.Delay(200);
        Assert.False(peer.TryReadLine(out string premature),
            $"FBB started on the gateway banner, not GB7CIP's SID: \"{premature}\"");

        // GB7CIP's actual FBB SID arrives → only now does the FBB caller session begin (our SID + a
        // proposal), proving the run handed over GB7CIP's SID and a clean stream — not WEM's banner.
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7CIP>\r");
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
        Assert.StartsWith("FA ", await peer.ReadLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NodeFailureText_FailsTheCycleAndRetriesWithBackoff()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Send = "C GB7RDG" }, new() { Send = "BBS" }],
            ForwardNewImmediately = true,
            ForwardIntervalSeconds = 3600,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        Message stored = QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("C GB7RDG", await peer.ReadLineAsync());

        // Failure text from the spec §4.4 ELSE-detection list fails the cycle: the
        // host closes the child without sending the next step.
        await peer.SendLineAsync("GB7BPQ:BPQ} Failure with GB7RDG");
        await peer.WaitForHostCloseAsync();

        // The queue entry survives, and the retry dials again after the 60 s backoff
        // (failure #1), well inside the partner interval.
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(30), () => Task.FromResult(host.Server.OpenAttempts >= 2));
    }

    [Fact]
    public async Task SilentNode_TimesOutAtConTimeoutAndRetries()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Send = "C GB7RDG" }, new() { Send = "BBS" }],
            ForwardNewImmediately = true,
            ForwardIntervalSeconds = 3600,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        Message stored = QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("C GB7RDG", await peer.ReadLineAsync());

        // The node never answers: the response wait is bounded by ConTimeout (default
        // 60 s, TimeProvider-driven — spec §4.1); the failed cycle closes the child and
        // a second dial follows after the 60 s backoff.
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(15), () => Task.FromResult(host.Server.OpenAttempts >= 2));
        await peer.WaitForHostCloseAsync();
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
    }

    [Fact]
    public async Task PortedConnect_PinsThePortOnTheOpen()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ-1", Port = "2" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        // C <port> <target> (spec §4.4): the port rides the RHP open.
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ-1", peer.Remote);
        Assert.Equal("2", peer.Port);

        // A stepless plan skips the script layer entirely: the FBB caller flow starts
        // directly (the answerer's SID + prompt draw our SID — spec §3.1).
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7BPQ-1>\r");
        Assert.Equal(OwnSid, await peer.ReadLineAsync());
    }

    [Fact]
    public async Task RegexExpect_ReleasesTheSendOnAPatternMatch()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "GB7[A-Z]+>", Send = "BBS", Match = "regex" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await Task.Delay(200);
        Assert.False(peer.TryReadLine(out _)); // nothing until the pattern matches
        await peer.SendTextAsync("GB7BPQ>");   // matches GB7[A-Z]+>
        Assert.Equal("BBS", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task ExpectAny_ReleasesTheSendOnTheFirstAlternativeToArrive()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { ExpectAny = ["NOPE", "GB7BPQ>"], Send = "BBS" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await peer.SendTextAsync("welcome GB7BPQ>"); // the second alternative appears
        Assert.Equal("BBS", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task ExactLineExpect_RequiresAWholeLineNotASubstring()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "OK", Send = "BBS", Match = "exact-line" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await peer.SendLineAsync("OKAY");   // a line CONTAINING OK, but not equal to it
        await Task.Delay(200);
        Assert.False(peer.TryReadLine(out string early), $"exact-line matched a substring: \"{early}\"");
        await peer.SendLineAsync("OK");     // the exact line
        Assert.Equal("BBS", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task RegexThatCanMatchEmpty_DoesNotFireBeforeData()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = ".*", Send = "BBS", Match = "regex" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await Task.Delay(200);
        Assert.False(peer.TryReadLine(out string early), $"a zero-length regex match fired before any prompt: \"{early}\"");
        await peer.SendTextAsync("GB7BPQ>"); // a non-empty match
        Assert.Equal("BBS", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task FailureMarkerBatchedAheadOfThePrompt_FailsTheCycle()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "READY>", Send = "BBS", Match = "exact-line" }],
            ForwardNewImmediately = true,
            ForwardIntervalSeconds = 3600,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        Message stored = QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        // A benign head line, then a BUSY failure marker, then the awaited prompt — all consumed
        // reaching the match. The failure that arrived BEFORE the prompt must fail the cycle, not be swallowed.
        await peer.SendTextAsync("hello\rBUSY from GB7XYZ\rREADY>\r");
        await peer.WaitForHostCloseAsync();
        Assert.False(peer.TryReadLine(out _)); // BBS never sent
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
    }

    [Fact]
    public async Task InvalidRegexPattern_FailsTheCycleGracefully()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "GB7[", Send = "BBS", Match = "regex" }],
            ForwardNewImmediately = true,
            ForwardIntervalSeconds = 3600,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        Message stored = QueueOne(host);

        // A malformed pattern surfaces as a clean cycle failure (caught + retried), never an uncaught throw.
        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(30), () => Task.FromResult(host.Server.OpenAttempts >= 2));
        await peer.WaitForHostCloseAsync();
        Assert.False(peer.TryReadLine(out _));
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
    }

    [Fact]
    public async Task PerStepTimeout_FiresWellBeforeThePartnerConTimeout()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            // Partner ConTimeout is an hour, but the step caps its own wait at 5s.
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "NEVER", Send = "BBS", TimeoutSeconds = 5 }],
            ForwardNewImmediately = true,
            ForwardIntervalSeconds = 3600,
            ConTimeoutSeconds = 3600,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        Message stored = QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        // 10s virtual steps × 200 max = 2000s < the 3600s partner ConTimeout: this only reaches a
        // second dial if the per-step 5s timeout fired (otherwise AdvanceUntilAsync throws).
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(10), () => Task.FromResult(host.Server.OpenAttempts >= 2));
        await peer.WaitForHostCloseAsync();
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
    }

    [Fact]
    public async Task RawSendWithEolNone_PutsExactControlBytesOnTheWire()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "GB7BPQ>", Send = @"\x1a", Raw = true, Eol = "none" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await peer.SendTextAsync("GB7BPQ>");
        byte[] sent = await peer.ReadChunkRawAsync();
        Assert.Equal(new byte[] { 0x1A }, sent); // Ctrl-Z, no trailing CR
    }

    [Fact]
    public async Task EolCrlf_TerminatesTheSendWithCrLf()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "GB7BPQ>", Send = "BBS", Eol = "crlf" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await peer.SendTextAsync("GB7BPQ>");
        byte[] sent = await peer.ReadChunkRawAsync();
        Assert.Equal(Encoding.Latin1.GetBytes("BBS\r\n"), sent);
    }

    [Fact]
    public async Task ExpectAny_FailureMarkerFirst_AbortsBeforeAnyAlternative()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { ExpectAny = ["GB7BPQ>", "TRY LATER"], Send = "BBS" }],
            ForwardNewImmediately = true,
            ForwardIntervalSeconds = 3600,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        Message stored = QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await peer.SendLineAsync("BUSY from GB7XYZ"); // a hard failure before either alternative
        await peer.WaitForHostCloseAsync();
        Assert.False(peer.TryReadLine(out _)); // BBS never sent
        Assert.Equal(stored.Number, Assert.Single(host.Store.GetForwardQueue("GB7BPQ")).Number);
    }

    [Fact]
    public async Task ExactLineExpect_DoesNotMatchAnUnterminatedPrompt()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            ConnectScript = [new() { Open = "GB7BPQ" }, new() { Expect = "=> ", Send = "BBS", Match = "exact-line" }],
            ForwardNewImmediately = true,
        });
        await host.StartLinkAsync();
        host.StartScheduler();
        QueueOne(host);

        FakeRhpPeer peer = await host.Server.NextOpenAsync();
        await peer.SendTextAsync("=> ");   // the prompt text, but with no CR/LF
        await Task.Delay(200);
        Assert.False(peer.TryReadLine(out string early), $"exact-line matched without a terminator: \"{early}\"");
        await peer.SendTextAsync("\r");    // now it's a complete line
        Assert.Equal("BBS", await peer.ReadLineAsync());
    }

    /// <summary>Stores + routes one personal message so the partner queue is non-empty (nudges the scheduler).</summary>
    private static Message QueueOne(HostHarness host, string subject = "Retry")
    {
        Message stored = host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = subject,
            Body = Encoding.Latin1.GetBytes("Local test body.\r"),
        });
        host.Routing.RouteMessage(stored);
        return stored;
    }
}
