# GB7RDG cutover — state of play

**Banked:** 2026-07-01 · **Re-prepped:** 2026-07-20 · **Attempt 2 — ROLLED BACK 2026-07-21.** Ran `freeze→sync→baseline→network→verify` clean; at the pre-`golive` hold a test-connect dialled on the wrong RF port, so `abort` was run. **Root cause VERIFIED** (see Attempt-2 note): a connect-script Dial `port` of `hf-40m` (a name) is non-numeric → dropped → the node dialled the *first* port (vhf-2m) instead of 40m; correct value is `3`. **GB7RDG is live again on LinBPQ; the CT is held; no dual-claim; no mail moved (`golive` never reached).** Fix the port values + address the filed issues, then re-cut fresh from `freeze`.

Living operational snapshot of the in-progress GB7RDG LinBPQ→pdn cutover. Read alongside [`cutover-gb7rdg.md`](cutover-gb7rdg.md) (the runbook) and [`cutover-gb7rdg-attempt1.md`](cutover-gb7rdg-attempt1.md) (attempt-1 retrospective + the corrected diagnosis). Update this in place as phases complete.

## Where we are (phase ladder)

Attempt 2 ran `preflight ✅ → freeze ✅ → sync ✅ → baseline ✅ → network ✅ → verify ✅`, then **`abort` ✅ — rolled back to LinBPQ** at the pre-`golive` hold. `connect-test` / `golive` / `validate` were NOT reached. A re-attempt starts fresh from `freeze`.

- **CT 129 (`gb7rdg.lan` / 10.45.0.87) is staged**: `packetnet` **0.35.0** + `pdn-bbs` **0.2.52** installed (canonical release debs, checksum-verified). Node held — all 7 ports `enabled=false` (4 RF + 3 AXUDP), `oarc.enabled=false`, healthz 200. Off-air; no dual-claim. Node 0.35.0 pulls a new `libhamlib-utils` dependency (resolved by `apt-get -f install`) and migrates the persisted config schema **v1→v2** on first start; the held `schemaVersion: 1` YAML was re-imported cleanly under 0.35.0 (validated — so `network`'s `ct_apply` import is de-risked).
- **`preflight` GREEN** (re-confirmed 2026-07-20 on node 0.35.0): all `[ok]`, one expected non-blocking warning (no tailscale key — M7TAW is dead).
- **GB7RDG is live on LinBPQ again** (`gb7rdg-node`, 10.45.0.121; `linbpq` active, `wg0` up as `10.66.66.6` with a fresh handshake). **The CT (129) is held** — all 7 ports `enabled=false`, `forwarding_master=0`, all partners disabled, `bbs.db.pre-cutover` restored, CT wg down (0 interfaces). **Single identity, no dual-claim.** Attempt 2 reached the pre-`golive` hold (mailbox had rebuilt fresh: 190 msgs, **0 orphan headers**) but **no mail ever forwarded** (`golive` not reached) — LinBPQ's mailbox is intact.
- **Attempt-2 root cause (2026-07-21, VERIFIED):** a **connect-script port-config error**, plus an unstable transport that hid it. The GB7CIP test-connect Dial step was `{"open":"GB7WEM-7","port":"hf-40m"}`. The Dial `port` field requires a **1-indexed numeric** port label; `hf-40m` (a port *name*) is silently dropped to null (pdn-bbs `ConnectScript.cs:117-124`), and the node then dials on the **first** configured port = `vhf-2m` (packet.net `SupervisorRhpGateway.cs:77-82`). Confirmed on the wire: the connect ran **80.07 s** = vhf-2m's `T1 4000 ms × N2 20` (hf-40m would be 56 s). So the dial was **misrouted to 2m** — the monitor showing `vhf-2m` was *correct*, not a display bug (an earlier hypothesis of mine, retracted). The correct value is **`3`** (= hf-40m, 3rd configured port; matches LinBPQ's `C 3 !GB7WEM-7`). Separately, the vhf-2m/vhf-6m KISS-TCP links (8910/8913) were flapping and the ACKMODE pacer faulted with `ObjectDisposedException` on the disposed `KissTcpClient` (`PacingKissModem`), so the misrouted 2m dial never reliably reached the air (zero 2m TX in the collector; 2m radio didn't key — the HF PTT the operator saw was concurrent *inbound* sessions on hf-40m). Issues filed: [pdn-bbs #91](https://github.com/packet-net/pdn-bbs/issues/91) (port-reference UX), [packet.net #664](https://github.com/packet-net/packet.net/issues/664) (ObjectDisposedException-on-reconnect), [packet.net #665](https://github.com/packet-net/packet.net/issues/665) (node silently defaults to the first port). **Before re-attempt:** set each partner's Dial `port` to its numeric label (GB7WEM-7/GB7CIP = `3`); re-cut fresh from `freeze`.

## Execution environment (IMPORTANT — not claude-code)

The cutover is driven from **studybox (the primary working host), NOT `claude-code`.** claude-code was a single point of failure that dropped mid-session and is now shut down; all dependence on it has been broken. Everything needed is local or pulled from the live node at run time:

- **Script**: `~/src/pdn-bbs/scripts/cutover-gb7rdg.sh` (this repo; also on GitHub).
- **`STAGE_DIR` = `~/gb7rdg-cutover/`** (local), containing:
  - `packetnet.yaml` + `bbs.yaml` — the authoritative **held** config (node ports/oarc HELD; bbs forwarding HELD + housekeeping 365/60). Originally authored on claude-code; now local.
  - `debs/node/packetnet_0.26.0_amd64.deb`, `debs/bbs/pdn-bbs_0.2.52_amd64.deb` (+ SHA256SUMS) — from GitHub releases, verified.
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

## Resume from here (studybox) — attempt 2 rolled back; re-attempt after offline testing

GB7RDG is live on LinBPQ; the CT is held. Do NOT re-cut until the RF/port-mapping symptom (above) is resolved offline. A re-attempt is a fresh run from `freeze` (never reuse a stale sync):

```sh
cd ~/src/pdn-bbs
bash scripts/cutover-gb7rdg.sh status          # where both nodes stand
bash scripts/cutover-gb7rdg.sh preflight       # re-confirm green before any re-attempt
# then, only once the port issue is fixed + on explicit go:
CUTOVER_YES=1 bash scripts/cutover-gb7rdg.sh freeze
# … sync → baseline → network → verify → connect-test → golive → validate
```

Env defaults already match this deployment (PVE `root@10.45.0.10`, CTID 129, `tf@gb7rdg-node`, `STAGE_DIR=~/gb7rdg-cutover`). See the script header for overrides.

## Partner reference (verified 2026-07-21 — for the next `connect-test`)

Re-checked against the live `gb7rdg-node:/opt/oarc/bpq/linmail.cfg` + `/etc/bpq32.cfg`. **The Dial `port` is a 1-indexed pdn-config-order label, NOT the BPQ port number** — they coincide only for 2m/70cm/40m:

| BPQ port | band | pdn Dial `port` |
|---|---|---|
| 1 | 2m | `1` |
| 2 | 70cm | `2` |
| 3 | 40m | `3` |
| 6 | 6m | `4` *(differs)* |
| 8 | AXIP | 5/6/7 (axudp) |
| 9 | "simulated RF link to pdn" | — *(no pdn equivalent)* |

Enabled partners in the live config → Dial port + disposition:

| Partner | BPQ connect script | pdn Dial `port` | Auto-imports? | Notes |
|---|---|---|---|---|
| GB7CIP | `C 3 !GB7WEM-7` → `C uhf gb7cip` | `3` | yes | multi-hop via GB7WEM-7 (URONode `=> ` → `C uhf gb7cip`) |
| GB7OXF | `NC 3 !GB7OXF-2` | `3` | yes | direct 40m; the marginal/XID-heavy one |
| GB7BSK | `NC 2 !GB7BSK-1` | `2` | yes | direct 70cm |
| GB7BPQ | `NC 3 !GB7BPQ` | `3` | yes | direct 40m |
| GB7LOX | `NC 3 !GB7LOX-2` → `bbs` | `3` | yes | multi-hop via GB7LOX-2 |
| **EI0RSI (RSI)** | `C 3 EI0RSI-1` | `3` | yes | **replaces EI5IYB** |
| GB7NDH | `C NDHBBS` | — (NET/ROM alias) | yes | AXUDP peer 10.66.66.10 |
| M9YYY | `C 9 !M9YYY-1` | — ⚠️ port 9 = sim-link-to-pdn | yes | no real pdn path — drop or re-route |
| EI5IYB | `NC 3 EI5IYB-1` | — | **no** (skipped) | superseded → use EI0RSI |
| GB7MNK | `C GB7MNK` | — (NET/ROM alias) | **no** (skipped) | add manually if wanted |
| GB7BRK | `c gb7wod` → `c gb7brk` → `bbs` | via GB7WOD (70cm = `2`) | **no** (skipped) | multi-hop; add manually if wanted |

Decisions for the re-attempt:
- **EI5IYB → EI0RSI (RSI)**: use EI0RSI, drop EI5IYB (same Irish relationship; EI0RSI imports, EI5IYB doesn't).
- **M9YYY**: BPQ port 9 is the cutover's own "simulated RF link to pdn" test link — no real pdn route. Drop it or give it a real path.
- **Skipped by importer** (no `F_BBS` user): EI5IYB, GB7MNK, GB7BRK → won't auto-import; add manually if wanted (EI5IYB → EI0RSI).
- **NET/ROM-alias partners** (GB7NDH `NDHBBS`, GB7MNK, GB7BRK-via-GB7WOD): the RHP `open` is direct-AX.25-on-a-port, **not** NET/ROM-routed — these need a NET/ROM connect approach worked out in test-connect, not a plain port dial.
- GB7BEX is `en=0` (disabled) in BPQ — not a forwarding partner either direction (supersedes the earlier "add GB7BEX" note).

## Immediate next actions

1. **ROLLED BACK** — GB7RDG live on LinBPQ, CT held, no mail moved. Nothing running on the node right now.
2. **Fix the connect-script Dial ports** — each partner's `port` must be a **1-indexed numeric** label (GB7WEM-7/GB7CIP = `3`), not a port name. Verified root cause of the abort (see Attempt-2 note).
3. **Track the filed issues** — [pdn-bbs #91](https://github.com/packet-net/pdn-bbs/issues/91) (make port refs robust: picker / accept names / don't silent-drop), [packet.net #664](https://github.com/packet-net/packet.net/issues/664) (`ObjectDisposedException` on KISS-TCP reconnect), [packet.net #665](https://github.com/packet-net/packet.net/issues/665) (don't default to the first port). Also worth checking why the 2m/6m KISS-TCP links (8910/8913) were flapping — were those TNCs/radios attached?
4. **Re-attempt** (after the above): fresh `freeze → … → connect-test → golive → validate`. For `connect-test`, use the **Partner reference** above — numeric Dial ports, EI5IYB→EI0RSI, and the M9YYY / skipped-import / NET-ROM-alias flags.
