using Isola.App;
using Isola.Native;

namespace Isola.Shell;

/// <summary>One active display path, as Windows' DisplayConfig reports it.</summary>
public sealed record DisplayPath(string GdiName, string FriendlyName, string DevicePath, uint Technology, int Width, int Height, double Hz)
{
    public bool Internal => Technology is Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL or Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
        or Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED or Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS;
    public string Connector => Technology switch
    {
        Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI => "HDMI",
        Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL => "DisplayPort",
        Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_MIRACAST => "Wireless",
        14 => "USB-C",
        _ when Internal => "Built-in",
        _ => "Display",
    };
}

/// <summary>A monitor as the island uses it: where it is and its scale.</summary>
public sealed record Monitor(nint Handle, string GdiName, RECT Bounds, uint Dpi, bool Primary = false);

public interface IDisplaySource
{
    IReadOnlyList<DisplayPath> ActivePaths();
    IReadOnlyList<Monitor> Monitors();
    /// <summary>Whether this computer has a built-in panel at all, switched on or not (a desktop has none).</summary>
    bool HasBuiltInPanel();
}

public sealed unsafe class RealDisplays : IDisplaySource
{
    public IReadOnlyList<DisplayPath> ActivePaths()
    {
        var list = new List<DisplayPath>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Win32.GetDisplayConfigBufferSizes(Win32.QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return list;
            var paths = new DISPLAYCONFIG_PATH_INFO[np]; var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            int r;
            fixed (DISPLAYCONFIG_PATH_INFO* pp = paths) fixed (DISPLAYCONFIG_MODE_INFO* pm = modes)
                r = Win32.QueryDisplayConfig(Win32.QDC_ONLY_ACTIVE_PATHS, ref np, pp, ref nm, pm, 0);
            if (r == 122 /*ERROR_INSUFFICIENT_BUFFER: topology changed under us*/) continue;
            if (r != 0) { Log.W("display", $"QueryDisplayConfig failed {r}"); return list; }
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header.type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME; src.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                src.header.adapterId = p.sourceInfo.adapterId; src.header.id = p.sourceInfo.id;
                Win32.DisplayConfigGetDeviceInfo(&src);
                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgt.header.type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME; tgt.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
                tgt.header.adapterId = p.targetInfo.adapterId; tgt.header.id = p.targetInfo.id;
                Win32.DisplayConfigGetDeviceInfo(&tgt);
                int w = 0, h = 0;
                uint mi = p.targetInfo.modeInfoIdx;
                if (mi != 0xFFFFFFFF && mi < nm && modes[mi].infoType == 2) { w = (int)modes[mi].activeCx; h = (int)modes[mi].activeCy; }
                double hz = p.targetInfo.refreshRate.Denominator > 0 ? (double)p.targetInfo.refreshRate.Numerator / p.targetInfo.refreshRate.Denominator : 0;
                list.Add(new DisplayPath(new string(src.viewGdiDeviceName), new string(tgt.monitorFriendlyDeviceName),
                    new string(tgt.monitorDevicePath), p.targetInfo.outputTechnology, w, h, hz));
            }
            return list;
        }
        return list;
    }

    // Every path, inactive ones too: a laptop panel that a display-switching tool turned
    // off still has one. Only the output technology is read.
    bool? builtIn;
    public bool HasBuiltInPanel()
    {
        if (builtIn is { } known) return known;
        if (Win32.GetDisplayConfigBufferSizes(Win32.QDC_ALL_PATHS, out uint np, out uint nm) != 0) return true;   // can't tell: assume a laptop, as before
        var paths = new DISPLAYCONFIG_PATH_INFO[np]; var modes = new DISPLAYCONFIG_MODE_INFO[nm];
        int r;
        fixed (DISPLAYCONFIG_PATH_INFO* pp = paths) fixed (DISPLAYCONFIG_MODE_INFO* pm = modes)
            r = Win32.QueryDisplayConfig(Win32.QDC_ALL_PATHS, ref np, pp, ref nm, pm, 0);
        if (r != 0) return true;
        bool any = false;
        for (int i = 0; i < np; i++)
            if (paths[i].targetInfo.outputTechnology is Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL or Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED or Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED)
                any = true;
        builtIn = any;
        Log.I("display", any ? "this computer has a built-in panel" : "no built-in panel (a desktop): \"Built-in\" means the main screen");
        return any;
    }

    public IReadOnlyList<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        var gh = System.Runtime.InteropServices.GCHandle.Alloc(list);
        try { Win32.EnumDisplayMonitors(0, 0, &Enum, System.Runtime.InteropServices.GCHandle.ToIntPtr(gh)); }
        finally { gh.Free(); }
        return list;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static int Enum(nint mon, nint hdc, RECT* r, nint data)
    {
        var list = (List<Monitor>)System.Runtime.InteropServices.GCHandle.FromIntPtr(data).Target;
        Win32.GetDpiForMonitor(mon, 0, out uint dx, out _);
        var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        bool primary = Win32.GetMonitorInfo(mon, &mi) && (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0;
        list.Add(new Monitor(mon, Win32.MonitorDevice(mon), Win32.MonitorRect(mon), dx == 0 ? 96 : dx, primary));
        return 1;
    }
}

/// <summary>
/// Finds the laptop's own panel among the active displays (by output
/// technology, never by position or "primary", which display tools and the user
/// change), and reports displays connecting and disconnecting.
/// Display changes come in bursts - a TV re-trains its link, Windows
/// re-enumerates - so alerts wait for the topology to hold still.
/// </summary>
public sealed class DisplayWatcher
{
    readonly IDisplaySource src;
    public Monitor Laptop { get; private set; }
    public IReadOnlyList<DisplayPath> Paths { get; private set; } = Array.Empty<DisplayPath>();
    public IReadOnlyList<Monitor> Monitors { get; private set; } = Array.Empty<Monitor>();
    public event Action LaptopChanged;
    public event Action<DisplayPath, bool> DisplayChanged;   // path, connected
    Dictionary<string, DisplayPath> known;
    long settleTimer;
    public double SettleSeconds = 2.5;

    public DisplayWatcher(IDisplaySource s) { src = s; }

    public void Refresh(string why)
    {
        var paths = src.ActivePaths();
        Paths = paths;
        var mons = src.Monitors();
        Monitors = mons;
        var mon = Pick(paths, mons);
        bool changed = !Equals(mon, Laptop);
        Laptop = mon;
        if (changed)
        {
            Log.I("display", mon == null ? $"no laptop panel on the desktop ({why})" : $"laptop panel {mon.GdiName} at {mon.Bounds} dpi {mon.Dpi} ({why})");
            LaptopChanged?.Invoke();
        }
        if (known == null) { known = paths.Where(p => !p.Internal).ToDictionary(Key); Log.I("display", "displays: " + string.Join(", ", paths.Select(Describe))); return; }
        Ui.Cancel(ref settleTimer);
        settleTimer = Ui.After(SettleSeconds, Settle);
    }

    /// <summary>"laptop", "primary", or a display's device path (Settings -> Behavior -> Screen).</summary>
    public Func<string> ShowOn = () => "laptop";

    /// <summary>
    /// The screen the island lives on. "laptop" finds the built-in panel by its
    /// output technology (never by position or primary-ness, which display tools
    /// and the user change) and, if it is off, leaves the island off. A named
    /// display that is unplugged falls back to the laptop, then the primary.
    /// </summary>
    Monitor Pick(IReadOnlyList<DisplayPath> paths, IReadOnlyList<Monitor> mons)
    {
        string pref = ShowOn?.Invoke() ?? "laptop";
        Monitor ByPath(DisplayPath p) => p == null ? null : mons.FirstOrDefault(m => string.Equals(m.GdiName, p.GdiName, StringComparison.OrdinalIgnoreCase));
        Monitor pick = null;
        if (pref == "primary") pick = mons.FirstOrDefault(m => m.Primary);
        else if (pref != "laptop") pick = ByPath(paths.FirstOrDefault(p => string.Equals(p.DevicePath, pref, StringComparison.OrdinalIgnoreCase) || p.FriendlyName == pref));
        if (pick == null)
        {
            var lap = paths.FirstOrDefault(p => p.Internal);
            pick = ByPath(lap);
            if (pick == null && lap != null) Log.W("display", $"laptop path {lap.GdiName} has no monitor yet");
        }
        // A desktop has no built-in panel, and "laptop" (the default) used to
        // leave the island off there for good. Without one it means the main
        // screen. A laptop whose panel is merely off still stays off.
        if (pick == null && (pref != "laptop" || !src.HasBuiltInPanel())) pick = mons.FirstOrDefault(m => m.Primary);
        return pick;
    }

    static string Key(DisplayPath p) => string.IsNullOrEmpty(p.DevicePath) ? p.FriendlyName + "|" + p.GdiName : p.DevicePath;
    public static string Describe(DisplayPath p) => $"{(string.IsNullOrEmpty(p.FriendlyName) ? p.Connector + " display" : p.FriendlyName)} [{p.Connector}{(p.Width > 0 ? $" {p.Width}x{p.Height}" : "")}{(p.Hz > 0 ? $" @{p.Hz:0}Hz" : "")}]";

    void Settle()
    {
        var now = src.ActivePaths().Where(p => !p.Internal).ToDictionary(Key);
        foreach (var (k, p) in now) if (!known.ContainsKey(k)) { Log.I("display", "connected: " + Describe(p)); DisplayChanged?.Invoke(p, true); }
        foreach (var (k, p) in known) if (!now.ContainsKey(k)) { Log.I("display", "disconnected: " + Describe(p)); DisplayChanged?.Invoke(p, false); }
        known = now;
    }
}
