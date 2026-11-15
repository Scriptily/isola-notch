"""Draws the logos in assets/: the icon with the name (with and without the
tagline) and the name without the icon, each for dark and light backgrounds,
at twice their 1x size. Uses Segoe UI Variable, which ships with Windows 11.

  python logos.py
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "assets"
FONT = r"C:\Windows\Fonts\SegUIVar.ttf"
NAME, TAGLINE = "Isola", "Dynamic island for Windows"


def font(size, weight, opsz):
    f = ImageFont.truetype(FONT, size)
    try:
        f.set_variation_by_axes([weight, opsz])   # wght, opsz
    except Exception:
        pass
    return f


def colors(dark):
    fg = (245, 245, 247, 255) if dark else (22, 22, 26, 255)
    sub = (160, 160, 170, 255) if dark else (96, 96, 104, 255)
    return fg, sub


def bbox(text, f):
    return ImageDraw.Draw(Image.new("RGBA", (1, 1))).textbbox((0, 0), text, font=f)


def with_icon(tagline, dark, S=4):
    icon = Image.open(ASSETS / "island.png").convert("RGBA")
    ic, gap = 112 * S, 28 * S
    name_f, tag_f = font(96 * S, 650, 36), font(30 * S, 400, 36)
    fg, sub = colors(dark)
    nb = bbox(NAME, name_f)
    tb = bbox(TAGLINE, tag_f) if tagline else (0, 0, 0, 0)
    text_w = max(nb[2] - nb[0], tb[2] - tb[0])
    text_h = (nb[3] - nb[1]) + ((tb[3] - tb[1]) + 14 * S if tagline else 0)
    W, H = ic + gap + text_w + 8 * S, max(ic, text_h) + 8 * S
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    im.alpha_composite(icon.resize((ic, ic), Image.LANCZOS), (0, (H - ic) // 2))
    d = ImageDraw.Draw(im)
    x, y = ic + gap, (H - text_h) // 2
    d.text((x - nb[0], y - nb[1]), NAME, font=name_f, fill=fg)
    if tagline:
        d.text((x - tb[0] + 3 * S, y + (nb[3] - nb[1]) + 14 * S - tb[1]), TAGLINE, font=tag_f, fill=sub)
    return im.resize((W // 2, H // 2), Image.LANCZOS)


def wordmark(dark, S=4):
    name_f, tag_f = font(96 * S, 650, 36), font(30 * S, 400, 14)
    fg, sub = colors(dark)
    nb, tb = bbox(NAME, name_f), bbox(TAGLINE, tag_f)
    gap = 18 * S
    W = max(nb[2] - nb[0], tb[2] - tb[0]) + 16 * S
    H = (nb[3] - nb[1]) + gap + (tb[3] - tb[1]) + 16 * S
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    y = 8 * S
    d.text(((W - (nb[2] - nb[0])) // 2 - nb[0], y - nb[1]), NAME, font=name_f, fill=fg)
    y += (nb[3] - nb[1]) + gap
    d.text(((W - (tb[2] - tb[0])) // 2 - tb[0], y - tb[1]), TAGLINE, font=tag_f, fill=sub)
    return im.resize((W // 2, H // 2), Image.LANCZOS)


if __name__ == "__main__":
    for dark in (True, False):
        theme = "dark" if dark else "light"
        with_icon(False, dark).save(ASSETS / f"logo-{theme}.png", optimize=True)
        with_icon(True, dark).save(ASSETS / f"logo-tagline-{theme}.png", optimize=True)
        wordmark(dark).save(ASSETS / f"wordmark-{theme}.png", optimize=True)
    print("logos written to", ASSETS)
