using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Isola.App;

namespace Isola.Services;

// ---- Core Audio COM (source-generated, NativeAOT-safe) ---------------------

[StructLayout(LayoutKind.Sequential)] public struct PROPERTYKEY { public Guid fmtid; public uint pid; }
[StructLayout(LayoutKind.Explicit, Size = 24)] public struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public nint p; [FieldOffset(8)] public uint u; }

[GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
public partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
public partial interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, int clsCtx, nint activationParams, out nint iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[GeneratedComInterface, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
public partial interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int i, out PROPERTYKEY key);
    [PreserveSig] int GetValue(in PROPERTYKEY key, out PROPVARIANT value);
}

[GeneratedComInterface, Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
public partial interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int GetChannelCount(out uint n);
    [PreserveSig] int SetMasterVolumeLevel(float db, in Guid ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float v, in Guid ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float v);
    [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, in Guid ctx);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float v, in Guid ctx);
    [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float v);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, in Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[GeneratedComInterface, Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
public unsafe partial interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(nint data);
}

[GeneratedComInterface, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
public partial interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
}

[GeneratedComInterface, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
public partial interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
}

[GeneratedComClass]
public sealed partial class VolumeCallback : IAudioEndpointVolumeCallback
{
    public Action<bool, float> Notify;
    public Guid Ours;
    public unsafe int OnNotify(nint data)
    {
        // AUDIO_VOLUME_NOTIFICATION_DATA: GUID context; BOOL muted; float master; ...
        var ctx = *(Guid*)data;
        bool muted = *(int*)(data + 16) != 0;
        float v = *(float*)(data + 20);
        if (ctx != Ours) Notify?.Invoke(muted, v);   // ignore changes we made ourselves
        return 0;
    }
}

[GeneratedComClass]
public sealed partial class DeviceCallback : IMMNotificationClient
{
    public Action<string> DefaultChanged;
    public int OnDeviceStateChanged(string id, int state) => 0;
    public int OnDeviceAdded(string id) => 0;
    public int OnDeviceRemoved(string id) => 0;
    public int OnDefaultDeviceChanged(int flow, int role, string id) { if (flow == 0 && role == 1) DefaultChanged?.Invoke(id); return 0; }   // render, multimedia
    public int OnPropertyValueChanged(string id, PROPERTYKEY key) => 0;
}

/// <summary>
/// Output volume, mute and peak level of the default playback device, and
/// notice of the default device changing.
/// </summary>
public sealed partial class AudioService
{
    static readonly Guid CLSID_Enumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly PROPERTYKEY PKEY_FriendlyName = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    readonly StrategyBasedComWrappers cw = new();
    IMMDeviceEnumerator enumr;
    IAudioEndpointVolume vol;
    IAudioMeterInformation meter;
    readonly VolumeCallback volCb = new() { Ours = Guid.NewGuid() };
    readonly DeviceCallback devCb = new();
    public float Volume { get; private set; }
    public bool Muted { get; private set; }
    public string DeviceName { get; private set; } = "";
    public event Action<bool, float> VolumeChanged;      // UI thread
    public event Action<string> OutputChanged;           // UI thread, friendly name
    float smoothed;
    string boundId;

    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, int ctx, in Guid iid, out nint obj);

    public void Start()
    {
        try
        {
            int hr = CoCreateInstance(CLSID_Enumerator, 0, 23 /*CLSCTX_ALL*/, typeof(IMMDeviceEnumerator).GUID, out nint p);
            if (hr < 0) throw new COMException("MMDeviceEnumerator", hr);
            enumr = (IMMDeviceEnumerator)cw.GetOrCreateObjectForComInstance(p, CreateObjectFlags.None);
            Marshal.Release(p);
            volCb.Notify = (m, v) => Ui.Post(() => { bool changed = Math.Abs(v - Volume) > 0.004 || m != Muted; Muted = m; Volume = v; if (changed) VolumeChanged?.Invoke(m, v); });
            // Windows announces a new default once per role (console, multimedia,
            // communications); announce each actual change once.
            devCb.DefaultChanged = id => Ui.Post(() =>
            {
                if (id == boundId) return;
                Bind();
                Log.I("audio", "default output -> " + DeviceName);
                OutputChanged?.Invoke(DeviceName);
            });
            enumr.RegisterEndpointNotificationCallback(devCb);
            Bind();
            Log.I("audio", $"output '{DeviceName}' volume {Volume:P0}{(Muted ? " muted" : "")}");
        }
        catch (Exception ex) { Log.E("audio", "Core Audio unavailable: no volume HUD or waveform levels", ex); }
    }

    void Bind()
    {
        try { vol?.UnregisterControlChangeNotify(volCb); } catch { }
        vol = null; meter = null;
        if (enumr.GetDefaultAudioEndpoint(0, 1, out var dev) < 0 || dev == null) { DeviceName = "No output"; boundId = null; return; }
        dev.GetId(out boundId);
        if (dev.Activate(typeof(IAudioEndpointVolume).GUID, 23, 0, out nint pv) >= 0)
        {
            vol = (IAudioEndpointVolume)cw.GetOrCreateObjectForComInstance(pv, CreateObjectFlags.None); Marshal.Release(pv);
            vol.GetMasterVolumeLevelScalar(out float v); vol.GetMute(out bool m); Volume = v; Muted = m;
            vol.RegisterControlChangeNotify(volCb);
        }
        if (dev.Activate(typeof(IAudioMeterInformation).GUID, 23, 0, out nint pm) >= 0)
        {
            meter = (IAudioMeterInformation)cw.GetOrCreateObjectForComInstance(pm, CreateObjectFlags.None); Marshal.Release(pm);
        }
        DeviceName = FriendlyName(dev) ?? "Speakers";
    }

    static string FriendlyName(IMMDevice dev)
    {
        try
        {
            if (dev.OpenPropertyStore(0, out var ps) < 0) return null;
            if (ps.GetValue(PKEY_FriendlyName, out var pv) < 0) return null;
            string s = pv.vt == 31 && pv.p != 0 ? Marshal.PtrToStringUni(pv.p) : null;
            PropVariantClear(ref pv);
            return s;
        }
        catch { return null; }
    }
    [LibraryImport("ole32.dll")] private static partial int PropVariantClear(ref PROPVARIANT pv);

    /// <summary>Output peak, smoothed (fast attack, slow release) for the waveform.</summary>
    public float Peak
    {
        get
        {
            float p = 0;
            try { meter?.GetPeakValue(out p); } catch { }
            smoothed = p > smoothed ? p : smoothed * 0.82f + p * 0.18f;
            return smoothed;
        }
    }

    public void SetVolume(float v) { try { vol?.SetMasterVolumeLevelScalar(Math.Clamp(v, 0, 1), volCb.Ours); Volume = v; } catch { } }
    public void ToggleMute() { try { vol?.SetMute(!Muted, volCb.Ours); Muted = !Muted; } catch { } }
}
