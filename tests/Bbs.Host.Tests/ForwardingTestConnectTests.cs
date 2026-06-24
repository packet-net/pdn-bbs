using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bbs.Core;
using Bbs.Host.Forwarding;
using Bbs.Host.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bbs.Host.Tests;

/// <summary>
/// The sysop "test connect" tool (<c>POST /forwarding/test-connect</c>, Feature 2): validate a
/// partner connection — reachability AND the real peer prompt — WITHOUT forwarding any mail. The
/// probe reuses the cycle's resolve→open→run-script→close path (<see cref="ForwardingTester"/>) but
/// runs NO FBB session and touches NO queue, so it is structurally incapable of moving mail. These
/// tests drive the real RHP open path against a <see cref="FakeRhpServer"/> via <see cref="HostHarness"/>,
/// and assert no message/forward rows changed.
/// </summary>
public sealed class ForwardingTestConnectTests : IAsyncDisposable
{
    private const string Sysop = HostHarness.SysopCall; // M0LTE
    private const string PeerSid = "[BPQ-6.0.25.30-B12FWIHJM$]";

    private readonly HostHarness _host = new();
    private WebApplication? _app;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _host.DisposeAsync();
    }

    /// <summary>
    /// Starts a webmail surface backed by the harness store + a real <see cref="ForwardingTester"/>
    /// over the harness RHP link, and returns a gateway-trusted HttpClient identified as
    /// <paramref name="pdnUser"/> (mapped to <paramref name="callsign"/>).
    /// </summary>
    private async Task<HttpClient> StartWebAsync(string pdnUser, string callsign)
    {
        _host.Store.UpsertUser(new User { Callsign = callsign, PdnUsername = pdnUser });
        var tester = new ForwardingTester(_host.Link, _host.Store, _host.Time, NullLogger<ForwardingTester>.Instance);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        Webmail.Map(_app, new WebmailOptions
        {
            Store = _host.Store,
            Routing = _host.Routing,
            Settings = _host.UserSettings,
            StationCallsign = HostHarness.OwnCall,
            SysopCallsign = Sysop,
            TestConnect = tester.TestConnectAsync,
        });
        await _app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Pdn-Gateway", "1");
        client.DefaultRequestHeaders.Add("X-Pdn-User", pdnUser);
        return client;
    }

    private static Task<HttpResponseMessage> PostTestConnectAsync(HttpClient client, string partner) =>
        client.PostAsync(
            new Uri("/forwarding/test-connect", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("partner", partner)]));

    /// <summary>
    /// Seeds a personal queued to <paramref name="partner"/> so a test can assert nothing moved. The
    /// partner is (re)created as a wildcard catch-all so the routed message lands in its queue (the
    /// proven B2-test pattern), preserving the caller's connect script.
    /// </summary>
    private long QueueAMessageTo(string partner)
    {
        Partner existing = _host.Store.GetPartner(partner) ?? new Partner { Call = partner };
        _host.Store.UpsertPartner(existing with { AtCalls = ["*"] });
        Message stored = _host.Store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0ABC",
            Recipients = ["G8XYZ@GB7BSK"],
            Subject = "should not move",
            Body = Encoding.Latin1.GetBytes("body\r"),
        });
        _host.Routing.RouteMessage(stored);
        return stored.Number;
    }

    [Fact]
    public async Task SidPartner_ReturnsOkWithSidAndTranscript_AndMovesNoMail()
    {
        _host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", ConnectScript = ["C GB7BPQ-1"] });
        long queued = QueueAMessageTo("GB7BPQ");
        int queuedBefore = _host.Store.GetForwardQueue("GB7BPQ").Count;
        (int totalBefore, _) = _host.Store.MessageCounts();
        await _host.StartLinkAsync();

        using HttpClient client = await StartWebAsync("tom", Sysop);
        Task<HttpResponseMessage> post = PostTestConnectAsync(client, "GB7BPQ");

        // The fake partner answers the open with a SID, then a prompt — the probe stops at the SID.
        FakeRhpPeer peer = await _host.Server.NextOpenAsync();
        await peer.SendLineAsync(PeerSid);
        await peer.SendTextAsync("de GB7BPQ>\r");

        HttpResponseMessage response = await post;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJsonAsync(response);

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("GB7BPQ", json.GetProperty("partner").GetString());
        Assert.Equal("GB7BPQ-1", json.GetProperty("target").GetString());
        Assert.Equal(PeerSid, json.GetProperty("sid").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("error").ValueKind);
        // The transcript carries what we saw — the SID line at least.
        string[] transcript = json.GetProperty("transcript").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains(transcript, t => t.Contains(PeerSid, StringComparison.Ordinal));

        // The probe must move NO mail: the queue is untouched (the message is still in it — a
        // forward would have stamped forwarded_utc + flipped the status to F, removing it) and the
        // total message count is unchanged.
        Assert.Equal(queuedBefore, _host.Store.GetForwardQueue("GB7BPQ").Count);
        Assert.Contains(_host.Store.GetForwardQueue("GB7BPQ"), m => m.Number == queued);
        Assert.NotEqual(MessageStatus.Forwarded, _host.Store.GetMessage(queued)!.Status);
        (int totalAfter, _) = _host.Store.MessageCounts();
        Assert.Equal(totalBefore, totalAfter);

        // And the child was closed (no lingering session) — the host closed its end.
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task PartnerReturnsError_ReturnsOkFalseWithError_AndMovesNoMail()
    {
        _host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", ConnectScript = ["C GB7BPQ-1"] });
        long queued = QueueAMessageTo("GB7BPQ");
        int queuedBefore = _host.Store.GetForwardQueue("GB7BPQ").Count;
        await _host.StartLinkAsync();

        using HttpClient client = await StartWebAsync("tom", Sysop);
        Task<HttpResponseMessage> post = PostTestConnectAsync(client, "GB7BPQ");

        FakeRhpPeer peer = await _host.Server.NextOpenAsync();
        await peer.SendLineAsync("Sorry, GB7BPQ-1 is UNABLE TO CONNECT");

        HttpResponseMessage response = await post;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await ReadJsonAsync(response);

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("sid").ValueKind);
        string error = json.GetProperty("error").GetString()!;
        Assert.Contains("UNABLE TO CONNECT", error, StringComparison.OrdinalIgnoreCase);

        // No mail moved despite the failed probe.
        Assert.Equal(queuedBefore, _host.Store.GetForwardQueue("GB7BPQ").Count);
        Assert.Contains(_host.Store.GetForwardQueue("GB7BPQ"), m => m.Number == queued);
        Assert.NotEqual(MessageStatus.Forwarded, _host.Store.GetMessage(queued)!.Status);
    }

    [Fact]
    public async Task SteplessOpen_SurfacesThePromptAsSid()
    {
        // A stepless dial script (a bare "C <call>" open with no post-connect steps): the probe waits
        // out the prompt (the runner returns nothing for a stepless plan — WaitForPeerSidAsync surfaces it).
        _host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", ConnectScript = ["C GB7BPQ"] });
        await _host.StartLinkAsync();

        using HttpClient client = await StartWebAsync("tom", Sysop);
        Task<HttpResponseMessage> post = PostTestConnectAsync(client, "GB7BPQ");

        FakeRhpPeer peer = await _host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ", peer.Remote); // dialled the connect-script target
        await peer.SendLineAsync(PeerSid);

        JsonElement json = await ReadJsonAsync(await post);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(PeerSid, json.GetProperty("sid").GetString());
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task InboundOnlyPartner_IsReportedAndNeverDialled()
    {
        // An empty connect script means the partner dials US (inbound-only): test-connect must NOT
        // dial — it reports the inbound-only status (ok, null target/sid) and contacts no one.
        _host.Store.UpsertPartner(new Partner { Call = "GB7BMY" }); // empty script → inbound-only
        await _host.StartLinkAsync();

        using HttpClient client = await StartWebAsync("tom", Sysop);
        JsonElement json = await ReadJsonAsync(await PostTestConnectAsync(client, "GB7BMY"));

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("sid").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("target").ValueKind);
        Assert.Equal(0, _host.Server.OpenAttempts); // never dialled

        bool saysInboundOnly = false;
        foreach (JsonElement line in json.GetProperty("transcript").EnumerateArray())
        {
            if (line.GetString()?.Contains("inbound-only", StringComparison.OrdinalIgnoreCase) == true)
            {
                saysInboundOnly = true;
            }
        }

        Assert.True(saysInboundOnly, "the transcript should explain the partner is inbound-only");
    }

    [Fact]
    public async Task PortAndTarget_DialledFromTheOpenLine()
    {
        // A partner's stored script is pdn-normalised at import (NC->C, "!" stripped — see
        // BpqConnectScriptTests), so test-connect sees a clean "C <port> <call>" open and dials it.
        _host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", ConnectScript = ["C 3 GB7BPQ"] });
        await _host.StartLinkAsync();

        using HttpClient client = await StartWebAsync("tom", Sysop);
        Task<HttpResponseMessage> post = PostTestConnectAsync(client, "GB7BPQ");

        FakeRhpPeer peer = await _host.Server.NextOpenAsync();
        Assert.Equal("GB7BPQ", peer.Remote);     // dialled the bare callsign
        Assert.Equal("3", peer.Port);            // port 3 rode the open
        await peer.SendLineAsync(PeerSid);

        JsonElement json = await ReadJsonAsync(await post);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("GB7BPQ", json.GetProperty("target").GetString());
        Assert.Equal("3", json.GetProperty("port").GetString());
        Assert.Empty(json.GetProperty("plan").GetProperty("warnings").EnumerateArray()); // clean script
        await peer.WaitForHostCloseAsync();
    }

    [Fact]
    public async Task NonSysop_Returns403()
    {
        _host.Store.UpsertPartner(new Partner { Call = "GB7BPQ", ConnectScript = ["C GB7BPQ-1"] });
        await _host.StartLinkAsync();

        // A mapped-but-not-sysop user.
        using HttpClient client = await StartWebAsync("notsysop", "G0NOT");
        HttpResponseMessage response = await PostTestConnectAsync(client, "GB7BPQ");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnknownPartner_Returns404()
    {
        await _host.StartLinkAsync();
        using HttpClient client = await StartWebAsync("tom", Sysop);
        HttpResponseMessage response = await PostTestConnectAsync(client, "GB7NONE");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
}
