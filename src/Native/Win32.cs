using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Isola.Native;

// Every Win32 entry point the island uses, source-generated (LibraryImport) so
// the same code compiles under NativeAOT. Only documented, long-stable APIs are
// used here on purpose: these are the ones a Windows feature update keeps.

[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly bool Contains(RECT o) => o.Left <= Left && o.Top <= Top && o.Right >= Right && o.Bottom >= Bottom;
    public readonly bool Intersects(RECT o) => o.Left < Right && o.Right > Left && o.Top < Bottom && o.Bottom > Top;
    public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct WNDCLASSEXW
{
    public uint cbSize, style; public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
    public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground;
    public char* lpszMenuName, lpszClassName; public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MONITORINFOEXW
{
    public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
    public fixed char szDevice[32];
}

[StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
[StructLayout(LayoutKind.Sequential)] public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
[StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize, dwTime; }
[StructLayout(LayoutKind.Sequential)] public struct COPYDATASTRUCT { public nint dwData; public int cbData; public nint lpData; }
[StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public nint hwndTarget; }
[StructLayout(LayoutKind.Sequential)] public struct RAWINPUTHEADER { public uint dwType, dwSize; public nint hDevice, wParam; }
[StructLayout(LayoutKind.Sequential)] public struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }
[StructLayout(LayoutKind.Sequential)] public struct POWERBROADCAST_SETTING { public Guid PowerSetting; public uint DataLength; public byte Data; }
[StructLayout(LayoutKind.Sequential)]
public struct POINTER_INFO
{
    public uint pointerType, pointerId, frameId, pointerFlags; public nint sourceDevice, hwndTarget;
    public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
    public uint dwTime, historyCount; public int InputData; public uint dwKeyStates; public ulong PerformanceCount; public int ButtonChangeType;
}
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public unsafe struct SHFILEINFOW { public nint hIcon; public int iIcon; public uint dwAttributes; public fixed char szDisplayName[260]; public fixed char szTypeName[80]; }
[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
[StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public int fIcon, xHotspot, yHotspot; public nint hbmMask, hbmColor; }
[StructLayout(LayoutKind.Sequential)] public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public nint bmBits; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SHELLEXECUTEINFOW
{
    public int cbSize; public uint fMask; public nint hwnd; public char* lpVerb, lpFile, lpParameters, lpDirectory;
    public int nShow; public nint hInstApp, lpIDList; public char* lpClass; public nint hkeyClass; public uint dwHotKey; public nint hIconOrMonitor, hProcess;
}
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NOTIFYICONDATAW
{
    public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
    public fixed char szTip[128]; public uint dwState, dwStateMask; public fixed char szInfo[256]; public uint uVersion;
    public fixed char szInfoTitle[64]; public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
}
[StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public uint cbSize; public nint hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public nint lParam; }

// ---- DisplayConfig (QueryDisplayConfig) -------------------------------------
[StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }
[StructLayout(LayoutKind.Sequential)] public struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }
[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }
[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
}
[StructLayout(LayoutKind.Sequential)] public struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DISPLAYCONFIG_MODE_INFO
{
    [FieldOffset(0)] public uint infoType; [FieldOffset(4)] public uint id; [FieldOffset(8)] public LUID adapterId;
    // target mode: video signal info; we only need the active size for alerts.
    [FieldOffset(16)] public ulong pixelRate; [FieldOffset(24)] public DISPLAYCONFIG_RATIONAL hSyncFreq; [FieldOffset(32)] public DISPLAYCONFIG_RATIONAL vSyncFreq;
    [FieldOffset(40)] public uint activeCx; [FieldOffset(44)] public uint activeCy;
    // source mode overlays the same union: width, height, pixelFormat, position
    [FieldOffset(16)] public uint srcWidth; [FieldOffset(20)] public uint srcHeight;
}
[StructLayout(LayoutKind.Sequential)] public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DISPLAYCONFIG_SOURCE_DEVICE_NAME { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public fixed char viewGdiDeviceName[32]; }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint flags, outputTechnology; public ushort edidManufactureId, edidProductCodeId;
    public uint connectorInstance; public fixed char monitorFriendlyDeviceName[64]; public fixed char monitorDevicePath[128];
}

public static unsafe partial class Win32
{
    // window styles
    public const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000,
        WS_EX_NOACTIVATE = 0x08000000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_APPWINDOW = 0x40000;
    public const int GWL_EXSTYLE = -20, GWL_STYLE = -16;
    public static readonly nint HWND_TOPMOST = -1, HWND_NOTOPMOST = -2, HWND_MESSAGE = -3, HWND_BOTTOM = 1;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOOWNERZORDER = 0x200, SWP_FRAMECHANGED = 0x20;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5, SW_SHOWNA = 8, SW_RESTORE = 9, SW_SHOWNORMAL = 1;

    // messages
    public const uint WM_DESTROY = 0x2, WM_SIZE = 0x5, WM_ACTIVATE = 0x6, WM_SETFOCUS = 0x7, WM_KILLFOCUS = 0x8, WM_CLOSE = 0x10, WM_QUIT = 0x12,
        WM_ERASEBKGND = 0x14, WM_SHOWWINDOW = 0x18, WM_SETTINGCHANGE = 0x1A, WM_ACTIVATEAPP = 0x1C, WM_SETCURSOR = 0x20, WM_MOUSEACTIVATE = 0x21,
        WM_GETMINMAXINFO = 0x24, WM_WINDOWPOSCHANGING = 0x46, WM_COPYDATA = 0x4A, WM_INPUTLANGCHANGE = 0x51, WM_DISPLAYCHANGE = 0x7E,
        WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1, WM_INPUT = 0xFF, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_SYSCHAR = 0x106,
        WM_COMMAND = 0x111, WM_SYSCOMMAND = 0x112, WM_TIMER = 0x113, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E, WM_CAPTURECHANGED = 0x215,
        WM_POWERBROADCAST = 0x218, WM_DEVICECHANGE = 0x219, WM_IME_STARTCOMPOSITION = 0x10D, WM_MOUSELEAVE = 0x2A3, WM_DPICHANGED = 0x2E0,
        WM_POINTERUPDATE = 0x245, WM_POINTERDOWN = 0x246, WM_POINTERUP = 0x247, WM_POINTERLEAVE = 0x24A, WM_POINTERCAPTURECHANGED = 0x24C,
        WM_CLIPBOARDUPDATE = 0x31D, WM_HOTKEY = 0x312, WM_WTSSESSION_CHANGE = 0x2B1, WM_DWMCOMPOSITIONCHANGED = 0x31E, WM_USER = 0x400, WM_APP = 0x8000,
        WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16, WM_PAINT = 0xF, WM_NCCREATE = 0x81, WM_NCCALCSIZE = 0x83, WM_SYSKEYUP = 0x105, WM_CONTEXTMENU = 0x7B;
    public const int MA_NOACTIVATE = 3, HTTRANSPARENT = -1, HTCLIENT = 1, HTCAPTION = 2;
    public const uint TME_LEAVE = 2, TME_HOVER = 1;
    public const uint PBT_APMPOWERSTATUSCHANGE = 0xA, PBT_APMRESUMEAUTOMATIC = 0x12, PBT_APMSUSPEND = 0x4, PBT_POWERSETTINGCHANGE = 0x8013;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const uint RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 1, RID_INPUT = 0x10000003, RIM_TYPEKEYBOARD = 1;
    public const uint QS_ALLINPUT = 0x4FF, MWMO_INPUTAVAILABLE = 4, WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258, INFINITE = 0xFFFFFFFF;
    public const uint PM_REMOVE = 1;
    public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;
    public const int IDC_ARROW = 32512, IDC_HAND = 32649, IDC_IBEAM = 32513;
    public const uint DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const uint PT_TOUCH = 2, PT_PEN = 3, PT_MOUSE = 4;
    public const uint POINTER_FLAG_INCONTACT = 0x4, POINTER_FLAG_PRIMARY = 0x2000;

    // WinEvents
    public const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_SYSTEM_MOVESIZEEND = 0xB, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17,
        EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_REORDER = 0x8004, EVENT_OBJECT_LOCATIONCHANGE = 0x800B,
        EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public const int OBJID_WINDOW = 0;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint h, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsIconic(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsZoomed(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint h, out RECT r);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetClientRect(nint h, out RECT r);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static partial nint GetWindowLongPtr(nint h, int idx);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static partial nint SetWindowLongPtr(nint h, int idx, nint v);
    [LibraryImport("user32.dll")] public static partial int GetMessageW(out MSG m, nint h, uint min, uint max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PeekMessageW(out MSG m, nint h, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(in MSG m);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(in MSG m);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll")] public static partial nint SendMessageW(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial nint SendMessageTimeoutW(nint h, uint m, nint w, nint l, uint flags, uint timeout, out nint result);
    [LibraryImport("user32.dll")] public static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint ms, uint wake, uint flags);
    [LibraryImport("user32.dll")] public static partial nuint SetTimer(nint h, nuint id, uint ms, nint proc);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(nint h, nuint id);
    [LibraryImport("user32.dll")] public static partial int SetWindowRgn(nint h, nint rgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT t);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ClientToScreen(nint hwnd, ref POINT p);
    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PlaySound(string sound, nint module, uint flags);
    public const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4, SND_LOOP = 0x8, SND_ALIAS = 0x10000, SND_FILENAME = 0x20000;
    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PlaySoundMem(nint sound, nint module, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ScreenToClient(nint h, ref POINT p);
    [LibraryImport("user32.dll")] public static partial nint SetCapture(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ReleaseCapture();
    [LibraryImport("user32.dll")] public static partial nint LoadCursorW(nint inst, nint id);
    [LibraryImport("user32.dll")] public static partial nint SetCursor(nint c);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromPoint(POINT p, uint flags);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromWindow(nint h, uint flags);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromRect(in RECT r, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetMonitorInfo(nint mon, MONITORINFOEXW* mi);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, RECT*, nint, int> cb, nint data);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")] public static partial nint SetFocus(nint h);
    [LibraryImport("user32.dll")] public static partial nint GetFocus();
    [LibraryImport("user32.dll")] public static partial nint SetActiveWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AllowSetForegroundWindow(int pid);
    [LibraryImport("user32.dll")] public static partial nint GetShellWindow();
    [LibraryImport("user32.dll")] public static partial nint GetDesktopWindow();
    [LibraryImport("user32.dll")] public static partial nint GetAncestor(nint h, uint flags);
    [LibraryImport("user32.dll")] public static partial nint GetWindow(nint h, uint cmd);
    [LibraryImport("user32.dll")] public static partial nint GetTopWindow(nint h);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint h, out uint pid);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int GetClassNameW(nint h, char* buf, int max);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int GetWindowTextW(nint h, char* buf, int max);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint FindWindowW(string cls, string name);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint FindWindowExW(nint parent, nint after, string cls, string name);
    [LibraryImport("user32.dll")] public static partial nint WindowFromPoint(POINT p);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint h);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForSystem();
    [LibraryImport("shcore.dll")] public static partial int GetDpiForMonitor(nint mon, int type, out uint dpiX, out uint dpiY);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetProcessDpiAwarenessContext(nint ctx);
    [LibraryImport("user32.dll")] public static partial nint SetThreadDpiAwarenessContext(nint ctx);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterHotKey(nint h, int id);
    [LibraryImport("user32.dll")] public static partial short GetKeyState(int vk);
    [LibraryImport("user32.dll")] public static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")] public static partial nint GetKeyboardLayout(uint thread);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetLastInputInfo(ref LASTINPUTINFO li);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterRawInputDevices(RAWINPUTDEVICE* devs, uint count, uint size);
    [LibraryImport("user32.dll")] public static partial uint GetRawInputData(nint raw, uint cmd, void* data, ref uint size, uint headerSize);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AddClipboardFormatListener(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RemoveClipboardFormatListener(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool OpenClipboard(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EmptyClipboard();
    [LibraryImport("user32.dll")] public static partial nint GetClipboardData(uint fmt);
    [LibraryImport("user32.dll")] public static partial nint SetClipboardData(uint fmt, nint mem);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsClipboardFormatAvailable(uint fmt);
    [LibraryImport("user32.dll")] public static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll")] public static partial nint GetClipboardOwner();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterClipboardFormatW(string name);
    [LibraryImport("user32.dll")] public static partial nint RegisterPowerSettingNotification(nint h, in Guid g, uint flags);
    [LibraryImport("user32.dll")] public static partial nint SetWinEventHook(uint min, uint max, nint mod, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> proc, uint pid, uint tid, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnhookWinEvent(nint h);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessageW(string s);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ChangeWindowMessageFilterEx(nint h, uint msg, uint action, nint info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetPointerInfo(uint id, out POINTER_INFO info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetIconInfo(nint h, out ICONINFO info);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AppendMenuW(nint m, uint flags, nuint id, string text);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenuEx(nint m, uint flags, int x, int y, nint h, nint tpm);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(nint m);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetrics(int idx);
    [LibraryImport("user32.dll")] public static partial uint GetDoubleClickTime();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool MessageBeep(uint type);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int MessageBoxW(nint h, string text, string caption, uint type);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(nint h, nint r, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ValidateRect(nint h, nint r);
    [LibraryImport("user32.dll")] public static partial uint SendInput(uint n, void* inputs, int size);
    [LibraryImport("user32.dll")] public static partial void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    [LibraryImport("gdi32.dll")] public static partial nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [LibraryImport("gdi32.dll")] public static partial nint CreateRectRgn(int l, int t, int r, int b);
    [LibraryImport("gdi32.dll")] public static partial int CombineRgn(nint dst, nint a, nint b, int mode);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint o);
    [LibraryImport("gdi32.dll")] public static partial int GetObjectW(nint h, int size, void* obj);
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial int GetDIBits(nint hdc, nint bmp, uint start, uint lines, void* bits, void* bmi, uint usage);
    public const int RGN_OR = 2;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint GetModuleHandleW(string name);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] public static partial nint CreateMutexW(nint attr, [MarshalAs(UnmanagedType.Bool)] bool owner, string name);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint CreateEventW(nint attr, [MarshalAs(UnmanagedType.Bool)] bool manual, [MarshalAs(UnmanagedType.Bool)] bool initial, string name);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetEvent(nint h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseHandle(nint h);
    [LibraryImport("kernel32.dll")] public static partial uint WaitForSingleObject(nint h, uint ms);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [LibraryImport("kernel32.dll")] public static partial ulong GetTickCount64();
    [LibraryImport("kernel32.dll")] public static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll")] public static partial uint GetCurrentProcessId();
    [LibraryImport("kernel32.dll")] public static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalLock(nint h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GlobalUnlock(nint h);
    [LibraryImport("kernel32.dll")] public static partial nuint GlobalSize(nint h);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalFree(nint h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetProcessWorkingSetSizeEx(nint proc, nint min, nint max, uint flags);
    [LibraryImport("kernel32.dll")] public static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetExitCodeProcess(nint h, out uint code);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool QueryFullProcessImageNameW(nint proc, uint flags, char* buf, ref int size);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetPriorityClass(nint proc, uint cls);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetProcessInformation(nint proc, int cls, void* info, uint size);
    public const uint GMEM_MOVEABLE = 2, CF_UNICODETEXT = 13, CF_HDROP = 15, CF_DIB = 8, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("dwmapi.dll")] public static partial int DwmGetWindowAttribute(nint h, uint attr, void* value, uint size);
    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint h, uint attr, void* value, uint size);
    [LibraryImport("dwmapi.dll")] public static partial int DwmFlush();

    [LibraryImport("user32.dll")] public static partial int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [LibraryImport("user32.dll")] public static partial int QueryDisplayConfig(uint flags, ref uint numPaths, DISPLAYCONFIG_PATH_INFO* paths, ref uint numModes, DISPLAYCONFIG_MODE_INFO* modes, nint topology);
    [LibraryImport("user32.dll")] public static partial int DisplayConfigGetDeviceInfo(void* packet);
    public const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2, QDC_VIRTUAL_MODE_AWARE = 0x10;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000, DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11,
        DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13, DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS = 6, DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI = 5,
        DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL = 10, DISPLAYCONFIG_OUTPUT_TECHNOLOGY_MIRACAST = 15;

    [LibraryImport("shell32.dll")] public static partial nuint SHAppBarMessage(uint msg, ref APPBARDATA data);
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABN_FULLSCREENAPP = 2;
    [LibraryImport("shell32.dll")] public static partial int SHQueryUserNotificationState(out int state);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nuint SHGetFileInfoW(string path, uint attrs, SHFILEINFOW* info, uint size, uint flags);
    [LibraryImport("shell32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShellExecuteExW(SHELLEXECUTEINFOW* info);

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOW
    {
        public int cb; public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public nint hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(string app, char* cmdLine, nint procAttr, nint threadAttr, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint env, string cwd, STARTUPINFOW* si, PROCESS_INFORMATION* pi);

    /// <summary>
    /// Starts a process with no window, outside our job object when the job
    /// allows it (so ending the island's job doesn't end it too). Returns its id, or 0.
    /// </summary>
    public static uint StartDetached(string exe, string args)
    {
        const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000, CREATE_NO_WINDOW = 0x08000000, CREATE_NEW_PROCESS_GROUP = 0x200;
        foreach (uint flags in new[] { CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP, CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP })
        {
            string cmd = $"\"{exe}\" {args}\0";
            var si = new STARTUPINFOW { cb = sizeof(STARTUPINFOW) };
            PROCESS_INFORMATION pi;
            fixed (char* c = cmd)
                if (CreateProcessW(exe, c, 0, 0, false, flags, 0, Path.GetDirectoryName(exe), &si, &pi))
                {
                    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                    return pi.dwProcessId;
                }
            // ERROR_ACCESS_DENIED: the job forbids breaking away; start inside it instead
            if (Marshal.GetLastPInvokeError() != 5) return 0;
        }
        return 0;
    }
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Shell_NotifyIconW(uint msg, NOTIFYICONDATAW* data);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint DragQueryFileW(nint drop, uint index, char* buf, uint size);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int SHParseDisplayName(string name, nint bindCtx, out nint pidl, uint sfgaoIn, out uint sfgaoOut);
    [LibraryImport("shell32.dll")] public static partial int SHOpenFolderAndSelectItems(nint pidlFolder, uint count, nint* items, uint flags);
    [LibraryImport("shell32.dll")] public static partial int SHCreateDataObject(nint pidlFolder, uint cidl, nint* apidl, nint pdtInner, in Guid riid, out nint ppv);
    [LibraryImport("shell32.dll")] public static partial nint ILFindLastID(nint pidl);
    [LibraryImport("shell32.dll")] public static partial nint ILClone(nint pidl);
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ILRemoveLastID(nint pidl);
    [LibraryImport("shell32.dll")] public static partial void ILFree(nint pidl);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int SHCreateItemFromParsingName(string path, nint bc, in Guid riid, out nint ppv);
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    public const uint SEE_MASK_INVOKEIDLIST = 0xC, SEE_MASK_NOASYNC = 0x100, SEE_MASK_FLAG_NO_UI = 0x400;

    [LibraryImport("ole32.dll")] public static partial int OleInitialize(nint reserved);
    [LibraryImport("ole32.dll")] public static partial void OleUninitialize();
    [LibraryImport("ole32.dll")] public static partial int RegisterDragDrop(nint hwnd, nint dropTarget);
    [LibraryImport("ole32.dll")] public static partial int RevokeDragDrop(nint hwnd);
    [LibraryImport("ole32.dll")] public static partial int DoDragDrop(nint dataObj, nint dropSource, uint okEffects, out uint effect);
    [LibraryImport("ole32.dll")] public static partial void ReleaseStgMedium(void* medium);
    [LibraryImport("ole32.dll")] public static partial void CoTaskMemFree(nint p);

    [LibraryImport("powrprof.dll")] public static partial uint PowerSettingRegisterNotification(in Guid g, uint flags, void* recipient, out nint handle);

    [LibraryImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool WTSRegisterSessionNotification(nint h, uint flags);
    public const uint WTS_SESSION_LOCK = 7, WTS_SESSION_UNLOCK = 8;

    // ---- helpers -----------------------------------------------------------
    public static string ClassName(nint h) { char* b = stackalloc char[256]; int n = GetClassNameW(h, b, 256); return new string(b, 0, Math.Max(0, n)); }
    public static string WindowText(nint h) { char* b = stackalloc char[512]; int n = GetWindowTextW(h, b, 512); return new string(b, 0, Math.Max(0, n)); }
    public static bool IsCloaked(nint h) { int c = 0; return DwmGetWindowAttribute(h, DWMWA_CLOAKED, &c, 4) == 0 && c != 0; }
    public static int LoWord(nint v) => (short)((long)v & 0xFFFF);
    public static int HiWord(nint v) => (short)(((long)v >> 16) & 0xFFFF);
    public static uint IdleMilliseconds() { var li = new LASTINPUTINFO { cbSize = 8 }; return GetLastInputInfo(ref li) ? (uint)Environment.TickCount - li.dwTime : 0; }
    public static string ProcessPath(uint pid)
    {
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid); if (h == 0) return null;
        try { char* b = stackalloc char[1024]; int n = 1024; return QueryFullProcessImageNameW(h, 0, b, ref n) ? new string(b, 0, n) : null; } finally { CloseHandle(h); }
    }
    public static string MonitorDevice(nint mon)
    {
        var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        return GetMonitorInfo(mon, &mi) ? new string(mi.szDevice) : null;
    }
    public static RECT MonitorRect(nint mon, bool work = false)
    {
        var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        return GetMonitorInfo(mon, &mi) ? (work ? mi.rcWork : mi.rcMonitor) : default;
    }
    public static void SetClipboardText(string s)
    {
        if (!OpenClipboard(0)) return;
        try
        {
            EmptyClipboard();
            nuint bytes = (nuint)((s.Length + 1) * 2); nint mem = GlobalAlloc(GMEM_MOVEABLE, bytes);
            char* p = (char*)GlobalLock(mem); fixed (char* src = s) Buffer.MemoryCopy(src, p, (long)bytes, s.Length * 2); p[s.Length] = '\0'; GlobalUnlock(mem);
            SetClipboardData(CF_UNICODETEXT, mem);
        }
        finally { CloseClipboard(); }
    }
    /// <summary>
    /// Copies text that shouldn't be remembered (a one-time code): marked so
    /// clipboard watchers, Isola's own included, skip it, and kept out of
    /// Windows' clipboard history and cloud clipboard.
    /// </summary>
    public static void SetClipboardSecret(string s)
    {
        if (!OpenClipboard(0)) return;
        try
        {
            EmptyClipboard();
            nuint bytes = (nuint)((s.Length + 1) * 2); nint mem = GlobalAlloc(GMEM_MOVEABLE, bytes);
            char* p = (char*)GlobalLock(mem); fixed (char* src = s) Buffer.MemoryCopy(src, p, (long)bytes, s.Length * 2); p[s.Length] = '\0'; GlobalUnlock(mem);
            SetClipboardData(CF_UNICODETEXT, mem);
            foreach (var name in new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
            {
                nint flag = GlobalAlloc(GMEM_MOVEABLE, 4);
                *(uint*)GlobalLock(flag) = 0; GlobalUnlock(flag);   // 0: no
                SetClipboardData(RegisterClipboardFormatW(name), flag);
            }
        }
        finally { CloseClipboard(); }
    }
    public static string GetClipboardText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !OpenClipboard(0)) return null;
        try { nint h = GetClipboardData(CF_UNICODETEXT); if (h == 0) return null; char* p = (char*)GlobalLock(h); try { return p == null ? null : new string(p); } finally { GlobalUnlock(h); } }
        finally { CloseClipboard(); }
    }
    public static bool ShellExecute(string file, string args = null, string verb = null, string dir = null, int show = SW_SHOWNORMAL, uint mask = 0)
    {
        fixed (char* f = file) fixed (char* a = args) fixed (char* v = verb) fixed (char* d = dir)
        {
            var sei = new SHELLEXECUTEINFOW { cbSize = sizeof(SHELLEXECUTEINFOW), lpFile = f, lpParameters = a, lpVerb = v, lpDirectory = d, nShow = show, fMask = mask | SEE_MASK_NOASYNC };
            return ShellExecuteExW(&sei);
        }
    }
}
