"""A stand-in Home Assistant serving sample entities, for the README recordings.

Answers /api/states and service calls (toggle, turn_on, turn_off,
set_temperature, ...) from its own copy of the states, and logs each call, so
the island's real HTTP code runs without any real home behind it.

  python stub_ha.py <states.json> <port> <calls.log>
"""
import json
import sys, threading
from datetime import datetime, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def serve(states_path, port, log_path):
    states = {s["entity_id"]: s for s in json.load(open(states_path, encoding="utf-8"))}
    lock = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def send(self, code, obj):
            body = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            p = self.path.split("?")[0]
            if p in ("/api/", "/api"):
                return self.send(200, {"message": "API running."})
            if p == "/api/states":
                with lock:
                    return self.send(200, list(states.values()))
            if p.startswith("/api/states/"):
                with lock:
                    s = states.get(p[len("/api/states/"):])
                return self.send(200, s) if s else self.send(404, {"message": "Entity not found."})
            self.send(404, {})

        def do_POST(self):
            n = int(self.headers.get("Content-Length") or 0)
            body = json.loads(self.rfile.read(n) or b"{}")
            p = self.path.split("?")[0]
            with open(log_path, "a", encoding="utf-8") as f:
                f.write(p + " " + json.dumps(body) + "\n")
            parts = p.split("/")   # /api/services/<domain>/<service>
            if len(parts) >= 5 and parts[2] == "services":
                svc, eid = parts[4], body.get("entity_id")
                with lock:
                    s = states.get(eid) if isinstance(eid, str) else None
                    if s:
                        if svc == "toggle": s["state"] = "off" if s["state"] == "on" else "on"
                        elif svc == "turn_on": s["state"] = "on"
                        elif svc == "turn_off": s["state"] = "off"
                        elif svc == "set_temperature" and "temperature" in body: s["attributes"]["temperature"] = body["temperature"]
                        elif svc == "set_hvac_mode" and "hvac_mode" in body: s["state"] = body["hvac_mode"]
                        if "brightness_pct" in body: s["attributes"]["brightness"] = int(body["brightness_pct"] * 2.55)
                if parts[3] == "weather":
                    # what a real Home Assistant answers: a week by day, the next hours by hour
                    kind = body.get("type", "daily")
                    now = datetime.now().astimezone().replace(minute=0, second=0, microsecond=0)
                    if kind == "hourly":
                        temps, conds = [22, 23, 23, 22, 21, 19, 18, 17], ["partlycloudy", "sunny", "sunny", "partlycloudy", "cloudy", "cloudy", "clear-night", "clear-night"]
                        fc = [{"datetime": (now + timedelta(hours=i + 1)).isoformat(), "temperature": t, "condition": c} for i, (t, c) in enumerate(zip(temps, conds))]
                    else:
                        fc = [{"datetime": (now + timedelta(days=i)).isoformat(), "temperature": hi, "templow": lo, "condition": c}
                              for i, (hi, lo, c) in enumerate([(24, 15, "partlycloudy"), (26, 16, "sunny"), (21, 14, "rainy"), (22, 13, "cloudy")])]
                    return self.send(200, {"service_response": {eid if isinstance(eid, str) else "weather.home": {"forecast": fc}}})
                return self.send(200, [])
            self.send(404, {})

    ThreadingHTTPServer(("127.0.0.1", int(port)), Handler).serve_forever()


if __name__ == "__main__":
    serve(*sys.argv[1:4])
