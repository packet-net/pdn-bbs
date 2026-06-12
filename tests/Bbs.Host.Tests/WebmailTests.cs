using System.Net;
using System.Text;
using Bbs.Core;
using Bbs.Host.Web;
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
    private WebApplication? _app;

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
        string pdnUser = "tom", bool gatewayHeader = true, string? forwardedPrefix = null, bool autoRedirect = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        Webmail.Map(_app, new WebmailOptions
        {
            Store = _store,
            Routing = _routing,
            BbsCallsign = "GB7PDN",
            SysopCallsign = "G0SYS", // distinct from the test users — sysop may read/kill anything
        });
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
}
