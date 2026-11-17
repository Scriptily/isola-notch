"""Records the stills the Store screenshots are made from (store/screenshots/src),
at full resolution, with the sample data the README uses. Then run
make_screenshots.py. The installed island is closed for each take and comes back.

  python store/record_sources.py [name ...]
"""
import os, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MEDIA = ROOT / "tools" / "readme-media"
sys.path.insert(0, str(MEDIA))
import sample_data  # noqa: E402

SRC = ROOT / "store" / "screenshots" / "src"
VIDEO = "https://www.youtube.com/watch?v=jNQXAC9IVRw"
# name, showcase state, seconds, needs
SHOTS = [
    ("music", "expand:media", 6, "media"),
    ("popup", "popups:11", 7, ""),
    ("downloads", f"download:{VIDEO};audio:{VIDEO}@4.5;downloads@13", 17.5, "tools"),
    ("search", "search:15% of 80", 6, ""),
    ("home", "expand:home", 6, ""),
    ("notes", "expand:notes", 6, ""),
    ("hello", "hello", 4.2, ""),
]


def main(only):
    SRC.mkdir(parents=True, exist_ok=True)
    stub = subprocess.Popen([sys.executable, str(MEDIA / "stub_ha.py"), str(sample_data.WORK / "states.json"), str(sample_data.PORT), str(sample_data.WORK / "calls.log")])
    try:
        for name, state, secs, needs in SHOTS:
            if only and name not in only:
                continue
            sample_data.write("notch", hello=(name == "hello"), media=(needs == "media"))
            env = dict(os.environ)
            if needs == "media":
                env["ISOLA_SHOWCASE_MEDIA"] = str(sample_data.WORK / "media" / "tracks.json")
            if needs == "tools":
                env["ISOLA_TOOLS"] = str(sample_data.WORK / "tools")
            extra = ["--padx", "260"] if name == "hello" else []
            r = subprocess.run([sys.executable, str(MEDIA / "record.py"), state, str(secs), str(SRC / f"{name}.png"), "--still", "--scale", "1", "--bg", "0d1117", *extra],
                               capture_output=True, text=True, env=env)
            print(name, (r.stdout.strip().splitlines() or [""])[-1], r.stderr.strip()[-200:])
    finally:
        stub.terminate()


if __name__ == "__main__":
    main(set(sys.argv[1:]))
