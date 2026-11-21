using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Isola.App;

namespace Isola.Services;

// Media Foundation's camera activity monitor (mfidl.h): which processes are streaming from which camera, the IR
// camera Windows Hello uses included. Windows calls back with a fresh report whenever that changes.

[GeneratedComInterface, Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8")]
public partial interface IMFSensorProcessActivity
{
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int GetStreamingState(out int streaming);
    [PreserveSig] int GetStreamingMode(out int mode);
    [PreserveSig] int GetReportTime(out long fileTime);
}

[GeneratedComInterface, Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD")]
public unsafe partial interface IMFSensorActivityReport
{
    [PreserveSig] int GetFriendlyName(char* name, uint cch, out uint written);
    [PreserveSig] int GetSymbolicLink(char* link, uint cch, out uint written);
    [PreserveSig] int GetProcessCount(out uint count);
    [PreserveSig] int GetProcessActivity(uint index, out IMFSensorProcessActivity activity);
}

[GeneratedComInterface, Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777")]
public partial interface IMFSensorActivitiesReport
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetActivityReport(uint index, out IMFSensorActivityReport report);
    [PreserveSig] int GetActivityReportByDeviceName([MarshalAs(UnmanagedType.LPWStr)] string name, out IMFSensorActivityReport report);
}

[GeneratedComInterface, Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751")]
public partial interface IMFSensorActivitiesReportCallback
{
    [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport report);
}

[GeneratedComInterface, Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB")]
public partial interface IMFSensorActivityMonitor
{
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
}

/// <summary>A process streaming from a camera, as the monitor last reported it.</summary>
public readonly record struct CameraUse(uint Pid, string Camera, bool Infrared);

[GeneratedComClass]
internal sealed unsafe partial class SensorReportHandler : IMFSensorActivitiesReportCallback, IAgileObject
{
    public volatile List<CameraUse> Latest = new();
    string lastSig = "";

    public int OnActivitiesReport(IMFSensorActivitiesReport report)
    {
        var list = new List<CameraUse>();
        try
        {
            report.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (report.GetActivityReport(i, out var dev) < 0 || dev == null) continue;
                char* buf = stackalloc char[512];
                string name = dev.GetFriendlyName(buf, 512, out uint w) >= 0 ? new string(buf, 0, (int)Math.Max(0, (int)w - 1)).TrimEnd('\0') : "";
                string link = dev.GetSymbolicLink(buf, 512, out uint lw) >= 0 ? new string(buf, 0, (int)Math.Max(0, (int)lw - 1)).TrimEnd('\0') : "";
                bool ir = name.Contains(" IR", StringComparison.OrdinalIgnoreCase) || name.Contains("Infrared", StringComparison.OrdinalIgnoreCase);
                dev.GetProcessCount(out uint pc);
                for (uint k = 0; k < pc; k++)
                {
                    if (dev.GetProcessActivity(k, out var pa) < 0 || pa == null) continue;
                    pa.GetStreamingState(out int on);
                    pa.GetProcessId(out uint pid);
                    if (on != 0 && pid != 0) list.Add(new CameraUse(pid, name.Length > 0 ? name : link, ir));
                }
            }
        }
        catch (Exception ex) { Log.D("privacy", "camera report unreadable: " + ex.Message); }
        // what the camera monitor said, when it changes (to see what a sign-in or an app really reports)
        string sig = string.Join("; ", list.Select(u => $"pid {u.Pid} on {u.Camera}{(u.Infrared ? " (IR)" : "")}"));
        if (sig != lastSig) { lastSig = sig; Log.D("privacy", "camera report: " + (sig.Length > 0 ? sig : "nothing streaming")); }
        Latest = list;
        return 0;
    }
}

/// <summary>Which processes are using a camera right now; empty when the monitor isn't available.</summary>
public sealed class SensorActivity : IDisposable
{
    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfsensorgroup.dll")] static extern int MFCreateSensorActivityMonitor(nint callback, out nint monitor);

    readonly SensorReportHandler handler = new();
    IMFSensorActivityMonitor monitor;
    nint pHandler;

    public bool Running => monitor != null;
    public IReadOnlyList<CameraUse> Now => handler.Latest;

    public bool Start(StrategyBasedComWrappers cw)
    {
        if (monitor != null) return true;
        try
        {
            MFStartup(0x00020070 /* MF_VERSION */, 0);
            nint unk = cw.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
            var iid = typeof(IMFSensorActivitiesReportCallback).GUID;
            int qhr = Marshal.QueryInterface(unk, in iid, out pHandler);
            Marshal.Release(unk);
            if (qhr < 0) return false;
            int hr = MFCreateSensorActivityMonitor(pHandler, out nint pm);
            if (hr < 0 || pm == 0) { Log.D("privacy", $"camera activity monitor unavailable (0x{hr:X8})"); return false; }
            monitor = (IMFSensorActivityMonitor)cw.GetOrCreateObjectForComInstance(pm, CreateObjectFlags.None);
            Marshal.Release(pm);
            hr = monitor.Start();
            if (hr < 0) { Log.D("privacy", $"camera activity monitor didn't start (0x{hr:X8})"); monitor = null; return false; }
            Log.D("privacy", "watching camera use");
            return true;
        }
        catch (Exception ex) { Log.D("privacy", "camera activity monitor: " + ex.Message); monitor = null; return false; }
    }

    public void Dispose()
    {
        try { monitor?.Stop(); } catch { }
        monitor = null;
        if (pHandler != 0) { Marshal.Release(pHandler); pHandler = 0; }
        handler.Latest = new();
    }
}
