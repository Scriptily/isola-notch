using System.Runtime.InteropServices;
using Isola.Island;
using Isola.Native;
using Isola.Render;
using Isola.Services;
using Isola.Shell;
using Vortice.Mathematics;
using Monitor = Isola.Shell.Monitor;
using Box = Isola.Render.Box;
using FontWeight = Vortice.DirectWrite.FontWeight;

namespace Isola.App;

/// <summary>
/// Settings, as a normal Windows 11 window (acrylic backdrop under our own
/// tint and grain, dark title bar) drawn by the island's own renderer. Every change applies immediately and is
/// saved a moment later; there is no Apply button to forget.
/// </summary>
public sealed unsafe partial class SettingsWindow
{
    readonly App app;
    readonly Hub svc;
    readonly Func<Config> getCfg;
    readonly Action<Config> apply;
    nint hwnd;
    Surface surface;
    readonly Canvas canvas = new();
    readonly Ux ux = new();
    float scale = 1;
    int section;
    bool dirty = true;
    double lastFrame;
    long saveTimer;
    Config work;
    readonly Dictionary<string, TextBox> fields = new();
    string focusField;
    float scrollY, scrollTarget, contentH;
    string haFilter = "";
    string testResult = "";
    // A pressed button shows its spinner for at least a beat, even when the
    // work is done in 30 ms: otherwise it flashes, or shows nothing and looks
    // like the press missed.
    const double MinSpin = 0.45;
    double testAt = -1, checkAt = -1;
    bool Spinning(double since) => Ui.Clock.Now - since < MinSpin;
    static readonly Dictionary<nint, SettingsWindow> byHwnd = new();
    public bool Alive => hwnd != 0 && Win32.IsWindow(hwnd);

    internal static readonly (string name, char glyph)[] Sections =
    {
        ("Appearance", ''), ("Behavior", ''), ("Live Activities", ''), ("Notifications & Sounds", Glyph.Bell),
        ("Widgets", ''), ("Downloads", Glyph.Download), ("Search & Keys", Glyph.Keyboard), ("Home Assistant", Glyph.Home), ("Spotify", Glyph.Music), ("About & Logs", ''), ("Feedback", Glyph.Message),
    };

    public SettingsWindow(App a, Hub s, Func<Config> cfg, Action<Config> onApply)
    {
        app = a; svc = s; getCfg = cfg; apply = onApply;
        ux.RequestFrame = () => dirty = true;
        ux.Now = () => Ui.Clock.Now;
        if (App.ZoneDir != null)
        {
            ux.DumpFile = Path.Combine(App.ZoneDir, "settings.txt");
            ux.Origin = () => { var p = new POINT(0, 0); Win32.ClientToScreen(hwnd, ref p); return (p.X, p.Y, scale); };
        }
    }

    [LibraryImport("dwmapi.dll")] private static partial int DwmExtendFrameIntoClientArea(nint h, int* margins);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    public void Show(Monitor m)
    {
        work = getCfg().Clone();
        if (!Alive) Create(m);
        Win32.ShowWindow(hwnd, Win32.SW_SHOWNORMAL);
        Win32.SetForegroundWindow(hwnd);
        dirty = true;
        if (section == 6) _ = svc.Ha?.RefreshAsync(true);
    }

    void Create(Monitor m)
    {
        nint inst = Win32.GetModuleHandleW(null);
        fixed (char* name = "Isola.Settings")
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &Proc, hInstance = inst, lpszClassName = name, hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW) };
            string icoFile = Path.Combine(AppContext.BaseDirectory, "assets", "island.ico");
            uint dpi = (uint)(m?.Dpi ?? 96);
            wc.hIcon = Win32.LoadImageW(0, icoFile, 1, GetSystemMetricsForDpi(11, dpi), GetSystemMetricsForDpi(12, dpi), 0x10);
            wc.hIconSm = Win32.LoadImageW(0, icoFile, 1, GetSystemMetricsForDpi(49, dpi), GetSystemMetricsForDpi(50, dpi), 0x10);
            Win32.RegisterClassExW(&wc);
        }
        var mon = m?.Bounds ?? new RECT(0, 0, 1920, 1080);
        scale = (m?.Dpi ?? 96) / 96f;
        int w = (int)(920 * scale), h = (int)(680 * scale);
        hwnd = Win32.CreateWindowExW(Win32.WS_EX_NOREDIRECTIONBITMAP | Win32.WS_EX_APPWINDOW, "Isola.Settings", "Isola Settings",
            Win32.WS_OVERLAPPEDWINDOW, mon.Left + (mon.Width - w) / 2, mon.Top + (mon.Height - h) / 2, w, h, 0, 0, inst, 0);
        byHwnd[hwnd] = this;
        // Windows 11's acrylic: the blurred, tinted desktop shows through
        // (DWMSBT_TRANSIENTWINDOW), with the dark title bar to match.
        int dark = 1, acrylic = 3, round = 2;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, 4);
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, &acrylic, 4);
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, &round, 4);
        // the island icon in the title bar and on the taskbar, at both sizes
        string ico = Path.Combine(AppContext.BaseDirectory, "assets", "island.ico");
        // per-monitor sizes: a 150% panel's taskbar wants 48 px, another screen something else
        uint mdpi = (uint)(m?.Dpi ?? 96);
        nint big = Win32.LoadImageW(0, ico, 1, GetSystemMetricsForDpi(11, mdpi), GetSystemMetricsForDpi(12, mdpi), 0x10);
        nint small = Win32.LoadImageW(0, ico, 1, GetSystemMetricsForDpi(49, mdpi), GetSystemMetricsForDpi(50, mdpi), 0x10);
        if (big != 0) Win32.SendMessageW(hwnd, 0x80 /*WM_SETICON*/, 1, big);
        if (small != 0) Win32.SendMessageW(hwnd, 0x80, 0, small);
        int* margins = stackalloc int[4] { -1, -1, -1, -1 };
        DwmExtendFrameIntoClientArea(hwnd, margins);
        Win32.GetClientRect(hwnd, out var rc);
        surface = new Surface(hwnd, rc.Width, rc.Height) { Dpi = 96 * scale };
        Log.I("settings", "settings window opened");
    }

    public void Close() { if (Alive) Win32.DestroyWindow(hwnd); hwnd = 0; }
    /// <summary>Something shown here changed elsewhere (a service's status): draw again.</summary>
    public void Invalidate() => dirty = true;

    /// <summary>Open at a section by its sidebar name (the island's "Connect Spotify" lands on Spotify).</summary>
    public void GoTo(string name)
    {
        int i = Array.FindIndex(Sections, s => s.name == name);
        if (i < 0) return;
        section = i; scrollTarget = 0; focusField = null; helpOpen = null; dirty = true;
        // testing: open scrolled down, for a screenshot without touching the wheel
        if (float.TryParse(Environment.GetEnvironmentVariable("ISOLA_SETTINGS_SCROLL"), out float sy)) scrollTarget = sy;
        helpOpen = Environment.GetEnvironmentVariable("ISOLA_SETTINGS_HELP") is { Length: > 0 } hk ? hk : helpOpen;
        if (name == "Home Assistant") _ = svc.Ha?.RefreshAsync(true);
    }

    /// <summary>When the main loop must wake for this window. A hidden window never needs to.</summary>
    public double NextWake()
    {
        if (!Alive || !Win32.IsWindowVisible(hwnd)) return double.PositiveInfinity;
        if (dirty || ux.Step(0)) return 0.002;   // poll the vsync waitable while animating
        if (loaderShown) return Math.Max(0, lastFrame + 1 / 30.0 - Ui.Clock.Now);   // a spinner or skeleton turns at 30 fps
        double tip = ux.TipDue - Ui.Clock.Now;   // a tooltip waiting to show draws without the mouse moving
        return Math.Max(0, Math.Min(tip, focusField != null ? lastFrame + 0.5 - Ui.Clock.Now : double.PositiveInfinity));
    }

    double lastStep;
    bool loaderShown;

    /// <summary>
    /// Draw a frame when something changed, paced by the swap chain's
    /// frame-latency waitable (so frames land on vsync), stepping the springs
    /// by the real time since the last drawn frame. Stepping on every loop pass
    /// while only sometimes drawing made scrolling jump and stutter.
    /// </summary>
    public void Tick()
    {
        if (!Alive || !Win32.IsWindowVisible(hwnd)) return;
        double now = Ui.Clock.Now;
        bool blink = focusField != null && now - lastFrame > 0.5;
        bool loader = loaderShown && now - lastFrame >= 1 / 30.0;
        bool tipDue = now >= ux.TipDue;   // a tooltip that's due needs a frame though nothing else changed
        if (!dirty && !ux.Step(0) && !blink && !loader && !tipDue) { lastStep = now; return; }
        if (surface.FrameWaitHandle != 0 && Win32.WaitForSingleObject(surface.FrameWaitHandle, 0) != 0) return;   // not this vsync yet
        ux.Step(Math.Clamp(now - lastStep, 0, 0.05));
        lastStep = now;
        lastFrame = now;
        dirty = false;
        var ctx = surface.Begin();
        canvas.Begin(ctx);
        try { Render(); } catch (Exception ex) { Log.E("settings", "render failed", ex); }
        loaderShown = canvas.LoaderDrawn;
        surface.End();
    }

    [UnmanagedCallersOnly]
    static nint Proc(nint h, uint m, nint w, nint l)
    {
        try { if (byHwnd.TryGetValue(h, out var s)) { var r = s.Handle(m, w, l); if (r.HasValue) return r.Value; } }
        catch (Exception ex) { Log.E("settings", $"message 0x{m:X}", ex); }
        return Win32.DefWindowProcW(h, m, w, l);
    }

    nint? Handle(uint m, nint w, nint l)
    {
        float X() => Win32.LoWord(l) / scale;
        float Y() => Win32.HiWord(l) / scale;
        switch (m)
        {
            case Win32.WM_SIZE: if (surface != null) { surface.Resize(Win32.LoWord(l), Win32.HiWord(l)); dirty = true; } return 0;
            case Win32.WM_DPICHANGED:
            {
                scale = Win32.HiWord(w) / 96f; surface.Dpi = 96 * scale;
                var r = *(RECT*)l; Win32.SetWindowPos(hwnd, 0, r.Left, r.Top, r.Width, r.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                dirty = true; return 0;
            }
            case Win32.WM_ERASEBKGND: return 1;
            case Win32.WM_MOUSEMOVE:
            {
                var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = Win32.TME_LEAVE, hwndTrack = hwnd };
                Win32.TrackMouseEvent(ref tme);
                ux.Move(X(), Y()); dirty = true;
                Win32.SetCursor(Win32.LoadCursorW(0, ux.CursorAt(X(), Y()) switch { CursorKind.Hand => Win32.IDC_HAND, CursorKind.IBeam => Win32.IDC_IBEAM, _ => Win32.IDC_ARROW }));
                return 0;
            }
            case Win32.WM_MOUSELEAVE: ux.Leave(); dirty = true; return 0;
            case Win32.WM_LBUTTONDOWN: Win32.SetCapture(hwnd); if (!ux.Down(X(), Y())) { focusField = null; } dirty = true; return 0;
            case Win32.WM_LBUTTONUP: Win32.ReleaseCapture(); ux.Up(X(), Y()); dirty = true; return 0;
            case Win32.WM_POINTERDOWN:
            case Win32.WM_POINTERUPDATE:
            case Win32.WM_POINTERUP:
                return TouchPointer(m, w) ? 0 : null;
            case Win32.WM_MOUSEWHEEL:
            {
                helpOpen = null;
                POINT p = new(Win32.LoWord(l), Win32.HiWord(l)); Win32.ScreenToClient(hwnd, ref p);
                if (!ux.WheelAt(p.X / scale, p.Y / scale, Win32.HiWord(w), false))
                {
                    Win32.GetClientRect(hwnd, out var rc);
                    // precision touchpads send small deltas; mouse wheels 120 per notch
                    scrollTarget = Math.Clamp(scrollTarget - Win32.HiWord(w) / 120f * 110, 0, Math.Max(0, contentH - rc.Height / scale));
                }
                dirty = true; return 0;
            }
            case Win32.WM_KEYDOWN:
                if (focusField != null && fields.TryGetValue(focusField, out var tb)) { tb.Key((int)w, Win32.GetKeyState(0x11) < 0, Win32.GetKeyState(0x10) < 0, Ui.Clock.Now); dirty = true; return 0; }
                if (w == 0x1B) { if (helpOpen != null) { helpOpen = null; dirty = true; } else Win32.ShowWindow(hwnd, Win32.SW_HIDE); return 0; }
                return null;
            case Win32.WM_CHAR:
                if (focusField != null && fields.TryGetValue(focusField, out var tb2)) { tb2.Char((char)w, Ui.Clock.Now); dirty = true; return 0; }
                return null;
            case Win32.WM_CLOSE:
                Win32.ShowWindow(hwnd, Win32.SW_HIDE);   // keep it around: reopening is instant
                FlushSave();
                { var c = getCfg(); Sounds.Keep(Sounds.All.Select(k => (k, App.ToneFor(c, k)))); }   // only the chosen sounds stay built
                return 0;
            case Win32.WM_GETMINMAXINFO:
                ((POINT*)l)[3] = new POINT((int)(760 * scale), (int)(520 * scale));
                return 0;
        }
        return null;
    }

    // ---- a finger: taps press, sideways on a slider drags it, up and down scrolls the page ----
    uint touchId; float touchX0, touchY0; int touchKind;   // 0 undecided, 1 dragging a control, 2 scrolling
    readonly PagePan pan = new();

    /// <summary>
    /// Touch and pen, handled here instead of as Windows' stand-in mouse: dragged as a mouse, a finger
    /// selected nothing and the page couldn't be scrolled, and the hover highlight stayed where it lifted.
    /// </summary>
    bool TouchPointer(uint m, nint w)
    {
        uint id = (uint)(w & 0xFFFF);
        if (!Win32.GetPointerInfo(id, out var pi) || pi.pointerType == Win32.PT_MOUSE) return false;
        var p = pi.ptPixelLocation; Win32.ScreenToClient(hwnd, ref p);
        float x = p.X / scale, y = p.Y / scale;
        double now = Ui.Clock.Now;
        switch (m)
        {
            case Win32.WM_POINTERDOWN:
                touchId = id; touchX0 = x; touchY0 = y; touchKind = 0;
                ux.Slop = TouchInput.Slop; ux.Move(x, y);
                if (!ux.Down(x, y)) focusField = null;
                if (ux.Dragging != null) touchKind = 1;   // a slider took it
                break;
            case Win32.WM_POINTERUPDATE:
                if (id != touchId) break;
                if (touchKind == 0 && Math.Abs(x - touchX0) + Math.Abs(y - touchY0) > TouchInput.Decide)
                {
                    bool vertical = Math.Abs(y - touchY0) > Math.Abs(x - touchX0);
                    if (!vertical && ux.PressedZone?.Drag != null) touchKind = 1;
                    else { touchKind = 2; ux.CancelPress(); helpOpen = null; pan.Begin(scrollY, MaxScroll(), touchY0, now); }
                }
                if (touchKind == 1) ux.Move(x, y);
                else if (touchKind == 2) { scrollTarget = pan.Move(y, now); ux.Snap("set:scroll", scrollTarget); }
                break;
            case Win32.WM_POINTERUP:
                if (id != touchId) break;
                if (touchKind == 2) { var (to, v) = pan.End(now); scrollTarget = to; ux.SetVelocity("set:scroll", v); }
                else ux.Up(x, y);
                ux.Slop = 0; ux.Leave(); touchId = 0;
                break;
        }
        dirty = true;
        return true;
    }

    float MaxScroll() { Win32.GetClientRect(hwnd, out var rc); return Math.Max(0, contentH - rc.Height / scale); }

    void Changed()
    {
        work.Normalize();
        apply(work.Clone());
        dirty = true;
    }

    void FlushSave() { Ui.Cancel(ref saveTimer); }

    // ======================================================================
    // drawing
    // ======================================================================

    void Render()
    {
        var c = canvas;
        ux.BeginFrame(c);
        Win32.GetClientRect(hwnd, out var rc);
        float W = rc.Width / scale, H = rc.Height / scale;
        var accent = Col.Hex(0x7aa2ff);
        // Fluent acrylic is the blurred backdrop under a strong tint and a fine
        // grain. DWM supplies only the blur, so the tint and grain are ours:
        // the desktop now just colors the window faintly, and when the window is
        // inactive (DWM shows plain gray underneath) it looks almost the same.
        // Windows' acrylic is the blurred desktop behind the window; a light
        // tint keeps text readable while it shows through. (The heavy tint and
        // a drawn grain over it read as a flat, grainy gray: the glass never showed.)
        // Maximized, the glass spans the whole screen: whatever is behind (the
        // wallpaper, a page full of thumbnails) showed through as a wash of color
        // across all of it, where the windowed glass mostly sits over dark app
        // windows. A maximized window gets a stronger tint, as Windows' own do.
        float max = ux.Anim("set:max", Win32.IsZoomed(hwnd) ? 1 : 0, SpringSpec.Smooth);
        c.Fill(new Box(0, 0, 220, H), Col.Hex(0x202026, 0.42f + 0.30f * max));
        c.Fill(new Box(220, 0, W - 220, H), Col.Hex(0x18181c, 0.58f + 0.26f * max));
        // sidebar
        c.Text("Isola", new Box(24, 22, 190, 30), 18, Col.White, FontWeight.SemiBold);
        c.Text("Settings", new Box(24, 46, 190, 20), 12, Col.White.A(0.5f), FontWeight.Medium);
        float sy = 86;
        float selY = ux.Anim("set:sel", 86 + section * 40, SpringSpec.Snappy);
        c.Fill(new Box(14, selY, 194, 34), Col.White.A(0.1f), 8);
        c.Fill(new Box(14, selY + 9, 3, 16), accent, 1.5f);
        for (int i = 0; i < Sections.Length; i++)
        {
            int idx = i;
            var b = new Box(14, sy, 194, 34);
            ux.Add("sec:" + i, b, () => { GoTo(Sections[idx].name); ux.Forget("set:scroll"); }, 8);
            float h = ux.HoverT("sec:" + i);
            if (i != section && h > 0.01f) c.Fill(b, Col.White.A(0.05f * h), 8);
            c.Icon(Sections[i].glyph, b.X + 22, b.CY, 14, Col.White.A(i == section ? 1 : 0.7f));
            c.Text(Sections[i].name, new Box(b.X + 42, b.Y, b.W - 50, b.H), 13, Col.White.A(i == section ? 1 : 0.75f), i == section ? FontWeight.SemiBold : FontWeight.Medium);
            sy += 40;
        }
        // Quit, at the foot of the sidebar: Isola stops for now, and starts again
        // from Start, or with Windows if it's set to. Red only on approach.
        var qb = new Box(14, H - 84, 194, 34);
        ux.Add("quit", qb, () => app.Quit("Settings -> Quit Isola"), 8).Tooltip = "Stops Isola until you open it again or Windows next starts it";
        float qh = ux.HoverT("quit");
        if (qh > 0.01f) c.Fill(qb, Col.Hex(0xff453a, 0.14f * qh), 8);
        var qcol = Col.Mix(Col.White.A(0.7f), Col.Hex(0xff6961), qh);
        c.Icon(Glyph.Power, qb.X + 22, qb.CY, 14, qcol);
        c.Text("Quit Isola", new Box(qb.X + 42, qb.Y, 150, qb.H), 13, qcol, FontWeight.Medium);
        c.Text("Changes apply instantly", new Box(24, H - 34, 190, 20), 11, Col.White.A(0.35f), FontWeight.Medium);

        // content: the scroll position follows its target on a spring
        scrollY = ux.Anim("set:scroll", scrollTarget, new SpringSpec(0.24, 1.0), 0.05);
        var area = new Box(220 + 32, 0, W - 220 - 64, H);
        // the page fades where there's more to scroll, rather than slicing a card in half
        // its scroll bar sits in the page's right margin (32 px), so the cards needn't narrow for it
        Paint.ScrollBar(c, ux, "settings", new Box(220, 0, W - 220 - 6, H), scrollY, scrollTarget, contentH, 1, v => { scrollTarget = v; dirty = true; }, "set:scroll");
        c.FadeScroll(new Box(220, 0, W - 220, H), scrollY, contentH - H, 28);
        float y = 24 - scrollY;
        c.Text(Sections[section].name, new Box(area.X, y, area.W, 36), 26, Col.White, FontWeight.SemiBold);
        y += 52;
        var L = new Layout(this, c, area.X, y, area.W);
        switch (Sections[section].name)
        {
            case "Appearance": Appearance(L); break;
            case "Behavior": Behavior(L); break;
            case "Live Activities": Activities(L); break;
            case "Notifications & Sounds": Notifications(L); break;
            case "Widgets": Widgets(L); break;
            case "Downloads": DownloadsSection(L); break;
            case "Search & Keys": Keys(L); break;
            case "Home Assistant": HomeAssistantSection(L); break;
            case "Spotify": SpotifySection(L); break;
            case "About & Logs": About(L); break;
            case "Feedback": FeedbackSection(L); break;
        }
        // the same 32 below the last card as at the sides. The fade above used
        // the height measured last frame: if this frame measured another (a page
        // just opened), draw once more so the edges match this page
        float measured = L.Y + scrollY + 32;
        if (Math.Abs(measured - contentH) > 0.5f) dirty = true;
        contentH = measured;
        c.Unfade();
        HelpPopover(W, H);
        TooltipLayer(W, H);
        ux.EndFrame();
    }

    /// <summary>Row-by-row layout helper for settings cards.</summary>
    sealed class Layout
    {
        readonly SettingsWindow s; readonly Canvas c; public float X, Y, W;
        bool inCard; float cardTop;
        public Layout(SettingsWindow s, Canvas c, float x, float y, float w) { this.s = s; this.c = c; X = x; Y = y; W = w; }
        public void Header(string t) { End(); c.Text(t.ToUpperInvariant(), new Box(X + 4, Y, W, 18), 11, Col.White.A(0.5f), FontWeight.Bold); Y += 24; }
        void Begin() { if (!inCard) { inCard = true; cardTop = Y; } }
        public void End() { if (inCard) { inCard = false; Y += 14; } }
        public Box Row(float h = 52)
        {
            Begin();
            var b = new Box(X, Y, W, h);
            c.Fill(b, Col.White.A(0.055f), 8);
            Y += h + 3;
            return b;
        }
        public void Note(string t, float h = 18) { c.Text(t, new Box(X + 4, Y, W - 8, h), 11.5f, Col.White.A(0.48f), FontWeight.Normal, HAlign.Left, VAlign.Top, true); Y += h + 6; }
    }

    // Every settings row: the title and (if any) a subtitle that wraps to two
    // lines, centered as a group against the row, stopping `reserve` short of
    // the row's control on the right. Rows used to put a subtitle on one line
    // that was simply cut off, set the two-line group a little high, and
    // reserve the same 300 for every control, so a slider crowded its subtitle.
    const float TitleH = 19, SubLineH = 16;
    Box LabeledRow(Layout L, string id, string title, string sub, float reserve, float minH, string help = null)
    {
        float textW = L.W - 20 - reserve - 16;
        float subH = sub == null ? 0 : Math.Min(2 * SubLineH, canvas.Measure(sub, 11.5f, FontWeight.Normal, textW, true).h);
        float groupH = TitleH + (sub == null ? 0 : 2 + subH);
        var b = L.Row(Math.Max(minH, groupH + 26));
        float top = b.Y + (b.H - groupH) / 2;
        canvas.Text(title, new Box(b.X + 20, top, textW, TitleH), 13.5f, Col.White.A(0.95f), FontWeight.Medium);
        if (sub != null) canvas.Text(sub, new Box(b.X + 20, top + TitleH + 2, textW, subH + 2), 11.5f, Col.White.A(0.5f), FontWeight.Normal, HAlign.Left, VAlign.Top, true);
        if (help != null) HelpDot(id, b.X + 20 + Math.Min(textW - 10, canvas.Measure(title, 13.5f, FontWeight.Medium).w) + 14, top + TitleH / 2, title, help);
        return b;
    }

    // ---- "?" help: the details that don't fit as a footnote, in a small popover ----
    string helpOpen, helpTitle, helpBody;
    Box helpAnchor;

    void HelpDot(string key, float cx, float cy, string title, string body)
    {
        var b = Box.Centered(cx, cy, 18, 18);
        bool open = helpOpen == key;
        ux.Add("help:" + key, b.Inset(-4), () => { helpOpen = open ? null : key; dirty = true; }, 11);
        float h = Math.Max(ux.HoverT("help:" + key), open ? 1 : 0);
        canvas.Circle(cx, cy, 8, Col.White.A(0.06f + 0.1f * h));
        canvas.Ring(cx, cy, 8, Col.White.A(0.3f + 0.35f * h), 1.2f);
        canvas.Text("?", b, 10.5f, Col.White.A(0.65f + 0.35f * h), FontWeight.SemiBold, HAlign.Center);
        if (open) { helpAnchor = b; helpTitle = title; helpBody = body; }
    }

    // ---- tooltips: the first waits 400 ms, the next ones along a row appear at once (see Ux.Tip) ----
    readonly string forcedTip = Environment.GetEnvironmentVariable("ISOLA_SETTINGS_TIP");   // screenshots

    void TooltipLayer(float W, float H)
    {
        string text; Box anchor; float appear;
        if (forcedTip != null && ux.Find(forcedTip) is { Tooltip: { Length: > 0 } } f) (text, anchor, appear) = (f.Tooltip, f.Box, 1);
        // the first one after a pause comes in from 97%, the rest along a row just appear; leaving, it fades out
        else if (ux.TipFrame(Ui.Clock.Now) is { } t) (text, anchor, appear) = t;
        else return;
        float w = canvas.Measure(text, 12, FontWeight.Medium).w + 18, h = 26;
        float x = Math.Clamp(anchor.CX - w / 2, 8, W - w - 8), y = anchor.Bottom + 8;
        if (y + h > H - 8) y = anchor.Y - h - 8;
        var b = new Box(x, y, w, h).Scale(0.97f + 0.03f * appear);
        // a tooltip floats above the help popover, so it's a step lighter (depth in the dark comes from lightness)
        canvas.Fill(b, Col.Hex(0x3a3a42, 0.98f * appear), 8);
        canvas.Stroke(b, Col.White.A(0.12f * appear), 8, 1);
        canvas.Text(text, b, 12, Col.White.A(0.92f * appear), FontWeight.Medium, HAlign.Center);
    }

    string helpShown;   // the popover still on screen: the open one, or the one fading out

    void HelpPopover(float W, float H)
    {
        if (helpOpen != null) helpShown = helpOpen;
        if (helpShown == null || helpBody == null) return;
        bool closing = helpOpen == null;
        // anywhere else closes it, as a popover does
        if (!closing) ux.Add("help:dismiss", new Box(0, 0, W, H), () => { helpOpen = null; dirty = true; }, 0, CursorKind.Arrow);
        const float cw = 340, pad = 16;
        float bodyH = canvas.Measure(helpBody, 12, FontWeight.Normal, cw - 2 * pad, true).h;
        float ch = pad + 20 + 6 + bodyH + pad;
        float x = Math.Clamp(helpAnchor.CX - 28, 230, W - cw - 16);
        float y = helpAnchor.Bottom + 8;
        if (y + ch > H - 12) y = helpAnchor.Y - 8 - ch;          // no room below: above
        // closing, it shrinks back to 97% and fades, quicker than it came (it vanished at once)
        float appear = ux.AnimFrom("help:in:" + helpShown, 0, closing ? 0 : 1, closing ? SpringSpec.Snappy : SpringSpec.Content);
        if (closing && appear < 0.02f) { ux.Forget("help:in:" + helpShown); helpShown = null; return; }
        var card = new Box(x, y, cw, ch).Scale(0.97f + 0.03f * appear);   // in from 97%, not from nothing
        canvas.DropShadow(18, Col.Black.A(0.45f * appear), 8, () => canvas.Fill(card, Col.Black, 12));
        canvas.Fill(card, Col.Hex(0x2a2a30, 0.98f * appear), 12);
        canvas.Stroke(card, Col.White.A(0.1f * appear), 12, 1);
        if (!closing) ux.Add("help:card", card, () => { }, 12, CursorKind.Arrow);
        canvas.Text(helpTitle, new Box(card.X + pad, card.Y + pad, cw - 2 * pad, 20), 13.5f, Col.White.A(appear), FontWeight.SemiBold);
        canvas.Text(helpBody, new Box(card.X + pad, card.Y + pad + 26, cw - 2 * pad, bodyH + 2), 12, Col.White.A(0.78f * appear), FontWeight.Normal, HAlign.Left, VAlign.Top, true);
    }

    /// <summary>
    /// Rows that only apply when another setting allows them: drawn dimmed and not answering while they don't
    /// (fading between the two), so what has no effect doesn't look like it does. Use: using (When("id", on)) { rows }
    /// </summary>
    IDisposable When(string id, bool on) => new Gate(this, id, on);

    sealed class Gate : IDisposable
    {
        readonly SettingsWindow w; readonly bool on, layer; readonly int mark;
        public Gate(SettingsWindow w, string id, bool on)
        {
            this.w = w; this.on = on; mark = w.ux.ZoneCount;
            float a = w.ux.Anim("gate:" + id, on ? 1 : 0.38f, SpringSpec.Snappy);
            layer = a < 0.995f;
            if (layer) w.canvas.Push(null, a);
        }
        public void Dispose()
        {
            if (layer) w.canvas.Pop();
            if (!on) w.ux.Inert(mark);
        }
    }

    void Toggle(Layout L, string id, string title, string sub, bool value, Action<bool> set, string help = null)
    {
        var b = LabeledRow(L, id, title, sub, 90, 48, help);
        ux.Switch("t:" + id, b.Right - 60, b.CY, value, () => { set(!value); Changed(); }, Col.Hex(0x0a84ff));
    }

    void Seg(Layout L, string id, string title, string sub, string[] labels, string[] values, string value, Action<string> set, string help = null)
    {
        float w = Math.Max(240, labels.Sum(l => canvas.Measure(l, 12.5f, FontWeight.SemiBold).w + 28));
        var b = LabeledRow(L, id, title, sub, w + 44, 52, help);
        var seg = new Box(b.Right - w - 20, b.CY - 15, w, 30);
        canvas.Fill(seg, Col.White.A(0.08f), 15);
        float x = seg.X + 2;
        var widths = labels.Select(l => (canvas.Measure(l, 12.5f, FontWeight.SemiBold).w + 28) / labels.Sum(q => canvas.Measure(q, 12.5f, FontWeight.SemiBold).w + 28) * (seg.W - 4)).ToArray();
        int sel = Math.Max(0, Array.IndexOf(values, value));
        float sx = seg.X + 2 + widths.Take(sel).Sum();
        float ax = ux.Anim("seg:" + id + ":x", sx, SpringSpec.Snappy), aw = ux.Anim("seg:" + id + ":w", widths[sel], SpringSpec.Snappy);
        canvas.Fill(new Box(ax, seg.Y + 2, aw, seg.H - 4), Col.White.A(0.22f), 13);
        for (int i = 0; i < labels.Length; i++)
        {
            var cell = new Box(x, seg.Y, widths[i], seg.H);
            string v = values[i];
            ux.Add("seg:" + id + i, cell, () => { set(v); Changed(); }, 13);
            canvas.Text(labels[i], cell, 12.5f, Col.White.A(i == sel ? 1 : 0.7f), FontWeight.SemiBold, HAlign.Center);
            x += widths[i];
        }
    }

    // a sound choice: the segmented control, and picking (or re-picking) one plays it
    void Tone(Layout L, string id, string title, string kind, string value, Action<string> set)
    {
        var ch = Sounds.Choices(kind);
        Seg(L, id, title, null, ch.Select(c => c.label).ToArray(), ch.Select(c => c.id).ToArray(), Sounds.Valid(kind, value),
            v => { set(v); app.PreviewSound(kind, v); });
    }

    void Slider(Layout L, string id, string title, string sub, double value, double min, double max, Func<double, string> fmt, Action<double> set, double step = 0, string help = null)
    {
        // the track (220) and its value (54) with their gaps, plus breathing room before the text
        var b = LabeledRow(L, id, title, sub, 20 + 54 + 14 + 220 + 24, 52, help);
        var track = new Box(b.Right - 20 - 54 - 14 - 220, b.CY - 8, 220, 16);
        ux.Slider("s:" + id, track, (float)((value - min) / (max - min)), (v, fin) =>
        {
            double nv = min + v * (max - min);
            if (step > 0) nv = Math.Round(nv / step) * step;
            set(nv);
            if (fin) { Changed(); if (id == "sndvol") app.PreviewSound(Sounds.Notification, work.NotificationTone); }
            else { work.Normalize(); dirty = true; if (id is "scale" or "glow") apply(work.Clone()); }
        }, Col.Hex(0x0a84ff), 4, true);
        canvas.Text(fmt(value), new Box(b.Right - 20 - 54, b.Y, 54, b.H), 12.5f, Col.White.A(0.8f), FontWeight.SemiBold, HAlign.Right);
    }

    TextBox Field(string id, string value, bool secret = false)
    {
        if (!fields.TryGetValue(id, out var tb)) { tb = new TextBox(); tb.Set(value); fields[id] = tb; }
        else if (focusField != id && tb.Text != value) tb.Set(value);
        return tb;
    }

    void Text(Layout L, string id, string title, string sub, string value, Action<string> set, string placeholder = "", bool secret = false, float width = 300, string help = null)
    {
        var b = LabeledRow(L, id, title, sub, width + 40, 52, help);
        var tb = Field(id, value);
        tb.Placeholder = placeholder;
        tb.Submit = () => { set(tb.Text.Trim()); focusField = null; Changed(); };
        tb.Escape = () => { tb.Set(value); focusField = null; };
        tb.Changed = () => { if (focusField == id) { set(tb.Text.Trim()); Ui.Cancel(ref saveTimer); saveTimer = Ui.After(0.6, Changed); } };
        var fb = new Box(b.Right - width - 20, b.CY - 16, width, 32);
        bool focused = focusField == id;
        canvas.Fill(fb, Col.Black.A(focused ? 0.45f : 0.3f), 7);
        canvas.Stroke(fb, (focused ? Col.Hex(0x0a84ff) : Col.White.A(0.12f)), 7, focused ? 1.5f : 1);
        if (secret && !focused && tb.Text.Length > 0) canvas.Text(new string('•', Math.Min(24, tb.Text.Length)), fb.Inset(10, 0), 13, Col.White.A(0.8f));
        else tb.Draw(canvas, fb.Inset(10, 0), 13, focused, Ui.Clock.Now, Col.Hex(0x0a84ff));
        ux.Add("f:" + id, fb, () => { focusField = id; tb.Touch(Ui.Clock.Now); var (x, y) = (ux.MouseX, ux.MouseY); tb.ClickAt(tb.IndexAt(x, y), false); }, 7, CursorKind.IBeam);
    }

    void Button(Layout L, string id, string title, string sub, string label, Action click, bool busy = false, string help = null)
    {
        var b = LabeledRow(L, id, title, sub, 196, 52, help);
        var pill = new Box(b.Right - 20 - 154, b.CY - 15, 154, 30);
        // while its action is in flight the button shows a spinner instead of its label, and can't be pressed again
        if (busy) { canvas.Fill(pill, Col.White.A(0.08f), 15); Paint.Spinner(canvas, pill.CX, pill.CY, 14, Col.White.A(0.8f), Ui.Clock.Now); }
        else ux.Pill("b:" + id, pill, label, click, Col.White);
    }

    // ---- sections -----------------------------------------------------------

    void Appearance(Layout L)
    {
        L.Header("Style");
        Seg(L, "style", "Island style", "A floating pill, or a notch that melts into the top edge", new[] { "Island", "Notch" }, new[] { "pill", "notch" }, work.Style, v => work.Style = v);
        Seg(L, "face", "When idle, show", null, new[] { "Eyes", "Clock", "Nothing" }, new[] { "eyes", "clock", "none" }, work.IdleFace, v => work.IdleFace = v);
        using (When("eyes", work.IdleFace == "eyes"))
            Toggle(L, "eyes", "Eyes follow the pointer", (work.IdleFace == "eyes" ? "They look toward the cursor when it's near" : "When idle shows the eyes"), work.EyesFollowCursor, v => work.EyesFollowCursor = v);
        Slider(L, "scale", "Size", "Scales the whole island on top of Windows' display scaling", work.Scale, 0.8, 1.4, v => $"{v * 100:0}%", v => work.Scale = v, 0.05);
        L.Header("Glow");
        Toggle(L, "glow", "Colorful rim", "A soft gradient along the island's edge", work.Glow, v => work.Glow = v);
        using (When("glow1", work.Glow))
            Slider(L, "glow", "Glow strength", null, work.GlowIntensity, 0, 1, v => $"{v * 100:0}%", v => work.GlowIntensity = v, 0.05);
        using (When("glow2", work.Glow))
            Seg(L, "glowc", "Glow colors", "Auto follows the album art of what's playing", new[] { "Auto", "Aurora", "Sunset", "Ocean", "Mono" }, new[] { "auto", "aurora", "sunset", "ocean", "mono" }, work.GlowColors, v => work.GlowColors = v);
        using (When("glow3", work.Glow))
            Toggle(L, "glowdrift", "Glow drift", "Colors wander slowly along the edge; off, they hold still", work.GlowDrift, v => work.GlowDrift = v);
        L.Header("Motion");
        Toggle(L, "rm", "Reduce motion", "Springs settle without bouncing", work.ReduceMotion, v => work.ReduceMotion = v);
        Toggle(L, "hello", "Say hello at startup", null, work.HelloOnLaunch, v => work.HelloOnLaunch = v);
        L.End();
    }

    void Behavior(Layout L)
    {
        L.Header("Hover and click");
        Toggle(L, "hover", "Expand on hover", "Hovering shows controls; clicking opens the full island", work.HoverToExpand, v => work.HoverToExpand = v);
        using (When("hdelay", work.HoverToExpand))
            Slider(L, "hdelay", "Hover delay", null, work.HoverDelayMs, 0, 800, v => $"{v:0} ms", v => work.HoverDelayMs = (int)v, 10);
        Slider(L, "cdelay", "Collapse delay", "How long it waits after the pointer leaves", work.CollapseDelayMs, 0, 2000, v => $"{v:0} ms", v => work.CollapseDelayMs = (int)v, 50);
        L.Header("Auto-hide");
        Slider(L, "autohide", "Hide after inactivity", "0 keeps it visible", work.AutoHideMinutes, 0, 30, v => v <= 0 ? "Never" : $"{v:0.#} min", v => work.AutoHideMinutes = v, 0.5);
        using (When("stayplay", work.AutoHideMinutes > 0))
            Toggle(L, "stayplay", "Stay while media plays", (work.AutoHideMinutes > 0 ? "Music and videos keep it on screen" : "Hide after inactivity is set to Never"), work.StayWhilePlaying, v => work.StayWhilePlaying = v);
        Toggle(L, "ind", "Leave a small indicator when hidden", "Point at the top center of the screen to bring it back", work.ShowIndicatorWhenHidden, v => work.ShowIndicatorWhenHidden = v);
        Slider(L, "zone", "Reveal zone width", null, work.RevealZoneWidth, 120, 1200, v => $"{v:0}", v => work.RevealZoneWidth = (int)v, 20);
        L.Header("Screen");
        var choices = app.DisplayChoices();
        Seg(L, "showon", "Show the island on", "Built-in follows the laptop panel wherever it is; on a desktop it is the main screen", choices.Select(x => x.label).ToArray(), choices.Select(x => x.value).ToArray(),
            choices.Any(x => x.value == work.ShowOn) ? work.ShowOn : "laptop", v => work.ShowOn = v);
        L.Header("Fullscreen and stacking");
        Toggle(L, "fs", "Step aside for fullscreen apps", "Only on the island's own screen; fullscreen on another screen doesn't hide it", work.HideInFullscreen, v => work.HideInFullscreen = v);
        using (When("fsalert", work.HideInFullscreen))
            Toggle(L, "fsalert", "Still show alerts over fullscreen", null, work.AlertsOverFullscreen, v => work.AlertsOverFullscreen = v);
        Toggle(L, "top", "Stay on top of other always-on-top windows", null, work.KeepOnTop, v => work.KeepOnTop = v);
        Toggle(L, "start", "Start with Windows", null, work.StartWithWindows, v => work.StartWithWindows = v);
        L.End();
    }

    void Activities(Layout L)
    {
        L.Header("Media");
        Toggle(L, "media", "Now Playing", "Spotify, YouTube in Chrome, Plex - anything in Windows' media controls", work.Media, v => work.Media = v);
        using (When("wave", work.Media))
            Toggle(L, "wave", "Live waveform", "Bars that move with the actual sound", work.MediaWaveform, v => work.MediaWaveform = v);
        using (When("track", work.Media))
            Toggle(L, "track", "Peek out when the track changes", "Even while the island is hidden", work.ShowOnTrackChange, v => work.ShowOnTrackChange = v);
        L.Header("Devices");
        Toggle(L, "bt", "Bluetooth alerts", "Devices connecting and disconnecting, and the radio turning on or off", work.BluetoothAlerts, v => work.BluetoothAlerts = v);
        Toggle(L, "pods", "AirPods detail", "Left, right and case battery, in-ear and lid", work.AirPods, v => work.AirPods = v);
        using (When("magic", work.AirPods))
            Toggle(L, "magic", "Read live AirPods data from MagicPods", svc.AirPods?.MagicPodsFound == true ? "MagicPods found" : "MagicPods not found - using Bluetooth broadcasts", work.UseMagicPods, v => work.UseMagicPods = v);
        Toggle(L, "disp", "Display alerts", "When a screen connects or disconnects", work.DisplayAlerts, v => work.DisplayAlerts = v);
        L.Header("System");
        Toggle(L, "bat", "Battery alerts", "Charger connected, unplugged, low and full", work.BatteryAlerts, v => work.BatteryAlerts = v);
        using (When("low", work.BatteryAlerts))
            Slider(L, "low", "Low battery at", null, work.LowBatteryPercent, 5, 50, v => $"{v:0}%", v => work.LowBatteryPercent = (int)v, 1);
        Toggle(L, "vol", "Volume HUD", null, work.VolumeHud, v => work.VolumeHud = v);
        Toggle(L, "caps", "Caps Lock HUD", null, work.CapsLockHud, v => work.CapsLockHud = v);
        Toggle(L, "lay", "Keyboard language HUD", null, work.KeyboardLayoutHud, v => work.KeyboardLayoutHud = v);
        L.End();
    }

    void Notifications(Layout L)
    {
        L.Header("Windows notifications");
        Toggle(L, "notif", "Show notifications in the island", "Including iPhone messages through Phone Link", work.Notifications, v => work.Notifications = v);
        using (When("prev", work.Notifications))
            Toggle(L, "prev", "Show message previews", null, work.NotificationPreviews, v => work.NotificationPreviews = v);
        using (When("winbanners", work.Notifications))
        if (Packaging.IsStore)
            Button(L, "winbanners", "Windows' own popups", "Turn off their banners so each notification shows once", "Open settings",
                () => Win32.ShellExecute("ms-settings:notifications"));
        else
            Toggle(L, "winbanners", "Hide Windows' own popups and chime", "Changes each app's Windows notification setting while the island shows them, and puts it back when it stops", work.HideWindowsBanners, v => work.HideWindowsBanners = v);
        Slider(L, "alertsec", "Keep alerts on screen for", null, work.AlertSeconds, 2, 15, v => $"{v:0} s", v => work.AlertSeconds = (int)v, 1);
        using (When("muted", work.Notifications))
            Text(L, "muted", "Don't show these apps", "Comma separated, e.g. Discord, Teams", string.Join(", ", work.NotificationMutedApps), v => work.NotificationMutedApps = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), "App names");
        L.End();
        L.Note("Status: " + (svc.Notifications?.Status ?? "Off"));
        L.Header("Calls");
        Toggle(L, "calls", "Phone calls", "Incoming calls from Phone Link, with answer and decline", work.Calls, v => work.Calls = v);
        L.End();
        L.Header("Privacy");
        Toggle(L, "devuse", "Show when the camera or microphone is used", "Names the app as it starts and stops. Apps that use them all day only show once in a while",
            work.DeviceUseNotices, v => work.DeviceUseNotices = v);
        L.End();
        L.Header("While you're focused");
        using (When("focussnd", work.Sounds))
            Seg(L, "focussnd", "Sounds while media plays", (work.Sounds ? "For notifications, calls and notices" : "Island sounds are off"),
            new[] { "Full", "Quieter", "Muted" }, new[] { "full", "quiet", "mute" }, work.SoundsWhilePlaying, v => work.SoundsWhilePlaying = v);
        using (When("focusduck", work.Sounds))
            Toggle(L, "focusduck", "Lower the media for sounds", (work.Sounds ? "The playing app dips while a chime or ringtone plays, then comes back up" : "Island sounds are off"),
            work.DuckMedia, v => work.DuckMedia = v);
        Toggle(L, "focushold", "Hold notifications until you're done", "While media plays or a game or video is fullscreen on any screen, they wait and show as one summary when it stops, or when you open the island",
            work.HoldWhileFocused, v => work.HoldWhileFocused = v);
        L.End();
        L.Note("Timers and low battery always come through. Held ones are in Recent right away.");
        L.Header("Sounds");
        Toggle(L, "snd", "Island sounds", Packaging.IsStore ? "Its own chimes and ringtone" : "Its own chimes and ringtone; Windows' chime is off while the island shows notifications", work.Sounds, v => work.Sounds = v);
        using (When("sndvol", work.Sounds))
            Slider(L, "sndvol", "Sound volume", "100% is the level they were tuned at; Windows' volume applies too", work.SoundLevel, 0.5, 1.5, v => $"{v * 100:0}%", v => work.SoundLevel = v, 0.05);
        using (When("sndnotif", work.Sounds))
            Toggle(L, "sndnotif", "Chime for notifications", null, work.NotificationSound, v => work.NotificationSound = v);
        using (When("tone:n", work.Sounds && work.NotificationSound))
            Tone(L, "tone:n", "Notification sound", Sounds.Notification, work.NotificationTone, v => work.NotificationTone = v);
        using (When("sndcall", work.Sounds && work.Calls))
            Toggle(L, "sndcall", "Ringtone for calls", null, work.CallRingtone, v => work.CallRingtone = v);
        using (When("tone:c", work.Sounds && work.Calls && work.CallRingtone))
            Tone(L, "tone:c", "Ringtone", Sounds.Call, work.Ringtone, v => work.Ringtone = v);
        using (When("tone:t", work.Sounds))
            Tone(L, "tone:t", "Timer sound", Sounds.Timer, work.TimerTone, v => work.TimerTone = v);
        using (When("snddev", work.Sounds))
            Toggle(L, "snddev", "Sounds for devices and the charger", "Connecting, disconnecting, plugging in, low battery", work.DeviceSounds, v => work.DeviceSounds = v);
        using (When("tone:d", work.Sounds && work.DeviceSounds))
            Tone(L, "tone:d", "Devices connecting", Sounds.Connect, work.DeviceTone, v => work.DeviceTone = v);
        using (When("tone:g", work.Sounds && work.DeviceSounds))
            Tone(L, "tone:g", "Charger plugged in", Sounds.Charge, work.ChargeTone, v => work.ChargeTone = v);
        using (When("tone:l", work.Sounds && work.DeviceSounds))
            Tone(L, "tone:l", "Low battery", Sounds.Low, work.LowTone, v => work.LowTone = v);
        using (When("sndnotice", work.Sounds))
            Toggle(L, "sndnotice", "Sounds for notices", "Fully charged, displays, sound output, updates and downloads", work.NoticeSound, v => work.NoticeSound = v);
        using (When("tone:i", work.Sounds && work.NoticeSound))
            Tone(L, "tone:i", "Notice sound", Sounds.Notice, work.NoticeTone, v => work.NoticeTone = v);
        L.End();
        L.Note("Picking a sound plays it; pick it again to hear it again.");
        Button(L, "preview", "Preview popups", "Shows one of each kind the island can show, in turn, with its sound", "Preview", () => app.PreviewPopups());
    }

    void Widgets(Layout L)
    {
        L.Header("Weather");
        Toggle(L, "wx", "Weather", null, work.Weather, v => work.Weather = v);
        using (When("wxsrc", work.Weather))
            Seg(L, "wxsrc", "Source", "Auto uses your Home Assistant weather when it's reachable", new[] { "Auto", "Home Assistant", "Open-Meteo" }, new[] { "auto", "homeassistant", "openmeteo" }, work.WeatherSource, v => work.WeatherSource = v);
        using (When("city", work.Weather && work.WeatherSource != "homeassistant"))
            Text(L, "city", "City (Open-Meteo)", "Used when Home Assistant isn't reachable", work.City, v => work.City = v, "e.g. New York");
        using (When("wxent", work.Weather && work.WeatherSource != "openmeteo"))
            Text(L, "wxent", "Weather entity (Home Assistant)", "Leave empty for the first weather.* entity", work.WeatherEntity, v => work.WeatherEntity = v, "weather.home");
        using (When("f", work.Weather))
            Toggle(L, "f", "Fahrenheit", null, work.Fahrenheit, v => work.Fahrenheit = v);
        Toggle(L, "24h", "24-hour clock", null, work.Use24Hour, v => work.Use24Hour = v);
        L.Header("Tools");
        Toggle(L, "notes", "Notes and to-dos", null, work.Notes, v => work.Notes = v);
        Toggle(L, "shelf", "File shelf", "Drop files on the island to park them", work.Shelf, v => work.Shelf = v);
        Toggle(L, "clip", "Clipboard history", "Kept in memory only, never written to disk", work.Clipboard, v => work.Clipboard = v);
        Toggle(L, "timer", "Timer", "A countdown of any length; also \"timer 10m\" in search", work.Timer, v => work.Timer = v);
        using (When("tsound", work.Timer))
            Toggle(L, "tsound", "Ring when a timer ends", "Windows' alarm, until you dismiss it (a minute at most)", work.TimerSound, v => work.TimerSound = v);
        L.End();
    }

    void Keys(Layout L)
    {
        L.Header("Search");
        Toggle(L, "search", "Search apps and files", svc.Search?.UsingEverything == true ? "Through Everything" : "Everything isn't running: the built-in index of apps and your folders", work.Search, v => work.Search = v);
        using (When("results", work.Search))
            Slider(L, "results", "Results to show", null, work.SearchResults, 3, 12, v => $"{v:0}", v => work.SearchResults = (int)v, 1);
        using (When("webans", work.Search))
            Toggle(L, "webans", "Web suggestions and answers", "Suggestions as you type, and definitions, translations and Wikipedia right in search. What you type is sent to Google, the dictionary and Wikipedia; file paths never are",
            work.WebAnswers, v => work.WebAnswers = v);
        L.Header("Shortcuts");
        Text(L, "hks", "Open search", "e.g. Alt+Space, Ctrl+Shift+Space", work.SearchHotkey, v => { if (Hotkey.TryParse(v, out _, out _)) work.SearchHotkey = v; }, "Alt+Space", false, 200);
        Text(L, "hkt", "Show / hide the island", null, work.ToggleHotkey, v => { if (Hotkey.TryParse(v, out _, out _)) work.ToggleHotkey = v; }, "Ctrl+Alt+I", false, 200);
        Toggle(L, "release", "Release shortcuts while a game or video is fullscreen", "So the shortcut keys reach the game, on any screen", work.ReleaseHotkeysInFullscreen, v => work.ReleaseHotkeysInFullscreen = v);
        L.End();
        if (!string.IsNullOrEmpty(app.HotkeyProblem)) L.Note(app.HotkeyProblem, 34);
    }

    void HomeAssistantSection(Layout L)
    {
        var ha = svc.Ha;
        L.Header("Connection");
        Text(L, "haurl", "Home Assistant URL", HomeAssistant.InsecureRemote(work.HaBaseUrl) ? "Not encrypted: use an https:// address when connecting over the internet" : null, work.HaBaseUrl, v => work.HaBaseUrl = v.TrimEnd('/'), "http://homeassistant.local:8123", false, 340,
            help: "The address you open Home Assistant at, such as http://homeassistant.local:8123. A remote address (Home Assistant Cloud or your own domain) works too, and keeps working when you're away from home.");
        Text(L, "hatok", "Access token", "A long-lived token from your Home Assistant profile", work.HaToken, v => work.HaToken = v, "Paste token", true, 340,
            help: "In Home Assistant, open your profile (your name at the bottom left), then the Security tab. Under Long-lived access tokens, press Create token, name it Isola, and copy it. Home Assistant shows it only once, so paste it here right away. The token stays on this computer.");
        Button(L, "hatest", "Test connection", testResult.Length > 0 ? testResult : ha?.StatusText, "Test", () => { testResult = "Testing…"; testAt = Ui.Clock.Now; Test(); }, testResult == "Testing…" || Spinning(testAt));
        L.Header($"Entities on the island ({work.HaEntities.Count})");
        if (ha == null) return;
        // selected first, removable
        foreach (var id in work.HaEntities.ToList())
        {
            var b = L.Row(40);
            ha.Entities.TryGetValue(id, out var e);
            var e2 = e ?? ha.All.FirstOrDefault(x => x.Id == id);
            canvas.Text(e2?.Name ?? id, new Box(b.X + 20, b.Y, b.W * 0.45f - 30, b.H), 13, Col.White, FontWeight.Medium);
            canvas.Text(id, new Box(b.X + b.W * 0.45f, b.Y, b.W * 0.55f - 130, b.H), 11.5f, Col.White.A(0.45f));
            int idx = work.HaEntities.IndexOf(id);
            ux.IconButton("haup:" + id, b.Right - 102, b.CY, 28, Glyph.Up, () => { if (idx > 0) { work.HaEntities.RemoveAt(idx); work.HaEntities.Insert(idx - 1, id); Changed(); } }, null, 11, idx > 0);
            ux.IconButton("hadn:" + id, b.Right - 66, b.CY, 28, Glyph.Down, () => { if (idx < work.HaEntities.Count - 1) { work.HaEntities.RemoveAt(idx); work.HaEntities.Insert(idx + 1, id); Changed(); } }, null, 11, idx < work.HaEntities.Count - 1);
            ux.IconButton("harm:" + id, b.Right - 30, b.CY, 28, Glyph.Close, () => { work.HaEntities.Remove(id); Changed(); }, Col.Hex(0xff453a), 10);
        }
        L.End();
        L.Header("Add entities");
        Text(L, "hafilter", "Filter", "Lights, switches, climate, fans, scenes, scripts, automations, buttons, selects, numbers, sensors…", haFilter, v => haFilter = v, "type to filter", false, 260);
        if (ha.All.Count == 0 && ha.Status is HaStatus.Online or HaStatus.Connecting)
        {
            // the entity list is on its way: rows in its shape
            for (int k = 0; k < 5; k++)
            {
                var r = L.Row(38);
                Paint.Skeleton(canvas, new Box(r.X + 20, r.CY - 5, r.W * (0.22f + 0.05f * (k % 3)), 10), 5, Ui.Clock.Now);
                Paint.Skeleton(canvas, new Box(r.X + r.W * 0.45f, r.CY - 4, r.W * 0.25f, 8), 4, Ui.Clock.Now, 0.7f);
            }
            L.End();
            return;
        }
        if (ha.All.Count == 0) { L.Note("Connect to Home Assistant to list your entities. (" + ha.StatusText + ")"); return; }
        var useful = new HashSet<string> { "light", "switch", "climate", "fan", "scene", "script", "automation", "button", "input_button", "input_boolean", "media_player", "cover", "lock", "humidifier", "select", "input_select", "number", "input_number", "sensor", "binary_sensor", "vacuum" };
        var list = ha.All.Where(e => useful.Contains(e.Domain) && !work.HaEntities.Contains(e.Id) &&
            (haFilter.Length == 0 || e.Name.Contains(haFilter, StringComparison.OrdinalIgnoreCase) || e.Id.Contains(haFilter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => DomainOrder(e.Domain)).ThenBy(e => e.Name).Take(120).ToList();
        string lastDom = null;
        foreach (var e in list)
        {
            if (e.Domain != lastDom) { L.End(); canvas.Text(e.Domain.Replace('_', ' '), new Box(L.X + 4, L.Y, 300, 18), 11, Col.White.A(0.4f), FontWeight.Bold); L.Y += 22; lastDom = e.Domain; }
            var b = L.Row(38);
            canvas.Text(e.Name, new Box(b.X + 20, b.Y, b.W * 0.42f, b.H), 12.5f, Col.White.A(0.9f), FontWeight.Medium);
            // the id stops where the state begins (a long id ran into it)
            float idX = b.X + b.W * 0.45f, stX = b.Right - 170;
            canvas.Text(e.Id, new Box(idX, b.Y, stX - idX - 10, b.H), 11, Col.White.A(0.4f));
            canvas.Text(EntityState(e.State), new Box(stX, b.Y, 110, b.H), 11, Col.White.A(0.5f), FontWeight.Medium, HAlign.Right);
            string id = e.Id;
            ux.IconButton("haadd:" + id, b.Right - 30, b.CY, 28, Glyph.Add, () => { work.HaEntities.Add(id); Changed(); _ = svc.Ha.RefreshAsync(); }, Col.Hex(0x34c759), 11);
        }
        L.End();
    }

    /// <summary>
    /// Spotify: connect once, then the volume slider inside the Spotify app shows
    /// under Now Playing. The steps are spelled out because Spotify's side (a
    /// developer app of your own) is the part nobody remembers how to do.
    /// </summary>
    void SpotifySection(Layout L)
    {
        var sp = svc.Spotify;
        if (sp == null) { L.Note("Spotify isn't available in this build."); return; }
        L.Header("Account");
        string sub = sp.Connected
            ? (sp.AccountName.Length > 0 ? "Signed in as " + sp.AccountName : "Signed in") + (sp.ReauthBy is DateTime by ? $" · sign in again by {by:MMM d, yyyy}" : "")
            : sp.Status;
        Button(L, "spconnect", sp.Connected ? "Connected" : sp.Expired ? "Sign in again" : "Connect Spotify", sub, sp.Connected ? "Disconnect" : "Connect",
            () => { if (sp.Connected) sp.Disconnect(); else { apply(work.Clone()); _ = sp.ConnectAsync(); } }, sp.Connecting,
            help: "Spotify asks everyone to sign in again six months after they connect. Two weeks before that, a Renew button shows up here, and it usually takes one click. Spotify also only lets these apps work while the account that owns them has Premium.");
        if (sp.Connected)
        {
            if (sp.Status != "Connected") L.Note(sp.Status);
            Button(L, "sprenew", "Renew the sign-in", "Starts a new six months, usually without asking for your password", "Renew",
                () => { apply(work.Clone()); _ = sp.ConnectAsync(); }, sp.Connecting);
        }
        Toggle(L, "spvol", "Volume under Now Playing", "Spotify's own volume, not the Windows mixer's", work.SpotifyVolume, v => work.SpotifyVolume = v);

        L.Header("Set up once");
        Step(L, 1, "Open the Spotify developer dashboard and sign in, then press Create app.", "Open", () => Win32.ShellExecute("https://developer.spotify.com/dashboard"));
        Step(L, 2, "Give it any name. Under Redirect URIs, paste this address and press Add. Tick Web API, then save.", Ui.Clock.Now - copiedAt < 2 ? "Copied" : "Copy address",
            () => { Win32.SetClipboardText(SpotifyService.RedirectUri); copiedAt = Ui.Clock.Now; });
        Step(L, 3, "Open your app's Settings, copy its Client ID, and paste it here. No client secret is needed.", null, null);
        Text(L, "spid", "Client ID", null, work.SpotifyClientId, v => work.SpotifyClientId = v, "Paste Client ID", false, 300,
            help: "Spotify needs a small developer app of your own so Isola can read what's playing and change Spotify's volume. It's free, takes about a minute, and you only do it once. The Client ID is on the app's page under Settings.");
        Step(L, 4, "Press Connect at the top. Your browser opens Spotify; agree, and you're done.", null, null);
    }
    double copiedAt = -10;

    // a numbered setup step, with an optional button for what the step needs
    void Step(Layout L, int n, string text, string button, Action click)
    {
        float reserve = button == null ? 0 : 170;
        float textW = L.W - 64 - reserve - 16;
        float th = canvas.Measure(text, 12.5f, FontWeight.Normal, textW, true).h;
        var b = L.Row(Math.Max(48, th + 24));
        canvas.Circle(b.X + 30, b.CY, 11, Col.White.A(0.1f));
        canvas.Text(n.ToString(), Box.Centered(b.X + 30, b.CY, 22, 22), 12, Col.White.A(0.85f), FontWeight.SemiBold, HAlign.Center);
        canvas.Text(text, new Box(b.X + 52, b.CY - th / 2, textW, th + 2), 12.5f, Col.White.A(0.85f), FontWeight.Normal, HAlign.Left, VAlign.Top, true);
        if (button != null) ux.Pill("step:" + n, new Box(b.Right - 20 - 150, b.CY - 15, 150, 30), button, click, Col.White);
    }

    /// <summary>A scene's or script's state is when it last ran, as an ISO timestamp: show that as a short local date and time.</summary>
    internal static string EntityState(string state) =>
        state != null && state.Length >= 19 && state[4] == '-' && state[10] == 'T' &&
        DateTime.TryParse(state, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("d MMM, HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : state;

    static int DomainOrder(string d) => d switch { "light" => 0, "scene" => 1, "script" => 2, "climate" => 3, "fan" => 4, "switch" => 5, "input_boolean" => 6, "humidifier" => 7, "media_player" => 8, "automation" => 9, _ => 10 };

    void Test()
    {
        apply(work.Clone());
        svc.Ha.RefreshAsync(true).ContinueWith(_ => Ui.Post(() => { testResult = svc.Ha.StatusText; dirty = true; }));
    }

    // ---- feedback ---------------------------------------------------------------
    bool fbStatusAsked;

    /// <summary>
    /// Feedback is paused: the form, greyed out and blurred behind a notice
    /// that says why, with a way to check again. Nothing on it can be used.
    /// </summary>
    void FeedbackPaused(Layout L, Feedback fb)
    {
        L.Header("Send feedback");
        var area = new Box(L.X, L.Y, L.W, 390);
        // a still picture of the form, out of focus
        canvas.Blurred(7, () =>
        {
            float y = area.Y;
            foreach (var (h, label) in new[] { (52f, "Type"), (82f, "Subject"), (162f, "Description"), (52f, "Attach logs for diagnostics") })
            {
                var r = new Box(area.X, y, area.W, h);
                canvas.Fill(r, Col.White.A(0.055f), 8);
                canvas.Text(label, new Box(r.X + 20, r.Y + 14, 300, 20), 13.5f, Col.White.A(0.9f), FontWeight.Medium);
                if (h > 60) canvas.Fill(new Box(r.X + 20, r.Y + 40, r.W - 40, h - 54), Col.White.A(0.06f), 10);
                y += h + 3;
            }
        }, 0.45f);
        // the notice, on top
        float cw = Math.Min(440, area.W - 48);
        var card = Box.Centered(area.CX, area.CY, cw, 190);
        canvas.Fill(card, Col.Hex(0x2a2a31, 0.97f), 18);
        canvas.Stroke(card, Col.White.A(0.1f), 18);
        canvas.Fill(Box.Centered(card.CX, card.Y + 38, 40, 40), Col.Hex(0xff9f0a, 0.16f), 12);
        canvas.Icon(Glyph.Pause, card.CX, card.Y + 38, 17, Col.Hex(0xffb340));
        canvas.Text("Feedback is paused", new Box(card.X + 20, card.Y + 66, card.W - 40, 22), 15, Col.White, FontWeight.SemiBold, HAlign.Center);
        canvas.Text("Isola's developer has paused feedback for a while, so nothing can be sent right now. Please try again later.", new Box(card.X + 28, card.Y + 92, card.W - 56, 40), 12.5f, Col.White.A(0.65f), FontWeight.Normal, HAlign.Center, VAlign.Top, true);
        var again = Box.Centered(card.CX, card.Bottom - 30, 140, 34);
        bool checking = Spinning(fbSpinAt);
        ux.Add("fb:recheck", again, () => { fbSpinAt = Ui.Clock.Now; dirty = true; fb.CheckPausedAsync(Feedback.Endpoint, true).ContinueWith(_ => Ui.Post(() => dirty = true)); }, 17);
        canvas.Fill(again, Col.White.A(0.1f + 0.08f * ux.HoverT("fb:recheck")), 17);
        if (checking) Paint.Spinner(canvas, again.CX, again.CY, 14, Col.White, Ui.Clock.Now);
        else canvas.Text("Check again", again, 13, Col.White, FontWeight.SemiBold, HAlign.Center, VAlign.Middle);
        L.Y += area.H + 14;
    }

    internal const string BugHint = "What happened, and what did you expect instead?\nSteps to reproduce:\n1. Open …\n2. Click …\n3. See …";
    FeedbackKind fbKind = FeedbackKind.Feature;
    bool fbLogs, fbBusy;
    string fbError;
    double fbSentAt = -10, fbCopiedAt = -10, fbSpinAt = -10;
    long fbSentReport;

    /// <summary>
    /// A subject, a description and, if the person wants, the log; sent to the
    /// relay that files it privately (Services/Feedback). The limits are shown
    /// before sending, not discovered after it.
    /// </summary>
    void FeedbackSection(Layout L)
    {
        var fb = svc.Feedback;
        if (fb == null) return;
        // is feedback open? asked as the page opens; until the answer, the form shows as usual
        if (!fbStatusAsked) { fbStatusAsked = true; fb.CheckPausedAsync(Feedback.Endpoint).ContinueWith(_ => Ui.Post(() => dirty = true)); }
        if (fb.Paused == true) { FeedbackPaused(L, fb); return; }
        var subject = Field("fbsub", fields.TryGetValue("fbsub", out var s0) ? s0.Text : "");
        var text = Field("fbtext", fields.TryGetValue("fbtext", out var t0) ? t0.Text : "");
        subject.MaxLength = Feedback.SubjectMax;
        text.Multiline = true; text.MaxLength = Feedback.TextMax;
        bool justSent = Ui.Clock.Now - fbSentAt < 6;

        L.Header("Send feedback");
        Seg(L, "fbkind", "Type", null, new[] { "Feature request", "Bug or glitch" }, new[] { "feature", "bug" },
            fbKind == FeedbackKind.Bug ? "bug" : "feature", v => fbKind = v == "bug" ? FeedbackKind.Bug : FeedbackKind.Feature);
        // label above a full-width field: these hold sentences, not settings
        StackedField(L, "fbsub", "Subject", subject, fbKind == FeedbackKind.Bug ? "What went wrong, in a few words" : "What would you like Isola to do?", 32);
        StackedField(L, "fbtext", "Description", text,
            // a bug report is only as useful as its steps: the hint shows the shape of one
            fbKind == FeedbackKind.Bug ? BugHint : "Tell us how you'd use it and why it would help.", 132,
            $"{text.Text.Length:N0} / {Feedback.TextMax:N0}");
        Toggle(L, "fblogs", "Attach logs for diagnostics", "Isola's log, with your name, folders, addresses and tokens removed", fbLogs, v => fbLogs = v);
        var idRow = LabeledRow(L, "fbid", "Your feedback ID", "A one-way number made from this computer, so we can tell your reports apart", 290, 52);
        canvas.Text(Feedback.Handle(fb.UserId), new Box(idRow.Right - 290, idRow.Y, 210, idRow.H), 12.5f, Col.White.A(0.9f), FontWeight.SemiBold, HAlign.Right);
        bool copied = Ui.Clock.Now - fbCopiedAt < 1.6;
        float ct = ux.Anim("fb:copied", copied ? 1 : 0, SpringSpec.Snappy);
        var copyBox = Box.Centered(idRow.Right - 44, idRow.CY, 40, 40);
        ux.Add("fb:copy", copyBox, () => { Win32.SetClipboardText(Feedback.Handle(fb.UserId)); fbCopiedAt = Ui.Clock.Now; dirty = true; }, 10).Tooltip = copied ? "Copied" : "Copy";
        float ch = ux.HoverT("fb:copy");
        canvas.Fill(Box.Centered(copyBox.CX, copyBox.CY, 30, 30), Col.White.A(0.06f + 0.08f * ch), 9);
        // the copy icon blurs into a check and back, as one shape
        canvas.Blurred(4 * ct, () => canvas.Icon(Glyph.Copy, copyBox.CX, copyBox.CY, 13, Col.White.A(0.8f)), 1 - ct);
        canvas.Blurred(4 * (1 - ct), () => canvas.Icon(Glyph.Check, copyBox.CX, copyBox.CY, 13, Col.Hex(0x34c759)), ct);
        L.End();

        // the send row: what's in the way (or how it went) on the left, the button on the right
        string problem = fbBusy || justSent ? null : fb.Problem(subject.Text, text.Text);
        bool waiting = fb.Wait > TimeSpan.Zero;
        bool untouched = subject.Text.Length == 0 && text.Text.Length == 0;
        var row = new Box(L.X, L.Y, L.W, 44);
        var btn = new Box(row.Right - 168, row.CY - 17, 168, 34);
        string note = justSent ? (fbSentReport > 0 ? $"Sent as report #{fbSentReport}. Thanks, it helps a lot." : "Sent. Thanks, it helps a lot.")
            : fbError ?? (Feedback.Endpoint.Length == 0 ? "Feedback isn't available in this build yet." : waiting || !untouched ? problem : null);
        if (note != null)
            canvas.Text(note, new Box(row.X + 4, row.Y, btn.X - row.X - 20, row.H), 12, justSent ? Col.Hex(0x34c759) : fbError != null ? Col.Hex(0xff6961) : Col.White.A(0.55f),
                FontWeight.Medium, HAlign.Left, VAlign.Middle, true);
        bool can = problem == null && !fbBusy && !justSent && Feedback.Endpoint.Length > 0;
        float sent = ux.Anim("fb:sent", justSent ? 1 : 0, SpringSpec.Snappy);
        canvas.Fill(btn, Col.Mix(Col.Hex(0x0a84ff), Col.Hex(0x34c759), sent).A(can || justSent ? 1 : 0.35f), 17);
        if (can) { ux.Add("fb:send", btn, SendFeedback, 17); canvas.Fill(btn, Col.White.A(0.12f * ux.HoverT("fb:send")), 17); }
        if (fbBusy || Spinning(fbSpinAt)) Paint.Spinner(canvas, btn.CX, btn.CY, 15, Col.White, Ui.Clock.Now);
        else
        {
            canvas.Blurred(4 * sent, () => canvas.Text("Send feedback", btn, 13, Col.White.A(can ? 1 : 0.7f), FontWeight.SemiBold, HAlign.Center), 1 - sent);
            canvas.Blurred(4 * (1 - sent), () =>
            {
                canvas.Icon(Glyph.Check, btn.CX - 22, btn.CY, 13, Col.White);
                canvas.Text("Sent", new Box(btn.CX - 10, btn.Y, 60, btn.H), 13, Col.White, FontWeight.SemiBold);
            }, sent);
        }
        L.Y += row.H + 8;
        L.Note("Feedback goes to the developer privately, never to the public issue tracker. One a day, so each one gets read.", 34);
    }

    // a sentence field: its label above, the field across the card
    void StackedField(Layout L, string id, string title, TextBox tb, string placeholder, float fieldH, string counter = null)
    {
        var b = L.Row(fieldH + 54);
        canvas.Text(title, new Box(b.X + 20, b.Y + 12, b.W - 40, TitleH), 13.5f, Col.White.A(0.95f), FontWeight.Medium);
        if (counter != null) canvas.Text(counter, new Box(b.X + 20, b.Y + 12, b.W - 40, TitleH), 11.5f, Col.White.A(0.4f), FontWeight.Medium, HAlign.Right);
        // inner radius = card radius (8) minus the inset, never below a hairline of rounding
        var fbx = new Box(b.X + 14, b.Y + 38, b.W - 28, fieldH);
        bool focused = focusField == id;
        tb.Placeholder = placeholder;
        tb.Escape = () => { focusField = null; dirty = true; };
        tb.Submit = () => { focusField = null; dirty = true; };
        tb.Changed = () => { fbError = null; dirty = true; };
        canvas.Fill(fbx, Col.Black.A(focused ? 0.45f : 0.3f), 6);
        canvas.Stroke(fbx, focused ? Col.Hex(0x0a84ff) : Col.White.A(0.12f), 6, focused ? 1.5f : 1);
        var inner = tb.Multiline ? fbx.Inset(10, 8) : fbx.Inset(10, 0);
        tb.Draw(canvas, inner, 13, focused, Ui.Clock.Now, Col.Hex(0x0a84ff));
        ux.Add("f:" + id, fbx, () => { focusField = id; tb.Touch(Ui.Clock.Now); tb.ClickAt(tb.IndexAt(ux.MouseX, ux.MouseY), false); }, 6, CursorKind.IBeam);
    }

    void SendFeedback()
    {
        var fb = svc.Feedback;
        string subject = fields["fbsub"].Text, text = fields["fbtext"].Text;
        fbBusy = true; fbError = null; fbSpinAt = Ui.Clock.Now; focusField = null; dirty = true;
        string logs = null;
        if (fbLogs)
        {
            Log.Flush();
            try
            {
                using var f = new FileStream(Paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(f);
                logs = Feedback.Redact(r.ReadToEnd(), Environment.UserName, Environment.MachineName,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), new[] { work.HaBaseUrl, work.HaToken, work.SpotifyClientId, work.City });
            }
            catch (Exception ex) { Log.W("feedback", "couldn't read the log: " + ex.Message); }
        }
        var kind = fbKind;
        string version = Updater.Short(typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0));
        string windows = Environment.OSVersion.Version.ToString();
        fb.SendAsync(Feedback.Endpoint, kind, subject, text, version, windows, Packaging.IsStore, logs).ContinueWith(t => Ui.Post(() =>
        {
            fbBusy = false;
            var res = t.IsFaulted ? new Feedback.Result("Couldn't send it. Try again later.", 0) : t.Result;
            if (res.Ok) { fbSentAt = Ui.Clock.Now; fbSentReport = res.Report; fields["fbsub"].Set(""); fields["fbtext"].Set(""); fbLogs = false; }
            else fbError = res.Error;
            dirty = true;
        }));
    }

    void DownloadsSection(Layout L)
    {
        var tools = svc.Tools; var dl = svc.Downloads;
        Toggle(L, "dlon", "Downloads and conversion", "Save a video or its audio from a link, and convert what's on your shelf", work.Downloads, v => work.Downloads = v);
        if (!work.Downloads)
        {
            L.Note("Off: the Shelf's link bar and the download commands are gone, and the tools aren't updated. They stay installed until you remove them below.");
            if (tools?.Ready == true) Button(L, "dlrm", "Remove the tools", $"Frees {tools.Bytes / 1_000_000} MB", "Remove", () => tools.Remove());
            return;
        }
        L.Note("Paste a link in the Shelf's bar, or type download and a link in search (audio and a link for just the sound). Hover a video or song on the shelf to convert it, or type convert in search.", 34);
        Seg(L, "dlto", "Save to", work.DownloadTo == "folder" ? work.DownloadFolder : work.DownloadTo == "shelf" ? "Kept on the shelf, in Isola's own folder" : "Your Downloads folder",
            new[] { "Downloads", "Shelf", "A folder" }, new[] { "downloads", "shelf", "folder" }, work.DownloadTo, v =>
            {
                if (v == "folder")
                {
                    string got = Native.FolderPicker.Pick(hwnd, "Save downloads to", string.IsNullOrEmpty(work.DownloadFolder) ? Services.KnownFolders.Downloads : work.DownloadFolder);
                    if (got == null) return;   // cancelled: stays where it was
                    work.DownloadFolder = got;
                }
                work.DownloadTo = v;
            });
        if (work.DownloadTo == "folder")
            Button(L, "dlfolder", "Folder", work.DownloadFolder, "Change", () =>
            {
                string got = Native.FolderPicker.Pick(hwnd, "Save downloads to", work.DownloadFolder);
                if (got != null) { work.DownloadFolder = got; Changed(); }
            });
        Seg(L, "dlq", "Video quality", "The most it downloads, as H.264 MP4 so it plays everywhere", new[] { "720p", "1080p", "Best" }, new[] { "720", "1080", "best" }, work.DownloadQuality, v => work.DownloadQuality = v);
        Seg(L, "dla", "Audio format", "What downloading the audio makes", new[] { "MP3", "M4A" }, new[] { "mp3", "m4a" }, work.DownloadAudioFormat, v => work.DownloadAudioFormat = v);
        if (tools != null)
        {
            string sub = tools.Busy ? tools.Status : tools.Ready ? $"{tools.Summary()} · {tools.Bytes / 1_000_000} MB · checked daily" : "Not installed yet: they're fetched (about 130 MB) the first time you download";
            Button(L, "dltools", "yt-dlp, FFmpeg and QuickJS", sub, tools.Ready ? "Update now" : "Get them now", () => { _ = tools.CheckAsync(true); dirty = true; }, tools.Busy,
                "Free programs from their own projects: yt-dlp downloads from YouTube and most other sites, FFmpeg converts and merges, QuickJS lets yt-dlp read YouTube's player. Isola fetches them from each project's GitHub releases, checks each file against the checksum GitHub publishes, and keeps them up to date. They're removed when you uninstall Isola.");
            if (tools.Busy && tools.Progress >= 0)
            {
                var bar = new Box(L.X + 4, L.Y - 6, L.W - 8, 3);
                canvas.Fill(bar, Col.White.A(0.08f), 1.5f);
                canvas.Fill(new Box(bar.X, bar.Y, bar.W * (float)tools.Progress, bar.H), Col.Hex(0x0a84ff), 1.5f);
                L.Y += 4;
            }
            if (tools.Ready && !tools.Busy) Button(L, "dlrm", "Remove the tools", $"Frees {tools.Bytes / 1_000_000} MB. They come back the next time you download", "Remove", () => { tools.Remove(); dirty = true; });
        }
        if (dl != null)
        {
            int n = dl.Jobs.Count(j => !j.Active);
            Button(L, "dlhist", "History", n == 0 ? "Nothing yet" : $"{n} downloads and conversions, in the Shelf's Downloads", "Clear", () => { dl.ClearHistory(); dirty = true; });
        }
        L.Note("Download only what you have the right to keep.");
    }

    void About(Layout L)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        L.Header("Isola");
        var b = L.Row(106);
        canvas.Text("Version " + Updater.Short(typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0)), new Box(b.X + 20, b.Y + 12, 400, 20), 13.5f, Col.White, FontWeight.SemiBold);
        AuthorLine(b.X + 20, b.Y + 34);
        canvas.Text($"GPU: {Gfx.AdapterName}  ·  Memory: {proc.WorkingSet64 / 1048576.0:0} MB working set, {proc.PrivateMemorySize64 / 1048576.0:0} MB private", new Box(b.X + 20, b.Y + 58, b.W - 36, 18), 11.5f, Col.White.A(0.55f));
        canvas.Text($"Data folder: {Paths.Home}", new Box(b.X + 20, b.Y + 78, b.W - 36, 18), 11.5f, Col.White.A(0.55f));
        Button(L, "sponsor", "Support Isola", "It's free and open source. A one-time or monthly sponsorship on GitHub keeps it going", "Sponsor", () => Win32.ShellExecute(SponsorPage));
        L.Header("Updates");
        if (Packaging.IsStore)
            Button(L, "updstore", "Updates", "The Microsoft Store keeps Isola up to date", "Open Store", () => Win32.ShellExecute("ms-windows-store://downloadsandupdates"));
        else
        {
            Toggle(L, "autoupd", "Update automatically", svc.Updates?.Status ?? "", work.AutoUpdate, v => work.AutoUpdate = v);
            Button(L, "updnow", "Check for updates", $"Running v{(svc.Updates != null ? Updater.Short(svc.Updates.Current) : "?")}", "Check now", () => { checkAt = Ui.Clock.Now; apply(work.Clone()); _ = svc.Updates?.CheckAsync(true); }, svc.Updates?.Busy == true || Spinning(checkAt));
        }
        L.Header("Logs");
        Toggle(L, "verbose", "Verbose log", "Everything the island decides, for diagnosing glitches later", work.VerboseLog, v => work.VerboseLog = v);
        Button(L, "log", "Log file", Paths.LogFile, "Open log", () => { Log.Flush(); Win32.ShellExecute(Paths.LogFile); });
        Button(L, "folder", "Data folder", "Settings, notes, shelf and logs", "Open folder", () => Win32.ShellExecute(Paths.Home));
        L.End();
    }

    internal const string Author = "Scriptily", RepoPage = "https://github.com/" + Updater.Repo,
        SponsorPage = "https://github.com/sponsors/Scriptily?frequency=one-time&sponsor=Scriptily";

    /// <summary>"Created by Scriptily", the name a link to the project's page.</summary>
    void AuthorLine(float x, float y)
    {
        const string lead = "Created by ";
        const float size = 12.5f;
        float lw = canvas.Measure(lead, size, FontWeight.Medium).w;
        float nw = canvas.Measure(Author, size, FontWeight.SemiBold).w;
        canvas.Text(lead, new Box(x, y, lw + 4, 18), size, Col.White.A(0.7f), FontWeight.Medium);
        var name = new Box(x + lw, y, nw, 18);
        // the clickable area reaches past the word, so it's easy to hit
        ux.Add("about:author", name.Inset(-8, -6), () => Win32.ShellExecute(RepoPage), 6).Tooltip = RepoPage.Replace("https://", "");
        float h = ux.HoverT("about:author");
        var color = Col.Mix(Col.Hex(0x0a84ff), Col.Hex(0x64b5ff), h);
        canvas.Text(Author, new Box(name.X, name.Y, nw + 4, name.H), size, color, FontWeight.SemiBold);
        // an underline grows in on hover, the way links say "this goes somewhere"
        if (h > 0.01f) canvas.Fill(new Box(name.X, name.Bottom - 1.5f, nw * h, 1.25f), color, 0.6f);
    }
}
