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
| `network` | WireGuard handover (old `wg0` down → CT `wg0` up as `10.66.66.6`; **auto-restores the old wg if the CT bring-up fails**), asserts the CT actually owns `10.66.66.6` + has a handshake, then enables the RF/AXUDP ports (oarc + forwarding STILL held). GB7RDG on-air as the pdn node. | yes (`abort`) |
| `verify` | Read-only: `/healthz`, CT wg == `10.66.66.6`, kissproxy modem connections, a live AXUDP peer reachable, recent node log. | n/a |
| `golive` | **POINT OF NO RETURN.** Re-runs the load-bearing readiness checks *hard* (healthz, wg address, modem links, currently-held), requires typing `GB7RDG GO`, then enables forwarding + OARC. Mail starts moving. | **NO** |
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

## Post-cutover

- Confirm forwarding drains to the live AXUDP peers (GB7NDH, GB7BDH) and any RF partners; the dead peers (GB7OUK, MB7NPW, M7TAW) will simply not connect — that's a remote-side condition, not a fault here.
- Confirm the OARC map shows the pdn GB7RDG (single entry).
- Remove the Debug log drop-in.
- Leave the old LinBPQ stopped + disabled.
