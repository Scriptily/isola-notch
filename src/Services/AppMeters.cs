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
    sealed class MeteredSession { public string Exe = "", Name = "", Id = ""; public uint Pid; public IAudioMeterInformation Meter; public ISimpleAudioVolume Vol; public IChannelAudioVolume Channels; public int State; }
    volatile List<MeteredSession> metered = new();
    volatile Dictionary<uint, ProcInfo> procs = new();

    /// <summary>A process from the last snapshot, its Store package looked up the first time it's asked for.</summary>
    ProcInfo Proc(uint pid)
    {
        if (!procs.TryGetValue(pid, out var p)) return null;
        p.Family ??= MediaApps.FamilyOf(pid);
        return p;
    }

    bool Owns(MeteredSession m, string aumid, string appName) => MediaApps.Owns(aumid, appName, m.Pid, Proc);
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
            try
            {
                var map = MediaApps.Snapshot(cw);
                var list = ReadMeters();
                foreach (var m in list) if (map.TryGetValue(m.Pid, out var pi)) pi.Name = m.Name;   // the program's own description, for the last-resort match
                procs = map; metered = list;
            }
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
            s.GetSessionInstanceIdentifier(out string sid);
            list.Add(new MeteredSession { Exe = exe, Name = name, Pid = pid, Meter = meter, Vol = s as ISimpleAudioVolume, Channels = s as IChannelAudioVolume, State = state, Id = sid ?? "" });
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
            if (!Owns(m, aumid, appName)) continue;
            float p = 0, v = 1; bool muted = false;
            try { m.Meter.GetPeakValue(out p); m.Vol?.GetMasterVolume(out v); m.Vol?.GetMute(out muted); } catch { continue; }
            best = Math.Max(best ?? 0, Audible(p, v, muted));
        }
        return best;
    }

    /// <summary>Every audio session of an app, as numbers, for the verbose log (diagnosing what the meters miss).</summary>
    public string AppSessionsReport(string aumid, string appName)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in metered)
        {
            if (!Owns(m, aumid, appName)) continue;
            try
            {
                m.Meter.GetPeakValue(out float p); float v = -1; bool mu = false; m.Vol?.GetMasterVolume(out v); m.Vol?.GetMute(out mu);
                string ch = "";
                if (m.Channels != null && m.Channels.GetChannelCount(out uint n) >= 0)
                    for (uint i = 0; i < n; i++) { m.Channels.GetChannelVolume(i, out float cv); ch += (i > 0 ? "/" : "") + cv.ToString("0.00"); }
                string id = m.Id.Length > 12 ? m.Id[^12..] : m.Id;
                sb.Append($" [{Path.GetFileName(m.Exe)} state {m.State} peak {p:0.000} vol {v:0.00}{(mu ? " muted" : "")} ch {ch} ..{id}]");
            }
            catch (Exception ex) { sb.Append(" [error " + ex.Message + "]"); }
        }
        return sb.ToString();
    }

    /// <summary>
    /// What of a session's sound reaches the speakers. Its meter reads the sound before the app's own
    /// volume (the Volume mixer scales it before drawing its bar), so Spotify turned to 0% still read as
    /// full music: it counted as heard and moved the visualizer, with nothing coming out.
    /// </summary>
    internal static float Audible(float peak, float volume, bool muted) => muted ? 0 : peak * Math.Clamp(volume, 0, 1);

}
