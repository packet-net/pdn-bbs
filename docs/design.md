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

1. **Local delivery beats forwarding** — the load-bearing rule: a personal whose AT is us (our callsign, or an address under our own HA) gets **zero forward targets**, full stop; and a personal with no AT whose TO is a known local user stays local rather than matching any partner's wildcard AT. The dangerous failure is the silent one: a wildcard default route swallowing local users' mail. Pinned by tests before any wildcard-AT partner config goes live.
2. **Auto-create the user on first inbound personal** — mail can arrive before its owner ever connects; a skeletal user record makes it listable on their first `L`.
3. **WP announcements** (spec SHOULD, promoted by this requirement) — emitting White Pages updates for homed users is how the rest of the network learns to route `<call> @ here`.
4. The RF console is already the full management surface (read/kill/send, `Home`/name/QTH persistence) — no node-owner involvement needed; callsign-is-identity per network norms.

## The plain-language mandate (Tom, 2026-06-11 — supersedes the compat-spec §8 "classic user surface" MUST)

**All of the arcane knowledge goes: L/R/K/S, status letters, hierarchical-route incantations, the lot.** Interop compatibility is a *wire* property (FBB forwarding with partner BBSes — untouched); it was never a reason to make humans speak W0RLI. The console becomes a plain-language line-mode surface that a TNC2 user on a dumb terminal can drive without folklore:

- **Canonical commands are words**: `help`, `list` (new mail), `read <n>`, `send <call>`, `reply`, `delete <n>`, `bulletins`, `topics`, `name`, `home`, `quit`. Single letters survive only as *accidental abbreviations* — any unambiguous prefix of a word works (`l`, `r 3`, `q`), so a die-hard's fingers still work, but nothing requires the folklore and `help` explains everything in sentences.
- **Listings are sentences, not column dumps with status letters**: "3 new messages — 1) from G4ABC, 12 Jun: Antenna party …", paclen-friendly lines, paged with a plain "more? (yes/no)".
- **Addressing is just a callsign**: `send g4abc` — the system resolves where G4ABC lives via the network directory; the power form `send g4abc@gb7bsk` exists; regions/hierarchies NEVER surface to users.
- **Onboarding in sentences**: the first-connect flow asks for a name and offers "make this your home mailbox?" — no Z/QTH/Home command trivia.
- **Sysop surface likewise**: `forwarding` (status), `queue`, `route <call>` (explain) — see forwarding.md's vocabulary table.
- **Recorded consequence**: legacy *automated* user-side clients that pattern-match W0RLI prompts (Winpack-era) will not drive this console. Partner BBS forwarding is unaffected (SID-triggered, wire-level). If a real need appears, a classic-mode shim can be an opt-in later — it is deliberately NOT being built now.
- The W3 console engine (session lifecycle, store wiring, paging, persistence) is kept; its command table and output shapes are re-languaged. This is wave **U-1**, runnable in parallel with F-1.

## Load-bearing decisions

1. **Session demux**: one RHP-bound callsign serves both users and partners. The answerer sniffs the first inbound line: `[...-...$]`-shaped SID → Fbb answerer FSM; anything else → Console. (LinBPQ does the same on its BBS port.)
2. **lzhuf**: implement from the compat spec §6 byte layouts, pinned by the research golden vectors (`"Hello World!\r\n"` → `b5 66 0e 00 00 00 ea 7c ...`; the ascmail corpus vector) and round-trip property tests. XMODEM CRC16 over len+bitstream, little-endian, prepended. N=2048/F=60/THRESHOLD=2/fill 0x20, MSB-first 16-bit accumulators.
3. **Forwarding FSM** is sans-IO: `Advance(line|block) → actions`. Both roles in one machine, tested against scripted transcripts (including the wl2k-go/JNOS/BPQ asymmetries: BPQ `F>` always checksummed vs JNOS bare for FA; FS `H` = defer when talking, accept-tolerant when listening; STX len-0 = 256 inbound, ≤250 emitted; the 6-byte restart preamble parse-tolerated).
4. **Identity/trust for webmail**: the gateway's `X-Pdn-Gateway: 1` + `X-Pdn-User` headers are the auth boundary (loopback bind, per the app-gateway contract). BBS users map pdn usernames ↔ callsigns via its own user table.
5. **SID**: advertise `[PDN-<ver>-B1FHM$]` minimum (never containing "BPQ" — the compat spec's gating trap); add `2` per-partner when B2F lands.
6. **Storage**: one SQLite db in the package state dir (`PDN_APP_STATE`), resilient-open pattern, schema versioned.
7. **Oracle-first**: every forwarding behaviour lands with (a) a transcript test from the spec, then (b) an assertion against the live LinBPQ container before it's called done — same diff-oracle discipline that paid off in RHPv2 R-4/R-5. Tom's m0lte/linbpq `tests/integration/fbb_partner.py` + `bpqmail_cfg.py` are the proven harness to port/reuse.

## Build waves

- **W0** scaffold: repo, sln, CPM, CI (self-hosted runner, packet.net conventions), docs in-repo, empty-green test lanes.
- **W1** `Bbs.Fbb` codecs: lzhuf+container (golden vectors), SID, proposals+F>, FS, framing, R: lines. Sub-agent; the compat spec carries everything needed.
- **W2** `Bbs.Core`: store + BID + addressing + routing + housekeeping. Sub-agent, parallel with W1.
- **W3** `Bbs.Console`: the user surface over fakes (parallel with W1/W2 once Core's interfaces pin).
- **W4** forwarding FSM both roles + scripted-peer transcript suite (needs W1).
- **W5** `Bbs.Host`: RHP client + demux + scheduler + webmail + manifest + config (needs W2–W4).
- **W6** oracle: LinBPQ docker lane green both directions; lab deploy as a package next to WALL/LOBBY; live demo (user session over netsim + a real forwarding cycle with the oracle).
- **W7** SHOULD wave: B2F, WP consume, MBL fallback, resume granting, NTS — per spec §8 ordering.
