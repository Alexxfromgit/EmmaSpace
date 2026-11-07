#!/usr/bin/env python3
"""Copies the files that belong on a GitHub release out of a Velopack output folder.

The pack folder also holds the previous release (vpk download puts it there so deltas can be built),
and its release indexes list every version. GitHub releases must only carry this version: Velopack's
GithubSource reads releases.<channel>.json from each GitHub release and downloads the packages from
that same release. So this copies the files listed in assets.<channel>.json (installer, portable
build, full and delta packages) and writes indexes that only list this version.

Usage: stage-release.py --pack-dir DIR --channel NAME --version X.Y.Z --out DIR
"""
import argparse
import json
import pathlib
import shutil
import sys


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--pack-dir", type=pathlib.Path, required=True)
    p.add_argument("--channel", required=True)
    p.add_argument("--version", required=True)
    p.add_argument("--out", type=pathlib.Path, required=True)
    a = p.parse_args()

    src, ch, ver = a.pack_dir, a.channel, a.version
    a.out.mkdir(parents=True, exist_ok=True)

    assets = json.loads((src / f"assets.{ch}.json").read_text(encoding="utf-8-sig"))
    names = [entry["RelativeFileName"] for entry in assets]
    for name in names:
        shutil.copy2(src / name, a.out / name)

    index = json.loads((src / f"releases.{ch}.json").read_text(encoding="utf-8-sig"))
    index["Assets"] = [entry for entry in index["Assets"] if entry["Version"] == ver]
    if not any(entry["Type"] == "Full" for entry in index["Assets"]):
        print(f"releases.{ch}.json has no full package for {ver}", file=sys.stderr)
        return 1
    (a.out / f"releases.{ch}.json").write_text(json.dumps(index, separators=(",", ":")), encoding="utf-8")

    # Legacy Squirrel-style index (Velopack still reads it from older clients); same filtering.
    legacy = src / f"RELEASES-{ch}"
    if legacy.exists():
        lines = legacy.read_text(encoding="utf-8-sig").splitlines()
        keep = [line for line in lines if len(parts := line.split()) >= 2 and parts[1] in names]
        (a.out / legacy.name).write_text("\n".join(keep) + "\n", encoding="utf-8")

    for f in sorted(a.out.iterdir()):
        print(f"{f.stat().st_size:>12,}  {f.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
