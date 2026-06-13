using Bbs.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bbs.Host;

/// <summary>
/// The host configuration, loaded from <c>$PDN_APP_STATE/bbs.yaml</c> (design.md "YAML
/// config"). A commented default file is written on first run. RHP endpoint defaults come
/// from the supervisor environment (<c>PDN_RHP_HOST</c>/<c>PDN_RHP_PORT</c> — see
/// packet.net docs/app-packages.md) with a 127.0.0.1:9000 fallback.
/// </summary>
public sealed record BbsHostConfig
{
    /// <summary>The callsign placeholder a fresh default config carries until the owner edits it.</summary>
    public const string PlaceholderCallsign = "N0CALL";

    /// <summary>BBS callsign (+ optional SSID) — the RHP bind identity.</summary>
    public string Callsign { get; init; } = PlaceholderCallsign;

    /// <summary>Sysop callsign (console sysop rights; webmail sysop view).</summary>
    public string Sysop { get; init; } = "";

    /// <summary>
    /// Our hierarchical route <b>without</b> the callsign (the linmail.cfg H-Route shape,
    /// e.g. <c>#23.GBR.EURO</c>) — feeds <see cref="RoutingEngine"/> and the R: line.
    /// </summary>
    public string HRoute { get; init; } = "";

    /// <summary>Webmail bind (loopback per the app-gateway contract).</summary>
    public WebConfig Web { get; init; } = new();

    /// <summary>The optional IMAP server (default off) — lets iPhone Mail / any IMAP client read packet mail.</summary>
    public ImapConfig Imap { get; init; } = new();

    /// <summary>The optional SMTP submission server (default off) — lets iPhone Mail / any mail client send packet mail.</summary>
    public SmtpConfig Smtp { get; init; } = new();

    /// <summary>The node's RHPv2 endpoint.</summary>
    public RhpConfig Rhp { get; init; } = new();

    /// <summary>Forwarding partners — the source of truth for partner config (v1); upserted into the store at startup.</summary>
    /// <remarks>Concrete <see cref="List{T}"/> because YamlDotNet binds collections, not read-only interfaces.</remarks>
    public List<PartnerConfig> Partners { get; init; } = [];

    /// <summary>
    /// How long the inbound demux waits for a first line before assuming a human caller
    /// (design decision 1: a SID-shaped first line selects the Fbb answerer).
    /// </summary>
    public int DemuxFirstLineWaitSeconds { get; init; } = 30;
}

/// <summary>Webmail bind configuration. MUST stay loopback (app-gateway contract).</summary>
public sealed record WebConfig
{
    /// <summary>Bind address; the gateway identity headers are only trustworthy on loopback.</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>Port; must match the <c>ui.upstream</c> in pdn-app.yaml.</summary>
    public int Port { get; init; } = 18090;
}

/// <summary>
/// The IMAP4rev1 read-mostly server (default off). When <see cref="Enabled"/>, a TCP listener
/// on <see cref="Bind"/>:<see cref="Port"/> lets an external mail client (iPhone Mail, Thunderbird,
/// MailKit) read packet mail; the login is the user's callsign + their BBS mail-password
/// (<see cref="BbsStore.VerifyMailPassword"/>, set in webmail). Off by default ⇒ a node that does
/// not configure it behaves exactly as before. The bind may be a LAN address (unlike webmail, whose
/// loopback bind is the app-gateway trust boundary) — pair a LAN bind with <see cref="ImapTlsConfig"/>.
/// </summary>
public sealed record ImapConfig
{
    /// <summary>Whether the IMAP listener is started at all (default false — the whole feature is opt-in).</summary>
    public bool Enabled { get; init; }

    /// <summary>Bind address. Loopback by default; a LAN address exposes the server to the local network (use TLS).</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>
    /// TCP port. Defaults to 1143 (the IANA unprivileged IMAP test port) so a non-root app needs no
    /// capability to bind it; set 143 (plaintext) or 993 (implicit TLS) when run with the privilege to.
    /// </summary>
    public int Port { get; init; } = 1143;

    /// <summary>Implicit-TLS settings (default off ⇒ plaintext). When on, every accepted socket is wrapped in TLS.</summary>
    public ImapTlsConfig Tls { get; init; } = new();
}

/// <summary>
/// Implicit-TLS configuration for the IMAP listener (RFC 8314: TLS from the first byte, the iPhone
/// "SSL" model on port 993). When <see cref="Enabled"/> the server wraps each accepted socket in an
/// <see cref="System.Net.Security.SslStream"/> with server authentication; the certificate is the
/// operator-supplied PKCS#12 at <see cref="CertificatePath"/>, or — when none is given and
/// <see cref="GenerateSelfSigned"/> — a self-signed certificate generated on first start and persisted
/// under the state directory (browsers/clients warn until it is trusted, but the channel is encrypted).
/// </summary>
public sealed record ImapTlsConfig
{
    /// <summary>
    /// Whether accepted sockets are wrapped in implicit TLS. <b>Default true</b> — if you expose IMAP
    /// you want it encrypted (the credential and mail cross the network); with no
    /// <see cref="CertificatePath"/> a self-signed cert is generated (<see cref="GenerateSelfSigned"/>).
    /// Set false only for a deliberately-plaintext deployment (e.g. loopback-only, behind a TLS proxy).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Path to an operator-supplied PKCS#12 (.pfx) certificate; when set it wins over self-signed.</summary>
    public string? CertificatePath { get; init; }

    /// <summary>Password for <see cref="CertificatePath"/>, or null for an unencrypted PKCS#12.</summary>
    public string? CertificatePassword { get; init; }

    /// <summary>
    /// When no <see cref="CertificatePath"/> is supplied, generate (and persist) a self-signed
    /// certificate on first start (default true). Set false to require an operator-supplied cert —
    /// TLS then does not start unless one is configured.
    /// </summary>
    public bool GenerateSelfSigned { get; init; } = true;
}

/// <summary>
/// The SMTP submission server (RFC 6409, default off). When <see cref="Enabled"/>, a TCP listener on
/// <see cref="Bind"/>:<see cref="Port"/> lets an external mail client (iPhone Mail, Thunderbird, MailKit)
/// SEND packet mail; the login is the user's callsign + their BBS mail-password
/// (<see cref="BbsStore.VerifyMailPassword"/>, set in webmail). This is a submission server, not a relay:
/// AUTH is required before any mail command, and a sent message is stored as a Personal and routed
/// exactly like a webmail compose (the stored From is always the authenticated callsign). Off by default
/// ⇒ a node that does not configure it behaves exactly as before. The bind may be a LAN address (unlike
/// webmail, whose loopback bind is the app-gateway trust boundary) — pair a LAN bind with <see cref="SmtpTlsConfig"/>.
/// </summary>
public sealed record SmtpConfig
{
    /// <summary>Whether the SMTP listener is started at all (default false — the whole feature is opt-in).</summary>
    public bool Enabled { get; init; }

    /// <summary>Bind address. Loopback by default; a LAN address exposes the server to the local network (use TLS).</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>
    /// TCP port. Defaults to 465 — the IANA implicit-TLS submission port (the iPhone "SSL" model). Binding
    /// a privileged port (&lt; 1024) needs the privilege to; set a high port for an unprivileged deployment.
    /// </summary>
    public int Port { get; init; } = 465;

    /// <summary>
    /// The STARTTLS submission port; <b>0 disables the STARTTLS listener</b>. Defaults to 587 — the IANA
    /// submission port and the one iPhone Mail's default "Add Mail Account" flow auto-probes for outgoing
    /// (STARTTLS, no port field). The STARTTLS listener starts plaintext and the client upgrades to TLS in
    /// band; it offers AUTH only AFTER the upgrade and shares the same certificate as the implicit-TLS path
    /// (<see cref="Tls"/>). Binding a privileged port (&lt; 1024) needs the privilege to.
    /// </summary>
    public int StartTlsPort { get; init; } = 587;

    /// <summary>
    /// Implicit-TLS settings (default on). When on, every accepted socket on <see cref="Port"/> is wrapped
    /// in TLS. The same certificate is also used for the STARTTLS upgrade on <see cref="StartTlsPort"/>.
    /// </summary>
    public SmtpTlsConfig Tls { get; init; } = new();
}

/// <summary>
/// Implicit-TLS configuration for the SMTP submission listener (RFC 8314: TLS from the first byte, the
/// iPhone "SSL" model on port 465). When <see cref="Enabled"/> the server wraps each accepted socket in
/// an <see cref="System.Net.Security.SslStream"/> with server authentication; the certificate is the
/// operator-supplied PKCS#12 at <see cref="CertificatePath"/>, or — when none is given and
/// <see cref="GenerateSelfSigned"/> — a self-signed certificate generated on first start and persisted
/// under the state directory (browsers/clients warn until it is trusted, but the channel is encrypted).
/// </summary>
public sealed record SmtpTlsConfig
{
    /// <summary>
    /// Whether accepted sockets are wrapped in implicit TLS. <b>Default true</b> — if you expose SMTP
    /// you want it encrypted (the credential and mail cross the network); with no
    /// <see cref="CertificatePath"/> a self-signed cert is generated (<see cref="GenerateSelfSigned"/>).
    /// Set false only for a deliberately-plaintext deployment (e.g. loopback-only, behind a TLS proxy).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Path to an operator-supplied PKCS#12 (.pfx) certificate; when set it wins over self-signed.</summary>
    public string? CertificatePath { get; init; }

    /// <summary>Password for <see cref="CertificatePath"/>, or null for an unencrypted PKCS#12.</summary>
    public string? CertificatePassword { get; init; }

    /// <summary>
    /// When no <see cref="CertificatePath"/> is supplied, generate (and persist) a self-signed
    /// certificate on first start (default true). Set false to require an operator-supplied cert —
    /// TLS then does not start unless one is configured.
    /// </summary>
    public bool GenerateSelfSigned { get; init; } = true;
}

/// <summary>RHPv2 endpoint + credentials. Null host/port defer to the supervisor environment.</summary>
public sealed record RhpConfig
{
    /// <summary>RHP host; null → <c>PDN_RHP_HOST</c> → 127.0.0.1.</summary>
    public string? Host { get; init; }

    /// <summary>RHP port; null → <c>PDN_RHP_PORT</c> → 9000.</summary>
    public int? Port { get; init; }

    /// <summary>RHP auth user (only when the node sets <c>rhp.requireAuth</c>).</summary>
    public string? User { get; init; }

    /// <summary>RHP auth password.</summary>
    public string? Pass { get; init; }
}

/// <summary>
/// One forwarding partner (compat spec §4.1, the v1 subset). Maps onto
/// <see cref="Partner"/> for the store.
/// </summary>
public sealed record PartnerConfig
{
    /// <summary>Partner callsign, exact incl. SSID (inbound match is by exact source call — compat spec §2.5).</summary>
    public string Call { get; init; } = "";

    /// <summary>
    /// Connect target for outbound cycles, simple form — the callsign/alias the RHP
    /// <c>open</c> dials (equivalent to a one-line script <c>C &lt;call&gt;</c>). Defaults
    /// to the partner call. For node navigation use <see cref="ConnectScript"/> instead.
    /// </summary>
    public string? Connect { get; init; }

    /// <summary>
    /// Full connect script (compat spec §4.4): an optional first <c>C [port] &lt;target&gt;</c>
    /// names the RHP open; later lines are sent verbatim once connected (e.g. <c>BBS</c> at a
    /// node prompt), each followed by a response wait; <c>PAUSE n</c> delays. When set, it
    /// takes precedence over <see cref="Connect"/>.
    /// </summary>
    /// <remarks>Concrete <see cref="List{T}"/> because YamlDotNet binds collections, not read-only interfaces.</remarks>
    public List<string> ConnectScript { get; init; } = [];

    /// <summary>
    /// Connect handshake timeout in seconds (compat spec §4.1 ConTimeout, default 60) —
    /// bounds each connect-script response wait, including the post-script SID wait.
    /// </summary>
    public int ConTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Retry cadence for QUEUED mail, minutes (compat spec §4.1 FwdInterval; default 60).
    /// The scheduler never dials an empty queue — this is a retry timer, not a poll.
    /// </summary>
    public int IntervalMinutes { get; init; } = 60;

    /// <summary>
    /// Dial as soon as a message is queued (compat spec §4.1 FWDNewImmediately).
    /// Default TRUE — the delivery posture is immediate delivery + in-session reverse
    /// (every FBB session drains both directions, spec §3.11), with timers as safety
    /// nets; BPQ's default-off + interval-polling model is the wart, not the target.
    /// </summary>
    public bool SendImmediately { get; init; } = true;

    /// <summary>TO-field distribution list (compat spec §4.1 TOCalls).</summary>
    public List<string> To { get; init; } = [];

    /// <summary>AT-field list; entries with <c>*</c> are the wildcard default route (compat spec §4.1 ATCalls).</summary>
    public List<string> At { get; init; } = [];

    /// <summary>
    /// Hierarchical routes (compat spec §4.1). v1 maps one list onto both HRoutes (flood)
    /// and HRoutesP (personals + directed bulls); flood matching additionally needs
    /// <see cref="BbsHa"/>.
    /// </summary>
    public List<string> Hr { get; init; } = [];

    /// <summary>The partner's own full HA — enables flood-bulletin HR matching (compat spec §4.1 BBSHA).</summary>
    public string? BbsHa { get; init; }

    /// <summary>Largest message accepted from this partner, bytes (bigger inbound → FS '-').</summary>
    public int MaxRx { get; init; } = 99999;

    /// <summary>Largest message proposed to this partner, bytes.</summary>
    public int MaxTx { get; init; } = 99999;

    /// <summary>Auto-dialling enabled (messages still queue when disabled).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Opt in to B2F (Winlink/FBB B2) with this partner — default FALSE ⇒ B1-only, today's
    /// behaviour unchanged (compat spec §3.2/§8). When true we advertise '2' in our SID to this
    /// partner and, if its SID also advertises B2, propose/accept FC (B2 objects) instead of
    /// FA/B1. B1 stays the lingua franca between BBSes; flip this only for a partner that wants B2.
    /// </summary>
    public bool AllowB2 { get; init; }

    /// <summary>Maps to the store's partner record (compat spec §4.1 keys).</summary>
    public Partner ToPartner() => new()
    {
        Call = Call,
        Enabled = Enabled,
        AllowB2F = AllowB2,
        ForwardIntervalSeconds = Math.Max(1, IntervalMinutes) * 60,
        ForwardNewImmediately = SendImmediately,

        // The full script wins over the simple form (documented in DefaultYaml).
        ConnectScript = ConnectScript.Count > 0
            ? [.. ConnectScript]
            : string.IsNullOrWhiteSpace(Connect) ? [] : [$"C {Connect.Trim()}"],
        ConTimeoutSeconds = Math.Max(1, ConTimeoutSeconds),
        ToCalls = [.. To],
        AtCalls = [.. At],
        HRoutes = [.. Hr],
        HRoutesP = [.. Hr],
        BbsHa = BbsHa,
        MaxRxSize = MaxRx,
        MaxTxSize = MaxTx,
    };
}

/// <summary>Loads <c>bbs.yaml</c> from the state dir, creating a commented default on first run.</summary>
public static class BbsHostConfigFile
{
    /// <summary>The config file name inside <c>$PDN_APP_STATE</c>.</summary>
    public const string FileName = "bbs.yaml";

    /// <summary>
    /// Loads the config, writing <see cref="DefaultYaml"/> first when the file does not
    /// exist. <paramref name="getEnv"/> supplies <c>PDN_RHP_HOST</c>/<c>PDN_RHP_PORT</c>
    /// defaults for an unset RHP endpoint.
    /// </summary>
    public static (BbsHostConfig Config, bool CreatedDefault) LoadOrCreate(string stateDir, Func<string, string?> getEnv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        ArgumentNullException.ThrowIfNull(getEnv);

        string path = Path.Combine(stateDir, FileName);
        bool created = false;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(path, DefaultYaml);
            created = true;
        }

        BbsHostConfig config = Parse(File.ReadAllText(path));
        return (ApplyEnvironment(config, getEnv), created);
    }

    /// <summary>Parses a YAML config document (camelCase keys; unknown keys ignored).</summary>
    public static BbsHostConfig Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<BbsHostConfig>(yaml) ?? new BbsHostConfig();
    }

    /// <summary>Resolves the RHP endpoint: explicit config → supervisor env → 127.0.0.1:9000.</summary>
    public static BbsHostConfig ApplyEnvironment(BbsHostConfig config, Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(getEnv);

        string host = config.Rhp.Host is { Length: > 0 } h ? h
            : getEnv("PDN_RHP_HOST") is { Length: > 0 } envHost ? envHost
            : "127.0.0.1";
        int port = config.Rhp.Port
            ?? (int.TryParse(getEnv("PDN_RHP_PORT"), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int envPort)
                ? envPort
                : 9000);
        return config with { Rhp = config.Rhp with { Host = host, Port = port } };
    }

    /// <summary>The commented default written on first run.</summary>
    public const string DefaultYaml = """
        # pdn-bbs configuration — created on first run; edit and restart the app.
        #
        # callsign: the BBS callsign (+ optional SSID). This is the callsign the BBS
        #           binds over RHPv2 — users and partner BBSes connect to it.
        callsign: N0CALL

        # sysop: the sysop's callsign (sysop rights on the console; sysop view in webmail).
        sysop: ""

        # hRoute: this BBS's hierarchical route WITHOUT the callsign, e.g. "#23.GBR.EURO"
        #         (the linmail.cfg H-Route shape). Drives routing and the R: trace line.
        hRoute: ""

        # web: the webmail bind. MUST stay loopback — pdn's app gateway is the trust
        #      boundary for the X-Pdn-* identity headers. The port must match the
        #      ui.upstream in pdn-app.yaml.
        web:
          bind: 127.0.0.1
          port: 18090

        # imap: an optional read-mostly IMAP4rev1 server so iPhone Mail (or any IMAP
        #       client) can read packet mail. DEFAULT OFF — leave it off and the node
        #       behaves exactly as before. The login is the user's CALLSIGN plus their BBS
        #       mail-password (set in webmail); a callsign with no mail-password set cannot
        #       log in. Unlike webmail (loopback-only, the app-gateway trust boundary), the
        #       IMAP bind MAY be a LAN address so a phone on the home network can reach it —
        #       pair a LAN bind with tls.enabled.
        #   enabled: start the IMAP listener at all (default false)
        #   bind:    bind address (default 127.0.0.1; set 0.0.0.0 or a LAN IP for phones)
        #   port:    TCP port (default 1143 — an unprivileged port; use 143 plaintext or 993
        #            implicit-TLS when the app has the privilege to bind them)
        #   tls:     implicit TLS (RFC 8314 — TLS from the first byte, the iPhone "SSL" model)
        #     enabled:             wrap every connection in TLS (DEFAULT TRUE — if you expose IMAP
        #                          you want it encrypted; set false only for a deliberately-plaintext
        #                          deployment, e.g. loopback-only or behind a TLS proxy)
        #     certificatePath:     operator-supplied PKCS#12 (.pfx); wins over self-signed. Point this
        #                          at a real cert (e.g. your node's LE cert) for a no-warning client
        #     certificatePassword: password for that .pfx (null if unencrypted)
        #     generateSelfSigned:  when no certificatePath, generate + persist a self-signed
        #                          cert on first start (default true; clients warn until it
        #                          is trusted, but the channel is encrypted)
        imap:
          enabled: false
          bind: 127.0.0.1
          port: 1143
          tls:
            enabled: true
            certificatePath: null
            certificatePassword: null
            generateSelfSigned: true

        # smtp: an optional SMTP SUBMISSION server (RFC 6409) so iPhone Mail (or any
        #       mail client) can SEND packet mail. DEFAULT OFF — leave it off and the node
        #       behaves exactly as before. The login is the user's CALLSIGN plus their BBS
        #       mail-password (the same credential as imap; set in webmail). This is a
        #       SUBMISSION server, not a relay: AUTH is required before any mail command
        #       (no open relay), and a sent message is stored as a Personal and routed just
        #       like a webmail compose — the stored From is always the authenticated
        #       callsign, never the (untrusted) MAIL FROM. v1 is TEXT-ONLY: only the text
        #       body is taken (attachments / 7plus-on-send are a later slice). Like imap,
        #       the bind MAY be a LAN address so a phone on the home network can reach it —
        #       pair a LAN bind with tls.enabled.
        #   The server offers TWO submission endpoints from one listener config, both using the
        #   same certificate (tls below):
        #     - IMPLICIT TLS on `port` (465): TLS from the first byte (the iPhone "SSL" model).
        #     - STARTTLS on `startTlsPort` (587): starts plaintext, the client upgrades in band.
        #       587/STARTTLS is the endpoint iPhone Mail's default "Add Mail Account" flow auto-
        #       probes for OUTGOING (it has no port field), so offering it lets that default flow
        #       succeed unaided. STARTTLS never offers AUTH before the TLS upgrade (RFC 3207).
        #   enabled: start the SMTP listener at all (default false)
        #   bind:    bind address (default 127.0.0.1; set 0.0.0.0 or a LAN IP for phones)
        #   port:    implicit-TLS TCP port (default 465 — the standard implicit-TLS submission
        #            port; a port < 1024 needs the privilege to bind it)
        #   startTlsPort: STARTTLS TCP port (default 587 — the iOS-default outgoing port; set 0
        #            to DISABLE the STARTTLS listener. Uses the same cert as the implicit port)
        #   tls:     TLS settings — drives the implicit-TLS port AND the STARTTLS upgrade
        #     enabled:             wrap every connection on `port` in implicit TLS (DEFAULT TRUE —
        #                          if you expose SMTP you want it encrypted; set false only for a
        #                          deliberately-plaintext deployment, e.g. loopback-only or behind a
        #                          TLS proxy. The STARTTLS port still upgrades to TLS regardless)
        #     certificatePath:     operator-supplied PKCS#12 (.pfx); wins over self-signed. Point this
        #                          at a real cert (e.g. your node's LE cert) for a no-warning client
        #     certificatePassword: password for that .pfx (null if unencrypted)
        #     generateSelfSigned:  when no certificatePath, generate + persist a self-signed
        #                          cert on first start (default true; clients warn until it
        #                          is trusted, but the channel is encrypted)
        smtp:
          enabled: false
          bind: 127.0.0.1
          port: 465
          startTlsPort: 587
          tls:
            enabled: true
            certificatePath: null
            certificatePassword: null
            generateSelfSigned: true

        # rhp: the node's RHPv2 endpoint. When omitted (or null) the supervisor
        #      environment (PDN_RHP_HOST / PDN_RHP_PORT) is used, falling back to
        #      127.0.0.1:9000. user/pass only matter when the node sets rhp.requireAuth.
        rhp:
          host: null
          port: null
          user: null
          pass: null

        # partners: BBS forwarding partners (compat spec §4.1, v1 subset).
        #
        # Delivery posture: mail moves because whoever HOLDS it dials at once
        # (sendImmediately, the default), and every session drains BOTH directions
        # (FBB in-session reverse — when our proposals are done the partner gets the
        # turn, and vice versa; oracle-proven both ways). intervalMinutes is only the
        # RETRY cadence for mail that could not be delivered — the BBS never dials an
        # empty queue, so a quiet link stays quiet. No polling timers to tune.
        #   call:            partner callsign, exact incl. SSID (inbound match is exact)
        #   connect:         simple connect form — the callsign/alias outbound cycles
        #                    dial (default: call). Equivalent to a one-line script
        #                    "C <call>".
        #   connectScript:   full connect script (compat spec §4.4) for node navigation;
        #                    takes precedence over connect when both are set. An optional
        #                    FIRST "C [port] <target>" line names the dial; every later
        #                    line is sent verbatim once connected (e.g. BBS at a node
        #                    prompt), each followed by a response wait; "PAUSE n" waits n
        #                    seconds. Node failure text (BUSY/FAILURE/...) or a response
        #                    wait exceeding conTimeoutSeconds fails the cycle (retried
        #                    with backoff).
        #   conTimeoutSeconds: per-response-wait timeout for the script + the final SID
        #                    wait (compat spec §4.1 ConTimeout; default 60)
        #   intervalMinutes: retry cadence for queued-but-undelivered mail (default 60;
        #                    never dials an empty queue)
        #   sendImmediately: dial as soon as a message queues (default true)
        #   to:              TO-field distribution list, e.g. [SYSOP]
        #   at:              AT-field list; "*" entries are the wildcard default route
        #   hr:              hierarchical routes, e.g. [GBR.EURO] (flood matching also
        #                    needs bbsHa — the partner's own full HA)
        #   bbsHa:           partner's full HA, e.g. GB7BPQ.#23.GBR.EURO
        #   maxRx / maxTx:   per-partner inbound/outbound size caps in bytes (default 99999)
        #   enabled:         auto-dialling on/off (messages still queue when off)
        #   allowB2:         opt in to B2F (Winlink/FBB B2) with this partner (default false).
        #                    Off ⇒ B1 compressed forwarding, the lingua franca between BBSes.
        #                    On ⇒ we advertise '2' and, if the partner's SID also offers B2,
        #                    exchange FC (B2 objects) instead of FA/B1. Only flip it for a
        #                    partner that wants B2 (e.g. a Winlink RMS gateway).
        partners: []
        #  - call: GB7BPQ
        #    connect: GB7BPQ
        #    intervalMinutes: 60
        #    sendImmediately: true
        #    at: ["*"]
        #    hr: [GBR.EURO]
        #    bbsHa: GB7BPQ.#23.GBR.EURO
        #  - call: GB7RDG
        #    connectScript:        # dial the node, then enter its BBS application
        #      - C GB7RDG
        #      - BBS
        #    at: ["*"]

        # demuxFirstLineWaitSeconds: how long the inbound demux holds the
        # forwarding-vs-console decision for a SILENT caller. Every caller is greeted
        # immediately on connect (our [SID] line + the console greeting); a partner BBS
        # announces itself with a [SID] first line and is handed to the forwarding
        # answerer. Once the wait expires the session is the console's and later input
        # is treated as commands.
        demuxFirstLineWaitSeconds: 30
        """;
}
