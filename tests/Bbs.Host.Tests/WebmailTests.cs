using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
            BbsCallsign = "GB7PDN",
            SysopCallsign = "G0SYS", // distinct from the test users — sysop may read/kill anything
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
}
