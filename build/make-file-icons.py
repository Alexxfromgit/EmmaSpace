#!/usr/bin/env python3
"""Generates the icons of .emmasnap snapshot files from branding/emmaspace-snapshot-art.png.

The source is square art (any size, opaque is fine). It is framed as a document: cropped to a 3:4
page with rounded corners and a folded top-right corner, a light rim and a soft shadow, so it reads
as a file rather than as a second app icon. Sizes up to 32 px use a tighter crop (--small-crop), so
the tiny icons still show something recognizable.
  src/EmmaSpace.App/Assets/emmasnap.ico   Windows, 16-256 px (installed next to EmmaSpace.exe)
  src/EmmaSpace.App/Assets/emmasnap.icns  macOS, 16-1024 px (copied into the .app's Resources)
  src/EmmaSpace.App/Assets/emmasnap.png   Linux, 256 px (the app installs it into the icon theme)

To use other art, replace the PNG (and adjust the crops) and run this again. Needs Pillow
(pip install pillow). On macOS the .icns is packed with iconutil; elsewhere Pillow writes it, with
every size except the non-Retina 16 and 32 px entries, which macOS then scales down from the larger
ones. The results are committed.

Usage: python3 build/make-file-icons.py [--source PATH] [--crop X,Y,W] [--small-crop X,Y,W]
"""
import argparse
import pathlib
import shutil
import subprocess
import tempfile

from PIL import Image, ImageDraw, ImageFilter

ROOT = pathlib.Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "EmmaSpace.App" / "Assets"
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
SMALL = 32                     # this size and below use the small crop

# The page on a 1024 px canvas: 3:4, centred, with room for the shadow.
CANVAS = 1024
LEFT, TOP, RIGHT, BOTTOM = 176, 64, 848, 960
RADIUS, FOLD = 56, 190
SUPERSAMPLE = 4


def crop(art: Image.Image, spec: tuple[float, float, float]) -> Image.Image:
    """X, Y and width as fractions of the art's width; the height follows from the page's 3:4."""
    x, y, w = (v * art.width for v in spec)
    h = w * (BOTTOM - TOP) / (RIGHT - LEFT)
    return art.crop((round(x), round(y), round(x + w), round(y + h)))


def framed(picture: Image.Image) -> Image.Image:
    s = SUPERSAMPLE
    n, l, t, r, b, rad, fold = (v * s for v in (CANVAS, LEFT, TOP, RIGHT, BOTTOM, RADIUS, FOLD))

    page = Image.new("L", (n, n), 0)
    draw = ImageDraw.Draw(page)
    draw.rounded_rectangle((l, t, r, b), rad, fill=255)
    draw.polygon([(r - fold, t - 1), (r + 1, t - 1), (r + 1, t + fold)], fill=0)  # cut the folded corner

    canvas = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    # Soft shadow below the page.
    shadow = Image.new("RGBA", (n, n), (8, 4, 40, 0))
    shadow.putalpha(page.point(lambda v: v * 110 // 255))
    shadow = shadow.transform((n, n), Image.Transform.AFFINE, (1, 0, 0, 0, 1, -14 * s)).filter(ImageFilter.GaussianBlur(18 * s))
    canvas.alpha_composite(shadow)

    # The art fills the page.
    fill = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    fill.paste(picture.convert("RGBA").resize((r - l, b - t), Image.Resampling.LANCZOS), (l, t))
    fill.putalpha(page)
    canvas.alpha_composite(fill)

    # A light rim, so the dark page stands out on dark backgrounds too.
    rim = Image.new("L", (n, n), 0)
    ImageDraw.Draw(rim).rounded_rectangle((l, t, r, b), rad, outline=255, width=7 * s)
    rim = Image.composite(rim, Image.new("L", (n, n), 0), page)
    rim_layer = Image.new("RGBA", (n, n), (214, 204, 255, 0))
    rim_layer.putalpha(rim.point(lambda v: v * 150 // 255))
    canvas.alpha_composite(rim_layer)

    # The folded corner: a light flap that casts a small shadow onto the page.
    flap = [(r - fold, t), (r - fold, t + fold - 30 * s), (r - fold + 30 * s, t + fold), (r, t + fold)]
    flap_shadow = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    ImageDraw.Draw(flap_shadow).polygon([(x - 6 * s, y + 10 * s) for x, y in flap], fill=(0, 0, 30, 140))
    flap_shadow = flap_shadow.filter(ImageFilter.GaussianBlur(8 * s))
    flap_shadow.putalpha(Image.composite(flap_shadow.getchannel("A"), Image.new("L", (n, n), 0), page))
    canvas.alpha_composite(flap_shadow)
    gradient = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    gd = ImageDraw.Draw(gradient)
    for i in range(fold):
        c = tuple(round(a + (z - a) * i / fold) for a, z in zip((246, 243, 255), (184, 168, 255)))
        gd.line([(r - fold + i, t), (r - fold + i, t + fold)], fill=c + (255,))
    flap_mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(flap_mask).polygon(flap, fill=255)
    gradient.putalpha(flap_mask)
    canvas.alpha_composite(gradient)
    ImageDraw.Draw(canvas).line([(r - fold, t), (r, t + fold)], fill=(255, 255, 255, 200), width=4 * s)

    return canvas.resize((CANVAS, CANVAS), Image.Resampling.LANCZOS)


def scaled(icon: Image.Image, px: int) -> Image.Image:
    return icon if icon.width == px else icon.resize((px, px), Image.Resampling.LANCZOS)


def write_icns(large: Image.Image, small: Image.Image, out: pathlib.Path) -> None:
    def pick(px: int) -> Image.Image:
        return scaled(small if px <= SMALL else large, px)

    if shutil.which("iconutil") is None:
        large.save(out, "ICNS", append_images=[pick(px) for px in (32, 64, 128, 256, 512)])
        return
    with tempfile.TemporaryDirectory() as tmp:
        iconset = pathlib.Path(tmp) / "emmasnap.iconset"
        iconset.mkdir()
        for size in (16, 32, 128, 256, 512):
            for scale in (1, 2):
                pick(size * scale).save(iconset / f"icon_{size}x{size}{'@2x' if scale == 2 else ''}.png")
        subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(out)], check=True)


def fractions(text: str) -> tuple[float, float, float]:
    x, y, w = (float(v) for v in text.split(","))
    return x, y, w


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=pathlib.Path, default=ROOT / "branding" / "emmaspace-snapshot-art.png")
    parser.add_argument("--crop", type=fractions, default=(0.125, 0.0, 0.75),
                        help="page crop: x,y,width as fractions of the art's width (default: the full height)")
    parser.add_argument("--small-crop", type=fractions, default=(0.311, 0.4705, 0.3828),
                        help="crop for 32 px and below (default: the database and camera)")
    args = parser.parse_args()

    art = Image.open(args.source).convert("RGB")
    large, small = framed(crop(art, args.crop)), framed(crop(art, args.small_crop))
    sizes = [scaled(small if px <= SMALL else large, px) for px in ICO_SIZES]
    # PNG-compressed entries, as in emmaspace.ico (supported since Windows Vista).
    sizes[-1].save(ASSETS / "emmasnap.ico", "ICO", sizes=[(px, px) for px in ICO_SIZES], append_images=sizes[:-1])
    write_icns(large, small, ASSETS / "emmasnap.icns")
    sizes[-1].save(ASSETS / "emmasnap.png")
    print(f"Wrote emmasnap.ico, emmasnap.icns and emmasnap.png in {ASSETS}")


if __name__ == "__main__":
    main()
