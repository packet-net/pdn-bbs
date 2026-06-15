namespace Bbs.Core;

/// <summary>
/// Per-partner forwarding configuration (compat spec §4.1). A plain, YAML-shaped record the
/// Host binds config onto; persisted via <see cref="BbsStore.UpsertPartner"/>.
///
/// Named deferrals (config keys in compat spec §4.1 not modelled yet): FWDTimes time bands,
/// RevFWDInterval (a SEPARATE collect cadence — we reuse <see cref="ForwardIntervalSeconds"/>
/// for the collect poll instead), FWDPersonalsOnly, MaxFBBBlock, SendCTRLZ, and the
/// per-partner protocol gates beyond B2 (AllowBlocked/AllowCompressed/UseB1Protocol are
/// effectively constant for our B1F-minimum SID — compat spec §3.2/§8).
/// </summary>
public sealed record Partner
{
    /// <summary>
    /// Partner identity: the partner node's callsign. Conventionally the base callsign (no SSID).
    /// Inbound forwarding connects are matched on the <b>base</b> callsign (SSID-agnostic, via
    /// <see cref="BbsStore.FindPartnerByBaseCall"/>): the source SSID of an inbound connect is
    /// indeterminate — an outbound AX.25 connect grabs whatever SSID is free at the time — so it
    /// cannot be relied on to route to the BBS. (Supersedes the earlier "exact source callsign
    /// including SSID" reading, compat spec §2.5 [M0LTE-IT], which assumed a stable source SSID.)
    /// </summary>
    public required string Call { get; init; }

    /// <summary>
    /// Auto-dialling enabled. Gates the Host's forwarding scheduler only — messages still queue
    /// to a disabled partner, matching LinBPQ (routing/CheckAndSend never consults Enabled).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Poll interval in seconds (compat spec §4.1 FwdInterval, default 3600).</summary>
    public int ForwardIntervalSeconds { get; init; } = 3600;

    /// <summary>Dial as soon as a message is queued (compat spec §4.1 FWDNewImmediately).</summary>
    public bool ForwardNewImmediately { get; init; }

    /// <summary>
    /// Reverse collection ("collect", compat spec §4.1 RequestReverse). When set, the forwarding
    /// scheduler dials this partner on the <see cref="ForwardIntervalSeconds"/> cadence even when
    /// our outbound queue is EMPTY — a deliberate poll — so a partner that holds mail FOR US but
    /// cannot dial us (an asymmetric link) is still collected: every FBB session drains both
    /// directions (in-session reverse, spec §3.11), so the empty-outbound poll session still
    /// receives the partner's reverse-forward batch. <b>Default false</b> ⇒ the scheduler never
    /// dials an empty queue (a quiet link stays quiet); in-session reverse on a session we open
    /// for our own queued mail needs no flag — it always happens.
    /// </summary>
    public bool Collect { get; init; }

    /// <summary>
    /// Connect-script lines, replayed verbatim by the Host ("Script lines are sent verbatim to
    /// the node" — compat spec §4.4; <c>C &lt;target&gt;</c> semantics belong to the Host).
    /// </summary>
    public IReadOnlyList<string> ConnectScript { get; init; } = [];

    /// <summary>
    /// Connect handshake timeout, seconds (compat spec §4.1 ConTimeout, default 60). The
    /// Host applies it to each connect-script response wait — including the post-script
    /// SID wait (named deviation: per-wait rather than whole-handshake).
    /// </summary>
    public int ConTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// TO-field distribution list (compat spec §4.1 TOCalls). Exact match for P/B routing;
    /// NTS wildcards <c>123*</c>/<c>*</c> and <c>!</c>/<c>-</c> never-prefixes apply to T
    /// routing only, mirroring [BPQ-SRC CheckBBSToList vs CheckBBSToForNTS].
    /// </summary>
    public IReadOnlyList<string> ToCalls { get; init; } = [];

    /// <summary>
    /// AT-field list (compat spec §4.1 ATCalls): exact entries match the first element of the
    /// message AT; entries containing <c>*</c> are the last-resort wildcard route.
    /// </summary>
    public IReadOnlyList<string> AtCalls { get; init; } = [];

    /// <summary>Hierarchical routes for flood bulletins (compat spec §4.1 HRoutes).</summary>
    public IReadOnlyList<string> HRoutes { get; init; } = [];

    /// <summary>Hierarchical routes for personals + directed bulletins (compat spec §4.1 HRoutesP).</summary>
    public IReadOnlyList<string> HRoutesP { get; init; } = [];

    /// <summary>
    /// The partner's own full HA, used for the flood "in target area" test (compat spec §4.1
    /// BBSHA / §4.2). Null or empty disables flood HR matching for this partner ("Not safe to
    /// flood" [BPQ-SRC CheckBBSHElementsFlood]).
    /// </summary>
    public string? BbsHa { get; init; }

    /// <summary>Largest message accepted from this partner, bytes (compat spec §4.1; bigger inbound → FS '-').</summary>
    public int MaxRxSize { get; init; } = 99999;

    /// <summary>Largest message proposed to this partner, bytes (compat spec §4.1).</summary>
    public int MaxTxSize { get; init; } = 99999;

    /// <summary>B2F opt-in (advertise '2' to this partner when the Fbb layer supports it — compat spec §3.2/§8 SHOULD).</summary>
    public bool AllowB2F { get; init; }
}
