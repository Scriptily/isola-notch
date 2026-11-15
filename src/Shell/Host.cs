using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Isola.App;
using Isola.Island;
using Isola.Native;
using Isola.Render;

namespace Isola.Shell;

/// <summary>
/// The two windows that make the island, on the laptop panel only.
///
/// * The visual window draws everything through DirectComposition. It is
///   layered + transparent, so it never takes a click: the desktop under the
///   glow and shadow stays usable.
/// * The input window has no pixels at all; its window region is set every
///   frame to the island's current shape, so exactly the island - and nothing
///   around it - takes the mouse, touch, keyboard and dropped files.
///
/// (A window region would also clip what DirectComposition draws, which is why
/// these are two windows and not one. Probed on this machine before writing it.)
/// </summary>
public sealed unsafe partial class Host : IIslandHost
{
    public nint Visual { get; private set; }
    public nint Input { get; private set; }
    public Surface Surface { get; private set; }
    public Island.Island Island;
    public Monitor Monitor { get; private set; }
    public float Scale { get; private set; } = 1;
    /// <summary>The windows' top-left on screen, in pixels.</summary>
    public int Left { get; private set; }
    public int Top { get; private set; }
    public const float WinW = 900, WinH = 560;      // DIPs, before the user's island scale
    // The user's island size is folded into the DPI, so text and shapes scale
    // together and the island's own coordinate space never changes.
    public float WindowWidth => WinW;
    public float WindowHeight => WinH;
    bool shown;
    RECT lastRgn;
    bool frameRequested = true;
    public bool FrameRequested { get => frameRequested; set => frameRequested = value; }
    readonly DropTarget drop = new();
    readonly StrategyBasedComWrappers cw = new();
    nint dropPtr;
    public Action<uint, nint, nint> SystemMessage;    // everything the input window gets that is not input
    public Action<string> OpenSettingsRequested;
    public Action<int, int> ContextMenuRequested;
    nint arrow, hand, ibeam;
    bool tracking;
    bool keyboard;
    long origEx;
    // touch / pen
    uint pointerId; POINT pointerStart; double pointerT; bool pointerMoved;
    POINT mouseDown; bool mouseDownBg;

    static readonly Dictionary<nint, Host> byHwnd = new();

    public void Create()
    {
        nint inst = Win32.GetModuleHandleW(null);
        arrow = Win32.LoadCursorW(0, Win32.IDC_ARROW); hand = Win32.LoadCursorW(0, Win32.IDC_HAND); ibeam = Win32.LoadCursorW(0, Win32.IDC_IBEAM);
        Register("Isola.Visual", inst);
        Register("Isola.Input", inst);
        Visual = Win32.CreateWindowExW(Win32.WS_EX_NOREDIRECTIONBITMAP | Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
            "Isola.Visual", "Isola", Win32.WS_POPUP, 0, 0, 100, 100, 0, 0, inst, 0);
        Input = Win32.CreateWindowExW(Win32.WS_EX_NOREDIRECTIONBITMAP | Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
            "Isola.Input", "Isola input", Win32.WS_POPUP, 0, 0, 100, 100, 0, 0, inst, 0);
        if (Visual == 0 || Input == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()})");
        byHwnd[Visual] = this; byHwnd[Input] = this;
        SetLayeredWindowAttributes(Visual, 0, 255, 2);
        origEx = Win32.GetWindowLongPtr(Input, Win32.GWL_EXSTYLE);
        Surface = new Surface(Visual, 100, 100);

        drop.Enter = () => Island?.DragEnter();
        drop.Leave = () => Island?.DragLeave();
        drop.Dropped = files => Island?.Drop(files);
        nint unk = cw.GetOrCreateComInterfaceForObject(drop, CreateComInterfaceFlags.None);
        Marshal.QueryInterface(unk, typeof(IDropTarget).GUID, out dropPtr);
        Marshal.Release(unk);
        int hr = Win32.RegisterDragDrop(Input, dropPtr);
        if (hr < 0) Log.W("host", $"RegisterDragDrop failed 0x{hr:X8}: the shelf won't take drops");
        Log.I("host", $"windows created: visual 0x{Visual:X} input 0x{Input:X}");
    }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);

    static void Register(string cls, nint inst)
    {
        fixed (char* name = cls)
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &Proc, hInstance = inst, lpszClassName = name, hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW) };
            Win32.RegisterClassExW(&wc);
        }
    }

    /// <summary>Put the windows at the top center of the laptop panel.</summary>
    public void Place(Monitor m, double islandScale)
    {
        Monitor = m;
        if (m == null) { SetShown(false); return; }
        Scale = m.Dpi / 96f * (float)islandScale;
        int w = (int)Math.Ceiling(WindowWidth * Scale), h = (int)Math.Ceiling(WindowHeight * Scale);
        int x = m.Bounds.Left + (m.Bounds.Width - w) / 2, y = m.Bounds.Top;
        Left = x; Top = y;
        placing = true;
        Win32.SetWindowPos(Visual, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE);
        Win32.SetWindowPos(Input, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE);
        placing = false;
        Surface.Dpi = 96f * Scale;
        Surface.Resize(w, h);
        lastRgn = default;
        Log.I("host", $"placed on {m.GdiName} at ({x},{y}) {w}x{h}px, scale {Scale:0.##}");
        RequestFrame();
    }

    /// <summary>Bring both windows back to the top of the topmost band (another topmost window just came forward).</summary>
    public void Reassert()
    {
        if (!shown) return;
        Win32.SetWindowPos(Visual, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        Win32.SetWindowPos(Input, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    // ---- IIslandHost ---------------------------------------------------------
    public void RequestFrame() => frameRequested = true;

    public void SetShown(bool on)
    {
        if (on == shown) return;
        if (on && Monitor == null) return;
        shown = on;
        Win32.ShowWindow(Visual, on ? Win32.SW_SHOWNA : Win32.SW_HIDE);
        Win32.ShowWindow(Input, on ? Win32.SW_SHOWNA : Win32.SW_HIDE);
        if (on) Reassert();
        Log.D("host", on ? "shown" : "hidden (no composition work while off)");
        RequestFrame();
    }
    public bool Shown => shown;

    /// <summary>Whether the pointer is over the island's input area (in screen pixels, with a small margin).</summary>
    public bool PointerOverIsland(int margin = 8)
    {
        if (lastRgn.Width <= 0 || !Win32.GetCursorPos(out var p)) return false;
        int x = p.X - Left, y = p.Y - Top;
        return x >= lastRgn.Left - margin && x <= lastRgn.Right + margin && y >= lastRgn.Top - margin && y <= lastRgn.Bottom + margin;
    }

    public void SetHitArea(Box b, float radius)
    {
        RECT r = b.W <= 0 ? default : new RECT((int)Math.Floor(b.X * Scale), (int)Math.Floor(b.Y * Scale), (int)Math.Ceiling(b.Right * Scale), (int)Math.Ceiling(b.Bottom * Scale));
        int rad = (int)(radius * 2 * Scale);
        if (r.Equals(lastRgn) && lastRadius == rad) return;
        lastRgn = r; lastRadius = rad;
        nint rgn = r.Width <= 0 ? Win32.CreateRectRgn(0, 0, 0, 0) : Win32.CreateRoundRectRgn(r.Left, r.Top, r.Right + 1, r.Bottom + 1, rad, rad);
        Win32.SetWindowRgn(Input, rgn, false);   // the system owns rgn now
    }
    int lastRadius;

    public void TakeKeyboard(bool on)
    {
        keyboard = on;
        if (on)
        {
            // drop NOACTIVATE so the window can hold the keyboard, then take it
            var fg = Win32.GetForegroundWindow();
            if (fg != Input) prevForeground = fg;
            Win32.SetWindowLongPtr(Input, Win32.GWL_EXSTYLE, (nint)(origEx & ~(long)Win32.WS_EX_NOACTIVATE));
            Win32.AllowSetForegroundWindow(-1);
            bool ok = Win32.SetForegroundWindow(Input);
            Win32.SetFocus(Input);
            if (!ok) ForceForeground();
            Log.D("host", "keyboard taken" + (ok ? "" : " (forced)"));
        }
        else
        {
            Win32.SetWindowLongPtr(Input, Win32.GWL_EXSTYLE, (nint)origEx);
            // give focus back to whatever had it, so typing carries on where it was
            if (Win32.GetForegroundWindow() == Input && prevForeground != 0 && Win32.IsWindow(prevForeground)) Win32.SetForegroundWindow(prevForeground);
            prevForeground = 0;
        }
    }
    public nint prevForeground;

    /// <summary>
    /// Windows only lets the process that received the last input take the
    /// foreground. A hotkey counts, a click on the island counts; if neither
    /// does, a zero-length synthetic Alt tap makes us the last input source -
    /// the documented-behavior workaround every launcher uses.
    /// </summary>
    void ForceForeground()
    {
        Win32.keybd_event(0x12, 0, 0, 0); Win32.keybd_event(0x12, 0, 2, 0);
        Win32.SetForegroundWindow(Input);
        Win32.SetFocus(Input);
    }

    public void SetCursor(CursorKind k) => Win32.SetCursor(k switch { CursorKind.Hand => hand, CursorKind.IBeam => ibeam, _ => arrow });

    public (float x, float y, bool onScreen) Cursor()
    {
        if (Monitor == null) return (-1e4f, -1e4f, false);
        Win32.GetCursorPos(out var p);
        bool on = p.X >= Monitor.Bounds.Left && p.X < Monitor.Bounds.Right && p.Y >= Monitor.Bounds.Top && p.Y < Monitor.Bounds.Bottom;
        Win32.GetWindowRect(Visual, out var wr);
        return ((p.X - wr.Left) / Scale, (p.Y - wr.Top) / Scale, on);
    }

    public void OpenSettings(string section = null) => OpenSettingsRequested?.Invoke(section);

    public void ShowContextMenu(float x, float y)
    {
        Win32.GetCursorPos(out var p);
        ContextMenuRequested?.Invoke(p.X, p.Y);
    }

    public void StartFileDrag(IReadOnlyList<string> paths) => Ui.Post(() => DragOut.Files(paths));
    public void StartTextDrag(string text) => Ui.Post(() => DragOut.Text(text));

    // ---- window procedure ------------------------------------------------------
    [UnmanagedCallersOnly]
    static nint Proc(nint h, uint m, nint w, nint l)
    {
        try
        {
            if (byHwnd.TryGetValue(h, out var host)) { var r = host.Handle(h, m, w, l); if (r.HasValue) return r.Value; }
        }
        catch (Exception ex) { Log.E("host", $"message 0x{m:X} failed", ex); }
        return Win32.DefWindowProcW(h, m, w, l);
    }

    (float, float) Dip(nint l) => (Win32.LoWord(l) / Scale, Win32.HiWord(l) / Scale);

    bool placing;

    /// <summary>
    /// The island is pinned: nothing but <see cref="Place"/> may move or resize
    /// it (not Win+arrows, not a window manager, not a stray SetWindowPos), and
    /// it cannot be minimized or maximized. Showing, hiding and Z order still pass.
    /// </summary>
    nint? Pinned(uint m, nint w, nint l)
    {
        if (m == Win32.WM_WINDOWPOSCHANGING && !placing)
        {
            uint* flags = (uint*)(l + 32);   // WINDOWPOS.flags
            *flags |= Win32.SWP_NOMOVE | Win32.SWP_NOSIZE;
            return 0;
        }
        if (m == Win32.WM_SYSCOMMAND)
        {
            uint sc = (uint)w & 0xFFF0;
            if (sc is 0xF010 /*MOVE*/ or 0xF000 /*SIZE*/ or 0xF020 /*MINIMIZE*/ or 0xF030 /*MAXIMIZE*/ or 0xF120 /*RESTORE*/) return 0;
        }
        if (m == Win32.WM_NCLBUTTONDOWN) return 0;   // no caption to drag, and none emulated
        return null;
    }

    nint? Handle(nint h, uint m, nint w, nint l)
    {
        if (Pinned(m, w, l) is nint pinned) return pinned;
        if (h == Visual)
        {
            if (m == Win32.WM_NCHITTEST) return Win32.HTTRANSPARENT;
            return null;
        }
        var isl = Island;
        switch (m)
        {
            case Win32.WM_MOUSEACTIVATE:
                return keyboard ? null : Win32.MA_NOACTIVATE;
            case Win32.WM_SETCURSOR:
                return null;
            case Win32.WM_MOUSEMOVE:
            {
                if (!tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = Win32.TME_LEAVE, hwndTrack = Input };
                    Win32.TrackMouseEvent(ref tme); tracking = true;
                }
                var (x, y) = Dip(l);
                isl?.MouseMove(x, y);
                if (mouseDownBg)
                {
                    // press-and-drag on the island's body: it follows the pointer;
                    // released far enough down it opens search, up it hides
                    Win32.GetCursorPos(out POINT sp);   // screen space: the region moves while dragging
                    float ddx = (sp.X - mouseDownScreen.X) / Scale, ddy = (sp.Y - mouseDownScreen.Y) / Scale;
                    if (!dragging && Math.Abs(ddy) > 6) { dragging = true; isl?.Ux.CancelPress(); Log.D("host", $"drag started on the island body ({ddy:0} DIP)"); }
                    if (dragging)
                    {
                        double now = Ui.Clock.Now;
                        if (now > lastDragT) dragVel = dragVel * 0.6f + 0.4f * (float)((ddy - lastDragY) / (now - lastDragT));
                        lastDragT = now; lastDragY = ddy;
                        isl?.DragTo(ddx, ddy);
                    }
                }
                return 0;
            }
            case Win32.WM_MOUSELEAVE:
                tracking = false;
                isl?.MouseLeave();
                return 0;
            case Win32.WM_LBUTTONDOWN:
            {
                var (x, y) = Dip(l);
                Win32.SetCapture(Input);
                mouseDown = new POINT(Win32.LoWord(l), Win32.HiWord(l));
                Win32.GetCursorPos(out mouseDownScreen);
                mouseDownBg = isl != null && isl.Ux.HitTest(x, y) == null;
                dragging = false; dragVel = 0; lastDragY = 0; lastDragT = Ui.Clock.Now;
                swallowUp = false;
                isl?.MouseDown(x, y);
                return 0;
            }
            case Win32.WM_LBUTTONUP:
            {
                Win32.ReleaseCapture();
                var (x, y) = Dip(l);
                mouseDownBg = false;
                if (dragging)
                {
                    dragging = false;
                    Win32.GetCursorPos(out POINT up);
                    isl?.DragEnd((up.X - mouseDownScreen.X) / Scale, (up.Y - mouseDownScreen.Y) / Scale, dragVel);
                    return 0;
                }
                if (swallowUp) { swallowUp = false; isl?.Ux.Up(-1e4f, -1e4f); return 0; }
                isl?.MouseUp(x, y);
                return 0;
            }
            case Win32.WM_RBUTTONUP: { var (x, y) = Dip(l); isl?.RightClick(x, y); return 0; }
            case Win32.WM_MOUSEWHEEL:
            case Win32.WM_MOUSEHWHEEL:
            {
                POINT p = new(Win32.LoWord(l), Win32.HiWord(l)); Win32.ScreenToClient(Input, ref p);
                isl?.Wheel(p.X / Scale, p.Y / Scale, Win32.HiWord(w), m == Win32.WM_MOUSEHWHEEL);
                return 0;
            }
            case Win32.WM_POINTERDOWN:
            case Win32.WM_POINTERUPDATE:
            case Win32.WM_POINTERUP:
                return Pointer(m, w) ? 0 : null;
            case Win32.WM_KEYDOWN:
            case Win32.WM_SYSKEYDOWN:
            {
                bool ctrl = Win32.GetKeyState(0x11) < 0, shift = Win32.GetKeyState(0x10) < 0;
                if (isl != null && isl.KeyDown((int)w, ctrl, shift)) return 0;
                return null;
            }
            case Win32.WM_CHAR:
                if (isl != null && isl.CharIn((char)w)) return 0;
                return null;
            case Win32.WM_ACTIVATE:
                if ((w & 0xFFFF) == 0 && keyboard) { keyboard = false; Win32.SetWindowLongPtr(Input, Win32.GWL_EXSTYLE, (nint)origEx); isl?.KeyboardLost(); }
                return null;
            case Win32.WM_CAPTURECHANGED:
                return null;
            case Win32.WM_CLOSE:
                return 0;   // only Quit ends the island; stray WM_CLOSEs (e.g. from window managers) are ignored
            case Ui.WM_WAKE:
                return 0;
        }
        if (SystemMessage != null && m != Win32.WM_NCHITTEST)
        {
            SystemMessage(m, w, l);
            if (m is App.App.WM_QUIT_ISLAND or Win32.WM_HOTKEY or Win32.WM_COPYDATA or Win32.WM_POWERBROADCAST or Win32.WM_DISPLAYCHANGE or Win32.WM_INPUT or Win32.WM_CLIPBOARDUPDATE or Win32.WM_DPICHANGED) return m == Win32.WM_COPYDATA ? 1 : 0;
        }
        return null;
    }
    bool swallowUp, dragging;
    POINT mouseDownScreen; float dragVel, lastDragY; double lastDragT;

    bool Pointer(uint m, nint w)
    {
        uint id = (uint)(w & 0xFFFF);
        if (!Win32.GetPointerInfo(id, out var pi) || pi.pointerType == Win32.PT_MOUSE) return false;
        var p = pi.ptPixelLocation; Win32.ScreenToClient(Input, ref p);
        float x = p.X / Scale, y = p.Y / Scale;
        var isl = Island; if (isl == null) return true;
        double now = Ui.Clock.Now;
        switch (m)
        {
            case Win32.WM_POINTERDOWN:
                pointerId = id; pointerStart = p; pointerT = now; pointerMoved = false;
                isl.MouseMove(x, y);
                isl.MouseDown(x, y);
                break;
            case Win32.WM_POINTERUPDATE:
                if (id != pointerId) break;
                if (Math.Abs(p.X - pointerStart.X) + Math.Abs(p.Y - pointerStart.Y) > 12 * Scale) pointerMoved = true;
                if (isl.Ux.Dragging != null) isl.MouseMove(x, y);
                break;
            case Win32.WM_POINTERUP:
                if (id != pointerId) break;
                float dx = (p.X - pointerStart.X) / Scale, dy = (p.Y - pointerStart.Y) / Scale;
                if (pointerMoved && isl.Ux.Dragging == null) { isl.Ux.CancelPress(); isl.Ux.Up(-1e4f, -1e4f); isl.Swipe(dx, dy); }
                else isl.MouseUp(x, y);
                // a finger leaving is not a hover: collapse like the pointer left
                isl.MouseLeave();
                pointerId = 0;
                break;
        }
        return true;
    }
}
