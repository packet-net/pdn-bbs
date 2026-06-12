# pdn-bbs — design

**What:** a ground-up packet-radio BBS for the pdn node platform: personal mail + bulletins + NTS-shaped traffic, the classic terse W0RLI/FBB-style RF command surface, **webmail**, and **full LinBPQ-mail forwarding compatibility** (FBB B1F compressed forwarding as the core; B2F as the follow-on). Companion spec: [`linbpq-mail-compat.md`](linbpq-mail-compat.md) — every wire-level requirement, sourced; its §8 MUST line is this project's compat contract and its §9 is the oracle checklist.

**Rules of the road** (Tom, 2026-06-11): own repo; **public interfaces only** — the BBS reaches pdn exclusively through RHPv2 (the network plane any app gets), its web UI rides the app-gateway identity contract, and it ships as a standard `pdn-app.yaml` package. pdn contains zero BBS-specific code. Webmail is a product MUST (compat-spec §8 parks it as forwarding-irrelevant, which it is — it's still required here).

## Architecture

```
pdn-bbs.sln
  src/Bbs.Fbb        the wire: lzhuf (N=2048, CRC16+len32 "e1" container), SID build/parse,
                     FA/FC proposals + F> checksum, FS parse/emit, SOH/STX/EOT framing,
                     R:-line codec, the forwarding session FSM (caller + answerer roles),
                     MBL/RLI text fallback (SHOULD). No I/O — pure codecs + an FSM over
                     an abstract duplex byte stream. Golden-vector-pinned.
  src/Bbs.Core       domain: SQLite message store (messages, recipients, BID dedup store
                     with lifetime, users/home-BBS, partner config, WP later), the
                     hierarchical-address parser/normaliser (WW-rooted), routing
                     (TO/AT/HR per-partner, implied-AT, best-HR-depth single-copy for P),
                     housekeeping (kill-by-age per type/state, BID lifetime, K-purge).
                     TimeProvider-driven throughout.
  src/Bbs.Console    the terse RF user surface over an abstract session stream:
                     prompt "de <CALL>>", ?/H A B X V N I L-family R/RM S-family K/KM OP
                     NODE, /ex + Ctrl-Z entry, paging, the exact acceptance shapes
                     (compat spec §1).
  src/Bbs.Host       composition: RHPv2 client (rhp2lib-net if consumable, else a minimal
                     in-repo client to the pinned wire) binding the BBS callsign —
                     inbound accept → Console session OR Fbb answerer (sniff the first
                     line: a SID = forwarding partner); outbound forwarding scheduler
                     (per-partner interval/immediate + connect scripts over RHP open);
                     ASP.NET loopback webmail (gateway identity headers, no second auth);
                     YAML config; pdn-app.yaml manifest (service + ui blocks).
  tests/             unit per project + golden vectors; tests/Bbs.Interop.Tests vs a
                     BPQMail-enabled LinBPQ container (compat spec §7) — the oracle lane.
  docker/            the oracle compose: linbpq + linmail.cfg (generated per spec §7) +
                     netsim, mirroring packet.net's interop stack patterns.
```

Dependency rule: Fbb and Core never reference Host; Console references Core only; Host references all three. Nothing references packet.net source — only published packages (and only if the RHP client lib is consumable; otherwise self-contained).

## The home-BBS requirement (Tom, 2026-06-11)

Although rare, a user MUST be able to adopt an instance as their **home BBS** without being the node owner — e.g. a legacy TNC2 user across town using this node as their mail provider. Their address becomes `<call> @ <bbscall>.<HA>`; the world routes mail here and it waits in their inbox. This decomposes to:

1. **Local delivery beats forwarding** — the load-bearing rule: a personal whose AT is us (our callsign, or an address under our own HA) gets **zero forward targets**, full stop; and a personal with no AT whose TO is a known local user stays local rather than matching any partner's wildcard AT. The dangerous failure is the silent one: a wildcard default route swallowing local users' mail. Pinned by tests before any wildcard-AT partner config goes live. **Implemented** (feat/local-delivery-rule): `RoutingEngine.Route` pre-empts the personal single-copy/flood dispatch with a `ResolvesToLocal` check (`Callsigns.BaseEquals(atBbs, _ownCall)` or `_ownHa.AreaContains(at)`; or no-AT + `RoutingRequest.ToIsLocalUser`) and returns an empty, `IsLocal`-flagged `RoutingDecision`; `RoutingService` sets `ToIsLocalUser` from `BbsStore.UserExists(<to>)` (base-callsign match). This restores LinBPQ's own "already here" local-first behaviour ([BPQ-SRC CheckAndSend]) the faithful single-copy port otherwise lacked. **Pinned by tests**: the leak test (`LocalDeliveryRoutingTests.NoAt_LocalUser_NeverLeaksToWildcardPartner` — verified red before implementation) plus the engine-level `RoutingEngineTests` "local delivery beats forwarding" suite (at-is-us / under-HA / no-over-suppression / explicit-remote-AT-still-forwards / bulletin+traffic regression guards) and `BbsStoreTests.UserExists_…`.
2. **Auto-create the user on first inbound personal** — mail can arrive before its owner ever connects; a skeletal user record makes it listable on their first `L`. **Implemented** (feat/auto-create-homed-user): the inbound delivery path (`InboundMessageReceiver.Deliver` — inbound forwarding only; webmail compose / console sends never reach it) calls `AutoCreateHomedUser` after storing. The trigger is exactly the homed-personal case: the delivery is **type Personal** AND its **AT resolves to us** — `RoutingEngine.AtResolvesToLocal(at)`, the own-call/own-HA half of rule #1's local-delivery signal (`Callsigns.BaseEquals(atBbs, _ownCall)` or `_ownHa.AreaContains(at)`; an empty AT is never us by this test, so the no-AT-existing-user case is excluded) AND the **TO is not already a known local user** (`BbsStore.UserExists`) → `BbsStore.EnsureUser(<to>)` inserts a skeletal row keyed by the base callsign (callsign only; name/QTH/Home left for the console's first-connect persistence). `EnsureUser` is idempotent (a no-op when the user already exists, matched on the base call), so repeat inbounds never duplicate or error. Explicit-remote-AT personals fail the AT-is-us test (they forward onward per rule #1), and bulletins / NTS-traffic are excluded by the Personal-only guard — none auto-create. **Pinned by tests** (written first; the positive auto-create cases verified red on the pre-rule receiver, then green): `BbsStoreTests.EnsureUser_CreatesSkeletalRecord_IdempotentOnBaseCall` (store half: skeletal create, idempotence, base-call keying) and `AutoCreateHomedUserTests` (host half through the real `Deliver`: AT=own-call → user created + mail listable; AT under our HA → created; second inbound for the same TO → still one user; no-AT existing-user → delivered, no new user; explicit remote AT → no auto-create; bulletin + NTS addressed locally → no auto-create).
3. **WP announcements** (spec SHOULD, promoted by this requirement) — emitting White Pages updates for homed users is how the rest of the network learns to route `<call> @ here`.
4. The RF console is already the full management surface (read/kill/send, `Home`/name/QTH persistence) — no node-owner involvement needed; callsign-is-identity per network norms.

## The plain-language mandate (Tom, 2026-06-11 — supersedes the compat-spec §8 "classic user surface" MUST)

**All of the arcane knowledge goes: L/R/K/S, status letters, hierarchical-route incantations, the lot.** Interop compatibility is a *wire* property (FBB forwarding with partner BBSes — untouched); it was never a reason to make humans speak W0RLI. The console becomes a plain-language line-mode surface that a TNC2 user on a dumb terminal can drive without folklore:

- **Canonical commands are words**: `help`, `list` (new mail), `read <n>`, `send <call>`, `reply`, `delete <n>`, `bulletins`, `topics`, `name`, `home`, `quit`. Single letters survive only as *accidental abbreviations* — any unambiguous prefix of a word works (`l`, `r 3`, `q`), so a die-hard's fingers still work, but nothing requires the folklore and `help` explains everything in sentences.
- **Listings are sentences, not column dumps with status letters**: "3 new messages — 1) from G4ABC, 12 Jun: Antenna party …", paclen-friendly lines, paged with a plain "more? (yes/no)".
- **Addressing is just a callsign**: `send g4abc` — the system resolves where G4ABC lives via the network directory; the power form `send g4abc@gb7bsk` exists; regions/hierarchies NEVER surface to users.
- **Onboarding in sentences**: the first-connect flow asks for a name and offers "make this your home mailbox?" — no Z/QTH/Home command trivia.
- **Sysop surface likewise**: `forwarding` (status), `queue`, `route <call>` (explain) — see forwarding.md's vocabulary table.
- **Classic mode is a per-user preference** (Tom): the byte-exact W0RLI surface the W3 wave built (193 tests) is kept whole as `interface: classic` in the per-user settings — for users whose automated clients (Winpack-era) pattern-match the legacy prompts. **Plain is the default**; a user flips it once with the `classic` command (typable by hand from any terminal, even a TNC2), from webmail, or the sysop sets it for them; the session engine picks the surface by callsign at connect (the caller is known before the greeting). Partner BBS forwarding is unaffected either way (SID-triggered, wire-level).
- So wave **U-1** builds the plain surface as the default alongside the kept classic implementation — same session engine (lifecycle, store wiring, paging, persistence), two command tables/renderers, one preference. Runnable in parallel with F-1.

## Load-bearing decisions

1. **Session demux — greet immediately** (F-0; supersedes the silent first-line peek): one RHP-bound callsign serves both users and partners, and the BBS speaks FIRST, like LinBPQ itself (the silent peek deadlocked against a real LinBPQ caller, which waits for our text before sending anything). On accept the demux instantly sends our SID line, then starts the Console session so its greeting/prompt flows at once — but holds the console's INPUT behind a first-line gate while the demux peeks the first inbound line. A forwarding opener (`[…-…]` SID shape, or `;FW:` per spec §1.1) → the console is aborted via the gate (it has consumed no input, so a partner's SID can never be eaten by the new-user name prompt) and awaited so its writes flush, then the Fbb answerer runs in continue-mode (`FbbSessionConfig.SidAlreadySent`: the FSM starts at SID-parse; the peeked bytes are fed in). Anything else → the gate opens and the line is the console's first command. A silent caller sees the greeting while the demux waits `demuxFirstLineWaitSeconds`; at expiry the session is the console's and input flows normally thereafter (a later SID is just an invalid command). For a known partner the console skips the banner (the BBS flag, spec §1.1), so the wire is the classic `[SID]` + `de CALL>` transcript.
2. **lzhuf**: implement from the compat spec §6 byte layouts, pinned by the research golden vectors (`"Hello World!\r\n"` → `b5 66 0e 00 00 00 ea 7c ...`; the ascmail corpus vector) and round-trip property tests. XMODEM CRC16 over len+bitstream, little-endian, prepended. N=2048/F=60/THRESHOLD=2/fill 0x20, MSB-first 16-bit accumulators.
3. **Forwarding FSM** is sans-IO: `Advance(line|block) → actions`. Both roles in one machine, tested against scripted transcripts (including the wl2k-go/JNOS/BPQ asymmetries: BPQ `F>` always checksummed vs JNOS bare for FA; FS `H` = defer when talking, accept-tolerant when listening; STX len-0 = 256 inbound, ≤250 emitted; the 6-byte restart preamble parse-tolerated).
4. **Identity/trust for webmail**: the gateway's `X-Pdn-Gateway: 1` + `X-Pdn-User` headers are the auth boundary (loopback bind, per the app-gateway contract). BBS users map pdn usernames ↔ callsigns via its own user table.
5. **SID**: advertise `[PDN-<ver>-B1FHM$]` minimum (never containing "BPQ" — the compat spec's gating trap); add `2` per-partner when B2F lands. **B2F landed** (feat/b2f-wire): per-partner `allowB2` (default off ⇒ B1 unchanged) drives the `2` digit; B2 is active for a session iff `allowB2` ∩ the partner's SID `B2`. Outbound proposes uniform `FC EM` + ships a B2 object through the same B1 container/framing; inbound accepts `FC` (same dup-BID/size/hold checks as `FA`), decodes the B2 object and stores it through the identical store/route path (so the home-BBS rules #1/#2 + the R-chain loop/age guard apply to B2 inbound too). The answerer advertises `2` only to a partner record we set `allowB2` on, so we never accept `FC` from a non-`allowB2` caller (the original `-` guard, intact). **Named deferral (slice 2):** single-recipient/no-attachment only; multi-recipient fan-out (multiple `To:`/`Cc:`) + `File:` attachments are F-1 (codec handles them; builder/receiver take the first recipient — the same first-recipient deferral the FA path takes; tracked by a skipped test).
6. **Storage**: one SQLite db in the package state dir (`PDN_APP_STATE`), resilient-open pattern, schema versioned.
7. **Oracle-first**: every forwarding behaviour lands with (a) a transcript test from the spec, then (b) an assertion against the live LinBPQ container before it's called done — same diff-oracle discipline that paid off in RHPv2 R-4/R-5. Tom's m0lte/linbpq `tests/integration/fbb_partner.py` + `bpqmail_cfg.py` are the proven harness to port/reuse.
8. **Connect scripts (F-0, spec §4.4)**: full script semantics over the RHP bearer. An optional FIRST `C [port] <target>` names the RHP open (the port rides the open; digi paths are warned-unsupported); every later line — including a second `C`, which at a node prompt is the remote node's own connect command — is sent verbatim with a response wait between lines (progress = ` CONNECTED` / `OK` / a `>` prompt; the §4.4 failure list — BUSY/FAILURE/SORRY/… — fails the cycle into the backoff-retry path); `PAUSE n` delays; the other §4.4 directives are recognised (kept off the wire) and warned. After the last line the script layer itself "waits for a SID or `>`" (§4.4 verbatim), consuming node chatter (CTEXT, `*** Connected to …` progress lines) so the Fbb caller FSM sees a clean stream from the SID on. Each wait is bounded by the partner's ConTimeout (named deviations: per-wait rather than whole-handshake; case-insensitive scans). Stepless plans (bare `connect:` / none) skip the script layer entirely — the FSM waits out the SID, exactly the pre-F-0 behaviour. Failures carry the attempt transcript (what we sent / what came back / where it stopped).

## Build waves

- **W0** scaffold: repo, sln, CPM, CI (self-hosted runner, packet.net conventions), docs in-repo, empty-green test lanes.
- **W1** `Bbs.Fbb` codecs: lzhuf+container (golden vectors), SID, proposals+F>, FS, framing, R: lines. Sub-agent; the compat spec carries everything needed.
- **W2** `Bbs.Core`: store + BID + addressing + routing + housekeeping. Sub-agent, parallel with W1.
- **W3** `Bbs.Console`: the user surface over fakes (parallel with W1/W2 once Core's interfaces pin).
- **W4** forwarding FSM both roles + scripted-peer transcript suite (needs W1).
- **W5** `Bbs.Host`: RHP client + demux + scheduler + webmail + manifest + config (needs W2–W4).
- **W6** oracle: LinBPQ docker lane green both directions; lab deploy as a package next to WALL/LOBBY; live demo (user session over netsim + a real forwarding cycle with the oracle).
- **W7** SHOULD wave: **B2F wiring ✅ landed** (feat/b2f-wire — per-partner `allowB2` gate, FC propose/transfer + receive/decode/store, single-recipient/no-attachment; multi-recipient + attachments deferred to F-1; see decision 5 and forwarding.md "B2F negotiation"); WP consume, MBL fallback, resume granting, NTS — per spec §8 ordering.
