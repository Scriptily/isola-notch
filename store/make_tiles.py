"""Draws the Microsoft Store package's tile and logo images from assets/island.png
into store/assets/, at the sizes and scales the Store and Windows look for.

  python store/make_tiles.py
"""
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "store" / "assets"
icon = Image.open(ROOT / "assets" / "island.png").convert("RGBA")


def square(size, pad=0.0):
    """The icon centered on a transparent square, with optional padding around it."""
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    inner = max(1, round(size * (1 - 2 * pad)))
    im.alpha_composite(icon.resize((inner, inner), Image.LANCZOS), ((size - inner) // 2, (size - inner) // 2))
    return im


def wide(w, h):
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    inner = round(h * 0.6)
    im.alpha_composite(icon.resize((inner, inner), Image.LANCZOS), ((w - inner) // 2, (h - inner) // 2))
    return im


OUT.mkdir(parents=True, exist_ok=True)
for scale in (100, 200):
    k = scale / 100
    square(round(44 * k)).save(OUT / f"Square44x44Logo.scale-{scale}.png")
    square(round(150 * k), 0.18).save(OUT / f"Square150x150Logo.scale-{scale}.png")
    wide(round(310 * k), round(150 * k)).save(OUT / f"Wide310x150Logo.scale-{scale}.png")
    square(round(50 * k)).save(OUT / f"StoreLogo.scale-{scale}.png")
# the plain names, for tools that don't read the resource index (MakeAppx validates these)
square(44).save(OUT / "Square44x44Logo.png")
square(150, 0.18).save(OUT / "Square150x150Logo.png")
wide(310, 150).save(OUT / "Wide310x150Logo.png")
square(50).save(OUT / "StoreLogo.png")
# the taskbar and Start list use these exact sizes, without a plate behind them
for t in (16, 24, 32, 48, 256):
    square(t).save(OUT / f"Square44x44Logo.targetsize-{t}.png")
    square(t).save(OUT / f"Square44x44Logo.targetsize-{t}_altform-unplated.png")
print("tiles written to", OUT)
