using System.Diagnostics;
using Isola.App;
using Microsoft.Win32;

namespace Isola.Services;

/// <summary>One app's use of the camera or the microphone, as Windows records it.</summary>
public readonly record struct DeviceUse(string Device, string App, string Key, bool InUse);

/// <summary>
/// Says when an app starts or stops using the camera or the microphone, naming it, for transparency. Windows
/// keeps a record of each app's use (what its own "in use" indicator and privacy settings read): a start time,
/// and a stop time that stays 0 while it's in use. It's looked at once a second.
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

    public PrivacyWatch(IClock clock, Func<IEnumerable<DeviceUse>> read = null) { this.clock = clock; this.read = read ?? ReadRegistry; }

    public void Start() { Ui.Cancel(ref timer); first = true; tracks.Clear(); Poll(); }
    public void Stop() => Ui.Cancel(ref timer);

    /// <summary>Looks at the record once (the timer calls it every second; tests call it directly).</summary>
    internal void Poll()
    {
        timer = Ui.After(1.0, Poll);
        List<DeviceUse> now;
        try { now = read().ToList(); } catch (Exception ex) { Log.D("privacy", "couldn't read the record: " + ex.Message); return; }
        double t = clock.Now;
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

    // ---- Windows' record ----------------------------------------------------------------------------------

    const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    static readonly Dictionary<string, string> names = new();

    static IEnumerable<DeviceUse> ReadRegistry()
    {
        var list = new List<DeviceUse>();
        foreach (var (cap, device) in new[] { ("webcam", "camera"), ("microphone", "microphone") })
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var root = hive.OpenSubKey(Store + cap);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    if (sub == "NonPackaged")
                    {
                        using var np = root.OpenSubKey(sub);
                        if (np == null) continue;
                        foreach (var exe in np.GetSubKeyNames()) Add(list, device, np, exe, true);
                    }
                    else Add(list, device, root, sub, false);
                }
            }
        return list;
    }

    static void Add(List<DeviceUse> list, string device, RegistryKey parent, string sub, bool desktop)
    {
        using var k = parent.OpenSubKey(sub);
        if (k?.GetValue("LastUsedTimeStart") is not long start || start == 0) return;
        bool inUse = k.GetValue("LastUsedTimeStop") is long stop && stop == 0;
        list.Add(new DeviceUse(device, NameOf(sub, desktop), sub, inUse));
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
