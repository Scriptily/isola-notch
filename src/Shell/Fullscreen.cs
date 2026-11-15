using Isola.App;
using Isola.Native;

namespace Isola.Shell;

/// <summary>A top-level window as the fullscreen check needs to see it.</summary>
public readonly record struct WinInfo(nint Hwnd, RECT Rect, bool Visible, bool Minimized, bool Cloaked, bool Maximized,
    bool HasCaption, bool ClickThrough, bool ToolWindow, string Class, uint Pid);

public interface IWindowSource
{
    /// <summary>Top-level windows, topmost first (Z order).</summary>
    IEnumerable<WinInfo> ZOrder();
    nint Foreground();
    WinInfo Describe(nint hwnd);
}

public sealed unsafe class RealWindows : IWindowSource
{
    public IEnumerable<WinInfo> ZOrder()
    {
        nint h = Win32.GetTopWindow(0);
        int guard = 0;
        while (h != 0 && guard++ < 2000)
        {
            yield return Describe(h);
            h = Win32.GetWindow(h, 2 /*GW_HWNDNEXT*/);
        }
    }

    public nint Foreground() => Win32.GetForegroundWindow();

    public WinInfo Describe(nint h)
    {
        bool vis = Win32.IsWindowVisible(h);
        RECT r = default;
        // DWM's frame bounds exclude the invisible resize borders, so a
        // maximized window does not look like it overhangs its monitor.
        if (Win32.DwmGetWindowAttribute(h, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)) != 0) Win32.GetWindowRect(h, out r);
        long style = Win32.GetWindowLongPtr(h, Win32.GWL_STYLE), ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE);
        Win32.GetWindowThreadProcessId(h, out uint pid);
        return new WinInfo(h, r, vis, Win32.IsIconic(h), vis && Win32.IsCloaked(h), Win32.IsZoomed(h),
            (style & 0x00C00000) == 0x00C00000, (ex & Win32.WS_EX_TRANSPARENT) != 0, (ex & Win32.WS_EX_TOOLWINDOW) != 0,
            vis ? Win32.ClassName(h) : "", pid);
    }
}

/// <summary>
/// Decides whether something is fullscreen on a given monitor - the laptop
/// panel for hiding the island, any monitor for releasing hotkeys.
///
/// It looks at Z order, not at the foreground window: a video left fullscreen
/// on the laptop while you work on the TV is still covering the laptop, and a
/// game fullscreen on the TV must not hide the island on the laptop. The
/// topmost real window that touches the monitor decides.
/// </summary>
public sealed class FullscreenDetector
{
    readonly IWindowSource src;
    public readonly HashSet<nint> Ours = new();
    public readonly HashSet<uint> OurPids = new() { Win32.GetCurrentProcessId() };

    // Shell surfaces and overlays that cover the screen without being "an app in fullscreen".
    static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "ForegroundStaging", "MultitaskingViewFrame",
        "EdgeUiInputTopWndClass", "NativeHWNDHost", "Shell_InputSwitchTopLevelWindow", "TaskListThumbnailWnd",
        "CEF-OSC-WIDGET", "NVOpenGLPbuffer", "GameBarOverlayWindow", "ApplicationFrameWindow_Hidden",
        "SysShadow", "tooltips_class32", "#32768", "IME", "MSCTFIME UI", "Windows.Internal.Shell.TabProxyWindow",
    };

    public FullscreenDetector(IWindowSource s) { src = s; }

    bool Candidate(in WinInfo w)
    {
        if (!w.Visible || w.Minimized || w.Cloaked) return false;
        if (Ours.Contains(w.Hwnd) || OurPids.Contains(w.Pid)) return false;
        if (w.ClickThrough) return false;                       // overlays (NVIDIA, Discord, Steam FPS) are click-through
        if (w.Rect.Width <= 0 || w.Rect.Height <= 0) return false;
        if (SkipClasses.Contains(w.Class)) return false;
        return true;
    }

    public static bool Covers(in WinInfo w, RECT mon)
    {
        // A maximized window with a title bar is "big", not fullscreen - even
        // with an auto-hidden taskbar, where it does reach every edge.
        if (w.Maximized && w.HasCaption) return false;
        return w.Rect.Left <= mon.Left && w.Rect.Top <= mon.Top && w.Rect.Right >= mon.Right && w.Rect.Bottom >= mon.Bottom;
    }

    /// <summary>The window that is fullscreen on this monitor, or 0.</summary>
    public nint FullscreenOn(RECT mon)
    {
        foreach (var w in src.ZOrder())
        {
            if (!Candidate(w)) continue;
            if (!w.Rect.Intersects(mon)) continue;
            // the topmost real window on this monitor decides
            return Covers(w, mon) ? w.Hwnd : 0;
        }
        return 0;
    }

    /// <summary>Is the foreground window fullscreen on whichever monitor it is on?</summary>
    public bool ForegroundFullscreen(Func<nint, RECT> monitorOf)
    {
        var fg = src.Foreground();
        if (fg == 0) return false;
        var w = src.Describe(fg);
        if (!Candidate(w)) return false;
        return Covers(w, monitorOf(fg));
    }
}
