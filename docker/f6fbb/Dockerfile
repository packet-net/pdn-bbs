# LinFBB (F6FBB BBS) oracle for pdn-bbs's F6FBB interop lane.
#
# There is no maintained F6FBB / LinFBB container image, so this builds it from
# the upstream SVN trunk exactly as the project documents
# (https://sourceforge.net/p/linfbb/wiki/SVNCompiling/ and the eindhoven.space
# "Build and Configure FBB from SVN" guide): autogen → configure → make →
# install → installconf. libax25 dev headers are MANDATORY (configure fails
# without them), so they are installed even though this oracle forwards over
# TCP/telnet (port.sys interface 9 type T), not over a TNC/AX.25 port.
#
# Pin note: SVN trunk is not content-addressable like the LinBPQ oracle's image
# digest. SOURCE_REV pins a revision for reproducibility; bump it deliberately
# (build, smoke, PR) the way the LinBPQ image digest is bumped.
FROM debian:bookworm-slim

ARG SOURCE_URL=https://svn.code.sf.net/p/linfbb/code/trunk
# HEAD by default; set to a fixed revision (e.g. --build-arg SOURCE_REV=618) to pin.
ARG SOURCE_REV=HEAD
ARG PREFIX=/opt/fbb

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential \
        autoconf \
        automake \
        subversion \
        libax25 \
        ax25-tools \
        ax25-apps \
        libax25-dev \
        libncurses-dev \
        ca-certificates \
        procps \
    && rm -rf /var/lib/apt/lists/*

# Build LinFBB from SVN trunk into $PREFIX (kept out of the OS root, per the guide).
RUN set -eux; \
    svn checkout -r "${SOURCE_REV}" "${SOURCE_URL}" /usr/src/linfbb; \
    cd /usr/src/linfbb; \
    ./autogen.sh; \
    ./configure --prefix="${PREFIX}"; \
    make -j"$(nproc)"; \
    make install; \
    make installconf

# Our oracle config templates (callsign F6FBB, a TCP/telnet forward port, and the
# PDNBBS forwarding partner). The entrypoint seeds these into the writable config
# dir on first boot — FBB rewrites parts of its config in place, so we seed-and-run
# rather than bind a read-only config (same pattern as the LinBPQ oracle).
COPY config/ /seed/
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

ENV FBB_PREFIX=${PREFIX}
# The TCP/telnet forward+login port xfbbd listens on inside the container.
EXPOSE 6300

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
