using System.Globalization;
using System.Net;
using System.Text;
using Bbs.Core;
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

    /// <summary>The BBS callsign (display + acceptance lines).</summary>
    public required string BbsCallsign { get; init; }

    /// <summary>The sysop callsign (sysop visibility in webmail), or empty.</summary>
    public string SysopCallsign { get; init; } = "";

    /// <summary>Rows per page on the inbox/bulletins lists.</summary>
    public int PageSize { get; init; } = 25;
}

/// <summary>
/// The webmail surface (design.md decision 4): server-rendered HTML on a loopback bind,
/// trusting the pdn app-gateway identity contract — every request must carry
/// <c>X-Pdn-Gateway: 1</c> (else 403) and identity arrives in <c>X-Pdn-User</c>. pdn
/// usernames map to BBS callsigns through the Core user table
/// (<see cref="User.PdnUsername"/>); an unmapped username gets a claim-your-callsign form.
/// Surfaces: inbox (personals to my callsign), bulletins (paged), read, compose (P/B
/// through the same store path as the console — BID generation + routing enqueue), and
/// kill-my-message. No JS, two forms.
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

            await next().ConfigureAwait(false);
        });

        app.MapGet("/", (HttpContext ctx, int? page) => WithCallsign(ctx, options,
            (prefix, call) => Inbox(options, prefix, call, page ?? 1)));

        app.MapGet("/bulletins", (HttpContext ctx, int? page) => WithCallsign(ctx, options,
            (prefix, call) => Bulletins(options, prefix, call, page ?? 1)));

        app.MapGet("/messages/{number:long}", (HttpContext ctx, long number) => WithCallsign(ctx, options,
            (prefix, call) => ReadMessage(options, prefix, call, number)));

        app.MapGet("/messages/{number:long}/attachments/{name}", (HttpContext ctx, long number, string name) =>
            WithCallsign(ctx, options, (_, call) => DownloadAttachment(options, call, number, name)));

        app.MapGet("/compose", (HttpContext ctx, string? to, string? type) => WithCallsign(ctx, options,
            (prefix, call) => ComposeForm(options, prefix, call, to, type)));

        app.MapPost("/compose", async (HttpContext ctx) =>
        {
            string user = PdnUser(ctx);
            string prefix = Prefix(ctx);
            string? call = FindCallsign(options.Store, user);
            if (call is null)
            {
                return Results.Redirect(U(prefix, "/"));
            }

            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return Compose(options, prefix, call, form);
        });

        app.MapPost("/messages/{number:long}/kill", (HttpContext ctx, long number) => WithCallsign(ctx, options,
            (prefix, call) => Kill(options, prefix, call, number)));

        app.MapPost("/claim", async (HttpContext ctx) =>
        {
            string user = PdnUser(ctx);
            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return Claim(options, Prefix(ctx), user, form["callsign"].ToString());
        });
    }

    private const string PdnUserKey = "pdnUser";
    private const string PrefixKey = "pdnPrefix";

    private static string PdnUser(HttpContext ctx) => (string)ctx.Items[PdnUserKey]!;

    private static string Prefix(HttpContext ctx) => (string)ctx.Items[PrefixKey]!;

    /// <summary>Roots an absolute path under the gateway mount prefix ("" when direct).</summary>
    private static string U(string prefix, string path) => prefix + path;

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
        return call is null ? ClaimForm(prefix, user, error: null) : handler(prefix, call);
    }

    // ---------------------------------------------------------------- pages

    private static IResult Inbox(WebmailOptions o, string prefix, string call, int page)
    {
        IReadOnlyList<Message> mine = HideSevenPlusParts(o.Store, o.Store.ListMessages(new MessageQuery
        {
            Type = MessageType.Personal,
            ToCall = call,
        }));
        // Personal placeholders are scoped to this user's incoming files only (never leak another
        // user's private incoming-file name into this inbox).
        string placeholders = SevenPlusPlaceholders(o.Store, MessageType.Personal, call);
        string rows = MessageRows(mine, page, o.PageSize, call, prefix, "/");
        return Html(Page(o, prefix, call, "Inbox",
            $"""
            <h2>Inbox — personal messages for {H(call)}</h2>
            {placeholders}
            {rows}
            """));
    }

    private static IResult Bulletins(WebmailOptions o, string prefix, string call, int page)
    {
        IReadOnlyList<Message> bulls = HideSevenPlusParts(o.Store, o.Store.ListMessages(new MessageQuery { Type = MessageType.Bulletin }));
        // Bulletins are world-visible, so the bulletin placeholders are not recipient-scoped.
        string placeholders = SevenPlusPlaceholders(o.Store, MessageType.Bulletin, recipientCall: null);
        string rows = MessageRows(bulls, page, o.PageSize, call, prefix, "/bulletins");
        return Html(Page(o, prefix, call, "Bulletins",
            $"""
            <h2>Bulletins</h2>
            {placeholders}
            {rows}
            """));
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

    private static IResult ReadMessage(WebmailOptions o, string prefix, string call, long number)
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
            ? $"""<form method="post" action="{U(prefix, Inv($"/messages/{message.Number}/kill"))}"><button type="submit">Kill message</button></form>"""
            : "";
        string to = string.Join("; ", message.Recipients.Where(r => !r.Cc).Select(r => r.ToCall));
        string ccRow = RenderCcRow(message);
        string attachments = RenderAttachments(message, prefix);
        return Html(Page(o, prefix, call, Inv($"Message {message.Number}"),
            $"""
            <h2>Message {message.Number} <span class="dim">[{H(message.Type.ToCode().ToString())}/{H(message.Status.ToCode().ToString())}]</span></h2>
            <table class="meta">
            <tr><th>From</th><td>{H(message.From)}</td></tr>
            <tr><th>To</th><td>{H(to)}{(message.At is null ? "" : " @ " + H(message.At))}</td></tr>
            {ccRow}
            <tr><th>Subject</th><td>{H(message.Subject)}</td></tr>
            <tr><th>BID</th><td>{H(message.Bid)}</td></tr>
            <tr><th>Date</th><td>{H(message.CreatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))}</td></tr>
            </table>
            <pre>{H(message.GetBodyText())}</pre>
            {attachments}
            {killForm}
            """));
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

    private static IResult ComposeForm(WebmailOptions o, string prefix, string call, string? to, string? type)
    {
        bool bulletin = string.Equals(type, "B", StringComparison.OrdinalIgnoreCase);
        return Html(Page(o, prefix, call, "Compose",
            $"""
            <h2>Compose</h2>
            <form method="post" action="{U(prefix, "/compose")}">
            <p><label>Type
            <select name="type">
            <option value="P"{(bulletin ? "" : " selected")}>Personal</option>
            <option value="B"{(bulletin ? " selected" : "")}>Bulletin</option>
            </select></label></p>
            <p><label>To <input name="to" value="{H(to ?? "")}" placeholder="callsign or topic" required></label>
            <label>@ <input name="at" placeholder="route, e.g. GB7BPQ.#23.GBR.EURO or EURO"></label></p>
            <p><label>Subject <input name="subject" size="60" maxlength="60" required></label></p>
            <p><textarea name="body" rows="12" cols="72"></textarea></p>
            <p><button type="submit">Send</button></p>
            </form>
            """));
    }

    private static IResult Compose(WebmailOptions o, string prefix, string call, IFormCollection form)
    {
        string typeField = form["type"].ToString().Trim().ToUpperInvariant();
        MessageType type = typeField == "B" ? MessageType.Bulletin : MessageType.Personal;
        string to = form["to"].ToString().Trim();
        string at = form["at"].ToString().Trim();
        string subject = form["subject"].ToString().Trim();
        string body = form["body"].ToString();

        if (to.Length == 0 || subject.Length == 0)
        {
            return Html(Page(o, prefix, call, "Compose",
                $"""<h2>Compose</h2><p class="err">TO and Subject are required.</p><p><a href="{U(prefix, "/compose")}">Back</a></p>"""),
                StatusCodes.Status400BadRequest);
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
            return Html(Page(o, prefix, call, "Compose",
                $"""<h2>Compose</h2><p class="err">TO is required.</p><p><a href="{U(prefix, "/compose")}">Back</a></p>"""),
                StatusCodes.Status400BadRequest);
        }

        // CR line discipline, like the console's body loop stores it (§1.5 step 6).
        string crBody = body.ReplaceLineEndings("\r");
        if (crBody.Length > 0 && !crBody.EndsWith('\r'))
        {
            crBody += "\r";
        }

        Message stored = o.Store.AddMessage(new MessageDraft
        {
            Type = type,
            From = call,
            Recipients = recipients,
            At = at.Length == 0 ? null : at,
            Subject = subject,
            Body = Encoding.Latin1.GetBytes(crBody),
        });
        o.Routing.RouteMessage(stored);
        return Results.Redirect(U(prefix, Inv($"/messages/{stored.Number}")));
    }

    private static IResult Kill(WebmailOptions o, string prefix, string call, long number)
    {
        Message? message = o.Store.GetMessage(number);
        if (message is null || !MessageRules.CanKill(message, call, IsSysop(o, call)))
        {
            return Results.NotFound("No such message (or not yours to kill).");
        }

        o.Store.Kill(number);
        return Results.Redirect(U(prefix, "/"));
    }

    private static IResult Claim(WebmailOptions o, string prefix, string pdnUser, string callsignInput)
    {
        string call = Callsigns.NormalizeAddressee(callsignInput);
        if (call.Length < 3 || !call.All(char.IsAsciiLetterOrDigit) || !call.Any(char.IsAsciiDigit))
        {
            return ClaimForm(prefix, pdnUser, "That doesn't look like a callsign.", StatusCodes.Status400BadRequest);
        }

        User? existing = o.Store.GetUser(call);
        if (existing?.PdnUsername is { Length: > 0 } owner
            && !string.Equals(owner, pdnUser, StringComparison.OrdinalIgnoreCase))
        {
            return ClaimForm(prefix, pdnUser, Inv($"{call} is already linked to another pdn account."), StatusCodes.Status409Conflict);
        }

        User user = (existing ?? new User { Callsign = call }) with { PdnUsername = pdnUser };
        o.Store.UpsertUser(user);
        return Results.Redirect(U(prefix, "/"));
    }

    private static IResult ClaimForm(string prefix, string pdnUser, string? error, int status = StatusCodes.Status200OK)
    {
        string err = error is null ? "" : $"""<p class="err">{H(error)}</p>""";
        return Html($$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>pdn-bbs — claim your callsign</title>{{Style}}</head>
            <body><main>
            <h1>pdn-bbs</h1>
            <p>Hello <b>{{H(pdnUser)}}</b>. This pdn account isn't linked to a callsign yet.</p>
            {{err}}
            <form method="post" action="{{U(prefix, "/claim")}}">
            <p><label>Your callsign <input name="callsign" maxlength="9" required></label>
            <button type="submit">Claim</button></p>
            </form>
            </main></body></html>
            """, status);
    }

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
    private static string RenderAttachments(Message message, string prefix)
    {
        if (message.Attachments.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder("<h3>Attachments</h3><ul class=\"attachments\">");
        foreach (MessageAttachment a in message.Attachments)
        {
            string href = U(prefix, Inv($"/messages/{message.Number}/attachments/{Uri.EscapeDataString(a.Name)}"));
            sb.Append(Inv($"""<li><a href="{H(href)}">{H(a.Name)}</a> <span class="dim">({a.Content.Length} bytes)</span></li>"""));
        }

        sb.Append("</ul>");
        return sb.ToString();
    }

    private static string MessageRows(IReadOnlyList<Message> messages, int page, int pageSize, string call, string prefix, string basePath)
    {
        page = Math.Max(1, page);
        var slice = messages.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        if (slice.Count == 0 && page == 1)
        {
            return "<p>No messages.</p>";
        }

        var sb = new StringBuilder();
        sb.Append("<table><tr><th>#</th><th>St</th><th>From</th><th>To</th><th>Date</th><th>Subject</th></tr>");
        foreach (Message m in slice)
        {
            bool unreadByMe = m.Recipients.Any(r =>
                Callsigns.BaseEquals(r.ToCall, call) && r.ReadAt is null);
            string subject = Inv($"""<a href="{U(prefix, Inv($"/messages/{m.Number}"))}">{H(m.Subject.Length == 0 ? "(no subject)" : m.Subject)}</a>""");
            sb.Append(Inv($"<tr{(unreadByMe ? " class=\"unread\"" : "")}><td>{m.Number}</td>"))
              .Append(Inv($"<td>{H(m.Status.ToCode().ToString())}</td><td>{H(m.From)}</td>"))
              .Append(Inv($"<td>{H(string.Join(";", m.Recipients.Where(r => !r.Cc).Select(r => r.ToCall)))}{(m.At is null ? "" : "@" + H(m.At))}</td>"))
              .Append(Inv($"<td>{H(m.CreatedAt.ToString("yyMMdd", CultureInfo.InvariantCulture))}</td><td>{subject}</td></tr>"));
        }

        sb.Append("</table><p class=\"pager\">");
        if (page > 1)
        {
            sb.Append(Inv($"""<a href="{U(prefix, basePath)}?page={page - 1}">&laquo; newer</a> """));
        }

        if (messages.Count > page * pageSize)
        {
            sb.Append(Inv($"""<a href="{U(prefix, basePath)}?page={page + 1}">older &raquo;</a>"""));
        }

        sb.Append("</p>");
        return sb.ToString();
    }

    private static string Page(WebmailOptions o, string prefix, string call, string title, string body) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>{{H(o.BbsCallsign)}} — {{H(title)}}</title>{{Style}}</head>
        <body><main>
        <h1>{{H(o.BbsCallsign)}} <span class="dim">webmail</span></h1>
        <nav><a href="{{U(prefix, "/")}}">Inbox</a> · <a href="{{U(prefix, "/bulletins")}}">Bulletins</a> · <a href="{{U(prefix, "/compose")}}">Compose</a>
        <span class="dim">— de {{H(call)}}</span></nav>
        {{body}}
        </main></body></html>
        """;

    private const string Style = """
        <style>
        body{font-family:system-ui,sans-serif;margin:0;background:#f4f3ef;color:#1c1c1a}
        main{max-width:60rem;margin:0 auto;padding:1.5rem}
        h1{font-size:1.3rem}h1 .dim,nav .dim,h2 .dim{color:#8a857c;font-weight:normal}
        nav{margin-bottom:1rem}
        table{border-collapse:collapse;width:100%}
        th,td{text-align:left;padding:.25rem .5rem;border-bottom:1px solid #ddd8cf;font-size:.92rem}
        tr.unread td{font-weight:bold}
        pre{background:#fff;border:1px solid #ddd8cf;padding:1rem;white-space:pre-wrap;word-break:break-word}
        table.meta th{width:6rem;color:#8a857c;font-weight:normal}
        .err{color:#a32014}.pager{margin-top:.75rem}
        ul.attachments{padding-left:1.2rem}ul.attachments .dim{color:#8a857c}
        ul.sevenplus-pending{list-style:none;padding:0;margin:0 0 1rem}
        ul.sevenplus-pending li{padding:.3rem .6rem;border:1px dashed #c9c3b8;background:#fbfaf6;border-radius:3px;margin-bottom:.3rem;font-size:.92rem}
        input,select,textarea{font:inherit}
        button{font:inherit;padding:.2rem .8rem}
        </style>
        """;

    private static IResult Html(string html, int statusCode = StatusCodes.Status200OK) =>
        Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);

    private static string H(string text) => WebUtility.HtmlEncode(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
