# Forwarding — configuration, operations, and the de-warting of BPQ's model

**Status:** design 2026-06-11 (Tom's brief: "design and build something equally capable, except this is your opportunity to learn from its mistakes and remove its warts"). The compat spec ([`linbpq-mail-compat.md`](linbpq-mail-compat.md) §4) defines what BPQ's forwarding *does*; this document defines how ours is configured, operated, and observed. The wire protocols are unchanged — this is entirely about the management model around them.

## The capability-parity line

Everything BPQMail's forwarding configuration can express, ours must too. The inventory (spec §4.1–§4.4):

| BPQ capability | Ours | Status |
|---|---|---|
| Per-partner identity (call+SSID exact) | `partners[].call` | ✅ shipped |
| Connect script (node navigation) | `connect` (bare call) / `connectScript` (full §4.4 semantics) | 🔨 in flight |
| Forward interval + send-immediately | `intervalMinutes`, `sendImmediately` | ✅ shipped |
| Forward time windows (`FWDTimes`) | `times: ["02:00-06:00", …]` — human-readable ranges, TimeProvider-scheduled | F-1 |
| TO / AT (incl. implied-AT) / HR routing lists | `to`/`at`/`hr` + `bbsHa` | ✅ shipped |
| Single-copy best-HR-depth for P; bulletin flood | RoutingEngine | ✅ shipped (oracle-proven) |
| NTS routing by TO wildcards | T-type routing | F-3 (spec SHOULD) |
| Per-partner protocol options (B/B1/B2, MaxBlock) | `protocol:` block (b1 default; b2 later; maxBlock) | F-1 |
| Per-partner size caps (MaxRX/MaxTX) | `maxRx`/`maxTx` | ✅ shipped |
| Reverse polling (`RequestReverse`) | `requestReverse: true` — dial on interval even with an empty queue to collect | F-1 |
| `FWD <partner> NOW` | console `FWD` sysop verb + webmail button + the scheduler nudge | F-2 |
| `FWD QUEUE` inspection | console verb + webmail sysop view | F-2 |
| `REROUTEMSGS` | **automatic** re-route on config apply (see wart 8) + explicit requeue surface | F-2 |
| Per-recipient fan-out (BPQ copies per recipient) | currently first-recipient-only (named deferral) | F-1 |
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

## Configuration shape (the end state)

```yaml
partners:
  - call: GB7RDG-2            # exact, incl. SSID (inbound match + identity)
    connect: GB7RDG-2          # bare call = direct dial; or:
    # connectScript:           # full §4.4 semantics for node navigation
    #   - C GB7RDG
    #   - BBS
    enabled: true
    intervalMinutes: 30
    sendImmediately: true
    requestReverse: true       # poll even with an empty queue (collect our mail)
    times: []                  # empty = always; else ["02:00-06:00", ...]
    priority: null             # explicit tie-break; null = call order
    to: []                     # TO-distribution (e.g. [SYSOP])
    at: ["*"]                  # AT routes; "*" = wildcard default route
    hr: [GBR.EURO]             # hierarchical routes (flood needs bbsHa)
    bbsHa: GB7RDG.#23.GBR.EURO
    maxRx: 99999
    maxTx: 99999
    protocol:                  # F-1
      b2: false                # B2F opt-in when built
      maxBlock: 10000
```

## Operations surface (the end state)

- **Console (sysop):** `FWD` (status table: per-partner queue depth, health, last cycle), `FWD <partner>` (cycle now), `FWD QUEUE [partner]`, `ROUTE? <to> [@at]` (explain).
- **Webmail (sysop view):** the same as pages — partner health cards (with script transcripts and session stats), queue browser, BID browser, routing explain, requeue.
- **Logs:** one structured line per cycle outcome; loud after N consecutive failures; the re-route diff on config apply.

## Build waves

- **F-0 (in flight):** greet-first demux + full §4.4 connect scripts with per-step timeouts and attempt transcripts.
- **F-1 — parity + the home-BBS rule:** **local-delivery-beats-forwarding** (at-is-us / TO-is-local-user → zero targets; the wildcard-AT leak pinned by tests — see design.md § The home-BBS requirement) + auto-create users on inbound personals; time windows, reverse polling, per-recipient fan-out, the `protocol:` block (maxBlock enforcement; b2 stub), `priority`.
- **F-2 — the de-warting ops layer:** health tracking + session stats, auto-re-route-on-config-apply, console `FWD`/`ROUTE?` verbs, the webmail sysop pages.
- **F-3 — spec SHOULDs that touch forwarding:** NTS routing, WP consumption AND emission (announcing homed users to the network — promoted by the home-BBS requirement), B2F.

The GB7RDG partnership (the real network) starts on F-0: a direct dial to `GB7RDG-2` needs no scripts, and the shipped routing/loop-guard/BID machinery is already oracle-proven. F-1/F-2 land behind it while real traffic flows.
