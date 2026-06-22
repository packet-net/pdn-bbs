# LinFBB (F6FBB) interop oracle

This stack stands up **Jean-Paul Roubelat F6FBB's own BBS software** — LinFBB,
the implementation the FBB forwarding protocol is named after — so the interop
suite proves forwarding against a *real F6FBB instance*, not captured or
simulated traffic. It is the F6FBB counterpart of the LinBPQ oracle
(`docker/compose.oracle.yml`, `docker/README.md`).

Transport is **TCP/telnet** (F6FBB `port.sys` interface 9 type T): F6FBB has no
userspace AX.25 stack — its RF path needs the Linux *kernel* AX.25 stack, which
is fragile inside CI containers — whereas its TCP/telnet forward works with no
kernel support. (This was a deliberate transport choice for the lane.)

## What runs against it

- `tests/Bbs.Interop.Tests/F6fbbOutboundForwardingInteropTests.cs` — us → F6FBB:
  the production FBB FSM forwards a message over a live TCP connection to F6FBB's
  forward port; the message must land in F6FBB's on-disk store (asserted via
  `docker exec`). Tagged `Category=InteropF6fbb`.
- The fast, no-docker wire-conformance suite
  `tests/Bbs.Fbb.Tests/F6fbbInteropTests.cs` complements this lane (it does not
  need the oracle).

## Run it locally

```sh
docker compose -f docker/compose.f6fbb.yml up -d --build --wait
dotnet test -c Release --filter "Category=InteropF6fbb"
docker compose -f docker/compose.f6fbb.yml down -v
```

Ports: TCP/telnet forward on `127.0.0.1:8311` (container `6300`). The FBB var
tree (message store, logs) is bind-mounted at `docker/f6fbb/state/`
(gitignored, container-owned — wipe via a throwaway container, as the CI job
does).

## ⚠ Validation status — needs a CI round-trip

The C# side (the TCP bearer `TcpByteSession`, the `F6fbbOracleFixture`, and the
interop test) mirrors the proven LinBPQ lane and is the solid part. The **LinFBB
oracle build + config could not be executed in the environment this was authored
in** (no docker daemon), and the exact LinFBB config-file syntax
(`init.srv`/`port.sys`/`forward.sys`/`passwd.sys`, and how a BBS-flagged
forwarding partner is provisioned) is not fully documented in publicly
reachable sources. Every config file here is **best-effort and marked
`VALIDATE-IN-CI`**.

### Iteration loop

1. `docker compose -f docker/compose.f6fbb.yml up --build` and watch the logs.
2. `docker exec -it pdnbbs-f6fbb sh` — compare our seeded files in
   `/opt/fbb/etc/ax25/fbb/` against the `make installconf` defaults the pinned
   SVN revision shipped, and reconcile column orders / directive names.
3. Confirm xfbbd actually listens on `6300` and answers the telnet login; tune
   the prompt wording match in `TcpByteSession.NavigateLoginAsync` if needed.
4. Provision `PDNBBS` as a BBS-flagged forwarding partner (HA
   `PDNBBS.#23.GBR.EURO`, password `PDNBBSFWD` = `F6fbbOracleFixture
   .PartnerPassword`) so the forward authenticates.
5. Once green, pin `SOURCE_REV` in `compose.f6fbb.yml`/`Dockerfile` and add
   `interop-f6fbb` to the release job's `needs:` in `.github/workflows/ci.yml`.

The inbound direction (F6FBB dials our `FbbTcpListener`) is scaffolded
(`forward.sys` connect-script + `FBB_FWD_TARGET`) but not yet covered by a test;
add it once the outbound lane is proven.
