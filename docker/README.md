# LinBPQ mail oracle (`compose.oracle.yml`)

A docker compose stack standing up **LinBPQ with BPQMail enabled** as the forwarding-interop oracle for the future `Bbs.Interop.Tests`. The oracle is the de-facto arbiter our BBS must interoperate with; the wire-level contract it embodies is specified in [`docs/linbpq-mail-compat.md`](../docs/linbpq-mail-compat.md) (§7 is the stand-up recipe this directory implements).

Two services:

| Service | Container | Image | Role |
|---|---|---|---|
| `gb7bpq` | `pdnbbs-gb7bpq` | `m0lte/linbpq@sha256:872343ff…` (~6.0.25.23, same pin as packet.net's interop stack) | LinBPQ node **GB7BPQ** + BPQMail BBS **GB7BPQ-1** (alias `BPQBBS`), mail enabled via `LINMAIL`, partner entry for **PDNBBS-1** (our BBS) |
| `netsim` | `pdnbbs-netsim` | `ghcr.io/packethacking/net-sim@sha256:0fb89608…` | Simulated RF channel (afsk1200, FM-capture). LinBPQ dials node `b` (`172.31.0.12:8202`) as a KISS-TCP client; node `a` is the external attach point for our side |

The stack runs **alongside** packet.net's interop stack: distinct compose project (`pdnbbs-oracle`), subnet (`172.31.0.0/24` vs `172.30.0.0/24`), container names, and host ports.

## Running it

```sh
docker compose -f docker/compose.oracle.yml up -d --wait   # bring up, gated on healthchecks
bash docker/oracle/smoke.sh                                 # full clean-slate smoke (down -v → up → drive → assert → down -v)
docker compose -f docker/compose.oracle.yml down -v         # tear down
```

`smoke.sh` needs `docker compose` v2, `bash`, and `python3` (the telnet driver is an embedded here-script). It is self-contained: it starts from, and returns to, a torn-down state.

The oracle is a **stateful singleton** (one forwarding-partner identity, one RF channel): two actors driving one instance poison each other's sessions (observed delta 9 below). If somebody else is using the stack on this box, run a private copy (clone the compose file with offset host ports / a distinct subnet+project, and sed the `bpq32.cfg` netsim IP to match) and point the tests at it via `PDNBBS_ORACLE_KISS_PORT` / `PDNBBS_ORACLE_TELNET_PORT` / `PDNBBS_ORACLE_CONTAINER` (see `OracleFixture`). CI and ordinary local runs need none of these.

## Port map (all loopback-only on the host)

| Host | Container | What |
|---|---|---|
| `127.0.0.1:8200` | netsim `:8200` | **KISS-TCP attach, netsim node `a`** — our side of the simulated RF channel. Attach a KISS TNC client here and connect AX.25 to `GB7BPQ` (node) or `GB7BPQ-1` (BBS direct) |
| `127.0.0.1:8180` | netsim `:8080` | net-sim web UI + `/api/status` |
| `127.0.0.1:8210` | gb7bpq `:8010` | **Telnet** (node prompt → `BBS`). `admin`/`admin` = node SYSOP **and** BBS sysop; `user`/`user` = plain user |
| `127.0.0.1:8211` | gb7bpq `:8011` | **FBBPORT** — raw-TCP connections land directly on the BBS application (drive the FBB forwarding protocol without the AX.25 leg) |
| `127.0.0.1:8208` | gb7bpq `:8008` | LinBPQ web UI incl. Mail Mgmt |

## Where state lives

`gb7bpq`'s `/data` is a **host bind mount** at `docker/oracle/state/` (gitignored) so tests can assert against the message store directly (spec §7.4):

| Path (host: `docker/oracle/state/…`) | Content |
|---|---|
| `Mail/m_%06d.mes` | one file per message body, raw text incl. `R:` lines — **the primary interop assertion target** |
| `linmail.cfg` | live BPQMail config (rewritten by the BBS itself; seeded from `docker/oracle/linmail.cfg` on first boot only) |
| `DIRMES.SYS`, `WFBID.SYS` | message-header array, BID database |
| `logs/log_YYMMDD_BBS.txt` | BBS log — routing traces (`Routing Trace … Matches AT PDNBBS-1`), dial-outs, `Message Rejected by BID Check`, … |

Files are root-owned (the container runs as root); `smoke.sh` wipes the directory with a throwaway container, never host `rm`. `docker compose down -v` does **not** clear a bind mount — a clean slate is `down -v` **plus** the state wipe (smoke.sh does both).

## The partner entry (what the oracle is configured to do with us)

`docker/oracle/linmail.cfg` seeds a `BBSForwarding.PDNBBS` record plus the mandatory `BBSUsers.PDNBBS` record with the **F_BBS flag (0x10)** — that user record is what makes LinBPQ treat an inbound connect from our BBS as a forwarding session at all (SID exchange instead of the chatty user banner). It is keyed by the **base callsign** (`PDNBBS`, no SSID): LinBPQ strips the SSID on inbound AX.25 connects *before* the user lookup (`BBSUtilities.c` ~10827), so an SSID-keyed record never matches an AX.25 partner — and worse, the dial-in lands on an auto-created plain user whose identity doesn't join to the partner's forward-queue bits, so **in-session reverse toward a dialling `PDNBBS-1` silently never proposes anything** (found by `BidirectionalForwardingInteropTests`; the GB7RDG production snapshot confirms real deployments base-key all partner records). Semantics, for the future outbound test:

- **Protocol: FBB compressed B1 ("B1F"), not B2F** — `AllowBlocked=1, AllowCompressed=1, UseB1Protocol=1, UseB2Protocol=0`. Expect FA proposals, `F>` checksum, FS `+ - =`, SOH/STX/EOT framing with the LZHUF N=2048 payload (spec §3.6–3.7).
- **Routing to us**: `ATCalls = PDNBBS|PDNBBS-1` (a message `@ PDNBBS` or `@ PDNBBS-1` queues for us — plus the implied-AT rule on the partner's own call), `HRoutes = WW` (all flood bulletins), `HRoutesP = GBR.EURO`, `BBSHA = PDNBBS.#23.GBR.EURO`.
- **Forward timing**: `FWDTimes = ""` (no time-band restriction), `FwdInterval = 2` **seconds** — the per-partner scan fires every ~2 s and dials `ConnectScript` (`C 2 PDNBBS-1`, port 2 = the netsim channel) whenever mail is queued (`FWDNewImmediately` is set but VESTIGIAL in this build — no engine code reads it; spec §4.1), and keeps redialling (~30 s per failed cycle: SABM retries then back-off) until something answers as `PDNBBS-1` on the channel. A test that listens on netsim node `a` as `PDNBBS-1` gets called without any prodding; the deterministic sysop levers (`FWD PDNBBS-1 NOW`, `DOHOUSEKEEPING`, `REROUTEMSGS`) also work from an `admin` telnet session.
- **No `SKIPPROMPT` needed**: the script is the plain `C 2 PDNBBS-1` — after its last line LinBPQ waits for a SID or `>` (spec §4.4), and our host **greets immediately** on accept (its SID is the first line on the wire, the greet-immediately demux). The host-path inbound interop test passing against this stock script IS the proof; if our side ever went silent-first again the oracle's dial would sit in that wait and the cycle would time out.
- **Reverse**: `RequestReverse = 0` (the oracle doesn't dial with an empty queue). Reverse forwarding *within* a session (we connect in, send `FF`, the oracle proposes its queued traffic) is inherent to the FBB block flow (spec §3.11) and needs no extra config.
- **Acceptance**: `MaxRXSize = 99999`, BIDs accepted and deduped (`WFBID.SYS`), `DontHoldNewUsers = 1` / `AllowAnon = 1` so smoke/test users are usable immediately. Housekeeping group is seeded empty (defaults: 30-day lifetimes, BID lifetime 60 — sane for an ephemeral CI container that's wiped every run).

## How `Bbs.Interop.Tests` uses it (the lane is live — `tests/Bbs.Interop.Tests`, CI job `interop`)

1. `docker compose -f docker/compose.oracle.yml up -d --wait` (CI step before the test run; the healthchecks gate readiness).
2. Mark oracle-dependent tests `[Trait("Category", "Interop")]` and run them with `--filter "Category=Interop"`; everything else keeps excluding the category (the packet.net convention).
3. Inbound direction (oracle → us): listen as `PDNBBS-1` on KISS-TCP `127.0.0.1:8200`, post a message `@ PDNBBS` via telnet `:8210` (or just wait — anything already queued redials every few seconds), accept the connect, do the SID/FA/FS/B1 exchange, assert on what we received.
4. Outbound direction (us → oracle): connect AX.25 to `GB7BPQ-1` via `:8200` (or raw TCP via `:8211`), forward a message, then assert **on the wire** (FS codes) and **on disk**: poll `docker/oracle/state/Mail/m_*.mes` for the body (the m0lte harness pattern), grep `state/logs/log_*_BBS.txt` for the routing trace / BID-reject lines.
5. `docker compose -f docker/compose.oracle.yml down -v` + wipe `docker/oracle/state/` between runs (steal `wipe_state` from `smoke.sh`).

Two **lanes** per direction:

- **Adapter lane** (`InboundForwardingInteropTests`, `OutboundForwardingInteropTests`): the FBB pump transcribed over the AX.25 leg directly (`Ax25FbbSessionRunner`) — fastest pinpointing of wire-level behaviour.
- **Host-path lane** (`HostInboundForwardingInteropTests`, `HostOutboundScriptInteropTests`): the REAL composed host (`HostComposition.Build` — real RhpNodeLink/InboundDemux/scheduler/store, webmail on an ephemeral port) attached to the wire-faithful in-test RHP node (`FakeRhpServer`, compile-linked from the W5 lane), whose accepted/dialled streams are bridged byte-for-byte onto the AX.25 leg by `RhpAx25Bridge`. The bridge occupies exactly the seam a real pdn node does (the RHP wire is the pinned contract), so nothing inside the host is faked. The inbound test asserts the greet-immediately flow end to end (our SID is the FIRST line the host emits; the oracle's stock no-SKIPPROMPT script completes; the message lands in the host store). The outbound test drives the spec §4.4 connect-script navigation: `connectScript: [C GB7BPQ, BBS]` opens the NODE callsign and enters the mail app via the `BBS` APPLICATION verb at the node prompt, then the real FBB cycle lands the message in the oracle's `.mes` store.

## What the smoke proves (`docker/oracle/smoke.sh`)

From a clean slate: stack comes up healthy → telnet login (`admin`/`admin`) → `BBS` answers with the BPQMail SID + `de GB7BPQ-1>` prompt → `S PDNTEST @ PDNBBS` walks the exact §1.5 entry flow (`Enter Title (only):` → `Enter Message Text (end with /ex or ctrl/z)` → `/EX`) → acceptance shape `Message: n Bid:  n_GB7BPQ-1 Size: …` → `L` lists it → `FWD QUEUE` shows `PDNBBS-1 1 Msgs` (sysop status works **and** the message routed to our partner queue) → the body is present in `/data/Mail/m_*.mes` (container) and readable from `docker/oracle/state/Mail/` (host). Exit 0 = all of that held; the stack is torn down either way.

i.e. the oracle answers like a BBS *and* the forwarding partner plumbing (routing + dial-out over the simulated RF channel) is live — before any C# exists.

## Observed deltas vs the spec ([VERIFY-ORACLE] discipline — this stack IS the verification)

Verified live against the pinned image, 2026-06-11. The spec's §7 recipe works as written, with these corrections/refinements:

1. **libconfig comments are `//` / `#` / `/* */`, not `;`.** The §7.2 seed example shows `;`-prefixed comment lines inside `linmail.cfg`; libconfig has no `;` comments (it's the value terminator) and the real binary would reject the file as corrupt. `docker/oracle/linmail.cfg` uses `//`.
2. **BBS-sysop needs a seeded `F_SYSOP` (0x08) user record.** §7.3's "from 127.0.0.1 no password needed" doesn't apply in compose: connections arrive from the docker bridge gateway IP, not loopback. `conn->sysop` requires the user's `F_SYSOP` flag **and** a Secure_Session (`BBSUtilities.c:10926`) — the node-level `USER=admin,…,SYSOP` telnet login provides the Secure_Session, and the seeded `BBSUsers.GB7BPQ` record provides the flag. Without it, `FWD`/`DOHOUSEKEEPING` answer `… needs SYSOP status` (observed).
3. **Pace interactive lines ≥0.3 s apart** (spec §3.13.2's bare-CR misparse, confirmed live over telnet): message body and `/EX` sent back-to-back in one TCP segment get demultiplexed as one buffer and the terminator is swallowed — the session sticks in text-entry mode. The m0lte harness's 0.3 s spacing fixes it.
4. **TO callsigns list 6-char-truncated** (`PDNTEST` → `PDNTES @PDNBBS`) — §1.5's "TO/FROM truncated to 6 chars" confirmed; don't grep listings for 7-char TO calls.
5. **`B` sign-off over the telnet host path emits no `73 de …`** to the client — the session ends with the node's `*** Disconnected from Stream 1` / `Disconnected from Node - Telnet Session kept`. (Spec §1.2's `73 de <BBSNAME>` may still apply on RF paths — feeds [VERIFY-ORACLE #6]'s prompt-shapes probe.)
6. **The user-session SID letter run varies with user flags** — a WLE-flagged auto-created user saw `[BPQ-6.0.25.23-B2FWIHJM$]`, the seeded sysop user sees `[BPQ-6.0.25.23-IHJM$]`. Don't read capability gates off a *user* session's SID; the forwarding-session SID (what `PDNBBS-1` will see) is governed by the per-partner `Allow…/Use…` gates.
7. **Mail enablement per §7.2 works exactly as written**: `LINMAIL` + `APPLICATION 1,BBS,,GB7BPQ-1,BPQBBS,255` in `bpq32.cfg` (no `mail` command-line arg needed), linmail.cfg seeded copy-on-first-boot into the writable volume (BPQMail rewrites it — a `.bak` appears next to it), message store and acceptance/list shapes all as specified.
8. **Inbound AX.25 connects reach BPQMail with the SSID stripped** (verified live by the interop lane): a connect from `PDNBBS-1` over the netsim port is logged `Incoming Connect from PDNBBS on Port 2` and lands on an auto-created user **`PDNBBS`** — the seeded F_BBS `BBSUsers.PDNBBS-1` record does **not** gate this direction (it gates the *outbound* dial + queue under `BBSForwarding.PDNBBS-1`). BPQMail still flips into FBB forwarding mode when our SID arrives, but it answers with the auto-user's SID shape (`[BPQ-…-B2FWIHJM$]`, delta 6) rather than the partner-configured B1F — harmless to a B1F caller (B1 is negotiated down), and the transfer/`FS +`/store path is identical. Don't assert partner-record semantics on the us→oracle direction.
9. **One forwarding session per partner at a time**: while the oracle holds an (even half-dead) session it believes is the `PDNBBS-1` forwarding partner, it won't service that queue again until its inactivity timeout (~100 s) reaps the old one. A test listener that vanishes without DISCing leaves exactly that state behind — `Bbs.Interop.Tests`' AX.25 endpoint tears its sessions down with a DISC on dispose for this reason.

## Files

```
docker/
  compose.oracle.yml    the stack (gb7bpq + netsim)
  README.md             this file
  .gitignore            ignores oracle/state/
  oracle/
    bpq32.cfg           LinBPQ node config (read-only bind; LINMAIL + APPLICATION)
    linmail.cfg         BPQMail seed (copied into /data on first boot only)
    network.yaml        net-sim topology (2 nodes, afsk1200)
    smoke.sh            clean-slate smoke test (exit 0 = oracle answers like a BBS)
    state/              runtime state bind mount (gitignored, root-owned)
```

Provenance: compose/healthcheck/netsim patterns from `packet.net/docker/` (same pinned images); `linmail.cfg` shape from `docs/linbpq-mail-compat.md` §7.2, which is itself validated by `m0lte/linbpq` `tests/integration/helpers/bpqmail_cfg.py` + the two-instance forwarding test; the telnet driver in `smoke.sh` is adapted from that repo's `helpers/telnet_client.py`.
