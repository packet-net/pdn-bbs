# Release + deploy pipeline

pdn-bbs ships as a versioned Debian `.deb`, exactly like the packet.net node host — no more hand-staging the binary. This note documents the pipeline and, crucially, the **code vs state** split that makes it safe to reinstall over a live BBS.

## The artifact

`pdn-bbs` is a .NET (`net10.0`) app. `dotnet publish` produces a self-contained single-file binary named `pdn-bbs` (the `Bbs.Host` project, `AssemblyName=pdn-bbs`). The `.deb` carries exactly the **code**:

```
usr/share/packetnet/apps/bbs/pdn-bbs       # the self-contained single-file binary (0755)
usr/share/packetnet/apps/bbs/pdn-app.yaml  # the app manifest, copied from the repo root (0644)
```

That is all. No config, no database, no systemd unit (the BBS is supervised by the packetnet node, not by systemd directly).

### Publish flags (and why)

```
-r <rid> --self-contained true
-p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true
-p:InvariantGlobalization=true
-p:DebugType=none -p:DebugSymbols=false
```

- **`IncludeNativeLibrariesForSelfExtract=true` is not optional.** The BBS uses `Microsoft.Data.Sqlite`, which carries the native `e_sqlite3` library. With single-file publish but *without* this flag, that native lib is left loose next to the binary instead of bundled into it — so a `.deb` that ships only `pdn-bbs` crashes on startup with `DllNotFoundException: e_sqlite3`. Learned the hard way.
- **No `PublishReadyToRun` / crossgen2.** R2R OOMs on cross-publish (the arm64 cross-publish is the heaviest) and the cold-start gain for the BBS is marginal. Plain self-contained cross-publish from x64 is fine for all three arches, so the whole release builds on one self-hosted x64 runner with no arch-native machines and no cross C-toolchain.

## Code vs state: `/usr/share` vs `/var/lib`

The packet.net node discovers app packages by scanning **two** roots:

| Root | Owner | What lives there |
|------|-------|------------------|
| `/usr/share/packetnet/apps` | the package (root, read-only to the service) | **code** — `pdn-bbs` + `pdn-app.yaml` |
| `/var/lib/packetnet/apps`   | the `packetnet` service user (0750) | **state** — `bbs.db`, `bbs.yaml`, `*.db-wal`/`*.db-shm` |

Each app's state dir is always `/var/lib/packetnet/apps/<id>` regardless of where its code was discovered. So for the BBS:

- The `.deb` installs **code** to `/usr/share/packetnet/apps/bbs/`.
- The BBS writes its **state** to `/var/lib/packetnet/apps/bbs/` (a commented default `bbs.yaml` on first run; `bbs.db` as messages arrive).

The `.deb` **never** ships `bbs.yaml` or `bbs.db`, and they are **not** listed as conffiles (they're runtime state, not configuration the packager owns). On upgrade, dpkg replaces only the code under `/usr/share`; the state under `/var/lib` is untouched, so message history and the operator's config survive.

The `postinst` only prepares the state dir (`install -d -o packetnet -g packetnet -m 0750 /var/lib/packetnet/apps/bbs`) **if the `packetnet` user exists** — the node host package owns that user, and the BBS is a soft `Recommends: packetnet`, so on a standalone install the user may be absent. pdn creates the per-app state dir at runtime anyway, so an absent dir here is harmless. The `postinst` is idempotent and deliberately does **not** restart the packetnet service.

## Don't leave hand-staged code under `/var/lib` (later-root-wins)

When the node finds the same app `id` under both roots, **the later root wins** — `/var/lib` overrides `/usr/share`. That's the right rule for an owner who wants to override a bundled app, but it's a trap if a box still has the *old hand-staged* layout: before this pipeline the BBS binary + manifest were hand-copied into `/var/lib/packetnet/apps/bbs/`, and if fresh code is installed under `/usr/share` while the old code lingers under `/var/lib`, the node keeps running the **stale** `/var/lib` copy forever.

Neither the `.deb` nor `scripts/deploy-bbs.sh` touches `/var/lib` — they only install code under `/usr/share`, and `/var/lib` is left entirely to state (`bbs.db`, `bbs.yaml`, `*.db-wal`/`*.db-shm`). The one box that had the old hand-staged layout (the lab) was migrated off it **once, by hand**: strip exactly `pdn-bbs` + `pdn-app.yaml` from `/var/lib/packetnet/apps/bbs/`, keep the state. After that `/usr/share` is authoritative and every future `.deb` upgrades the code in place. (This is a one-off, not codified — a normal install is `/usr/share`-only from the start.)

## The two entry points

### Release — `.github/workflows/publish-bbs.yml`

Triggered by a `v*` tag (or a manual `workflow_dispatch` with an explicit version). Runs on `[self-hosted, Linux, X64]` (no GitHub-hosted runners — this repo has no hosted-runner budget). Resolves the version from the tag (`${GITHUB_REF#refs/tags/v}`) or the dispatch input, loops the three RIDs through `scripts/build-deb.sh`, `sha256sum`s the three `.deb`s into `SHA256SUMS`, and `gh release create`s the tag with the three `.deb`s + `SHA256SUMS`.

```
git tag v0.1.0 && git push origin v0.1.0      # → builds amd64/arm64/armhf, cuts the release
```

### Dev loop — `scripts/deploy-bbs.sh`

The tight build → deploy → show loop against the live lab box (`root@packetdotnet`), no CI wait, same artifact shape GHA ships:

```
scripts/deploy-bbs.sh            # build amd64 (PDN_FAST=1), scp, dpkg -i, restart, verify
scripts/deploy-bbs.sh --logs     # …then follow the journal
scripts/deploy-bbs.sh --skip-build   # redeploy the most recent existing .deb
```

It builds with `PDN_FAST=1`, which is the named dev-loop seam — it skips nothing critical (single-file + the bundled sqlite lib stay on, or the app won't start). After install it restarts the packetnet node and prints a liveness summary: service active, `/healthz`, the bbs app starting in the journal, and the **preserved message count** (read via `python3`'s `sqlite3` — there's no `sqlite3` CLI on the box).

## Manual one-arch build

```
scripts/build-deb.sh linux-x64 0.1.0           # → artifacts/pdn-bbs_0.1.0_amd64.deb
scripts/build-deb.sh linux-arm64 0.1.0         # → artifacts/pdn-bbs_0.1.0_arm64.deb
PDN_FAST=1 scripts/build-deb.sh linux-x64 ...  # dev-loop flags (same critical flags today)
```

RID → arch map: `linux-x64`→`amd64`, `linux-arm64`→`arm64`, `linux-arm`→`armhf`.
