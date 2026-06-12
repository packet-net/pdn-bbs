# Forwarding — configuration, operations, and the de-warting of BPQ's model

**Status:** design 2026-06-11 (Tom's brief: "design and build something equally capable, except this is your opportunity to learn from its mistakes and remove its warts"). The compat spec ([`linbpq-mail-compat.md`](linbpq-mail-compat.md) §4) defines what BPQ's forwarding *does*; this document defines how ours is configured, operated, and observed. The wire protocols are unchanged — this is entirely about the management model around them.

## The capability-parity line

Everything BPQMail's forwarding configuration can express, ours must too. The inventory (spec §4.1–§4.4):

| BPQ capability | Ours | Status |
|---|---|---|
| Per-partner identity | `partners[].call` (call+SSID exact) | ✅ shipped — **stricter than BPQ**, which strips the SSID before its partner lookup (`BBSUtilities.c:10827`), so a BPQ partner may dial in under any SSID. Safe for the GB7RDG partnership because BPQ dials out with its stable APPLICATION call (`GB7RDG-2`); a base-call match option is an F-1 candidate if a partner ever floats SSIDs |
| Connect script (node navigation) | `connect` (bare call) / `connectScript` (full §4.4 semantics) | 🔨 in flight |
| Forward interval + send-immediately | `intervalMinutes`, `sendImmediately` | ✅ shipped |
| Forward time windows (`FWDTimes`) | `times: ["02:00-06:00", …]` — human-readable ranges, TimeProvider-scheduled | F-1 |
| TO / AT (incl. implied-AT) / HR routing lists | `to`/`at`/`hr` + `bbsHa` | ✅ shipped |
| Single-copy best-HR-depth for P; bulletin flood | RoutingEngine | ✅ shipped (oracle-proven) |
| **Local delivery beats forwarding** (the home-BBS rule) | RoutingEngine pre-empt for personals: AT-is-us (own call / under our HA) or no-AT + TO-is-a-local-user → **zero forward targets**; the wildcard-AT leak that a faithful single-copy port otherwise opens. Restores LinBPQ's own "already here" local-first behaviour [BPQ-SRC CheckAndSend] | ✅ shipped — pinned by tests (the leak test verified red on the pre-rule code), see design.md § The home-BBS requirement rule #1 |
| NTS routing by TO wildcards | T-type routing | F-3 (spec SHOULD) |
| Per-partner protocol options (B/B1/B2, MaxBlock) | per-partner `allowB2` (✅ shipped — see "B2F negotiation" below; default off ⇒ B1 unchanged); `maxBlock` still F-1 | ✅ B2 shipped / maxBlock F-1 |
| Per-partner size caps (MaxRX/MaxTX) | `maxRx`/`maxTx` | ✅ shipped |
| In-session reverse (`DoReverse`, default on) | inherent — every session drains BOTH directions (FbbSession TakeTurn; oracle-proven both roles vs stock `RequestReverse=0` BPQ, `BidirectionalForwardingInteropTests`) | ✅ shipped |
| Reverse polling (`RequestReverse`/`RevFWDInterval`) | `collect:` — dial on a timer even with an empty queue. Opt-in safety net ONLY for partners that cannot dial us (asymmetric links, e.g. GB7RDG→GB7CIP for the WW feed); on a symmetric link it is redundant chatter | F-1 (demoted) |
| `FWD <partner> NOW` | console `FWD` sysop verb + webmail button + the scheduler nudge | F-2 |
| `FWD QUEUE` inspection | console verb + webmail sysop view | F-2 |
| `REROUTEMSGS` | **automatic** re-route on config apply (see wart 8) + explicit requeue surface | F-2 |
| Per-recipient fan-out (BPQ copies per recipient) | B2: full multi-To/Cc + File: attachments stored, relayed & downloadable (✅ shipped — see "B2F negotiation"); per-*home* fan-out (split into per-`@home` copies) still deferred — the message forwards once on the primary route carrying the full To: list, FBB's next hop re-distributes | ✅ B2 multi-recipient/attachment / per-home split F-1 |
| Multi-partner topologies | any number of partners; deterministic tie-breaks | ✅ shipped |
| WP-driven completion | WP consume (spec SHOULD) | F-3 |

## The warts, and what replaces them

BPQ's forwarding *works* — the network runs on it — but operating it teaches you its failure modes the hard way. Each wart below names the BPQ behaviour, why it hurts, and our replacement.

**1. Config is opaque and ordering-magical.** linmail.cfg blobs rewritten by the web UI; list order silently decides ties; one-letter flags. → Ours is typed YAML with validation that *explains* (`bbs.yaml` rejects with "partner GB7RDG-2: hr entry 'GBR.EURO' needs bbsHa for flood matching" rather than silently never matching), and every implicit behaviour is documented next to the field in the generated default file.

**2. No dry-run, no "why".** You cannot ask BPQ where a message would route or why it didn't. → **Routing explain**: the RoutingEngine already returns `(partner, reason, depth)` — surface it as a console sysop verb (`ROUTE? <to> [@at]`) and a webmail tool: paste an address, see the decision trace ("P → GB7RDG-2: best HR depth 3 via GBR.EURO; GB7XYZ rejected: own call in R-chain"). Config lint warns when implied-AT is doing load-bearing work (wart 4's cousin).

**3. Failures are silent until you notice the queue.** A dead partner in BPQ just accumulates; nothing tells you. → **Per-partner health**: last successful cycle, last failure + its reason, consecutive-failure count, next retry time — in the webmail sysop view, the console `FWD QUEUE` output, and structured logs. A partner failing for >N cycles gets a loud log (and, later, a node-UI surface via the pdn events feed).

**4. Connect scripts are write-only.** A BPQ script that stops working gives you nothing to debug. → The script runner keeps a **per-attempt transcript** (what we sent, what came back, where it timed out), retained for the last K attempts per partner, shown on the health surface. Each step has an explicit timeout; failure names the step.

**5. Forward times are cryptic strings.** → `times: ["02:00-06:00"]` ranges in local-or-UTC (explicit `tz:` field), validated, TimeProvider-driven, and the health surface shows "outside window — next eligible 02:00".

**6. Order-dependent tie-breaks.** BPQ's strict-`>` HR-depth comparison silently keeps the first-configured partner on ties. → Deterministic and *visible*: ties broken by explicit `priority:` when set, else partner-call order, and the explain trace says which rule won and why.

**7. The forward-status letters are a private language.** (`F`, `$`, `N`…) → Keep the letters on the wire-facing surfaces (compat), but the webmail sysop view shows real words and per-partner queue membership per message.

**8. Config changes don't take effect until you remember REROUTEMSGS.** → Re-route pending (unforwarded) messages **automatically on config apply** — the routing engine recomputes queues for everything not yet forwarded; the log summarises the diff ("12 messages re-queued: 8 moved GB7XYZ→GB7RDG-2, 4 newly routable"). An explicit requeue verb remains for sysop surgery.

**9. Session outcomes are invisible.** → Per-cycle session stats on the health surface: negotiated SID/protocol (B1F/B2F), proposals offered/accepted/deferred/rejected, bytes, compression ratio, duration. (The FbbSession actions already carry this — it's surfacing, not new machinery.)

**10. BID hygiene is unauditable.** → The BID store is browsable/searchable in the webmail sysop view (first-seen, direction, link to the live copy); dedup rejections are logged with the offering partner.

**11. Loop protection is receive-side only.** BPQ checks R-lines on receipt; a misconfigured pair can still ping-pong proposals. → We additionally guard at **route time** (never queue toward a partner whose call appears in the R-chain; never re-offer a BID back toward where it came from) — already shipped; the explain trace shows when these guards fire.

**12. The implicit-vs-explicit AT muddle.** A bare `@CALL` matching a partner's own call is magic BPQ users rely on. → Kept (compat), but explain names it ("matched: implied-AT — partner's own callsign"), and lint suggests the explicit form.

## Vocabulary: the jargon goes away (Tom, 2026-06-11)

The wire keeps speaking FBB (it must), and the terse RF console keeps its classic letters (clients pattern-match them) — but **our configuration, webmail, explain output, logs, and docs use plain language only**. The legacy terms appear exactly once in our surfaces: in the translation table below, for operators arriving from BPQ.

| Legacy | Ours | Meaning |
|---|---|---|
| AT / `@BBS` field | **mail for** | mail addressed to stations whose home BBS is X |
| implied-AT | *(no name — just behaviour)* | a partner naturally receives mail addressed to stations homed on it |
| HR / hierarchical routes | **regions** | the network's geographic tree (`gbr.euro`, `#42.gbr.euro`) |
| BBSHA / H-address | **network address** | where a BBS sits in that tree (`gb7rdg.#42.gbr.euro`) |
| TO-distribution (bulletins) | **topics** | bulletin categories (`SALE`, `ARRL`, …) |
| BID / MID | **network id** | the network-wide dedup identity of a message |
| FWDTimes | **window** | when dialling is allowed |
| RequestReverse | **collect** | dial on a timer just to pick up waiting mail — only for partners that cannot dial us (in-session pickup needs no config; it always happens) |
| flood vs directed bulletin | **broadcast** vs **routed** | sent to every matching partner vs the best single one |
| WP / White Pages | **the network directory** | who is homed where |
| R: lines | **routing trace** | the hop-by-hop history (R: on the wire, words on screen) |
| FWD QUEUE / REROUTEMSGS | **queue / re-route** | plain words on every surface |

Explain output speaks the same language: *"personal → GB7RDG-2: it carries mail for stations in region gbr.euro (closest match)"*, never "matched HR at depth 3". The F-1 config rename drops the legacy keys outright (pre-1.0, single deployment — no alias shims); the F-2 webmail/console sysop surfaces are built jargon-free from the start.

## Delivery posture (Tom, 2026-06-11): immediate + turnaround, never polling

Mail moves because **whoever holds it dials at once** (`immediately: true`, the default), and **every session drains both directions** (FBB in-session reverse is inherent to the block flow — when the caller's proposals are done the answerer gets the turn; proven both ways against stock BPQ with `RequestReverse=0`). Timers are safety nets, not the delivery mechanism: `every` is the retry cadence for queued-but-undelivered mail (the scheduler never dials an empty queue), and `collect` (timed empty-queue polling, BPQ's `RequestReverse`) exists only for asymmetric links where the partner cannot dial us. A quiet link stays quiet.

BPQ trap this design dodges (found live, `BidirectionalForwardingInteropTests`): BPQ SSID-strips inbound AX.25 connects before its partner lookup, so a partner record keyed with an SSID silently breaks the reverse-queue join — the dial-in lands on an auto-created plain user and reverse never proposes. BPQ-side partner records for us must be keyed by **base callsign** (production GB7RDG base-keys all 24 of its records).

## B2F negotiation and the `allowB2` gate (Tom, 2026-06-12)

B1 compressed forwarding is the lingua franca between BBSes and stays the default; B2F (Winlink/FBB "B2") buys multi-recipient + attachment messages and Winlink RMS interop, and a partner can be opted into it per-partner. It is wired through the existing FBB session and codec — a B2 object is just a different plaintext shipped through the *same* B1 LZHUF container + SOH/STX/EOT framing ("B2 uses B1 mode (crc on front of file)"), proposed with an `FC EM` line instead of `FA`.

**The gate — `allowB2`, default FALSE.** Each partner has an `allowB2` flag (maps to `Partner.AllowB2F`). With it unset — the default — nothing changes: we advertise the B1-only SID `[PDN-<ver>-B1FHM$]`, propose `FA`, and refuse any `FC` with `-`, exactly as before. An owner sets `allowB2: true` on a partner to turn it on.

**B2 is active for a session iff `allowB2` AND the partner's parsed SID advertises B2.** The SID intersection makes both directions consistent:

- **Outbound (we dial an `allowB2` partner):** our SID advertises B2 (`[PDN-<ver>-B12FHM$]`). If the partner's SID also offers B2 we propose every queued message as a uniform `FC EM` (MID = the message's network-id/BID, plus the uncompressed and compressed object sizes) and transfer each as a B2 object; if the partner turns out B1-only we fall back to `FA`/B1 with no loss.
- **Answering an inbound connect:** we advertise B2 in the greeting SID *only* when the caller matches a partner record we set `allowB2` on (keyed by call, as the reverse-forward queue join is). So we only ever accept `FC` from partners we enabled — a non-`allowB2` caller still sees the B1 SID and its `FC` (if it sends one anyway) is refused with `-` (the original guard, intact).

**Receiving a B2 object** runs through the *same* store/route path as a B1 inbound: the object decodes (`Mid → BID`, `From`, `To`, `Subject`, `Body`), is stored verbatim, and is subject to the identical dup-BID / size / R-chain loop-and-age holds and the home-BBS rules (local-delivery-beats-forwarding + auto-create-homed-user). The B2 `Body` we *send* is the same plaintext B1 would (R: chain + first-hop blank + body), so the receive-side loop/age guard is protocol-agnostic and B2 needs no special R-line handling.

**Scope — B2 completeness (multi-recipient + attachments) ✅ shipped (2026-06-12).** A received-or-relayed B2 carries its full envelope end-to-end: ALL `To:` are stored as To-recipients and ALL `Cc:` as Cc-recipients (the store's `recipients` table gained a `cc` flag), and ALL `File:` parts are stored byte-exact (a new `attachments` table) — `InboundMessageReceiver.DeliverFc` stores them; `OutboundBuilder.BuildB2Object` emits them so a relayed message carries its attachments + full recipient list onward; webmail renders the Cc list and offers per-attachment download links (`GET /messages/{n}/attachments/{name}`, recipient/sysop-gated, served from the DB BLOB by exact name — no filesystem path). The single-To/no-attachment wire is byte-identical to before (the GB7RDG↔pdn path is untouched). Schema migration is additive (`ALTER TABLE recipients ADD cc`; `CREATE TABLE attachments`) so the live lab db upgrades on open.

One **named deferral remains** from the B2-completeness slice (visible, not silent):
- **Per-*home* fan-out (F-1).** Recipients with a different `@home` than the primary are stored as recipients, but the message is forwarded ONCE on the primary's route carrying the full To: list (FBB's next hop re-distributes); we do not split into per-`@home` copies. This matches the single-`At` store model + the webmail compose.

**Send-side file slice ✅ shipped (2026-06-12) — closes the earlier "webmail compose upload" deferral.** Webmail compose now accepts a file upload and a per-send "how to send this file" choice (Tom's framing: *"the option to 7plus-encode an uploaded file they plan to send, or not"*). `ComposeForm` renders a file input + a `fileMode` radio (`7plus` default / `attachment`); the form posts `multipart/form-data` and `POST /compose` reads both the text fields (`form[...]`) and the upload (`form.Files`). The pdn app-gateway reverse-proxies the raw body to the loopback upstream with the Content-Type intact, so multipart flows through unmodified — there is no body rewrite or size cap in the gateway/app-package wiring (pdn carries zero BBS-specific code); we enforce our own upload ceiling (`WebmailOptions.MaxUploadBytes`, default 256 KiB) and reject oversize cleanly. Two send choices:
- **7plus-encode (universal).** `SevenPlusEncoder.Encode(bytes, filename)` → the parts are appended VERBATIM (their wire-faithful CRLF) to the user's body after a blank line; the message then sends as ordinary text over any path (B1 or B2). A pdn recipient's inbound `SevenPlusAssembler` reassembles it; a non-pdn recipient uses its own 7plus decoder. **Gotcha (caught by the byte-exact round-trip test):** the parts must NOT be run through the body's CR line discipline — `string.ReplaceLineEndings` rewrites byte 0x85 (a 7plus code-alphabet byte) as a Unicode line break (U+0085 NEL) and corrupts reassembly. Appending verbatim CRLF is both correct and round-trips (the scanner tolerates CRLF/CR/LF).
- **Binary attachment (B2 only).** Stored as a `MessageAttachment` on the draft → rides the attachments plumbing; `BuildB2Object` emits it as a B2 `File:` part. The UI notes binary attachments only reach B2-capable partners (7plus is the universal choice) — a hint, not a hard block.

Uploaded filenames are path-stripped (a `../`-shaped name resolves to a bare filename). The inbound `SevenPlusAssembler` is NOT run on compose-originated messages (it's inbound-only) — an outgoing 7plus message correctly stays as text in the sender's own Sent view. **Two named deferrals (this slice):** (1) splitting a large 7plus file into separate part-bulletins (one message per part) for size-capped broadcast paths — the MVP embeds all parts in one body (large bodies forward fine post the multi-frame-TX node fix); (2) pretty "Sent" rendering of an outgoing 7plus message as a file chip (it shows as composed). IMAP/SMTP send path is a separate roadmap item.

**Oracle finding (what a real LinBPQ does with a multi-To/Cc/File B2 — `B2ForwardingInteropTests`):** outbound, the live oracle accepts our FC and stores the object but NORMALISES the recipient envelope on receipt — it strips its OWN call from the `To:` list (the implied-AT "already here"), DROPS the `Cc:` line, and FAITHFULLY PRESERVES the `File:` attachment (header, name, exact bytes). Inbound, the oracle's telnet user-entry surface cannot ORIGINATE a genuine multi-To (space-separated → "Invalid Format"; comma → one literal addressee token) nor a File: attachment (no upload step), so the inbound multi-To/Cc/File DECODE is pinned at the host level; the live inbound FC receive + store is oracle-proven for the single-recipient object. We assert what the oracle actually does, not what our codec emits — surfaced, not forced.

## Configuration shape (the end state)

```yaml
partners:
  - call: GB7RDG-2             # exact, incl. SSID (who answers / who we dial)
    dial: GB7RDG-2             # bare call = direct; or steps: [C GB7RDG, BBS]
    enabled: true
    every: 30m                 # RETRY cadence for queued mail; an empty queue never dials
    immediately: true          # dial as soon as something queues (the default posture)
    collect: false             # timed empty-queue polling — only for partners that can't
                               # dial us; sessions drain both directions regardless
    window: []                 # when dialling is allowed; empty = always; ["02:00-06:00"]
    priority: null             # explicit tie-break; null = call order
    networkAddress: gb7rdg.#42.gbr.euro   # where this partner sits in the network tree
    sends:
      mailFor: [gb7rdg-2]      # mail addressed to stations homed on these BBSes
      regions: [gbr.euro]      # this partner carries mail toward these regions
      topics: []               # bulletin categories to pass on (e.g. [SALE, ARRL])
    limits:
      receive: 99999           # per-message size caps, bytes
      send: 99999
    allowB2: false             # opt in to B2F (Winlink/FBB B2); default off ⇒ B1. ✅ shipped
    protocol:                  # F-1
      maxBlock: 10000
```

## Operations surface (the end state)

- **Console (sysop):** `forwarding` (status: per-partner queue depth, health, last cycle), `forward <partner>` (cycle now), `queue [partner]`, `route <call>` (explain) — plain words per the vocabulary section; no FWD/REROUTE incantations. **Read-only diagnostics ✅ shipped (2026-06-12, feat/plain-sysop-ops-and-webmail-toggle):** `forwarding` (per-partner status: dialled/off, dial cadence, queue depth), `queue` (the pending forward queue in sentences), `route <call>`/`route <call>@<region>` (the explain trace through the live `RoutingEngine`, plain §77 phrasing). PLAIN-surface only (the classic surface stays byte-exact), sysop-gated. **Named deferrals (visible):** the `forward <partner> now` / re-route *action* verbs (they trigger real dials — a later slice with the scheduler nudge), and the live per-partner *health* (last cycle / failure + reason / next retry, wart 3) — the `ForwardingScheduler` keeps that in a private loop variable, so `forwarding` names it as deferred rather than faking it.
- **Webmail (sysop view):** the same as pages — partner health cards (with script transcripts and session stats), queue browser, BID browser, routing explain, requeue.
- **Logs:** one structured line per cycle outcome; loud after N consecutive failures; the re-route diff on config apply.

## Build waves

- **F-0 (in flight):** greet-first demux + full §4.4 connect scripts with per-step timeouts and attempt transcripts.
- **F-1 — parity + the home-BBS rule:** **local-delivery-beats-forwarding** (at-is-us / TO-is-local-user → zero targets; the wildcard-AT leak pinned by tests — see design.md § The home-BBS requirement) — **✅ landed** (feat/local-delivery-rule: the `RoutingEngine` personal pre-empt + `BbsStore.UserExists` + the leak/regression test suite) + auto-create users on inbound personals (still F-1); time windows, `collect` (timed empty-queue polling — opt-in safety net for partners that cannot dial us; in-session reverse already shipped), per-recipient fan-out, the `protocol:` block (maxBlock enforcement), `priority`. **B2F (per-partner `allowB2`, default off) — ✅ shipped** (single-recipient/no-attachment path; multi-recipient fan-out + attachments remain F-1, see "B2F negotiation").
- **F-2 — the de-warting ops layer:** health tracking + session stats, auto-re-route-on-config-apply, console `FWD`/`ROUTE?` verbs, the webmail sysop pages. **Partial ✅ (2026-06-12):** the plain *read-only* console diagnostics (`forwarding` status, `queue`, `route` explain) shipped, and the webmail interface-mode toggle (`GET`/`POST /settings`) shipped; still outstanding for F-2 — live per-partner health tracking + session stats, the forwarding *action* verbs (forward-now / re-route), auto-re-route-on-config-apply, and the fuller webmail sysop forwarding/queue/BID pages (wart 3/8/9/10).
- **F-3 — spec SHOULDs that touch forwarding:** NTS routing, WP consumption AND emission (announcing homed users to the network — promoted by the home-BBS requirement), B2F.

The GB7RDG partnership (the real network) starts on F-0: a direct dial to `GB7RDG-2` needs no scripts, and the shipped routing/loop-guard/BID machinery is already oracle-proven. F-1/F-2 land behind it while real traffic flows.
