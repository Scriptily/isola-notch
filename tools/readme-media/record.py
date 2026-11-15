"""Records one README image or GIF of the development build, over a solid
backdrop, without sending any keyboard or mouse input.

The installed island is closed for the take (only one runs at a time) and
started again afterwards, whatever happens. Frames are kept only once the
island is drawing its backdrop: before that, its window shows whatever is on
the desktop behind it, and that must never end up in a public image.

  python record.py <state> <seconds> <out.gif|out.png> [--delay s] [--fps n]
                   [--bg rrggbb] [--padx px] [--scale f] [--still]

<state> is "rest", "hello" (the start-up animation; set HelloOnLaunch in the
sample data), or anything ISOLA_SHOWCASE accepts: "expand:home",
"search:12*4.5", "popups", "popups:11,13,12".
"""
import argparse, ctypes, ctypes.wintypes as wt, os, subprocess, sys, time
from pathlib import Path
from PIL import Image, ImageChops

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
WORK = HERE / ".work"
EXE = ROOT / "bin" / "Debug" / "net10.0-windows10.0.26100.0" / "win-x64" / "Isola.exe"
INSTALLED = Path(os.environ["LOCALAPPDATA"]) / "Programs" / "Isola" / "Isola.exe"
WM_QUIT_ISLAND = 0x8009   # App.WM_QUIT_ISLAND (WM_APP + 9)

ctypes.windll.shcore.SetProcessDpiAwareness(2)   # physical pixels
u, g = ctypes.windll.user32, ctypes.windll.gdi32


class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG), ("biPlanes", wt.WORD), ("biBitCount", wt.WORD),
                ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
                ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


class Grabber:
    """BitBlt of just the region (layered windows included): far cheaper than
    grabbing the whole desktop, which held a take to 8 fps."""
    def __init__(self, l, t, w, h):
        self.l, self.t, self.w, self.h = l, t, w, h
        self.sdc = u.GetDC(0)
        self.mdc = g.CreateCompatibleDC(self.sdc)
        self.bmp = g.CreateCompatibleBitmap(self.sdc, w, h)
        g.SelectObject(self.mdc, self.bmp)
        self.bih = BIH(ctypes.sizeof(BIH), w, -h, 1, 32, 0, 0, 0, 0, 0, 0)
        self.buf = ctypes.create_string_buffer(w * h * 4)

    def grab(self):
        g.BitBlt(self.mdc, 0, 0, self.w, self.h, self.sdc, self.l, self.t, 0x00CC0020 | 0x40000000)   # SRCCOPY | CAPTUREBLT
        g.GetDIBits(self.mdc, self.bmp, 0, self.h, self.buf, ctypes.byref(self.bih), 0)
        return Image.frombuffer("RGB", (self.w, self.h), self.buf.raw, "raw", "BGRX", 0, 1)


def isola_running():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Isola.exe", "/NH"], capture_output=True, text=True).stdout
    return "Isola.exe" in out


def quit_island():
    h = u.FindWindowW("Isola.Input", None)
    if h:
        u.PostMessageW(h, WM_QUIT_ISLAND, 0, 0)
    t0 = time.time()
    while isola_running() and time.time() - t0 < 10:
        time.sleep(0.2)


def rect(h):
    r = wt.RECT()
    u.GetWindowRect(h, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def take(state, seconds, delay, fps, bg):
    quit_island()
    time.sleep(0.8)
    env = dict(os.environ, ISOLA_HOME=str(WORK / "home"), ISOLA_BACKDROP=bg, ISOLA_NO_EVERYTHING="1")
    if state not in ("rest", "hello"):
        env["ISOLA_SHOWCASE"] = state
    subprocess.Popen([str(EXE)], env=env, cwd=str(WORK))
    t0, h = time.time(), 0
    while not h and time.time() - t0 < 15:
        time.sleep(0.05)
        h = u.FindWindowW("Isola.Visual", None)
    if not h:
        sys.exit("the island's window never appeared (is the Debug build there?)")
    # The window is created small and placed a moment later; the hello
    # animation starts at once, so capture begins the moment it's placed.
    t1 = time.time()
    while time.time() - t1 < 5:
        l, t, r, b = rect(h)
        if r - l > 300:
            break
        time.sleep(0.01)
    if state != "hello":
        time.sleep(0.6)
    dpi = u.GetDpiForWindow(h) or 96
    b = min(b, t + 600 * dpi // 96)
    time.sleep(delay)
    grabber, frames = Grabber(l, t, r - l, b - t), []
    start = nxt = time.time()
    while time.time() - start < seconds:
        frames.append(grabber.grab())
        nxt += 1 / fps
        time.sleep(max(0, nxt - time.time()))
    return frames, len(frames) / (time.time() - start), dpi


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("state"); ap.add_argument("seconds", type=float); ap.add_argument("out")
    ap.add_argument("--delay", type=float, default=0); ap.add_argument("--fps", type=float, default=25)
    ap.add_argument("--bg", default="0d1117"); ap.add_argument("--padx", type=int, default=24)
    ap.add_argument("--scale", type=float, default=0, help="0: back to 1x (96 DPI) from the screen's scaling")
    ap.add_argument("--still", action="store_true", help="save the last frame as PNG")
    a = ap.parse_args()
    BG = tuple(int(a.bg[i:i + 2], 16) for i in (0, 2, 4))
    try:
        frames, real_fps, dpi = take(a.state, a.seconds, a.delay, a.fps, a.bg)
    finally:
        quit_island()
        subprocess.Popen(["explorer.exe", str(INSTALLED)])   # the installed island comes back, whatever happened

    def clean(f):
        # the window's edges (not the island's spot at the top middle, nor the center) must be the backdrop
        w, h = f.size
        px = [f.getpixel((x, y)) for x in (0, w // 2, w - 1) for y in (0, h // 2, h - 1) if not (x == w // 2 and y in (0, h // 2))]
        return all(sum(abs(p[i] - BG[i]) for i in range(3)) <= 6 for p in px)

    first = next((i for i, f in enumerate(frames) if clean(f)), None)
    if first is None:
        sys.exit("no clean frame: the backdrop never showed")
    frames = frames[first:]
    if not all(clean(f) for f in frames):
        sys.exit("a frame later in the take isn't clean; not saving it")

    box = None
    for f in frames[::3]:
        bb = ImageChops.difference(f, Image.new("RGB", f.size, BG)).convert("L").point(lambda v: 255 if v > 6 else 0).getbbox()
        if bb:
            box = bb if box is None else (min(box[0], bb[0]), min(box[1], bb[1]), max(box[2], bb[2]), max(box[3], bb[3]))
    W, H = frames[0].size
    box = (max(0, box[0] - a.padx), 0, min(W, box[2] + a.padx), min(H, box[3] + 24))
    scale = a.scale or 96 / dpi
    frames = [f.crop(box) for f in frames]
    frames = [f.resize((round(f.width * scale), round(f.height * scale)), Image.LANCZOS) for f in frames]

    if a.still:
        frames[-1].save(a.out, optimize=True)
        print(f"still {frames[-1].size} -> {a.out}")
        return
    dur, kept, durs = 1000 / real_fps, [], []
    for f in frames:   # a run of identical frames becomes one longer frame
        if kept and ImageChops.difference(f, kept[-1]).getbbox() is None:
            durs[-1] += dur
            continue
        kept.append(f); durs.append(dur)
    pal = [f.quantize(colors=255, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE) for f in kept]
    pal[0].save(a.out, save_all=True, append_images=pal[1:], duration=[int(d) for d in durs], loop=0, optimize=True, disposal=1)
    print(f"{len(frames)} frames at {real_fps:.1f} fps -> {len(kept)} kept, {os.path.getsize(a.out) // 1024} KB, {kept[0].size} -> {a.out}")


if __name__ == "__main__":
    main()
