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

    /// <summary>
    /// The default SSID for a callsign derived from the node (<c>PDN_NODE_CALLSIGN</c>): the BBS
    /// lives at <c>&lt;node-base&gt;-1</c> by default — the classic AX.25 mailbox SSID, and distinct
    /// from the sibling apps (DAPPS -7, bpqchat -4). The free-SSID probe (see
    /// <see cref="Callsigns.SsidProbeCandidates"/>) makes the exact default low-stakes: if -1 is
    /// already claimed on the node the link walks to the next free SSID and keeps it.
    /// </summary>
    public const int DerivedDefaultSsid = 1;

    /// <summary>The literal service alias the BBS additionally binds so users can <c>C BBS</c>.</summary>
    public const string DefaultServiceCallsign = "BBS";

    /// <summary>BBS callsign (+ optional SSID) — the RHP bind identity.</summary>
    public string Callsign { get; init; } = PlaceholderCallsign;

    /// <summary>
    /// A friendly service alias bound IN ADDITION to <see cref="Callsign"/> so users can reach the
    /// mailbox by an easy name (<c>C BBS</c>). Defaults to <see cref="DefaultServiceCallsign"/>;
    /// set empty to bind no alias. Inbound connects to either callsign route to the same session
    /// handler. pdn's RHP server allows binding an arbitrary callsign (it refuses only the node's
    /// own call + duplicates), and <c>BBS</c> is a valid AX.25 address.
    /// </summary>
    public string ServiceCallsign { get; init; } = DefaultServiceCallsign;

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
    /// TCP port. Defaults to 993, the standard implicit-TLS IMAP port (so iPhone Mail's port-less
    /// "Add Mail Account" flow auto-discovers it). 993 is privileged (&lt;1024); the pdn .deb's systemd
    /// unit grants the service <c>CAP_NET_BIND_SERVICE</c> so the non-root <c>packetnet</c> user can bind
    /// it. For a manual non-root run without that capability, set 1143 (the IANA unprivileged test port).
    /// </summary>
    public int Port { get; init; } = 993;

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
/// AUTH is required before any mail command, and a sent message is stored and routed exactly like a
/// webmail compose (the stored From is always the authenticated callsign). A recipient addressed to a
/// callsign is a personal; a recipient addressed to a token that is NOT a valid callsign (ALL, NEWS, …) is
/// a bulletin to that category. Attachments are 7plus-encoded into the body, the universal packet path.
/// Off by default ⇒ a node that does not configure it behaves exactly as before. The bind may be a LAN
/// address (unlike webmail, whose loopback bind is the app-gateway trust boundary) — pair a LAN bind with
/// <see cref="SmtpTlsConfig"/>.
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
    /// <summary>Partner node callsign (conventionally base, no SSID). Inbound match is on the BASE
    /// callsign (SSID-agnostic) — an inbound connect's source SSID is indeterminate, so it can't key the match.</summary>
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

    /// <summary>
    /// Reverse collection ("collect", compat spec §4.1 RequestReverse). Default FALSE — the
    /// scheduler never dials an empty queue, so a quiet link stays quiet and existing behaviour
    /// is unchanged. Set TRUE for a partner that holds mail FOR US but cannot dial us (an
    /// asymmetric link): the scheduler then dials it on the <see cref="IntervalMinutes"/> cadence
    /// even with nothing of ours to send, and the session's in-session reverse (spec §3.11)
    /// collects the partner's queue. In-session reverse on a session opened for our OWN mail
    /// needs no flag — it always happens; this only adds the empty-queue POLL.
    /// </summary>
    public bool Collect { get; init; }

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
        Collect = Collect,

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
    /// The supervisor environment variable pdn's <c>AppServiceSupervisor</c> sets to the app id
    /// (<c>bbs</c>) for every supervised app. Its presence is how the BBS detects it is running
    /// UNDER pdn (vs a standalone deployment) and writes the pdn-flavoured first-run defaults —
    /// IMAP + SMTP submission as plaintext on loopback, because pdn's tailnet sidecar owns the
    /// TLS edge (the <c>forward:</c> block in pdn-app.yaml) and the BBS must NOT do its own TLS
    /// or bind a public interface.
    /// </summary>
    public const string PdnAppIdEnv = "PDN_APP_ID";

    /// <summary>
    /// The supervisor environment variable pdn injects with the NODE's own callsign (e.g.
    /// <c>M9YYY</c> or <c>M9YYY-2</c>) into every supervised app. When the configured
    /// <see cref="BbsHostConfig.Callsign"/> is still the placeholder (or blank) and this is set,
    /// the BBS derives its on-air callsign as <c>&lt;node-base&gt;-&lt;ssid&gt;</c> (see
    /// <see cref="Callsigns.DeriveFromNode"/>) instead of binding <c>N0CALL</c> — matching the
    /// sibling apps (DAPPS, bpqchat, convers). An explicit, non-placeholder <c>callsign:</c> in
    /// <c>bbs.yaml</c> always wins (no derivation). Standalone (no <c>PDN_NODE_CALLSIGN</c>) keeps
    /// the placeholder.
    /// </summary>
    public const string PdnNodeCallsignEnv = "PDN_NODE_CALLSIGN";

    /// <summary>
    /// The fixed loopback IMAP port the BBS serves PLAINTEXT under pdn; pdn's sidecar
    /// TLS-terminates :993 on the tailnet and forwards here (the <c>forward:</c> target in
    /// pdn-app.yaml must match this).
    /// </summary>
    public const int PdnImapLoopbackPort = 11430;

    /// <summary>
    /// The fixed loopback SMTP-submission port the BBS serves PLAINTEXT under pdn; pdn's sidecar
    /// TLS-terminates :465 on the tailnet and forwards here (the <c>forward:</c> target in
    /// pdn-app.yaml must match this).
    /// </summary>
    public const int PdnSmtpLoopbackPort = 11465;

    /// <summary>
    /// Loads the config, writing the first-run default when the file does not exist —
    /// <see cref="PdnDefaultYaml"/> when running under pdn (<see cref="PdnAppIdEnv"/> present in
    /// <paramref name="getEnv"/>), otherwise the standalone <see cref="DefaultYaml"/>.
    /// <paramref name="getEnv"/> also supplies <c>PDN_RHP_HOST</c>/<c>PDN_RHP_PORT</c> defaults
    /// for an unset RHP endpoint.
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
            // Under pdn the tailnet sidecar terminates TLS and forwards plaintext to our
            // loopback listeners, so the pdn first-run default turns IMAP + SMTP ON as
            // plaintext-on-loopback. Standalone keeps the historical default (both off; their
            // own implicit-TLS on 993/465 when the owner opts in).
            bool underPdn = getEnv(PdnAppIdEnv) is { Length: > 0 };
            File.WriteAllText(path, underPdn ? PdnDefaultYaml : DefaultYaml);
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

    /// <summary>
    /// The resolved primary bind callsign and whether the RHP link should SSID-probe it.
    /// </summary>
    /// <param name="Callsign">The callsign to bind (normalised, incl. SSID).</param>
    /// <param name="Probe">
    /// True when the callsign was DERIVED from the node and should walk to the next free SSID if
    /// the node refuses the listen with errCode 9 (a real, non-placeholder configured callsign or
    /// a standalone placeholder never probes).
    /// </param>
    /// <param name="NodeCallsign">
    /// The node's own callsign (<c>PDN_NODE_CALLSIGN</c>) the probe skips the SSID of, or null.
    /// </param>
    public readonly record struct ResolvedCallsign(string Callsign, bool Probe, string? NodeCallsign);

    /// <summary>
    /// Resolves the primary bind callsign (brief change #1). Precedence:
    /// <list type="number">
    ///   <item>An explicit, non-placeholder <c>callsign:</c> wins verbatim — no derivation, no probe.</item>
    ///   <item>Otherwise, when the callsign is the placeholder (or blank) AND <c>PDN_NODE_CALLSIGN</c>
    ///         is set: derive <c>&lt;node-base&gt;-<see cref="BbsHostConfig.DerivedDefaultSsid"/>&gt;</c>
    ///         and mark it to PROBE for a free SSID on a duplicate-socket refusal.</item>
    ///   <item>Otherwise (standalone, no node env): keep the configured placeholder, no probe.</item>
    /// </list>
    /// </summary>
    public static ResolvedCallsign ResolveCallsign(BbsHostConfig config, Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(getEnv);

        string configured = Callsigns.Normalize(config.Callsign ?? "");
        bool isPlaceholder = configured.Length == 0
            || string.Equals(configured, BbsHostConfig.PlaceholderCallsign, StringComparison.OrdinalIgnoreCase);

        // An explicit, non-placeholder callsign always wins (no derivation, no probe).
        if (!isPlaceholder)
        {
            return new ResolvedCallsign(configured, Probe: false, NodeCallsign: null);
        }

        string? nodeCallsign = getEnv(PdnNodeCallsignEnv);
        string? derived = Callsigns.DeriveFromNode(nodeCallsign, BbsHostConfig.DerivedDefaultSsid);
        if (derived is null)
        {
            // Standalone (or an unusable node callsign): keep the placeholder, no probe.
            return new ResolvedCallsign(configured.Length == 0 ? BbsHostConfig.PlaceholderCallsign : configured,
                Probe: false, NodeCallsign: null);
        }

        // The primary identity is the FIRST probe candidate, not the raw <base>-<defaultSsid>: when
        // the default SSID collides with the node's own (the node runs at the default), the first
        // candidate is already the next free, non-node SSID — so the BBS never adopts the node's own
        // on-air callsign even before the duplicate-socket probe runs.
        string node = Callsigns.Normalize(nodeCallsign!);
        string primary = Callsigns.SsidProbeCandidates(derived, node)[0];
        return new ResolvedCallsign(primary, Probe: true, NodeCallsign: node);
    }

    /// <summary>The commented default written on first run.</summary>
    public const string DefaultYaml = """
        # pdn-bbs configuration — created on first run; edit and restart the app.
        #
        # callsign: the BBS callsign (+ optional SSID). This is the callsign the BBS
        #           binds over RHPv2 — users and partner BBSes connect to it.
        #           Left at N0CALL it is a PLACEHOLDER: when the BBS runs UNDER pdn (the
        #           supervisor sets PDN_NODE_CALLSIGN) it instead DERIVES <node-base>-1
        #           from the node's callsign and, if -1 is already claimed on the node,
        #           probes for the next free SSID (skipping 0 and the node's own SSID) —
        #           exactly like the sibling apps (DAPPS -7, bpqchat -4). Set a real,
        #           non-placeholder callsign here to PIN an identity: it then wins outright
        #           (no derivation, no probe). Standalone (no PDN_NODE_CALLSIGN) keeps N0CALL.
        callsign: N0CALL

        # serviceCallsign: a friendly alias bound IN ADDITION to callsign so users can
        #                  "C BBS" to reach the mailbox (default "BBS"; "" disables it).
        #                  Inbound connects to either callsign reach the same session.
        serviceCallsign: BBS

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
        #       NOTE: this is the STANDALONE default. When the BBS runs UNDER pdn (the
        #       supervisor sets PDN_APP_ID), the first-run bbs.yaml instead enables IMAP as
        #       PLAINTEXT on loopback (127.0.0.1:11430, tls.enabled false) — pdn's tailnet
        #       sidecar TLS-terminates :993 with the node's real Let's Encrypt cert and
        #       forwards to that loopback port (the forward: block in pdn-app.yaml). Under
        #       pdn you reach IMAPS over Tailscale at <node>-pdn.<tailnet>.ts.net:993 with a
        #       trusted cert — no certificate config in the BBS at all.
        #   enabled: start the IMAP listener at all (default false)
        #   bind:    bind address (default 127.0.0.1; set 0.0.0.0 or a LAN IP for phones)
        #   port:    TCP port (default 993 — the standard implicit-TLS IMAP port iPhone Mail
        #            auto-discovers; the pdn .deb grants CAP_NET_BIND_SERVICE to bind it. Use 1143
        #            for a manual non-root run without that capability, or 143 for plaintext.)
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
          port: 993
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
        #       (no open relay), and a sent message is stored and routed just like a webmail
        #       compose — the stored From is always the authenticated callsign, never the
        #       (untrusted) MAIL FROM. A recipient addressed to a callsign is stored as a
        #       PERSONAL; a recipient addressed to a token that is NOT a valid callsign
        #       (e.g. ALL, NEWS, SALE, DX) is treated as a BULLETIN to that category. Any
        #       attachments are 7plus-encoded into the message body (the universal packet
        #       path, exactly like a webmail compose) so a phone that attaches a photo
        #       produces a message a recipient can decode back to a file. Like imap, the
        #       bind MAY be a LAN address so a phone on the home network can reach it —
        #       pair a LAN bind with tls.enabled.
        #       NOTE: this is the STANDALONE default. When the BBS runs UNDER pdn (the
        #       supervisor sets PDN_APP_ID), the first-run bbs.yaml instead enables SMTP
        #       submission as PLAINTEXT on loopback (127.0.0.1:11465, tls.enabled false,
        #       startTlsPort 0) — pdn's tailnet sidecar TLS-terminates :465 with the node's
        #       real Let's Encrypt cert and forwards to that loopback port (the forward:
        #       block in pdn-app.yaml). Under pdn you reach SMTPS over Tailscale at
        #       <node>-pdn.<tailnet>.ts.net:465 with a trusted cert — no cert config needed.
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
        # EXCEPTION — collect (below): for a partner that holds mail FOR US but cannot
        # dial us (an asymmetric link), set collect:true to POLL it on the
        # intervalMinutes cadence even with an empty queue; the session's in-session
        # reverse then collects whatever it holds. Default off.
        #   call:            partner node callsign (base, no SSID; inbound match is SSID-agnostic)
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
        #                    never dials an empty queue — unless collect is on, when it
        #                    is ALSO the reverse-collection poll cadence)
        #   sendImmediately: dial as soon as a message queues (default true)
        #   collect:         reverse collection (default false). On ⇒ dial this partner
        #                    on the intervalMinutes cadence EVEN WITH AN EMPTY QUEUE, to
        #                    pick up mail it holds for us via the in-session reverse —
        #                    only for partners that cannot dial us (an asymmetric link).
        #                    In-session reverse on a session we open for OUR OWN queued
        #                    mail needs no flag; it always happens. Off ⇒ a quiet link
        #                    stays quiet (existing behaviour).
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
        #  - call: GB7CIP          # an asymmetric partner that never dials us:
        #    connect: GB7CIP       # POLL it to collect the mail it holds for us
        #    collect: true         # dial on intervalMinutes even with an empty queue
        #    intervalMinutes: 30

        # demuxFirstLineWaitSeconds: how long the inbound demux holds the
        # forwarding-vs-console decision for a SILENT caller. Every caller is greeted
        # immediately on connect (our [SID] line + the console greeting); a partner BBS
        # announces itself with a [SID] first line and is handed to the forwarding
        # answerer. Once the wait expires the session is the console's and later input
        # is treated as commands.
        demuxFirstLineWaitSeconds: 30
        """;

    /// <summary>
    /// The commented default written on first run WHEN RUNNING UNDER PDN (the supervisor sets
    /// <see cref="PdnAppIdEnv"/>). Identical to <see cref="DefaultYaml"/> except that IMAP and
    /// SMTP submission are turned ON as PLAINTEXT on loopback at the fixed ports
    /// <see cref="PdnImapLoopbackPort"/> / <see cref="PdnSmtpLoopbackPort"/>: pdn's tailnet
    /// sidecar owns the TLS edge (it TLS-terminates :993/:465 on the tailnet with the node's
    /// real Let's Encrypt cert and forwards plaintext here — the <c>forward:</c> block in
    /// pdn-app.yaml), so the BBS must NOT do its own TLS and must NOT bind a public interface.
    /// The owner can still edit this file (it is config, the source of truth); the only
    /// difference from standalone is the first-run posture.
    /// </summary>
    public const string PdnDefaultYaml = """
        # pdn-bbs configuration — created on first run UNDER PDN; edit and restart the app.
        #
        # This file was written with pdn's tailnet-forwarding defaults (PDN_APP_ID was set by
        # the supervisor). IMAP + SMTP submission are PLAINTEXT on loopback below: pdn's
        # embedded Tailscale node TLS-terminates :993 (IMAPS) and :465 (SMTPS) with the node's
        # real Let's Encrypt cert and forwards to these loopback ports (the forward: block in
        # this app's pdn-app.yaml). Reach mail over Tailscale at
        # <node>-pdn.<tailnet>.ts.net:993 / :465 with a TRUSTED cert — no certificate config
        # in the BBS at all. Do NOT enable the BBS's own TLS or bind a public interface here;
        # pdn owns the TLS edge and the trusted name.
        #
        # callsign: the BBS callsign (+ optional SSID). This is the callsign the BBS
        #           binds over RHPv2 — users and partner BBSes connect to it.
        #           Left at N0CALL it is a PLACEHOLDER: under pdn the supervisor sets
        #           PDN_NODE_CALLSIGN, so the BBS DERIVES <node-base>-1 from the node's
        #           callsign and, if -1 is already claimed, probes for the next free SSID
        #           (skipping 0 and the node's own SSID) — like the sibling apps (DAPPS -7,
        #           bpqchat -4). Set a real callsign here to PIN it (then no derivation/probe).
        callsign: N0CALL

        # serviceCallsign: a friendly alias bound IN ADDITION to callsign so users can
        #                  "C BBS" to reach the mailbox (default "BBS"; "" disables it).
        #                  Inbound connects to either callsign reach the same session.
        serviceCallsign: BBS

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

        # imap: the read-mostly IMAP4rev1 server so iPhone Mail (or any IMAP client) can read
        #       packet mail. UNDER PDN this is plaintext on loopback — pdn's sidecar adds TLS
        #       on the tailnet (:993) and forwards here. The login is the user's CALLSIGN plus
        #       their BBS mail-password (set in webmail). Keep bind loopback and tls.enabled
        #       false: the bytes from pdn's sidecar arrive over loopback already, and pdn's
        #       cert is the trusted one clients see. (For a STANDALONE, non-pdn deployment you
        #       would instead bind a LAN address and turn tls.enabled on with port 993.)
        imap:
          enabled: true
          bind: 127.0.0.1
          port: 11430        # pdn forwards tailnet :993 here (must match pdn-app.yaml forward.target)
          tls:
            enabled: false   # pdn's sidecar terminates TLS; the BBS stays plaintext on loopback
            certificatePath: null
            certificatePassword: null
            generateSelfSigned: false

        # smtp: the SMTP SUBMISSION server (RFC 6409) so iPhone Mail (or any mail client) can
        #       SEND packet mail. UNDER PDN this is plaintext on loopback — pdn's sidecar adds
        #       TLS on the tailnet (:465) and forwards here. AUTH (callsign + mail-password) is
        #       still required before any mail command; a sent message is stored + routed just
        #       like a webmail compose. startTlsPort is 0 (no STARTTLS listener): pdn forwards
        #       only the implicit-TLS :465 endpoint, so there is nothing for STARTTLS to add.
        #       (For a STANDALONE deployment you would bind a LAN address, turn tls.enabled on
        #       with port 465, and set startTlsPort 587 for the iOS-default outgoing probe.)
        smtp:
          enabled: true
          bind: 127.0.0.1
          port: 11465        # pdn forwards tailnet :465 here (must match pdn-app.yaml forward.target)
          startTlsPort: 0    # no STARTTLS endpoint under pdn — only implicit-TLS :465 is forwarded
          tls:
            enabled: false   # pdn's sidecar terminates TLS; the BBS stays plaintext on loopback
            certificatePath: null
            certificatePassword: null
            generateSelfSigned: false

        # rhp: the node's RHPv2 endpoint. When omitted (or null) the supervisor
        #      environment (PDN_RHP_HOST / PDN_RHP_PORT) is used, falling back to
        #      127.0.0.1:9000. user/pass only matter when the node sets rhp.requireAuth.
        rhp:
          host: null
          port: null
          user: null
          pass: null

        # partners: BBS forwarding partners (compat spec §4.1, v1 subset). See the standalone
        # default's comments for the full key reference; left empty here.
        partners: []

        # demuxFirstLineWaitSeconds: how long the inbound demux holds the
        # forwarding-vs-console decision for a SILENT caller (see standalone default).
        demuxFirstLineWaitSeconds: 30
        """;
}
