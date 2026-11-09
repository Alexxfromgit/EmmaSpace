#!/usr/bin/env python3
"""Generates the icons of .emmasnap snapshot files from branding/emmaspace-snapshot-icon.png.

The source art is a 1024 px PNG with transparency that already has the document shape (page, folded
corner, margins), so it is only scaled, never cropped or rounded:
  src/EmmaSpace.App/Assets/emmasnap.ico   Windows, 16-256 px (installed next to EmmaSpace.exe)
  src/EmmaSpace.App/Assets/emmasnap.icns  macOS, 16-1024 px (copied into the .app's Resources)
  src/EmmaSpace.App/Assets/emmasnap.png   Linux, 256 px (the app installs it into the icon theme)

To use other art, replace the PNG and run this again. Needs Pillow (pip install pillow). On macOS the
.icns is packed with iconutil; elsewhere Pillow writes it, with every size except the non-Retina 16
and 32 px entries, which macOS then scales down from the larger ones. The results are committed.

Usage: python3 build/make-file-icons.py [--source PATH]
"""
import argparse
import pathlib
import shutil
import subprocess
import tempfile

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "EmmaSpace.App" / "Assets"
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def scaled(art: Image.Image, px: int) -> Image.Image:
    return art if art.width == px else art.resize((px, px), Image.Resampling.LANCZOS)


def write_icns(art: Image.Image, out: pathlib.Path) -> None:
    if shutil.which("iconutil") is None:
        art.save(out, "ICNS", append_images=[scaled(art, px) for px in (32, 64, 128, 256, 512)])
        return
    with tempfile.TemporaryDirectory() as tmp:
        iconset = pathlib.Path(tmp) / "emmasnap.iconset"
        iconset.mkdir()
        for size in (16, 32, 128, 256, 512):
            for scale in (1, 2):
                name = f"icon_{size}x{size}{'@2x' if scale == 2 else ''}.png"
                scaled(art, size * scale).save(iconset / name)
        subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(out)], check=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=pathlib.Path, default=ROOT / "branding" / "emmaspace-snapshot-icon.png")
    args = parser.parse_args()

    art = scaled(Image.open(args.source).convert("RGBA"), 1024)
    sizes = [scaled(art, px) for px in ICO_SIZES]
    # PNG-compressed entries, as in emmaspace.ico (supported since Windows Vista).
    sizes[-1].save(ASSETS / "emmasnap.ico", "ICO", sizes=[(px, px) for px in ICO_SIZES], append_images=sizes[:-1])
    write_icns(art, ASSETS / "emmasnap.icns")
    sizes[-1].save(ASSETS / "emmasnap.png")
    print(f"Wrote emmasnap.ico, emmasnap.icns and emmasnap.png in {ASSETS}")


if __name__ == "__main__":
    main()
