"""Re-records every image and GIF in the README into assets/readme/, in a
dark version (GitHub's dark page color, #0d1117) and a white one, notch style.

Build the Debug island first (dotnet build Isola.csproj), then:

  python record_all.py            # everything
  python record_all.py home open  # only these
"""
import subprocess, sys, time
from pathlib import Path

import sample_data

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
OUT = ROOT / "assets" / "readme"
PY = sys.executable

# name, showcase state, seconds, start delay, fps, still, extra
JOBS = [
    ("hello", "hello", 5, 0, 30, False, ["--padx", "260", "--scale", "1"]),
    ("open", "expand:home", 4.5, 1.2, 30, False, []),
    ("search", "search:15% of 80", 5, 1.6, 25, False, []),
    ("popups", "popups:11,13,12,0,14", 21, 1.5, 20, False, []),
    ("home", "expand:home", 6, 0, 5, True, []),
    ("weather", "expand:weather", 6, 0, 5, True, []),
    ("notes", "expand:notes", 6, 0, 5, True, []),
    ("timer", "expand:timer", 6, 0, 5, True, []),
    ("commands", "search:help", 6, 0, 5, True, []),
]


def main(only):
    sample_data.WORK.mkdir(parents=True, exist_ok=True)
    stub = subprocess.Popen([PY, str(HERE / "stub_ha.py"), str(sample_data.WORK / "states.json"), str(sample_data.PORT),
                             str(sample_data.WORK / "calls.log")])
    try:
        time.sleep(1)
        for theme, bg in (("dark", "0d1117"), ("light", "ffffff")):
            for name, state, secs, delay, fps, still, extra in JOBS:
                if only and name not in only:
                    continue
                sample_data.write("notch", hello=(name == "hello"))
                out = OUT / f"{name}-{theme}.{'png' if still else 'gif'}"
                cmd = [PY, str(HERE / "record.py"), state, str(secs), str(out), "--delay", str(delay), "--fps", str(fps), "--bg", bg, *extra]
                if still:
                    cmd.append("--still")
                r = subprocess.run(cmd, capture_output=True, text=True)
                print(name, theme, (r.stdout.strip().splitlines() or [""])[-1], r.stderr.strip()[-300:])
    finally:
        stub.terminate()


if __name__ == "__main__":
    main(set(sys.argv[1:]))
