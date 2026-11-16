using System.Diagnostics;
using System.Runtime.InteropServices;
using Isola.Island;
using Isola.Native;
using Isola.Render;
using Vortice.Mathematics;
using Box = Isola.Render.Box;
using FontWeight = Vortice.DirectWrite.FontWeight;

namespace Isola.App;

public enum StepState { Pending, Running, Done, Failed, Skipped }

public sealed class SetupStep
{
    public string Title = "", Detail = "";
    public StepState State;
    public Action Skip;    // offered while it waits on the user elsewhere (the Store)
}

/// <summary>
/// The setup wizard: IsolaSetup.exe unpacks the island to a temporary
/// folder and runs it with --setup-wizard, so the wizard is drawn by the
/// island's own renderer and looks like its settings. Pages: what this PC has,
/// the options (Everything and MagicPods off by default, and grayed out as
/// "Already installed" when they are), the install with a line per step, and
/// done. The island itself is installed by the setup program it came from
/// (/silent /nolaunch), so there is one install path for the wizard, updates
/// and silent installs alike.
///
/// When the other kind of install is there (the Microsoft Store version for
/// this setup, or the GitHub install for the Store version's first start), the
/// first page asks whether to replace it, keeping settings or not (Switchover).
///
/// Exit codes: 0 installed (or replaced), 2 closed before installing, 1 failed,
/// 3 the window couldn't be made (setup then falls back to its plain dialogs).
/// </summary>
public sealed unsafe partial class SetupWizard
{
    nint hwnd;
    Surface surface;
    readonly Canvas canvas = new();
    readonly Ux ux = new();
    float scale = 1;
    bool dirty = true, quit;
    int page, exitCode = 2;
    readonly string setupExe;
    static SetupWizard current;

    // checks (filled in the background)
    volatile bool checksDone;
    bool winOk = true, fontOk = true, wingetOk = true, btOk = true;
    string access = "…";
    bool everythingHere, magicPodsHere;

    // options
    bool optAutostart = true, optEverything, optMagicPods;

    // already installed: update, repair or uninstall
    const int PWelcome = 0, POptions = 1, PInstalling = 2, PDone = 3, PMaintain = 4, PReplace = 5;

    // the other kind of install: Store for this setup, GitHub for the Store version
    enum Other { None, Store, GitHub }
    Other other;
    bool replaceAgreed, keepData = true;
    // running as the Store version, asked whether to replace the GitHub install
    bool StoreSide => other == Other.GitHub;
    enum Action { Update, Repair, Uninstall }
    Action action = Action.Repair;
    bool wipeData;
    Version installed, latestOnline;
    string onlineUrl, onlineSha, onlineNote = "Checking GitHub…";
    volatile bool onlineDone;
    readonly Version mine = typeof(SetupWizard).Assembly.GetName().Version is { } mv ? new Version(mv.Major, mv.Minor, Math.Max(0, mv.Build)) : new Version(1, 0, 0);
    Version UpdateTo => Choices(installed, mine, latestOnline).updateTo;
    bool CanUpdate => Choices(installed, mine, latestOnline).update;
    bool CanRepair => Choices(installed, mine, latestOnline).repair;

    /// <summary>
    /// What the wizard offers over an installed island: an update to the newer
    /// of this setup and GitHub's newest, if either is newer; a repair with this
    /// setup's version unless that would go backwards.
    /// </summary>
    internal static (bool update, Version updateTo, bool repair) Choices(Version installed, Version mine, Version online)
    {
        var to = online != null && online > mine ? online : mine;
        return (installed != null && to > installed, to, installed != null && mine >= installed);
    }

    readonly List<SetupStep> steps = new();
    volatile bool installing, finished, anyFailed;
    readonly string installedExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Isola", "Isola.exe");

    SetupWizard(string setup) { setupExe = setup; }
    int previewPage = -1;

    void Preview()
    {
        page = previewPage;
        if (page >= PReplace) { other = page == PReplace ? Other.Store : Other.GitHub; page = PReplace; checksDone = true; return; }
        if (page >= 2 && page <= 3)
        {
            var a = Add("Icon font"); a.State = StepState.Done; a.Detail = "Installed for your account";
            var b = Add("Isola"); b.State = StepState.Done; b.Detail = "Installed; starts when you sign in";
            var c = Add("Everything"); c.State = page == 2 ? StepState.Running : StepState.Done; c.Detail = page == 2 ? "Installing through Windows Package Manager; approve the administrator prompt" : "Installed and running";
            var d = Add("MagicPods"); d.State = page == 2 ? StepState.Pending : StepState.Skipped; d.Detail = page == 2 ? "Waiting" : "Skipped: the island reads your AirPods itself";
            installing = page == 2; finished = page == 3; exitCode = 0;
        }
    }

    [LibraryImport("dwmapi.dll")] private static partial int DwmExtendFrameIntoClientArea(nint h, int* margins);

    public static int Run(string[] args)
    {
        int i = Array.IndexOf(args, "--setup");
        string setup = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        // --preview-page N: show a page with sample steps and run nothing (for
        // screenshots); it opens on the main screen without taking the focus
        int pi = Array.IndexOf(args, "--preview-page");
        int preview = pi >= 0 && pi + 1 < args.Length && int.TryParse(args[pi + 1], out int pp) ? pp : -1;
        if (preview < 0 && (setup == null || !File.Exists(setup))) { Log.E("wizard", "no setup program given", null); return 3; }
        try
        {
            // Programs started from here (the installed island on "Open", setup,
            // winget) inherit the working folder; the wizard's own temp folder
            // would stay locked by the island and never be cleaned up.
            try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch { }
            Win32.OleInitialize(0);
            Gfx.Init(false);
            var w = current = new SetupWizard(setup) { previewPage = preview };
            // asked first: the Store version would otherwise keep this one from starting
            if (preview < 0 && Shared.Switchover.StoreInstalled()) { w.other = Other.Store; w.page = PReplace; Log.I("wizard", "the Microsoft Store version is installed"); }
            if (!w.Create()) return 3;
            return w.Loop();
        }
        catch (Exception ex) { Log.Crash("wizard", ex); return 3; }
    }

    /// <summary>
    /// The Store version's first question when the GitHub install is also
    /// there, before the island starts. 0 replaced, 2 not now, 1 failed, 3 the
    /// window couldn't be made.
    /// </summary>
    public static int RunReplace()
    {
        try
        {
            Win32.OleInitialize(0);
            Gfx.Init(false);
            var w = current = new SetupWizard(null) { other = Other.GitHub, page = PReplace, checksDone = true };
            if (!w.Create()) return 3;
            int r = w.Loop();
            w.surface?.Dispose();
            current = null;
            return r;
        }
        catch (Exception ex) { Log.E("wizard", "replace question failed", ex); return 3; }
    }

    bool Create()
    {
        nint inst = Win32.GetModuleHandleW(null);
        fixed (char* name = "Isola.Setup")
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &Proc, hInstance = inst, lpszClassName = name, hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW) };
            string ico = Path.Combine(AppContext.BaseDirectory, "assets", "island.ico");
            wc.hIcon = Win32.LoadImageW(0, ico, 1, 32, 32, 0x10); wc.hIconSm = Win32.LoadImageW(0, ico, 1, 16, 16, 0x10);
            Win32.RegisterClassExW(&wc);
        }
        // on the screen with the pointer, at its scale
        Win32.GetCursorPos(out var pt);
        if (previewPage >= 0) pt = new POINT(0, 0);   // the main screen, wherever the pointer is
        nint mon = Win32.MonitorFromPoint(pt, 2);
        Win32.GetDpiForMonitor(mon, 0, out uint dpi, out _);
        scale = Math.Max(1, dpi / 96f);
        var mi = new MONITORINFOEXW(); mi.cbSize = (uint)sizeof(MONITORINFOEXW); Win32.GetMonitorInfo(mon, &mi);
        var wa = mi.rcWork;
        int w = (int)(720 * scale), h = (int)(560 * scale);
        hwnd = Win32.CreateWindowExW(Win32.WS_EX_NOREDIRECTIONBITMAP | Win32.WS_EX_APPWINDOW, "Isola.Setup", StoreSide ? "Isola" : "Isola Setup",
            0x00CA0000 /*caption, sysmenu, minimize*/ | 0x00020000, wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h, 0, 0, inst, 0);
        if (hwnd == 0) return false;
        int dark = 1, acrylic = 3, round = 2;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, 4);
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, &acrylic, 4);
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, &round, 4);
        int* margins = stackalloc int[4] { -1, -1, -1, -1 };
        DwmExtendFrameIntoClientArea(hwnd, margins);
        Win32.GetClientRect(hwnd, out var rc);
        surface = new Surface(hwnd, rc.Width, rc.Height) { Dpi = 96 * scale };
        ux.RequestFrame = () => dirty = true;
        ux.Now = () => Ui.Clock.Now;
        Ui.Init(hwnd);
        if (previewPage >= 0) { Win32.ShowWindow(hwnd, 4 /*SW_SHOWNOACTIVATE*/); Preview(); }
        else { Win32.ShowWindow(hwnd, Win32.SW_SHOWNORMAL); Win32.SetForegroundWindow(hwnd); }
        if (!StoreSide) Task.Run(Check);
        Log.I("wizard", StoreSide ? "asking whether to replace the GitHub install" : "setup wizard opened");
        return true;
    }

    void Check()
    {
        try
        {
            winOk = SetupChecks.WindowsSupported(Environment.OSVersion.Version);
            fontOk = SetupChecks.IconFontPresent();
            everythingHere = SetupChecks.EverythingInstalled();
            magicPodsHere = SetupChecks.MagicPodsInstalled();
            access = SetupChecks.NotificationAccess();
            btOk = SetupChecks.BluetoothPresent();
            wingetOk = SetupChecks.WingetAvailable();
            installed = SetupChecks.InstalledVersion(installedExe);
            if (installed != null) { action = CanUpdate ? Action.Update : Action.Repair; Task.Run(CheckOnline); }
            Log.I("wizard", $"checks: windows={winOk} font={fontOk} everything={everythingHere} magicpods={magicPodsHere} notifications={access} bluetooth={btOk} winget={wingetOk}");
        }
        catch (Exception ex) { Log.E("wizard", "checks failed", ex); }
        checksDone = true;
        Ui.Post(() => dirty = true);
    }

    /// <summary>The newest release on GitHub, with the installed island's own token (the repository is private).</summary>
    // the installed island's own settings (where its GitHub token is), wherever this copy runs from
    static string InstalledConfig => Environment.GetEnvironmentVariable("ISOLA_HOME") is { Length: > 0 } h
        ? Path.Combine(h, "isola.config.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Isola", "isola.config.json");

    void CheckOnline()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Isola-Setup/1.0");
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Services.Updater.Repo}/releases?per_page=30");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = http.Send(req);
            if (!resp.IsSuccessStatusCode) { onlineNote = $"GitHub said {(int)resp.StatusCode}: couldn't look for a newer release"; return; }
            var best = Services.Updater.Latest(System.Text.Json.Nodes.JsonNode.Parse(new StreamReader(resp.Content.ReadAsStream()).ReadToEnd()) as System.Text.Json.Nodes.JsonArray);
            if (best is { } b) { latestOnline = new Version(b.ver.Major, b.ver.Minor, Math.Max(0, b.ver.Build)); onlineUrl = b.assetUrl; onlineSha = b.sha; }
            onlineNote = latestOnline == null ? "No releases found" : $"Newest on GitHub: v{Services.Updater.Short(latestOnline)}";
            if (CanUpdate && action == Action.Repair) action = Action.Update;
        }
        catch (Exception ex) { onlineNote = "Couldn't reach GitHub: " + ex.Message; }
        finally { onlineDone = true; Ui.Post(() => dirty = true); }
    }

    int Loop()
    {
        double lastStep = Ui.Clock.Now;
        while (!quit)
        {
            while (Win32.PeekMessageW(out var msg, 0, 0, 0, Win32.PM_REMOVE))
            {
                if (msg.message == Win32.WM_QUIT) { quit = true; break; }
                Win32.TranslateMessage(msg); Win32.DispatchMessageW(msg);
            }
            if (quit) break;
            Ui.Drain(); Ui.RunTimers();
            double now = Ui.Clock.Now;
            bool spin = !checksDone || installing;
            if (dirty || ux.Step(0) || spin)
            {
                if (surface.FrameWaitHandle == 0 || Win32.WaitForSingleObject(surface.FrameWaitHandle, 0) == 0)
                {
                    ux.Step(Math.Clamp(now - lastStep, 0, 0.05)); lastStep = now; dirty = false;
                    var ctx = surface.Begin(); canvas.Begin(ctx);
                    try { Render(); } catch (Exception ex) { Log.E("wizard", "render failed", ex); }
                    surface.End();
                }
            }
            else lastStep = now;
            Win32.MsgWaitForMultipleObjectsEx(0, null, spin || dirty ? 16u : 200u, Win32.QS_ALLINPUT, Win32.MWMO_INPUTAVAILABLE);
        }
        Log.I("wizard", "setup wizard closed: " + exitCode);
        Log.Flush();
        return exitCode;
    }

    [UnmanagedCallersOnly]
    static nint Proc(nint h, uint m, nint w, nint l)
    {
        try { if (current != null && current.hwnd == h) { var r = current.Handle(m, w, l); if (r.HasValue) return r.Value; } }
        catch (Exception ex) { Log.E("wizard", $"message 0x{m:X}", ex); }
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
                Win32.SetCursor(Win32.LoadCursorW(0, ux.CursorAt(X(), Y()) == CursorKind.Hand ? Win32.IDC_HAND : Win32.IDC_ARROW));
                return 0;
            }
            case Win32.WM_MOUSELEAVE: ux.Leave(); dirty = true; return 0;
            case Win32.WM_LBUTTONDOWN: Win32.SetCapture(hwnd); ux.Down(X(), Y()); dirty = true; return 0;
            case Win32.WM_LBUTTONUP: Win32.ReleaseCapture(); ux.Up(X(), Y()); dirty = true; return 0;
            case Win32.WM_CLOSE:
                if (installing) return 0;            // never half-way: the install finishes first
                Win32.DestroyWindow(hwnd); return 0;
            case 0x0002 /*WM_DESTROY*/:
                quit = true;
                // The Store version's question runs on the island's own thread,
                // just before the island starts: a WM_QUIT left in the queue
                // ended the island the moment it was ready.
                if (!StoreSide) Win32.PostQuitMessage(0);
                return 0;
        }
        return null;
    }

    // ======================================================================
    // drawing
    // ======================================================================

    static readonly Color4 Blue = Col.Hex(0x0a84ff), Green = Col.Hex(0x34c759), Amber = Col.Hex(0xff9f0a), Red = Col.Hex(0xff453a);
    int[] Order => StoreSide ? new[] { PReplace, PInstalling }
                 : other == Other.Store ? BaseOrder.Prepend(PReplace).ToArray() : BaseOrder;
    int[] BaseOrder => installed != null && action == Action.Uninstall ? new[] { PWelcome, PMaintain, PInstalling, PDone }
                 : installed != null ? new[] { PWelcome, PMaintain, POptions, PInstalling, PDone }
                 : new[] { PWelcome, POptions, PInstalling, PDone };

    void Render()
    {
        var c = canvas;
        ux.BeginFrame(c);
        Win32.GetClientRect(hwnd, out var rc);
        float W = rc.Width / scale, H = rc.Height / scale;
        c.Fill(new Box(0, 0, W, H), Col.Hex(0x1c1c20, 0.9f));
        // the island itself, as a small pill at the top: what is being installed
        var pill = Box.Centered(W / 2, 34, 120, 30);
        c.Fill(pill, Col.Black, 15);
        using (var glow = c.Linear(new System.Numerics.Vector2(pill.X, 0), new System.Numerics.Vector2(pill.Right, 0), (0, Col.Hex(0x14d4c4).A(0.9f)), (0.5f, Col.Hex(0x2f6bff)), (1, Col.Hex(0x8b5cf6).A(0.9f))))
            c.Fill(new Box(pill.X + 14, pill.Bottom - 2, pill.W - 28, 2), glow);
        c.Circle(pill.CX - 9, pill.CY, 3.2f, Col.White); c.Circle(pill.CX + 9, pill.CY, 3.2f, Col.White);
        // where we are
        var order = Order; int at = Math.Max(0, Array.IndexOf(order, page));
        float dx = W / 2 - (order.Length - 1) * 9;
        for (int i = 0; i < order.Length; i++)
            c.Circle(dx + i * 18, 70, i == at ? 3.5f : 2.5f, Col.White.A(i == at ? 0.9f : i < at ? 0.5f : 0.2f));

        var area = new Box(48, 92, W - 96, H - 92 - 76);
        switch (page)
        {
            case 0: Welcome(c, area); break;
            case 1: Options(c, area); break;
            case 2: Installing(c, area); break;
            case 3: Done(c, area); break;
            case 4: Maintain(c, area); break;
            case PReplace: Replace(c, area); break;
        }
        Buttons(c, W, H);
        ux.EndFrame();
    }

    void Title(Canvas c, Box a, string title, string sub)
    {
        c.Text(title, new Box(a.X, a.Y, a.W, 34), 24, Col.White, FontWeight.SemiBold);
        if (sub != null) c.Text(sub, new Box(a.X, a.Y + 36, a.W, 40), 12.5f, Col.White.A(0.6f), FontWeight.Normal, HAlign.Left, VAlign.Top, true);
    }

    // one line of a checklist: a state mark, a title and what it means
    void Line(Canvas c, float x, float y, float w, StepState st, string title, string detail, Color4? mark = null)
    {
        var row = new Box(x, y, w, 46);
        c.Fill(row, Col.White.A(0.05f), 8);
        float mx = row.X + 22, my = row.CY;
        switch (st)
        {
            case StepState.Running: Paint.Spinner(c, mx, my, 14, Col.White.A(0.8f), Ui.Clock.Now); break;
            case StepState.Done: c.Circle(mx, my, 9, (mark ?? Green).A(0.95f)); c.Icon(Glyph.Check, mx, my, 11, Col.Black); break;
            case StepState.Failed: c.Circle(mx, my, 9, (mark ?? Red).A(0.95f)); c.Icon(Glyph.Warning, mx, my, 10, Col.Black); break;
            case StepState.Skipped: c.Circle(mx, my, 9, Col.White.A(0.18f)); c.Text("–", Box.Centered(mx, my, 18, 18), 12, Col.White.A(0.7f), FontWeight.Bold, HAlign.Center); break;
            default: c.Ring(mx, my, 8, Col.White.A(0.25f), 1.5f); break;
        }
        c.Text(title, new Box(row.X + 44, row.Y + 6, row.W - 60, 18), 13, Col.White.A(0.95f), FontWeight.Medium);
        c.Text(detail ?? "", new Box(row.X + 44, row.Y + 24, row.W - 60, 16), 11.5f, Col.White.A(0.5f), FontWeight.Normal);
    }

    void Welcome(Canvas c, Box a)
    {
        string ver = typeof(SetupWizard).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
        Title(c, a, installed != null ? "Isola " + ver : "Install Isola " + ver,
            installed != null ? $"Version {Services.Updater.Short(installed)} is installed. First, what this PC has:" : "For your account only. First, what this PC already has:");
        float y = a.Y + 74, w = a.W;
        if (!checksDone)
        {
            for (int i = 0; i < 5; i++) { Paint.Skeleton(c, new Box(a.X, y, w, 46), 8, Ui.Clock.Now); y += 52; }
            return;
        }
        Line(c, a.X, y, w, winOk ? StepState.Done : StepState.Failed, "Windows", winOk ? Environment.OSVersion.VersionString : "Needs Windows 10 version 2004 or later"); y += 52;
        Line(c, a.X, y, w, fontOk ? StepState.Done : StepState.Pending, "Icon font", fontOk ? "Segoe Fluent Icons is installed" : "Missing (Windows 10): it will be installed from Microsoft"); y += 52;
        bool allowed = access == "Allowed";
        Line(c, a.X, y, w, allowed ? StepState.Done : StepState.Failed, "Notification access", allowed ? "Windows lets the island read notifications" : "Off in Windows settings: notifications and calls need it", allowed ? null : Amber);
        if (!allowed) ux.Pill("w:notif", new Box(a.Right - 150, y + 9, 136, 28), "Open settings", () => Win32.ShellExecute("ms-settings:privacy-notifications"), Col.White);
        y += 52;
        Line(c, a.X, y, w, btOk ? StepState.Done : StepState.Failed, "Bluetooth", btOk ? "AirPods and device popups will work" : "No Bluetooth radio: device popups stay off", btOk ? null : Amber); y += 52;
        Line(c, a.X, y, w, wingetOk ? StepState.Done : StepState.Failed, "Windows Package Manager", wingetOk ? "Can install Everything for you" : "Not found: Everything would open its download page instead", wingetOk ? null : Amber);
    }

    // the other copy: what changes, and whether settings come along
    void Replace(Canvas c, Box a)
    {
        bool store = other == Other.Store;
        Title(c, a, store ? "Isola Notch is installed from the Microsoft Store" : "Isola is also installed from GitHub",
            store ? "Only one copy can run at a time. Replace it with this version from GitHub?"
                  : "Only one copy can run at a time. Replace it with this Microsoft Store version?");
        float y = a.Y + 80;
        var info = new Box(a.X, y, a.W, 62);
        c.Fill(info, Col.White.A(0.055f), 8);
        c.Icon(store ? Glyph.Bell : Glyph.Refresh, info.X + 24, info.CY, 15, Col.White.A(0.6f));
        c.Text(store ? "This version can hide Windows' own popups" : "The Store keeps this version up to date",
            new Box(info.X + 50, info.Y + 11, info.W - 70, 20), 13.5f, Col.White.A(0.95f), FontWeight.Medium);
        c.Text(store ? "It updates itself from GitHub. The Store version is removed." : "It can't hide Windows' own popups. The GitHub version is removed.",
            new Box(info.X + 50, info.Y + 32, info.W - 70, 18), 11.5f, Col.White.A(0.5f), FontWeight.Normal);
        y += 68;
        var row = new Box(a.X, y, a.W, 62);
        c.Fill(row, Col.White.A(0.055f), 8);
        c.Icon(Glyph.Settings, row.X + 24, row.CY, 15, Col.White.A(0.6f));
        c.Text("Keep my settings, notes and shelf", new Box(row.X + 50, row.Y + 11, row.W - 130, 20), 13.5f, Col.White.A(0.95f), FontWeight.Medium);
        c.Text(keepData ? "They carry over to this version" : "Start fresh: they're removed with the other copy",
            new Box(row.X + 50, row.Y + 32, row.W - 130, 18), 11.5f, Col.White.A(0.5f), FontWeight.Normal);
        ux.Switch("o:keep", row.Right - 60, row.CY, keepData, () => { keepData = !keepData; dirty = true; }, Blue);
    }

    void AgreeReplace()
    {
        if (other == Other.Store) { replaceAgreed = true; page = PWelcome; dirty = true; return; }
        StartReplaceGitHub();
    }

    void Maintain(Canvas c, Box a)
    {
        if (installed == null) { float sy = a.Y; for (int i = 0; i < 4; i++) { Paint.Skeleton(c, new Box(a.X, sy, a.W, i == 0 ? 34 : 62), 8, Ui.Clock.Now); sy += i == 0 ? 80 : 68; } return; }
        Title(c, a, $"Version {Services.Updater.Short(installed)} is installed", onlineDone ? onlineNote : "Checking GitHub for a newer release…");
        float y = a.Y + 80;
        Choice(c, a, ref y, Action.Update, CanUpdate ? $"Update to {Services.Updater.Short(UpdateTo)}" : "Update",
            CanUpdate ? (UpdateTo > mine ? "Downloads it from GitHub, verified, then installs; settings kept" : "Installs this version; settings kept")
                      : onlineDone ? "You have the newest version" : "Looking for a newer version…", CanUpdate);
        Choice(c, a, ref y, Action.Repair, $"Repair / reinstall {Services.Updater.Short(mine)}",
            CanRepair ? "Puts the program files back as new; settings, notes and shelf kept" : $"This setup is older than the installed {Services.Updater.Short(installed)}", CanRepair);
        Choice(c, a, ref y, Action.Uninstall, "Uninstall", "Removes the island, its sign-in start and shortcuts, and gives Windows' own popups back", true);
        if (action == Action.Uninstall)
        {
            var row = new Box(a.X + 28, y, a.W - 28, 50);
            c.Fill(row, Col.White.A(0.04f), 8);
            c.Text("Also remove my settings, notes and shelf", new Box(row.X + 16, row.Y, row.W - 100, row.H), 13, Col.White.A(0.9f), FontWeight.Medium);
            ux.Switch("o:wipe", row.Right - 56, row.CY, wipeData, () => { wipeData = !wipeData; dirty = true; }, Red);
        }
    }

    // a selectable row (one of three); one that doesn't apply is grayed out
    void Choice(Canvas c, Box a, ref float y, Action act, string title, string sub, bool enabled)
    {
        var row = new Box(a.X, y, a.W, 62);
        bool sel = action == act && enabled;
        c.Fill(row, Col.White.A(!enabled ? 0.03f : sel ? 0.11f : 0.055f), 8);
        if (sel) c.Stroke(row, Blue.A(0.8f), 8, 1.5f);
        float alpha = enabled ? 1 : 0.4f;
        float rx = row.X + 24;
        c.Ring(rx, row.CY, 8, Col.White.A((sel ? 0.9f : 0.35f) * alpha), 1.5f);
        if (sel) c.Circle(rx, row.CY, 4.5f, Blue);
        c.Text(title, new Box(row.X + 46, row.Y + 11, row.W - 60, 20), 13.5f, Col.White.A(0.95f * alpha), FontWeight.Medium);
        c.Text(sub, new Box(row.X + 46, row.Y + 32, row.W - 60, 18), 11.5f, Col.White.A(0.5f * alpha), FontWeight.Normal);
        if (enabled) ux.Add("c:" + act, row, () => { action = act; dirty = true; }, 8);
        y += 68;
    }

    void Options(Canvas c, Box a)
    {
        Title(c, a, "Options", "Everything and MagicPods are optional: without them the island searches and reads your AirPods itself.");
        float y = a.Y + 80;
        Option(c, a, ref y, "auto", "Start when I sign in", "Starts the moment you sign in, before other startup apps", optAutostart, false, v => optAutostart = v);
        Option(c, a, ref y, "ev", "Install Everything", everythingHere ? "Already installed" : "Instant search across every drive (voidtools, free). Windows asks for administrator rights",
            optEverything && !everythingHere, everythingHere, v => optEverything = v);
        Option(c, a, ref y, "mp", "Install MagicPods", magicPodsHere ? "Already installed" : "Exact AirPods battery, ear detection and more (paid, Microsoft Store): opens its Store page",
            optMagicPods && !magicPodsHere, magicPodsHere, v => optMagicPods = v);
        // The privacy policy, shown before anything is installed: no account or
        // tracking, and it talks only to services the user sets up.
        y += 6;
        var row = new Box(a.X, y, a.W, 62);
        c.Icon(Glyph.Shield, row.X + 14, row.CY, 15, Col.White.A(0.55f));
        c.Text("Your privacy", new Box(row.X + 38, row.Y + 11, row.W - 190, 20), 13.5f, Col.White.A(0.9f), FontWeight.Medium);
        c.Text("No account or tracking. It only connects to services you set up.",
            new Box(row.X + 38, row.Y + 32, row.W - 190, 18), 11.5f, Col.White.A(0.5f), FontWeight.Normal);
        ux.Pill("privacy", new Box(row.Right - 150, row.CY - 14, 136, 28), "Privacy policy", () => Win32.ShellExecute(PrivacyUrl), Col.White);
    }

    public const string PrivacyUrl = "https://github.com/Scriptily/isola-notch/blob/main/PRIVACY.md";

    // a switch row; an installed helper's row is grayed out with a check instead of a switch
    void Option(Canvas c, Box a, ref float y, string id, string title, string sub, bool value, bool present, Action<bool> set)
    {
        var row = new Box(a.X, y, a.W, 62);
        c.Fill(row, Col.White.A(present ? 0.03f : 0.055f), 8);
        float alpha = present ? 0.45f : 1;
        c.Text(title, new Box(row.X + 20, row.Y + 11, row.W - 130, 20), 13.5f, Col.White.A(0.95f * alpha), FontWeight.Medium);
        c.Text(sub, new Box(row.X + 20, row.Y + 32, row.W - 130, 18), 11.5f, Col.White.A(0.5f * alpha), FontWeight.Normal);
        if (present)
        {
            c.Circle(row.Right - 40, row.CY, 10, Col.White.A(0.2f));
            c.Icon(Glyph.Check, row.Right - 40, row.CY, 11, Col.White.A(0.7f));
        }
        else ux.Switch("o:" + id, row.Right - 60, row.CY, value, () => { set(!value); dirty = true; }, Blue);
        y += 68;
    }

    void Installing(Canvas c, Box a)
    {
        string doing = StoreSide ? "Replacing" : uninstalled ? "Uninstalling" : installed == null ? "Installing" : action == Action.Update ? "Updating" : "Repairing";
        Title(c, a, finished ? (anyFailed ? (StoreSide ? "Couldn't replace it" : "Done, with a note") : "All set") : doing,
            finished ? (StoreSide && anyFailed ? "Isola from GitHub stays installed and keeps running." : null) : "This takes a moment. Leave this window open.");
        float y = a.Y + 74;
        lock (steps)
            foreach (var s in steps)
            {
                Line(c, a.X, y, a.W, s.State, s.Title, s.Detail);
                if (s.Skip != null && s.State == StepState.Running)
                {
                    var act = s.Skip;
                    ux.Pill("skip:" + s.Title, new Box(a.Right - 96, y + 9, 82, 28), "Skip", act, Col.White);
                }
                y += 52;
            }
    }

    void Done(Canvas c, Box a)
    {
        if (uninstalled)
        {
            Title(c, a, anyFailed ? "Mostly removed" : "Isola was removed", wipeData ? "Its program, sign-in start, shortcuts and your data are gone." : "Your settings, notes and shelf are kept in case you install it again.");
            float yy = a.Y + 90;
            lock (steps) foreach (var s in steps) { Line(c, a.X, yy, a.W, s.State, s.Title, s.Detail); yy += 52; }
            return;
        }
        Title(c, a, anyFailed ? "Isola is installed" : "Isola is ready",
            "It sits at the top of your screen. Hover to peek, click to open, and find settings behind the gear." +
            (anyFailed ? " One optional step didn't finish; the island works without it." : ""));
        float y = a.Y + 90;
        lock (steps) foreach (var s in steps) { Line(c, a.X, y, a.W, s.State, s.Title, s.Detail); y += 52; }
    }

    void Buttons(Canvas c, float W, float H)
    {
        float y = H - 58, bw = 120;
        var primary = new Box(W - 48 - bw, y, bw, 34);
        var secondary = new Box(primary.X - 12 - bw, y, bw, 34);
        switch (page)
        {
            case PReplace:
                Primary(primary, "Replace", AgreeReplace);
                ux.Pill("cancel", secondary, StoreSide ? "Not now" : "Cancel", () => Win32.DestroyWindow(hwnd), Col.White);
                break;
            case PWelcome:
                if (!checksDone) { c.Fill(primary, Col.White.A(0.08f), 17); Paint.Spinner(c, primary.CX, primary.CY, 14, Col.White.A(0.8f), Ui.Clock.Now); }
                else if (winOk || installed != null) Primary(primary, "Next", () => { page = installed != null ? PMaintain : POptions; dirty = true; });
                if (other == Other.Store) ux.Pill("back", secondary, "Back", () => { page = PReplace; dirty = true; }, Col.White);
                else ux.Pill("cancel", secondary, "Cancel", () => Win32.DestroyWindow(hwnd), Col.White);
                break;
            case PMaintain:
                if (action == Action.Uninstall) Primary(primary, "Uninstall", StartUninstall, Red);
                else if ((action == Action.Update && CanUpdate) || (action == Action.Repair && CanRepair)) Primary(primary, "Next", () => { page = POptions; dirty = true; });
                ux.Pill("back", secondary, "Back", () => { page = PWelcome; dirty = true; }, Col.White);
                break;
            case POptions:
                Primary(primary, installed == null ? "Install" : action == Action.Update ? "Update" : "Repair", StartInstall);
                ux.Pill("back", secondary, "Back", () => { page = installed != null ? PMaintain : PWelcome; dirty = true; }, Col.White);
                break;
            case 2:
                if (finished && StoreSide) { if (anyFailed) Primary(primary, "Close", () => Win32.DestroyWindow(hwnd)); }
                else if (finished) Primary(primary, "Next", () => { page = 3; dirty = true; });
                else { c.Fill(primary, Col.White.A(0.08f), 17); Paint.Spinner(c, primary.CX, primary.CY, 14, Col.White.A(0.8f), Ui.Clock.Now); }
                break;
            case 3:
                if (exitCode == 0 && !uninstalled) Primary(primary, "Open", () => { try { Process.Start(new ProcessStartInfo(installedExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installedExe) }); } catch { } Win32.DestroyWindow(hwnd); });
                ux.Pill("close", secondary, "Close", () => Win32.DestroyWindow(hwnd), Col.White);
                break;
        }
    }

    void Primary(Box b, string label, System.Action click, Color4? tint = null)
    {
        ux.Add("p:" + label, b, click, 17);
        float h = ux.HoverT("p:" + label);
        canvas.Fill(b, Col.Mix(tint ?? Blue, Col.White, 0.12f * h), 17);
        canvas.Text(label, b, 13, Col.White, FontWeight.SemiBold, HAlign.Center);
    }

    // ======================================================================
    // installing
    // ======================================================================

    SetupStep Add(string title, string detail = "Waiting")
    {
        var s = new SetupStep { Title = title, Detail = detail };
        lock (steps) steps.Add(s);
        return s;
    }

    void Set(SetupStep s, StepState st, string detail) { s.State = st; s.Detail = detail; Ui.Post(() => dirty = true); }

    bool uninstalled;

    void StartUninstall()
    {
        page = PInstalling; installing = true; uninstalled = true; dirty = true;
        var st = Add("Isola", "Removing");
        Task.Run(() =>
        {
            try
            {
                Set(st, StepState.Running, "Closing it and removing its files");
                string setup = Path.Combine(Path.GetDirectoryName(installedExe)!, "IsolaSetup.exe");
                if (!File.Exists(setup)) setup = setupExe;
                var (code, _) = SetupChecks.Run(setup, "/uninstall /silent" + (wipeData ? " /wipe" : ""), 2 * 60 * 1000);
                if (code == 0) { Set(st, StepState.Done, wipeData ? "Removed, with your data" : "Removed; your data kept"); exitCode = 0; }
                else { Set(st, StepState.Failed, "Uninstall failed (" + code + "); details in setup.log"); exitCode = 1; anyFailed = true; }
            }
            catch (Exception ex) { Log.E("wizard", "uninstall failed", ex); anyFailed = true; exitCode = 1; }
            installing = false; finished = true;
            Ui.Post(() => dirty = true);
        });
    }

    // the Store version replacing the GitHub install; the island starts once it's gone
    void StartReplaceGitHub()
    {
        page = PInstalling; installing = true; dirty = true;
        var st = Add("Isola from GitHub", "Waiting");
        Task.Run(() =>
        {
            try
            {
                Set(st, StepState.Running, "Closing it and removing it");
                var (ok, d) = Shared.Switchover.ReplaceGitHub(keepData, Shared.Switchover.GitHubDir, Paths.Home,
                    (f, a) => SetupChecks.Run(f, a, 2 * 60 * 1000).code, m => Log.I("switch", m), Paths.LogFile);
                Set(st, ok ? StepState.Done : StepState.Failed, d);
                exitCode = ok ? 0 : 1; anyFailed = !ok;
            }
            catch (Exception ex) { Log.E("wizard", "replace failed", ex); Set(st, StepState.Failed, ex.Message); exitCode = 1; anyFailed = true; }
            installing = false; finished = true;
            Ui.Post(() => { dirty = true; if (!anyFailed) Ui.After(0.8, () => Win32.DestroyWindow(hwnd)); });
        });
    }

    void StartInstall()
    {
        page = 2; installing = true; dirty = true;
        var store = replaceAgreed ? Add("Microsoft Store version") : null;
        var font = fontOk ? null : Add("Icon font");
        var island = Add("Isola");
        var ev = optEverything && !everythingHere ? Add("Everything") : null;
        var mp = optMagicPods && !magicPodsHere ? Add("MagicPods") : null;
        Task.Run(() =>
        {
            try
            {
                if (store != null)
                {
                    Set(store, StepState.Running, "Closing it and removing it");
                    void log(string m) => Log.I("switch", m);
                    var (ok, d) = Shared.Switchover.ReplaceStore(keepData, Shared.Switchover.Data, Shared.Switchover.StoreData, () => Shared.Switchover.RemoveStorePackage(log), log);
                    Set(store, ok ? StepState.Done : StepState.Failed, d);
                    if (!ok) anyFailed = true;
                }

                if (font != null)
                {
                    Set(font, StepState.Running, "Downloading from Microsoft");
                    var (ok, d) = SetupChecks.InstallIconFont();
                    Set(font, ok ? StepState.Done : StepState.Failed, ok ? d : "Couldn't install it: " + d + " (icons may show as boxes)");
                    if (!ok) anyFailed = true;
                }

                string runSetup = setupExe;
                if (installed != null && action == Action.Update && latestOnline != null && latestOnline > mine)
                {
                    Set(island, StepState.Running, $"Downloading v{Services.Updater.Short(latestOnline)} from GitHub");
                    var (file, err) = SetupChecks.DownloadVerified(onlineUrl, onlineSha, latestOnline);
                    if (file == null) { Set(island, StepState.Running, "Download failed (" + err + "); installing this version instead"); }
                    else runSetup = file;
                }
                Set(island, StepState.Running, "Copying files");
                var (code, output) = SetupChecks.Run(runSetup, "/silent /nolaunch" + (optAutostart ? "" : " /noautostart"), 5 * 60 * 1000);
                if (code == 0) { Set(island, StepState.Done, optAutostart ? "Installed; starts when you sign in" : "Installed"); exitCode = 0; }
                else { Set(island, StepState.Failed, "Setup failed (" + code + "); details in setup.log"); exitCode = 1; anyFailed = true; }

                if (ev != null)
                {
                    if (!wingetOk)
                    {
                        Win32.ShellExecute("https://www.voidtools.com/downloads/");
                        Set(ev, StepState.Skipped, "Opened its download page in your browser");
                    }
                    else
                    {
                        Set(ev, StepState.Running, "Installing through Windows Package Manager; approve the administrator prompt");
                        var (ok, d) = SetupChecks.InstallEverything();
                        Set(ev, ok ? StepState.Done : StepState.Failed, ok ? d : "Not installed: " + d);
                        if (!ok) anyFailed = true;
                    }
                }

                if (mp != null)
                {
                    bool skipped = false;
                    mp.Skip = () => skipped = true;
                    Set(mp, StepState.Running, "Finish in the Microsoft Store window (buy or install there)");
                    Win32.ShellExecute("ms-windows-store://pdp/?productid=" + SetupChecks.MagicPodsStoreId);
                    var until = DateTime.Now.AddMinutes(15);
                    while (!skipped && DateTime.Now < until && !SetupChecks.MagicPodsInstalled()) Thread.Sleep(2000);
                    mp.Skip = null;
                    if (SetupChecks.MagicPodsInstalled()) Set(mp, StepState.Done, "Installed");
                    else Set(mp, StepState.Skipped, skipped ? "Skipped: the island reads your AirPods itself" : "Not installed yet: the island reads your AirPods itself");
                }
            }
            catch (Exception ex) { Log.E("wizard", "install failed", ex); anyFailed = true; if (exitCode != 0) exitCode = 1; }
            installing = false; finished = true;
            Ui.Post(() => dirty = true);
        });
    }
}
