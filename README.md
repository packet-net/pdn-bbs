# pdn-bbs

A ground-up packet-radio BBS for the [pdn](https://github.com/M0LTE/packet.net) node platform: personal mail + bulletins, the classic terse RF command surface, webmail, and full LinBPQ-mail forwarding compatibility (FBB B1F compressed forwarding; B2F to follow).

Strictly an **app package**: it reaches the node exclusively through public interfaces — the RHPv2 network plane, the app-gateway web identity contract, and a `pdn-app.yaml` manifest. pdn contains zero BBS-specific code.

- [`docs/design.md`](docs/design.md) — architecture + build waves
- [`docs/linbpq-mail-compat.md`](docs/linbpq-mail-compat.md) — the sourced compatibility spec (the §8 MUST line is the compat contract; §9 is the oracle checklist)
- [`docs/release-pipeline.md`](docs/release-pipeline.md) — the `.deb` release + deploy pipeline and the `/usr/share` (code) vs `/var/lib` (state) split

## Releasing + deploying

pdn-bbs ships as a versioned Debian `.deb` (one per arch: amd64 / arm64 / armhf) that installs the self-contained binary + manifest under `/usr/share/packetnet/apps/bbs`. Push a `v*` tag (or run the `publish-bbs` workflow) to cut a GitHub Release; run `scripts/deploy-bbs.sh` for the tight build → deploy → show loop against the lab box. State (`bbs.db`, `bbs.yaml`) lives in `/var/lib/packetnet/apps/bbs` and is preserved across upgrades. See [`docs/release-pipeline.md`](docs/release-pipeline.md).
