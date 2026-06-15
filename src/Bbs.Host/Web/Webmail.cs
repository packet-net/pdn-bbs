using System.Globalization;
using System.Net;
using System.Text;
using Bbs.Console;
using Bbs.Core;
using Bbs.SevenPlus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Bbs.Host.Web;

/// <summary>Webmail composition inputs.</summary>
public sealed record WebmailOptions
{
    /// <summary>The message store.</summary>
    public required BbsStore Store { get; init; }

    /// <summary>Routes composed messages into the forward queues.</summary>
    public required RoutingService Routing { get; init; }

    /// <summary>
    /// Called AFTER any partner mutation through the forwarding editor (create / edit / delete, and
    /// the bulk YAML apply). Wired in <c>HostComposition</c> to <see cref="Bbs.Host.Forwarding.ForwardingScheduler.Reconcile"/>
    /// so a newly created/enabled partner gets a forwarding loop immediately and a deleted/disabled
    /// one is reaped, without waiting for the scheduler's periodic re-sweep. Null in tests/standalone
    /// that don't run a scheduler — the store mutation still lands; only the reconcile is skipped.
    /// </summary>
    public Action? OnPartnersChanged { get; init; }

    /// <summary>
    /// "Forward now" for a single partner — wired to <see cref="Bbs.Host.Forwarding.ForwardingScheduler.Nudge"/>
    /// (via <see cref="RoutingService.NudgePartner"/>) so the sysop can dial a partner on demand from
    /// the editor. Null when no scheduler is running (the button still renders but is a no-op).
    /// </summary>
    public Action<string>? OnForwardNow { get; init; }

    /// <summary>
    /// The per-user console preferences store (the same singleton the console session uses —
    /// <c>HostComposition</c> wires the <see cref="Bbs.Host.Sessions.JsonUserSettingsStore"/>).
    /// Backs the <c>/settings</c> interface-mode toggle so a webmail flip is the same persisted
    /// choice the next console connect reads.
    /// </summary>
    public required IUserSettingsStore Settings { get; init; }

    /// <summary>
    /// The station's connect identity — the SSID'd callsign you connect to (e.g. <c>M9YYY-1</c>),
    /// shown in the page title and header. This is deliberately NOT the SSID-less mail-namespace
    /// own-call: a user's mail address (<c>M0LTE@M9YYY.#42.GBR.EURO</c>), BIDs and R-lines stay
    /// SSID-less and come from the store/routing-engine own-call, not from here. (The title was
    /// twice mistakenly pointed at the bare mail call, dropping the SSID — hence the explicit name.)
    /// </summary>
    public required string StationCallsign { get; init; }

    /// <summary>The sysop callsign (sysop visibility in webmail), or empty.</summary>
    public string SysopCallsign { get; init; } = "";

    /// <summary>Rows per page on the inbox/bulletins lists.</summary>
    public int PageSize { get; init; } = 25;

    /// <summary>
    /// Max accepted compose-upload size in bytes (the send-side file slice). A sane ceiling so a
    /// stray huge upload is rejected cleanly rather than encoded/stored — not the partner MaxTx (a
    /// composed file routes/forwards as a normal message and 7plus-encoding expands it ~12%, so the
    /// per-partner cap is enforced downstream in <see cref="Bbs.Host.Forwarding.OutboundBuilder"/>).
    /// Default 256 KiB.
    /// </summary>
    public int MaxUploadBytes { get; init; } = 256 * 1024;
}

/// <summary>
/// The webmail surface (design.md decision 4): server-rendered HTML on a loopback bind,
/// trusting the pdn app-gateway identity contract — every request must carry
/// <c>X-Pdn-Gateway: 1</c> (else 403) and identity arrives in <c>X-Pdn-User</c>. pdn
/// usernames map to BBS callsigns through the Core user table
/// (<see cref="User.PdnUsername"/>); an unmapped username gets a claim-your-callsign form.
/// Surfaces: inbox (personals to my callsign), bulletins (paged), read, compose (P/B
/// through the same store path as the console — BID generation + routing enqueue),
/// kill-my-message, settings, and a sysop-only read-only forwarding view. No JS.
/// </summary>
public static class Webmail
{
    /// <summary>Maps the webmail routes and the gateway-trust middleware onto <paramref name="app"/>.</summary>
    public static void Map(WebApplication app, WebmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        app.Use(async (context, next) =>
        {
            // The gateway contract (packet.net docs/app-gateway.md): pdn strips any
            // client-supplied copy of these headers before injecting its own, and the
            // loopback bind means only pdn can reach us.
            if (context.Request.Headers["X-Pdn-Gateway"] != "1")
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: pdn gateway only.").ConfigureAwait(false);
                return;
            }

            string user = context.Request.Headers["X-Pdn-User"].ToString();
            if (string.IsNullOrWhiteSpace(user))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: sign in to pdn first.").ConfigureAwait(false);
                return;
            }

            context.Items[PdnUserKey] = user;

            // The public mount point (e.g. "/apps/bbs") when proxied through the gateway;
            // absent (→ "") on direct loopback access. Every absolute URL we render or
            // redirect to must carry it, or the browser escapes to pdn's root.
            context.Items[PrefixKey] = context.Request.Headers["X-Forwarded-Prefix"].ToString().TrimEnd('/');

            // Slot/headless mode: pdn renders the app inside its own chrome via a borderless
            // iframe carrying ?pdn_embed=1 (an X-Pdn-Embed header is also honoured). When embedded
            // we drop our own outer identity bar (the panel already shows the app name) so there's
            // no double chrome — and the signal MUST persist across navigation, so we thread it
            // through every rendered link and form (see U/Page/EmbedField). Server-rendered + no-JS
            // means navigation is real link clicks / form posts; the flag rides those.
            context.Items[EmbedKey] = context.Request.Query["pdn_embed"] == "1"
                || context.Request.Headers["X-Pdn-Embed"] == "1";

            // Theme handoff (panel → app): an iframe is a separate document that can't see the
            // panel's manual `.dark` class, so the BBS would otherwise fall back to
            // prefers-color-scheme (the OS) — a mismatch with a manual panel toggle. pdn appends
            // the panel's active theme as ?theme=dark|light on the slot iframe src (an X-Pdn-Theme
            // header is also honoured). We persist it via a same-origin first-party cookie (the
            // embed iframe is same-origin under the gateway) so it survives in-iframe navigation
            // WITHOUT having to thread the param through every link/form. Resolution order each
            // request: query ?? cookie; when the query supplied it we (re)write the cookie so a
            // panel toggle (which reloads the iframe with a new ?theme=) updates the stored value.
            context.Items[ThemeKey] = ResolveTheme(context);

            // Server-rendered, per-user, dynamic content (mail + the inline CSS) must never be cached
            // by the browser: a cached iframe page shows stale mail AND stale styling (an app update
            // wouldn't render until a hard refresh). no-store keeps the slot iframe always current.
            context.Response.Headers.CacheControl = "no-store";

            await next().ConfigureAwait(false);
        });

        app.MapGet("/", (HttpContext ctx, int? page) => WithCallsign(ctx, options,
            (prefix, call) => Inbox(options, prefix, call, page ?? 1, Embed(ctx), Theme(ctx))));

        app.MapGet("/bulletins", (HttpContext ctx, int? page) => WithCallsign(ctx, options,
            (prefix, call) => Bulletins(options, prefix, call, page ?? 1, Embed(ctx), Theme(ctx))));

        app.MapGet("/messages/{number:long}", (HttpContext ctx, long number) => WithCallsign(ctx, options,
            (prefix, call) => ReadMessage(options, prefix, call, number, Embed(ctx), Theme(ctx))));

        app.MapGet("/messages/{number:long}/attachments/{name}", (HttpContext ctx, long number, string name) =>
            WithCallsign(ctx, options, (_, call) => DownloadAttachment(options, call, number, name)));

        app.MapGet("/compose", (HttpContext ctx, string? to, string? type) => WithCallsign(ctx, options,
            // theme: NAMED — it must land in `theme`, not the positional `error` slot (which would
            // render the theme string in a red error box AND drop the dark class → a light page).
            (prefix, call) => ComposeForm(options, prefix, call, to, type, Embed(ctx), theme: Theme(ctx))));

        app.MapPost("/compose", async (HttpContext ctx) =>
        {
            string user = PdnUser(ctx);
            string prefix = Prefix(ctx);
            bool embed = Embed(ctx);
            string? call = FindCallsign(options.Store, user);
            if (call is null)
            {
                return Results.Redirect(U(prefix, "/", embed));
            }

            // ReadFormAsync transparently parses BOTH application/x-www-form-urlencoded (the legacy
            // no-file case) and multipart/form-data (the file-upload case): text fields land in
            // form[...] and an uploaded file in form.Files. The pdn app-gateway reverse-proxies the
            // raw request body to our loopback upstream (manifest ui.upstream) with the Content-Type
            // intact, so multipart flows through unmodified — there is no body rewrite or size cap in
            // the gateway/app-package wiring (pdn carries zero BBS-specific code). We cap the upload
            // ourselves below (WebmailOptions.MaxUploadBytes) rather than relying on a gateway limit.
            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return Compose(options, prefix, call, form, form.Files.GetFile("file"), Embed(ctx), Theme(ctx));
        });

        app.MapPost("/messages/{number:long}/kill", (HttpContext ctx, long number) => WithCallsign(ctx, options,
            (prefix, call) => Kill(options, prefix, call, number, Embed(ctx))));

        app.MapPost("/claim", async (HttpContext ctx) =>
        {
            string user = PdnUser(ctx);
            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return Claim(options, Prefix(ctx), user, form["callsign"].ToString(), Embed(ctx), Theme(ctx));
        });

        app.MapGet("/settings", (HttpContext ctx) => WithCallsign(ctx, options,
            (prefix, call) => SettingsPage(options, prefix, call, Embed(ctx), theme: Theme(ctx))));

        app.MapPost("/settings", async (HttpContext ctx) =>
        {
            string prefix = Prefix(ctx);
            bool embed = Embed(ctx);
            string? call = FindCallsign(options.Store, PdnUser(ctx));
            if (call is null)
            {
                return Results.Redirect(U(prefix, "/", embed));
            }

            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return SaveSettings(options, prefix, call, form["interface"].ToString(), embed, Theme(ctx));
        });

        app.MapPost("/settings/mailpw", async (HttpContext ctx) =>
        {
            string prefix = Prefix(ctx);
            bool embed = Embed(ctx);
            string? call = FindCallsign(options.Store, PdnUser(ctx));
            if (call is null)
            {
                return Results.Redirect(U(prefix, "/", embed));
            }

            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return SaveMailPassword(options, prefix, call, form["new"].ToString(), form["confirm"].ToString(), embed, Theme(ctx));
        });

        app.MapPost("/settings/mailpw/clear", (HttpContext ctx) => WithCallsign(ctx, options,
            (prefix, call) => ClearMailPassword(options, prefix, call, Embed(ctx), Theme(ctx))));

        // Sysop-only forwarding EDITOR: the Forms | YAML switch over the store (the source of truth).
        // Gated inside each handler (a non-sysop gets a 403 page; the nav tab is only rendered for the
        // sysop). Both surfaces — the per-partner forms and the bulk YAML block — read/write the same
        // store, so a change in one shows in the other.
        app.MapGet("/forwarding", (HttpContext ctx, string? tab, string? edit, string? notice) => WithCallsign(ctx, options,
            (prefix, call) => Forwarding(options, prefix, call, tab, edit, Embed(ctx), Theme(ctx), notice: notice)));

        app.MapPost("/forwarding/partner", async (HttpContext ctx) =>
        {
            string prefix = Prefix(ctx);
            bool embed = Embed(ctx);
            string? call = FindCallsign(options.Store, PdnUser(ctx));
            if (call is null)
            {
                return Results.Redirect(U(prefix, "/", embed));
            }

            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return SavePartner(options, prefix, call, form, embed, Theme(ctx));
        });

        app.MapPost("/forwarding/partner/delete", async (HttpContext ctx) =>
        {
            string prefix = Prefix(ctx);
            bool embed = Embed(ctx);
            string? call = FindCallsign(options.Store, PdnUser(ctx));
            if (call is null)
            {
                return Results.Redirect(U(prefix, "/", embed));
            }

            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return DeletePartnerPost(options, prefix, call, form["call"].ToString(), embed, Theme(ctx));
        });

        app.MapPost("/forwarding/partner/forward-now", async (HttpContext ctx) =>
        {
            string prefix = Prefix(ctx);
            bool embed = Embed(ctx);
            string? call = FindCallsign(options.Store, PdnUser(ctx));
            if (call is null)
            {
                return Results.Redirect(U(prefix, "/", embed));
            }

            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return ForwardNow(options, prefix, call, form["call"].ToString(), embed, Theme(ctx));
        });

        app.MapPost("/forwarding/yaml", async (HttpContext ctx) =>
        {
            string prefix = Prefix(ctx);
            bool embed = Embed(ctx);
            string? call = FindCallsign(options.Store, PdnUser(ctx));
            if (call is null)
            {
                return Results.Redirect(U(prefix, "/", embed));
            }

            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return SaveYaml(options, prefix, call, form["yaml"].ToString(), embed, Theme(ctx));
        });
    }

    private const string PdnUserKey = "pdnUser";
    private const string PrefixKey = "pdnPrefix";
    private const string EmbedKey = "pdnEmbed";
    private const string ThemeKey = "pdnTheme";

    /// <summary>The same-origin first-party cookie that persists the panel's theme across in-iframe navigation.</summary>
    private const string ThemeCookie = "pdn_theme";

    private static string PdnUser(HttpContext ctx) => (string)ctx.Items[PdnUserKey]!;

    private static string Prefix(HttpContext ctx) => (string)ctx.Items[PrefixKey]!;

    /// <summary>Whether this request renders chrome-less inside the pdn panel (slot mode).</summary>
    private static bool Embed(HttpContext ctx) => (bool)ctx.Items[EmbedKey]!;

    /// <summary>
    /// The resolved theme for this request — <c>"dark"</c>, <c>"light"</c>, or <c>null</c> (no
    /// explicit theme → keep the prefers-color-scheme default; standalone rendering is unchanged).
    /// </summary>
    private static string? Theme(HttpContext ctx) => ctx.Items[ThemeKey] as string;

    /// <summary>
    /// Resolves the explicit theme for this request — query <c>?theme=</c> first, then the persisted
    /// <see cref="ThemeCookie"/> — and, when it came from the query, (re)writes the cookie so it
    /// survives in-iframe navigation (the panel reloads the iframe with a fresh <c>?theme=</c> on a
    /// toggle, updating the stored value). Returns <c>null</c> when neither is present, leaving the
    /// page to fall back to <c>prefers-color-scheme</c> (so direct/standalone rendering is unchanged).
    /// The header <c>X-Pdn-Theme</c> is honoured alongside the query for the slot embed.
    /// </summary>
    private static string? ResolveTheme(HttpContext context)
    {
        string fromRequest = Normalize(context.Request.Query["theme"].ToString());
        if (fromRequest.Length == 0)
        {
            fromRequest = Normalize(context.Request.Headers["X-Pdn-Theme"].ToString());
        }

        if (fromRequest.Length > 0)
        {
            // Persist so subsequent in-iframe navigations (no query) stay themed; scope to the mount
            // prefix (or "/") so it's a first-party cookie under the gateway path.
            string path = Prefix(context);
            context.Response.Cookies.Append(ThemeCookie, fromRequest, new CookieOptions
            {
                Path = path.Length == 0 ? "/" : path,
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
            });
            return fromRequest;
        }

        string fromCookie = Normalize(context.Request.Cookies[ThemeCookie] ?? "");
        return fromCookie.Length == 0 ? null : fromCookie;
    }

    /// <summary>Accepts only the two known theme tokens (case-insensitive); anything else → empty.</summary>
    private static string Normalize(string value) => value.Trim().ToLowerInvariant() switch
    {
        "dark" => "dark",
        "light" => "light",
        _ => "",
    };

    /// <summary>
    /// Roots an absolute path under the gateway mount prefix ("" when direct), and — in slot/embed
    /// mode — threads <c>pdn_embed=1</c> through the link so a click stays headless within the panel
    /// iframe (server-rendered, no-JS: the signal must persist across every navigation).
    /// </summary>
    private static string U(string prefix, string path, bool embed = false)
    {
        string url = prefix + path;
        if (!embed)
        {
            return url;
        }

        return url + (path.Contains('?', StringComparison.Ordinal) ? "&pdn_embed=1" : "?pdn_embed=1");
    }

    /// <summary>The hidden form field that carries the embed signal across a POST, or empty when not embedded.</summary>
    private static string EmbedField(bool embed) =>
        embed ? """<input type="hidden" name="pdn_embed" value="1">""" : "";

    /// <summary>The pdn-username → callsign mapping (case-insensitive over the user table).</summary>
    internal static string? FindCallsign(BbsStore store, string pdnUser)
    {
        foreach (User user in store.ListUsers())
        {
            if (string.Equals(user.PdnUsername, pdnUser, StringComparison.OrdinalIgnoreCase))
            {
                return user.Callsign;
            }
        }

        return null;
    }

    private static IResult WithCallsign(HttpContext ctx, WebmailOptions options, Func<string, string, IResult> handler)
    {
        string user = PdnUser(ctx);
        string prefix = Prefix(ctx);
        string? call = FindCallsign(options.Store, user);
        return call is null ? ClaimForm(prefix, user, error: null, embed: Embed(ctx), theme: Theme(ctx)) : handler(prefix, call);
    }

    // ---------------------------------------------------------------- pages

    private static IResult Inbox(WebmailOptions o, string prefix, string call, int page, bool embed, string? theme)
    {
        IReadOnlyList<Message> mine = HideSevenPlusParts(o.Store, o.Store.ListMessages(new MessageQuery
        {
            Type = MessageType.Personal,
            ToCall = call,
        }));
        // Personal placeholders are scoped to this user's incoming files only (never leak another
        // user's private incoming-file name into this inbox).
        string placeholders = SevenPlusPlaceholders(o.Store, MessageType.Personal, call);
        string rows = MessageRows(mine, page, o.PageSize, call, prefix, "/", embed);
        return Html(Page(o, prefix, call, "Inbox", embed,
            $"""
            <h2>Inbox — personal messages for {H(call)}</h2>
            {placeholders}
            {rows}
            """, theme));
    }

    private static IResult Bulletins(WebmailOptions o, string prefix, string call, int page, bool embed, string? theme)
    {
        IReadOnlyList<Message> bulls = HideSevenPlusParts(o.Store, o.Store.ListMessages(new MessageQuery { Type = MessageType.Bulletin }));
        // Bulletins are world-visible, so the bulletin placeholders are not recipient-scoped.
        string placeholders = SevenPlusPlaceholders(o.Store, MessageType.Bulletin, recipientCall: null);
        string rows = MessageRows(bulls, page, o.PageSize, call, prefix, "/bulletins", embed);
        return Html(Page(o, prefix, call, "Bulletins", embed,
            $"""
            <h2>Bulletins</h2>
            {placeholders}
            {rows}
            """, theme));
    }

    /// <summary>
    /// Drops raw 7plus source part-bulletins from a listing (design.md "the raw 7plus text never
    /// shown"). The hidden messages stay in the store and still forward — only the listing hides
    /// them; their read route still works for a direct link. The assembled-file message (a
    /// <c>local_only</c> message with an attachment) is NOT in the part set, so it lists normally.
    /// </summary>
    private static IReadOnlyList<Message> HideSevenPlusParts(BbsStore store, IReadOnlyList<Message> messages)
    {
        IReadOnlySet<long> hidden = store.GetSevenPlusPartMessageNumbers();
        if (hidden.Count == 0)
        {
            return messages;
        }

        var visible = new List<Message>(messages.Count);
        foreach (Message m in messages)
        {
            if (!hidden.Contains(m.Number))
            {
                visible.Add(m);
            }
        }

        return visible;
    }

    /// <summary>
    /// A lightweight, read-only placeholder per in-flight (incomplete, not-yet-assembled) 7plus file
    /// whose parts arrived as the given type — "FIELDS.JPG — 7plus, 3/5 parts received". No
    /// attachment, no link: it shows progress until the set completes and a real assembled-file
    /// message replaces it. Empty when there are none.
    /// </summary>
    private static string SevenPlusPlaceholders(BbsStore store, MessageType type, string? recipientCall)
    {
        var pending = new List<SevenPlusProgress>();
        foreach (SevenPlusProgress p in store.ListIncompleteSevenPlusFiles(recipientCall))
        {
            // Show under the matching list; default a typeless (no source yet — shouldn't happen) to
            // the bulletins list since 7plus files conventionally arrive as part-bulletins.
            MessageType placeholderType = p.SourceType ?? MessageType.Bulletin;
            if (placeholderType == type)
            {
                pending.Add(p);
            }
        }

        if (pending.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder("<ul class=\"sevenplus-pending\">");
        foreach (SevenPlusProgress p in pending)
        {
            sb.Append(Inv(
                $"""<li>{H(p.HeaderName.Trim())} <span class="dim">— 7plus, {p.ReceivedParts}/{p.TotalParts} parts received</span></li>"""));
        }

        sb.Append("</ul>");
        return sb.ToString();
    }

    private static IResult ReadMessage(WebmailOptions o, string prefix, string call, long number, bool embed, string? theme)
    {
        Message? message = o.Store.GetMessage(number);
        bool isSysop = IsSysop(o, call);
        if (message is null || !MessageRules.CanRead(message, call, isSysop))
        {
            return Results.NotFound("No such message.");
        }

        o.Store.MarkRead(number, call); // no-op unless we are an addressee (N→Y rules in the store)
        message = o.Store.GetMessage(number)!;

        bool canKill = MessageRules.CanKill(message, call, isSysop);
        string killForm = canKill && message.Status != MessageStatus.Killed
            ? $"""<form method="post" action="{U(prefix, Inv($"/messages/{message.Number}/kill"), embed)}">{EmbedField(embed)}<button type="submit">Kill message</button></form>"""
            : "";
        string to = string.Join("; ", message.Recipients.Where(r => !r.Cc).Select(r => r.ToCall));
        string ccRow = RenderCcRow(message);
        string attachments = RenderAttachments(message, prefix, embed);
        // Contextual back link to the list this message came from (personals → Inbox, else Bulletins),
        // so a reader isn't stranded on the message with only the top nav to escape.
        string backPath = message.Type == MessageType.Personal ? "/" : "/bulletins";
        string backLabel = message.Type == MessageType.Personal ? "Inbox" : "Bulletins";
        return Html(Page(o, prefix, call, Inv($"Message {message.Number}"), embed,
            $"""
            <p class="back"><a href="{U(prefix, backPath, embed)}">&laquo; Back to {backLabel}</a></p>
            <h2>Message {message.Number} <span class="dim">— {H(TypeWord(message.Type))} · {H(StatusWord(message.Status))}</span></h2>
            <table class="meta">
            <tr><th>From</th><td>{H(message.From)}</td></tr>
            <tr><th>To</th><td>{H(to)}{(message.At is null ? "" : " @ " + H(message.At))}</td></tr>
            {ccRow}
            <tr><th>Subject</th><td>{H(message.Subject)}</td></tr>
            <tr><th>Network ID</th><td>{H(message.Bid)}</td></tr>
            <tr><th>Date</th><td>{H(message.CreatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))}</td></tr>
            </table>
            {RenderMessageBody(message)}
            {attachments}
            {killForm}
            <p class="back"><a href="{U(prefix, backPath, embed)}">&laquo; Back to {backLabel}</a></p>
            """, theme));
    }

    /// <summary>
    /// Streams one attachment BLOB as a download. Authorization is the SAME as reading the message
    /// (<see cref="MessageRules.CanRead"/> — a recipient or the sysop), so a non-recipient gets a
    /// 404 for both the read page and its attachments. The name is matched against the EXACT stored
    /// name (<see cref="BbsStore.GetAttachment"/>) — no path interpretation — so a traversal-shaped
    /// name resolves to nothing; the served file name is re-quoted from the request name with quotes
    /// and control chars stripped so it cannot break the Content-Disposition header.
    /// </summary>
    private static IResult DownloadAttachment(WebmailOptions o, string call, long number, string name)
    {
        Message? message = o.Store.GetMessage(number);
        if (message is null || !MessageRules.CanRead(message, call, IsSysop(o, call)))
        {
            return Results.NotFound("No such message.");
        }

        ReadOnlyMemory<byte>? content = o.Store.GetAttachment(number, name);
        if (content is null)
        {
            return Results.NotFound("No such attachment.");
        }

        return Results.File(content.Value.ToArray(), "application/octet-stream", fileDownloadName: SafeFileName(name));
    }

    /// <summary>Strips quotes / control chars so a stored name can't break the Content-Disposition header.</summary>
    private static string SafeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c != '"' && c != '\\' && !char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.Length == 0 ? "attachment" : sb.ToString();
    }

    private static IResult ComposeForm(
        WebmailOptions o, string prefix, string call, string? to, string? type, bool embed,
        string? error = null, int status = StatusCodes.Status200OK, string? theme = null)
    {
        bool bulletin = string.Equals(type, "B", StringComparison.OrdinalIgnoreCase);
        string err = error is null ? "" : $"""<p class="err">{H(error)}</p>""";

        // The file-handling choice (the send-side file slice): attach a file and pick how it travels.
        // 7plus is the UNIVERSAL choice (the parts ride in the body text over any path — B1 or B2, and
        // even a text-only TNC2 link); a binary B2 attachment only reaches B2-capable partners. The
        // form posts multipart/form-data so Request.Form.Files surfaces the upload; with no file the
        // mode is ignored, so the dominant no-file case is unchanged behaviour.
        return Html(Page(o, prefix, call, "Compose", embed,
            $"""
            <h2>Compose</h2>
            {err}
            <form method="post" action="{U(prefix, "/compose", embed)}" enctype="multipart/form-data">
            {EmbedField(embed)}
            <p><label>Type
            <select name="type">
            <option value="P"{(bulletin ? "" : " selected")}>Personal</option>
            <option value="B"{(bulletin ? " selected" : "")}>Bulletin</option>
            </select></label></p>
            <p><label>To <input name="to" value="{H(to ?? "")}" placeholder="callsign or topic" required></label>
            <label>@ <input name="at" placeholder="route, e.g. GB7BPQ.#23.GBR.EURO or EURO"></label></p>
            <p><label>Subject <input name="subject" size="60" maxlength="60" required></label></p>
            <p><textarea name="body" rows="12" cols="72"></textarea></p>
            <fieldset><legend>Attach a file <span class="dim">(optional)</span></legend>
            <p><input type="file" name="file"></p>
            <p><label><input type="radio" name="fileMode" value="7plus" checked> 7plus-encode it <span class="dim">— universal: the encoded text rides in the message body and reaches any partner (and a TNC2 user)</span></label></p>
            <p><label><input type="radio" name="fileMode" value="attachment"> Send as a binary attachment <span class="dim">— only reaches B2-capable partners; 7plus is the universal choice</span></label></p>
            </fieldset>
            <p><button type="submit">Send</button></p>
            </form>
            """, theme), status);
    }

    private static IResult Compose(WebmailOptions o, string prefix, string call, IFormCollection form, IFormFile? file, bool embed, string? theme)
    {
        string typeField = form["type"].ToString().Trim().ToUpperInvariant();
        MessageType type = typeField == "B" ? MessageType.Bulletin : MessageType.Personal;
        string to = form["to"].ToString().Trim();
        string at = form["at"].ToString().Trim();
        string subject = form["subject"].ToString().Trim();
        string body = form["body"].ToString();

        if (to.Length == 0 || subject.Length == 0)
        {
            return ComposeForm(o, prefix, call, to, typeField, embed, "TO and Subject are required.", StatusCodes.Status400BadRequest, theme);
        }

        // `call@route` in the TO box works like the console's S-line grammar (§1.5).
        int atSign = to.IndexOf('@', StringComparison.Ordinal);
        if (atSign > 0 && at.Length == 0)
        {
            at = to[(atSign + 1)..].Trim();
            to = to[..atSign].Trim();
        }

        string[] recipients = to.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients.Length == 0)
        {
            return ComposeForm(o, prefix, call, to, typeField, embed, "TO is required.", StatusCodes.Status400BadRequest, theme);
        }

        // CR line discipline, like the console's body loop stores it (§1.5 step 6).
        string crBody = body.ReplaceLineEndings("\r");
        if (crBody.Length > 0 && !crBody.EndsWith('\r'))
        {
            crBody += "\r";
        }

        // The send-side file slice: an uploaded file (when present) either 7plus-encodes into the body
        // text (universal — survives B1/text-only paths; an inbound SevenPlusAssembler reassembles it
        // at a pdn recipient) or rides as a binary B2 attachment. With no file, both lists below stay
        // empty and the path is exactly the prior text-only compose.
        var attachments = new List<MessageAttachment>();
        if (file is { Length: > 0 })
        {
            if (file.Length > o.MaxUploadBytes)
            {
                return ComposeForm(o, prefix, call, to, typeField, embed,
                    Inv($"That file is too large ({file.Length} bytes); the limit is {o.MaxUploadBytes} bytes."),
                    StatusCodes.Status400BadRequest, theme);
            }

            // Path components are stripped so the stored/encoded name is a bare filename.
            string fileName = StripUploadPath(file.FileName);
            byte[] bytes = ReadAll(file, o.MaxUploadBytes);

            string mode = form["fileMode"].ToString().Trim().ToLowerInvariant();
            if (mode == "attachment")
            {
                // Rides the attachments plumbing; BuildB2Object emits it as a B2 File: part.
                attachments.Add(new MessageAttachment(fileName, bytes));
            }
            else
            {
                // 7plus (the default): append the encoded parts after the user's text, blank-separated.
                // The codec handles the DOS-8.3 + extended long-name lines; we just give it the real
                // name. A large file yields multiple parts regardless (the codec's 512-line/part cap) —
                // MVP embeds them all in one body (the multi-frame-TX node fix forwards large bodies).
                //
                // The parts are appended VERBATIM (their wire-faithful CRLF separators), NOT run through
                // the body's CR line discipline: a 7plus code line can legitimately contain byte 0x85,
                // which string.ReplaceLineEndings treats as a Unicode line break (U+0085 NEL) and would
                // rewrite — corrupting the line and breaking reassembly. CRLF is the codec default and
                // the inbound scanner tolerates CRLF/CR/LF, so verbatim is both correct and round-trips.
                IReadOnlyList<string> parts = SevenPlusEncoder.Encode(bytes, fileName);
                var sb = new StringBuilder(crBody);
                if (sb.Length > 0)
                {
                    sb.Append('\r'); // a blank line between the user's text and the 7plus block
                }

                foreach (string part in parts)
                {
                    sb.Append(part);
                }

                crBody = sb.ToString();
            }
        }

        Message stored = o.Store.AddMessage(new MessageDraft
        {
            Type = type,
            From = call,
            Recipients = recipients,
            At = at.Length == 0 ? null : at,
            Subject = subject,
            Body = Encoding.Latin1.GetBytes(crBody),
            Attachments = attachments,
        });
        o.Routing.RouteMessage(stored);
        return Results.Redirect(U(prefix, Inv($"/messages/{stored.Number}"), embed));
    }

    /// <summary>Strips any directory component from an uploaded filename (both '/' and '\'), defending against a traversal-shaped name.</summary>
    private static string StripUploadPath(string name)
    {
        int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        string bare = (slash >= 0 ? name[(slash + 1)..] : name).Trim();
        return bare.Length == 0 ? "upload.bin" : bare;
    }

    /// <summary>Reads the uploaded file into a byte array, bounded by <paramref name="cap"/> (the size guard is enforced by the caller first).</summary>
    private static byte[] ReadAll(IFormFile file, int cap)
    {
        using var ms = new MemoryStream(checked((int)Math.Min(file.Length, cap)));
        using Stream stream = file.OpenReadStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static IResult Kill(WebmailOptions o, string prefix, string call, long number, bool embed)
    {
        Message? message = o.Store.GetMessage(number);
        if (message is null || !MessageRules.CanKill(message, call, IsSysop(o, call)))
        {
            return Results.NotFound("No such message (or not yours to kill).");
        }

        o.Store.Kill(number);
        return Results.Redirect(U(prefix, "/", embed));
    }

    private static IResult Claim(WebmailOptions o, string prefix, string pdnUser, string callsignInput, bool embed, string? theme)
    {
        string call = Callsigns.NormalizeAddressee(callsignInput);
        if (call.Length < 3 || !call.All(char.IsAsciiLetterOrDigit) || !call.Any(char.IsAsciiDigit))
        {
            return ClaimForm(prefix, pdnUser, "That doesn't look like a callsign.", embed, StatusCodes.Status400BadRequest, theme);
        }

        User? existing = o.Store.GetUser(call);
        if (existing?.PdnUsername is { Length: > 0 } owner
            && !string.Equals(owner, pdnUser, StringComparison.OrdinalIgnoreCase))
        {
            return ClaimForm(prefix, pdnUser, Inv($"{call} is already linked to another pdn account."), embed, StatusCodes.Status409Conflict, theme);
        }

        User user = (existing ?? new User { Callsign = call }) with { PdnUsername = pdnUser };
        o.Store.UpsertUser(user);
        return Results.Redirect(U(prefix, "/", embed));
    }

    private static IResult ClaimForm(string prefix, string pdnUser, string? error, bool embed, int status = StatusCodes.Status200OK, string? theme = null)
    {
        string err = error is null ? "" : $"""<p class="err">{H(error)}</p>""";
        // Embedded: drop our own <h1> identity bar (the panel shows the app name) and trim the top
        // margin so the content sits under the panel header. Keep a complete valid HTML document.
        string heading = embed ? "" : "<h1>pdn-bbs</h1>";
        string mainClass = embed ? """ class="embed" style="padding-top:.5rem" """.TrimEnd() : "";
        return Html($$"""
            <!doctype html>
            <html{{HtmlThemeClass(theme)}}><head><meta charset="utf-8"><title>pdn-bbs — claim your callsign</title>{{Style}}</head>
            <body><main{{mainClass}}>
            {{heading}}
            <p>Hello <b>{{H(pdnUser)}}</b>. This pdn account isn't linked to a callsign yet.</p>
            {{err}}
            <form method="post" action="{{U(prefix, "/claim", embed)}}">
            {{EmbedField(embed)}}
            <p><label>Your callsign <input name="callsign" maxlength="9" required></label>
            <button type="submit">Claim</button></p>
            </form>
            </main></body></html>
            """, status);
    }

    // ---------------------------------------------------------------- settings (interface-mode toggle)

    /// <summary>
    /// The settings page: the user's interface mode (plain / classic — the same
    /// <see cref="UserSettings.InterfaceMode"/> the console session reads) and the BBS mail-password
    /// used by external mail apps (IMAP). Plain is the default for a never-set user (the
    /// plain-language mandate, design.md). The forms post back to the prefix-correct mounts. An
    /// optional <paramref name="notice"/> banner confirms (or, with <paramref name="noticeError"/>,
    /// reports a problem with) a just-applied change.
    /// </summary>
    private static IResult SettingsPage(WebmailOptions o, string prefix, string call, bool embed, string? notice = null, bool noticeError = false, string? theme = null)
    {
        // Null InterfaceMode means "never chosen" → the plain default per the mandate.
        InterfaceMode mode = o.Settings.Load(call).InterfaceMode ?? InterfaceMode.Plain;
        bool plain = mode == InterfaceMode.Plain;
        string banner = notice is null
            ? ""
            : Inv($"""<p class="{(noticeError ? "err" : "saved")}">{H(notice)}</p>""");

        bool hasMailPw = o.Store.HasMailPassword(call);
        string mailPwState = hasMailPw
            ? """<p class="saved">A mail password is set.</p>"""
            : """<p class="dim">No mail password set yet — set one to read your mail in an app like iPhone Mail.</p>""";
        // A sibling form (HTML forbids nesting it inside the set/change form below).
        string removeForm = hasMailPw
            ? Inv($"""
                <form method="post" action="{U(prefix, "/settings/mailpw/clear", embed)}">
                {EmbedField(embed)}
                <p><button type="submit" class="link">Remove mail password</button></p>
                </form>
                """)
            : "";

        return Html(Page(o, prefix, call, "Settings", embed,
            $"""
            <h2>Settings</h2>
            {banner}
            <form method="post" action="{U(prefix, "/settings", embed)}">
            {EmbedField(embed)}
            <fieldset><legend>Mailbox command surface</legend>
            <p>How the RF/console mailbox talks to you when you connect over packet.</p>
            <p><label><input type="radio" name="interface" value="plain"{(plain ? " checked" : "")}> Plain language <span class="dim">— sentences and whole words (the default)</span></label></p>
            <p><label><input type="radio" name="interface" value="classic"{(plain ? "" : " checked")}> Classic <span class="dim">— the terse W0RLI letters, for old automated clients</span></label></p>
            <p><button type="submit">Save</button></p>
            </fieldset>
            </form>
            <form method="post" action="{U(prefix, "/settings/mailpw", embed)}">
            {EmbedField(embed)}
            <fieldset><legend>Mail password (external mail apps)</legend>
            <p>A separate password for reading your mail over IMAP in an app like iPhone Mail.
            Log in there with your callsign <b>{H(call)}</b> and this password.
            It is not your packet/console login — keep it different.</p>
            {mailPwState}
            <p><label>New mail password<br><input type="password" name="new" minlength="{BbsStore.MinMailPasswordLength}" autocomplete="new-password" required></label></p>
            <p><label>Confirm<br><input type="password" name="confirm" minlength="{BbsStore.MinMailPasswordLength}" autocomplete="new-password" required></label></p>
            <p class="dim">At least {BbsStore.MinMailPasswordLength} characters.</p>
            <p><button type="submit">{(hasMailPw ? "Change" : "Set")} mail password</button></p>
            </fieldset>
            </form>
            {removeForm}
            """, theme));
    }

    /// <summary>
    /// Applies the posted interface choice: loads the user's <see cref="UserSettings"/>, sets
    /// <see cref="UserSettings.InterfaceMode"/> and saves it (so the next console connect reads the
    /// new surface), then re-renders the page with a confirmation. An unrecognised value falls back
    /// to plain (the mandate's default) rather than erroring.
    /// </summary>
    private static IResult SaveSettings(WebmailOptions o, string prefix, string call, string interfaceValue, bool embed, string? theme)
    {
        InterfaceMode mode = string.Equals(interfaceValue.Trim(), "classic", StringComparison.OrdinalIgnoreCase)
            ? InterfaceMode.Classic
            : InterfaceMode.Plain;

        o.Settings.Save(call, o.Settings.Load(call) with { InterfaceMode = mode });
        return SettingsPage(o, prefix, call, embed, "Saved. Your choice applies to your next mailbox connection.", theme: theme);
    }

    /// <summary>
    /// Sets/changes the caller's BBS mail-password from the posted <c>new</c>/<c>confirm</c> fields.
    /// Requires the two to match and delegates length/format policy to
    /// <see cref="BbsStore.SetMailPassword"/> (whose <see cref="ArgumentException"/> message is shown
    /// verbatim). The plaintext is never logged or echoed back — only a pass/fail notice.
    /// </summary>
    private static IResult SaveMailPassword(WebmailOptions o, string prefix, string call, string newPw, string confirm, bool embed, string? theme)
    {
        if (!string.Equals(newPw, confirm, StringComparison.Ordinal))
        {
            return SettingsPage(o, prefix, call, embed, "Those passwords didn't match. Nothing was changed.", noticeError: true, theme: theme);
        }

        try
        {
            o.Store.SetMailPassword(call, newPw);
        }
        catch (ArgumentException ex)
        {
            return SettingsPage(o, prefix, call, embed, ex.Message, noticeError: true, theme: theme);
        }

        return SettingsPage(o, prefix, call, embed, "Mail password updated. Use it with your callsign in your mail app.", theme: theme);
    }

    /// <summary>Removes the caller's BBS mail-password (disabling IMAP login for them).</summary>
    private static IResult ClearMailPassword(WebmailOptions o, string prefix, string call, bool embed, string? theme)
    {
        bool removed = o.Store.ClearMailPassword(call);
        return SettingsPage(o, prefix, call, embed,
            removed ? "Mail password removed. External mail apps can no longer sign in." : "No mail password was set.", theme: theme);
    }

    // ---------------------------------------------------------------- forwarding editor (sysop)

    /// <summary>
    /// The sysop-only forwarding EDITOR — a Forms | YAML switch over the partner store (the source of
    /// truth, store-first). The Forms tab shows one card per partner (its dial config, the LIVE
    /// forward-queue depth, the plain-language routing rules) with Edit / Delete / Forward now, an
    /// edit form when <paramref name="editCall"/> names a partner, and an Add-partner form. The YAML
    /// tab is the same store rendered as the <c>partners:</c> block (the bbs.yaml shape) in a textarea
    /// the sysop can edit and save. Both surfaces read/write the store, so a change in one shows in the
    /// other; bbs.yaml is seed-only (it seeds an empty store at first run).
    /// </summary>
    private static IResult Forwarding(
        WebmailOptions o, string prefix, string call, string? tab, string? editCall, bool embed, string? theme,
        string? notice = null, bool noticeError = false, string? yamlText = null, int status = StatusCodes.Status200OK)
    {
        if (!IsSysop(o, call))
        {
            return Html(Page(o, prefix, call, "Forwarding", embed,
                """
                <h2>Forwarding</h2>
                <p class="err">This page is for the sysop only.</p>
                """, theme), StatusCodes.Status403Forbidden);
        }

        bool yamlTab = string.Equals(tab, "yaml", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<Partner> partners = o.Store.ListPartners();
        string banner = notice is null
            ? ""
            : Inv($"""<p class="{(noticeError ? "err" : "saved")}">{H(notice)}</p>""");

        // The tab switch — both links thread the prefix + embed; the active tab is marked.
        string formsHref = U(prefix, "/forwarding", embed);
        string yamlHref = U(prefix, "/forwarding?tab=yaml", embed);
        string tabs = $"""
            <nav class="subtabs">
            <a href="{formsHref}"{(yamlTab ? "" : " class=\"active\"")}>Forms</a>
            <a href="{yamlHref}"{(yamlTab ? " class=\"active\"" : "")}>YAML</a>
            </nav>
            """;

        string body = yamlTab
            ? YamlTab(o, prefix, embed, partners, yamlText)
            : FormsTab(o, prefix, embed, partners, editCall);

        return Html(Page(o, prefix, call, "Forwarding", embed,
            $"""
            <h2>Forwarding partners <span class="dim">— {partners.Count} configured</span></h2>
            <p class="dim">Everything here is configurable by form or YAML — both edit the same store. <code>bbs.yaml</code> only seeds an empty store on first run.</p>
            {tabs}
            {banner}
            {body}
            """, theme), status);
    }

    /// <summary>The Forms tab: partner cards (each with Edit / Delete / Forward now), an edit form, and the Add form.</summary>
    private static string FormsTab(WebmailOptions o, string prefix, bool embed, IReadOnlyList<Partner> partners, string? editCall)
    {
        Partner? editing = editCall is { Length: > 0 } ? o.Store.GetPartner(editCall) : null;

        var sb = new StringBuilder();
        if (partners.Count == 0)
        {
            sb.Append("""<p class="dim">No forwarding partners yet. Add one below, or paste a YAML block in the YAML tab.</p>""");
        }
        else
        {
            foreach (Partner p in partners)
            {
                sb.Append(PartnerCard(o, prefix, embed, p, editing));
            }
        }

        // The Add form only when not editing (one form at a time keeps the page focused; editing shows
        // the edit form in place of the Add form).
        if (editing is null)
        {
            sb.Append(PartnerForm(prefix, embed, partner: null));
        }

        return sb.ToString();
    }

    /// <summary>
    /// One partner's card: dial config + live queue depth + the plain-language routing rules, plus the
    /// Edit / Delete / Forward-now action row. When this card is the one being edited the inline edit
    /// form is rendered in its place instead.
    /// </summary>
    private static string PartnerCard(WebmailOptions o, string prefix, bool embed, Partner p, Partner? editing)
    {
        if (editing is not null && Callsigns.BaseEquals(editing.Call, p.Call))
        {
            return PartnerForm(prefix, embed, p);
        }

        string badge = p.Enabled
            ? """<span class="badge on">auto-dial on</span>"""
            : """<span class="badge off">auto-dial off</span>""";

        int queued = o.Store.GetForwardQueue(p.Call).Count;
        string queue = queued == 0
            ? """<span class="dim">empty</span>"""
            : Inv($"<b>{queued}</b> message{(queued == 1 ? "" : "s")} waiting");

        string connect = p.ConnectScript.Count == 0
            ? """<span class="dim">none</span>"""
            : H(string.Join("  ·  ", p.ConnectScript));

        string schedule = Inv($"every {Math.Max(1, p.ForwardIntervalSeconds / 60)} min")
            + (p.ForwardNewImmediately ? ", and as soon as a message is queued" : "")
            + (p.Collect ? ", and polls even when nothing is queued (collect)" : "");

        string limits = p.MaxRxSize >= 99999 && p.MaxTxSize >= 99999
            ? """<span class="dim">no size limit</span>"""
            : Inv($"accepts ≤ {p.MaxRxSize:N0} B · sends ≤ {p.MaxTxSize:N0} B");

        string mode = p.AllowB2F ? "B2 (binary capable)" : "B1 (text only)";

        // Action row: Edit (link → ?edit=call), Forward now + Delete (posts; the embed/prefix threaded).
        string editHref = U(prefix, Inv($"/forwarding?edit={Uri.EscapeDataString(p.Call)}"), embed);
        string actions = $"""
            <p class="card-actions">
            <a href="{editHref}">Edit</a>
            <form method="post" action="{U(prefix, "/forwarding/partner/forward-now", embed)}">{EmbedField(embed)}<input type="hidden" name="call" value="{H(p.Call)}"><button type="submit" class="link plain">Forward now</button></form>
            <form method="post" action="{U(prefix, "/forwarding/partner/delete", embed)}">{EmbedField(embed)}<input type="hidden" name="call" value="{H(p.Call)}"><button type="submit" class="link">Delete</button></form>
            </p>
            """;

        return $$"""
            <fieldset>
            <legend>{{H(p.Call)}} {{badge}}</legend>
            <table class="meta">
            <tr><th>Connect</th><td>{{connect}}</td></tr>
            <tr><th>Schedule</th><td>{{H(schedule)}}</td></tr>
            <tr><th>Queue</th><td>{{queue}}</td></tr>
            <tr><th>Limits</th><td>{{limits}}</td></tr>
            <tr><th>Mode</th><td>{{H(mode)}}</td></tr>
            </table>
            {{RoutingSummary(p)}}
            {{actions}}
            </fieldset>
            """;
    }

    /// <summary>
    /// The add/edit partner form — EVERY <see cref="PartnerConfig"/> field with a plain-language label.
    /// When <paramref name="partner"/> is non-null it's the edit form (the call is read-only, the key);
    /// null is the Add form. List fields (to/at/hr) are space-separated text inputs; the connect script
    /// is a textarea, one line each; bools are checkboxes. Posts to <c>/forwarding/partner</c>.
    /// </summary>
    private static string PartnerForm(string prefix, bool embed, Partner? partner)
    {
        bool editing = partner is not null;
        string title = editing ? Inv($"Edit {H(partner!.Call)}") : "Add partner";
        string callField = editing
            ? Inv($"""<input type="hidden" name="call" value="{H(partner!.Call)}"><b>{H(partner.Call)}</b> <span class="dim">— the partner's callsign can't be changed; delete and re-add to rename.</span>""")
            : """<input name="call" placeholder="e.g. GB7BPQ" maxlength="9" required>""";

        string script = editing ? string.Join("\n", partner!.ConnectScript) : "";
        string to = editing ? string.Join(" ", partner!.ToCalls) : "";
        string at = editing ? string.Join(" ", partner!.AtCalls) : "";
        string hr = editing ? string.Join(" ", partner!.HRoutes) : "";
        string bbsHa = editing ? partner!.BbsHa ?? "" : "";
        int interval = editing ? Math.Max(1, partner!.ForwardIntervalSeconds / 60) : 60;
        int conTimeout = editing ? partner!.ConTimeoutSeconds : 60;
        int maxRx = editing ? partner!.MaxRxSize : 99999;
        int maxTx = editing ? partner!.MaxTxSize : 99999;
        bool enabled = editing ? partner!.Enabled : true;
        bool sendImmediately = editing ? partner!.ForwardNewImmediately : true;
        bool collect = editing ? partner!.Collect : false;
        bool allowB2 = editing ? partner!.AllowB2F : false;

        string cancel = editing
            ? Inv($"""<a class="cancel" href="{U(prefix, "/forwarding", embed)}">Cancel</a>""")
            : "";

        return $"""
            <form method="post" action="{U(prefix, "/forwarding/partner", embed)}">
            {EmbedField(embed)}
            <fieldset>
            <legend>{H(title)}</legend>
            <p><label>Partner callsign<br>{callField}</label></p>
            <p><label>Connect script <span class="dim">— one line each. The first line names the dial (e.g. <code>C GB7BPQ</code>). Later lines are <code>EXPECT=SEND</code> steps: wait for the text before the <code>=</code> on the link, then send the text after it — e.g. <code>GB7RDG&gt;=BBS</code> waits for the node prompt, then sends <code>BBS</code>. A line with no <code>=</code> is send-only (no wait), e.g. <code>BBS</code></span><br>
            <textarea name="connectScript" rows="3" cols="48" placeholder="C GB7BPQ">{H(script)}</textarea></label></p>
            <p><label>@ addresses <span class="dim">— routes this partner serves; <code>*</code> is the catch-all default uplink (space-separated)</span><br>
            <input name="at" size="48" value="{H(at)}" placeholder="* or GB7BPQ"></label></p>
            <p><label>Recipients <span class="dim">— exact TO callsigns to route here (space-separated)</span><br>
            <input name="to" size="48" value="{H(to)}" placeholder="SYSOP"></label></p>
            <p><label>Areas <span class="dim">— hierarchical routes for floods + personals, e.g. <code>GBR.EURO</code> (space-separated)</span><br>
            <input name="hr" size="48" value="{H(hr)}" placeholder="GBR.EURO"></label></p>
            <p><label>Partner address <span class="dim">— the partner's own full HA, needed for flood matching, e.g. <code>GB7BPQ.#23.GBR.EURO</code></span><br>
            <input name="bbsHa" size="48" value="{H(bbsHa)}" placeholder="GB7BPQ.#23.GBR.EURO"></label></p>
            <p><label>Dial every <input type="number" name="intervalMinutes" min="1" value="{Inv($"{interval}")}" style="width:6rem"> minutes <span class="dim">— retry cadence for queued mail</span></label></p>
            <p><label>Connect timeout <input type="number" name="conTimeoutSeconds" min="1" value="{Inv($"{conTimeout}")}" style="width:6rem"> seconds <span class="dim">— per response wait during the connect handshake</span></label></p>
            <p><label>Max accepted <input type="number" name="maxRx" min="0" value="{Inv($"{maxRx}")}" style="width:8rem"> bytes <span class="dim">— largest message accepted from this partner</span></label></p>
            <p><label>Max sent <input type="number" name="maxTx" min="0" value="{Inv($"{maxTx}")}" style="width:8rem"> bytes <span class="dim">— largest message proposed to this partner</span></label></p>
            <p><label><input type="checkbox" name="enabled" value="1"{(enabled ? " checked" : "")}> Auto-dial enabled <span class="dim">— off ⇒ messages still queue but aren't dialled out</span></label></p>
            <p><label><input type="checkbox" name="sendImmediately" value="1"{(sendImmediately ? " checked" : "")}> Send immediately <span class="dim">— dial as soon as a message is queued for this partner</span></label></p>
            <p><label><input type="checkbox" name="collect" value="1"{(collect ? " checked" : "")}> Collect <span class="dim">— poll on the dial cadence even with an empty queue, to pick up mail a partner that can't dial us holds for us</span></label></p>
            <p><label><input type="checkbox" name="allowB2" value="1"{(allowB2 ? " checked" : "")}> Allow B2 <span class="dim">— opt in to binary B2 (Winlink/FBB) with this partner; off ⇒ B1 text forwarding</span></label></p>
            <p><button type="submit">{(editing ? "Save changes" : "Add partner")}</button> {cancel}</p>
            </fieldset>
            </form>
            """;
    }

    /// <summary>
    /// The YAML tab: a textarea of the store's partners as the <c>partners:</c> block (the bbs.yaml
    /// shape). Save parses + validates + applies to the store (upsert each parsed partner, delete
    /// store partners absent from the YAML). <paramref name="yamlText"/> is the sysop's unsaved text
    /// re-shown after a parse error; null renders the current store.
    /// </summary>
    private static string YamlTab(WebmailOptions o, string prefix, bool embed, IReadOnlyList<Partner> partners, string? yamlText)
    {
        string text = yamlText ?? PartnerYaml.Serialize(partners);
        return $"""
            <p class="dim">The whole partner set as YAML — the same <code>partners:</code> block as <code>bbs.yaml</code>. Save applies it to the store: partners in the YAML are created/updated, partners missing from it are removed.</p>
            <form method="post" action="{U(prefix, "/forwarding/yaml", embed)}">
            {EmbedField(embed)}
            <p><textarea name="yaml" rows="22" cols="80" spellcheck="false">{H(text)}</textarea></p>
            <p><button type="submit">Save YAML</button> <a class="cancel" href="{U(prefix, "/forwarding?tab=yaml", embed)}">Reset</a></p>
            </form>
            """;
    }

    /// <summary>
    /// Creates or updates a partner from the posted add/edit form, then reconciles the scheduler and
    /// redirects back to the Forms tab with a notice. The call is validated (non-empty + callsign
    /// shape) via the same <see cref="PartnerConfig.ToPartner"/> mapping the YAML/startup paths use; an
    /// invalid call re-renders the form with the error and leaves the store untouched.
    /// </summary>
    private static IResult SavePartner(WebmailOptions o, string prefix, string call, IFormCollection form, bool embed, string? theme)
    {
        if (!IsSysop(o, call))
        {
            return Forwarding(o, prefix, call, tab: null, editCall: null, embed, theme, status: StatusCodes.Status403Forbidden);
        }

        string partnerCall = Callsigns.Normalize(form["call"].ToString());
        bool editing = o.Store.GetPartner(partnerCall) is not null;
        if (partnerCall.Length == 0 || !Callsigns.IsCallsignShaped(partnerCall))
        {
            // Re-render the relevant form with the error; nothing written.
            return Forwarding(o, prefix, call, tab: null, editCall: editing ? partnerCall : null, embed, theme,
                notice: partnerCall.Length == 0 ? "A partner callsign is required." : $"'{partnerCall}' doesn't look like a callsign.",
                noticeError: true, status: StatusCodes.Status400BadRequest);
        }

        var config = new PartnerConfig
        {
            Call = partnerCall,
            ConnectScript = SplitLines(form["connectScript"].ToString()),
            To = SplitTokens(form["to"].ToString()),
            At = SplitTokens(form["at"].ToString()),
            Hr = SplitTokens(form["hr"].ToString()),
            BbsHa = form["bbsHa"].ToString().Trim() is { Length: > 0 } ha ? ha : null,
            IntervalMinutes = ParseInt(form["intervalMinutes"], 60),
            ConTimeoutSeconds = ParseInt(form["conTimeoutSeconds"], 60),
            MaxRx = ParseInt(form["maxRx"], 99999),
            MaxTx = ParseInt(form["maxTx"], 99999),
            Enabled = Checked(form["enabled"]),
            SendImmediately = Checked(form["sendImmediately"]),
            Collect = Checked(form["collect"]),
            AllowB2 = Checked(form["allowB2"]),
        };

        o.Store.UpsertPartner(config.ToPartner());
        o.OnPartnersChanged?.Invoke();
        return Results.Redirect(U(prefix, "/forwarding", embed) + Notice(embed, editing ? $"Saved {partnerCall}." : $"Added {partnerCall}."));
    }

    /// <summary>Deletes a partner (sysop only), reconciles, and redirects to the Forms tab with a notice.</summary>
    private static IResult DeletePartnerPost(WebmailOptions o, string prefix, string call, string partnerCall, bool embed, string? theme)
    {
        if (!IsSysop(o, call))
        {
            return Forwarding(o, prefix, call, tab: null, editCall: null, embed, theme, status: StatusCodes.Status403Forbidden);
        }

        string normalized = Callsigns.Normalize(partnerCall);
        bool removed = o.Store.DeletePartner(normalized);
        if (removed)
        {
            o.OnPartnersChanged?.Invoke();
        }

        return Results.Redirect(U(prefix, "/forwarding", embed)
            + Notice(embed, removed ? $"Removed {normalized}." : $"No partner '{normalized}' to remove."));
    }

    /// <summary>"Forward now" for one partner (sysop only): nudges its loop, then redirects to the Forms tab with a notice.</summary>
    private static IResult ForwardNow(WebmailOptions o, string prefix, string call, string partnerCall, bool embed, string? theme)
    {
        if (!IsSysop(o, call))
        {
            return Forwarding(o, prefix, call, tab: null, editCall: null, embed, theme, status: StatusCodes.Status403Forbidden);
        }

        string normalized = Callsigns.Normalize(partnerCall);
        Partner? partner = o.Store.GetPartner(normalized);
        if (partner is null)
        {
            return Results.Redirect(U(prefix, "/forwarding", embed) + Notice(embed, $"No partner '{normalized}'."));
        }

        o.OnForwardNow?.Invoke(normalized);
        return Results.Redirect(U(prefix, "/forwarding", embed) + Notice(embed, $"Forwarding to {normalized} now."));
    }

    /// <summary>
    /// Applies the posted YAML to the store: parse + validate (<see cref="PartnerYaml.Parse"/>), then
    /// upsert every parsed partner and delete store partners absent from the YAML — the whole set in
    /// one transaction-less but lock-guarded pass. A parse/validation error re-renders the YAML tab
    /// with the error + the user's text, touching NOTHING in the store.
    /// </summary>
    private static IResult SaveYaml(WebmailOptions o, string prefix, string call, string yaml, bool embed, string? theme)
    {
        if (!IsSysop(o, call))
        {
            return Forwarding(o, prefix, call, tab: "yaml", editCall: null, embed, theme, status: StatusCodes.Status403Forbidden);
        }

        IReadOnlyList<Partner> parsed;
        try
        {
            parsed = PartnerYaml.Parse(yaml);
        }
        catch (PartnerYamlException ex)
        {
            // Re-render the YAML view with the error AND the user's exact text; store untouched.
            return Forwarding(o, prefix, call, tab: "yaml", editCall: null, embed, theme,
                notice: ex.Message, noticeError: true, yamlText: yaml, status: StatusCodes.Status400BadRequest);
        }

        // Apply: upsert each parsed partner, then delete any store partner not in the parsed set.
        var keep = new HashSet<string>(parsed.Select(p => Callsigns.Normalize(p.Call)), StringComparer.OrdinalIgnoreCase);
        foreach (Partner p in parsed)
        {
            o.Store.UpsertPartner(p);
        }

        foreach (Partner existing in o.Store.ListPartners())
        {
            if (!keep.Contains(Callsigns.Normalize(existing.Call)))
            {
                o.Store.DeletePartner(existing.Call);
            }
        }

        o.OnPartnersChanged?.Invoke();
        return Results.Redirect(U(prefix, "/forwarding?tab=yaml", embed)
            + Notice(embed, Inv($"Applied — {parsed.Count} partner{(parsed.Count == 1 ? "" : "s")} now configured."), already: true));
    }

    /// <summary>Appends a <c>notice=</c> query param to a redirect URL (the redirect target reads it for the banner).</summary>
    private static string Notice(bool embed, string message, bool already = false)
    {
        // `embed` already added "?pdn_embed=1" via U(); `already` is for a URL that already has a query
        // (tab=yaml). Choose the right separator either way.
        char sep = (embed || already) ? '&' : '?';
        return sep + "notice=" + Uri.EscapeDataString(message);
    }

    /// <summary>Splits a connect-script textarea into trimmed, non-empty lines.</summary>
    private static List<string> SplitLines(string text) =>
        [.. text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Splits a space-separated (or comma-separated) list field into trimmed, non-empty tokens.</summary>
    private static List<string> SplitTokens(string text) =>
        [.. text.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>A number field with a fallback when blank/unparseable.</summary>
    private static int ParseInt(Microsoft.Extensions.Primitives.StringValues value, int fallback) =>
        int.TryParse(value.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

    /// <summary>A checkbox is "on" when present with our value (unchecked checkboxes aren't posted at all).</summary>
    private static bool Checked(Microsoft.Extensions.Primitives.StringValues value) => value.Count > 0;

    /// <summary>
    /// The partner's routing rules in plain language. Mirrors the priority order
    /// <see cref="Bbs.Core.RoutingEngine"/> applies: exact recipients (<c>ToCalls</c>), the
    /// <c>@</c>-addresses it serves (<c>AtCalls</c>; a <c>*</c> entry is the last-resort default
    /// uplink), the hierarchical flood/personal areas (<c>HRoutes</c>/<c>HRoutesP</c>), and the
    /// partner's own HA used for the flood "in target area" test (<c>BbsHa</c>).
    /// </summary>
    private static string RoutingSummary(Partner p)
    {
        var rows = new List<string>();

        bool wildcard = p.AtCalls.Any(a => a.Contains('*', StringComparison.Ordinal));
        List<string> namedAt = p.AtCalls.Where(a => !a.Contains('*', StringComparison.Ordinal)).ToList();

        if (wildcard)
        {
            rows.Add(RouteRow("Default uplink", "catch-all — mail no other partner claims forwards here"));
        }

        if (namedAt.Count > 0)
        {
            rows.Add(RouteRow("@ addresses", string.Join(", ", namedAt)));
        }

        if (p.ToCalls.Count > 0)
        {
            rows.Add(RouteRow("Recipients", string.Join(", ", p.ToCalls)));
        }

        List<string> areas = p.HRoutes.Concat(p.HRoutesP).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (areas.Count > 0)
        {
            rows.Add(RouteRow("Areas", string.Join(", ", areas)));
        }

        if (!string.IsNullOrWhiteSpace(p.BbsHa))
        {
            rows.Add(RouteRow("Partner address", p.BbsHa!));
        }

        return rows.Count == 0
            ? """<p class="dim">No routing rules — this partner receives nothing automatically.</p>"""
            : "<ul class=\"routes\">" + string.Concat(rows) + "</ul>";
    }

    private static string RouteRow(string label, string value) =>
        Inv($"""<li><span class="label">{H(label)}</span>{H(value)}</li>""");

    // ---------------------------------------------------------------- rendering

    private static bool IsSysop(WebmailOptions o, string call) =>
        o.SysopCallsign.Length > 0 && Callsigns.BaseEquals(o.SysopCallsign, call);

    /// <summary>The Cc meta row, or empty when the message has no Cc recipients (spec §3.9).</summary>
    private static string RenderCcRow(Message message)
    {
        string cc = string.Join("; ", message.Recipients.Where(r => r.Cc).Select(r => r.ToCall));
        return cc.Length == 0 ? "" : Inv($"<tr><th>Cc</th><td>{H(cc)}</td></tr>");
    }

    /// <summary>The attachments block with one download link per file, or empty when there are none.</summary>
    private static string RenderAttachments(Message message, string prefix, bool embed)
    {
        if (message.Attachments.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder("<h3>Attachments</h3><ul class=\"attachments\">");
        foreach (MessageAttachment a in message.Attachments)
        {
            string href = U(prefix, Inv($"/messages/{message.Number}/attachments/{Uri.EscapeDataString(a.Name)}"), embed);
            sb.Append(Inv($"""<li><a href="{H(href)}">{H(a.Name)}</a> <span class="dim">({a.Content.Length} bytes)</span></li>"""));
        }

        sb.Append("</ul>");
        return sb.ToString();
    }

    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decode a stored message body for DISPLAY: UTF-8 when the bytes are valid UTF-8 (the common
    /// ASCII / UTF-8 case — gateways, Winlink, copy-pasted smart quotes), else Latin-1 (genuine
    /// 8-bit packet content). Bodies are stored byte-transparent (Latin-1 round-trips for
    /// forwarding fidelity), but forcing UTF-8 content through Latin-1 mojibakes it (a smart quote
    /// shows as stray high-byte glyphs, "â€¦"). Render-only — the stored bytes are untouched.
    /// </summary>
    private static string DecodeBodyForDisplay(ReadOnlyMemory<byte> body)
    {
        try
        {
            return Utf8Strict.GetString(body.Span);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(body.Span);
        }
    }

    /// <summary>
    /// Render a message body for the reader: the actual content in a <c>&lt;pre&gt;</c>, with any
    /// leading FBB <c>R:</c> routing-trace lines lifted out below into a friendly relayed-path line
    /// + a collapsed raw block — so the cryptic "R:260614/1246Z 1207@GB7RDG.#42.GBR.EURO …" headers
    /// don't bury the message. Body decoded UTF-8-or-Latin-1 (<see cref="DecodeBodyForDisplay"/>).
    /// </summary>
    private static string RenderMessageBody(Message message)
    {
        string[] lines = DecodeBodyForDisplay(message.Body).ReplaceLineEndings("\n").Split('\n');
        int i = 0;
        var trace = new List<string>();
        while (i < lines.Length && lines[i].StartsWith("R:", StringComparison.Ordinal))
        {
            trace.Add(lines[i]);
            i++;
        }

        // Drop a single blank separator the trace block conventionally ends with.
        if (i < lines.Length && lines[i].Trim().Length == 0)
        {
            i++;
        }

        string content = string.Join("\n", lines.Skip(i));
        // Trace (the friendly "Relayed:" path + the raw-headers expando) ABOVE the body, grouped
        // with the message's other headers — the body reads cleanly beneath them.
        return RenderTrace(trace) + Inv($"<pre>{H(content)}</pre>");
    }

    /// <summary>
    /// Friendly rendering of the FBB <c>R:</c> trace: a "Relayed: origin → … → here" path (each hop
    /// is the BBS after <c>@</c>/<c>@:</c> in an R: line; R: lines are prepended newest-first, so we
    /// reverse to read origin→here) plus a collapsed raw block. Empty when there is no trace.
    /// </summary>
    private static string RenderTrace(List<string> trace)
    {
        if (trace.Count == 0)
        {
            return "";
        }

        var hops = new List<string>();
        foreach (string line in trace)
        {
            if (ExtractTraceBbs(line) is { Length: > 0 } bbs)
            {
                hops.Add(bbs);
            }
        }

        hops.Reverse(); // R: lines read newest(here)→oldest(origin) top-down; show origin→here.
        string path = hops.Count > 0
            ? Inv($"""<p class="dim trace-path">Relayed: {H(string.Join(" → ", hops))}</p>""")
            : "";
        string raw = string.Join("\n", trace);
        return path + Inv($"""<details class="trace"><summary>Routing headers ({trace.Count})</summary><pre>{H(raw)}</pre></details>""");
    }

    /// <summary>The relaying BBS callsign in an R: line — the token after <c>@</c> (or <c>@:</c>) up to the first dot/space; null when absent.</summary>
    private static string? ExtractTraceBbs(string rLine)
    {
        int at = rLine.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        int start = at + 1;
        if (start < rLine.Length && rLine[start] == ':')
        {
            start++; // the "@:" form
        }

        int end = start;
        while (end < rLine.Length && rLine[end] != '.' && !char.IsWhiteSpace(rLine[end]))
        {
            end++;
        }

        return end > start ? rLine[start..end] : null;
    }

    /// <summary>Plain-language message type for the reader — no terse wire letters (P/B/T).</summary>
    private static string TypeWord(MessageType type) => type switch
    {
        MessageType.Personal => "Personal",
        MessageType.Bulletin => "Bulletin",
        MessageType.Traffic => "NTS traffic",
        _ => type.ToString(),
    };

    /// <summary>Plain-language message status for the reader — replaces the archaic N/Y/$/F/K codes.</summary>
    private static string StatusWord(MessageStatus status) => status switch
    {
        MessageStatus.Unread => "Unread",
        MessageStatus.Read => "Read",
        MessageStatus.BulletinQueued => "Queued to forward",
        MessageStatus.Forwarded => "Forwarded",
        MessageStatus.Killed => "Killed",
        _ => status.ToString(),
    };

    private static string MessageRows(IReadOnlyList<Message> messages, int page, int pageSize, string call, string prefix, string basePath, bool embed)
    {
        page = Math.Max(1, page);
        var slice = messages.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        if (slice.Count == 0 && page == 1)
        {
            return "<p>No messages.</p>";
        }

        var sb = new StringBuilder();
        sb.Append("<table><tr><th>#</th><th class=\"read-col\"></th><th>From</th><th>To</th><th>Date (UTC)</th><th>Subject</th></tr>");
        foreach (Message m in slice)
        {
            bool unreadByMe = m.Recipients.Any(r =>
                Callsigns.BaseEquals(r.ToCall, call) && r.ReadAt is null);
            // A clear read/unread marker (a dot) in place of the terse status code. unreadByMe is a
            // per-addressee fact, so it lights only for a personal addressed to this user; bulletins
            // (no matching recipient) carry no dot.
            string readDot = unreadByMe ? """<span class="unread-dot" title="Unread">&#9679;</span>""" : "";
            string subject = Inv($"""<a href="{U(prefix, Inv($"/messages/{m.Number}"), embed)}">{H(m.Subject.Length == 0 ? "(no subject)" : m.Subject)}</a>""");
            sb.Append(Inv($"<tr{(unreadByMe ? " class=\"unread\"" : "")}><td>{m.Number}</td>"))
              .Append(Inv($"<td class=\"read-col\">{readDot}</td><td>{H(m.From)}</td>"))
              .Append(Inv($"<td>{H(string.Join(";", m.Recipients.Where(r => !r.Cc).Select(r => r.ToCall)))}{(m.At is null ? "" : "@" + H(m.At))}</td>"))
              .Append(Inv($"<td class=\"nowrap\">{H(m.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}</td><td>{subject}</td></tr>"));
        }

        sb.Append("</table><p class=\"pager\">");
        if (page > 1)
        {
            sb.Append(Inv($"""<a href="{U(prefix, Inv($"{basePath}?page={page - 1}"), embed)}">&laquo; newer</a> """));
        }

        if (messages.Count > page * pageSize)
        {
            sb.Append(Inv($"""<a href="{U(prefix, Inv($"{basePath}?page={page + 1}"), embed)}">older &raquo;</a>"""));
        }

        sb.Append("</p>");
        return sb.ToString();
    }

    /// <summary>
    /// The page chrome. In slot/embed mode (pdn renders us in a borderless iframe with
    /// <c>pdn_embed=1</c>) we OMIT the big <c>&lt;h1&gt;</c> station-identity bar — the panel already
    /// shows the app's name, so emitting our own would double the chrome — and trim the top margin so
    /// the content sits cleanly under the panel header. We KEEP the <c>&lt;title&gt;</c> (harmless in
    /// an iframe) and the <c>&lt;nav&gt;</c> tabs (the app's functional nav, not chrome). It stays a
    /// complete valid HTML document because the iframe loads a full page. Non-embedded output is
    /// byte-for-byte the prior standalone rendering.
    /// </summary>
    private static string Page(WebmailOptions o, string prefix, string call, string title, bool embed, string body, string? theme = null)
    {
        string header = embed
            ? ""
            : $"""<h1>{H(o.StationCallsign)} <span class="dim">webmail</span></h1>""" + "\n";
        // Embedded: trim the top padding so the content sits flush under the panel's own header. An
        // inline style (not a shared-stylesheet rule) keeps the standalone page's CSS byte-for-byte.
        string mainClass = embed ? """ class="embed" style="padding-top:.5rem" """.TrimEnd() : "";
        // The forwarding view is a sysop tool — only the sysop sees its nav tab (the route itself
        // also 403s a non-sysop). Regular users get the unchanged four-tab nav.
        string forwardingTab = IsSysop(o, call)
            ? Inv($""" · <a href="{U(prefix, "/forwarding", embed)}">Forwarding</a>""")
            : "";
        return $$"""
            <!doctype html>
            <html{{HtmlThemeClass(theme)}}><head><meta charset="utf-8"><title>{{H(o.StationCallsign)}} — {{H(title)}}</title>{{Style}}</head>
            <body><main{{mainClass}}>
            {{header}}<nav><a href="{{U(prefix, "/", embed)}}">Inbox</a> · <a href="{{U(prefix, "/bulletins", embed)}}">Bulletins</a> · <a href="{{U(prefix, "/compose", embed)}}">Compose</a> · <a href="{{U(prefix, "/settings", embed)}}">Settings</a>{{forwardingTab}}
            <span class="dim">signed in as {{H(call)}}</span></nav>
            {{body}}
            </main></body></html>
            """;
    }

    // The webmail's self-contained stylesheet, restyled to mirror the PDN control panel's design
    // system (web/packetnet-ui — shadcn-flavoured Tailwind). The colour tokens are PDN's exact HSL
    // triplets, exposed here as CSS variables consumed via hsl(var(--token)) — light under :root,
    // dark under the SAME values PDN uses for .dark. Because the panel renders us in a borderless
    // iframe (a separate document that cannot see the parent's .dark class), we auto-blend with the
    // panel's active theme through prefers-color-scheme; a .dark class on <html> is also honoured for
    // robustness. Fonts, radii, and the button/input/card/table/tab/badge/link looks track PDN's
    // ui/index.tsx primitives. NOT loaded from PDN's compiled bundle at runtime (cross-repo/hashed) —
    // this is deliberately standalone so it renders cleanly both in-panel and on its own.
    private const string Style = """
        <style>
        :root{
          --background:0 0% 100%;--foreground:222 24% 12%;
          --card:0 0% 100%;--card-foreground:222 24% 12%;
          --primary:200 92% 44%;--primary-foreground:0 0% 100%;
          --secondary:220 14% 96%;--secondary-foreground:222 24% 12%;
          --muted:220 14% 96%;--muted-foreground:220 9% 46%;
          --accent:220 14% 94%;--accent-foreground:222 24% 12%;
          --border:220 13% 90%;--input:220 13% 88%;--ring:200 92% 44%;
          --success:152 58% 40%;--warning:33 92% 45%;--danger:0 72% 50%;
          --radius:0.5rem;
        }
        .dark{
          --background:220 26% 7%;--foreground:210 22% 92%;
          --card:220 23% 9.5%;--card-foreground:210 22% 92%;
          --primary:199 89% 52%;--primary-foreground:220 40% 8%;
          --secondary:220 18% 14%;--secondary-foreground:210 22% 92%;
          --muted:220 18% 14%;--muted-foreground:218 12% 58%;
          --accent:220 18% 16%;--accent-foreground:210 22% 92%;
          --border:220 16% 17%;--input:220 16% 19%;--ring:199 89% 52%;
          --success:152 56% 46%;--warning:38 92% 52%;--danger:0 74% 56%;
        }
        @media (prefers-color-scheme:dark){
          :root:not(.light){
            --background:220 26% 7%;--foreground:210 22% 92%;
            --card:220 23% 9.5%;--card-foreground:210 22% 92%;
            --primary:199 89% 52%;--primary-foreground:220 40% 8%;
            --secondary:220 18% 14%;--secondary-foreground:210 22% 92%;
            --muted:220 18% 14%;--muted-foreground:218 12% 58%;
            --accent:220 18% 16%;--accent-foreground:210 22% 92%;
            --border:220 16% 17%;--input:220 16% 19%;--ring:199 89% 52%;
            --success:152 56% 46%;--warning:38 92% 52%;--danger:0 74% 56%;
          }
        }
        *{box-sizing:border-box}
        html{scrollbar-width:thin;scrollbar-color:hsl(var(--border)) transparent}
        ::-webkit-scrollbar{width:10px;height:10px}
        ::-webkit-scrollbar-thumb{background-color:hsl(var(--border));border-radius:9999px;border:2px solid transparent;background-clip:content-box}
        body{
          font-family:Inter,ui-sans-serif,system-ui,sans-serif;margin:0;
          background:hsl(var(--background));color:hsl(var(--foreground));
          font-size:14px;line-height:1.5;-webkit-font-smoothing:antialiased;
        }
        main{max-width:60rem;margin:0 auto;padding:1.5rem}
        h1{font-size:1.25rem;font-weight:600;letter-spacing:-.01em;margin:0 0 1rem}
        h2{font-size:1.05rem;font-weight:600;margin:0 0 .75rem}
        h3{font-size:.9rem;font-weight:600;margin:1.25rem 0 .5rem}
        .dim,h1 .dim,nav .dim,h2 .dim{color:hsl(var(--muted-foreground));font-weight:400}
        a{color:hsl(var(--primary));text-decoration:none}
        a:hover{text-decoration:underline}
        nav{
          display:flex;flex-wrap:wrap;align-items:center;gap:.25rem;
          margin:0 0 1.25rem;padding:.25rem;border-radius:var(--radius);
          background:hsl(var(--muted));font-size:.8rem;
        }
        nav a{
          padding:.3rem .7rem;border-radius:calc(var(--radius) - 2px);
          color:hsl(var(--muted-foreground));font-weight:500;text-decoration:none;
          transition:color .15s,background-color .15s;
        }
        nav a:hover{color:hsl(var(--foreground));text-decoration:none}
        nav .dim{margin-left:auto;padding:0 .5rem}
        table{border-collapse:collapse;width:100%;font-size:.875rem}
        th{
          text-align:left;padding:.4rem .6rem;border-bottom:1px solid hsl(var(--border));
          font-size:.6875rem;font-weight:600;text-transform:uppercase;letter-spacing:.04em;
          color:hsl(var(--muted-foreground));
        }
        td{text-align:left;padding:.45rem .6rem;border-bottom:1px solid hsl(var(--border));vertical-align:top}
        tr.unread td{font-weight:600}
        th.read-col,td.read-col{width:1.4rem;text-align:center;padding-left:0;padding-right:.2rem}
        .unread-dot{color:hsl(var(--primary));font-size:.7rem;line-height:1}
        .back{margin:0 0 .85rem;font-size:.8rem}
        td.nowrap{white-space:nowrap}
        .trace-path{margin:1rem 0 .25rem}
        details.trace{margin:0 0 1rem;font-size:.8rem}
        details.trace summary{cursor:pointer;color:hsl(var(--muted-foreground));user-select:none}
        details.trace pre{margin-top:.5rem;font-size:.78rem;line-height:1.5}
        tbody tr:hover td,table tr:hover td{background:hsl(var(--accent)/.5)}
        pre{
          background:hsl(var(--card));border:1px solid hsl(var(--border));
          border-radius:var(--radius);padding:1rem;white-space:pre-wrap;word-break:break-word;
          font-family:"JetBrains Mono",ui-monospace,SFMono-Regular,monospace;font-size:.85rem;line-height:1.55;
        }
        table.meta{width:auto}
        table.meta th{
          width:6rem;text-transform:none;letter-spacing:0;font-size:.8rem;font-weight:500;
          color:hsl(var(--muted-foreground));border-bottom:0;padding:.2rem .6rem .2rem 0;vertical-align:top;
        }
        table.meta td{border-bottom:0;padding:.2rem 0}
        table.meta tr:hover td{background:none}
        .err{
          color:hsl(var(--danger));background:hsl(var(--danger)/.1);
          border-radius:calc(var(--radius) - 2px);padding:.5rem .75rem;margin:.75rem 0;font-size:.875rem;
        }
        .saved{
          color:hsl(var(--success));background:hsl(var(--success)/.12);
          border-radius:calc(var(--radius) - 2px);padding:.5rem .75rem;margin:.75rem 0;font-size:.875rem;
        }
        p.dim{margin:.5rem 0}
        .pager{margin-top:1rem;font-size:.875rem;display:flex;gap:1rem}
        form{margin:0 0 1rem}
        fieldset{
          border:1px solid hsl(var(--border));border-radius:var(--radius);
          margin:0 0 1rem;padding:1rem 1.25rem;background:hsl(var(--card));
        }
        legend{padding:0 .4rem;color:hsl(var(--muted-foreground));font-size:.8rem;font-weight:600}
        label{display:inline-block}
        ul.attachments{padding-left:1.2rem;font-size:.875rem}
        ul.attachments .dim{color:hsl(var(--muted-foreground))}
        ul.sevenplus-pending{list-style:none;padding:0;margin:0 0 1rem}
        ul.sevenplus-pending li{
          padding:.5rem .75rem;border:1px dashed hsl(var(--border));background:hsl(var(--muted)/.5);
          border-radius:calc(var(--radius) - 2px);margin-bottom:.4rem;font-size:.875rem;color:hsl(var(--foreground));
        }
        input,select,textarea{
          font:inherit;color:hsl(var(--foreground));background:hsl(var(--background));
          border:1px solid hsl(var(--input));border-radius:calc(var(--radius) - 2px);
          padding:.4rem .6rem;font-size:.875rem;transition:border-color .15s,box-shadow .15s;
        }
        input[type=radio],input[type=checkbox]{
          border-radius:0;padding:0;accent-color:hsl(var(--primary));vertical-align:middle;margin-right:.35rem;
        }
        input[type=file]{border:0;padding:.2rem 0;background:none}
        textarea{width:100%;max-width:100%;font-family:"JetBrains Mono",ui-monospace,SFMono-Regular,monospace;line-height:1.55}
        input:focus,select:focus,textarea:focus{
          outline:none;border-color:hsl(var(--ring));box-shadow:0 0 0 2px hsl(var(--ring)/.35);
        }
        button{
          font:inherit;font-weight:500;font-size:.875rem;cursor:pointer;
          padding:.45rem 1rem;border-radius:calc(var(--radius) - 2px);border:1px solid transparent;
          background:hsl(var(--primary));color:hsl(var(--primary-foreground));
          box-shadow:0 1px 2px 0 hsl(220 26% 7%/.06);transition:background-color .15s,box-shadow .15s;
        }
        button:hover{background:hsl(var(--primary)/.9)}
        button:focus-visible{outline:none;box-shadow:0 0 0 2px hsl(var(--background)),0 0 0 4px hsl(var(--ring))}
        button.link{
          padding:0;border:0;background:none;box-shadow:none;font-weight:400;
          color:hsl(var(--danger));text-decoration:underline;
        }
        button.link:hover{background:none;color:hsl(var(--danger)/.85)}
        code{
          font-family:"JetBrains Mono",ui-monospace,SFMono-Regular,monospace;font-size:.85em;
          background:hsl(var(--muted)/.6);padding:.05rem .3rem;border-radius:calc(var(--radius) - 4px);
        }
        .badge{
          display:inline-block;font-size:.7rem;font-weight:600;padding:.1rem .45rem;
          border-radius:9999px;text-transform:uppercase;letter-spacing:.03em;vertical-align:middle;margin-left:.4rem;
        }
        .badge.on{color:hsl(var(--success));background:hsl(var(--success)/.12)}
        .badge.off{color:hsl(var(--muted-foreground));background:hsl(var(--muted)/.7)}
        ul.routes{list-style:none;padding:0;margin:.5rem 0 0;font-size:.85rem}
        ul.routes li{padding:.15rem 0;border-bottom:0}
        ul.routes .label{
          display:inline-block;min-width:8.5rem;color:hsl(var(--muted-foreground));
          font-size:.72rem;text-transform:uppercase;letter-spacing:.03em;font-weight:600;margin-right:.5rem;
        }
        nav.subtabs{display:inline-flex;gap:.2rem;margin:0 0 1rem;width:auto}
        nav.subtabs a.active{background:hsl(var(--background));color:hsl(var(--foreground));box-shadow:0 1px 2px 0 hsl(220 26% 7%/.06)}
        p.card-actions{display:flex;align-items:center;gap:1rem;margin:.75rem 0 0;font-size:.85rem}
        p.card-actions form{display:inline;margin:0}
        button.link.plain{color:hsl(var(--primary))}
        button.link.plain:hover{color:hsl(var(--primary)/.85)}
        a.cancel{font-size:.85rem;color:hsl(var(--muted-foreground));margin-left:.5rem}
        </style>
        """;

    private static IResult Html(string html, int statusCode = StatusCodes.Status200OK) =>
        Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);

    private static string H(string text) => WebUtility.HtmlEncode(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>
    /// The <c>&lt;html&gt;</c> class attribute that pins an explicit theme — <c>" class=\"dark\""</c>
    /// or <c>" class=\"light\""</c> — so the page overrides <c>prefers-color-scheme</c> (the existing
    /// stylesheet honours <c>.dark</c> and <c>:root:not(.light)</c>). Empty when no theme is resolved,
    /// leaving the plain <c>&lt;html&gt;</c> (prefers-color-scheme behaviour) for standalone rendering.
    /// </summary>
    private static string HtmlThemeClass(string? theme) => theme is "dark" or "light" ? $" class=\"{theme}\"" : "";
}
