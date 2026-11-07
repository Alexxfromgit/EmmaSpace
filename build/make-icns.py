#!/usr/bin/env python3
"""Generates src/EmmaSpace.App/Assets/emmaspace.icns from branding/emmaspace-icon.png.

The art sits in an 824 px rounded square on a 1024 px canvas, the grid that macOS app icons use, so
Emma Space lines up with the other icons in the Dock and Finder. macOS only (needs iconutil) and
Pillow (pip install pillow). The result is committed, so CI does not run this.

Usage: python3 build/make-icns.py [--source PATH]
"""
import argparse
import pathlib
import subprocess
import tempfile

from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parent.parent
CANVAS = 1024
TILE = 824          # Apple's icon grid: 100 px margin on every side
RADIUS = 185        # corner radius of the tile at 1024 px


def master(source: pathlib.Path) -> Image.Image:
    art = Image.open(source).convert("RGBA").resize((TILE, TILE), Image.Resampling.LANCZOS)
    # Draw the mask at 4x and scale it down for smooth, anti-aliased corners.
    big = Image.new("L", (TILE * 4, TILE * 4), 0)
    ImageDraw.Draw(big).rounded_rectangle((0, 0, TILE * 4 - 1, TILE * 4 - 1), RADIUS * 4, fill=255)
    art.putalpha(big.resize((TILE, TILE), Image.Resampling.LANCZOS))
    canvas = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    offset = (CANVAS - TILE) // 2
    canvas.alpha_composite(art, (offset, offset))
    return canvas


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=pathlib.Path, default=ROOT / "branding" / "emmaspace-icon.png")
    args = parser.parse_args()

    image = master(args.source)
    out = ROOT / "src" / "EmmaSpace.App" / "Assets" / "emmaspace.icns"
    with tempfile.TemporaryDirectory() as tmp:
        iconset = pathlib.Path(tmp) / "emmaspace.iconset"
        iconset.mkdir()
        for size in (16, 32, 128, 256, 512):
            for scale in (1, 2):
                px = size * scale
                name = f"icon_{size}x{size}{'@2x' if scale == 2 else ''}.png"
                image.resize((px, px), Image.Resampling.LANCZOS).save(iconset / name)
        subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(out)], check=True)
    print(f"Wrote {out}")


if __name__ == "__main__":
    main()
