#!/usr/bin/env bash
# Smoke test for the pdn-bbs LinBPQ mail oracle.
#
# Proves the oracle answers like a BBS before any C# exists:
#   1. clean-slate `docker compose down -v` + state wipe
#   2. `up -d --wait` (healthchecks gate readiness)
#   3. drives the telnet port with an embedded here-script:
#      login (admin/admin) -> BBS -> `S PDNTEST @ PDNBBS` -> title ->
#      body -> /EX -> expect `Message: n Bid: ...` -> `L` lists it -> `B`
#   4. asserts the message body landed in the on-disk store
#      (/data/Mail/m_*.mes — the primary CI assertion target, spec §7.4)
#   5. tears the stack down (down -v + state wipe)
#
# Exit 0 = green. Any failure exits 1 with the transcript on stdout.
#
# Prereqs: docker compose v2, python3 (the telnet driver), bash.
# Run from anywhere: paths are derived from this script's location.

set -u -o pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$(dirname "$SCRIPT_DIR")"
COMPOSE_FILE="$DOCKER_DIR/compose.oracle.yml"
STATE_DIR="$SCRIPT_DIR/state"
TELNET_HOST=127.0.0.1
TELNET_PORT=8210
# Same pinned image as compose.oracle.yml — reused as the throwaway
# root-capable container that wipes the root-owned state dir.
LINBPQ_IMAGE="m0lte/linbpq@sha256:872343ffb76a52316b1ae42a42d3c0893f545ccd577301ebc7c4455b292a8025"

NONCE="smoke-$(date +%s)-$$"
BODY="pdn-bbs oracle smoke body $NONCE"
TITLE="oracle-smoke $NONCE"

fail() { echo "SMOKE: FAIL — $*" >&2; exit 1; }

compose() { docker compose -f "$COMPOSE_FILE" "$@"; }

wipe_state() {
    # /data is a host bind mount written by root inside the container, so
    # wipe it with a throwaway container rather than host rm.
    mkdir -p "$STATE_DIR"
    docker run --rm --entrypoint /bin/sh -v "$STATE_DIR:/data" "$LINBPQ_IMAGE" \
        -c 'rm -rf /data/* /data/.[!.]* /data/..?* 2>/dev/null; true' \
        || fail "could not wipe state dir $STATE_DIR"
}

teardown() {
    compose down -v --remove-orphans >/dev/null 2>&1
    wipe_state >/dev/null 2>&1
}

echo "SMOKE: clean slate (down -v + state wipe)"
compose down -v --remove-orphans || true
wipe_state

trap teardown EXIT

echo "SMOKE: bringing the stack up (compose up -d --wait)"
if ! compose up -d --wait; then
    compose ps
    compose logs --tail 50
    fail "stack did not reach healthy"
fi

echo "SMOKE: driving telnet $TELNET_HOST:$TELNET_PORT"

# Telnet driver here-script. Minimal RFC 854 (IAC negotiation stripped),
# read-until-marker with deadline — the same approach as the proven
# m0lte/linbpq integration harness (tests/integration/helpers/
# telnet_client.py), which this is adapted from.
TRANSCRIPT="$(mktemp /tmp/oracle-smoke-XXXXXX.log)"
python3 - "$TELNET_HOST" "$TELNET_PORT" "$BODY" "$TITLE" "$NONCE" <<'PY' >"$TRANSCRIPT" 2>&1
import socket, sys, time

host, port, body, title, nonce = (
    sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4], sys.argv[5]
)

IAC, DONT, DO, WONT, WILL = 0xFF, 0xFE, 0xFD, 0xFC, 0xFB

def strip_iac(buf: bytes) -> bytes:
    out, i = bytearray(), 0
    while i < len(buf):
        b = buf[i]
        if b == IAC:
            if i + 2 < len(buf) and buf[i + 1] in (DO, DONT, WILL, WONT):
                i += 3
                continue
            i += 1
            continue
        out.append(b)
        i += 1
    return bytes(out)

class Tn:
    def __init__(self, host, port, timeout=10.0):
        # The container can be healthy (HTTP up) a beat before the telnet
        # listener answers; retry the connect briefly.
        deadline = time.monotonic() + 30
        while True:
            try:
                self.s = socket.create_connection((host, port), timeout=timeout)
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(0.5)
        self.s.settimeout(timeout)
        self.buf = b""

    def read_until(self, marker: bytes, timeout=10.0) -> bytes:
        deadline = time.monotonic() + timeout
        while marker not in self.buf:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"never saw {marker!r}; got {self.buf!r}")
            self.s.settimeout(remaining)
            chunk = self.s.recv(4096)
            if not chunk:
                raise ConnectionError(f"closed before {marker!r}; got {self.buf!r}")
            self.buf += strip_iac(chunk)
        idx = self.buf.index(marker) + len(marker)
        out, self.buf = self.buf[:idx], self.buf[idx:]
        return out

    def line(self, text: str):
        self.s.sendall(text.encode("ascii") + b"\r")

def show(tag: str, data: bytes):
    print(f"--- {tag} ---")
    print(data.decode("ascii", "replace"))

t = Tn(host, port)

# Login (bpq32.cfg: USER=admin,admin,GB7BPQ,,SYSOP)
show("login:user-prompt", t.read_until(b"user:", timeout=15))
t.line("admin")
show("login:password-prompt", t.read_until(b"password:"))
t.line("admin")
show("login:banner", t.read_until(b"Telnet Server\r\n"))

# Enter the BBS application; prompt is `de <BBSName>>` (spec §1.2),
# BBSName = GB7BPQ-1 (linmail.cfg seed). The welcome carries BPQMail's
# SID (`[BPQ-...]`) — proof the mail subsystem is up, not just the node.
t.line("BBS")
welcome = t.read_until(b"de GB7BPQ-1>", timeout=15)
show("bbs:welcome", welcome)
if b"[BPQ-" not in welcome:
    raise SystemExit(f"FAIL: no BPQMail SID in BBS welcome: {welcome!r}")

# Post a personal message (spec §1.5 exact flow). S == SP.
t.line("S PDNTEST @ PDNBBS")
show("send:title-prompt", t.read_until(b"Enter Title"))
t.line(title)
show("send:text-prompt", t.read_until(b"Enter Message Text"))
# Pace body and /EX apart (0.3 s, same as the m0lte harness): BPQMail
# misparses rapidly-arrived CR-separated lines that land in one buffer
# (spec §3.13.2) — sent back-to-back, the /EX terminator is swallowed
# and the session stays stuck in text-entry mode (observed live).
time.sleep(0.3)
t.line(body)
time.sleep(0.3)
t.line("/EX")
# Acceptance shape: `Message: %d Bid:  %s Size: %d` (spec §1.5 step 7).
accept = t.read_until(b"Bid:", timeout=15)
accept += t.read_until(b"de GB7BPQ-1>", timeout=15)
show("send:acceptance", accept)
if b"Message:" not in accept or b"Bid:" not in accept:
    raise SystemExit(f"FAIL: no Message:/Bid: acceptance in {accept!r}")

# List — the new message must show (spec §1.6 line shape:
# `msg# dd-mmm TS size TO@VIA FROM title`). NOTE: the TO callsign is
# stored truncated to 6 chars (spec §1.5: "TO/FROM truncated to 6
# chars", confirmed live) so PDNTEST lists as `PDNTES @PDNBBS` — assert
# on the @VIA and the nonce-bearing title, not the 7-char TO.
t.line("L")
listing = t.read_until(b"de GB7BPQ-1>", timeout=15)
show("list", listing)
if b"@PDNBBS" not in listing or nonce.encode() not in listing:
    raise SystemExit(f"FAIL: posted message not in listing: {listing!r}")

# Forwarding queue — proves (a) the seeded F_SYSOP user record grants
# BBS-sysop on this Secure_Session telnet login and (b) the message
# routed to the PDNBBS partner queue (`%s %d Msgs` rows, spec §4.4 —
# the record is keyed by BASE call; the dial still targets PDNBBS-1).
# The BBS log corroborates with `Routing Trace PDNBBS Matches AT
# PDNBBS` followed by `Connecting to BBS PDNBBS` dial-out attempts.
t.line("FWD QUEUE")
queue = t.read_until(b"de GB7BPQ-1>", timeout=15)
show("fwd-queue", queue)
if b"PDNBBS" not in queue or b"Msgs" not in queue:
    raise SystemExit(f"FAIL: message not queued for partner PDNBBS: {queue!r}")

# Sign off. Spec §1.2 says `73 de <BBSNAME>` — but over the telnet host
# path the 73 text never reaches the client (verified live); the node
# reports `*** Disconnected from Stream 1` / `Disconnected from Node -
# Telnet Session kept` instead. See README "observed deltas".
t.line("B")
try:
    show("bye", t.read_until(b"Disconnected", timeout=10))
except (TimeoutError, ConnectionError) as e:
    print(f"--- bye (tolerated: {e.__class__.__name__}) ---")

print("DRIVER-OK")
PY
DRIVER_RC=$?

echo "--- telnet transcript ---"
cat "$TRANSCRIPT"
echo "-------------------------"

grep -q "DRIVER-OK" "$TRANSCRIPT" || { rm -f "$TRANSCRIPT"; fail "telnet driver did not complete (rc=$DRIVER_RC)"; }
rm -f "$TRANSCRIPT"

echo "SMOKE: asserting the message store on disk (spec §7.4)"
# Primary CI assertion target: the body must be in /data/Mail/m_*.mes.
# Poll briefly — the .mes file is written on save but give the disk a beat.
FOUND=""
for _ in $(seq 1 20); do
    if docker exec pdnbbs-gb7bpq sh -c "grep -l '$NONCE' /data/Mail/m_*.mes 2>/dev/null"; then
        FOUND=yes
        break
    fi
    sleep 0.5
done
[ -n "$FOUND" ] || fail "message body ($NONCE) not found in /data/Mail/m_*.mes"

# The store must also be inspectable from the HOST (the whole point of the
# bind mount). Root-owned but world-readable; tolerate exotic umasks by
# warning instead of failing.
if grep -rqs "$NONCE" "$STATE_DIR/Mail/" 2>/dev/null; then
    echo "SMOKE: host-side state inspection OK ($STATE_DIR/Mail)"
else
    echo "SMOKE: WARN — could not read $STATE_DIR/Mail from the host (perms?); docker exec assertion passed" >&2
fi

echo "SMOKE: PASS"
# trap runs teardown
exit 0
