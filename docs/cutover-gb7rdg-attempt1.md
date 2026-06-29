# GB7RDG cutover — attempt 1 retrospective + re-attempt plan

**Date:** 2026-06-24 · **Outcome:** attempted, then **rolled back** (cleanly, no mail moved) · **Author:** autonomous session with M0LTE

This is the write-up of the first live GB7RDG LinBPQ→pdn cutover attempt. It records what was done, the one blocker that stopped it, the bugs found along the way, what shipped, and the checklist to clear before going again. Read [`cutover-gb7rdg.md`](cutover-gb7rdg.md) (the runbook) alongside this.

> **Correction (2026-06-29, M0LTE).** The mod-128 / broken-fallback diagnosis in this retrospective was **wrong** — it was propagation-confounded on-air. The **actual** blocker was a functional miss in *connect-script* support: the flat `EXPECT=SEND` form split on the first `=` and trimmed both sides, so an intermediate hop's prompt (URONode's `=> ` — `=` is the field delimiter, the trailing space is eaten) was unrepresentable, and **GB7CIP / GB7LOX could never be authored or brought up in `connect-test`**. Fixed by **connect-scripts v2** (structured steps — [`connect-script-v2.md`](connect-script-v2.md), released 0.2.52). Verified 2026-06-29: the node's SABME→SABM fallback **is** present and default-on (live LinBPQ + XRouter interop tests), and real BPQ *rejects* SABME with FRMR (it is never silent), so the fallback engages cleanly — there was no node-side blocker. The "## The blocker" section and re-attempt item 1 below are kept as the original record but are **superseded**; the updated procedure is in [`cutover-gb7rdg.md`](cutover-gb7rdg.md).

## TL;DR

The phased cutover ran cleanly through `freeze → sync → baseline → network → verify` — GB7RDG came up on-air as the pdn node, fully held (no forwarding). The Tom-owned `connect-test` phase then surfaced **the blocker: pdn dials AX.25 with SABME (mod-128) only, and the mod-128→mod-8 fallback is broken**, while every partner (and the original BPQ config) is mod-8. Because AX.25 connection-mode behaviour must be validated against deterministic peers (not live-RF propagation luck), the call was to **roll back to LinBPQ and fix the node in the lab**. `abort` restored LinBPQ on-air and stopped the CT. The lab box (`packetdotnet`) was then upgraded to the latest node + bbs for that focused testing. The connect-script SID-capture fix that was found and shipped during the attempt (pdn-bbs v0.2.44) is good and kept.

## What happened (timeline)

1. **freeze** — stopped LinBPQ; GB7RDG off-air, mail files frozen consistent.
2. **sync** — atomic dump, fail-closed orphan-header gate, rebuilt `bbs.db`, loaded into the CT (held). 0 orphan headers.
3. **baseline** — snapshot (155 msgs after housekeeping, high-water 1410, 1685 BIDs, 15 partners all `enabled=0`, `forwarding_master=0`).
4. **network** — WireGuard handover (CT inherited `10.66.66.6` + hub handshake), all 7 ports enabled. GB7RDG on-air as the pdn node, **mail still held** (master + per-partner gate, both held).
5. **verify** — read-only on-air health: node healthy, CT owns `10.66.66.6`, 4/4 kissproxy modems ESTABLISHED, AXUDP peer reachable. Wire-truth via kiss-collector: the new node TX'd ID/BEACON/NODES on 2m/40m/70cm and heard GB7WOD.
6. **connect-test** (Tom-owned) — found the connect-script wrong-banner bug (fixed + shipped, see below), then the mod-128 blocker.
7. **Decision (Tom): roll back**, do focused connect-script + mod-8/fallback testing in the lab, re-attempt later.
8. **abort** — re-held + restored the CT, dropped its wg (verified down → no dual-claim), brought LinBPQ + its wg back on-air. **LinBPQ live again as GB7RDG; CT stopped.**
9. **Lab refresh** — `packetdotnet` upgraded node 0.22.0→0.26.0, bbs 0.2.16→0.2.44 for the focused testing.

A re-cutover **must start fresh from `freeze`** (never reuse a stale import).

## The blocker (fix before re-attempt): pdn dials mod-128 only, fallback broken

> **Superseded (2026-06-29) — see the Correction at the top.** The SABME→SABM fallback was already present and default-on; mod-8 was never the blocker (the real one was connect-script support). Kept below verbatim as the original record.

This is the thing that actually stopped the cutover.

- **On the wire** (kiss-collector, the node *is* the collector): GB7RDG's forwarding dials went out as **SABME (mod-128) only** — 24 SABME on 40m + 20 SABME on 70cm in the test window, **zero SABM**. No mod-128→mod-8 fallback ever fired.
- **The partners are mod-8.** The original `bpq32.cfg` sets `MAXFRAME=5/4/3/7` (2m/70cm/40m/6m) — all ≤7 → mod-8 — with no `EAX25`/mod-128 anywhere. mod-8 is the faithful setting; pdn defaulting to mod-128 is the divergence.
- **pdn has no per-port mod knob**, and the BPQ import dropped `MAXFRAME` (it never reached the pdn port config), so the node fell to its mod-128 default.
- **Why it sometimes "worked":** GB7WEM-7 (a Linux-kernel-AX.25 box, mod-128-capable) UA'd the SABME and connected — proving the node *can* dial out. The BPQ partners (GB7BPQ, GB7BSK-1) returned nothing — they were also out of propagation in the window, so mode-vs-propagation couldn't be fully separated on-air. But a mod-8-only BPQ node that ignores an unanswered SABME would never get a SABM, so it would fail even with perfect propagation.
- **Tom confirmed:** the SABME→SABM fallback is *supposed* to exist (so it's broken), and "node prompts are not standardised in any way."

**The fix (packet.net node — NOT pdn-bbs — done in the lab):**
1. Repair the **SABME→SABM fallback** so a connect that doesn't establish in mod-128 retries in mod-8 (and isn't gated solely on receiving a `DM`/`FRMR`, since a silent/out-of-range peer never sends one).
2. **Carry BPQ `MAXFRAME` → mod-8 through the import** so known-mod-8 ports are dialled mod-8 directly and don't depend on fallback at all.
3. **Test against controlled peers** — a real mod-8 BPQ, a mod-128 Linux/direwolf box, a silent peer — in the lab/net-sim. Do **not** validate AX.25 mode behaviour on the live CT; propagation there is uncontrolled.

## Shipped during the attempt (good, kept)

- **pdn-bbs v0.2.44** — connect-script terminal fix. A multi-hop dial (e.g. GB7CIP = `C 3 GB7WEM-7` then bare `C uhf gb7cip`) was capturing the **gateway's node banner** instead of the partner's BBS SID, because the post-script hand-to-FBB wait accepted any `>`-terminated line and URONode's `…Help: ? <command>` ends in `>`. The FBB SID `[…-…]` is the only standardised token in the exchange, so the terminal wait now accepts `Sid.IsSidShaped` **only** and skips every intermediate node banner. (PR #79.)
  - **Lessons** (see also the connect-script source docs): node prompts are *not* standardised, so a bare line cannot reliably auto-wait for one — to wait, set an explicit `<prompt>=<command>` EXPECT step. An earlier attempt to make bare steps auto-wait for a *guessed* prompt broke the real BPQ oracle interop test (BPQ's node prompt isn't `>`-shaped) and was reverted. Also: `PAUSE n` → recognised-but-superseded note (the expect gate supersedes timed pacing); an **empty connect script = inbound-only** (the partner dials us and polls; we never dial it).
- **node-v0.26.0** — catalog refresh pinning bbs 0.2.44 (no node code change since 0.25.0).

## Bugs found (still to land)

**Cutover-runbook (`scripts/cutover-gb7rdg.sh`) — 7 bugs + 1 wart.** The script had never been run end-to-end; the live run found:
1. `freeze`: `sleep 3` too short for LinBPQ graceful shutdown → poll-for-inactive (40 s).
2. `is-active | grep -qx inactive` can never pass under `set -o pipefail` (`is-active` exits 3 for inactive, and the pipe takes its exit, not grep's) → a `bpq_inactive` string-compare helper.
3. `baseline` high-water Python query used double-quotes that break `ct()`'s `bash -lc "…"` wrapping → `chr(39)`.
4. `network` demanded a WireGuard handshake *before* any traffic flowed (WG is lazy) → ping-to-trigger then poll.
5+6. `verify` modem count (line ~372): `ss` renders the peer as `[::ffff:10.45.0.121]:8912` with `ESTAB` at the line **start**, so `$HOST:891x.*ESTAB` never matched (false "RF ports not up"), and a stray `|| echo 0` double-printed → `[[: 0\n0` arithmetic error → `grep -F $HOST | grep -cE ':891[0-3]' || true | tr -dc 0-9`.
7. `abort` SQL re-hold used double-quotes that break `ct()`'s `bash -lc` (same class as #3) — it died mid-abort, briefly leaving GB7RDG off-air entirely → parameterized SQL.
- **Wart:** `abort` starts the node (`systemctl start`) on the stale, ports-**enabled** config *before* `ct_apply` re-holds it, so the node comes up on-air for ~1 s (one stray `GB7RDG-2` frame; no dual-claim because LinBPQ was still down). It should start held — drop the premature start and let `ct_apply` bring it up with the held config.

**pdn-app.yaml version lag (build bug).** `scripts/build-deb.sh` copies `pdn-app.yaml` into the deb **verbatim** (no `@VERSION@` substitution, unlike the deb control file), and the repo file is hardcoded `version: "0.2.43"`. So the 0.2.44 deb's app-manifest reports 0.2.43 — the catalog pins 0.2.44 but ships a 0.2.43-labelled manifest (wrong version display + spurious "update available" on any node). The binary is correctly 0.2.44 (assembly-stamped). **Fix:** templatize `pdn-app.yaml` `version: "@VERSION@"` and have `build-deb.sh` sed-stamp it. Worked around on the lab by correcting the staged manifest.

## Re-attempt checklist

- [x] ~~**Fix + ship the node mod-8/fallback** (the blocker) — lab-tested against controlled mod-8 / mod-128 / silent peers.~~ **Superseded (2026-06-29):** the SABME→SABM fallback was already present + default-on (verified; live LinBPQ/XRouter interop tests) and BPQ FRMRs SABME, so mod-8 was never the blocker. See the Correction at the top.
- [ ] **Land the cutover-runbook fixes** (the 7 bugs + the abort-start-held wart).
- [ ] **Fix the pdn-app.yaml version stamping** (next bbs release).
- [ ] **Re-run focused connect-script testing on the lab** — per-partner `EXPECT=` tuning for the multi-hop partners (GB7CIP `C 3 GB7WEM-7`→`C uhf gb7cip`; GB7LOX `C 3 GB7LOX-2`→`bbs`), using the real prompt the test-connect transcript surfaces.
- [ ] **Re-cut from `freeze`** (fresh sync; never reuse a stale import).

## Current state (end of attempt 1)

- **LinBPQ live** on `gb7rdg-node` again — reclaimed wg `10.66.66.6`, transmitting (verified on the kiss-collector). The CT is **stopped** and reset to a held state.
- **Lab `packetdotnet`** on **node 0.26.0 + bbs 0.2.44** — ready for the focused connect-script + (separately) the mod-8/fallback node work.
- Mail integrity intact throughout: nothing forwarded, the cutover never reached `golive`, and the rollback restored LinBPQ as the single GB7RDG identity (no dual-claim at any point).

## Partner reference (for the re-attempt)

| Partner | Script shape | Notes |
|---|---|---|
| Direct dial | `C <port> <call>` | EI5IYB, GB7BPQ, GB7BSK, GB7HFD, GB7IOW, M9YYY — wait for the partner FBB SID |
| Portless/alias | `C NDHBBS` | GB7NDH — node routes it |
| PAUSE + dial | `PAUSE n` / `INTERLOCK n` / `C <port> <call>` | GB7AUG, GB7OXF, M5MPC — PAUSE/INTERLOCK now notes |
| Multi-hop | dial gateway → opaque onward command | GB7CIP (`C uhf gb7cip` via GB7WEM-7), GB7LOX (`bbs` via GB7LOX-2) — set explicit `EXPECT=` |
| Inbound-only | empty script | GB7BMY, SA6DAZ, M7TAW — they dial us; never dialled outbound |

The 3 NET/ROM neighbours to notify on a real cutover: GB7WOD (RF), GB7NDH + GB7BDH (AXUDP). BBS forwarding partners are a separate relationship.
