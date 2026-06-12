#!/usr/bin/env bash
#
# deploy-bbs.sh — build a real pdn-bbs .deb and install it on the live box.
#
# The tight build -> deploy -> show loop for the BBS app package, bypassing
# CI/GHA but using the SAME packaging as the release: build the amd64 .deb with
# scripts/build-deb.sh (PDN_FAST=1), scp it to the box, `dpkg -i` it, restart the
# packetnet node, then print a liveness summary (service state, /healthz, the bbs
# app starting in the journal, and the preserved message count) so you can see it
# came up with its data intact.
#
# CODE vs STATE (docs/release-pipeline.md): the .deb installs CODE to the SYSTEM
# app dir /usr/share/packetnet/apps/bbs. STATE (bbs.db, bbs.yaml) lives in the
# OWNER app-state dir /var/lib/packetnet/apps/bbs. The packetnet node discovers
# apps by scanning BOTH roots, and LATER ROOT WINS on id collision — so the box
# must not also carry a hand-staged copy of the CODE under /var/lib (it would
# shadow this /usr/share install). This script only installs CODE under
# /usr/share; STATE under /var/lib is never read or written here.
#
# Default target is root@packetdotnet (Ubuntu/systemd LXC on the LAN); the
# packetnet node host package owns the service + the packetnet user there.

set -euo pipefail

# --- Config (env-overridable) -----------------------------------------------
HOST="${PDNBBS_HOST:-root@packetdotnet}"
SSH_KEY="${PDNBBS_SSH_KEY:-$HOME/.ssh/id_ed25519}"
SERVICE="${PDNBBS_SERVICE:-packetnet}"
HTTP_PORT="${PDNBBS_HTTP_PORT:-8080}"
RID="linux-x64"
ARCH="amd64"
# State dir on the box (owner app-state — code lives under /usr/share, not here).
STATE_DIR="${PDNBBS_STATE_DIR:-/var/lib/packetnet/apps/bbs}"
# A dev version that's distinct per build and sorts ABOVE the last release (the
# +dev<timestamp> build-metadata segment makes `dpkg --compare-versions` rank it
# above plain 0.1.0 while staying obviously non-release).
VERSION="${PDNBBS_DEB_VERSION:-0.1.0+dev$(date +%Y%m%d%H%M%S)}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() {
  cat <<'EOF'
deploy-bbs.sh — build a real pdn-bbs .deb and install it on the live box.

Builds the amd64 .deb with scripts/build-deb.sh (PDN_FAST=1), scp's it to the
deploy box, dpkg-installs it, restarts the packetnet node, and prints a liveness
summary (service state, /healthz, the bbs app in the journal, preserved message
count). The tight build->deploy->show dev loop, no CI wait — same artifact shape
GHA ships.

Usage: scripts/deploy-bbs.sh [--skip-build] [--logs] [-h|--help]

  --skip-build   Deploy the most recent existing artifacts/pdn-bbs_*_amd64.deb
                 without rebuilding.
  --logs         Follow the service log after deploying (Ctrl-C to stop).

Env overrides:
  PDNBBS_HOST         (default root@packetdotnet)
  PDNBBS_SSH_KEY      (default ~/.ssh/id_ed25519)
  PDNBBS_SERVICE      (default packetnet)
  PDNBBS_HTTP_PORT    (default 8080)
  PDNBBS_STATE_DIR    (default /var/lib/packetnet/apps/bbs)
  PDNBBS_DEB_VERSION  (default 0.1.0+dev<UTCstamp>)
EOF
}

SKIP_BUILD=0
FOLLOW_LOGS=0
for arg in "$@"; do
  case "$arg" in
    --skip-build) SKIP_BUILD=1 ;;
    --logs)       FOLLOW_LOGS=1 ;;
    -h|--help)    usage; exit 0 ;;
    *) echo "unknown argument: $arg (try --help)" >&2; exit 2 ;;
  esac
done

SSH=(ssh -i "$SSH_KEY" -o BatchMode=yes)
SCP=(scp -i "$SSH_KEY" -o BatchMode=yes)
say() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }

# --- 1. Build the .deb ------------------------------------------------------
if [[ "$SKIP_BUILD" -eq 1 ]]; then
  DEB="$(ls -t "$REPO_ROOT"/artifacts/pdn-bbs_*_"$ARCH".deb 2>/dev/null | head -n1)"
  [[ -n "$DEB" && -f "$DEB" ]] || { echo "no artifacts/pdn-bbs_*_${ARCH}.deb — build first (run without --skip-build)" >&2; exit 1; }
  say "Skipping build; deploying most recent .deb: $DEB"
else
  say "Building .deb $VERSION ($RID, PDN_FAST)"
  PDN_FAST=1 "$REPO_ROOT/scripts/build-deb.sh" "$RID" "$VERSION"
  DEB="$REPO_ROOT/artifacts/pdn-bbs_${VERSION}_${ARCH}.deb"
  [[ -f "$DEB" ]] || { echo "expected $DEB but it wasn't produced" >&2; exit 1; }
fi
DEB_BASE="$(basename "$DEB")"

# --- 2. Ship + install ------------------------------------------------------
# scp to /tmp, then dpkg -i (--force-confold is harmless here — the BBS ships no
# conffiles — but keep it for parity / future-proofing). Fall back to apt -f
# install only if dpkg reports unmet deps. Then remove the staged .deb.
say "Shipping $DEB_BASE to $HOST:/tmp"
"${SCP[@]}" "$DEB" "$HOST:/tmp/$DEB_BASE"

say "Installing on $HOST (dpkg -i --force-confold)"
"${SSH[@]}" "$HOST" bash -s "$DEB_BASE" <<'REMOTE'
set -e
deb="$1"
if ! dpkg -i --force-confold "/tmp/$deb"; then
  echo "dpkg reported unmet deps — running apt-get -f install"
  apt-get -y -f install
fi
rm -f "/tmp/$deb"
REMOTE

# --- 3. Restart -------------------------------------------------------------
# The pdn-bbs postinst deliberately does NOT restart packetnet (it only ships
# the app code); the node re-scans its app dirs on (re)start, so bounce it here.
say "Restarting $SERVICE"
"${SSH[@]}" "$HOST" "systemctl restart $SERVICE"

# --- 4. Verify --------------------------------------------------------------
# Service active; /healthz answers (poll — Kestrel binds a beat after the unit
# goes active); the bbs app started in the journal; and the message count is
# preserved (the .deb never touches /var/lib state). No sqlite3 CLI on the box — use
# python3. No curl on the box either — probe /healthz with bash's /dev/tcp.
say "Verifying"
"${SSH[@]}" "$HOST" bash -s "$SERVICE" "$HTTP_PORT" "$STATE_DIR" <<'REMOTE' || true
svc="$1"; port="$2"; dir="$3"
printf 'service: '; systemctl is-active "$svc" || true
printf 'healthz: '
ok=0
for _ in $(seq 1 30); do
  if command -v curl >/dev/null 2>&1; then
    body=$(curl -s "http://127.0.0.1:$port/healthz" 2>/dev/null || true)
    if [ -n "$body" ]; then echo "$body"; ok=1; break; fi
  elif { exec 3<>/dev/tcp/127.0.0.1/"$port"; } 2>/dev/null; then
    printf 'GET /healthz HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3
    body=$(timeout 3 cat <&3 | tr -d '\r' | tail -n1)
    exec 3>&- 2>/dev/null || true
    echo "$body"; ok=1; break
  fi
  sleep 0.5
done
[ "$ok" -eq 1 ] || echo "(no answer on 127.0.0.1:$port after 15s)"

echo '--- bbs app in the journal ---'
journalctl -u "$svc" --no-pager -o cat | grep -iE '\bbbs\b' | tail -n 6 || echo '(no bbs lines yet)'

echo '--- preserved message count ---'
db="$dir/bbs.db"
if [ -f "$db" ]; then
  if command -v python3 >/dev/null 2>&1; then
    python3 -c "import sqlite3; print('messages:', sqlite3.connect('$db').execute('select count(*) from messages').fetchone()[0])" 2>&1 || echo "(could not read message count)"
  else
    echo "(no python3 on the box — cannot read $db)"
  fi
else
  echo "(no $db yet — BBS not yet configured/started, or fresh install)"
fi
REMOTE

say "Done — $HOST updated."

if [[ "$FOLLOW_LOGS" -eq 1 ]]; then
  say "Following logs (Ctrl-C to stop)"
  exec "${SSH[@]}" "$HOST" "journalctl -u $SERVICE -f -o cat"
fi
