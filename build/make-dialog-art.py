#!/usr/bin/env python3
"""Generates the portraits shown in Emma Space's dialogs from the art in branding/.

Each source is a wide painting with Emma in the middle. This crops a portrait around her at the
dialog panel's 7:10 shape and scales it for 2x displays (420 x 600 px, shown at 210 x 300):
  branding/emmaspace-dialog-art.png  ->  src/EmmaSpace.App/Assets/dialog-emma.jpg        (questions)
  branding/emmaspace-error-art.png   ->  src/EmmaSpace.App/Assets/dialog-emma-error.jpg  (errors)
The dialog fades each portrait's right edge into its background itself, so keep her face left of
about 60 % of the crop. Needs Pillow; the results are committed.

Usage: python3 build/make-dialog-art.py [--only question|error] [--crop X,Y,W]
"""
import argparse
import pathlib

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "EmmaSpace.App" / "Assets"
WIDTH, HEIGHT = 420, 600

# name: (source art, output, crop as x,y,width fractions of the art's width; the height follows from 7:10)
# Both use the same crop width, so Emma appears at the same size in either dialog.
PORTRAITS = {
    "question": ("emmaspace-dialog-art.png", "dialog-emma.jpg", (0.29, 0.052, 0.443)),   # face and open hand
    "error": ("emmaspace-error-art.png", "dialog-emma-error.jpg", (0.31, 0.052, 0.443)),  # face and warning sign
}


def make(source: pathlib.Path, out: pathlib.Path, crop: tuple[float, float, float]) -> None:
    art = Image.open(source).convert("RGB")
    x, y, w = (v * art.width for v in crop)
    h = w * HEIGHT / WIDTH
    y = max(0.0, min(y, art.height - h))  # keep the crop inside the art, so it is never squeezed
    portrait = art.crop((round(x), round(y), round(x + w), round(y + h)))
    portrait.resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS).save(out, "JPEG", quality=90, optimize=True, progressive=True)
    print(f"Wrote {out}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--only", choices=sorted(PORTRAITS), help="make just this portrait")
    parser.add_argument("--crop", help="x,y,width for --only, overriding its default")
    args = parser.parse_args()

    for name, (source, out, crop) in PORTRAITS.items():
        if args.only and name != args.only:
            continue
        if args.crop:
            crop = tuple(float(v) for v in args.crop.split(","))
        make(ROOT / "branding" / source, ASSETS / out, crop)


if __name__ == "__main__":
    main()
