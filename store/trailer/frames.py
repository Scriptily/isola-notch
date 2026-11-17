"""The README's dark recordings as PNG frame sequences with the backdrop keyed to
transparent (the island over a wallpaper in the trailer), and the stills too.
Writes frames/<clip>/NNNN.png and frames/index.json (each clip's frame times)."""
import json
from pathlib import Path
from PIL import Image

ASSETS = Path(r"C:\Users\Scriptily\Documents\dirs\isola-notch\assets\readme")
OUT = Path(__file__).resolve().parent / "frames"
BG = (13, 17, 23)


def key(im):
    im = im.convert("RGB")
    px = im.load()
    a = Image.new("L", im.size, 0)
    ap = a.load()
    for y in range(im.height):
        for x in range(im.width):
            r, g, b = px[x, y]
            d = abs(r - BG[0]) + abs(g - BG[1]) + abs(b - BG[2])
            ap[x, y] = 255 if d > 22 else int(255 * (d / 22) ** 1.5)
    out = im.convert("RGBA")
    out.putalpha(a)
    return out


index = {}
for clip in ("hello", "music", "popups", "downloads", "search"):
    im = Image.open(ASSETS / f"{clip}-dark.webp")
    d = OUT / clip
    d.mkdir(parents=True, exist_ok=True)
    times, t = [], 0.0
    for i in range(im.n_frames):
        im.seek(i)
        dur = im.info.get("duration", 33) / 1000
        key(im).save(d / f"{i:04d}.png")
        times.append(round(t, 4))
        t += dur
    index[clip] = {"frames": im.n_frames, "times": times, "length": round(t, 4), "w": im.width, "h": im.height}
    print(clip, im.n_frames, "frames", round(t, 2), "s", im.size)
for still in ("home", "weather", "notes", "timer", "media", "shelf", "commands"):
    im = Image.open(ASSETS / f"{still}-dark.png")
    key(im).save(OUT / f"{still}.png")
    index[still] = {"w": im.width, "h": im.height}
(OUT / "index.json").write_text(json.dumps(index))
print("ok")
