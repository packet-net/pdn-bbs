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
            await next().ConfigureAwait(false);
        });

        app.MapGet("/", (HttpContext ctx, int? page) => WithCallsign(ctx, options,
            (user, call) => Inbox(options, call, page ?? 1)));

        app.MapGet("/bulletins", (HttpContext ctx, int? page) => WithCallsign(ctx, options,
            (user, call) => Bulletins(options, call, page ?? 1)));

        app.MapGet("/messages/{number:long}", (HttpContext ctx, long number) => WithCallsign(ctx, options,
            (user, call) => ReadMessage(options, call, number)));

        app.MapGet("/compose", (HttpContext ctx, string? to, string? type) => WithCallsign(ctx, options,
            (user, call) => ComposeForm(options, call, to, type)));

        app.MapPost("/compose", async (HttpContext ctx) =>
        {
            string user = PdnUser(ctx);
            string? call = FindCallsign(options.Store, user);
            if (call is null)
            {
                return Results.Redirect("/");
            }

            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return Compose(options, call, form);
        });

        app.MapPost("/messages/{number:long}/kill", (HttpContext ctx, long number) => WithCallsign(ctx, options,
            (user, call) => Kill(options, call, number)));

        app.MapPost("/claim", async (HttpContext ctx) =>
        {
            string user = PdnUser(ctx);
            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return Claim(options, user, form["callsign"].ToString());
        });
    }

    private const string PdnUserKey = "pdnUser";

    private static string PdnUser(HttpContext ctx) => (string)ctx.Items[PdnUserKey]!;

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
        string? call = FindCallsign(options.Store, user);
        return call is null ? ClaimForm(user, error: null) : handler(user, call);
    }

    // ---------------------------------------------------------------- pages

    private static IResult Inbox(WebmailOptions o, string call, int page)
    {
        IReadOnlyList<Message> mine = o.Store.ListMessages(new MessageQuery
        {
            Type = MessageType.Personal,
            ToCall = call,
        });
        string rows = MessageRows(mine, page, o.PageSize, call, "/");
        return Html(Page(o, call, "Inbox",
            $"""
            <h2>Inbox — personal messages for {H(call)}</h2>
            {rows}
            """));
    }

    private static IResult Bulletins(WebmailOptions o, string call, int page)
    {
        IReadOnlyList<Message> bulls = o.Store.ListMessages(new MessageQuery { Type = MessageType.Bulletin });
        string rows = MessageRows(bulls, page, o.PageSize, call, "/bulletins");
        return Html(Page(o, call, "Bulletins",
            $"""
            <h2>Bulletins</h2>
            {rows}
            """));
    }

    private static IResult ReadMessage(WebmailOptions o, string call, long number)
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
            ? $"""<form method="post" action="/messages/{message.Number}/kill"><button type="submit">Kill message</button></form>"""
            : "";
        string to = string.Join("; ", message.Recipients.Select(r => r.ToCall));
        return Html(Page(o, call, Inv($"Message {message.Number}"),
            $"""
            <h2>Message {message.Number} <span class="dim">[{H(message.Type.ToCode().ToString())}/{H(message.Status.ToCode().ToString())}]</span></h2>
            <table class="meta">
            <tr><th>From</th><td>{H(message.From)}</td></tr>
            <tr><th>To</th><td>{H(to)}{(message.At is null ? "" : " @ " + H(message.At))}</td></tr>
            <tr><th>Subject</th><td>{H(message.Subject)}</td></tr>
            <tr><th>BID</th><td>{H(message.Bid)}</td></tr>
            <tr><th>Date</th><td>{H(message.CreatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))}</td></tr>
            </table>
            <pre>{H(message.GetBodyText())}</pre>
            {killForm}
            """));
    }

    private static IResult ComposeForm(WebmailOptions o, string call, string? to, string? type)
    {
        bool bulletin = string.Equals(type, "B", StringComparison.OrdinalIgnoreCase);
        return Html(Page(o, call, "Compose",
            $"""
            <h2>Compose</h2>
            <form method="post" action="/compose">
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

    private static IResult Compose(WebmailOptions o, string call, IFormCollection form)
    {
        string typeField = form["type"].ToString().Trim().ToUpperInvariant();
        MessageType type = typeField == "B" ? MessageType.Bulletin : MessageType.Personal;
        string to = form["to"].ToString().Trim();
        string at = form["at"].ToString().Trim();
        string subject = form["subject"].ToString().Trim();
        string body = form["body"].ToString();

        if (to.Length == 0 || subject.Length == 0)
        {
            return Html(Page(o, call, "Compose",
                """<h2>Compose</h2><p class="err">TO and Subject are required.</p><p><a href="/compose">Back</a></p>"""),
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
            return Html(Page(o, call, "Compose",
                """<h2>Compose</h2><p class="err">TO is required.</p><p><a href="/compose">Back</a></p>"""),
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
        return Results.Redirect(Inv($"/messages/{stored.Number}"));
    }

    private static IResult Kill(WebmailOptions o, string call, long number)
    {
        Message? message = o.Store.GetMessage(number);
        if (message is null || !MessageRules.CanKill(message, call, IsSysop(o, call)))
        {
            return Results.NotFound("No such message (or not yours to kill).");
        }

        o.Store.Kill(number);
        return Results.Redirect("/");
    }

    private static IResult Claim(WebmailOptions o, string pdnUser, string callsignInput)
    {
        string call = Callsigns.NormalizeAddressee(callsignInput);
        if (call.Length < 3 || !call.All(char.IsAsciiLetterOrDigit) || !call.Any(char.IsAsciiDigit))
        {
            return ClaimForm(pdnUser, "That doesn't look like a callsign.", StatusCodes.Status400BadRequest);
        }

        User? existing = o.Store.GetUser(call);
        if (existing?.PdnUsername is { Length: > 0 } owner
            && !string.Equals(owner, pdnUser, StringComparison.OrdinalIgnoreCase))
        {
            return ClaimForm(pdnUser, Inv($"{call} is already linked to another pdn account."), StatusCodes.Status409Conflict);
        }

        User user = (existing ?? new User { Callsign = call }) with { PdnUsername = pdnUser };
        o.Store.UpsertUser(user);
        return Results.Redirect("/");
    }

    private static IResult ClaimForm(string pdnUser, string? error, int status = StatusCodes.Status200OK)
    {
        string err = error is null ? "" : $"""<p class="err">{H(error)}</p>""";
        return Html($$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>pdn-bbs — claim your callsign</title>{{Style}}</head>
            <body><main>
            <h1>pdn-bbs</h1>
            <p>Hello <b>{{H(pdnUser)}}</b>. This pdn account isn't linked to a callsign yet.</p>
            {{err}}
            <form method="post" action="/claim">
            <p><label>Your callsign <input name="callsign" maxlength="9" required></label>
            <button type="submit">Claim</button></p>
            </form>
            </main></body></html>
            """, status);
    }

    // ---------------------------------------------------------------- rendering

    private static bool IsSysop(WebmailOptions o, string call) =>
        o.SysopCallsign.Length > 0 && Callsigns.BaseEquals(o.SysopCallsign, call);

    private static string MessageRows(IReadOnlyList<Message> messages, int page, int pageSize, string call, string basePath)
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
            string subject = Inv($"""<a href="/messages/{m.Number}">{H(m.Subject.Length == 0 ? "(no subject)" : m.Subject)}</a>""");
            sb.Append(Inv($"<tr{(unreadByMe ? " class=\"unread\"" : "")}><td>{m.Number}</td>"))
              .Append(Inv($"<td>{H(m.Status.ToCode().ToString())}</td><td>{H(m.From)}</td>"))
              .Append(Inv($"<td>{H(string.Join(";", m.Recipients.Select(r => r.ToCall)))}{(m.At is null ? "" : "@" + H(m.At))}</td>"))
              .Append(Inv($"<td>{H(m.CreatedAt.ToString("yyMMdd", CultureInfo.InvariantCulture))}</td><td>{subject}</td></tr>"));
        }

        sb.Append("</table><p class=\"pager\">");
        if (page > 1)
        {
            sb.Append(Inv($"""<a href="{basePath}?page={page - 1}">&laquo; newer</a> """));
        }

        if (messages.Count > page * pageSize)
        {
            sb.Append(Inv($"""<a href="{basePath}?page={page + 1}">older &raquo;</a>"""));
        }

        sb.Append("</p>");
        return sb.ToString();
    }

    private static string Page(WebmailOptions o, string call, string title, string body) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>{{H(o.BbsCallsign)}} — {{H(title)}}</title>{{Style}}</head>
        <body><main>
        <h1>{{H(o.BbsCallsign)}} <span class="dim">webmail</span></h1>
        <nav><a href="/">Inbox</a> · <a href="/bulletins">Bulletins</a> · <a href="/compose">Compose</a>
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
        input,select,textarea{font:inherit}
        button{font:inherit;padding:.2rem .8rem}
        </style>
        """;

    private static IResult Html(string html, int statusCode = StatusCodes.Status200OK) =>
        Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);

    private static string H(string text) => WebUtility.HtmlEncode(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
