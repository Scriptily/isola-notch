using System.Diagnostics;
using Isola.App;
using Microsoft.Win32;

namespace Isola.Services;

/// <summary>One app's use of the camera or the microphone, as Windows records it.</summary>
public readonly record struct DeviceUse(string Device, string App, string Key, bool InUse);

/// <summary>
/// Says when an app starts or stops using the camera or the microphone, naming it, for transparency. It looks
/// once a second at what's live: the processes streaming from a camera (Media Foundation's camera activity
/// monitor, the IR camera Windows Hello uses included) and those recording from a microphone (an active session
/// on a capture device). It first read Windows' usage record in the registry, which newer Windows no longer
/// keeps there (it moved to a protected database), so nothing was ever seen.
///
/// Background recorders (NVIDIA's container, Medal, OBS) open and close the microphone all day: a start or a stop
/// is only told once it has held for a moment, and an app that keeps going on and off is told once, then left
/// quiet for a minute, so the island isn't full of them.
/// </summary>
public sealed class PrivacyWatch
{
    readonly IClock clock;
    readonly Func<IEnumerable<DeviceUse>> read;
    /// <summary>An app started (true) or stopped (false) using the camera or microphone: device, app name.</summary>
    public event Action<string, string, bool> Changed;

    /// <summary>A start counts once it has held this long, a stop once it has lasted this long.</summary>
    public const double StartHold = 0.8, StopHold = 1.5;
    /// <summary>After telling about an app, its next starts and stops are kept quiet this long.</summary>
    public const double Quiet = 60;

    sealed class Track { public bool On, Settled, StartTold; public double Since; public string App = ""; public double QuietUntil = double.NegativeInfinity; }
    readonly Dictionary<string, Track> tracks = new();
    long timer;
    bool first = true;

    readonly bool background;
    bool reading;

    /// <param name="read">what's in use now; <paramref name="background"/>: read on a worker (it talks to Windows' audio and camera services)</param>
    public PrivacyWatch(IClock clock, Func<IEnumerable<DeviceUse>> read, bool background = false) { this.clock = clock; this.read = read; this.background = background; }

    public void Start() { Ui.Cancel(ref timer); first = true; tracks.Clear(); Poll(); }
    public void Stop() => Ui.Cancel(ref timer);

    /// <summary>Looks at the record once (the timer calls it every second; tests call it directly).</summary>
    internal void Poll()
    {
        timer = Ui.After(1.0, Poll);
        if (!background) { Apply(Read()); return; }
        if (reading) return;
        reading = true;
        Task.Run(() => { var r = Read(); Ui.Post(() => { reading = false; Apply(r); }); });
    }

    List<DeviceUse> Read()
    {
        try { return read().ToList(); } catch (Exception ex) { Log.D("privacy", "couldn't see what's in use: " + ex.Message); return null; }
    }

    void Apply(List<DeviceUse> now)
    {
        if (now == null) return;
        double t = clock.Now;
        // what's no longer listed has stopped
        var seen = now.Select(u => u.Device + "|" + u.Key).ToHashSet();
        foreach (var (id, tr) in tracks) if ((tr.On || !tr.Settled) && !seen.Contains(id)) { int bar = id.IndexOf('|'); now.Add(new DeviceUse(id[..bar], tr.App, id[(bar + 1)..], false)); }
        foreach (var u in now)
        {
            string id = u.Device + "|" + u.Key;
            if (!tracks.TryGetValue(id, out var tr))
            {
                // what's in use when Isola starts is known, not news; an app seen for the first time later is
                tracks[id] = tr = new Track { On = first && u.InUse, Since = t, App = u.App, Settled = true };
            }
            if (tr.On != u.InUse) { tr.On = u.InUse; tr.Since = t; tr.Settled = false; tr.App = u.App; }
            if (tr.Settled || t - tr.Since < (tr.On ? StartHold : StopHold)) continue;
            tr.Settled = true;
            if (tr.On)
            {
                if (t < tr.QuietUntil) continue;   // on and off again within the minute: left quiet
                tr.QuietUntil = t + Quiet; tr.StartTold = true;
            }
            else
            {
                if (!tr.StartTold) continue;   // a stop is told only after its start was
                tr.StartTold = false;
            }
            Log.I("privacy", $"{u.App} {(tr.On ? "started" : "stopped")} using the {u.Device}");
            Changed?.Invoke(u.Device, u.App, tr.On);
        }
        first = false;
    }

    // ---- what's live ---------------------------------------------------------------------------------------

    static readonly Dictionary<string, string> names = new();

    /// <summary>
    /// What's using the camera and microphone now, one entry per app (Chrome's many processes are one Chrome).
    /// </summary>
    public static IEnumerable<DeviceUse> Live(Func<List<uint>> recording, Func<IReadOnlyList<CameraUse>> cameras)
    {
        var list = new List<DeviceUse>();
        foreach (var app in (recording?.Invoke() ?? new()).Select(p => AppOf(p, false)).Where(a => a != null).Distinct())
            list.Add(new DeviceUse("microphone", app, app.ToLowerInvariant(), true));
        foreach (var app in (cameras?.Invoke() ?? Array.Empty<CameraUse>()).Select(c => AppOf(c.Pid, c.Infrared)).Where(a => a != null).Distinct())
            list.Add(new DeviceUse("camera", app, app.ToLowerInvariant(), true));
        return list;
    }

    /// <summary>A process's name as people know it ("OBS Studio"); Windows' service host on the IR camera is Windows Hello.</summary>
    internal static string AppOf(uint pid, bool infrared)
    {
        string path = Native.Win32.ProcessPath(pid);
        if (string.IsNullOrEmpty(path)) return null;
        if (Path.GetFileName(path).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)) return infrared ? "Windows Hello" : "Windows";
        return NameOf(path.Replace('\\', '#'), true);
    }

    /// <summary>"C:#Program Files#obs-studio#bin#64bit#obs64.exe" is "OBS Studio"; a Store app's family name, its words.</summary>
    internal static string NameOf(string key, bool desktop)
    {
        lock (names) if (names.TryGetValue(key, out var n)) return n;
        string name;
        if (desktop)
        {
            string path = key.Replace('#', '\\');
            name = Path.GetFileNameWithoutExtension(path);
            try { if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d) name = d.Trim(); } catch { }
        }
        else name = StoreName(key);
        lock (names) names[key] = name;
        return name;
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" is "Windows Camera".</summary>
    internal static string StoreName(string family)
    {
        string id = family.Split('_')[0];
        string last = id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : id;
        return System.Text.RegularExpressions.Regex.Replace(last, @"(?<=[a-z])(?=[A-Z])", " ");
    }
}
