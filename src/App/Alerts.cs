using Vortice.Mathematics;
using Isola.Render;
using Isola.Services;

namespace Isola.App;

/// <summary>
/// Every kind of popup the island shows, built in one place: the real events
/// and Settings -> Notifications -> Preview popups both come here, so what the
/// preview shows is what the events will show.
/// </summary>
public static class Alerts
{
    public static Alert BluetoothRadio(bool on) => new()
    {
        Key = "btradio", Kind = "bluetooth", Title = on ? "Bluetooth On" : "Bluetooth Off", Subtitle = on ? "Devices can reconnect" : "Wireless devices disconnected",
        Glyph = Glyph.Bluetooth, Accent = on ? Col.Hex(0x0a84ff) : Col.Gray(0.45f), Seconds = 2.5, Sound = on ? Sounds.Connect : Sounds.Disconnect,
    };

    public static Alert Battery(string e, int percent) => e switch
    {
        "plugged" => new() { Key = "battery", Kind = "battery", Title = "Charging", Subtitle = $"{percent}%", Glyph = Glyph.Charging, Accent = Col.Hex(0x34c759), Seconds = 2.8, Sound = Sounds.Charge, Batteries = { ("", percent, true) } },
        "unplugged" => new() { Key = "battery", Kind = "battery", Title = "On battery", Subtitle = $"{percent}% remaining", Glyph = Glyph.Battery, Accent = Col.Gray(0.5f), Seconds = 2.5, Batteries = { ("", percent, false) } },
        "low" => new() { Key = "battery", Kind = "battery", Title = "Low battery", Subtitle = $"{percent}% remaining · plug in soon", Glyph = Glyph.Battery, Accent = Col.Hex(0xff453a), Seconds = 6, Priority = 1, Sound = Sounds.Low, Batteries = { ("", percent, false) } },
        _ => new() { Key = "battery", Kind = "battery", Title = "Fully charged", Subtitle = "100%", Glyph = Glyph.Battery, Accent = Col.Hex(0x34c759), Seconds = 3, Batteries = { ("", 100, false) } },
    };

    /// <summary>A Bluetooth device connecting or going; AirPods get their three battery rings.</summary>
    public static Alert Device(string id, string name, char glyph, bool connected, int battery, AirPodsState pods)
    {
        var a = new Alert
        {
            Key = "bt:" + id, Kind = "bluetooth", Title = name, Subtitle = connected ? "Connected" : "Disconnected",
            Glyph = glyph, Accent = connected ? Col.Hex(0x0a84ff) : Col.Gray(0.45f), Seconds = connected ? 3.5 : 2.5,
            Sound = connected ? Sounds.Connect : Sounds.Disconnect,
        };
        if (connected && pods != null) AddPods(a, pods);
        else if (connected && battery >= 0) a.Batteries.Add(("", battery, false));
        return a;
    }

    /// <summary>The iPhone-style card when the AirPods case opens.</summary>
    public static Alert PodsCase(AirPodsState s)
    {
        var a = new Alert { Key = "pods", Kind = "airpods", Title = s.Name, Subtitle = "Case open", Glyph = Glyph.Headphones, Accent = Col.Gray(0.85f), Seconds = 4, Sound = Sounds.Connect };
        AddPods(a, s);
        return a;
    }

    static void AddPods(Alert a, AirPodsState s)
    {
        if (s.Left < 0 && s.Right < 0) return;
        a.Batteries.Add(("L", s.Left, s.LeftCharging));
        a.Batteries.Add(("R", s.Right, s.RightCharging));
        if (s.Case >= 0) a.Batteries.Add(("Case", s.Case, s.CaseCharging));
        a.Glyph = Glyph.Headphones;
    }

    public static Alert Call(string id, string caller, byte[] icon, Action decline, Action answer, Action open) => new()
    {
        Key = "call:" + id, Kind = "call", Title = caller, Subtitle = "Incoming call · iPhone", Glyph = Glyph.Phone, Image = icon,
        // outranks the timer; gives up after 75 s even if the call's end is never seen
        Accent = Col.Hex(0x34c759), Priority = 3, Sticky = true, MaxSeconds = 75, Seconds = 30, Clicked = open, Sound = Sounds.Call,
        Actions =
        {
            new AlertAction { Label = "Decline", Glyph = Glyph.PhoneEnd, Tint = Col.Hex(0xff453a), Run = decline },
            new AlertAction { Label = "Answer", Glyph = Glyph.Phone, Tint = Col.Hex(0x34c759), Run = answer },
        },
    };

    // A notification can carry pages of text; the popup shows two lines at most,
    // and laying out the rest every frame is wasted work.
    internal static string Cap(string s, int max) => string.IsNullOrEmpty(s) ? s ?? "" : s.Length <= max ? s : s[..max].TrimEnd() + "…";

    public static Alert Notification(string id, string app, string title, string body, byte[] icon, double seconds) => new()
    {
        Key = "notif:" + id, Kind = "notification", Title = Cap(title, 160), Subtitle = Cap(app, 80), Detail = Cap(body, 400), Image = icon, Sound = Sounds.Notification,
        Glyph = Glyph.Bell, Accent = Col.Hex(0x0a84ff), Seconds = seconds,
    };

    /// <summary>How long a notification with a one-time code stays, to read or copy it.</summary>
    internal const double CodeSeconds = 20;

    /// <summary>
    /// Adds a Copy button to a notification that carries a one-time code (as
    /// Windows' own banners do), and keeps it on screen long enough to use.
    /// With message previews off, the code isn't shown, only offered.
    /// </summary>
    public static Alert OfferCode(Alert a, string code, bool previews, Action<string> copy)
    {
        if (a == null || string.IsNullOrEmpty(code)) return a;
        a.Seconds = Math.Max(a.Seconds, CodeSeconds);
        if (!previews) a.Detail = "A one-time code is ready to copy";
        a.Actions.Add(new AlertAction { Label = "Copy code", Glyph = Glyph.Copy, Tint = Col.Hex(0x0a84ff), Primary = true, Run = () => copy(code) });
        return a;
    }

    /// <summary>A download or conversion finished: open it, or show it in its folder.</summary>
    public static Alert DownloadDone(DownloadJob job, Action open, Action reveal) => new()
    {
        Key = "download:" + job.Id, Kind = "download", Title = job.Kind == "convert" ? "Converted" : "Downloaded",
        Subtitle = job.ToShelf ? "On your shelf" : Path.GetFileName(job.Output), Detail = job.Label,
        Glyph = Glyph.Download, Accent = Col.Hex(0x34c759), Seconds = 6, Sound = Services.Sounds.Notification,
        Actions =
        {
            new AlertAction { Label = "Open", Glyph = Glyph.Play, Tint = Col.Hex(0x0a84ff), Primary = true, Run = open },
            new AlertAction { Label = "Show in folder", Glyph = Glyph.Folder, Run = reveal },
        },
    };

    /// <summary>It didn't work: why, in words, and a way to try again.</summary>
    public static Alert DownloadFailed(DownloadJob job, Action retry) => new()
    {
        Key = "download:" + job.Id, Kind = "download", Title = job.Kind == "convert" ? "Couldn't convert it" : "Couldn't download it",
        Subtitle = job.Error, Detail = job.Label,
        Glyph = Glyph.Warning, Accent = Col.Hex(0xff9f0a), Seconds = 8,
        Actions = { new AlertAction { Label = "Try again", Glyph = Glyph.Refresh, Tint = Col.Hex(0x0a84ff), Primary = true, Run = retry } },
    };

    /// <summary>The moment after copying: a check, briefly.</summary>
    public static Alert Copied() => new()
    {
        Key = "copied", Kind = "copied", Title = "Code copied", Subtitle = "Paste it where it's asked for",
        Glyph = Glyph.Check, Accent = Col.Hex(0x34c759), Seconds = 1.8,
    };

    public static Alert Display(string name, string detail, bool connected) => new()
    {
        Key = "display:" + name, Kind = "display", Title = name, Subtitle = connected ? $"Connected · {detail}" : "Disconnected",
        Glyph = Glyph.Display, Accent = connected ? Col.Hex(0x5e5ce6) : Col.Gray(0.45f), Seconds = 3.5,
    };

    /// <summary>
    /// Windows names a Bluetooth output "{endpoint} ({device})", and AirPods'
    /// device is their Find My record: "AirPods Pro 2 (AirPods Pro 2 - Find My)".
    /// The suffix goes, and so does a parenthesis that only repeats the name.
    /// </summary>
    internal static string OutputName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        name = name.Replace(" - Find My", "", StringComparison.OrdinalIgnoreCase).Trim();
        int open = name.IndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && name.EndsWith(')') && name[(open + 2)..^1].Trim().Equals(name[..open].Trim(), StringComparison.OrdinalIgnoreCase)) name = name[..open].Trim();
        return name;
    }

    public static Alert Output(string name) => new()
    {
        Key = "output", Kind = "audio", Title = OutputName(name), Subtitle = "Sound output", Glyph = Glyph.Volume3, Accent = Col.Hex(0x0a84ff), Seconds = 2.5,
    };

    public static Alert Track(string title, string artist, byte[] art, Color4 accent) => new()
    {
        Key = "track", Kind = "track", Title = title, Subtitle = artist, Image = art, Glyph = Glyph.Music, Seconds = 3.5, Accent = accent,
    };

    public static Alert Timer(double length, bool whileClosed, Action again, Action stop, Action ended, Action shown = null)
    {
        string what = CountdownTimer.Describe(length) + " timer";
        return new Alert
        {
            Key = "timer", Kind = "timer", Glyph = Glyph.Timer, Accent = Col.Hex(0xff9f0a), Priority = 2, Seconds = whileClosed ? 12 : 60,
            Title = whileClosed ? "Timer ended" : "Timer done",
            Subtitle = whileClosed ? what + " ended while the island wasn't running" : what,
            // the alarm plays while the popup is on screen: a call interrupting it
            // silences it, and it rings again when the popup comes back
            Clicked = stop, Ended = ended, Shown = shown, Suspended = ended,
            Sound = whileClosed ? null : Sounds.Timer,
            Actions =
            {
                new AlertAction { Label = "Again", Glyph = Glyph.Repeat, Tint = Col.Hex(0xff9f0a), Run = again },
                new AlertAction { Label = "Stop", Glyph = Glyph.Close, Tint = Col.White, Primary = true, Run = stop },
            },
        };
    }

    public static Alert Updated(string version) => new()
    {
        Key = "updated", Kind = "info", Title = $"Updated to v{version}", Subtitle = "Isola", Glyph = Glyph.Check, Accent = Col.Hex(0x34c759), Seconds = 3.5,
    };

    /// <summary>
    /// One of each, with sample data, for Settings -> Notifications -> Preview
    /// popups: each shows for 3.5 s in turn, and its buttons only close it.
    /// </summary>
    public static List<Alert> Samples(Action<string> dismiss, byte[] art, Color4 artAccent)
    {
        var pods = new AirPodsState { Name = "AirPods Pro 2", Left = 82, Right = 79, Case = 64, CaseCharging = true };
        var list = new List<Alert>
        {
            Device("preview-pods", "AirPods Pro 2", Glyph.Headphones, true, -1, pods),
            Device("preview-speaker", "Bluetooth Speaker", Glyph.Volume3, true, 60, null),
            Device("preview-gone", "Wireless Keyboard", Glyph.Bluetooth, false, -1, null),
            PodsCase(pods),
            BluetoothRadio(true),
            Display("Living Room TV", "HDMI · 3840×2160 @ 120 Hz", true),
            Display("Living Room TV", "", false),
            Battery("plugged", 93), Battery("low", 15), Battery("full", 100),
            Output("Speakers (High Definition Audio)"),
            Notification("preview", "Messages", "Sam", "Are you still coming tonight? We're leaving around 8 if you want a ride", null, 3.5),
            Track("Golden Hour", "Isola Sample", art, artAccent),
            Call("preview", "Mom", null, () => dismiss("call:preview"), () => dismiss("call:preview"), null),
            Timer(600, false, () => dismiss("timer"), () => dismiss("timer"), null),
            Updated("1.0.0"),
        };
        // Unique keys: two display samples (or three battery ones) sharing a key
        // would replace each other in the queue and the preview would skip them.
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            a.Key = $"preview:{i}:{a.Key}"; a.Seconds = 3.5; a.Sticky = false; a.Priority = 0;
            a.Clicked = () => dismiss(a.Key);
            foreach (var act in a.Actions) act.Run = () => dismiss(a.Key);
        }
        return list;
    }
}
