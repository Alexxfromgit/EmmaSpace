#!/usr/bin/env python3
"""Generates the portraits shown in Emma Space's dialogs and Settings window, and the start page's
backdrop, from the art in branding/.

Each source is a wide painting with Emma in the middle. This crops a portrait around her at the
shape of the panel it fills and scales it for 2x displays:
  branding/emmaspace-dialog-art.png    ->  src/EmmaSpace.App/Assets/dialog-emma.jpg        (questions, 420 x 600)
  branding/emmaspace-error-art.png     ->  src/EmmaSpace.App/Assets/dialog-emma-error.jpg  (errors, 420 x 600)
  branding/emmaspace-settings-art.png  ->  src/EmmaSpace.App/Assets/settings-emma.jpg      (Settings, 680 x 760)
  branding/emmaspace-home-art.png      ->  src/EmmaSpace.App/Assets/home-emma.jpg          (start page, whole painting)
The dialogs fade each portrait's right edge into their background themselves, so keep her face left
of about 60 % of the crop (the Settings window fades only the last few percent, for her gear). The
start page shows the whole painting faintly behind the drives, so it is only re-encoded, not cropped.
Needs Pillow; the results are committed.

Usage: python3 build/make-dialog-art.py [--only question|error|settings|home] [--crop X,Y,W]
"""
import argparse
import pathlib

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "EmmaSpace.App" / "Assets"
DIALOG = (420, 600)  # the dialogs' 7:10 panel, shown at 210 x 300
SETTINGS = (680, 760)  # the Settings window's panel, shown at 340 x 380
HOME = (1672, 941)  # the start page fills the window with it, so it keeps the art's full 16:9 size

# name: (source art, output, crop as x,y,width fractions of the art's width, output size; the crop's
# height follows from the size). Both dialogs use the same crop width, so Emma appears at the same size.
PORTRAITS = {
    "question": ("emmaspace-dialog-art.png", "dialog-emma.jpg", (0.29, 0.052, 0.443), DIALOG),   # face and open hand
    "error": ("emmaspace-error-art.png", "dialog-emma-error.jpg", (0.31, 0.052, 0.443), DIALOG),  # face and warning sign
    "settings": ("emmaspace-settings-art.png", "settings-emma.jpg", (0.29, 0.0, 0.595), SETTINGS),  # face and gear
    "home": ("emmaspace-home-art.png", "home-emma.jpg", (0.0, 0.0, 1.0), HOME),  # all of it
}


def make(source: pathlib.Path, out: pathlib.Path, crop: tuple[float, float, float], size: tuple[int, int]) -> None:
    art = Image.open(source).convert("RGB")
    x, y, w = (v * art.width for v in crop)
    h = w * size[1] / size[0]
    y = max(0.0, min(y, art.height - h))  # keep the crop inside the art, so it is never squeezed
    portrait = art.crop((round(x), round(y), round(x + w), round(y + h)))
    portrait.resize(size, Image.Resampling.LANCZOS).save(out, "JPEG", quality=90, optimize=True, progressive=True)
    print(f"Wrote {out}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--only", choices=sorted(PORTRAITS), help="make just this portrait")
    parser.add_argument("--crop", help="x,y,width for --only, overriding its default")
    args = parser.parse_args()

    for name, (source, out, crop, size) in PORTRAITS.items():
        if args.only and name != args.only:
            continue
        if args.crop:
            crop = tuple(float(v) for v in args.crop.split(","))
        make(ROOT / "branding" / source, ASSETS / out, crop, size)


if __name__ == "__main__":
    main()
