# GB7RDG cutover — state of play

**Banked:** 2026-07-01 · **Re-prepped:** 2026-07-20 · **CUTOVER IN PROGRESS:** 2026-07-21 ~09:01 UTC — ran `freeze → sync → baseline → network → verify` (all clean). **GB7RDG is now LIVE on the pdn node (CT 129), forwarding HELD; old LinBPQ is STOPPED.** Holding pre-`golive` — still reversible via `abort`. Next: `connect-test` (author + enable partners), then `golive` (typed `GB7RDG GO`).

Living operational snapshot of the in-progress GB7RDG LinBPQ→pdn cutover. Read alongside [`cutover-gb7rdg.md`](cutover-gb7rdg.md) (the runbook) and [`cutover-gb7rdg-attempt1.md`](cutover-gb7rdg-attempt1.md) (attempt-1 retrospective + the corrected diagnosis). Update this in place as phases complete.

## Where we are (phase ladder)

`preflight ✅ → freeze ✅ → sync ✅ → baseline ✅ → network ✅ → verify ✅ → connect-test ⬜ → golive ⬜ (HOLD HERE — reversible up to this line) → validate ⬜`

- **CT 129 (`gb7rdg.lan` / 10.45.0.87) is staged**: `packetnet` **0.35.0** + `pdn-bbs` **0.2.52** installed (canonical release debs, checksum-verified). Node held — all 7 ports `enabled=false` (4 RF + 3 AXUDP), `oarc.enabled=false`, healthz 200. Off-air; no dual-claim. Node 0.35.0 pulls a new `libhamlib-utils` dependency (resolved by `apt-get -f install`) and migrates the persisted config schema **v1→v2** on first start; the held `schemaVersion: 1` YAML was re-imported cleanly under 0.35.0 (validated — so `network`'s `ct_apply` import is de-risked).
- **`preflight` GREEN** (re-confirmed 2026-07-20 on node 0.35.0): all `[ok]`, one expected non-blocking warning (no tailscale key — M7TAW is dead).
- **GB7RDG is now LIVE on the pdn node (CT 129)** — WireGuard `10.66.66.6` moved to the CT, all 7 ports enabled, node transmitting on-air as `Packet.NET 0.35.0` (collector-confirmed: `Welcome to READNG (GB7RDG)` TX on 70cm; XID/SABM/UA with GB7WOD/GB7WEM-7; correctly declining inbound FBB while forwarding HELD). **Old LinBPQ on `gb7rdg-node` is STOPPED, its wg down — no dual-claim.** Mailbox rebuilt fresh (190 msgs, **0 orphan headers**; baseline msgs=169, bids=1313, high-water=1976, queued=1249, sent=660). **No mail forwarded yet — `golive` not reached — still fully reversible.**
- **Rollback (until `golive`)**: `cd ~/src/pdn-bbs && CUTOVER_YES=1 bash scripts/cutover-gb7rdg.sh abort` — re-holds the CT, restores `bbs.db.pre-cutover`, drops the CT wg (verifies down first), brings LinBPQ back on-air. **Complete instead**: `connect-test` (author+enable partners in the BBS UI), then `golive` (types `GB7RDG GO`; ≥1 partner must be enabled). ⚠️ `CUTOVER_YES=1` bypasses `golive`'s typed gate — never set it on a `golive` run.

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

## Resume from here (studybox) — mid-cutover, holding pre-`golive`

`freeze → verify` are DONE; GB7RDG is on-air on the CT, forwarding HELD. From here it's either roll back or complete:

```sh
cd ~/src/pdn-bbs
# ROLL BACK (valid only before golive) — restores LinBPQ as GB7RDG:
CUTOVER_YES=1 bash scripts/cutover-gb7rdg.sh abort
# or COMPLETE:
#   1. connect-test — author each partner's structured connect script + Enable
#      the ones to forward to, in the BBS sysop UI (moves no mail). >=1 required.
bash scripts/cutover-gb7rdg.sh connect-test        # prints the partner list + guidance
#   2. golive — POINT OF NO RETURN; types 'GB7RDG GO'. Do NOT set CUTOVER_YES=1 here.
bash scripts/cutover-gb7rdg.sh golive
#   3. validate at T+15m / T+1h / T+24h.
```

Env defaults already match this deployment (PVE `root@10.45.0.10`, CTID 129, `tf@gb7rdg-node`, `STAGE_DIR=~/gb7rdg-cutover`). See the script header for overrides.

## Immediate next actions

1. **HOLDING pre-`golive`** — GB7RDG live on the pdn node, forwarding HELD, rollback (`abort`) still available.
2. `connect-test` — **Tom's step**: author each partner's connect script + enable (in the BBS UI). Note the importer skipped GB7BEX (no F_BBS user in linmail.cfg) though it's an active RF partner — add it here if wanted.
3. `golive` — irreversible; Tom's typed `GB7RDG GO`.
