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
    /// The per-user console preferences store (the same singleton the console session uses —
    /// <c>HostComposition</c> wires the <see cref="Bbs.Host.Sessions.JsonUserSettingsStore"/>).
    /// Backs the <c>/settings</c> interface-mode toggle so a webmail flip is the same persisted
    /// choice the next console connect reads.
    /// </summary>
    public required IUserSettingsStore Settings { get; init; }

    /// <summary>The BBS callsign (display + acceptance lines).</summary>
    public required string BbsCallsign { get; init; }

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

            // ReadFormAsync transparently parses BOTH application/x-www-form-urlencoded (the legacy
            // no-file case) and multipart/form-data (the file-upload case): text fields land in
            // form[...] and an uploaded file in form.Files. The pdn app-gateway reverse-proxies the
            // raw request body to our loopback upstream (manifest ui.upstream) with the Content-Type
            // intact, so multipart flows through unmodified — there is no body rewrite or size cap in
            // the gateway/app-package wiring (pdn carries zero BBS-specific code). We cap the upload
            // ourselves below (WebmailOptions.MaxUploadBytes) rather than relying on a gateway limit.
            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return Compose(options, prefix, call, form, form.Files.GetFile("file"));
        });

        app.MapPost("/messages/{number:long}/kill", (HttpContext ctx, long number) => WithCallsign(ctx, options,
            (prefix, call) => Kill(options, prefix, call, number)));

        app.MapPost("/claim", async (HttpContext ctx) =>
        {
            string user = PdnUser(ctx);
            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return Claim(options, Prefix(ctx), user, form["callsign"].ToString());
        });

        app.MapGet("/settings", (HttpContext ctx) => WithCallsign(ctx, options,
            (prefix, call) => SettingsPage(options, prefix, call, saved: false)));

        app.MapPost("/settings", async (HttpContext ctx) =>
        {
            string prefix = Prefix(ctx);
            string? call = FindCallsign(options.Store, PdnUser(ctx));
            if (call is null)
            {
                return Results.Redirect(U(prefix, "/"));
            }

            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            return SaveSettings(options, prefix, call, form["interface"].ToString());
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

    private static IResult ComposeForm(
        WebmailOptions o, string prefix, string call, string? to, string? type,
        string? error = null, int status = StatusCodes.Status200OK)
    {
        bool bulletin = string.Equals(type, "B", StringComparison.OrdinalIgnoreCase);
        string err = error is null ? "" : $"""<p class="err">{H(error)}</p>""";

        // The file-handling choice (the send-side file slice): attach a file and pick how it travels.
        // 7plus is the UNIVERSAL choice (the parts ride in the body text over any path — B1 or B2, and
        // even a text-only TNC2 link); a binary B2 attachment only reaches B2-capable partners. The
        // form posts multipart/form-data so Request.Form.Files surfaces the upload; with no file the
        // mode is ignored, so the dominant no-file case is unchanged behaviour.
        return Html(Page(o, prefix, call, "Compose",
            $"""
            <h2>Compose</h2>
            {err}
            <form method="post" action="{U(prefix, "/compose")}" enctype="multipart/form-data">
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
            """), status);
    }

    private static IResult Compose(WebmailOptions o, string prefix, string call, IFormCollection form, IFormFile? file)
    {
        string typeField = form["type"].ToString().Trim().ToUpperInvariant();
        MessageType type = typeField == "B" ? MessageType.Bulletin : MessageType.Personal;
        string to = form["to"].ToString().Trim();
        string at = form["at"].ToString().Trim();
        string subject = form["subject"].ToString().Trim();
        string body = form["body"].ToString();

        if (to.Length == 0 || subject.Length == 0)
        {
            return ComposeForm(o, prefix, call, to, typeField, "TO and Subject are required.", StatusCodes.Status400BadRequest);
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
            return ComposeForm(o, prefix, call, to, typeField, "TO is required.", StatusCodes.Status400BadRequest);
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
                return ComposeForm(o, prefix, call, to, typeField,
                    Inv($"That file is too large ({file.Length} bytes); the limit is {o.MaxUploadBytes} bytes."),
                    StatusCodes.Status400BadRequest);
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
        return Results.Redirect(U(prefix, Inv($"/messages/{stored.Number}")));
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

    // ---------------------------------------------------------------- settings (interface-mode toggle)

    /// <summary>
    /// The settings page: shows the user's current interface mode (plain / classic — the same
    /// <see cref="UserSettings.InterfaceMode"/> the console session reads) and a control to change
    /// it. Plain is the default for a never-set user (the plain-language mandate, design.md). The
    /// form posts back to the prefix-correct <c>/settings</c> mount. A <paramref name="saved"/>
    /// banner confirms a just-applied change.
    /// </summary>
    private static IResult SettingsPage(WebmailOptions o, string prefix, string call, bool saved)
    {
        // Null InterfaceMode means "never chosen" → the plain default per the mandate.
        InterfaceMode mode = o.Settings.Load(call).InterfaceMode ?? InterfaceMode.Plain;
        bool plain = mode == InterfaceMode.Plain;
        string banner = saved
            ? """<p class="saved">Saved. Your choice applies to your next mailbox connection.</p>"""
            : "";

        return Html(Page(o, prefix, call, "Settings",
            $"""
            <h2>Settings</h2>
            {banner}
            <form method="post" action="{U(prefix, "/settings")}">
            <fieldset><legend>Mailbox command surface</legend>
            <p>How the RF/console mailbox talks to you when you connect over packet.</p>
            <p><label><input type="radio" name="interface" value="plain"{(plain ? " checked" : "")}> Plain language <span class="dim">— sentences and whole words (the default)</span></label></p>
            <p><label><input type="radio" name="interface" value="classic"{(plain ? "" : " checked")}> Classic <span class="dim">— the terse W0RLI letters, for old automated clients</span></label></p>
            </fieldset>
            <p><button type="submit">Save</button></p>
            </form>
            """));
    }

    /// <summary>
    /// Applies the posted interface choice: loads the user's <see cref="UserSettings"/>, sets
    /// <see cref="UserSettings.InterfaceMode"/> and saves it (so the next console connect reads the
    /// new surface), then re-renders the page with a confirmation. An unrecognised value falls back
    /// to plain (the mandate's default) rather than erroring.
    /// </summary>
    private static IResult SaveSettings(WebmailOptions o, string prefix, string call, string interfaceValue)
    {
        InterfaceMode mode = string.Equals(interfaceValue.Trim(), "classic", StringComparison.OrdinalIgnoreCase)
            ? InterfaceMode.Classic
            : InterfaceMode.Plain;

        o.Settings.Save(call, o.Settings.Load(call) with { InterfaceMode = mode });
        return SettingsPage(o, prefix, call, saved: true);
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
        <nav><a href="{{U(prefix, "/")}}">Inbox</a> · <a href="{{U(prefix, "/bulletins")}}">Bulletins</a> · <a href="{{U(prefix, "/compose")}}">Compose</a> · <a href="{{U(prefix, "/settings")}}">Settings</a>
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
        .err{color:#a32014}.saved{color:#1a6b2f}.pager{margin-top:.75rem}
        fieldset{border:1px solid #ddd8cf;margin:0 0 1rem;padding:.5rem 1rem}legend{color:#8a857c}
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
