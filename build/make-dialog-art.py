#!/usr/bin/env python3
"""Generates the portrait shown in Emma Space's confirmation dialogs from branding/emmaspace-dialog-art.png.

The source is a wide painting with Emma in the middle. This crops a portrait around her (face and
open hand) at the dialog panel's 7:10 shape and scales it for 2x displays:
  src/EmmaSpace.App/Assets/dialog-emma.jpg   420 x 600 px (shown at 210 x 300)
The dialog fades the panel's right edge into its background itself. Needs Pillow; the result is committed.

Usage: python3 build/make-dialog-art.py [--source PATH] [--crop X,Y,W]
"""
import argparse
import pathlib

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "EmmaSpace.App" / "Assets" / "dialog-emma.jpg"
WIDTH, HEIGHT = 420, 600


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=pathlib.Path, default=ROOT / "branding" / "emmaspace-dialog-art.png")
    parser.add_argument("--crop", default="0.29,0.052,0.443",
                        help="x,y,width as fractions of the art's width; the height follows from 7:10")
    args = parser.parse_args()

    art = Image.open(args.source).convert("RGB")
    x, y, w = (float(v) * art.width for v in args.crop.split(","))
    h = w * HEIGHT / WIDTH
    portrait = art.crop((round(x), round(y), round(x + w), round(min(y + h, art.height))))
    portrait.resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS).save(OUT, "JPEG", quality=90, optimize=True, progressive=True)
    print(f"Wrote {OUT}")


if __name__ == "__main__":
    main()
