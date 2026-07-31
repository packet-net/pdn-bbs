# GB7RDG cutover — state of play

**Banked:** 2026-07-01 · **Attempt 2 — ROLLED BACK 2026-07-21** · **Re-prepped for attempt 3: 2026-07-31.**

Attempt 2 ran `freeze→sync→baseline→network→verify` clean; at the pre-`golive` hold a test-connect dialled on the wrong RF port, so `abort` was run. **Root cause VERIFIED** (see Attempt-2 note): a connect-script Dial `port` of `hf-40m` (a name) was non-numeric → dropped to null → the node dialled the *first* port (vhf-2m) instead of 40m. **GB7RDG is live on LinBPQ; the CT is held; no dual-claim; no mail moved (`golive` never reached).**

**Attempt-3 prep DONE (2026-07-31):** CT restaged to **node 0.36.2 + pdn-bbs 0.2.53** (the pair that carries a port **name** end to end), `preflight` re-run **GREEN**, and `freeze` now also **disables** LinBPQ and **restarts kissproxy**. Ready for `freeze` on Tom's go.

Living operational snapshot of the in-progress GB7RDG LinBPQ→pdn cutover. Read alongside [`cutover-gb7rdg.md`](cutover-gb7rdg.md) (the runbook) and [`cutover-gb7rdg-attempt1.md`](cutover-gb7rdg-attempt1.md) (attempt-1 retrospective + the corrected diagnosis). Update this in place as phases complete.

## Where we are (phase ladder)

Attempt 2 ran `preflight ✅ → freeze ✅ → sync ✅ → baseline ✅ → network ✅ → verify ✅`, then **`abort` ✅ — rolled back to LinBPQ** at the pre-`golive` hold. `connect-test` / `golive` / `validate` were NOT reached. A re-attempt starts fresh from `freeze`.

- **CT 129 (`gb7rdg.lan` / 10.45.0.87) is staged**: `packetnet` **0.36.2** + `pdn-bbs` **0.2.53** installed 2026-07-31 (canonical release debs, SHA256-verified). Node held — all 7 ports `enabled=false` (4 RF + 3 AXUDP), `oarc.enabled=false`, healthz 200, forwarding HELD in the log. Off-air; no dual-claim. Config store is on schema **v2**, loaded clean (`callsign GB7RDG, 7 port(s)`), no errors on restart. The node's live port ids read back as **`vhf-2m, uhf-70cm, hf-40m, vhf-6m, axudp-10093, axudp-10094, axudp-10095`** — these are exactly the strings the Dial `port` takes (see Partner reference).
- **`preflight` GREEN** (re-run 2026-07-31 on node 0.36.2 / pdn-bbs 0.2.53): all `[ok]`, one expected non-blocking warning (no tailscale key — M7TAW is dead). `preflight` now **hard-gates** node ≥ 0.36.2 and pdn-bbs ≥ 0.2.53 (it previously allowed ≥ 0.22.0 / ≥ 0.2.52, which is how the wrong pair got staged).
- **GB7RDG is live on LinBPQ again** (`gb7rdg-node`, 10.45.0.121; `linbpq` active, `wg0` up as `10.66.66.6` with a fresh handshake). **The CT (129) is held** — all 7 ports `enabled=false`, `forwarding_master=0`, all partners disabled, `bbs.db.pre-cutover` restored, CT wg down (0 interfaces). **Single identity, no dual-claim.** Attempt 2 reached the pre-`golive` hold (mailbox had rebuilt fresh: 190 msgs, **0 orphan headers**) but **no mail ever forwarded** (`golive` not reached) — LinBPQ's mailbox is intact.
- **Attempt-2 root cause (2026-07-21, VERIFIED):** a **connect-script port-config error**, plus an unstable transport that hid it. The GB7CIP test-connect Dial step was `{"open":"GB7WEM-7","port":"hf-40m"}`. The Dial `port` field requires a **1-indexed numeric** port label; `hf-40m` (a port *name*) is silently dropped to null (pdn-bbs `ConnectScript.cs:117-124`), and the node then dials on the **first** configured port = `vhf-2m` (packet.net `SupervisorRhpGateway.cs:77-82`). Confirmed on the wire: the connect ran **80.07 s** = vhf-2m's `T1 4000 ms × N2 20` (hf-40m would be 56 s). So the dial was **misrouted to 2m** — the monitor showing `vhf-2m` was *correct*, not a display bug (an earlier hypothesis of mine, retracted). The correct value is **`3`** (= hf-40m, 3rd configured port; matches LinBPQ's `C 3 !GB7WEM-7`). Separately, the vhf-2m/vhf-6m KISS-TCP links (8910/8913) were flapping and the ACKMODE pacer faulted with `ObjectDisposedException` on the disposed `KissTcpClient` (`PacingKissModem`), so the misrouted 2m dial never reliably reached the air (zero 2m TX in the collector; 2m radio didn't key — the HF PTT the operator saw was concurrent *inbound* sessions on hf-40m). Issues filed: [pdn-bbs #91](https://github.com/packet-net/pdn-bbs/issues/91) (port-reference UX), [packet.net #664](https://github.com/packet-net/packet.net/issues/664) (ObjectDisposedException-on-reconnect), [packet.net #665](https://github.com/packet-net/packet.net/issues/665) (node silently defaults to the first port). **All three are now FIXED + released (2026-07-21):** node-v0.36.1 (#664 pacing retry, #665 no silent first-port, #91 accept port ids) + pdn-bbs 0.2.53 (pass the port verbatim). The Dial `port` now takes a port **name** (GB7WEM-7/GB7CIP = `hf-40m`) — see the Partner reference below. **Before re-attempt:** stage node-v0.36.1 + pdn-bbs 0.2.53, set Dial ports to names, re-cut fresh from `freeze`.

## Execution environment (IMPORTANT — not claude-code)

The cutover is driven from **studybox (the primary working host), NOT `claude-code`.** claude-code was a single point of failure that dropped mid-session and is now shut down; all dependence on it has been broken. Everything needed is local or pulled from the live node at run time:

- **Script**: `~/src/pdn-bbs/scripts/cutover-gb7rdg.sh` (this repo; also on GitHub).
- **`STAGE_DIR` = `~/gb7rdg-cutover/`** (local), containing:
  - `packetnet.yaml` + `bbs.yaml` — the authoritative **held** config (node ports/oarc HELD; bbs forwarding HELD + housekeeping 365/60). Originally authored on claude-code; now local.
  - `debs/node/packetnet_0.36.2_amd64.deb`, `debs/bbs/pdn-bbs_0.2.53_amd64.deb` (+ SHA256SUMS) — from GitHub releases, SHA256-verified (2026-07-31). The superseded `packetnet_0.26.0_amd64.deb` is still present but is **not** the staged version.
  - `reference/` — `GAP-ANALYSIS.md`, `ANALYSIS.md`, `bpq32.cfg` (2026-06-11 snapshot), `gb7rdg-migration.md`, `gb7rdg-loadtest-reset.md`.
  - `.cutover-work/` — created **fresh** per run by the script (do NOT reuse claude-code's stale attempt-1 `.cutover-work`).
- **Toolchain**: .NET 10 SDK (10.0.109) present; `sync`'s BPQ importer (`tools/Bbs.Import.Bpq` → `bpq-import.dll`) builds + runs here.
- **Topology / access** (all reachable from studybox):
  - Proxmox host `root@10.45.0.10` → `pct exec 129` for the CT.
  - Old live LinBPQ box `tf@gb7rdg-node` (10.45.0.121), **passwordless sudo OK**.
  - WireGuard address **10.66.66.6** — the CT inherits it from the old box's `wg0.conf` at the `network` phase.
- **Latest-state discipline**: the mailbox dump (`sync`) and the WG identity (`network`) are pulled **fresh from the live old box** at run time — never from claude-code or stale artifacts.
- **kiss-collector MCP** (validation wire-truth) is claude.ai-hosted and independent of claude-code (verified live with claude-code down). The underlying collector is `gb7rdg-node`, reachable directly if ever needed.

## Live forwarding partners (validation target)

From 24h of kiss-collector traffic (2026-06-29; re-measure per-partner baselines at `baseline`). See runbook `validate` criterion 2.

| Partner | Path / band | Forwarding | ~24h frames |
|---|---|---|---|
| GB7BEX | direct, 70cm | FBB-over-AX.25 (clean) | ~480 |
| GB7BSK | 70cm | over NET/ROM (PID 0xCF), to GB7BSK-1 | ~600 |
| GB7OXF | 40m HF | slow + XID-heavy — the risk partner | ~250 |
| GB7CIP | via GB7WEM-7 | no callsign of its own on air — **watch the GB7WEM-7 link** | ~2,200 |

GB7LOX (the other attempt-1 multi-hop) shows zero 24h traffic → inactive. AXUDP forwarding partners are RF-invisible (validate separately).

## Key decisions / constraints in force

- **Real attempt-1 blocker was connect-script support, not mod-128.** Fixed by connect-scripts v2 (structured steps, `docs/connect-script-v2.md`, shipped 0.2.52). The mod-8/SABME fallback was verified present + default-on and is a non-blocker. See the attempt-1 retrospective's Correction.
- **The BPQ importer imports every partner DISABLED with a BLANK connect script** (v2 retired auto-translate). At `connect-test`, each partner to forward to must have its structured script **authored by Tom** (his step; do not pre-author). "kinder import" (auto-derive direct dials) is parked (would revise a deliberate decision).
- **AXUDP peer set CONFIRMED CURRENT** (2026-07-21): the held config's AXUDP peers match the live `gb7rdg-node:/etc/bpq32.cfg` port-8 (`PORTNUM=8`, `DRIVER=BPQAXIP`) active MAP entries exactly — GB7OUK/MB7NPW/GB7BDH (UDP 10094), GB7NDH (10095), M7TAW (10093); the commented-out `M0LTE-9`/`MB7NGP`/`M0LTE-3` are correctly omitted. No reconciliation needed. (A 2026-07-20 note here wrongly claimed "no MAP lines" — it grepped `/opt/oarc/bpq/bpq32.cfg`, which does not exist; the real node config is **`/etc/bpq32.cfg`**. `/opt/oarc/bpq` is the BPQMail *data* dir — the script's `BPQ_DIR`, used by `sync` for DIRMES/WFBID/linmail.cfg/Mail, not the node config.)
- `golive` is the point of no return (one-way; typed `GB7RDG GO`, ≥1 partner enabled). `abort` is valid only before `golive`. A re-cut always starts fresh from `freeze`.

## Resume from here (studybox) — attempt 3 is prepped and green

GB7RDG is live on LinBPQ; the CT is held, restaged (node 0.36.2 / pdn-bbs 0.2.53) and `preflight`-green as of 2026-07-31. A re-attempt is a fresh run from `freeze` (never reuse a stale sync):

```sh
cd ~/src/pdn-bbs
bash scripts/cutover-gb7rdg.sh status          # where both nodes stand
bash scripts/cutover-gb7rdg.sh preflight       # re-confirm green on the day
# then, on explicit go — freeze also disables LinBPQ + restarts kissproxy:
CUTOVER_YES=1 bash scripts/cutover-gb7rdg.sh freeze
# … sync → baseline → network → verify → connect-test → golive → validate
```

Env defaults already match this deployment (PVE `root@10.45.0.10`, CTID 129, `tf@gb7rdg-node`, `STAGE_DIR=~/gb7rdg-cutover`). See the script header for overrides.

## Partner reference (verified 2026-07-21 — for the next `connect-test`)

> **Port handling changed the same day — all three bugs fixed + released.** The Dial `port` is now a **pdn port id (name)**, matched case-insensitively against the node's configured port ids (node-v0.36.x `SupervisorRhpGateway.ResolvePortId`); a bad value gives a visible `No such port 'X' (<list>)`, and a null port errors on an outbound dial — no more silent misroute. **So use the port NAME (`hf-40m`)** — what was originally typed. The earlier "use numeric `3`" applied only to node ≤0.35.0 and is superseded. **Stage `node-v0.36.2` + `pdn-bbs 0.2.53`** for the re-attempt. ⚠️ **`node-v0.36.1` is NOT enough** — 0.36.1 carries [#664](https://github.com/packet-net/packet.net/issues/664) + [#665](https://github.com/packet-net/packet.net/issues/665) only; the port-**name** resolution (#91/[#668](https://github.com/packet-net/packet.net/pull/668)) landed in **0.36.2**, whose `ResolvePortId` matches port ids case-insensitively (0.36.1's is `int.TryParse` 1..N and rejects `hf-40m` outright).

Re-checked against the live `gb7rdg-node:/opt/oarc/bpq/linmail.cfg` + `/etc/bpq32.cfg`. Translate each BPQ port number to its pdn port **id**:

| BPQ port | band | pdn `port` (id) |
|---|---|---|
| 1 | 2m | `vhf-2m` |
| 2 | 70cm | `uhf-70cm` |
| 3 | 40m | `hf-40m` |
| 6 | 6m | `vhf-6m` |
| 8 | AXIP | `axudp-10093` / `-10094` / `-10095` |
| 9 | "simulated RF link to pdn" | — *(no pdn equivalent)* |

Enabled partners in the live config → Dial port + disposition:

| Partner | BPQ connect script | pdn Dial `port` | Auto-imports? | Notes |
|---|---|---|---|---|
| GB7CIP | `C 3 !GB7WEM-7` → `C uhf gb7cip` | `hf-40m` | yes | multi-hop via GB7WEM-7 (URONode `=> ` → `C uhf gb7cip`) |
| GB7OXF | `NC 3 !GB7OXF-2` | `hf-40m` | yes | direct 40m; the marginal/XID-heavy one |
| GB7BSK | `NC 2 !GB7BSK-1` | `uhf-70cm` | yes | direct 70cm |
| GB7BPQ | `NC 3 !GB7BPQ` | `hf-40m` | yes | direct 40m |
| GB7LOX | `NC 3 !GB7LOX-2` → `bbs` | `hf-40m` | yes | multi-hop via GB7LOX-2 |
| **EI0RSI (RSI)** | `C 3 EI0RSI-1` | `hf-40m` | yes | **replaces EI5IYB** |
| GB7NDH | `C NDHBBS` | — (NET/ROM alias) | yes | AXUDP peer 10.66.66.10 |
| M9YYY | `C 9 !M9YYY-1` | — ⚠️ port 9 = sim-link-to-pdn | yes | no real pdn path — drop or re-route |
| EI5IYB | `NC 3 EI5IYB-1` | — | **no** (skipped) | superseded → use EI0RSI |
| GB7MNK | `C GB7MNK` | — (NET/ROM alias) | **no** (skipped) | add manually if wanted |
| GB7BRK | `c gb7wod` → `c gb7brk` → `bbs` | via GB7WOD (`uhf-70cm`) | **no** (skipped) | multi-hop; add manually if wanted |

Decisions for the re-attempt:
- **EI5IYB → EI0RSI (RSI)**: use EI0RSI, drop EI5IYB (same Irish relationship; EI0RSI imports, EI5IYB doesn't).
- **M9YYY**: BPQ port 9 is the cutover's own "simulated RF link to pdn" test link — no real pdn route. Drop it or give it a real path.
- **Skipped by importer** (no `F_BBS` user): EI5IYB, GB7MNK, GB7BRK → won't auto-import; add manually if wanted (EI5IYB → EI0RSI).
- **NET/ROM-alias partners** (GB7NDH `NDHBBS`, GB7MNK, GB7BRK-via-GB7WOD): the RHP `open` is direct-AX.25-on-a-port, **not** NET/ROM-routed — these need a NET/ROM connect approach worked out in test-connect, not a plain port dial.
- GB7BEX is `en=0` (disabled) in BPQ — not a forwarding partner either direction (supersedes the earlier "add GB7BEX" note).

## Attempt-3 prep (2026-07-31) — DONE

1. **CT restaged** to node **0.36.2** + pdn-bbs **0.2.53** (debs downloaded to `$STAGE_DIR/debs/{node,bbs}`, SHA256-verified, installed together). Came back held: 7 ports `enabled=false`, forwarding HELD, healthz 200, schema v2, no errors.
2. **`preflight` re-run GREEN** on the new pair, and its version gates raised to node ≥ 0.36.2 / pdn-bbs ≥ 0.2.53 (hard `die`, not `warn`) so the wrong pair cannot be staged again.
3. **`freeze` hardened** (Tom's call, 2026-07-31) — it now, after LinBPQ is confirmed inactive:
   - **`systemctl disable linbpq`** — a stop alone survives only to the next reboot of the old box, after which LinBPQ would return and dual-claim GB7RDG. `abort` re-enables it (added to the abort path too).
   - **`systemctl restart kissproxy`** — kissproxy (8910-8913) is the serial↔TCP KISS bridge *both* nodes dial. Suspected latent connection bug: LinBPQ's client sessions have just dropped, and a stale/half-open session on kissproxy's side is the prime suspect for the 8910/8913 flapping at attempt 2. Restarting after LinBPQ is down and before the CT connects at `network` hands the CT a clean bridge. Verified active + listening on `0.0.0.0:8910` before the phase returns.
4. **Dial ports are port NAMES** (`hf-40m`, `uhf-70cm`, …) — confirmed against the node's live port ids. The earlier "1-indexed numeric" instruction is **superseded**; do not use numbers.

## Still open going into attempt 3

- **Why were the 2m/6m KISS-TCP links (8910/8913) flapping?** Not root-caused. #664 fixed the *symptom* (the pacer's `ObjectDisposedException` on a disposed `KissTcpClient`); nobody established why the links dropped in the first place, or whether those TNCs/radios were even attached. The `freeze` kissproxy restart is a precaution against a latent kissproxy-side connection bug, **not** a diagnosis. Watch 8910/8913 closely at `network`/`verify`.
- **Filed issues — all FIXED + released 2026-07-21:** [pdn-bbs #91](https://github.com/packet-net/pdn-bbs/issues/91) → pdn-bbs 0.2.53 (port passed verbatim); [packet.net #664](https://github.com/packet-net/packet.net/issues/664) (pacing retry on reconnect) + [#665](https://github.com/packet-net/packet.net/issues/665) (no silent first-port) → node-v0.36.1; #91/[#668](https://github.com/packet-net/packet.net/pull/668) (accept port ids by name) → **node-v0.36.2**.

## Next action

**Re-attempt = a fresh run from `freeze`** (never reuse a stale `sync`), then `sync → baseline → network → verify → connect-test → golive → validate`. For `connect-test`, use the **Partner reference** above — port **names**, EI5IYB→EI0RSI, plus the M9YYY / skipped-import / NET-ROM-alias flags.
