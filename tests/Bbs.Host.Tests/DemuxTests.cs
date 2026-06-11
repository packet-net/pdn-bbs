using Bbs.Core;

namespace Bbs.Host.Tests;

public class DemuxTests
{
    [Fact]
    public async Task SidShapedFirstLine_RunsFbbAnswerer()
    {
        await using var host = new HostHarness();
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("GB7BPQ");
        await peer.SendLineAsync("[BPQ-6.0.24.44-B1FHM$]");

        // The answerer opens with our SID + a >-terminated prompt (spec §3.1 step 2).
        Assert.Equal("[PDN-0.1.0-B1FHM$]", await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        // Caller has nothing; we have nothing → FQ and the host closes the child.
        await peer.SendLineAsync("FF");
        Assert.Equal("FQ", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task TextFirstLine_RunsConsole_GreetingReachesPeer()
    {
        await using var host = new HostHarness();
        host.Store.UpsertUser(new User { Callsign = "M0ABC", Name = "Alice", HomeBbs = "GB7PDN" });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");
        await peer.SendLineAsync("B");

        // Console greeting (compat spec §1.1) reaches the peer, then the prompt, then the
        // held first command ("B") is dispatched: sign-off + close.
        Assert.Equal("Hello Alice. Latest Message is 0, Last listed is 0", await peer.ReadLineAsync());
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());
        Assert.Equal("73 de GB7PDN", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task SilentCaller_FallsBackToConsoleAfterBoundedWait()
    {
        await using var host = new HostHarness(firstLineWait: TimeSpan.FromSeconds(30));
        host.Store.UpsertUser(new User { Callsign = "M0ABC", Name = "Alice", HomeBbs = "GB7PDN" });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");

        // Nothing arrives within the demux window → the console greets the human.
        string? greeting = null;
        await host.AdvanceUntilAsync(TimeSpan.FromSeconds(10), () =>
        {
            if (peer.TryReadLine(out string line))
            {
                greeting = line;
            }

            return Task.FromResult(greeting is not null);
        });

        Assert.Equal("Hello Alice. Latest Message is 0, Last listed is 0", greeting);
        Assert.Equal("de GB7PDN>", await peer.ReadLineAsync());

        // The session is fully live: a command round-trips.
        await peer.SendLineAsync("B");
        Assert.Equal("73 de GB7PDN", await peer.ReadLineAsync());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task ConsoleEnteredMessage_IsRoutedWhenSessionEnds()
    {
        await using var host = new HostHarness();
        host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        host.Store.UpsertUser(new User { Callsign = "M0ABC", Name = "Alice", HomeBbs = "GB7PDN" });
        await host.StartLinkAsync();
        host.StartDemux();

        FakeRhpPeer peer = await host.Server.AcceptChildAsync("M0ABC");
        await peer.SendLineAsync("S G8ABC");
        // Skip output until the title prompt, answer the flow, then sign off.
        await ReadUntilAsync(peer, "Enter Title (only):");
        await peer.SendLineAsync("Hello there");
        await ReadUntilAsync(peer, "Enter Message Text (end with /ex or ctrl/z)");
        await peer.SendLineAsync("First line.");
        await peer.SendLineAsync("/ex");
        await peer.SendLineAsync("B");
        await peer.WaitForHostCloseAsync();

        // The demux routed the new message into the forward queues at session end.
        IReadOnlyList<Message> queue = host.Store.GetForwardQueue("GB7BPQ");
        Message queued = Assert.Single(queue);
        Assert.Equal("HELLO THERE", queued.Subject.ToUpperInvariant());
    }

    private static async Task ReadUntilAsync(FakeRhpPeer peer, string expected)
    {
        for (int i = 0; i < 50; i++)
        {
            string line = await peer.ReadLineAsync();
            if (line.Contains(expected, StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail($"Never saw \"{expected}\".");
    }
}
