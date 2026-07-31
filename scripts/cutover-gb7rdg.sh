#!/usr/bin/env bash
#
# cutover-gb7rdg.sh — the GATED, PHASED GB7RDG LinBPQ -> pdn cutover runbook.
#
# This is the WHOLE-NODE cutover (mail + AX.25 node + AXUDP + WireGuard), of which the
# mail sync (scripts/migrate-gb7rdg.sh) is one phase. It is CORRECTNESS-CRITICAL and
# ONE-WAY: once the new node forwards on-air, the network has advanced past stopped
# LinBPQ and there is NO rollback. So it is split into PHASES you run ONE AT A TIME,
# each verifying before it returns, with a SAFE-ABORT window up to (but not including)
# `golive`, which is gated behind hard readiness checks + a typed double-confirm.
#
# SAFE-ABORT WINDOW: everything from `freeze` up to `verify` is reversible — `abort`
# verifiably re-holds the CT, drops its WireGuard, restores the CT's pre-cutover mailbox,
# and brings the OLD node (LinBPQ + its wg) back on-air. The moment `golive` enables
# forwarding, mail starts moving and abort is no longer possible. Nothing before `golive`
# moves mail (forwarding stays HELD in both directions until then).
#
# PHASES (run in order; check each result before the next):
#   preflight  read-only: assert everything is staged + reachable + the held config is
#              correct (forwarding HELD, housekeeping >= BPQ MaxAge). Changes nothing.
#   freeze     stop + DISABLE LinBPQ on the old box -> GB7RDG off-air, mail files frozen, and it
#              cannot return on a reboot to dual-claim. Then restart kissproxy (the 8910-8913 KISS
#              bridge) now LinBPQ has let go, so the CT meets a clean bridge at `network`.
#   sync       pull the FROZEN dump (atomic) -> rebuild bbs.db -> load into the CT (HELD).
#   baseline   snapshot the held mailbox + node state (for `validate` to diff). Run after sync.
#   network    WireGuard handover (CT inherits 10.66.66.6, auto-rollback on failure) +
#              bring the CT's RF/AXUDP ports up -> GB7RDG on-air, mail STILL HELD.
#   verify     read-only: confirm on-air health (wg==10.66.66.6, modems, live peers).
#   connect-test the per-partner BRING-UP: forwarding is gated in TWO tiers now — the whole-BBS
#              MASTER switch (held) AND every partner imported DISABLED with a BLANK connect script
#              (connect-scripts v2 retired flat-form auto-translate). For each partner: AUTHOR its
#              structured connect script, test-connect (moves NO mail; 'Use as Wait-for' captures the
#              real prompt), then ENABLE it. Run after network, before golive. A disabled/blank-script
#              partner simply won't forward.
#   golive     >>> POINT OF NO RETURN <<< hard readiness re-check (incl. >=1 partner ENABLED, else
#              forwarding would dial no one) + typed confirm, then flip the store-backed MASTER ON +
#              enable OARC. Mail starts moving to the enabled partners.
#   validate   post-golive: re-check node/bbs/db vs baseline (pass/fail) + emit the RF
#              wire-truth checklist to run via the kiss-collector MCP. Run T+15m/+1h/+24h.
#   abort      reverse freeze/network/sync (valid ONLY before golive).
#   status     show where both nodes currently stand.
#
# CONFIG IS PRIVATE, SCRIPT IS GENERIC: the GB7RDG-specific node + bbs config (with the
# WireGuard / AXUDP peer addresses) lives OUTSIDE this public repo, under $STAGE_DIR;
# this script only carries the generic, env-driven mechanism. The HELD config is the
# source of truth; the network/live variants are generated from it at run time and the
# SEMANTIC diff (normalised, comments-stripped on both sides) is shown before applying.
#
# USAGE: scripts/cutover-gb7rdg.sh <phase>   (env-overridable targets below)

set -euo pipefail

# --- Targets (env-overridable) ----------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Old live LinBPQ box.
BPQ_SSH="${BPQ_SSH:-tf@gb7rdg-node}"          # sudo-capable login on the old node
BPQ_SUDO="${BPQ_SUDO:-sudo}"                  # how to escalate there
BPQ_DIR="${BPQ_DIR:-/opt/oarc/bpq}"           # BPQMail data dir (DIRMES/WFBID/Mail/linmail)
BPQ_SERVICE="${BPQ_SERVICE:-linbpq}"          # the systemd unit (mail engine + node)
KISSPROXY_SERVICE="${KISSPROXY_SERVICE:-kissproxy}"  # serial<->TCP KISS bridge (8910-8913); BOTH nodes dial it
BPQ_WG_IFACE="${BPQ_WG_IFACE:-wg0}"           # the WireGuard iface that holds 10.66.66.6
WG_ADDR="${WG_ADDR:-10.66.66.6}"              # the wg address the CT must inherit
WG_PROBE="${WG_PROBE:-10.66.66.10}"           # a known-LIVE AXUDP peer to ping (GB7NDH)

# New pdn node: a Proxmox CT reached via the host (NOT direct-SSH — survives CT net changes).
PVE_SSH="${PVE_SSH:-root@10.45.0.10}"         # the Proxmox host
CTID="${CTID:-129}"                           # the CT id (gb7rdg.lan / 10.45.0.87)
NODE_SVC="${NODE_SVC:-packetnet}"
NODE_BIN="${NODE_BIN:-/opt/packetnet/app/packetnet}"
NODE_DB="${NODE_DB:-/var/lib/packetnet/pdn.db}"
BBS_STATE="${BBS_STATE:-/var/lib/packetnet/apps/bbs}"
KISS_HOST="${KISS_HOST:-10.45.0.121}"         # old box LAN ip where the CT dials kissproxy

# Staging (PRIVATE — not in the repo). The held config is authoritative.
STAGE_DIR="${STAGE_DIR:-$HOME/gb7rdg-cutover}"
NODE_HELD="${NODE_HELD:-$STAGE_DIR/packetnet.yaml}"   # ports + oarc HELD
BBS_HELD="${BBS_HELD:-$STAGE_DIR/bbs.yaml}"           # forwarding HELD + housekeeping>=365
TAILSCALE_KEY="${TAILSCALE_KEY:-$STAGE_DIR/tailscale.authkey}"  # optional (M7TAW dead -> non-blocking)
MIN_LIFETIME_DAYS="${MIN_LIFETIME_DAYS:-365}"        # assert all msg lifetimes >= this (BPQ MaxAge)

# Local work area for the dump + rebuilt db + session markers.
WORK_DIR="${WORK_DIR:-$STAGE_DIR/.cutover-work}"
SNAP="${SNAP:-$WORK_DIR/snapshot}"
BUILT_DB="${BUILT_DB:-$WORK_DIR/bbs.db}"
SYNC_MARKER="${SYNC_MARKER:-$WORK_DIR/.synced}"       # proves a fresh sync preceded golive
BASELINE_FILE="${BASELINE_FILE:-$WORK_DIR/baseline.env}"  # pre-golive snapshot for `validate` to diff
GOLIVE_MARKER="${GOLIVE_MARKER:-$WORK_DIR/.golive-utc}"   # records the golive moment (validate uses it)

# --- helpers ----------------------------------------------------------------
die()  { echo "ERROR: $*" >&2; exit 1; }
note() { echo ">>> $*"; }
ok()   { echo "  [ok] $*"; }
warn() { echo "  [!!] $*" >&2; }

confirm() { [[ "${CUTOVER_YES:-0}" == "1" ]] && return 0; read -r -p "$1 [y/N] " a; [[ "$a" == y || "$a" == Y ]]; }

bpq()    { ssh "$BPQ_SSH" "$BPQ_SUDO bash -lc \"$1\""; }            # run as root on the old box
# True iff the old LinBPQ is STOPPED. NB: `systemctl is-active` EXITS 3 for an inactive unit, so the
# old `is-active | grep` form returned 3 under `set -o pipefail` (the pipe takes is-active's exit, not
# grep's match) — it could NEVER pass for a stopped service. Compare the OUTPUT string instead.
bpq_inactive() { [[ "$(bpq "systemctl is-active $BPQ_SERVICE" 2>/dev/null || true)" == inactive ]]; }
ct()     { ssh "$PVE_SSH" "pct exec $CTID -- bash -lc \"$1\""; }    # run in the CT (via the PVE host)
ct_push(){ # ct_push <local> <ct-path>  — temp file on the PVE host is always cleaned up
  scp -q "$1" "$PVE_SSH:/tmp/.cutover.$$" || die "scp to PVE host failed ($1)"
  ssh "$PVE_SSH" "pct push $CTID /tmp/.cutover.$$ '$2' --user 0 --group 0; rc=\$?; rm -f /tmp/.cutover.$$; exit \$rc" \
    || die "pct push to CT failed ($2)"
}

require_cmd() { command -v "$1" >/dev/null || die "missing required local command: $1"; }

# Assert a bbs.yaml's forwarding.enabled is exactly $1 (true|false). Parsed, not grepped.
assert_bbs_forwarding() { # assert_bbs_forwarding <true|false> <file>
  python3 - "$2" "$1" <<'PY' || die "bbs config $2: forwarding.enabled is not $3"
import sys, yaml
d = yaml.safe_load(open(sys.argv[1])) or {}
want = (sys.argv[2] == "true")
sys.exit(0 if d.get("forwarding", {}).get("enabled") is want else 1)
PY
}

# Assert every message-class lifetime in a bbs.yaml is >= MIN_LIFETIME_DAYS and bidLifetimeDays >= 60.
assert_housekeeping() { # assert_housekeeping <file>
  python3 - "$1" "$MIN_LIFETIME_DAYS" <<'PY' || die "bbs config $1: housekeeping does NOT preserve BPQ retention (mail-loss risk) — see message above"
import sys, yaml
d = yaml.safe_load(open(sys.argv[1])) or {}
mn = int(sys.argv[2])
hk = d.get("housekeeping")
if hk is None:
    print(f"  housekeeping block MISSING — pdn defaults (bulletins ~7d) would purge live mail.", file=sys.stderr); sys.exit(1)
msg_keys = ["personalReadDays","personalUnreadDays","personalForwardedDays","personalUnforwardedDays",
            "bulletinForwardedDays","bulletinUnforwardedDays","ntsDeliveredDays","ntsForwardedDays","ntsUnforwardedDays"]
bad = []
for k in msg_keys:
    v = hk.get(k)
    if v is None or int(v) < mn: bad.append(f"{k}={v} (need >= {mn})")
bid = hk.get("bidLifetimeDays")
if bid is None or int(bid) < 60: bad.append(f"bidLifetimeDays={bid} (need >= 60)")
if bad:
    print("  housekeeping keys too short / missing: " + "; ".join(bad), file=sys.stderr); sys.exit(1)
sys.exit(0)
PY
}

# Apply a node-config yaml (or "-") + a bbs.yaml (or "-") to the CT atomically; FAIL on a rejected import.
ct_apply() { # ct_apply <node-yaml-or-"-"> <bbs-yaml-or-"-">
  local nyaml="$1" byaml="$2" out
  [[ "$nyaml" != "-" ]] && ct_push "$nyaml" /var/lib/packetnet/.cutover-node.yaml
  [[ "$byaml" != "-" ]] && ct_push "$byaml" "$BBS_STATE/bbs.yaml"
  ct "systemctl stop $NODE_SVC"
  if [[ "$nyaml" != "-" ]]; then
    out="$(ct "DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/packetnet HOME=/var/lib/packetnet $NODE_BIN config import /var/lib/packetnet/.cutover-node.yaml --db $NODE_DB 2>&1")" \
      || { echo "$out" >&2; die "node config import FAILED (nonzero exit)"; }
    echo "$out"
    grep -qi 'rejected' <<<"$out" && die "node config import was REJECTED — see output above (NOT applied)"
    grep -qi 'Applied\|imported' <<<"$out" || die "node config import did not confirm 'Applied' — refusing to continue"
    ct "rm -f /var/lib/packetnet/.cutover-node.yaml"
  fi
  ct "chown -R packetnet:packetnet '$BBS_STATE' $NODE_DB* 2>/dev/null || true"
  ct "systemctl start $NODE_SVC"; sleep 5
  ct "systemctl is-active $NODE_SVC" | grep -qx active || die "node did not come back active after config apply"
}

# Generate a node-config variant from the HELD config (robust yaml transform). echoes the path.
# gen_node <ports_enabled true|false> <oarc_enabled true|false> <tailscale true|false>
gen_node() {
  local ports="$1" oarc="$2" tscale="$3" out="$WORK_DIR/node.p$1-o$2-ts$3.yaml"
  mkdir -p "$WORK_DIR"
  TS_KEY="$([[ "$tscale" == true && -f "$TAILSCALE_KEY" ]] && cat "$TAILSCALE_KEY" || echo '')" \
  python3 - "$NODE_HELD" "$out" "$ports" "$oarc" "$tscale" <<'PY'
import sys, os, yaml
src, out, ports, oarc, tscale = sys.argv[1:6]
d = yaml.safe_load(open(src))
for p in d.get("ports", []) or []:
    p["enabled"] = (ports == "true")
d.setdefault("oarc", {})["enabled"] = (oarc == "true")
if tscale == "true":
    t = d.setdefault("tailscale", {})
    t["enabled"] = True
    t.setdefault("stateDir", "/var/lib/packetnet/tsnet")
    t.setdefault("target", "127.0.0.1:8080")
    key = os.environ.get("TS_KEY", "")
    if key: t["authKey"] = key
yaml.safe_dump(d, open(out, "w"), sort_keys=False)
PY
  echo "$out"
}

gen_bbs() { # gen_bbs <forwarding true|false> -> path
  # NOTE: bbs.yaml's forwarding.enabled only SEEDS the store-backed master switch on FIRST start
  # (when the meta key is null). After the sync re-import has seeded it held, this value is IGNORED
  # — the live master is store-backed (HostComposition.cs: seed-if-null), so the runtime switch
  # survives restarts. Flip the master at runtime with ct_set_master (below), NOT by re-writing this.
  local fwd="$1" out="$WORK_DIR/bbs.fwd-$1.yaml"
  mkdir -p "$WORK_DIR"
  python3 - "$BBS_HELD" "$out" "$fwd" <<'PY'
import sys, yaml
src, out, fwd = sys.argv[1:4]
d = yaml.safe_load(open(src))
d.setdefault("forwarding", {})["enabled"] = (fwd == "true")
yaml.safe_dump(d, open(out, "w"), sort_keys=False)
PY
  echo "$out"
}

# The whole-BBS forwarding MASTER switch is store-backed (meta.forwarding_master), read LIVE by the
# scheduler + inbound answerer. The UI toggles it; for the scripted cutover we flip it deterministically
# in the store while the node is stopped (no auth, no race). gen_bbs(true) at golive would be a NO-OP
# (the meta key is already seeded held), which would silently leave forwarding held — hence this.
ct_set_master() { # ct_set_master <1|0>   (1 = forwarding ON, 0 = held)
  local v="$1"
  ct "systemctl stop $NODE_SVC; sleep 1
python3 - <<PY
import sqlite3
c=sqlite3.connect('$BBS_STATE/bbs.db')
c.execute(\"INSERT INTO meta(key,value) VALUES('forwarding_master','$v') ON CONFLICT(key) DO UPDATE SET value=excluded.value\")
c.commit(); c.close()
PY
chown packetnet:packetnet $BBS_STATE/bbs.db
systemctl start $NODE_SVC; sleep 5
systemctl is-active $NODE_SVC | grep -qx active" || die "node did not come back active after master flip"
}

# Count partners currently ENABLED in the store (the per-partner gate; importer lands all disabled).
ct_enabled_count() {
  ct "python3 -c \"import sqlite3; print(sqlite3.connect('file:$BBS_STATE/bbs.db?mode=ro',uri=True).execute('SELECT COUNT(*) FROM partners WHERE enabled=1').fetchone()[0])\"" 2>/dev/null | tr -dc '0-9'
}

# Show the SEMANTIC diff (both sides normalised through safe_dump) so comment/format noise is gone.
show_diff() { # show_diff <held-file> <generated-file>
  local norm="$WORK_DIR/.norm.$$"
  python3 -c 'import sys,yaml; yaml.safe_dump(yaml.safe_load(open(sys.argv[1])), open(sys.argv[2],"w"), sort_keys=False)' "$1" "$norm"
  diff -u "$norm" "$2" || true
  rm -f "$norm"
}

assert_ct_wg_addr() { ct "ip -4 addr show wg0 2>/dev/null | grep -qw $WG_ADDR"; }

# Read bbs.db counts from the CT (read-only) as key=value lines.
ct_bbs_counts() {
  ct "python3 - <<'PY'
import sqlite3
c=sqlite3.connect('file:$BBS_STATE/bbs.db?mode=ro',uri=True)
def q(s):
    try: return c.execute(s).fetchone()[0]
    except Exception: return -1
print('msgs='+str(q('SELECT COUNT(*) FROM messages')))
print('bids='+str(q('SELECT COUNT(*) FROM bids')))
print('partners='+str(q('SELECT COUNT(*) FROM partners')))
print('sent='+str(q('SELECT COUNT(*) FROM forwards WHERE forwarded_utc IS NOT NULL')))
print('queued='+str(q('SELECT COUNT(*) FROM forwards WHERE forwarded_utc IS NULL')))
print('highwater='+str(q('SELECT seq FROM sqlite_sequence WHERE name='+chr(39)+'messages'+chr(39))))
PY"
}
chk() { if eval "$2"; then ok "$1"; else warn "FAIL: $1"; FAILS=$((FAILS+1)); fi; }

# ============================================================================
phase="${1:-}"; [[ -n "$phase" ]] || { sed -n '2,46p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0; }
require_cmd python3; require_cmd dotnet; require_cmd ssh; require_cmd scp

case "$phase" in

preflight)
  note "PREFLIGHT (read-only) — nothing is changed."
  [[ -f "$NODE_HELD" ]] || die "missing held node config: $NODE_HELD"
  [[ -f "$BBS_HELD"  ]] || die "missing held bbs config: $BBS_HELD"
  ok "held config present"
  assert_bbs_forwarding false "$BBS_HELD" && ok "bbs held config: forwarding HELD"
  assert_housekeeping "$BBS_HELD" && ok "bbs held config: housekeeping preserves BPQ retention (all msg lifetimes >= $MIN_LIFETIME_DAYS, BID >= 60)"
  python3 -c 'import sys,yaml; d=yaml.safe_load(open(sys.argv[1])); sys.exit(0 if any(p.get("enabled") is False for p in (d.get("ports") or [])) else 1)' "$NODE_HELD" \
    && ok "node held config: ports held (enabled:false)" || warn "node held config: no held ports?"
  # Old box
  bpq "systemctl is-active $BPQ_SERVICE" | grep -qx active && ok "old LinBPQ running" || warn "old LinBPQ not active?"
  bpq "ss -ltn | grep -qE '0\.0\.0\.0:8910'" && ok "kissproxy bound 0.0.0.0 (CT-reachable)" || die "kissproxy NOT bound 0.0.0.0 — rebind (anyHost:true) first"
  bpq "test -r /etc/wireguard/$BPQ_WG_IFACE.conf" && ok "old box wg conf readable" || die "cannot read old box wg conf (needed to inherit $WG_ADDR)"
  bpq "grep -qw 'Address.*$WG_ADDR' /etc/wireguard/$BPQ_WG_IFACE.conf" && ok "old wg conf carries $WG_ADDR" || warn "old wg conf may not set $WG_ADDR — check before network phase"
  # CT
  ssh "$PVE_SSH" "pct status $CTID" | grep -q running && ok "CT $CTID running" || die "CT $CTID not running"
  ct "command -v wg-quick >/dev/null" && ok "CT has wireguard-tools" || die "CT lacks wireguard-tools (apt-get install -y wireguard-tools in the CT)"
  ct "test -c /dev/net/tun" && ok "CT has /dev/net/tun" || die "CT lacks /dev/net/tun — wg inherit will fail"
  ct "curl -s -m5 -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/healthz" | grep -qx 200 && ok "CT node healthy" || warn "CT node /healthz not 200"
  # VERSION FLOOR — the connect-script port must survive end to end as a port NAME (e.g. hf-40m):
  #   pdn-bbs < 0.2.53 drops a non-numeric port to null (ConnectScript.cs) — the attempt-2 misroute;
  #   node    < 0.36.2 resolves ports 1-indexed only (SupervisorRhpGateway.ResolvePortId) and rejects
  #                    a name outright. Both legs are required, so both are hard gates (die, not warn).
  nodev="$(ct "dpkg -l packetnet | tail -1 | awk '{print \\\$3}'")"
  bbsv="$(ct "dpkg -l pdn-bbs | tail -1 | awk '{print \\\$3}'")"
  if command -v dpkg >/dev/null 2>&1; then
    dpkg --compare-versions "$nodev" ge 0.36.2 && ok "CT node $nodev >= 0.36.2 (resolves port ids by name)" || die "CT node $nodev below 0.36.2 — it cannot resolve a connect-script port NAME (needs #668); install packetnet 0.36.2+ in the CT"
    dpkg --compare-versions "$bbsv" ge 0.2.53 && ok "CT pdn-bbs $bbsv >= 0.2.53 (passes the port verbatim)" || die "CT pdn-bbs $bbsv below 0.2.53 — it silently drops a non-numeric connect-script port (the attempt-2 misroute, #91); install pdn-bbs 0.2.53+ in the CT"
  else
    warn "no local dpkg to version-compare; CT reports node $nodev / pdn-bbs $bbsv — ensure node >= 0.36.2 and pdn-bbs >= 0.2.53"
  fi
  [[ -f "$TAILSCALE_KEY" ]] && ok "tailscale auth key staged (optional)" || warn "no tailscale key at $TAILSCALE_KEY — tailscale join skipped (M7TAW is dead -> non-blocking)"
  note "PREFLIGHT done. If all [ok], proceed: cutover-gb7rdg.sh freeze"
  ;;

freeze)
  note "FREEZE — stop LinBPQ on $BPQ_SSH. GB7RDG goes OFF-AIR; mail files become consistent."
  confirm "Stop $BPQ_SERVICE on the OLD box now?" || die "aborted"
  bpq "systemctl stop $BPQ_SERVICE"
  # POLL until inactive — LinBPQ's graceful shutdown (closing L2 links) outlives a fixed sleep, so it
  # is briefly "deactivating" before "inactive". Wait it out (up to ~40s) rather than checking once.
  for _i in $(seq 1 20); do bpq_inactive && break; sleep 2; done
  bpq_inactive && ok "LinBPQ stopped; GB7RDG off-air, mail frozen" || die "LinBPQ still active after 40s — STOP before continuing"
  # DISABLE it too: a stop alone survives only until the old box reboots, after which LinBPQ would
  # come back and dual-claim GB7RDG against the CT. `abort` re-enables it.
  bpq "systemctl disable $BPQ_SERVICE" >/dev/null 2>&1 || true
  bpq "systemctl is-enabled $BPQ_SERVICE 2>/dev/null || true" | grep -qx disabled \
    && ok "LinBPQ disabled (will not return on reboot)" || warn "could not confirm $BPQ_SERVICE is disabled — check before 'network'"
  # RESTART kissproxy now that LinBPQ has let go of it. kissproxy (8910-8913) is the serial<->TCP KISS
  # bridge both nodes dial; LinBPQ's client sessions have just dropped, and a stale/half-open session
  # on its side is the prime suspect for the 8910/8913 flapping seen at attempt 2. Restarting here —
  # after LinBPQ is down, before the CT connects at `network` — hands the CT a clean bridge.
  bpq "systemctl restart $KISSPROXY_SERVICE" || true
  for _i in $(seq 1 15); do bpq "ss -ltn | grep -qE '0\.0\.0\.0:8910'" && break; sleep 2; done
  bpq "systemctl is-active $KISSPROXY_SERVICE" | grep -qx active \
    && ok "kissproxy restarted (clean bridge for the CT)" || die "$KISSPROXY_SERVICE not active after restart — the CT cannot reach the radios; fix before 'network'"
  bpq "ss -ltn | grep -qE '0\.0\.0\.0:8910'" \
    && ok "kissproxy listening on 0.0.0.0:8910 (CT-reachable)" || die "kissproxy not listening on 8910 after restart — fix before 'network'"
  ok "Next: cutover-gb7rdg.sh sync"
  ;;

sync)
  note "SYNC — pull the FROZEN dump (atomic), rebuild bbs.db, load into the CT (still HELD)."
  bpq_inactive || die "LinBPQ still running — run 'freeze' first (dump would be inconsistent)"
  rm -rf "$SNAP"; mkdir -p "$SNAP"
  note "pulling an atomic snapshot from $BPQ_SSH:$BPQ_DIR ..."
  # ONE tar over the wire = a single consistent snapshot (no inter-file mutation window).
  bpq "cd $BPQ_DIR && tar -cf - DIRMES.SYS WFBID.SYS linmail.cfg Mail" | tar -C "$SNAP" -xf - || die "atomic dump pull failed"
  # Re-assert LinBPQ stayed stopped across the pull (catch a cron/restart mutating mid-snapshot).
  bpq_inactive || die "LinBPQ became active DURING the pull — snapshot may be inconsistent; re-freeze + re-sync"
  [[ -f "$SNAP/DIRMES.SYS" && -f "$SNAP/WFBID.SYS" ]] || die "snapshot missing DIRMES.SYS/WFBID.SYS"
  ok "atomic dump staged at $SNAP"
  note "building importer + dry-run validation ..."
  dotnet build "$REPO_ROOT/tools/Bbs.Import.Bpq/Bbs.Import.Bpq.csproj" -c Release >/dev/null
  IMP="$REPO_ROOT/tools/Bbs.Import.Bpq/bin/Release/net10.0/bpq-import.dll"
  DRY="$(dotnet "$IMP" --source "$SNAP" --dry-run)"; echo "$DRY"
  # Fail-CLOSED consistency gate: the orphan-header line MUST be present AND zero.
  grep -q 'Orphan headers' <<<"$DRY" || die "could not find the orphan-header line in the dry-run — importer output changed? refusing (fail-closed)"
  OH="$(sed -n 's/.*Orphan headers (header, no body) *: \([0-9][0-9]*\).*/\1/p' <<<"$DRY" | head -1)"
  [[ "$OH" =~ ^[0-9]+$ ]] || die "could not parse a numeric orphan-header count — refusing (fail-closed)"
  [[ "$OH" == 0 ]] || die "$OH orphan header(s) -> dump INCONSISTENT (LinBPQ not fully stopped?). Refusing."
  ok "dump consistent (0 orphan headers)"
  rm -f "$BUILT_DB" "$BUILT_DB"-wal "$BUILT_DB"-shm
  dotnet "$IMP" --source "$SNAP" --target "$BUILT_DB" >/dev/null
  [[ -f "$BUILT_DB" ]] || die "import produced no db"
  ok "rebuilt $(du -h "$BUILT_DB" | cut -f1) bbs.db"
  note "loading into the CT (forwarding HELD, housekeeping preserved) ..."
  ct "systemctl stop $NODE_SVC"
  ct "if [ -f $BBS_STATE/bbs.db ]; then cp -a $BBS_STATE/bbs.db $BBS_STATE/bbs.db.pre-cutover; fi; rm -f $BBS_STATE/bbs.db-wal $BBS_STATE/bbs.db-shm"
  ct_push "$BUILT_DB" "$BBS_STATE/bbs.db"
  ct_push "$BBS_HELD" "$BBS_STATE/bbs.yaml"
  ct "chown -R packetnet:packetnet $BBS_STATE; systemctl start $NODE_SVC"; sleep 6
  ct "journalctl -u $NODE_SVC --no-pager -n 80 -o cat | grep -i 'forwarding is HELD' | tail -1" | grep -qi HELD \
    && ok "CT loaded the final mailbox; forwarding HELD" || warn "could not confirm HELD in logs — check 'status' before continuing"
  date +%s > "$SYNC_MARKER"
  ok "SYNC done. Next: cutover-gb7rdg.sh network"
  ;;

network)
  note "NETWORK — WireGuard handover (CT inherits $WG_ADDR) + bring CT ports up. Mail STILL HELD."
  [[ -f "$SYNC_MARKER" ]] || die "no sync marker — run 'sync' first (the CT must hold the FINAL mailbox before going on-air)"
  confirm "Take $BPQ_WG_IFACE DOWN on the old box and bring it up on the CT (inherit $WG_ADDR)?" || die "aborted"
  bpq "cat /etc/wireguard/$BPQ_WG_IFACE.conf" > "$WORK_DIR/wg0.conf" || die "could not read old wg conf"
  ct_push "$WORK_DIR/wg0.conf" /etc/wireguard/wg0.conf
  ct "chmod 600 /etc/wireguard/wg0.conf"
  rm -f "$WORK_DIR/wg0.conf"
  # Hub allows ONE peer per key: old DOWN, then CT UP. If CT-up fails, AUTO-RESTORE old wg.
  bpq "wg-quick down $BPQ_WG_IFACE || true"; ok "old box $BPQ_WG_IFACE down (released $WG_ADDR)"
  restore_old_wg() { warn "CT wg bring-up FAILED — restoring old box wg ($WG_ADDR back on the old node)"; bpq "wg-quick up $BPQ_WG_IFACE || true"; }
  trap 'restore_old_wg; die "WireGuard handover failed; old node restored on the mesh. Investigate before retrying network."' ERR
  ct "wg-quick down wg0 2>/dev/null || true; wg-quick up wg0"
  sleep 2
  assert_ct_wg_addr || { false; }                       # address is assigned locally (immediate); else trap -> restore + die
  # WireGuard is LAZY — it does not handshake until traffic flows, so checking latest-handshakes first
  # always sees 0. GENERATE traffic (ping a known-live peer THROUGH the tunnel) to trigger the CT<->hub
  # handshake, THEN verify a handshake landed. Poll to ride out a slow first handshake. (We check the
  # handshake, not the ping result, so a transiently-down peer doesn't false-fail a working tunnel.)
  hs=0
  for _i in $(seq 1 10); do
    ct "ping -c1 -W2 $WG_PROBE >/dev/null 2>&1 || true; wg show wg0 latest-handshakes | awk '{print \\\$2}' | grep -qvx 0" && { hs=1; break; }
    sleep 2
  done
  [[ "$hs" == 1 ]] || { false; }                        # no hub handshake after ~20s of traffic -> trap -> restore + die
  trap - ERR
  ok "CT wg up as $WG_ADDR with a hub handshake"
  ct "ping -c2 -W2 $WG_PROBE >/dev/null 2>&1" && ok "CT reaches a live AXUDP peer ($WG_PROBE)" || warn "CT cannot ping $WG_PROBE yet (peer may be transiently down)"
  # Bring ports up; forwarding + oarc STILL held. tailscale only if a key is staged.
  ts=false; [[ -f "$TAILSCALE_KEY" ]] && ts=true
  NCFG="$(gen_node true false "$ts")"
  note "node config change to apply (ports ENABLED, oarc HELD, tailscale=$ts) — semantic diff:"
  show_diff "$NODE_HELD" "$NCFG"
  confirm "Apply this and bring GB7RDG on-air (still no forwarding)?" || die "aborted (wg already handed over — run 'abort' to fully reverse)"
  ct_apply "$NCFG" "-"
  ok "ports enabled; GB7RDG on-air as the pdn node. Next: cutover-gb7rdg.sh verify"
  ;;

verify)
  note "VERIFY (read-only) — on-air health BEFORE the irreversible forwarding flip."
  ct "curl -s -m5 -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/healthz" | grep -qx 200 && ok "node healthy" || warn "node /healthz not 200"
  assert_ct_wg_addr && ok "CT wg owns $WG_ADDR" || warn "CT wg is NOT $WG_ADDR — peers addressing GB7RDG will not reach it"
  # `ss` renders the peer as [::ffff:10.45.0.121]:8912 with ESTAB at the line START, so the old
  # '$KISS_HOST:891x.*ESTAB' pattern could NEVER match (a ']' sits before the colon; ESTAB precedes
  # the address). `ss -tn` lists only established sockets, so match the host (fixed-string, dots-safe)
  # then count the 891x peer ports. `|| true` stops grep -c's exit-1-on-zero from tripping pipefail;
  # `tr -dc` collapses the output to a clean integer (the old `|| echo 0` double-printed -> "0\n0").
  N="$(ct "ss -tn 2>/dev/null | grep -F '$KISS_HOST' | grep -cE ':891[0-3]' || true" | tr -dc 0-9)"; [[ "${N:-0}" -ge 1 ]] && ok "CT has $N kissproxy modem connection(s)" || warn "no modem connections to $KISS_HOST — RF ports not up"
  ct "ping -c2 -W2 $WG_PROBE >/dev/null 2>&1" && ok "live AXUDP peer $WG_PROBE reachable" || warn "$WG_PROBE unreachable"
  note "recent node log (links / netrom / AXUDP peers):"
  ct "journalctl -u $NODE_SVC --no-pager -n 40 -o cat | grep -iE 'listening|netrom|link|node|axudp|peer' | tail -10 || true"
  note "If the above is healthy, validate the partner connect scripts next:"
  note "  cutover-gb7rdg.sh connect-test    (then golive)"
  ;;

connect-test)
  # Run AFTER 'network' (node on-air), BEFORE 'golive'. You AUTHOR each partner's structured connect
  # script (v2 steps) and test-connect it against the real prompt — WITHOUT moving any mail (the
  # test-connect tool runs the script + the SID/prompt wait, then disconnects; no FBB session).
  note "CONNECT-TEST — author + validate each partner, then ENABLE the good ones (on-air, forwarding still HELD)."
  note "Two-tier gate: the whole-BBS MASTER is held AND every partner is imported DISABLED with a BLANK"
  note "connect script (connect-scripts v2 retired flat-form auto-translate) — so nothing moves yet, and no"
  note "partner dials until you author its script. For EACH partner, in the BBS sysop UI (Forwarding page)"
  note "— or an authenticated POST to /apps/bbs/forwarding/{test-connect,partner/enable}:"
  note "  1. AUTHOR its structured connect script (Forms tab): a Dial step + expect/send steps;"
  note "  2. click 'Test connect' — it STREAMS the live dialogue (moves NO mail). 'Use as Wait-for' on the"
  note "     observed prompt drops it (trailing space and all) into a step's expect; re-test to confirm;"
  note "  3. on a CLEAN connect (ok=true + partner FBB SID seen), click 'Enable' (it stays gated until you do);"
  note "  4. a partner that will NOT connect — fix its script/route, or leave it DISABLED."
  note "Direct-BBS partners are a one-step 'connect: <call>'; the multi-hop ones — GB7LOX ('… bbs' via"
  note "GB7LOX-2) and GB7CIP ('C 3 !GB7WEM-7' -> URONode '=> ' -> 'BBS') — need the full expect/send walk."
  note "ALL partners + their current connect script + enabled state (bbs.db):"
  ct "python3 - <<'PY'
import sqlite3
c=sqlite3.connect('file:$BBS_STATE/bbs.db?mode=ro',uri=True)
rows=c.execute(\"SELECT call, enabled, connect_script FROM partners ORDER BY call\").fetchall()
for call,en,cs in rows:
    print('  ['+('ENABLED ' if en else 'disabled')+'] '+call+': '+repr((cs or '').replace(chr(10),' | ')))
print('  (enabled now: '+str(sum(1 for _ ,en,_2 in rows if en))+' / '+str(len(rows))+')')
PY"
  note "Enable each partner you have test-connected. golive then refuses unless >=1 partner is enabled."
  note "When the partners you want are tested + ENABLED, proceed: cutover-gb7rdg.sh golive"
  ;;

golive)
  note "GO-LIVE — POINT OF NO RETURN. Re-checking readiness before enabling forwarding + OARC ..."
  [[ -f "$SYNC_MARKER" ]] || die "no sync marker — refusing golive without a fresh sync in this cutover"
  ct "curl -s -m5 -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/healthz" | grep -qx 200 || die "readiness: node /healthz not 200"
  assert_ct_wg_addr || die "readiness: CT wg is not $WG_ADDR (peers could not reach GB7RDG)"
  N="$(ct "ss -tn 2>/dev/null | grep -cE '$KISS_HOST:891[0-3].*ESTAB'" || echo 0)"; [[ "$N" -ge 1 ]] || die "readiness: no kissproxy modem connections — RF ports not up"
  ct "journalctl -u $NODE_SVC --no-pager -n 80 -o cat | grep -i 'forwarding is HELD' | tail -1" | grep -qi HELD || warn "could not confirm forwarding is currently HELD (it should be)"
  # NEW two-tier gate: the master is about to go ON, so at least one partner MUST be enabled — else
  # golive would succeed into a zero-dial state (forwarding ACTIVE, dialling no one, mail queuing
  # silently). The operator enables partners in connect-test; this asserts they actually did.
  EN="$(ct_enabled_count)"; [[ "${EN:-0}" -ge 1 ]] || die "readiness: 0 partners enabled — run connect-test and ENABLE the partners you want before golive (else nothing forwards)"
  ok "readiness checks passed ($EN partner(s) enabled)"
  echo
  note "############################################################"
  note "# Enabling forwarding (master ON) + ports + OARC. Mail     #"
  note "# starts moving to the $EN enabled partner(s); abort is    #"
  note "# GONE. Decommission the OLD LinBPQ after this.            #"
  note "############################################################"
  if [[ "${CUTOVER_YES:-0}" != "1" ]]; then
    read -r -p "Type exactly 'GB7RDG GO' to enable forwarding (irreversible): " a
    [[ "$a" == "GB7RDG GO" ]] || die "go-live not confirmed"
  fi
  ts=false; [[ -f "$TAILSCALE_KEY" ]] && ts=true
  # Node config: ports stay up + OARC reporting ON. (BBS forwarding is NOT driven by yaml any more —
  # the per-partner enables are already in the store from connect-test; the master is flipped below.)
  NCFG="$(gen_node true true "$ts")"
  ct_apply "$NCFG" "-"
  # Flip the store-backed master ON — THE actual "forwarding starts" step (gen_bbs(true) is a no-op).
  ct_set_master 1
  sleep 6
  ct "journalctl -u $NODE_SVC --no-pager -n 80 -o cat | grep -iE 'forwarding (ACTIVE|cycle)|OARC reporting|announc' | grep -vi HELD | tail -6 || true"
  ct "journalctl -u $NODE_SVC --no-pager -n 40 -o cat | grep -i 'forwarding ACTIVE' | tail -1" | grep -qi ACTIVE \
    || warn "did NOT see 'forwarding ACTIVE' in the log — verify the master flipped on (forwarding_master=1) and partners are draining"
  rm -f "$SYNC_MARKER"
  date -u +%Y-%m-%dT%H:%M:%SZ > "$GOLIVE_MARKER"
  ok "GO-LIVE applied (master ON, $EN partner(s) enabled). Watch forwarding drain + OARC map. Keep the OLD LinBPQ STOPPED (decommissioned)."
  ok "Now validate: cutover-gb7rdg.sh validate  (at T+15m, T+1h, T+24h)"
  ;;

abort)
  note "ABORT — reverse the cutover (valid ONLY before golive). Restores the OLD node."
  warn "If you have already run 'golive', DO NOT abort — the network has advanced; this cannot undo it."
  confirm "Reverse: re-hold + restore the CT, drop its wg, bring the OLD node back?" || die "aborted-abort"
  # 1) Re-hold the CT (ports off, forwarding off) and restore its pre-cutover mailbox. The store-backed
  #    master + per-partner enables live in bbs.db, so the pre-cutover restore re-holds both tiers; we
  #    ALSO force them held explicitly (master=0, every partner disabled) in case the backup is missing
  #    or connect-test enabled partners — so the CT is definitively held in BOTH directions.
  NCFG="$(gen_node false false false)"; BCFG="$(gen_bbs false)"
  ct "systemctl stop $NODE_SVC"
  ct "if [ -f $BBS_STATE/bbs.db.pre-cutover ]; then mv -f $BBS_STATE/bbs.db.pre-cutover $BBS_STATE/bbs.db; rm -f $BBS_STATE/bbs.db-wal $BBS_STATE/bbs.db-shm; fi"
  ct "python3 - <<PY
import sqlite3
c=sqlite3.connect('$BBS_STATE/bbs.db')
c.execute('INSERT INTO meta(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=?', ('forwarding_master','0','0'))
c.execute('UPDATE partners SET enabled=0')
c.commit(); c.close()
PY
chown packetnet:packetnet $BBS_STATE/bbs.db"
  # Apply the HELD config (ports off) + start the node HELD via ct_apply — do NOT start it first here:
  # that brings the node up on the still-stored ports-ENABLED config for a beat (a stray on-air frame
  # as GB7RDG-2). ct_apply does its own stop+import+start, so the node comes up already held.
  ct_apply "$NCFG" "$BCFG"
  ok "CT re-held (master off + all partners disabled) + pre-cutover mailbox restored"
  # 2) Drop the CT's wg and VERIFY it is down before re-arming the old node (no dual-claim).
  ct "wg-quick down wg0 2>/dev/null || true"
  ct "wg show wg0 2>/dev/null | grep -q interface" && die "CT wg STILL up — refusing to raise old wg (would dual-claim $WG_ADDR). Fix the CT first." || ok "CT wg down"
  # 3) Old box back on-air. Re-ENABLE first (freeze disabled it so a reboot could not dual-claim) —
  #    otherwise the restored node would silently not survive the next reboot of the old box.
  bpq "wg-quick up $BPQ_WG_IFACE 2>/dev/null || true"
  bpq "systemctl enable $BPQ_SERVICE" >/dev/null 2>&1 || true
  bpq "systemctl is-enabled $BPQ_SERVICE 2>/dev/null || true" | grep -qx enabled \
    && ok "LinBPQ re-enabled (survives reboot)" || warn "could not confirm $BPQ_SERVICE is enabled — re-enable by hand"
  bpq "systemctl start $BPQ_SERVICE"; sleep 3
  bpq "systemctl is-active $BPQ_SERVICE" | grep -qx active && ok "old LinBPQ back on-air" || warn "old LinBPQ did not restart — investigate"
  rm -f "$SYNC_MARKER"
  ok "ABORT complete. OLD GB7RDG is live again; the CT is held. A re-cutover MUST start from 'freeze' (fresh sync)."
  ;;

baseline)
  note "BASELINE — snapshot the held mailbox + node state for 'validate' to diff. Run AFTER 'sync', BEFORE 'golive'."
  mkdir -p "$WORK_DIR"
  nv="$(ct "dpkg -l packetnet | tail -1 | awk '{print \\\$3}'")"
  bv="$(ct "dpkg -l pdn-bbs | tail -1 | awk '{print \\\$3}'")"
  { echo "# GB7RDG cutover baseline (held, pre-golive)"; echo "node_version=$nv"; echo "bbs_version=$bv"; ct_bbs_counts; } > "$BASELINE_FILE"
  cat "$BASELINE_FILE"
  ok "baseline saved -> $BASELINE_FILE"
  note "ALSO capture the RF wire baseline via the kiss-collector MCP (the reference ranges to validate against):"
  note "  - per-band 24h frame counts (stats since=24h): 40m busiest, all 4 bands active"
  note "  - top forwarding partners (top_talkers by=to direction=TX): GB7BPQ/GB7OXF/GB7BSK/EI5IYB + GB7WOD"
  note "  - channel_wait_ms per band (the CSMA yardstick): 40m ~2s, 70cm ~180ms, 6m ~550ms, 2m ~240ms"
  ;;

validate)
  note "VALIDATE — post-golive health vs baseline. Run at T+15m / T+1h / T+24h."
  [[ -f "$BASELINE_FILE" ]] || die "no baseline ($BASELINE_FILE) — 'baseline' must be run before golive"
  bval() { grep -oE "^$1=-?[0-9]+" "$BASELINE_FILE" | cut -d= -f2; }
  b_bids="$(bval bids)"; b_queued="$(bval queued)"; b_hw="$(bval highwater)"; b_msgs="$(bval msgs)"
  FAILS=0
  # Precompute the (messy, remote) values FIRST, then chk simple tests on the locals — keeps the
  # chk eval free of nested ct/$() quoting.
  hz="$(ct "curl -s -m5 -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/healthz" 2>/dev/null || echo 000)"
  act="$(ct "systemctl is-active $NODE_SVC" 2>/dev/null || echo unknown)"
  modems="$(ct "ss -tn 2>/dev/null | grep -cE '$KISS_HOST:891[0-3].*ESTAB'" 2>/dev/null || echo 0)"
  fa="$(ct "journalctl -u $NODE_SVC --no-pager -n 300 -o cat | grep -ci 'forwarding ACTIVE'" 2>/dev/null || echo 0)"
  now="$(ct_bbs_counts)"
  n_bids="$(grep -oE '^bids=-?[0-9]+' <<<"$now"|cut -d= -f2)"; n_queued="$(grep -oE '^queued=-?[0-9]+' <<<"$now"|cut -d= -f2)"
  n_hw="$(grep -oE '^highwater=-?[0-9]+' <<<"$now"|cut -d= -f2)"; n_msgs="$(grep -oE '^msgs=-?[0-9]+' <<<"$now"|cut -d= -f2)"
  # --- node on-air ---
  chk "node /healthz 200 (got $hz)" "[ \"$hz\" = 200 ]"
  chk "node service active (got $act)" "[ \"$act\" = active ]"
  chk "CT wg owns $WG_ADDR" "assert_ct_wg_addr"
  chk ">=1 kissproxy modem connection (got ${modems:-0})" "[ \"${modems:-0}\" -ge 1 ]"
  chk "live AXUDP peer GB7NDH (10.66.66.10) reachable" "ct 'ping -c2 -W2 10.66.66.10 >/dev/null 2>&1'"
  chk "live AXUDP peer GB7BDH (10.66.66.24) reachable" "ct 'ping -c2 -W2 10.66.66.24 >/dev/null 2>&1'"
  # --- forwarding ON + mailbox sane ---
  chk "forwarding ACTIVE (not HELD)" "[ \"${fa:-0}\" -ge 1 ]"
  echo "  baseline: msgs=$b_msgs bids=$b_bids queued=$b_queued hw=$b_hw   now: msgs=$n_msgs bids=$n_bids queued=$n_queued hw=$n_hw"
  # The BID store CHURNS, it does not monotonically grow: new BIDs arrive but BPQ-style
  # BidLifetime (60d) expiry prunes orphan BIDs older than the dedup window (correct — those
  # are past where any BBS dedups). So assert it did not COLLAPSE (a wipe), not that it grew.
  chk "BID dedup store not collapsing (>= 90% of baseline)" "[ \"${n_bids:-0}\" -ge \"$(( ${b_bids:-0} * 9 / 10 ))\" ]"
  chk "queue draining (queued <= baseline)" "[ \"${n_queued:-999999}\" -le \"${b_queued:-0}\" ]"
  chk "high-water carried (>= baseline)" "[ \"${n_hw:-0}\" -ge \"${b_hw:-0}\" ]"
  chk "message count not collapsing (>= 80% of baseline)" "[ \"${n_msgs:-0}\" -ge \"$(( ${b_msgs:-0} * 8 / 10 ))\" ]"
  # --- re-flood signal (logs): bodies actually forwarded vs partner BID-rejects on the drain ---
  if [[ -f "$GOLIVE_MARKER" ]]; then
    since="$(cat "$GOLIVE_MARKER")"
    fwd="$(ct "journalctl -u $NODE_SVC --no-pager --since '$since' -o cat | grep -ic 'Forwarded message' || true")"
    rej="$(ct "journalctl -u $NODE_SVC --no-pager --since '$since' -o cat | grep -icE 'RefusedBid|already' || true")"
    note "re-flood signal since golive: forwarded(bodies)=$fwd  partner-BID-rejected=$rej  (a healthy drain rejects the backlog dups; a flood = many accepts)"
  fi
  echo
  [[ "$FAILS" -eq 0 ]] && ok "VALIDATE: all bash-reachable checks passed" || warn "VALIDATE: $FAILS check(s) FAILED — investigate before trusting the cutover"
  note "RF WIRE-TRUTH — run via the kiss-collector MCP and compare to the baseline ranges:"
  note "  1. GB7RDG is TRANSMITTING since golive on the active bands (search_traffic direction=TX since=$([[ -f $GOLIVE_MARKER ]] && cat $GOLIVE_MARKER || echo '<golive>'))"
  note "  2. exactly ONE GB7RDG on air (no dual-claim) + ID/BEACON/NODES resumed"
  note "  3. per-band frames ~ baseline (stats) + forwarding partners unchanged with NO volume spike (top_talkers by=to)"
  note "  4. channel_wait_ms ~ baseline per band (CSMA sane; near-zero=collisions, huge=stuck)"
  ;;

status)
  note "OLD box ($BPQ_SSH):"
  bpq "systemctl is-active $BPQ_SERVICE; wg show $BPQ_WG_IFACE 2>/dev/null | grep -E 'interface|latest-hand' | head -2 || echo 'wg down'" || true
  note "CT ($CTID via $PVE_SSH):"
  ct "systemctl is-active $NODE_SVC; ip -4 addr show wg0 2>/dev/null | grep -oE 'inet [0-9.]+' || echo 'wg down'; journalctl -u $NODE_SVC --no-pager -n 60 -o cat | grep -iE 'forwarding is HELD|forwarding ACTIVE|configured port' | tail -2" || true
  ;;

*) die "unknown phase '$phase' (preflight|freeze|sync|baseline|network|verify|connect-test|golive|validate|abort|status)";;
esac
