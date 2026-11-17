"""Store listing screenshots (1920x1080): the README images of the island, at the
top of a plain dark screen, with a short caption. Made from the sample data the
README recordings use, so nothing personal appears.

  python store/make_screenshots.py
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[1]
# PNG stills recorded at full resolution (tools/readme-media/record.py --still --scale 1),
# never GIF frames: a GIF's 256 colors turn the glow's gradients into stripes
SRC = ROOT / "store" / "screenshots" / "src"
OUT = ROOT / "store" / "screenshots"
W, H = 1920, 1080
FONT = r"C:\Windows\Fonts\SegUIVar.ttf"


def font(size, weight):
    f = ImageFont.truetype(FONT, size)
    try:
        f.set_variation_by_axes([weight, 36])
    except Exception:
        pass
    return f


def backdrop():
    """GitHub's dark page color with two soft color glows, like a calm wallpaper."""
    bg = Image.new("RGB", (W, H), (13, 17, 23))
    glow = Image.new("RGB", (W, H), (0, 0, 0))
    d = ImageDraw.Draw(glow)
    d.ellipse((-300, 500, 900, 1400), fill=(18, 92, 96))
    d.ellipse((1100, 450, 2300, 1350), fill=(70, 46, 140))
    glow = glow.filter(ImageFilter.GaussianBlur(220))
    return Image.blend(bg, glow, 0.35)


def shot(island, title, sub, name):
    img = backdrop()
    # as large as fits above the caption, never enlarged past 1.4x (it'd go soft)
    scale = min(1500 / island.width, 640 / island.height, 1.4)
    isl = island.resize((round(island.width * scale), round(island.height * scale)), Image.LANCZOS)
    # the island images have GitHub's dark color around them: blend it away into the backdrop
    mask = Image.new("L", isl.size, 0)
    px, mk = isl.load(), mask.load()
    for y in range(isl.height):
        for x in range(isl.width):
            r, g, b = px[x, y]
            dist = abs(r - 13) + abs(g - 17) + abs(b - 23)
            mk[x, y] = 255 if dist > 24 else int(255 * dist / 24)
    img.paste(isl, ((W - isl.width) // 2, 0), mask)
    d = ImageDraw.Draw(img)
    tf, sf = font(52, 650), font(26, 400)
    tb = d.textbbox((0, 0), title, font=tf)
    d.text(((W - (tb[2] - tb[0])) // 2, H - 220), title, font=tf, fill=(245, 245, 247))
    sb = d.textbbox((0, 0), sub, font=sf)
    d.text(((W - (sb[2] - sb[0])) // 2, H - 140), sub, font=sf, fill=(160, 160, 172))
    OUT.mkdir(parents=True, exist_ok=True)
    img.save(OUT / name, optimize=True)
    print(name)


for old in OUT.glob("*.png"):
    old.unlink()   # the set changes; none of the last one stays behind
shot(Image.open(SRC / "music.png").convert("RGB"), "Your music, alive at the top", "The cover, a waveform and a glow in its colors, whatever app is playing", "1-music.png")
shot(Image.open(SRC / "popup.png").convert("RGB"), "Notifications that don't get in the way", "Messages, calls from your phone, music and devices, in one small pill", "2-popups.png")
shot(Image.open(SRC / "downloads.png").convert("RGB"), "Paste a link, get the video", "Download from YouTube and most sites, or convert a file to MP3, MP4 or GIF", "3-downloads.png")
shot(Image.open(SRC / "search.png").convert("RGB"), "A launcher on Alt+Space", "Math, units, commands, apps and files as you type", "4-search.png")
shot(Image.open(SRC / "home.png").convert("RGB"), "Your home, at the top of the screen", "Lights, scenes, climate and locks from Home Assistant, a click away", "5-home.png")
shot(Image.open(SRC / "notes.png").convert("RGB"), "Notes and to-dos, always at hand", "Weather, a file shelf, your clipboard and a timer are a scroll away", "6-notes.png")
shot(Image.open(SRC / "hello.png").convert("RGB"), "Isola", "A small, fast island for Windows: about 30 MB of memory, no telemetry", "7-hello.png")
