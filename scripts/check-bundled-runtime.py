#!/usr/bin/env python3
"""Fails if any package bundles a .NET 10 runtime older than the latest patch.

The program in each .deb is self-contained, so the package carries the runtime of whichever SDK
built it, and an operating system update never patches it. A self-hosted runner with a stale SDK
once shipped another packet-net project's armhf packages on runtime 10.0.7 while the rest had
10.0.13, months of security fixes behind, and nothing noticed. This reads the runtime version out
of each package's program and compares it with the latest 10.0 runtime Microsoft lists. A newer one
passes: SDK workloads can run ahead of the release index (the WebAssembly workload did on 2026-10-25,
10.0.14 against a listed 10.0.13), and the point is to catch a stale runner, not one ahead of the index.

Usage: scripts/check-bundled-runtime.py <package.deb>...
"""
import json
import re
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path

INDEX = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"
CHANNEL = "10.0"
PROGRAM = "pdn-bbs"
# Both shared frameworks are bundled, and each is patched on its own, so each is checked.
RUNTIME = re.compile(rb"runtimepack\.Microsoft\.(NETCore|AspNetCore)\.App\.Runtime\.[a-z0-9-]+/(\d+\.\d+\.\d+)")


def latest_runtime():
    with urllib.request.urlopen(INDEX, timeout=30) as response:
        index = json.load(response)
    for channel in index["releases-index"]:
        if channel["channel-version"] == CHANNEL:
            return channel["latest-runtime"]
    sys.exit(f"no .NET {CHANNEL} channel in {INDEX}")


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def program_bytes(package):
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["dpkg-deb", "-x", str(package), tmp], check=True)
        return [(str(p.relative_to(tmp)), p.read_bytes())
                for p in Path(tmp).rglob(PROGRAM) if p.is_file() and not p.is_symlink()]


def main(packages):
    if not packages:
        sys.exit(__doc__)
    want = latest_runtime()
    print(f"latest .NET {CHANNEL} runtime: {want}")
    bad = False
    for package in map(Path, packages):
        programs = program_bytes(package)
        if not programs:
            print(f"::error::{package.name}: no program found to check")
            bad = True
        for name, data in programs:
            found = {}
            for framework, version in RUNTIME.findall(data):
                found.setdefault(framework.decode(), set()).add(version.decode())
            report = ", ".join(f"{f} {'/'.join(sorted(v))}" for f, v in sorted(found.items()))
            print(f"{package.name}: {name} bundles {report or 'nothing recognisable'}")
            if sorted(found) != ["AspNetCore", "NETCore"] or any(len(v) != 1 for v in found.values()):
                print(f"::error::{package.name} bundles .NET runtime {report or 'unknown'}, "
                      f"not one recognisable version for both frameworks. Check how {name} was built.")
                bad = True
            elif any(version_key(next(iter(v))) < version_key(want) for v in found.values()):
                print(f"::error::{package.name} bundles .NET runtime {report}, older than {want} "
                      "for at least one framework. Update the .NET 10 SDK on the runner that built it.")
                bad = True
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
