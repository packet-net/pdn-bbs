# Canonical-F6FBB interop lane (`Category=InteropF6fbb`)

The `Bbs.Interop.Tests` `F6fbb*` tests drive pdn-bbs's **production** `FbbSessionRunner` against
**real canonical F6FBB** (LinFBB 7.0.11 — the original Jean-Paul Roubelat code), over AXUDP, to catch
divergences that the LinBPQ oracle lane (a *re*implementation of FBB) can't. This is the heavyweight
sibling of the [LinBPQ mail oracle](../docker/README.md): same idea, but the peer is a whole VM
instead of a container.

## The dependency: an out-of-band VM

The peer is the **f6fbb-interop rig** — a QEMU guest running a 6.18 kernel with in-kernel AX.25,
ve7fet `ax25ipd`, and LinFBB, reachable over AXUDP. It lives in its **own repo**
([`M0LTE/f6fbb-interop`](https://github.com/M0LTE/f6fbb-interop)) and is **never vendored or built by
pdn-bbs** — it's a kernel + rootfs + F6FBB build, far too heavy to carry here. pdn-bbs only points at
its AXUDP endpoint.

| | |
|---|---|
| Default endpoint | `192.168.76.2:10093` (VM side; host side `192.168.76.1:10093`) |
| Override | `PDNBBS_F6FBB_HOST` / `PDNBBS_F6FBB_PORT` (mirrors `PDNBBS_ORACLE_*`) |
| Strict mode | `PDNBBS_F6FBB_REQUIRED=1` → unreachable rig **fails** instead of skipping |

## How the tests behave without the rig

Every `F6fbb*` test is a `[SkippableFact]` that calls `F6fbbRig.RequireAsync()` first. That probes the
endpoint **once** per run:

- **Rig reachable** → the tests run normally.
- **Rig absent** → they **skip** (not fail). A developer running the whole suite locally without the
  VM just sees skips. `[Trait("Category","InteropF6fbb")]` also keeps them out of the fast CI lane
  (`ci.yml`'s `test` job filters `Category!=InteropF6fbb`), so they never pay the probe timeout there.
- **Rig absent but `PDNBBS_F6FBB_REQUIRED=1`** → they **fail**. The on-demand CI job sets this: it
  just booted the rig, so an unreachable rig means a broken bridge/route, not a legitimate absence —
  it must not masquerade as all-green.

`InteropF6fbbDebug` is a diagnostic transcript-dumper, excluded from both CI lanes.

## Running it locally

```sh
# 1. In an f6fbb-interop checkout, boot the rig (tap mode; KVM if /dev/kvm, else TCG):
make run            # foreground, or `make interop` for boot+selftest+teardown

# 2. In pdn-bbs, run just the F6FBB lane:
dotnet test --filter "Category=InteropF6fbb"
```

Override the endpoint if your rig isn't on the default address:

```sh
PDNBBS_F6FBB_HOST=10.0.0.5 dotnet test --filter "Category=InteropF6fbb"
```

## Running it in CI

`.github/workflows/interop-f6fbb.yml` — **`workflow_dispatch` only, never scheduled, never a PR gate.**
Trigger it from **Actions ▸ interop-f6fbb ▸ Run workflow**. It checks out the rig, boots it (NET=tap,
waiting for the rig's `RIG-SELFTEST RESULT=PASS` marker), runs `Category=InteropF6fbb` with
`PDNBBS_F6FBB_REQUIRED=1`, and tears the VM down. The bootable image is cached (keyed on the rig's
build inputs) so only the first run pays the full `make all`.

Runner prerequisites (self-hosted): `/dev/kvm` (falls back to slow TCG), passwordless `sudo` for the
`f6fbbr0` bridge + tap, and the `make all` build deps.
