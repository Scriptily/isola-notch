using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Isola.App;

namespace Isola.Services;

/// <summary>
/// Each app's own sound level, from its audio sessions' meters, so the island can follow the app it
/// shows instead of everything the speakers play: Spotify at 0% under a loud video is silent here even
/// though it's "playing", and the video's level doesn't move Spotify's visualizer.
/// </summary>
public sealed partial class AudioService
{
    sealed class MeteredSession { public string Exe = "", Name = ""; public IAudioMeterInformation Meter; public ISimpleAudioVolume Vol; }
    volatile List<MeteredSession> metered = new();
    int meterBusy;
    double meteredAt = double.NegativeInfinity;

    /// <summary>Re-reads which apps have audio sessions (at most every 2 s, on a worker thread: enumerating them is slow).</summary>
    public void RefreshAppMeters(double now)
    {
        if (enumr == null || now - meteredAt < 2) return;
        meteredAt = now;
        if (Interlocked.Exchange(ref meterBusy, 1) == 1) return;
        Task.Run(() =>
        {
            try { metered = ReadMeters(); }
            catch (Exception ex) { Log.W("audio", "app meters unavailable: " + ex.Message); }
            finally { Interlocked.Exchange(ref meterBusy, 0); }
        });
    }

    List<MeteredSession> ReadMeters()
    {
        var list = new List<MeteredSession>();
        if (enumr.GetDefaultAudioEndpoint(0, 1, out var def) < 0 || def == null) return list;
        if (def.Activate(typeof(IAudioSessionManager2).GUID, 23, 0, out nint pm) < 0) return list;
        var mgr = (IAudioSessionManager2)cw.GetOrCreateObjectForComInstance(pm, CreateObjectFlags.None); Marshal.Release(pm);
        if (mgr.GetSessionEnumerator(out var en) < 0 || en == null) return list;
        en.GetCount(out int n);
        for (int i = 0; i < n; i++)
        {
            if (en.GetSession(i, out var s) < 0 || s == null) continue;
            if (s.IsSystemSoundsSession() == 0) continue;
            s.GetState(out int state);
            if (state == 2) continue;
            s.GetProcessId(out uint pid);
            if (pid == 0 || s is not IAudioMeterInformation meter) continue;
            var (name, exe) = ProcName(pid);
            list.Add(new MeteredSession { Exe = exe, Name = name, Meter = meter, Vol = s as ISimpleAudioVolume });
        }
        return list;
    }

    /// <summary>
    /// The loudest of an app's audio sessions right now (Chrome has one per tab), by the media session's
    /// app id and name; null when none of the sessions can be told to be that app's.
    /// </summary>
    public float? AppPeak(string aumid, string appName)
    {
        float? best = null;
        foreach (var m in metered)
        {
            if (!AppMatches(aumid, appName, m.Exe, m.Name)) continue;
            float p = 0, v = 1; bool muted = false;
            try { m.Meter.GetPeakValue(out p); m.Vol?.GetMasterVolume(out v); m.Vol?.GetMute(out muted); } catch { continue; }
            best = Math.Max(best ?? 0, Audible(p, v, muted));
        }
        return best;
    }

    /// <summary>
    /// What of a session's sound reaches the speakers. Its meter reads the sound before the app's own
    /// volume (the Volume mixer scales it before drawing its bar), so Spotify turned to 0% still read as
    /// full music: it counted as heard and moved the visualizer, with nothing coming out.
    /// </summary>
    internal static float Audible(float peak, float volume, bool muted) => muted ? 0 : peak * Math.Clamp(volume, 0, 1);

    /// <summary>
    /// Whether an audio session's program is the app a media session belongs to. Media sessions name
    /// their app by an id: an exe ("Spotify.exe"), a registered name ("Chrome", "MSEdge") or a Store
    /// package ("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"); some only by a name to compare.
    /// </summary>
    internal static bool AppMatches(string aumid, string appName, string exe, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant();
        string id = (aumid ?? "").ToLowerInvariant();
        if (stem.Length >= 3 && id.Contains(stem)) return true;
        return !string.IsNullOrWhiteSpace(appName) && !string.IsNullOrWhiteSpace(name) && string.Equals(appName.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
