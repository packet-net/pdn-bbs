#!/bin/sh
# Entrypoint for the LinFBB (F6FBB) interop oracle.
#
# Seeds our config into the writable config dir on first boot (FBB rewrites parts
# of its config in place, so we seed-and-run rather than mount read-only — same
# pattern as the LinBPQ oracle), wires the inbound-forward target from the
# environment, ensures the data tree exists, then runs xfbbd in the foreground.
#
# ⚠ VALIDATE-IN-CI: the exact config dir, the xfbbd invocation, and whether
# installconf already populated a usable user/partner DB all need a CI round-trip
# to confirm (this environment has no docker daemon). docker/f6fbb/README.md
# documents the iteration loop. Keep this script POSIX sh (the slim image's
# /bin/sh is dash).
set -eu

PREFIX="${FBB_PREFIX:-/opt/fbb}"
CONFDIR="${PREFIX}/etc/ax25/fbb"
DATADIR="${PREFIX}/var/ax25/fbb"

# Where, as seen from inside this container, the pdn-bbs FbbTcpListener lives.
# Used only by the inbound-direction lane (the oracle dials us). The outbound
# lane (we dial the oracle) does not need it.
FBB_FWD_TARGET="${FBB_FWD_TARGET:-host.docker.internal 8312}"

mkdir -p "${CONFDIR}" "${DATADIR}" "${DATADIR}/mail"

# Seed each config file only if absent (first boot), then patch in the runtime
# forward target. `make installconf` may have placed defaults here already; our
# seeds intentionally overwrite the few files the interop lane pins.
for f in init.srv port.sys forward.sys passwd.sys; do
    if [ -f "/seed/${f}" ]; then
        cp "/seed/${f}" "${CONFDIR}/${f}"
    fi
done

# Rewrite the connect-script placeholder with the real inbound-forward target.
if [ -f "${CONFDIR}/forward.sys" ]; then
    sed -i "s|PDNBBS_FWD_TARGET|${FBB_FWD_TARGET}|g" "${CONFDIR}/forward.sys"
fi

echo "LinFBB oracle starting: BBS=F6FBB conf=${CONFDIR} data=${DATADIR} fwd-target='${FBB_FWD_TARGET}'"

# Run the daemon in the foreground so the container's lifetime tracks it.
# xfbbd backgrounds itself on some builds; if so, fall back to a log tail so the
# container stays up and `docker exec` store assertions keep working.
"${PREFIX}/sbin/xfbbd" "${CONFDIR}" || "${PREFIX}/sbin/xfbbd" || true
exec tail -F "${DATADIR}/fbb.log" 2>/dev/null || exec sleep infinity
