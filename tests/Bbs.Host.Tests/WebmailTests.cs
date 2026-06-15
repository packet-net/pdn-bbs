using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Bbs.Console;
using Bbs.Core;
using Bbs.Fbb;
using Bbs.Host.Forwarding;
using Bbs.Host.Web;
using Bbs.SevenPlus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Host.Tests;

public sealed class WebmailTests : IAsyncDisposable
{
    private readonly DirectoryInfo _dir;
    private readonly FakeTimeProvider _time;
    private readonly BbsStore _store;
    private readonly RoutingService _routing;
    private readonly InMemoryUserSettingsStore _settings = new();
    private WebApplication? _app;

    // Forwarding-editor seam observers: the harness counts OnPartnersChanged calls and records each
    // "forward now" partner so a test can assert the scheduler reconcile / nudge actually fired.
    private int _partnersChanged;
    private readonly List<string> _forwardNow = [];

    public WebmailTests()
    {
        _dir = Directory.CreateTempSubdirectory("bbs-webmail-test-");
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));
        _store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), "GB7PDN", _time);
        _routing = new RoutingService(
            _store, new RoutingEngine("GB7PDN", "#23.GBR.EURO"), NullLogger<RoutingService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _store.Dispose();
        _dir.Delete(recursive: true);
    }

    private async Task<HttpClient> StartAsync(
        string pdnUser = "tom", bool gatewayHeader = true, string? forwardedPrefix = null, bool autoRedirect = true,
        int? maxUploadBytes = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        var options = new WebmailOptions
        {
            Store = _store,
            Routing = _routing,
            Settings = _settings,
            // The station identity is SSID'd (the connect callsign), distinct from the SSID-less
            // mail own-call "GB7PDN" the store/engine use above — so BIDs are GB7PDN while the title is GB7PDN-1.
            StationCallsign = "GB7PDN-1",
            SysopCallsign = "G0SYS", // distinct from the test users — sysop may read/kill anything
            OnPartnersChanged = () => Interlocked.Increment(ref _partnersChanged),
            OnForwardNow = call => { lock (_forwardNow) { _forwardNow.Add(call); } },
        };
        if (maxUploadBytes is { } cap)
        {
            options = options with { MaxUploadBytes = cap };
        }

        Webmail.Map(_app, options);
        await _app.StartAsync();

        var handler = new HttpClientHandler { AllowAutoRedirect = autoRedirect };
        var client = new HttpClient(handler) { BaseAddress = new Uri(_app.Urls.First()) };
        if (gatewayHeader)
        {
            client.DefaultRequestHeaders.Add("X-Pdn-Gateway", "1");
        }

        client.DefaultRequestHeaders.Add("X-Pdn-User", pdnUser);
        if (forwardedPrefix is not null)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-Prefix", forwardedPrefix);
        }

        return client;
    }

    private void ClaimCallsign(string pdnUser, string callsign) =>
        _store.UpsertUser(new User { Callsign = callsign, PdnUsername = pdnUser });

    [Fact]
    public async Task MissingGatewayHeader_Returns403()
    {
        using HttpClient client = await StartAsync(gatewayHeader: false);
        HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EmptyUser_Returns403()
    {
        using HttpClient client = await StartAsync(pdnUser: "");
        HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnmappedUser_SeesClaimForm_AndClaimLinksCallsign()
    {
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("claim", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("action=\"/claim\"", page, StringComparison.Ordinal);

        HttpResponseMessage claim = await client.PostAsync(
            new Uri("/claim", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("callsign", "m0lte")]));
        claim.EnsureSuccessStatusCode(); // follows the redirect to the inbox

        User? user = _store.GetUser("M0LTE");
        Assert.NotNull(user);
        Assert.Equal("tom", user.PdnUsername);

        string inbox = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("Inbox", inbox, StringComparison.Ordinal);
        Assert.Contains("M0LTE", inbox, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Header_ShowsTheSsiddStationIdentity_NotTheBareMailCall()
    {
        // The webmail title/header is the STATION identity — the SSID'd connect callsign (here
        // GB7PDN-1) — distinct from the SSID-less mail own-call (GB7PDN) the store/engine use for
        // BIDs/R-lines/@home. Regression: the title was twice pointed at the bare mail call, dropping
        // the SSID. Both the <title> and the <h1> must carry the SSID.
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync(pdnUser: "tom");

        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.Contains("<title>GB7PDN-1 —", page, StringComparison.Ordinal);
        Assert.Contains("<h1>GB7PDN-1 <span", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimingSomeoneElsesCallsign_IsRefused()
    {
        ClaimCallsign("alice", "M0LTE");
        using HttpClient client = await StartAsync(pdnUser: "tom");

        HttpResponseMessage claim = await client.PostAsync(
            new Uri("/claim", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("callsign", "M0LTE")]));
        Assert.Equal(HttpStatusCode.Conflict, claim.StatusCode);
        Assert.Equal("alice", _store.GetUser("M0LTE")!.PdnUsername);
    }

    [Fact]
    public async Task Compose_StoresRoutesAndRendersTheMessage()
    {
        ClaimCallsign("tom", "M0LTE");
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/compose", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("type", "P"),
                new KeyValuePair<string, string>("to", "G8ABC@GB7BPQ"),
                new KeyValuePair<string, string>("subject", "Webmail test"),
                new KeyValuePair<string, string>("body", "Hello\nWorld"),
            ]));
        response.EnsureSuccessStatusCode(); // redirect followed to /messages/1
        string page = await response.Content.ReadAsStringAsync();
        Assert.Contains("Webmail test", page, StringComparison.Ordinal);
        Assert.Contains("Hello", page, StringComparison.Ordinal);

        // Same store path as the console: BID generated, AT captured, CR body discipline.
        Message stored = Assert.Single(_store.ListMessages(new MessageQuery()));
        Assert.Equal("M0LTE", stored.From);
        Assert.Equal("G8ABC", Assert.Single(stored.Recipients).ToCall);
        Assert.Equal("GB7BPQ", stored.At);
        Assert.Equal("1_GB7PDN", stored.Bid);
        Assert.Equal("Hello\rWorld\r", stored.GetBodyText());

        // Compose enqueues routing (implied-AT → GB7BPQ).
        Assert.Equal(stored.Number, Assert.Single(_store.GetForwardQueue("GB7BPQ")).Number);
    }

    [Fact]
    public async Task ComposeBulletin_GoesToTheBulletinsList()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/compose", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("type", "B"),
                new KeyValuePair<string, string>("to", "PACKET"),
                new KeyValuePair<string, string>("at", "GBR"),
                new KeyValuePair<string, string>("subject", "Hello all"),
                new KeyValuePair<string, string>("body", "Bulletin body"),
            ]));
        response.EnsureSuccessStatusCode();

        string bulletins = await client.GetStringAsync(new Uri("/bulletins", UriKind.Relative));
        Assert.Contains("Hello all", bulletins, StringComparison.Ordinal);

        Message stored = Assert.Single(_store.ListMessages(new MessageQuery()));
        Assert.Equal(MessageType.Bulletin, stored.Type);
        Assert.Equal("GBR", stored.At);
    }

    [Fact]
    public async Task ReadingAPersonal_MarksItRead_AndInboxShowsIt()
    {
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            Subject = "For you",
            Body = Encoding.Latin1.GetBytes("Read me.\r"),
        });
        using HttpClient client = await StartAsync();

        string inbox = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("For you", inbox, StringComparison.Ordinal);

        string page = await client.GetStringAsync(new Uri($"/messages/{stored.Number}", UriKind.Relative));
        Assert.Contains("Read me.", page, StringComparison.Ordinal);
        Assert.Equal(MessageStatus.Read, _store.GetMessage(stored.Number)!.Status);
    }

    [Fact]
    public async Task Inbox_ExcludesOutboundMailToSelfAtARemoteBbs_ButShowsReceivedMail()
    {
        // The reported bug: composing to M0LTE@GB7RDG (our own user, but @ a remote partner) made
        // the outbound message appear in M0LTE's local Inbox — it's queued for forwarding to GB7RDG,
        // not received here. The Inbox must show received/locally-homed mail only.
        ClaimCallsign("tom", "M0LTE");

        Message outbound = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["M0LTE"],
            At = "GB7RDG",
            Subject = "OUTBOUND-to-self-at-GB7RDG",
            Body = Encoding.Latin1.GetBytes("forward me on.\r"),
        });
        _store.EnqueueForwards(outbound.Number, ["GB7RDG"]); // the routed/outbound state

        _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            Subject = "RECEIVED-for-me",
            Body = Encoding.Latin1.GetBytes("hello.\r"),
        });

        using HttpClient client = await StartAsync();
        string inbox = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.Contains("RECEIVED-for-me", inbox, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTBOUND-to-self-at-GB7RDG", inbox, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sent_ListsMyComposedMail_WithForwardingStatus()
    {
        ClaimCallsign("tom", "M0LTE");

        // Outbound to self @ a remote BBS — queued for forwarding (the message the Inbox now hides).
        Message queued = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["M0LTE"],
            At = "GB7RDG",
            Subject = "QUEUED-one",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        _store.EnqueueForwards(queued.Number, ["GB7RDG"]);

        // Same, but its forward leg has completed.
        Message forwarded = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            At = "GB7RDG",
            Subject = "FORWARDED-one",
            Body = Encoding.Latin1.GetBytes("y\r"),
        });
        _store.EnqueueForwards(forwarded.Number, ["GB7RDG"]);
        _store.MarkForwarded(forwarded.Number, "GB7RDG");

        // A personal I sent to a local user (no @): homed here, no forward leg.
        _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["2E0XYZ"],
            Subject = "LOCAL-one",
            Body = Encoding.Latin1.GetBytes("z\r"),
        });

        // Mail FROM someone else is not "Sent" by me.
        _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            Subject = "INBOUND-not-mine",
            Body = Encoding.Latin1.GetBytes("w\r"),
        });

        using HttpClient client = await StartAsync();
        string sent = await client.GetStringAsync(new Uri("/sent", UriKind.Relative));

        Assert.Contains("QUEUED-one", sent, StringComparison.Ordinal);
        Assert.Contains("queued &rarr; GB7RDG", sent, StringComparison.Ordinal);
        Assert.Contains("FORWARDED-one", sent, StringComparison.Ordinal);
        Assert.Contains("forwarded", sent, StringComparison.Ordinal);
        Assert.Contains("LOCAL-one", sent, StringComparison.Ordinal);
        Assert.Contains("delivered here", sent, StringComparison.Ordinal);
        // Not my mail — absent from Sent.
        Assert.DoesNotContain("INBOUND-not-mine", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sent_ShowsHoldReason_ForOversizeAutoHeldMessage_AndForwardingCardCountsIt()
    {
        // The sysop is the viewer (the harness sysop call is G0SYS) so held mail is visible in Sent.
        ClaimCallsign("tom", "G0SYS");
        _store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });

        Message m = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G0SYS",
            Recipients = ["M0LTE"],
            At = "GB7RDG",
            Subject = "big photo",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        _store.EnqueueForwards(m.Number, ["GB7RDG"]);
        // What the scheduler does when the message exceeds the partner's MaxTxSize.
        _store.HoldMessage(m.Number, "too large for GB7RDG (209595 > 99999 bytes)");

        using HttpClient client = await StartAsync();

        // Sent explains the held message instead of showing a misleading "queued".
        string sent = await client.GetStringAsync(new Uri("/sent", UriKind.Relative));
        Assert.Contains("big photo", sent, StringComparison.Ordinal);
        // The '>' in the reason is HTML-escaped in the rendered page.
        Assert.Contains("too large for GB7RDG (209595 &gt; 99999 bytes)", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("queued &rarr; GB7RDG", sent, StringComparison.Ordinal);

        // The forwarding card surfaces it as held (not a mute "empty" queue).
        string fwd = await client.GetStringAsync(new Uri("/forwarding", UriKind.Relative));
        Assert.Contains("1 held", fwd, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageView_Sysop_CanReleaseHeldMessage_ResumingForwarding()
    {
        ClaimCallsign("tom", "G0SYS"); // the harness sysop
        _store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });
        Message m = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G0SYS",
            Recipients = ["M0LTE"],
            At = "GB7RDG",
            Subject = "held one",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        _store.EnqueueForwards(m.Number, ["GB7RDG"]);
        _store.HoldMessage(m.Number, "too large for GB7RDG (209595 > 99999 bytes)");

        using HttpClient client = await StartAsync();

        // The message view offers Release, with the reason.
        string view = await client.GetStringAsync(new Uri($"/messages/{m.Number}?from=sent", UriKind.Relative));
        Assert.Contains("Release hold", view, StringComparison.Ordinal);
        Assert.Contains("too large for GB7RDG", view, StringComparison.Ordinal);
        Assert.Contains($"/messages/{m.Number}/unhold", view, StringComparison.Ordinal);

        // Releasing resumes forwarding: status back to N (unread), message back in the partner queue.
        HttpResponseMessage resp = await client.PostAsync(
            new Uri($"/messages/{m.Number}/unhold", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);  // the 302 → /sent is auto-followed
        Assert.Equal(MessageStatus.Unread, _store.GetMessage(m.Number)!.Status);
        Assert.Equal([m.Number], _store.GetForwardQueue("GB7RDG").Select(x => x.Number));
    }

    [Fact]
    public async Task Unhold_ByNonSysop_IsRejected_AndLeavesMessageHeld()
    {
        ClaimCallsign("bob", "M0LTE"); // a normal user (the sysop is G0SYS)
        Message m = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            Subject = "held",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        _store.HoldMessage(m.Number, "manual hold");

        using HttpClient client = await StartAsync(pdnUser: "bob", autoRedirect: false);
        HttpResponseMessage resp = await client.PostAsync(
            new Uri($"/messages/{m.Number}/unhold", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(MessageStatus.Held, _store.GetMessage(m.Number)!.Status); // still held
    }

    [Fact]
    public async Task Sent_MessageView_BackLinkReturnsToSent_NotInbox()
    {
        ClaimCallsign("tom", "M0LTE");
        Message m = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            At = "GB7RDG",
            Subject = "sent thing",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        _store.EnqueueForwards(m.Number, ["GB7RDG"]);

        using HttpClient client = await StartAsync();

        // The Sent list links each row with the ?from=sent context marker.
        string sent = await client.GetStringAsync(new Uri("/sent", UriKind.Relative));
        Assert.Contains($"/messages/{m.Number}?from=sent", sent, StringComparison.Ordinal);

        // Opened from Sent → the back link returns to Sent.
        string fromSent = await client.GetStringAsync(new Uri($"/messages/{m.Number}?from=sent", UriKind.Relative));
        Assert.Contains("Back to Sent", fromSent, StringComparison.Ordinal);
        Assert.Contains("href=\"/sent\"", fromSent, StringComparison.Ordinal);
        Assert.DoesNotContain("Back to Inbox", fromSent, StringComparison.Ordinal);

        // Opened without the marker → the type-based default (a personal → Inbox) is unchanged.
        string plain = await client.GetStringAsync(new Uri($"/messages/{m.Number}", UriKind.Relative));
        Assert.Contains("Back to Inbox", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Back to Sent", plain, StringComparison.Ordinal);
    }

    // ------------------------------------------------ B2 completeness: Cc + attachment download

    [Fact]
    public async Task ReadPage_RendersCcAndAttachmentDownloadLinks()
    {
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            CcRecipients = ["G4XYZ"],
            Subject = "with bits",
            Body = Encoding.Latin1.GetBytes("Body.\r"),
            Attachments = [new MessageAttachment("REPORT.PDF", new byte[] { 1, 2, 3 })],
        });
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri($"/messages/{stored.Number}", UriKind.Relative));
        Assert.Contains("Cc", page, StringComparison.Ordinal);
        Assert.Contains("G4XYZ", page, StringComparison.Ordinal);
        Assert.Contains("Attachments", page, StringComparison.Ordinal);
        Assert.Contains($"/messages/{stored.Number}/attachments/REPORT.PDF", page, StringComparison.Ordinal);
        Assert.Contains("REPORT.PDF", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachmentDownload_ReturnsBytesWithDownloadHeaders()
    {
        ClaimCallsign("tom", "M0LTE");
        byte[] bytes = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0xFF];
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            Subject = "file me",
            Body = Encoding.Latin1.GetBytes("x\r"),
            Attachments = [new MessageAttachment("DATA.BIN", bytes)],
        });
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/messages/{stored.Number}/attachments/DATA.BIN", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("DATA.BIN", response.Content.Headers.ContentDisposition.FileNameStar
            ?? response.Content.Headers.ContentDisposition.FileName?.Trim('"'));
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AttachmentDownload_UnknownName_Returns404()
    {
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            Subject = "file me",
            Body = Encoding.Latin1.GetBytes("x\r"),
            Attachments = [new MessageAttachment("REAL.BIN", new byte[] { 1 })],
        });
        using HttpClient client = await StartAsync();

        HttpResponseMessage missing = await client.GetAsync(
            new Uri($"/messages/{stored.Number}/attachments/NOPE.BIN", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task AttachmentDownload_NonRecipient_IsRefused()
    {
        // A personal addressed to someone else: the non-recipient can neither read it nor pull its
        // attachment (same CanRead gate as the read page).
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["G4XYZ"],
            Subject = "not yours",
            Body = Encoding.Latin1.GetBytes("secret\r"),
            Attachments = [new MessageAttachment("SECRET.BIN", new byte[] { 9, 9, 9 })],
        });
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/messages/{stored.Number}/attachments/SECRET.BIN", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AttachmentDownload_ForwardedPrefix_LinkCarriesThePrefix()
    {
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            Subject = "prefixed file",
            Body = Encoding.Latin1.GetBytes("x\r"),
            Attachments = [new MessageAttachment("F.BIN", new byte[] { 1 })],
        });
        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/bbs");

        string page = await client.GetStringAsync(new Uri($"/messages/{stored.Number}", UriKind.Relative));
        Assert.Contains($"/apps/bbs/messages/{stored.Number}/attachments/F.BIN", page, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"/messages/{stored.Number}/attachments", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomeoneElsesPersonal_IsNotReadable()
    {
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["G4XYZ"],
            Subject = "Private",
            Body = Encoding.Latin1.GetBytes("Secret.\r"),
        });
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.GetAsync(new Uri($"/messages/{stored.Number}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task KillMyMessage_KillsIt_OthersCannot()
    {
        ClaimCallsign("tom", "M0LTE");
        Message mine = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "Mine",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        Message theirs = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Bulletin,
            From = "G8XYZ",
            Recipients = ["PACKET"],
            Subject = "Theirs",
            Body = Encoding.Latin1.GetBytes("y\r"),
        });
        using HttpClient client = await StartAsync();

        HttpResponseMessage killMine = await client.PostAsync(
            new Uri($"/messages/{mine.Number}/kill", UriKind.Relative), content: null);
        killMine.EnsureSuccessStatusCode();
        Assert.Equal(MessageStatus.Killed, _store.GetMessage(mine.Number)!.Status);

        // A bulletin is killable only by its sender (compat spec §2.2).
        HttpResponseMessage killTheirs = await client.PostAsync(
            new Uri($"/messages/{theirs.Number}/kill", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.NotFound, killTheirs.StatusCode);
        Assert.Equal(MessageStatus.Unread, _store.GetMessage(theirs.Number)!.Status);
    }

    [Fact]
    public async Task Bulletins_ArePaged()
    {
        ClaimCallsign("tom", "M0LTE");
        for (int i = 1; i <= 30; i++)
        {
            _store.AddMessage(new MessageDraft
            {
                Type = MessageType.Bulletin,
                From = "G8ABC",
                Recipients = ["PACKET"],
                Subject = $"Bulletin {i}",
                Body = Encoding.Latin1.GetBytes("b\r"),
            });
        }

        using HttpClient client = await StartAsync();

        string page1 = await client.GetStringAsync(new Uri("/bulletins", UriKind.Relative));
        Assert.Contains("href=\"/messages/30\"", page1, StringComparison.Ordinal); // newest first
        Assert.DoesNotContain("href=\"/messages/5\"", page1, StringComparison.Ordinal);
        Assert.Contains("older", page1, StringComparison.Ordinal);

        string page2 = await client.GetStringAsync(new Uri("/bulletins?page=2", UriKind.Relative));
        Assert.Contains("href=\"/messages/5\"", page2, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/messages/30\"", page2, StringComparison.Ordinal);
    }

    // ------------------------------------------------ slot/headless embed (?pdn_embed=1)
    // pdn's single-chrome integrated look: the panel renders the app inside its own chrome in a
    // borderless iframe carrying ?pdn_embed=1. When embedded the app drops its own outer <h1>
    // station-identity bar (the panel already shows the app name) but keeps the functional content +
    // the nav tabs, and — because it is server-rendered + no-JS — threads pdn_embed=1 through every
    // link and form so navigation stays headless within the iframe. Non-embedded output is unchanged.

    [Fact]
    public async Task Embedded_Inbox_OmitsTheStationHeader_ButKeepsContentAndNavTabs()
    {
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            Subject = "embedded hello",
            Body = Encoding.Latin1.GetBytes("hi\r"),
        });
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri("/?pdn_embed=1", UriKind.Relative));

        // No big <h1> station-identity bar (no double chrome) — but it is still a full HTML doc.
        Assert.DoesNotContain("<h1>GB7PDN-1 <span", page, StringComparison.Ordinal);
        Assert.DoesNotContain("webmail</span></h1>", page, StringComparison.Ordinal);
        Assert.Contains("<!doctype html>", page, StringComparison.Ordinal);
        Assert.Contains("<title>GB7PDN-1 —", page, StringComparison.Ordinal); // <title> kept (harmless in an iframe)

        // The functional content + nav tabs survive.
        Assert.Contains("Inbox", page, StringComparison.Ordinal);
        Assert.Contains("embedded hello", page, StringComparison.Ordinal);
        Assert.Contains(">Inbox</a>", page, StringComparison.Ordinal);
        Assert.Contains(">Bulletins</a>", page, StringComparison.Ordinal);
        Assert.Contains(">Compose</a>", page, StringComparison.Ordinal);
        Assert.Contains(">Settings</a>", page, StringComparison.Ordinal);

        // The embed signal persists across navigation: every internal link carries pdn_embed=1.
        Assert.Contains("href=\"/?pdn_embed=1\"", page, StringComparison.Ordinal);            // Inbox tab
        Assert.Contains("href=\"/bulletins?pdn_embed=1\"", page, StringComparison.Ordinal);   // Bulletins tab
        Assert.Contains("href=\"/compose?pdn_embed=1\"", page, StringComparison.Ordinal);     // Compose tab
        Assert.Contains("href=\"/settings?pdn_embed=1\"", page, StringComparison.Ordinal);    // Settings tab
        Assert.Contains($"href=\"/messages/{stored.Number}?pdn_embed=1\"", page, StringComparison.Ordinal); // message link
        // Nothing escapes the headless mode (no bare internal link without the flag).
        _ = stored;
    }

    [Fact]
    public async Task Embedded_ComposeForm_CarriesHiddenEmbedInput_AndEmbeddedAction()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        string form = await client.GetStringAsync(new Uri("/compose?pdn_embed=1", UriKind.Relative));

        // The form action stays headless and a hidden field carries the signal across the POST.
        Assert.Contains("action=\"/compose?pdn_embed=1\"", form, StringComparison.Ordinal);
        Assert.Contains("<input type=\"hidden\" name=\"pdn_embed\" value=\"1\">", form, StringComparison.Ordinal);
        // No station-identity header in the embedded compose page either.
        Assert.DoesNotContain("<h1>GB7PDN-1 <span", form, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embedded_ViaHeader_AlsoSuppressesTheHeader()
    {
        // An X-Pdn-Embed: 1 header is honoured equivalently to ?pdn_embed=1 (defensive: the contract
        // is the query param, but the panel may also signal via the header).
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();
        client.DefaultRequestHeaders.Add("X-Pdn-Embed", "1");

        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.DoesNotContain("<h1>GB7PDN-1 <span", page, StringComparison.Ordinal);
        Assert.Contains(">Inbox</a>", page, StringComparison.Ordinal);
        // Even reached via the header, rendered links thread the query param so navigation persists it.
        Assert.Contains("href=\"/bulletins?pdn_embed=1\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotEmbedded_Inbox_KeepsTheStationHeader_AndPlainLinks()
    {
        // The standalone page is unchanged when pdn_embed is absent: the <h1> is present and links
        // carry no embed flag. (Companion to Header_ShowsTheSsiddStationIdentity_NotTheBareMailCall.)
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.Contains("<h1>GB7PDN-1 <span", page, StringComparison.Ordinal);
        Assert.DoesNotContain("pdn_embed", page, StringComparison.Ordinal);     // nothing leaks the flag
        Assert.Contains("<a href=\"/bulletins\">Bulletins</a>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embedded_PersistsAcrossPrefixedNavigation()
    {
        // Under the gateway mount the embed flag rides alongside the prefix on every link.
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/bbs");

        string page = await client.GetStringAsync(new Uri("/?pdn_embed=1", UriKind.Relative));
        Assert.Contains("href=\"/apps/bbs/bulletins?pdn_embed=1\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/apps/bbs/?pdn_embed=1\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>GB7PDN-1 <span", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------ theme handoff (panel → app)
    // An iframe is a separate document that can't see the panel's manual `.dark` class, so the BBS
    // would otherwise fall back to prefers-color-scheme (the OS) — a mismatch with a manual panel
    // toggle. pdn appends the panel's active theme as ?theme=dark|light on the slot iframe src; we
    // emit <html class="dark"|"light"> (the stylesheet already honours .dark / :root:not(.light), so
    // an explicit class overrides prefers-color-scheme) and persist it in a same-origin first-party
    // cookie so it survives in-iframe navigation without threading the param through every link/form.
    // Standalone rendering (no embed, no theme) is unchanged — plain <html>, prefers-color-scheme.

    [Fact]
    public async Task Embedded_ThemeDarkQuery_RendersDarkHtmlClass_AndSetsCookie()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.GetAsync(new Uri("/?pdn_embed=1&theme=dark", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        string page = await response.Content.ReadAsStringAsync();

        // The explicit theme class on <html> overrides prefers-color-scheme.
        Assert.Contains("<html class=\"dark\">", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<html class=\"light\">", page, StringComparison.Ordinal);

        // The theme is persisted via a Set-Cookie so it survives in-iframe navigation.
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            c => c.Contains("pdn_theme=dark", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Embedded_ThemeLightQuery_RendersLightHtmlClass()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.GetAsync(new Uri("/?pdn_embed=1&theme=light", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        string page = await response.Content.ReadAsStringAsync();

        Assert.Contains("<html class=\"light\">", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<html class=\"dark\">", page, StringComparison.Ordinal);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            c => c.Contains("pdn_theme=light", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Embedded_ThemeCookieOnly_NoQuery_StillThemes()
    {
        // A follow-up in-iframe navigation carries only the persisted cookie (no ?theme=) — the page
        // still themes from the cookie, so navigation stays themed without threading the param.
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();
        client.DefaultRequestHeaders.Add("Cookie", "pdn_theme=dark");

        string page = await client.GetStringAsync(new Uri("/?pdn_embed=1", UriKind.Relative));
        Assert.Contains("<html class=\"dark\">", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embedded_ThemeViaHeader_AlsoThemes()
    {
        // The X-Pdn-Theme header is honoured alongside the query (defensive parity with X-Pdn-Embed).
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();
        client.DefaultRequestHeaders.Add("X-Pdn-Embed", "1");
        client.DefaultRequestHeaders.Add("X-Pdn-Theme", "dark");

        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("<html class=\"dark\">", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThemeCookie_ScopedToTheGatewayMountPrefix()
    {
        // Under the gateway mount the cookie Path is the mount prefix (a first-party cookie under the
        // app's path), not the panel root.
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/bbs");

        HttpResponseMessage response = await client.GetAsync(new Uri("/?pdn_embed=1&theme=dark", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            c => c.Contains("pdn_theme=dark", StringComparison.Ordinal)
                && c.Contains("path=/apps/bbs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Standalone_NoEmbedNoTheme_RendersPlainHtml_Unchanged()
    {
        // No embed, no theme query, no cookie → the plain <html> (prefers-color-scheme behaviour) is
        // kept; standalone rendering must not change.
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("<html><head>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<html class=", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidTheme_IsIgnored_NoClassNoCookie()
    {
        // A junk theme value is not one of the two known tokens → ignored (no class, no cookie),
        // falling back to prefers-color-scheme rather than emitting an unknown class.
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.GetAsync(new Uri("/?pdn_embed=1&theme=neon", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        string page = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<html class=", page, StringComparison.Ordinal);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
    }

    // ------------------------------------------------ X-Forwarded-Prefix (gateway mount)
    // Behind pdn's app gateway the app is mounted at /apps/bbs/ with the prefix stripped on
    // proxying; pdn injects X-Forwarded-Prefix and every absolute URL we render or redirect
    // to must carry it (packet.net docs/app-gateway.md). Without the header (the tests
    // above), URLs stay root-relative.

    [Fact]
    public async Task ForwardedPrefix_ClaimFormAction_CarriesThePrefix()
    {
        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/bbs");

        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("action=\"/apps/bbs/claim\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardedPrefix_SuccessfulClaim_RedirectsUnderThePrefix()
    {
        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/bbs", autoRedirect: false);

        HttpResponseMessage claim = await client.PostAsync(
            new Uri("/claim", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("callsign", "m0lte")]));
        Assert.Equal(HttpStatusCode.Found, claim.StatusCode);
        Assert.Equal("/apps/bbs/", claim.Headers.Location!.OriginalString);
        Assert.Equal("tom", _store.GetUser("M0LTE")!.PdnUsername);
    }

    [Fact]
    public async Task ForwardedPrefix_InboxNavRowAndPagerLinks_CarryThePrefix()
    {
        ClaimCallsign("tom", "M0LTE");
        for (int i = 1; i <= 30; i++)
        {
            _store.AddMessage(new MessageDraft
            {
                Type = MessageType.Personal,
                From = "G8ABC",
                Recipients = ["M0LTE"],
                Subject = $"Personal {i}",
                Body = Encoding.Latin1.GetBytes("p\r"),
            });
        }

        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/bbs");

        string inbox = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("<a href=\"/apps/bbs/\">Inbox</a>", inbox, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/apps/bbs/bulletins\">Bulletins</a>", inbox, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/apps/bbs/compose\">Compose</a>", inbox, StringComparison.Ordinal);
        Assert.Contains("href=\"/apps/bbs/messages/30\"", inbox, StringComparison.Ordinal);
        Assert.Contains("href=\"/apps/bbs/?page=2\"", inbox, StringComparison.Ordinal); // pager keeps the prefix
        Assert.DoesNotContain("href=\"/messages/", inbox, StringComparison.Ordinal);    // nothing escapes the mount

        string older = await client.GetStringAsync(new Uri("/?page=2", UriKind.Relative));
        Assert.Contains("href=\"/apps/bbs/?page=1\"", older, StringComparison.Ordinal); // and the "newer" leg

        // The read page's kill form (M0LTE is an addressee, so it renders) posts under the mount too.
        string read = await client.GetStringAsync(new Uri("/messages/30", UriKind.Relative));
        Assert.Contains("action=\"/apps/bbs/messages/30/kill\"", read, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardedPrefix_ComposeFormAndRedirect_CarryThePrefix()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/bbs", autoRedirect: false);

        string form = await client.GetStringAsync(new Uri("/compose", UriKind.Relative));
        Assert.Contains("action=\"/apps/bbs/compose\"", form, StringComparison.Ordinal);

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/compose", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("type", "P"),
                new KeyValuePair<string, string>("to", "G8ABC"),
                new KeyValuePair<string, string>("subject", "Prefixed"),
                new KeyValuePair<string, string>("body", "x"),
            ]));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/apps/bbs/messages/1", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ForwardedPrefix_TrailingSlash_IsTrimmed()
    {
        ClaimCallsign("tom", "M0LTE");
        Message mine = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            Subject = "Mine",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/bbs/", autoRedirect: false);

        HttpResponseMessage kill = await client.PostAsync(
            new Uri($"/messages/{mine.Number}/kill", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.Found, kill.StatusCode);
        Assert.Equal("/apps/bbs/", kill.Headers.Location!.OriginalString); // not /apps/bbs//
    }

    // ------------------------------------------------ inbound 7plus: hide raw parts + placeholder

    private const string SevenPlusKey = "7p|10|FIELDS.JPG  |145173|5|138";

    /// <summary>Stores a raw 7plus part-bulletin and records it against the file identity.</summary>
    private Message SeedPart(int partNumber, int total, string subject)
    {
        Message part = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Bulletin,
            From = "M0XYZ",
            Recipients = ["ALL"],
            Subject = subject,
            Body = Encoding.Latin1.GetBytes("raw 7plus go_7+ part text\r"),
            ReceivedFrom = "GB7BPQ",
        });
        _store.RecordSevenPlusPart(SevenPlusKey, "FIELDS.JPG  ", 145173, total, 138, partNumber, part.Number);
        return part;
    }

    [Fact]
    public async Task Bulletins_HideRaw7plusPartMessages_ButShowAPlaceholderForTheIncompleteSet()
    {
        ClaimCallsign("tom", "M0LTE");
        Message p1 = SeedPart(1, total: 5, "FIELDS.P01 of 05");
        Message p2 = SeedPart(2, total: 5, "FIELDS.P02 of 05");
        using HttpClient client = await StartAsync();

        string bulletins = await client.GetStringAsync(new Uri("/bulletins", UriKind.Relative));

        // The raw part-bulletin subjects do NOT appear in the listing.
        Assert.DoesNotContain("FIELDS.P01", bulletins, StringComparison.Ordinal);
        Assert.DoesNotContain("FIELDS.P02", bulletins, StringComparison.Ordinal);
        // ...but the lightweight progress placeholder does, clearly distinguished and read-only. The
        // "N/M parts received" text only renders when a placeholder is present (the class name lives
        // in the always-present stylesheet, so assert on the visible text instead).
        Assert.Contains("FIELDS.JPG", bulletins, StringComparison.Ordinal);
        Assert.Contains("2/5 parts received", bulletins, StringComparison.Ordinal);
        Assert.Contains("<ul class=\"sevenplus-pending\">", bulletins, StringComparison.Ordinal);

        // The hidden messages are still in the store + still forward (only the listing hides them):
        // their read route still works for a direct link.
        string read = await client.GetStringAsync(new Uri($"/messages/{p1.Number}", UriKind.Relative));
        Assert.Contains("go_7+", read, StringComparison.Ordinal);
        Assert.Equal(2, _store.ListMessages(new MessageQuery { Type = MessageType.Bulletin }).Count);
        _ = p2;
    }

    [Fact]
    public async Task Inbox_PersonalPlaceholder_IsScopedToTheAddressee_NotLeakedToOtherUsers()
    {
        // A PERSONAL 7plus file in flight to M0AAA must show its placeholder ONLY in M0AAA's inbox —
        // never in M0BBB's (which would leak the private incoming filename + progress).
        ClaimCallsign("aaa", "M0AAA");
        ClaimCallsign("bbb", "M0BBB");
        Message part = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0AAA"],
            Subject = "SECRET.P01",
            Body = Encoding.Latin1.GetBytes("go_7+ private part\r"),
        });
        _store.RecordSevenPlusPart(SevenPlusKey, "SECRET.ZIP  ", 145173, 5, 138, 1, part.Number);

        using HttpClient aaa = await StartAsync(pdnUser: "aaa");
        string aaaInbox = await aaa.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("SECRET.ZIP", aaaInbox, StringComparison.Ordinal);
        Assert.Contains("1/5 parts received", aaaInbox, StringComparison.Ordinal);

        await _app!.DisposeAsync();
        _app = null;

        using HttpClient bbb = await StartAsync(pdnUser: "bbb");
        string bbbInbox = await bbb.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.DoesNotContain("SECRET.ZIP", bbbInbox, StringComparison.Ordinal);
        Assert.DoesNotContain("parts received", bbbInbox, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bulletins_AssembledFile_ListsNormally_WithItsAttachment_AndNoPlaceholder()
    {
        ClaimCallsign("tom", "M0LTE");
        SeedPart(1, total: 1, "FIELDS.P01");

        // Assemble: a synthesized local_only bulletin carrying the decoded file as an attachment.
        Message assembled = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Bulletin,
            From = "M0XYZ",
            Recipients = ["ALL"],
            Subject = "fields.jpg",
            Body = Encoding.Latin1.GetBytes("7plus file fields.jpg — 1 parts, assembled 2019-03-19.\r"),
            Attachments = [new MessageAttachment("fields.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })],
            LocalOnly = true,
        });
        Assert.True(_store.MarkSevenPlusAssembled(SevenPlusKey, assembled.Number));

        using HttpClient client = await StartAsync();
        string bulletins = await client.GetStringAsync(new Uri("/bulletins", UriKind.Relative));

        // The assembled-file message lists like any bulletin; no placeholder (the set is assembled).
        Assert.Contains("fields.jpg", bulletins, StringComparison.Ordinal);
        // No placeholder is rendered (the set is assembled) — assert on the visible markup, since the
        // CSS class name itself lives in the always-present stylesheet.
        Assert.DoesNotContain("<ul class=\"sevenplus-pending\">", bulletins, StringComparison.Ordinal);
        Assert.DoesNotContain("parts received", bulletins, StringComparison.Ordinal);
        Assert.Contains($"/messages/{assembled.Number}", bulletins, StringComparison.Ordinal);

        // Opening it shows the attachment download (rides the existing attachments work).
        string page = await client.GetStringAsync(new Uri($"/messages/{assembled.Number}", UriKind.Relative));
        Assert.Contains("Attachments", page, StringComparison.Ordinal);
        Assert.Contains($"/messages/{assembled.Number}/attachments/fields.jpg", page, StringComparison.Ordinal);

        HttpResponseMessage download = await client.GetAsync(
            new Uri($"/messages/{assembled.Number}/attachments/fields.jpg", UriKind.Relative));
        download.EnsureSuccessStatusCode();
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, await download.Content.ReadAsByteArrayAsync());
    }

    // ------------------------------------------------ compose file upload: 7plus / attachment / guard
    // The send-side file slice: a composer may attach a file and choose how it travels — 7plus-encode
    // (universal, appended to the body text) or a binary B2 attachment. Multipart parsing reads both
    // the file and the text fields; an oversize upload is rejected cleanly.

    /// <summary>
    /// Builds a multipart/form-data compose request: the text fields plus an optional file part named
    /// <c>file</c> with the file-handling <c>fileMode</c> radio (none / 7plus / attachment).
    /// </summary>
    private static MultipartFormDataContent ComposeMultipart(
        string type, string to, string subject, string body,
        string? at = null, byte[]? file = null, string? fileName = null, string? fileMode = null)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(type), "type" },
            { new StringContent(to), "to" },
            { new StringContent(at ?? ""), "at" },
            { new StringContent(subject), "subject" },
            { new StringContent(body), "body" },
            { new StringContent(fileMode ?? "none"), "fileMode" },
        };
        if (file is not null)
        {
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(part, "file", fileName ?? "upload.bin");
        }

        return content;
    }

    [Fact]
    public async Task ComposeWithFile_SevenPlus_AppendsValidPartsToBody_RoundTripsByteExact()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        // A byte spread (incl. high bytes + a NUL) that ONLY survives 7plus, not raw text.
        byte[] original = new byte[600];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)((i * 37) & 0xFF);
        }

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/compose", UriKind.Relative),
            ComposeMultipart("P", "G8ABC", "photo", "Here is the file.", file: original, fileName: "photo.jpg", fileMode: "7plus"));
        response.EnsureSuccessStatusCode();

        Message stored = Assert.Single(_store.ListMessages(new MessageQuery()));
        // The user's typed text survives ahead of the appended 7plus block.
        Assert.Contains("Here is the file.", stored.GetBodyText(), StringComparison.Ordinal);
        // It's a normal text message — no binary attachment, stays as composed (no inbound assembler).
        Assert.Empty(stored.Attachments);

        // Full round-trip through the REAL compose path: scan the stored body, assemble, byte-exact.
        IReadOnlyList<SevenPlusPart> parts = SevenPlusScanner.ExtractParts(stored.Body.Span);
        Assert.NotEmpty(parts);
        (bool complete, byte[]? content, _) = SevenPlusFile.TryAssemble(parts);
        Assert.True(complete);
        Assert.Equal(original, content);
    }

    [Fact]
    public async Task ComposeWithFile_Attachment_StoresMessageAttachment_AndBuildB2ObjectEmitsIt()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        byte[] file = [0x00, 0x01, 0x02, 0xFD, 0xFE, 0xFF];
        HttpResponseMessage response = await client.PostAsync(
            new Uri("/compose", UriKind.Relative),
            ComposeMultipart("P", "G8ABC", "binary", "See attached.", file: file, fileName: "data.bin", fileMode: "attachment"));
        response.EnsureSuccessStatusCode();

        Message stored = Assert.Single(_store.ListMessages(new MessageQuery()));
        Assert.Equal("See attached.\r", stored.GetBodyText()); // body verbatim, file is NOT appended
        MessageAttachment a = Assert.Single(stored.Attachments);
        Assert.Equal("data.bin", a.Name);
        Assert.Equal(file, a.Content.ToArray()); // byte-exact

        // Follow-on: a B2-enabled partner's outbound object carries it as a File: part (the relay path).
        var identity = new BbsIdentity { Callsign = "GB7PDN", HRoute = "#23.GBR.EURO", SoftwareVersion = "PDN0.1.0" };
        var partner = new Partner { Call = "GB7BPQ", AllowB2F = true, AtCalls = ["*"] };
        IReadOnlyList<OutboundItem> items = OutboundBuilder.Build(
            [_store.GetMessage(stored.Number)!], partner, identity, _time, NullLogger.Instance);
        ReadOnlyMemory<byte>? b2Object = Assert.Single(items).Wire.B2Object;
        Assert.NotNull(b2Object);
        B2Message decoded = B2Message.Decode(b2Object.Value.Span);
        B2Attachment emitted = Assert.Single(decoded.Files);
        Assert.Equal("data.bin", emitted.Name);
        Assert.Equal(file, emitted.Content.ToArray());
    }

    [Fact]
    public async Task ComposeWithFile_StripsPathComponentsFromTheName()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/compose", UriKind.Relative),
            ComposeMultipart("P", "G8ABC", "traversal", "x",
                file: [1, 2, 3], fileName: "../../etc/passwd", fileMode: "attachment"));
        response.EnsureSuccessStatusCode();

        Message stored = Assert.Single(_store.ListMessages(new MessageQuery()));
        MessageAttachment a = Assert.Single(stored.Attachments);
        Assert.Equal("passwd", a.Name); // no path components
    }

    [Fact]
    public async Task ComposeMultipart_NoFile_IsUnchanged_FormFieldsStillRead()
    {
        ClaimCallsign("tom", "M0LTE");
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        using HttpClient client = await StartAsync();

        // multipart with the text fields but NO file part: the dominant case, behaviour unchanged.
        HttpResponseMessage response = await client.PostAsync(
            new Uri("/compose", UriKind.Relative),
            ComposeMultipart("P", "G8ABC@GB7BPQ", "no file", "Body line one\nLine two", fileMode: "none"));
        response.EnsureSuccessStatusCode();

        Message stored = Assert.Single(_store.ListMessages(new MessageQuery()));
        Assert.Equal("M0LTE", stored.From);
        Assert.Equal("G8ABC", Assert.Single(stored.Recipients).ToCall);
        Assert.Equal("GB7BPQ", stored.At);
        Assert.Equal("Body line one\rLine two\r", stored.GetBodyText()); // body verbatim, CR discipline
        Assert.Empty(stored.Attachments);
    }

    [Fact]
    public async Task ComposeFormUrlEncoded_StillWorks_NoFile()
    {
        // The legacy form-urlencoded POST (no multipart) must keep working unchanged.
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/compose", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("type", "P"),
                new KeyValuePair<string, string>("to", "G8ABC"),
                new KeyValuePair<string, string>("subject", "urlencoded"),
                new KeyValuePair<string, string>("body", "Hello"),
            ]));
        response.EnsureSuccessStatusCode();

        Message stored = Assert.Single(_store.ListMessages(new MessageQuery()));
        Assert.Equal("Hello\r", stored.GetBodyText());
        Assert.Empty(stored.Attachments);
    }

    [Fact]
    public async Task ComposeWithOversizeFile_IsRejectedCleanly_NoMessageStored()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync(maxUploadBytes: 1024);

        byte[] tooBig = new byte[2048];
        HttpResponseMessage response = await client.PostAsync(
            new Uri("/compose", UriKind.Relative),
            ComposeMultipart("P", "G8ABC", "huge", "body", file: tooBig, fileName: "big.bin", fileMode: "attachment"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // rejected, not crashed
        string page = await response.Content.ReadAsStringAsync();
        Assert.Contains("too large", page, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_store.ListMessages(new MessageQuery())); // nothing stored
    }

    [Fact]
    public async Task ComposeForm_RendersFileInputAndModeChoice()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        string form = await client.GetStringAsync(new Uri("/compose", UriKind.Relative));
        Assert.Contains("multipart/form-data", form, StringComparison.Ordinal); // form posts multipart
        Assert.Contains("type=\"file\"", form, StringComparison.Ordinal);        // a file input
        Assert.Contains("name=\"file\"", form, StringComparison.Ordinal);
        Assert.Contains("name=\"fileMode\"", form, StringComparison.Ordinal);    // the handling choice
        Assert.Contains("7plus", form, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("B2", form, StringComparison.Ordinal);                   // the B2-only hint
    }

    // ------------------------------------------------ settings (interface-mode toggle)
    // The webmail follow-on: GET /settings shows the current plain/classic mode; POST /settings
    // flips + persists it through the SAME IUserSettingsStore the console session reads. Gated to
    // the signed-in user; prefix-correct URLs.

    [Fact]
    public async Task Settings_DefaultsToPlain_AndOffersBothModes()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri("/settings", UriKind.Relative));
        Assert.Contains("Settings", page, StringComparison.Ordinal);
        Assert.Contains("action=\"/settings\"", page, StringComparison.Ordinal);
        // A never-set user is plain (the mandate's default) → the plain radio is checked.
        Assert.Contains("value=\"plain\" checked", page, StringComparison.Ordinal);
        Assert.Contains("value=\"classic\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"classic\" checked", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_RendersTheSavedClassicMode()
    {
        ClaimCallsign("tom", "M0LTE");
        _settings.Save("M0LTE", new UserSettings { InterfaceMode = InterfaceMode.Classic });
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri("/settings", UriKind.Relative));
        Assert.Contains("value=\"classic\" checked", page, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"plain\" checked", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_Post_FlipsToClassic_AndPersists()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/settings", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("interface", "classic")]));
        response.EnsureSuccessStatusCode();
        string page = await response.Content.ReadAsStringAsync();
        Assert.Contains("Saved.", page, StringComparison.Ordinal);
        Assert.Contains("value=\"classic\" checked", page, StringComparison.Ordinal);

        // Persisted through the store — a fresh load (what the next console connect reads) shows it.
        Assert.Equal(InterfaceMode.Classic, _settings.Load("M0LTE").InterfaceMode);
    }

    [Fact]
    public async Task Settings_Post_FlipsBackToPlain_AndPersists()
    {
        ClaimCallsign("tom", "M0LTE");
        _settings.Save("M0LTE", new UserSettings { InterfaceMode = InterfaceMode.Classic });
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/settings", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("interface", "plain")]));
        response.EnsureSuccessStatusCode();

        Assert.Equal(InterfaceMode.Plain, _settings.Load("M0LTE").InterfaceMode);
    }

    [Fact]
    public async Task Settings_Post_PreservesOtherUserSettings()
    {
        // Flipping the interface mode must not clobber the user's other saved console prefs.
        ClaimCallsign("tom", "M0LTE");
        _settings.Save("M0LTE", new UserSettings { Qth = "Reading", PageLength = 20 });
        using HttpClient client = await StartAsync();

        await client.PostAsync(
            new Uri("/settings", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("interface", "classic")]));

        UserSettings saved = _settings.Load("M0LTE");
        Assert.Equal(InterfaceMode.Classic, saved.InterfaceMode);
        Assert.Equal("Reading", saved.Qth);
        Assert.Equal(20, saved.PageLength);
    }

    [Fact]
    public async Task Settings_UnmappedUser_GetsTheClaimForm_NotSettings()
    {
        // No callsign claimed → the WithCallsign gate sends the claim form, like every page.
        using HttpClient client = await StartAsync(pdnUser: "stranger");
        string page = await client.GetStringAsync(new Uri("/settings", UriKind.Relative));
        Assert.Contains("claim", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mailbox command surface", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_Post_IsScopedToTheSignedInUser()
    {
        // tom is M0LTE; the flip lands on M0LTE only, not some other callsign.
        ClaimCallsign("tom", "M0LTE");
        ClaimCallsign("alice", "G4ABC");
        using HttpClient client = await StartAsync(pdnUser: "tom");

        await client.PostAsync(
            new Uri("/settings", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("interface", "classic")]));

        Assert.Equal(InterfaceMode.Classic, _settings.Load("M0LTE").InterfaceMode);
        Assert.Null(_settings.Load("G4ABC").InterfaceMode); // untouched
    }

    [Fact]
    public async Task Settings_NavLink_AndPrefixCorrectUrls()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/bbs", autoRedirect: false);

        // The nav link to settings carries the mount prefix everywhere.
        string inbox = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("<a href=\"/apps/bbs/settings\">Settings</a>", inbox, StringComparison.Ordinal);

        // The settings form posts under the mount.
        string page = await client.GetStringAsync(new Uri("/settings", UriKind.Relative));
        Assert.Contains("action=\"/apps/bbs/settings\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/settings\"", page, StringComparison.Ordinal); // nothing escapes the mount
        // The mail-password forms also carry the mount prefix.
        Assert.Contains("action=\"/apps/bbs/settings/mailpw\"", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------ mail password (IMAP / external mail apps)
    // The credential an external mail client logs in with (callsign + this password). Set over the
    // gateway-authenticated settings page; stored Argon2id-hashed in the BBS user's mail_auth row.

    private const string GoodMailPw = "imap-secret-pw"; // ≥ BbsStore.MinMailPasswordLength

    [Fact]
    public async Task MailPassword_SettingsPage_ShowsSetForm_WhenNoneSet()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri("/settings", UriKind.Relative));
        Assert.Contains("Mail password", page, StringComparison.Ordinal);
        Assert.Contains("No mail password set yet", page, StringComparison.Ordinal);
        Assert.Contains("name=\"new\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"confirm\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MailPassword_Post_SetsIt_AndShowsConfirmation()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/settings/mailpw", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("new", GoodMailPw),
                new KeyValuePair<string, string>("confirm", GoodMailPw),
            ]));
        response.EnsureSuccessStatusCode();
        string page = await response.Content.ReadAsStringAsync();

        Assert.Contains("Mail password updated", page, StringComparison.Ordinal);
        Assert.Contains("A mail password is set", page, StringComparison.Ordinal);
        Assert.Contains("Remove mail password", page, StringComparison.Ordinal);
        // Stored + verifiable against the BBS store; never the plaintext.
        Assert.True(_store.VerifyMailPassword("M0LTE", GoodMailPw));
        Assert.DoesNotContain(GoodMailPw, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MailPassword_Post_Mismatch_IsRejected()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/settings/mailpw", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("new", GoodMailPw),
                new KeyValuePair<string, string>("confirm", GoodMailPw + "x"),
            ]));
        response.EnsureSuccessStatusCode();
        string page = await response.Content.ReadAsStringAsync();

        // The notice is HTML-encoded (the apostrophe → &#39;), so match the apostrophe-free part.
        Assert.Contains("Those passwords", page, StringComparison.Ordinal);
        Assert.False(_store.HasMailPassword("M0LTE")); // nothing stored
    }

    [Fact]
    public async Task MailPassword_Post_TooShort_IsRejected()
    {
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/settings/mailpw", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("new", "short"),
                new KeyValuePair<string, string>("confirm", "short"),
            ]));
        response.EnsureSuccessStatusCode();
        string page = await response.Content.ReadAsStringAsync();

        Assert.Contains("at least", page, StringComparison.OrdinalIgnoreCase);
        Assert.False(_store.HasMailPassword("M0LTE"));
    }

    [Fact]
    public async Task MailPassword_Clear_RemovesIt()
    {
        ClaimCallsign("tom", "M0LTE");
        _store.SetMailPassword("M0LTE", GoodMailPw);
        using HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.PostAsync(
            new Uri("/settings/mailpw/clear", UriKind.Relative), content: null);
        response.EnsureSuccessStatusCode();
        string page = await response.Content.ReadAsStringAsync();

        Assert.Contains("Mail password removed", page, StringComparison.Ordinal);
        Assert.False(_store.HasMailPassword("M0LTE"));
    }

    [Fact]
    public async Task MailPassword_Post_IsScopedToTheSignedInUser()
    {
        ClaimCallsign("tom", "M0LTE");
        ClaimCallsign("alice", "G4ABC");
        using HttpClient client = await StartAsync(pdnUser: "tom");

        await client.PostAsync(
            new Uri("/settings/mailpw", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("new", GoodMailPw),
                new KeyValuePair<string, string>("confirm", GoodMailPw),
            ]));

        Assert.True(_store.HasMailPassword("M0LTE"));
        Assert.False(_store.HasMailPassword("G4ABC")); // untouched
    }

    [Fact]
    public async Task MailPassword_UnmappedUser_GetsClaimForm_NotSettings()
    {
        using HttpClient client = await StartAsync(pdnUser: "stranger");
        HttpResponseMessage response = await client.PostAsync(
            new Uri("/settings/mailpw", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("new", GoodMailPw),
                new KeyValuePair<string, string>("confirm", GoodMailPw),
            ]));
        response.EnsureSuccessStatusCode();
        string page = await response.Content.ReadAsStringAsync();
        Assert.Contains("claim", page, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------ message-view + listing UX

    [Fact]
    public async Task ReadMessage_ShowsPlainTypeAndStatus_AndABackLink()
    {
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Bulletin,
            From = "G8ABC",
            Recipients = ["PACKET"],
            Subject = "Plain header",
            Body = Encoding.Latin1.GetBytes("hi\r"),
        });
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri($"/messages/{stored.Number}", UriKind.Relative));
        // Plain words for the reader — not the archaic [B/...] wire codes.
        Assert.Contains("Bulletin", page, StringComparison.Ordinal);
        Assert.DoesNotContain("[B/", page, StringComparison.Ordinal);
        // A contextual back link to the list it belongs to (a bulletin → Bulletins).
        Assert.Contains("Back to Bulletins", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMessage_PersonalLinksBackToTheInbox()
    {
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            Subject = "for me",
            Body = Encoding.Latin1.GetBytes("hi\r"),
        });
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri($"/messages/{stored.Number}", UriKind.Relative));
        Assert.Contains("Back to Inbox", page, StringComparison.Ordinal);
        Assert.Contains("Personal", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inbox_MarksUnreadPersonalsWithAReadIndicator_NotTheTerseCodeColumn()
    {
        ClaimCallsign("tom", "M0LTE");
        _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "G8ABC",
            Recipients = ["M0LTE"],
            Subject = "Unread one",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        using HttpClient client = await StartAsync();

        string inbox = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("unread-dot", inbox, StringComparison.Ordinal);        // the clear read/unread marker
        Assert.Contains("class=\"unread\"", inbox, StringComparison.Ordinal);  // the row is bold-flagged
        Assert.DoesNotContain(">St<", inbox, StringComparison.Ordinal);        // the archaic status-code column is gone
    }

    [Fact]
    public async Task ReadMessage_DecodesUtf8Body_NoMojibake()
    {
        ClaimCallsign("tom", "M0LTE");
        // A UTF-8 “smart quote” pair (U+201C/U+201D) — mojibakes to "â€¦" if forced through Latin-1.
        byte[] body = Encoding.UTF8.GetBytes("the “flag”\r");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal, From = "G8ABC", Recipients = ["M0LTE"], Subject = "utf8", Body = body,
        });
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri($"/messages/{stored.Number}", UriKind.Relative));
        Assert.Contains("“flag”", page, StringComparison.Ordinal); // the real smart quotes
        Assert.DoesNotContain("â", page, StringComparison.Ordinal);     // no "â" mojibake
    }

    [Fact]
    public async Task ReadMessage_LiftsRTraceIntoAFriendlyPath_AndShowsTheContent()
    {
        ClaimCallsign("tom", "M0LTE");
        byte[] body = Encoding.Latin1.GetBytes(
            "R:260614/1246Z 1207@GB7RDG.#42.GBR.EURO LinBPQ6.0.25\r" +
            "R:260614/1240Z 17277@VK2RZ.#SYD.NSW.AUS.OC LinBPQ6.0.25\r" +
            "\r" +
            "Actual message text.\r");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Bulletin, From = "G8ABC", Recipients = ["PACKET"], Subject = "traced", Body = body,
        });
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri($"/messages/{stored.Number}", UriKind.Relative));
        Assert.Contains("Actual message text.", page, StringComparison.Ordinal);   // content shown cleanly
        Assert.Contains("Relayed:", page, StringComparison.Ordinal);               // friendly path summary
        Assert.Contains("VK2RZ → GB7RDG", page, StringComparison.Ordinal);    // origin → here order
        Assert.Contains("Routing headers (2)", page, StringComparison.Ordinal);    // collapsible raw block
        // The trace sits ABOVE the body.
        Assert.True(page.IndexOf("Relayed:", StringComparison.Ordinal)
            < page.IndexOf("Actual message text.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadMessage_HasBackLinks_AtTopAndBottom()
    {
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Bulletin, From = "G8ABC", Recipients = ["PACKET"], Subject = "two backs", Body = Encoding.Latin1.GetBytes("hi\r"),
        });
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri($"/messages/{stored.Number}", UriKind.Relative));
        // One back link above the message, one below.
        Assert.Equal(2, page.Split("Back to Bulletins").Length - 1);
    }

    [Fact]
    public async Task Compose_FromDarkTheme_RendersDark_WithNoSpuriousErrorBox()
    {
        // Regression: GET /compose passed Theme(ctx) into the positional `error` slot, so the theme
        // string rendered in a red error box AND the dark class was dropped (a light page).
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync();

        HttpResponseMessage resp = await client.GetAsync(new Uri("/compose?pdn_embed=1&theme=dark", UriKind.Relative));
        resp.EnsureSuccessStatusCode();
        string page = await resp.Content.ReadAsStringAsync();

        Assert.Contains("<html class=\"dark\">", page, StringComparison.Ordinal); // dark applied
        Assert.DoesNotContain("class=\"err\"", page, StringComparison.Ordinal);   // no red error box
    }

    [Fact]
    public async Task Inbox_ShowsFriendlyDateAndTime_NotTheTerseYymmdd()
    {
        ClaimCallsign("tom", "M0LTE");
        _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal, From = "G8ABC", Recipients = ["M0LTE"], Subject = "dated", Body = Encoding.Latin1.GetBytes("x\r"),
        });
        using HttpClient client = await StartAsync();

        string inbox = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("Date (UTC)", inbox, StringComparison.Ordinal);
        Assert.Contains("2026-06-11 12:00", inbox, StringComparison.Ordinal); // the harness clock, date + time
    }

    [Fact]
    public async Task ReadMessage_LabelsNetworkId_NotBID_AndNavReadsSignedInAs()
    {
        ClaimCallsign("tom", "M0LTE");
        Message stored = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal, From = "G8ABC", Recipients = ["M0LTE"], Subject = "labels", Body = Encoding.Latin1.GetBytes("x\r"),
        });
        using HttpClient client = await StartAsync();

        string page = await client.GetStringAsync(new Uri($"/messages/{stored.Number}", UriKind.Relative));
        Assert.Contains("Network ID", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">BID<", page, StringComparison.Ordinal);
        Assert.Contains("signed in as M0LTE", page, StringComparison.Ordinal);
        Assert.DoesNotContain("de M0LTE", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------ forwarding view (sysop, read-only)
    // A sysop-only, read-only picture of how mail leaves this BBS: one panel per configured partner
    // with its dial config, the LIVE forward-queue depth, and the routing rules translated out of FBB
    // vocabulary. The nav tab + the route are sysop-gated; a non-sysop never sees the tab and a direct
    // GET 403s. It does NOT surface last-contact/last-error (the store persists no session outcome).

    [Fact]
    public async Task Forwarding_Sysop_ListsPartnerWithTranslatedRoutingAndLiveQueueDepth()
    {
        ClaimCallsign("tom", "G0SYS"); // the configured SysopCallsign
        _store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            Enabled = true,
            AtCalls = ["*"],                 // → "default uplink (catch-all)"
            HRoutes = ["GBR.EURO"],          // → "Areas"
            BbsHa = "GB7BPQ.#23.GBR.EURO",   // → "Partner address"
            ConnectScript = ["C GB7BPQ"],
            ForwardIntervalSeconds = 1800,   // → "every 30 min"
            ForwardNewImmediately = true,    // → "and as soon as a message is queued"
        });
        // One message queued to the partner → the live depth the store can answer.
        Message m = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            At = "GB7BPQ",
            Subject = "queued",
            Body = Encoding.Latin1.GetBytes("x\r"),
        });
        _store.EnqueueForwards(m.Number, ["GB7BPQ"]);

        using HttpClient client = await StartAsync(pdnUser: "tom");

        // The sysop sees the nav tab.
        string inbox = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains(">Forwarding</a>", inbox, StringComparison.Ordinal);

        string page = await client.GetStringAsync(new Uri("/forwarding", UriKind.Relative));
        Assert.Contains("GB7BPQ", page, StringComparison.Ordinal);
        Assert.Contains("auto-dial on", page, StringComparison.Ordinal);
        Assert.Contains("C GB7BPQ", page, StringComparison.Ordinal);                 // connect script
        Assert.Contains("every 30 min", page, StringComparison.Ordinal);             // schedule
        Assert.Contains("as soon as a message is queued", page, StringComparison.Ordinal);
        Assert.Contains("Default uplink", page, StringComparison.Ordinal);           // at:* translated
        Assert.Contains("GBR.EURO", page, StringComparison.Ordinal);                 // areas
        Assert.Contains("GB7BPQ.#23.GBR.EURO", page, StringComparison.Ordinal);      // partner HA
        Assert.Contains("message waiting", page, StringComparison.Ordinal);          // live queue depth
    }

    [Fact]
    public async Task Forwarding_DisabledPartner_ShowsAutoDialOff()
    {
        ClaimCallsign("tom", "G0SYS");
        _store.UpsertPartner(new Partner { Call = "GB7XYZ", Enabled = false, AtCalls = ["*"] });
        using HttpClient client = await StartAsync(pdnUser: "tom");

        string page = await client.GetStringAsync(new Uri("/forwarding", UriKind.Relative));
        Assert.Contains("GB7XYZ", page, StringComparison.Ordinal);
        Assert.Contains("auto-dial off", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forwarding_NoPartners_ShowsEmptyState()
    {
        ClaimCallsign("tom", "G0SYS");
        using HttpClient client = await StartAsync(pdnUser: "tom");

        string page = await client.GetStringAsync(new Uri("/forwarding", UriKind.Relative));
        Assert.Contains("No forwarding partners", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forwarding_NonSysop_Gets403_AndSeesNoNavTab()
    {
        // A regular (non-sysop) user: the route 403s and the nav tab is never rendered for them.
        ClaimCallsign("tom", "M0LTE");
        using HttpClient client = await StartAsync(pdnUser: "tom");

        string inbox = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.DoesNotContain(">Forwarding</a>", inbox, StringComparison.Ordinal);

        HttpResponseMessage response = await client.GetAsync(new Uri("/forwarding", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("sysop only", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------ forwarding EDITOR (sysop): forms + YAML
    // Store-first: both the per-partner forms and the bulk YAML block edit the SQLite store (the
    // source of truth). Every mutation fires OnPartnersChanged (the scheduler reconcile seam); forward-
    // now fires OnForwardNow (the nudge). A change in one surface shows in the other (parity). The whole
    // editor is sysop-gated. bbs.yaml is seed-only and not touched by the editor.

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    [Fact]
    public async Task Editor_ForwardingTab_HasFormsAndYamlSwitch()
    {
        ClaimCallsign("tom", "G0SYS");
        using HttpClient client = await StartAsync(pdnUser: "tom");

        string forms = await client.GetStringAsync(new Uri("/forwarding", UriKind.Relative));
        Assert.Contains(">Forms</a>", forms, StringComparison.Ordinal);
        Assert.Contains(">YAML</a>", forms, StringComparison.Ordinal);
        Assert.Contains("Add partner", forms, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editor_CreatePartnerViaForm_WritesStore_AndFiresReconcile()
    {
        ClaimCallsign("tom", "G0SYS");
        using HttpClient client = await StartAsync(pdnUser: "tom", autoRedirect: false);

        HttpResponseMessage post = await client.PostAsync(new Uri("/forwarding/partner", UriKind.Relative), Form(
            ("call", "gb7bpq"),
            ("connectScript", "C GB7BPQ\nBBS"),
            ("at", "*"),
            ("hr", "GBR.EURO"),
            ("bbsHa", "GB7BPQ.#23.GBR.EURO"),
            ("intervalMinutes", "30"),
            ("conTimeoutSeconds", "45"),
            ("maxRx", "50000"),
            ("maxTx", "40000"),
            ("enabled", "1"),
            ("sendImmediately", "1"),
            ("allowB2", "1")));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        Partner? p = _store.GetPartner("GB7BPQ");
        Assert.NotNull(p);
        Assert.Equal(["C GB7BPQ", "BBS"], p!.ConnectScript);
        Assert.Equal(["*"], p.AtCalls);
        Assert.Equal(["GBR.EURO"], p.HRoutes);
        Assert.Equal("GB7BPQ.#23.GBR.EURO", p.BbsHa);
        Assert.Equal(30 * 60, p.ForwardIntervalSeconds);
        Assert.Equal(45, p.ConTimeoutSeconds);
        Assert.Equal(50000, p.MaxRxSize);
        Assert.Equal(40000, p.MaxTxSize);
        Assert.True(p.Enabled);
        Assert.True(p.ForwardNewImmediately);
        Assert.True(p.AllowB2F);
        Assert.False(p.Collect); // checkbox unchecked → not posted → false
        Assert.Equal(1, _partnersChanged);
    }

    [Fact]
    public async Task Editor_EditPartnerViaForm_UpdatesStore_AndFiresReconcile()
    {
        ClaimCallsign("tom", "G0SYS");
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", Enabled = true, AtCalls = ["*"], ForwardNewImmediately = true });
        using HttpClient client = await StartAsync(pdnUser: "tom", autoRedirect: false);

        // The edit form is reachable at ?edit=CALL.
        string editForm = await client.GetStringAsync(new Uri("/forwarding?edit=GB7BPQ", UriKind.Relative));
        Assert.Contains("Edit GB7BPQ", editForm, StringComparison.Ordinal);

        // Disable it + clear send-immediately + add an area.
        HttpResponseMessage post = await client.PostAsync(new Uri("/forwarding/partner", UriKind.Relative), Form(
            ("call", "GB7BPQ"),
            ("at", "*"),
            ("hr", "GBR.EURO DEU.EURO"),
            ("intervalMinutes", "60"),
            ("conTimeoutSeconds", "60"),
            ("maxRx", "99999"),
            ("maxTx", "99999")));
        // enabled / sendImmediately checkboxes not posted → both turn off.
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        Partner? p = _store.GetPartner("GB7BPQ");
        Assert.NotNull(p);
        Assert.False(p!.Enabled);
        Assert.False(p.ForwardNewImmediately);
        Assert.Equal(["GBR.EURO", "DEU.EURO"], p.HRoutes);
        Assert.Equal(1, _partnersChanged);
    }

    [Fact]
    public async Task Editor_DeletePartnerViaForm_RemovesFromStore_AndFiresReconcile()
    {
        ClaimCallsign("tom", "G0SYS");
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        using HttpClient client = await StartAsync(pdnUser: "tom", autoRedirect: false);

        HttpResponseMessage post = await client.PostAsync(new Uri("/forwarding/partner/delete", UriKind.Relative),
            Form(("call", "GB7BPQ")));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        Assert.Null(_store.GetPartner("GB7BPQ"));
        Assert.Equal(1, _partnersChanged);
    }

    [Fact]
    public async Task Editor_ForwardNow_NudgesThatPartner()
    {
        ClaimCallsign("tom", "G0SYS");
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        using HttpClient client = await StartAsync(pdnUser: "tom", autoRedirect: false);

        HttpResponseMessage post = await client.PostAsync(new Uri("/forwarding/partner/forward-now", UriKind.Relative),
            Form(("call", "GB7BPQ")));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        Assert.Equal(["GB7BPQ"], _forwardNow);
        Assert.Equal(0, _partnersChanged); // forward-now is a nudge, not a config mutation
    }

    [Fact]
    public async Task Editor_InvalidCallsign_IsRejected_WithNoStoreChange()
    {
        ClaimCallsign("tom", "G0SYS");
        using HttpClient client = await StartAsync(pdnUser: "tom");

        HttpResponseMessage post = await client.PostAsync(new Uri("/forwarding/partner", UriKind.Relative),
            Form(("call", "NOTACALL"))); // no digit in the call position → not callsign-shaped
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Contains("look like a callsign", await post.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(_store.ListPartners());
        Assert.Equal(0, _partnersChanged);
    }

    [Fact]
    public async Task Editor_YamlTab_RendersStorePartnersAsYaml()
    {
        ClaimCallsign("tom", "G0SYS");
        _store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            AtCalls = ["*"],
            HRoutes = ["GBR.EURO"],
            HRoutesP = ["GBR.EURO"],
            BbsHa = "GB7BPQ.#23.GBR.EURO",
            ConnectScript = ["C GB7BPQ"],
        });
        using HttpClient client = await StartAsync(pdnUser: "tom");

        string yaml = await client.GetStringAsync(new Uri("/forwarding?tab=yaml", UriKind.Relative));
        // The textarea carries the partners: block (HTML-encoded).
        Assert.Contains("GB7BPQ", yaml, StringComparison.Ordinal);
        Assert.Contains("connectScript", yaml, StringComparison.Ordinal);
        Assert.Contains("GBR.EURO", yaml, StringComparison.Ordinal);
        Assert.Contains("<textarea", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editor_SaveYaml_AppliesAddRemoveChange_ToStore()
    {
        ClaimCallsign("tom", "G0SYS");
        // Two partners already in the store; the YAML will keep one (changed), drop the other, add a new one.
        _store.UpsertPartner(new Partner { Call = "GB7OLD", AtCalls = ["*"] });
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"], Enabled = true });
        using HttpClient client = await StartAsync(pdnUser: "tom", autoRedirect: false);

        string yaml = """
            partners:
              - call: GB7BPQ
                enabled: false
                at: ["*"]
              - call: GB7NEW
                at: ["EURO"]
            """;
        HttpResponseMessage post = await client.PostAsync(new Uri("/forwarding/yaml", UriKind.Relative),
            Form(("yaml", yaml)));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        Assert.Null(_store.GetPartner("GB7OLD"));               // removed (absent from YAML)
        Partner? bpq = _store.GetPartner("GB7BPQ");
        Assert.NotNull(bpq);
        Assert.False(bpq!.Enabled);                              // changed
        Partner? added = _store.GetPartner("GB7NEW");
        Assert.NotNull(added);                                   // added
        Assert.Equal(["EURO"], added!.AtCalls);
        Assert.Equal(1, _partnersChanged);
    }

    [Fact]
    public async Task Editor_SaveInvalidYaml_ShowsError_AndDoesNotTouchStore()
    {
        ClaimCallsign("tom", "G0SYS");
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        using HttpClient client = await StartAsync(pdnUser: "tom");

        HttpResponseMessage post = await client.PostAsync(new Uri("/forwarding/yaml", UriKind.Relative),
            Form(("yaml", "partners:\n  - call: GB7BPQ\n   bad: : indent")));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        string body = await post.Content.ReadAsStringAsync();
        Assert.Contains("class=\"err\"", body, StringComparison.Ordinal);
        // The store is untouched: the original partner is still exactly as it was.
        Partner? p = _store.GetPartner("GB7BPQ");
        Assert.NotNull(p);
        Assert.Equal(0, _partnersChanged);
    }

    [Fact]
    public async Task Editor_SaveYamlWithInvalidCall_ShowsError_AndDoesNotTouchStore()
    {
        ClaimCallsign("tom", "G0SYS");
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        using HttpClient client = await StartAsync(pdnUser: "tom");

        HttpResponseMessage post = await client.PostAsync(new Uri("/forwarding/yaml", UriKind.Relative),
            Form(("yaml", "partners:\n  - call: NOTACALL\n    at: [\"*\"]")));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Contains("look like a callsign", await post.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // No GB7NEW added, GB7BPQ unchanged, nothing deleted.
        Assert.Single(_store.ListPartners());
        Assert.NotNull(_store.GetPartner("GB7BPQ"));
        Assert.Equal(0, _partnersChanged);
    }

    [Fact]
    public async Task Editor_Parity_FormEditAppearsInYaml_AndYamlEditAppearsInForms()
    {
        ClaimCallsign("tom", "G0SYS");
        using HttpClient client = await StartAsync(pdnUser: "tom");

        // 1) Create via the FORM, then see it in the YAML tab.
        await client.PostAsync(new Uri("/forwarding/partner", UriKind.Relative), Form(
            ("call", "GB7BPQ"), ("at", "*"), ("intervalMinutes", "60"), ("conTimeoutSeconds", "60"),
            ("maxRx", "99999"), ("maxTx", "99999"), ("enabled", "1")));
        string yaml = await client.GetStringAsync(new Uri("/forwarding?tab=yaml", UriKind.Relative));
        Assert.Contains("GB7BPQ", yaml, StringComparison.Ordinal);

        // 2) Change via the YAML tab, then see it in the FORMS tab.
        await client.PostAsync(new Uri("/forwarding/yaml", UriKind.Relative), Form(("yaml", """
            partners:
              - call: GB7BPQ
                at: ["*"]
              - call: GB7ZZZ
                at: ["EURO"]
            """)));
        string forms = await client.GetStringAsync(new Uri("/forwarding", UriKind.Relative));
        Assert.Contains("GB7ZZZ", forms, StringComparison.Ordinal); // the YAML-added partner shows as a card
    }

    [Fact]
    public async Task Editor_YamlRoundTrip_IsStable()
    {
        ClaimCallsign("tom", "G0SYS");
        // A partner exercising the round-trip-risky fields: non-default bools (enabled false,
        // sendImmediately false), collect true, a null-ish BbsHa, multi-line script, multiple list items.
        _store.UpsertPartner(new Partner
        {
            Call = "GB7BPQ",
            Enabled = false,
            ForwardNewImmediately = false,
            Collect = true,
            ConnectScript = ["C GB7BPQ", "BBS"],
            AtCalls = ["*", "GB7XYZ"],
            ToCalls = ["SYSOP"],
            HRoutes = ["GBR.EURO"],
            HRoutesP = ["GBR.EURO"],
            ForwardIntervalSeconds = 45 * 60,
            ConTimeoutSeconds = 30,
            MaxRxSize = 12345,
            MaxTxSize = 6789,
            AllowB2F = true,
        });

        string first = Bbs.Host.Web.PartnerYaml.Serialize(_store.ListPartners());
        IReadOnlyList<Partner> reparsed = Bbs.Host.Web.PartnerYaml.Parse(first);
        string second = Bbs.Host.Web.PartnerYaml.Serialize(reparsed);
        Assert.Equal(first, second); // serialise → parse → serialise is byte-stable

        // And the reparsed partner equals the stored one on the fields the config shape carries.
        Partner p = Assert.Single(reparsed);
        Assert.False(p.Enabled);
        Assert.False(p.ForwardNewImmediately);
        Assert.True(p.Collect);
        Assert.Equal(["C GB7BPQ", "BBS"], p.ConnectScript);
        Assert.Equal(["*", "GB7XYZ"], p.AtCalls);
        Assert.Equal(["SYSOP"], p.ToCalls);
        Assert.Equal(45 * 60, p.ForwardIntervalSeconds);
        Assert.Equal(30, p.ConTimeoutSeconds);
        Assert.Equal(12345, p.MaxRxSize);
        Assert.Equal(6789, p.MaxTxSize);
        Assert.True(p.AllowB2F);
    }

    [Fact]
    public async Task Editor_ForwardedPrefixAndEmbed_AreThreadedThroughLinksAndForms()
    {
        ClaimCallsign("tom", "G0SYS");
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        using HttpClient client = await StartAsync(pdnUser: "tom", forwardedPrefix: "/apps/bbs");

        // Slot/embed: pdn renders us in a borderless iframe with ?pdn_embed=1. The LIST view's
        // Add button, per-card POST forms, and the YAML tab link all carry the mount prefix +
        // ?pdn_embed=1; the POST forms also carry the hidden embed field so it survives the POST.
        string forms = await client.GetStringAsync(new Uri("/forwarding?pdn_embed=1", UriKind.Relative));
        Assert.Contains("href=\"/apps/bbs/forwarding?add=1&pdn_embed=1\"", forms, StringComparison.Ordinal);
        Assert.Contains("/apps/bbs/forwarding/partner/forward-now?pdn_embed=1", forms, StringComparison.Ordinal);
        Assert.Contains("href=\"/apps/bbs/forwarding?tab=yaml&pdn_embed=1", forms, StringComparison.Ordinal);
        Assert.Contains("name=\"pdn_embed\" value=\"1\"", forms, StringComparison.Ordinal);

        // The focused Add form (?add=1) posts to the prefixed + embedded create route.
        string addForm = await client.GetStringAsync(new Uri("/forwarding?add=1&pdn_embed=1", UriKind.Relative));
        Assert.Contains("action=\"/apps/bbs/forwarding/partner?pdn_embed=1\"", addForm, StringComparison.Ordinal);
        Assert.Contains("name=\"pdn_embed\" value=\"1\"", addForm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editor_AddIsAFocusedView_NotInlineInTheList()
    {
        ClaimCallsign("tom", "G0SYS");
        _store.UpsertPartner(new Partner { Call = "GB7BPQ", AtCalls = ["*"] });
        using HttpClient client = await StartAsync(pdnUser: "tom");

        // The list view offers an "Add partner" button, NOT an inline new-partner form.
        string list = await client.GetStringAsync(new Uri("/forwarding", UriKind.Relative));
        Assert.Contains("/forwarding?add=1", list, StringComparison.Ordinal);                 // the Add button
        Assert.DoesNotContain("name=\"call\" placeholder", list, StringComparison.Ordinal);   // no inline call input

        // The focused Add view renders the form alone — editable callsign, "Add partner" + Cancel.
        string add = await client.GetStringAsync(new Uri("/forwarding?add=1", UriKind.Relative));
        Assert.Contains("name=\"call\" placeholder", add, StringComparison.Ordinal);
        Assert.Contains(">Add partner</button>", add, StringComparison.Ordinal);
        Assert.Contains(">Cancel</a>", add, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editor_NonSysop_AllMutationRoutes_Get403_WithNoStoreChange()
    {
        ClaimCallsign("tom", "M0LTE"); // not the sysop
        using HttpClient client = await StartAsync(pdnUser: "tom");

        HttpResponseMessage create = await client.PostAsync(new Uri("/forwarding/partner", UriKind.Relative),
            Form(("call", "GB7BPQ"), ("at", "*")));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        HttpResponseMessage del = await client.PostAsync(new Uri("/forwarding/partner/delete", UriKind.Relative),
            Form(("call", "GB7BPQ")));
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);

        HttpResponseMessage fwd = await client.PostAsync(new Uri("/forwarding/partner/forward-now", UriKind.Relative),
            Form(("call", "GB7BPQ")));
        Assert.Equal(HttpStatusCode.Forbidden, fwd.StatusCode);

        HttpResponseMessage yaml = await client.PostAsync(new Uri("/forwarding/yaml", UriKind.Relative),
            Form(("yaml", "partners:\n  - call: GB7BPQ\n    at: [\"*\"]")));
        Assert.Equal(HttpStatusCode.Forbidden, yaml.StatusCode);

        Assert.Empty(_store.ListPartners());
        Assert.Equal(0, _partnersChanged);
        Assert.Empty(_forwardNow);
    }
}
