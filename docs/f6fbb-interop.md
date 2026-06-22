# F6FBB interoperability

The FBB forwarding protocol is named after Jean-Paul Roubelat **F6FBB** and his
BBS software. pdn-bbs proves it interoperates with F6FBB along two lanes.

## 1. Fast wire-conformance lane (no docker)

`tests/Bbs.Fbb.Tests/F6fbbInteropTests.cs` drives the real `FbbSession` FSM and
codecs with the exact bytes an FBB BBS puts on (and expects off) the wire,
sourced from the official FBB protocol documentation (`docs/linbpq-mail-compat.md`
references [FBB-PROTO], [FBB-APP9], [FBB-APP10], [FBB-SID]):

- F6FBB SID dialects (`[FBB-7.0.8-AB1FHMRX$]`, `[FBB-5.15-B1FHM$]`, the legacy
  CRC-less `[FBB-5.11-BFHM$]`) → correct container negotiation, with the
  FBB-specific `A`/`R`/`X` feature letters inert.
- Full compressed forwarding exchanges in **both roles** (F6FBB calls us / we
  dial F6FBB), with F6FBB's `FA`/`FB` proposals, its `F> HH` checksum, its
  French node banner + bare `de F6FBB>` prompt, and FBB R: routing headers.
- The crux: FBB LZHUF is **our** LZHUF, byte-for-byte (the golden vectors come
  from `lzhuf_1.c`/`lzhuf32.c`, the canonical FBB compressor), including the
  N=2048 window-wrap case a stock LZHUF would get wrong.

Runs in the normal unit lane (`Category!=Interop&Category!=InteropF6fbb`).

## 2. Live-instance lane (real LinFBB over TCP/telnet)

`tests/Bbs.Interop.Tests/F6fbbOutboundForwardingInteropTests.cs`
(`Category=InteropF6fbb`) forwards a message to a **live LinFBB** instance — F6FBB's
own software, built from SVN — over a real TCP connection and asserts the message
lands in F6FBB's on-disk store. No captured/simulated traffic.

- Oracle stack: `docker/compose.f6fbb.yml` + `docker/f6fbb/` (see its README).
- Transport: TCP/telnet (`port.sys` interface 9 type T). F6FBB's RF path needs
  the kernel AX.25 stack, which is fragile in CI; the TCP/telnet forward isn't.
- CI: the `interop-f6fbb` job stands the stack up and runs the lane. It is **not
  yet a release gate** — the LinFBB oracle build/config needs a CI round-trip to
  finalise (it could not be executed where this was authored). See
  `docker/f6fbb/README.md` for the iteration loop and current validation status.
```
docker compose -f docker/compose.f6fbb.yml up -d --build --wait
dotnet test -c Release --filter "Category=InteropF6fbb"
```
