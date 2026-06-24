# GB7RDG cutover runbook

The gated, phased procedure for moving **GB7RDG** from its live LinBPQ node to the new pdn + pdn-bbs node (Proxmox CT `gb7rdg.lan`). Driven by [`scripts/cutover-gb7rdg.sh`](../scripts/cutover-gb7rdg.sh). Read [`docs/bpq-import.md`](bpq-import.md) first — this runbook is the whole-node cutover; the mailbox import is one phase of it.

## The model

This is **one-way**. Once the new node forwards mail on-air, the network has advanced past stopped LinBPQ and there is **no rollback**. So the cutover is split into phases you run **one at a time**, each verifying before it returns, with a **safe-abort window** up to (but not including) `golive`. Nothing before `golive` moves mail — forwarding stays HELD in both directions until the final, gated flip.

The mail sync is a **deterministic rebuild**, not an incremental delta: `sync` takes a fresh dump from the *stopped* LinBPQ and rebuilds `bbs.db` from scratch (the rehearsal db is discarded). The dump's `forw`/`fbbs` bitmaps pre-mark what BPQ already sent (never re-sent) vs queued (forwarded once live), and the full `WFBID.SYS` seeds the dedup store — this is what preserves no-duplicate-transfer.

## Prerequisites (do these BEFORE starting)

- **CT staged + held**: `node-v0.22.0`+ and `pdn-bbs 0.2.36`+ installed; the GB7RDG node config + `bbs.yaml` applied and fully HELD (ports `enabled:false`, `oarc.enabled:false`, `forwarding.enabled:false`, `housekeeping` set to BPQ's `MaxAge` — all `*Days: 365`, `bidLifetimeDays: 60`). `preflight` asserts all of this.
- **kissproxy** on the old box rebound to `0.0.0.0` (so the CT can reach the modems): `/etc/kissproxy.conf` `anyHost: true` for each modem.
- **WireGuard inherit**: the CT has `wireguard-tools` + `/dev/net/tun`; the old box's `wg0.conf` (key + `Address 10.66.66.6`) is readable. The CT will *inherit* that identity so the WG AXUDP peers need no MAP change.
- **Tailscale** (optional, non-blocking): the only Tailscale AXUDP peer (M7TAW) has been offline for weeks; a tailscale auth key at `$STAGE_DIR/tailscale.authkey` is used if present, otherwise the join is skipped.
- **Private config** lives under `$STAGE_DIR` (default `~/gb7rdg-cutover`): `packetnet.yaml` (held) + `bbs.yaml` (held). The script is generic; these hold the GB7RDG-specific WG/AXUDP addresses and stay out of the repo.

## Phases

Run each, read the result, then run the next. All targets are env-overridable (see the script header).

| Phase | What it does | Reversible? |
|---|---|---|
| `preflight` | Read-only assertions: held config correct (forwarding held, housekeeping ≥ 365), old box reachable + LinBPQ up + kissproxy on 0.0.0.0, CT up + wg-capable + healthy. **Changes nothing.** | n/a |
| `freeze` | Stops LinBPQ on the old box → GB7RDG off-air, mail files frozen consistent. | yes (`abort`) |
| `sync` | Pulls an **atomic** dump (single tar, re-asserts LinBPQ stayed stopped), fail-closed orphan-header gate, rebuilds `bbs.db`, loads it into the CT (keeps `bbs.db.pre-cutover`), forwarding still HELD. | yes (`abort`) |
| `baseline` | Snapshots the held mailbox + node/bbs versions to `baseline.env` for `validate` to diff. **Run after `sync`, before `golive`.** Also prompts you to capture the RF wire baseline via the kiss-collector MCP. | n/a |
| `network` | WireGuard handover (old `wg0` down → CT `wg0` up as `10.66.66.6`; **auto-restores the old wg if the CT bring-up fails**), asserts the CT actually owns `10.66.66.6` + has a handshake, then enables the RF/AXUDP ports (oarc + forwarding STILL held). GB7RDG on-air as the pdn node. | yes (`abort`) |
| `verify` | Read-only: `/healthz`, CT wg == `10.66.66.6`, kissproxy modem connections, a live AXUDP peer reachable, recent node log. | n/a |
| `golive` | **POINT OF NO RETURN.** Re-runs the load-bearing readiness checks *hard* (healthz, wg address, modem links, currently-held), requires typing `GB7RDG GO`, then enables forwarding + OARC; records the golive timestamp. Mail starts moving. | **NO** |
| `validate` | Post-golive: re-checks node/bbs/db against `baseline.env` and prints **pass/fail** (on-air, wg==10.66.66.6, modems, live AXUDP peers, forwarding ACTIVE, BID-store-grows, queue-draining, high-water-carried, message-count-not-collapsing), reports the re-flood signal from the logs, and emits the **RF wire-truth checklist** to run via the kiss-collector MCP. Run at **T+15 m / T+1 h / T+24 h**. | n/a |
| `abort` | Valid only before `golive`. Re-holds the CT, restores `bbs.db.pre-cutover`, verifies the CT wg is down (no dual-claim) before bringing the old box's wg + LinBPQ back on-air. | — |
| `status` | Shows where both nodes stand. | — |

After `golive`: **keep the old LinBPQ stopped (decommissioned)** so two stations never both claim GB7RDG. A re-cutover (if ever needed before golive) must start again from `freeze` — never reuse a stale import.

## Watching it (logging)

The pdn-bbs forwarding path is well-instrumented at **Information** — at `golive` you'll see the partner being dialled, per-message forward verdicts (sent / already-held-by-BID / deferred), and (from `pdn-bbs 0.2.37`+) a `forwarding ACTIVE: N partners, M queued` line and per-cycle drain summaries. Watch with `journalctl -u packetnet -f` (the bbs app logs through the node's journal).

For deeper detail during the cutover:

- **pdn-bbs wire/route detail** → raise `Bbs.Host.Forwarding` to `Debug`. The bbs app picks up `Logging__LogLevel__Bbs.Host.Forwarding=Debug` from its environment/appsettings.
- **pdn node AXUDP peer liveness / NET-ROM** → from `node-v0.23.0`+ the AXUDP multipoint transport logs per-peer send/heard (so you can see which of GB7NDH/GB7BDH are exchanging). The node has no live log-level switch yet, so to raise it to `Debug` add a systemd drop-in **while still HELD** (before `network`), then remove it after `golive`:

  ```
  systemctl edit packetnet      # add:
  #   [Service]
  #   Environment=Logging__LogLevel__Packet.Node.Core.Transports=Debug
  #   Environment=Logging__LogLevel__Packet.Node.Core.NetRom=Debug
  systemctl restart packetnet    # safe to restart while HELD (off-air, forwarding held)
  ```

  Remove the drop-in + restart after cutover to return to Information.

## Success criteria (what `validate` confirms)

Validate at **T+15 m** (takeover + no immediate re-flood), **T+1 h** (drain + link continuity), **T+24 h** (steady-state parity). The bash-reachable criteria are checked by `cutover-gb7rdg.sh validate`; the RF wire-truth is checked by hand against the kiss-collector MCP (gb7rdg-node *is* the collector, so it sees GB7RDG's actual RF on all four bands).

**1. Takeover & single identity** *(RF, MCP)* — TX from GB7RDG (+ -1/-2/-4 SSIDs) appears within ~15 min on the bands that were active (esp. 40m + 70cm), ID/BEACON/NODES resume (~15 min cadence), and there is **exactly one** GB7RDG on air (the old LinBPQ is stopped — no dual-claim).

**2. No duplicate re-flood — the critical one** *(logs + MCP)* — outbound mail volume per RF partner stays in the baseline band (~100–200 frames/24 h each), **not** an order-of-magnitude spike. `validate` reports the log signal (bodies forwarded vs partner BID-rejects on the drain); a healthy drain rejects the backlog dups. The `forwarding ACTIVE: N partners, M queued` line shows M = the pre-marked queue, not the whole mailbox.

**3. Mail flows + no loss** *(bbs.db, `validate`)* — queue **draining** (queued ≤ baseline), **BID store not collapsing** (≥ 90 % of baseline — it churns: new BIDs arrive while 60-day-expired orphans prune, so it doesn't grow monotonically, but it must never be wiped), high-water carried (≥ baseline), message count not collapsing (≥ 80 % of baseline).

**4. AXUDP** *(node logs, `validate`)* — GB7NDH + GB7BDH reachable + exchanging (Debug per-peer logs); GB7OUK/MB7NPW/M7TAW staying silent is **expected**, not a failure.

**5. Channel health** *(MCP)* — per-band `channel_wait_ms` + airtime stay in the baseline ballpark (40m ~2 s, 70cm ~180 ms); near-zero waits (CSMA off → collisions) or a REJ/SREJ storm = a config FAIL.

**6. Services & map** — telnet/RHP/IMAP/SMTP up; BBS/chat/dapps answer their SSIDs; the OARC map shows **one** GB7RDG.

Because `golive` is no-rollback, this is *confirm-success-or-damage-control*: if criterion 2 trips, the response is `forwarding.enabled:false` again + investigate (not a rollback) — which is why the pre-golive readiness gate is hard.

## Post-cutover

- Confirm forwarding drains to the live AXUDP peers (GB7NDH, GB7BDH) and any RF partners; the dead peers (GB7OUK, MB7NPW, M7TAW) will simply not connect — that's a remote-side condition, not a fault here.
- Confirm the OARC map shows the pdn GB7RDG (single entry).
- Remove the Debug log drop-in.
- Leave the old LinBPQ stopped + disabled.
