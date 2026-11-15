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

    static readonly (string name, char glyph)[] Sections =
    {
        ("Appearance", ''), ("Behavior", ''), ("Live Activities", ''), ("Notifications", Glyph.Bell),
        ("Widgets", ''), ("Search & Keys", Glyph.Keyboard), ("Home Assistant", Glyph.Home), ("Spotify", Glyph.Music), ("About & Logs", ''),
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
        return focusField != null ? Math.Max(0, lastFrame + 0.5 - Ui.Clock.Now) : double.PositiveInfinity;
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
        if (!dirty && !ux.Step(0) && !blink && !loader) { lastStep = now; return; }
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
                return 0;
            case Win32.WM_GETMINMAXINFO:
                ((POINT*)l)[3] = new POINT((int)(760 * scale), (int)(520 * scale));
                return 0;
        }
        return null;
    }

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
            c.Text(Sections[i].name, new Box(b.X + 42, b.Y, 150, b.H), 13, Col.White.A(i == section ? 1 : 0.75f), i == section ? FontWeight.SemiBold : FontWeight.Medium);
            sy += 40;
        }
        c.Text("Changes apply instantly", new Box(24, H - 34, 190, 20), 11, Col.White.A(0.35f), FontWeight.Medium);

        // content: the scroll position follows its target on a spring
        scrollY = ux.Anim("set:scroll", scrollTarget, new SpringSpec(0.24, 1.0), 0.05);
        var area = new Box(220 + 32, 0, W - 220 - 64, H);
        c.ClipRect(new Box(220, 0, W - 220, H));
        float y = 24 - scrollY;
        c.Text(Sections[section].name, new Box(area.X, y, area.W, 36), 26, Col.White, FontWeight.SemiBold);
        y += 52;
        var L = new Layout(this, c, area.X, y, area.W);
        switch (section)
        {
            case 0: Appearance(L); break;
            case 1: Behavior(L); break;
            case 2: Activities(L); break;
            case 3: Notifications(L); break;
            case 4: Widgets(L); break;
            case 5: Keys(L); break;
            case 6: HomeAssistantSection(L); break;
            case 7: SpotifySection(L); break;
            case 8: About(L); break;
        }
        contentH = L.Y + scrollY + 32;   // the same 32 below the last card as at the sides
        c.Unclip();
        HelpPopover(W, H);
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

    void HelpPopover(float W, float H)
    {
        if (helpOpen == null || helpBody == null) return;
        // anywhere else closes it, as a popover does
        ux.Add("help:dismiss", new Box(0, 0, W, H), () => { helpOpen = null; dirty = true; }, 0, CursorKind.Arrow);
        const float cw = 340, pad = 16;
        float bodyH = canvas.Measure(helpBody, 12, FontWeight.Normal, cw - 2 * pad, true).h;
        float ch = pad + 20 + 6 + bodyH + pad;
        float x = Math.Clamp(helpAnchor.CX - 28, 230, W - cw - 16);
        float y = helpAnchor.Bottom + 8;
        if (y + ch > H - 12) y = helpAnchor.Y - 8 - ch;          // no room below: above
        float appear = ux.Anim("help:in:" + helpOpen, 1, SpringSpec.Snappy);
        var card = new Box(x, y + (1 - appear) * 6, cw, ch);
        canvas.DropShadow(18, Col.Black.A(0.45f * appear), 8, () => canvas.Fill(card, Col.Black, 12));
        canvas.Fill(card, Col.Hex(0x2a2a30, 0.98f * appear), 12);
        canvas.Stroke(card, Col.White.A(0.1f * appear), 12, 1);
        ux.Add("help:card", card, () => { }, 12, CursorKind.Arrow);
        canvas.Text(helpTitle, new Box(card.X + pad, card.Y + pad, cw - 2 * pad, 20), 13.5f, Col.White.A(appear), FontWeight.SemiBold);
        canvas.Text(helpBody, new Box(card.X + pad, card.Y + pad + 26, cw - 2 * pad, bodyH + 2), 12, Col.White.A(0.78f * appear), FontWeight.Normal, HAlign.Left, VAlign.Top, true);
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
            if (fin) Changed(); else { work.Normalize(); dirty = true; if (id is "scale" or "glow") apply(work.Clone()); }
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
        Toggle(L, "eyes", "Eyes follow the pointer", "They look toward the cursor when it's near", work.EyesFollowCursor, v => work.EyesFollowCursor = v);
        Slider(L, "scale", "Size", "Scales the whole island on top of Windows' display scaling", work.Scale, 0.8, 1.4, v => $"{v * 100:0}%", v => work.Scale = v, 0.05);
        L.Header("Glow");
        Toggle(L, "glow", "Colorful rim", "A soft gradient along the island's edge", work.Glow, v => work.Glow = v);
        Slider(L, "glow", "Glow strength", null, work.GlowIntensity, 0, 1, v => $"{v * 100:0}%", v => work.GlowIntensity = v, 0.05);
        Seg(L, "glowc", "Glow colors", "Auto follows the album art of what's playing", new[] { "Auto", "Aurora", "Sunset", "Ocean", "Mono" }, new[] { "auto", "aurora", "sunset", "ocean", "mono" }, work.GlowColors, v => work.GlowColors = v);
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
        Slider(L, "hdelay", "Hover delay", null, work.HoverDelayMs, 0, 800, v => $"{v:0} ms", v => work.HoverDelayMs = (int)v, 10);
        Slider(L, "cdelay", "Collapse delay", "How long it waits after the pointer leaves", work.CollapseDelayMs, 0, 2000, v => $"{v:0} ms", v => work.CollapseDelayMs = (int)v, 50);
        L.Header("Auto-hide");
        Slider(L, "autohide", "Hide after inactivity", "0 keeps it visible", work.AutoHideMinutes, 0, 30, v => v <= 0 ? "Never" : $"{v:0.#} min", v => work.AutoHideMinutes = v, 0.5);
        Toggle(L, "ind", "Leave a small indicator when hidden", "Point at the top center of the screen to bring it back", work.ShowIndicatorWhenHidden, v => work.ShowIndicatorWhenHidden = v);
        Slider(L, "zone", "Reveal zone width", null, work.RevealZoneWidth, 120, 1200, v => $"{v:0}", v => work.RevealZoneWidth = (int)v, 20);
        L.Header("Screen");
        var choices = app.DisplayChoices();
        Seg(L, "showon", "Show the island on", "Built-in follows the laptop panel wherever it is; on a desktop it is the main screen", choices.Select(x => x.label).ToArray(), choices.Select(x => x.value).ToArray(),
            choices.Any(x => x.value == work.ShowOn) ? work.ShowOn : "laptop", v => work.ShowOn = v);
        L.Header("Fullscreen and stacking");
        Toggle(L, "fs", "Step aside for fullscreen apps", "Only on the island's own screen; fullscreen on another screen doesn't hide it", work.HideInFullscreen, v => work.HideInFullscreen = v);
        Toggle(L, "fsalert", "Still show alerts over fullscreen", null, work.AlertsOverFullscreen, v => work.AlertsOverFullscreen = v);
        Toggle(L, "top", "Stay on top of other always-on-top windows", null, work.KeepOnTop, v => work.KeepOnTop = v);
        Toggle(L, "start", "Start with Windows", null, work.StartWithWindows, v => work.StartWithWindows = v);
        L.End();
    }

    void Activities(Layout L)
    {
        L.Header("Media");
        Toggle(L, "media", "Now Playing", "Spotify, YouTube in Chrome, Plex - anything in Windows' media controls", work.Media, v => work.Media = v);
        Toggle(L, "wave", "Live waveform", "Bars that move with the actual sound", work.MediaWaveform, v => work.MediaWaveform = v);
        Toggle(L, "track", "Peek out when the track changes", "Even while the island is hidden", work.ShowOnTrackChange, v => work.ShowOnTrackChange = v);
        L.Header("Devices");
        Toggle(L, "bt", "Bluetooth alerts", "Devices connecting and disconnecting, and the radio turning on or off", work.BluetoothAlerts, v => work.BluetoothAlerts = v);
        Toggle(L, "pods", "AirPods detail", "Left, right and case battery, in-ear and lid", work.AirPods, v => work.AirPods = v);
        Toggle(L, "magic", "Read live AirPods data from MagicPods", svc.AirPods?.MagicPodsFound == true ? "MagicPods found" : "MagicPods not found - using Bluetooth broadcasts", work.UseMagicPods, v => work.UseMagicPods = v);
        Toggle(L, "disp", "Display alerts", "When a screen connects or disconnects", work.DisplayAlerts, v => work.DisplayAlerts = v);
        L.Header("System");
        Toggle(L, "bat", "Battery alerts", "Charger connected, unplugged, low and full", work.BatteryAlerts, v => work.BatteryAlerts = v);
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
        Toggle(L, "prev", "Show message previews", null, work.NotificationPreviews, v => work.NotificationPreviews = v);
        Toggle(L, "winbanners", "Hide Windows' own popups and chime", "Changes each app's Windows notification setting while the island shows them, and puts it back when it stops", work.HideWindowsBanners, v => work.HideWindowsBanners = v);
        Slider(L, "alertsec", "Keep alerts on screen for", null, work.AlertSeconds, 2, 15, v => $"{v:0} s", v => work.AlertSeconds = (int)v, 1);
        Text(L, "muted", "Don't show these apps", "Comma separated, e.g. Discord, Teams", string.Join(", ", work.NotificationMutedApps), v => work.NotificationMutedApps = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), "App names");
        L.End();
        L.Note("Status: " + (svc.Notifications?.Status ?? "Off"));
        L.Header("Calls");
        Toggle(L, "calls", "Phone calls", "Incoming calls from Phone Link, with answer and decline", work.Calls, v => work.Calls = v);
        L.End();
        L.Header("Sounds");
        Toggle(L, "snd", "Island sounds", "Its own chimes and ringtone; Windows' chime is off while the island shows notifications", work.Sounds, v => work.Sounds = v);
        Slider(L, "sndvol", "Sound volume", null, work.SoundVolume, 0, 1, v => $"{v * 100:0}%", v => work.SoundVolume = v, 0.05);
        Toggle(L, "sndnotif", "Chime for notifications", null, work.NotificationSound, v => work.NotificationSound = v);
        Tone(L, "tone:n", "Notification sound", Sounds.Notification, work.NotificationTone, v => work.NotificationTone = v);
        Toggle(L, "sndcall", "Ringtone for calls", null, work.CallRingtone, v => work.CallRingtone = v);
        Tone(L, "tone:c", "Ringtone", Sounds.Call, work.Ringtone, v => work.Ringtone = v);
        Tone(L, "tone:t", "Timer sound", Sounds.Timer, work.TimerTone, v => work.TimerTone = v);
        Toggle(L, "snddev", "Sounds for devices and the charger", "Connecting, disconnecting, plugging in, low battery", work.DeviceSounds, v => work.DeviceSounds = v);
        Tone(L, "tone:d", "Device sound", Sounds.Connect, work.DeviceTone, v => work.DeviceTone = v);
        L.End();
        L.Note("Picking a sound plays it; pick it again to hear it again.");
        Button(L, "preview", "Preview popups", "Shows one of each kind the island can show, in turn, with its sound", "Preview", () => app.PreviewPopups());
    }

    void Widgets(Layout L)
    {
        L.Header("Weather");
        Toggle(L, "wx", "Weather", null, work.Weather, v => work.Weather = v);
        Seg(L, "wxsrc", "Source", "Auto uses your Home Assistant weather when it's reachable", new[] { "Auto", "Home Assistant", "Open-Meteo" }, new[] { "auto", "homeassistant", "openmeteo" }, work.WeatherSource, v => work.WeatherSource = v);
        Text(L, "city", "City (Open-Meteo)", "Used when Home Assistant isn't reachable", work.City, v => work.City = v, "e.g. New York");
        Text(L, "wxent", "Weather entity (Home Assistant)", "Leave empty for the first weather.* entity", work.WeatherEntity, v => work.WeatherEntity = v, "weather.home");
        Toggle(L, "f", "Fahrenheit", null, work.Fahrenheit, v => work.Fahrenheit = v);
        Toggle(L, "24h", "24-hour clock", null, work.Use24Hour, v => work.Use24Hour = v);
        L.Header("Tools");
        Toggle(L, "notes", "Notes and to-dos", null, work.Notes, v => work.Notes = v);
        Toggle(L, "shelf", "File shelf", "Drop files on the island to park them", work.Shelf, v => work.Shelf = v);
        Toggle(L, "clip", "Clipboard history", "Kept in memory only, never written to disk", work.Clipboard, v => work.Clipboard = v);
        Toggle(L, "timer", "Timer", "A countdown of any length; also \"timer 10m\" in search", work.Timer, v => work.Timer = v);
        Toggle(L, "tsound", "Ring when a timer ends", "Windows' alarm, until you dismiss it (a minute at most)", work.TimerSound, v => work.TimerSound = v);
        L.End();
    }

    void Keys(Layout L)
    {
        L.Header("Search");
        Toggle(L, "search", "Search apps and files", svc.Search?.UsingEverything == true ? "Through Everything" : "Everything isn't running: the built-in index of apps and your folders", work.Search, v => work.Search = v);
        Slider(L, "results", "Results to show", null, work.SearchResults, 3, 12, v => $"{v:0}", v => work.SearchResults = (int)v, 1);
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
            help: "In Home Assistant, open your profile (your name at the bottom left), then the Security tab. Under Long-lived access tokens, press Create token, name it Isola, and copy it. Home Assistant shows it only once, so paste it here right away. The token stays on this PC.");
        Button(L, "hatest", "Test connection", testResult.Length > 0 ? testResult : ha?.StatusText, "Test", () => { testResult = "Testing…"; testAt = Ui.Clock.Now; Test(); }, testResult == "Testing…" || Spinning(testAt));
        L.Header($"Entities on the island ({work.HaEntities.Count})");
        if (ha == null) return;
        // selected first, removable
        foreach (var id in work.HaEntities.ToList())
        {
            var b = L.Row(40);
            ha.Entities.TryGetValue(id, out var e);
            var e2 = e ?? ha.All.FirstOrDefault(x => x.Id == id);
            canvas.Text(e2?.Name ?? id, new Box(b.X + 20, b.Y, b.W - 280, b.H), 13, Col.White, FontWeight.Medium);
            canvas.Text(id, new Box(b.X + b.W * 0.45f, b.Y, b.W * 0.4f, b.H), 11.5f, Col.White.A(0.45f));
            int idx = work.HaEntities.IndexOf(id);
            ux.IconButton("haup:" + id, b.Right - 88, b.CY, 26, Glyph.Up, () => { if (idx > 0) { work.HaEntities.RemoveAt(idx); work.HaEntities.Insert(idx - 1, id); Changed(); } }, null, 11, idx > 0);
            ux.IconButton("hadn:" + id, b.Right - 60, b.CY, 26, Glyph.Down, () => { if (idx < work.HaEntities.Count - 1) { work.HaEntities.RemoveAt(idx); work.HaEntities.Insert(idx + 1, id); Changed(); } }, null, 11, idx < work.HaEntities.Count - 1);
            ux.IconButton("harm:" + id, b.Right - 33, b.CY, 26, Glyph.Close, () => { work.HaEntities.Remove(id); Changed(); }, Col.Hex(0xff453a), 10);
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
            ux.IconButton("haadd:" + id, b.Right - 33, b.CY, 26, Glyph.Add, () => { work.HaEntities.Add(id); Changed(); _ = svc.Ha.RefreshAsync(); }, Col.Hex(0x34c759), 11);
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

    void About(Layout L)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        L.Header("Isola");
        var b = L.Row(84);
        canvas.Text("Version " + Updater.Short(typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0)), new Box(b.X + 20, b.Y + 12, 400, 20), 13.5f, Col.White, FontWeight.SemiBold);
        canvas.Text($"GPU: {Gfx.AdapterName}  ·  Memory: {proc.WorkingSet64 / 1048576.0:0} MB working set, {proc.PrivateMemorySize64 / 1048576.0:0} MB private", new Box(b.X + 20, b.Y + 34, b.W - 36, 18), 11.5f, Col.White.A(0.55f));
        canvas.Text($"Data folder: {Paths.Home}", new Box(b.X + 20, b.Y + 54, b.W - 36, 18), 11.5f, Col.White.A(0.55f));
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
}
