using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Isola.App;

namespace Isola.Services;

[GeneratedComInterface, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
public partial interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint n);
    [PreserveSig] int Item(uint i, out IMMDevice dev);
}

[GeneratedComInterface, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
public partial interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(nint guid, uint flags, out nint ctl);
    [PreserveSig] int GetSimpleAudioVolume(nint guid, uint flags, out nint vol);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
}

[GeneratedComInterface, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
public partial interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int n);
    [PreserveSig] int GetSession(int i, out IAudioSessionControl2 s);
}

/// <summary>IAudioSessionControl and IAudioSessionControl2, flattened in vtable order.</summary>
[GeneratedComInterface, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
public partial interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, in Guid ctx);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, in Guid ctx);
    [PreserveSig] int GetGroupingParam(out Guid g);
    [PreserveSig] int SetGroupingParam(in Guid g, in Guid ctx);
    [PreserveSig] int RegisterAudioSessionNotification(nint cb);
    [PreserveSig] int UnregisterAudioSessionNotification(nint cb);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
}

/// <summary>A session's per-channel volumes, which the Volume mixer doesn't show.</summary>
[GeneratedComInterface, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8")]
public partial interface IChannelAudioVolume
{
    [PreserveSig] int GetChannelCount(out uint n);
    [PreserveSig] int SetChannelVolume(uint i, float v, in Guid ctx);
    [PreserveSig] int GetChannelVolume(uint i, out float v);
}

[GeneratedComInterface, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
public partial interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float v, in Guid ctx);
    [PreserveSig] int GetMasterVolume(out float v);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, in Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

/// <summary>
/// IPolicyConfig: how every audio switcher (EarTrumpet, SoundSwitch, the
/// Windows sound panel itself) sets the default device. It is not a
/// documented interface - the one exception in this project - so it is used
/// only for "make this the default output", and if it ever stops working the
/// island opens Sound settings instead.
/// </summary>
[GeneratedComInterface, Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
public partial interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(nint a, nint b);
    [PreserveSig] int GetDeviceFormat(nint a, int b, nint c);
    [PreserveSig] int ResetDeviceFormat(nint a);
    [PreserveSig] int SetDeviceFormat(nint a, nint b, nint c);
    [PreserveSig] int GetProcessingPeriod(nint a, int b, nint c, nint d);
    [PreserveSig] int SetProcessingPeriod(nint a, nint b);
    [PreserveSig] int GetShareMode(nint a, nint b);
    [PreserveSig] int SetShareMode(nint a, nint b);
    [PreserveSig] int GetPropertyValue(nint a, int b, nint c, nint d);
    [PreserveSig] int SetPropertyValue(nint a, int b, nint c, nint d);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
}

public sealed class OutputDevice { public string Id = "", Name = ""; public bool Default; public float Volume; public bool Muted; internal IAudioEndpointVolume Vol; }
public sealed class AppSession
{
    public string Key = "", Name = "", ExePath = ""; public uint Pid; public float Volume; public bool Muted; public bool Active;
    internal ISimpleAudioVolume Vol;
}

public sealed partial class AudioService
{
    static readonly Guid CLSID_PolicyConfig = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    public List<OutputDevice> Outputs { get; private set; } = new();
    public List<AppSession> Apps { get; private set; } = new();
    readonly Dictionary<uint, (string name, string exe)> procCache = new();

    int mixerBusy;

    /// <summary>
    /// Re-read devices and app sessions on a worker thread (Core Audio objects
    /// are free-threaded), then swap the lists in on the UI thread. Enumerating
    /// endpoints and sessions and reading every app's version info took long
    /// enough on the UI thread to freeze the tab transition.
    /// </summary>
    public void RefreshMixerAsync(Action done)
    {
        if (Interlocked.Exchange(ref mixerBusy, 1) == 1) return;
        Task.Run(() =>
        {
            try { RefreshMixer(); }
            finally { Interlocked.Exchange(ref mixerBusy, 0); }
            Ui.Post(done);
        });
    }

    /// <summary>Re-read devices and app sessions. Builds new lists and swaps them in, so readers never see a half-built one.</summary>
    public void RefreshMixer()
    {
        if (enumr == null) return;
        try
        {
            string defId = null;
            if (enumr.GetDefaultAudioEndpoint(0, 1, out var def) >= 0 && def != null) def.GetId(out defId);
            var outs = new List<OutputDevice>();
            if (enumr.EnumAudioEndpoints(0, 1, out nint pc) >= 0 && pc != 0)
            {
                var col = (IMMDeviceCollection)cw.GetOrCreateObjectForComInstance(pc, CreateObjectFlags.None); Marshal.Release(pc);
                col.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    if (col.Item(i, out var d) < 0 || d == null) continue;
                    d.GetId(out string id);
                    var od = new OutputDevice { Id = id, Name = FriendlyName(d) ?? "Output", Default = id == defId };
                    if (d.Activate(typeof(IAudioEndpointVolume).GUID, 23, 0, out nint pv) >= 0)
                    {
                        od.Vol = (IAudioEndpointVolume)cw.GetOrCreateObjectForComInstance(pv, CreateObjectFlags.None); Marshal.Release(pv);
                        od.Vol.GetMasterVolumeLevelScalar(out od.Volume); od.Vol.GetMute(out od.Muted);
                    }
                    outs.Add(od);
                }
            }
            Outputs = outs.OrderByDescending(o => o.Default).ThenBy(o => o.Name).ToList();

            var apps = new Dictionary<string, AppSession>();
            if (def != null && def.Activate(typeof(IAudioSessionManager2).GUID, 23, 0, out nint pm) >= 0)
            {
                var mgr = (IAudioSessionManager2)cw.GetOrCreateObjectForComInstance(pm, CreateObjectFlags.None); Marshal.Release(pm);
                if (mgr.GetSessionEnumerator(out var en) >= 0 && en != null)
                {
                    en.GetCount(out int n);
                    for (int i = 0; i < n; i++)
                    {
                        if (en.GetSession(i, out var s) < 0 || s == null) continue;
                        if (s.IsSystemSoundsSession() == 0) continue;     // S_OK means it *is* system sounds
                        s.GetState(out int state);
                        if (state == 2) continue;                          // expired
                        s.GetProcessId(out uint pid);
                        if (pid == 0) continue;
                        var vol = s as ISimpleAudioVolume;
                        if (vol == null) continue;
                        var (name, exe) = ProcName(pid);
                        s.GetDisplayName(out string dn);
                        if (!string.IsNullOrWhiteSpace(dn) && !dn.StartsWith('@')) name = dn;
                        // one row per app: several sessions (Chrome tabs) share the exe
                        string key = string.IsNullOrEmpty(exe) ? "pid:" + pid : exe.ToLowerInvariant();
                        vol.GetMasterVolume(out float v); vol.GetMute(out bool mu);
                        if (apps.TryGetValue(key, out var existing)) { existing.Active |= state == 1; continue; }
                        apps[key] = new AppSession { Key = key, Name = name, ExePath = exe, Pid = pid, Volume = v, Muted = mu, Active = state == 1, Vol = vol };
                    }
                }
            }
            Apps = apps.Values.OrderByDescending(a => a.Active).ThenBy(a => a.Name).ToList();
        }
        catch (Exception ex) { Log.W("audio", "mixer refresh failed: " + ex.Message); }
    }

    (string, string) ProcName(uint pid)
    {
        lock (procCache) return ProcNameLocked(pid);   // the mixer and the app meters both read it on workers
    }

    (string, string) ProcNameLocked(uint pid)
    {
        if (procCache.TryGetValue(pid, out var c)) return c;
        string exe = Native.Win32.ProcessPath(pid) ?? "", name = "";
        try { if (exe.Length > 0) name = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
        if (string.IsNullOrWhiteSpace(name)) name = exe.Length > 0 ? Path.GetFileNameWithoutExtension(exe) : "PID " + pid;
        if (name.Length > 28) name = name[..28];
        return procCache[pid] = (name.Trim(), exe);
    }

    public void SetDeviceVolume(OutputDevice d, float v) { try { d.Vol?.SetMasterVolumeLevelScalar(Math.Clamp(v, 0, 1), volCb.Ours); d.Volume = v; if (d.Default) Volume = v; } catch { } }
    public void ToggleDeviceMute(OutputDevice d) { try { d.Vol?.SetMute(!d.Muted, volCb.Ours); d.Muted = !d.Muted; } catch { } }
    public void SetAppVolume(AppSession a, float v) { try { a.Vol?.SetMasterVolume(Math.Clamp(v, 0, 1), Guid.Empty); a.Volume = v; } catch { } }
    public void ToggleAppMute(AppSession a)
    {
        try
        {
            // mute every session of that app, as the Volume mixer does
            a.Vol?.SetMute(!a.Muted, Guid.Empty); a.Muted = !a.Muted;
            Log.I("audio", $"{a.Name} {(a.Muted ? "muted" : "unmuted")}");
        }
        catch { }
    }

    public bool SetDefault(OutputDevice d)
    {
        try
        {
            int hr = CoCreateInstance(CLSID_PolicyConfig, 0, 1 /*INPROC*/, typeof(IPolicyConfig).GUID, out nint p);
            if (hr < 0) throw new COMException("PolicyConfig", hr);
            var pc = (IPolicyConfig)cw.GetOrCreateObjectForComInstance(p, CreateObjectFlags.None); Marshal.Release(p);
            for (int role = 0; role <= 2; role++) pc.SetDefaultEndpoint(d.Id, role);
            Log.I("audio", "default output set to " + d.Name);
            return true;
        }
        catch (Exception ex)
        {
            Log.W("audio", "couldn't set the default device (" + ex.Message + "); opening Sound settings");
            Native.Win32.ShellExecute("ms-settings:sound");
            return false;
        }
    }
}
