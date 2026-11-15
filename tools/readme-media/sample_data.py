"""Writes the sample data folder the README recordings run on: a config with
nothing personal (sounds, notifications, media and Windows' banner changes off,
so nothing real or audible shows up), made-up Home Assistant devices, and notes.

  python sample_data.py [notch|pill] [hello]
"""
import json, sys
from datetime import datetime, timedelta
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
WORK = HERE / ".work"
HOME = WORK / "home"
PORT = 8125


def write(style="notch", hello=False):
    HOME.mkdir(parents=True, exist_ok=True)
    for f in HOME.iterdir():
        if f.suffix == ".log" or f.name.startswith(("timer", "isola.log")):
            f.unlink()
    cfg = json.loads((ROOT / "isola.config.example.json").read_text(encoding="utf-8"))
    cfg.update({
        "Style": style, "HelloOnLaunch": hello, "AutoHideMinutes": 0,
        "Media": False, "ShowOnTrackChange": False,
        "BluetoothAlerts": False, "AirPods": False, "DisplayAlerts": False, "BatteryAlerts": False,
        "VolumeHud": False, "CapsLockHud": False, "KeyboardLayoutHud": False,
        "Notifications": False, "Calls": False, "HideWindowsBanners": False, "Sounds": False,
        "AutoUpdate": False, "Clipboard": False, "Shelf": True,
        "WeatherSource": "homeassistant", "WeatherEntity": "weather.home",
        "HaBaseUrl": f"http://127.0.0.1:{PORT}", "HaToken": "sample",
        "HaEntities": ["light.living_room", "light.desk_lamp", "light.kitchen", "scene.movie_night",
                       "climate.thermostat", "fan.bedroom_fan", "lock.front_door", "switch.coffee_maker"],
    })
    (HOME / "isola.config.json").write_text(json.dumps(cfg, indent=2), encoding="utf-8")

    now = datetime.now().astimezone()

    def st(eid, state, **attrs):
        t = (now - timedelta(minutes=12)).isoformat()
        return {"entity_id": eid, "state": state, "attributes": attrs, "last_changed": t, "last_updated": t}

    states = [
        st("light.living_room", "on", friendly_name="Living Room", brightness=204, color_mode="brightness", supported_color_modes=["brightness"]),
        st("light.desk_lamp", "on", friendly_name="Desk Lamp", brightness=140, color_mode="brightness", supported_color_modes=["brightness"]),
        st("light.kitchen", "off", friendly_name="Kitchen", supported_color_modes=["brightness"]),
        st("scene.movie_night", (now - timedelta(hours=20)).isoformat(), friendly_name="Movie Night"),
        st("climate.thermostat", "heat", friendly_name="Thermostat", current_temperature=21.5, temperature=22,
           hvac_modes=["off", "heat", "cool", "auto"], min_temp=7, max_temp=35),
        st("fan.bedroom_fan", "on", friendly_name="Bedroom Fan", percentage=50, percentage_step=25),
        st("lock.front_door", "locked", friendly_name="Front Door"),
        st("switch.coffee_maker", "off", friendly_name="Coffee Maker"),
        st("weather.home", "partlycloudy", friendly_name="Home", temperature=22, temperature_unit="°C", humidity=48, wind_speed=11),
    ]
    (WORK / "states.json").write_text(json.dumps(states, indent=1), encoding="utf-8")

    notes = [
        {"Text": "Pick up the dry cleaning", "Done": False, "Created": (now - timedelta(hours=3)).isoformat()},
        {"Text": "Call the dentist about Thursday", "Done": False, "Created": (now - timedelta(hours=2)).isoformat()},
        {"Text": "Send the slides before lunch", "Done": True, "Created": (now - timedelta(hours=1)).isoformat()},
    ]
    (HOME / "notes.json").write_text(json.dumps(notes, indent=1), encoding="utf-8")


if __name__ == "__main__":
    write(sys.argv[1] if len(sys.argv) > 1 else "notch", "hello" in sys.argv[2:])
    print(HOME)
