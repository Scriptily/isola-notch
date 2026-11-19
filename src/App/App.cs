using System.Runtime.InteropServices;
using Isola.Island;
using Isola.Native;
using Isola.Render;
using Isola.Services;
using Isola.Shell;
using Microsoft.Win32;

namespace Isola.App;

/// <summary>Wires the windows, the services and the island together, and runs the loop.</summary>
public sealed unsafe partial class App
{
    public const uint WM_SHOW_EXISTING = Win32.WM_APP + 2;
    /// <summary>Posted by the installer/updater: save and exit cleanly.</summary>
    public const uint WM_QUIT_ISLAND = Win32.WM_APP + 9;
    const uint WM_TRAY = Win32.WM_APP + 3;
    const uint WM_APPBAR = Win32.WM_APP + 4;
    const int HK_SEARCH = 1, HK_TOGGLE = 2;

    readonly string[] args;
    Config cfg;
    Host host;
    Island.Island island;
    readonly Canvas canvas = new();
    readonly Hub svc = new();
    DisplayWatcher displays;
    FullscreenDetector fullscreen;
    readonly IClock clock = Ui.Clock;
    bool quit;
    bool hotkeysOn;
    string hotkeyProblem = "";
    long fsTimer, fsPoll;
    nint[] hooks = Array.Empty<nint>();
    static App current;
    double lastFrame, nextAmbient;
    readonly RevealIntent reveal = new();
    nint lastLayout;
    bool rawOn;
    SettingsWindow settings;
    /// <summary>Test harness only: where windows write their click targets (see Ux.DumpFile).</summary>
    public static readonly string ZoneDir = Environment.GetEnvironmentVariable("ISOLA_ZONES") is { Length: > 0 } z ? Directory.CreateDirectory(z).FullName : null;
    FileSystemWatcher cfgWatch;
    double ignoreCfgUntil;
    public string HotkeyProblem => hotkeyProblem;

    public App(string[] a) { args = a; current = this; }

    public int Run()
    {
        cfg = Config.Load(Paths.Config);
        Log.MinLevel = cfg.VerboseLog ? LogLevel.Debug : LogLevel.Info;
        Log.I("app", $"Isola {typeof(App).Assembly.GetName().Version} starting · home {Paths.Home} · {Environment.OSVersion.VersionString} · .NET {Environment.Version}");
        if (cfg.LoadError != null) Log.W("config", "config file was unreadable, running on defaults (kept as .broken): " + cfg.LoadError);
        // Write it back normalized, so every setting (including ones added in a
        // newer version) is present in the file to read and hand-edit.
        if (cfg.LoadError == null) { try { bool had = File.Exists(Paths.Config); cfg.Save(Paths.Config); if (!had) Log.I("config", "wrote default config to " + Paths.Config); } catch (Exception ex) { Log.W("config", "couldn't write config: " + ex.Message); } }

        Win32.OleInitialize(0);
        Gfx.Init(args.Contains("--warp"));

        host = new Host();
        host.Create();
        Ui.Init(host.Input);
        host.SystemMessage = OnSystemMessage;
        host.OpenSettingsRequested = OpenSettings;
        host.ContextMenuRequested = (x, y) => Menu(x, y);

        island = new Island.Island(cfg, host, clock) { Svc = svc };
        if (ZoneDir != null) { island.Ux.DumpFile = Path.Combine(ZoneDir, "island.txt"); island.Ux.Origin = () => (host.Left, host.Top, host.Scale); }
        host.Island = island;
        island.SettingsChanged = SaveConfig;

        fullscreen = new FullscreenDetector(new RealWindows());
        fullscreen.Ours.Add(host.Visual); fullscreen.Ours.Add(host.Input);

        StartServices();

        displays = new DisplayWatcher(new RealDisplays()) { ShowOn = () => cfg.ShowOn };
        displays.LaptopChanged += OnLaptopChanged;
        displays.DisplayChanged += OnDisplayChanged;
        displays.Refresh("startup");
        OnLaptopChanged();

        HookWindows();
        RegisterAppBar();
        Tray(true);
        SyncAutostart();
        WatchConfig();
        UpdateHotkeys();
        UpdateRawInput();
        Win32.AddClipboardFormatListener(host.Input);
        Win32.WTSRegisterSessionNotification(host.Input, 0);
        EvaluateFullscreen("startup");

        island.Start();
        // --settings [section]: open Settings at start (a shortcut, or for screenshots)
        int si = Array.IndexOf(args, "--settings");
        if (si >= 0) { string sec = si + 1 < args.Length && !args[si + 1].StartsWith("--") ? args[si + 1] : null; Ui.After(1.5, () => host.OpenSettings(sec)); }
        if (Environment.GetEnvironmentVariable("ISOLA_SHOWCASE") is { Length: > 0 } show)
            Ui.After(2.5, () =>
            {
                // "popups" for all of them, "popups:0,11,13" for some, by their place in the list
                // "a;b@4;c@9": a now, then b at 4 s and c at 9 s (a recording that moves between states)
                if (show.Contains(';'))
                {
                    foreach (var part in show.Split(';'))
                    {
                        int at = part.LastIndexOf('@');
                        double when = at > 0 && double.TryParse(part[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 0;
                        string st = at > 0 ? part[..at] : part;
                        Ui.After(when, () => island.Showcase(st));
                    }
                    return;
                }
                if (!show.StartsWith("popups")) { island.Showcase(show); return; }
                var all = Alerts.Samples(key => island.DismissAlert(key), null, Col.Hex(0x8b5cf6));
                var pick = show.Contains(':') ? show[7..].Split(',').Select(int.Parse).Where(i => i < all.Count).Select(i => all[i]) : all;
                foreach (var a in pick) island.Alert(a);
            });
        if (args.Contains("--updated")) Ui.After(3.8, () => island.Alert(Alerts.Updated(Updater.Short(svc.Updates.Current))));
        diagTimer = Ui.After(30, Diagnostics);
        Log.I("app", "ready");
        Loop();
        Shutdown();
        return 0;
    }

    // ======================================================================
    // services
    // ======================================================================

    void StartServices()
    {
        svc.Audio = new AudioService();
        svc.Audio.Start();
        Sounds.OutputIsBluetooth = () => AudioService.IsBluetoothOutput(svc.Audio.Bus, svc.Audio.ContainerId, svc.Audio.DeviceName, svc.Bluetooth?.Devices);
        Sounds.OutputAwake = () => svc.Audio.PeakNow() > 0.0005f;
        svc.Audio.VolumeChanged += (m, v) => { if (cfg.VolumeHud) island.Hud(m ? HudKind.Mute : HudKind.Volume, v); };
        svc.Audio.OutputChanged += name => island.Alert(Alerts.Output(name));

        // recordings for the README and the Store (development only): real tracks shown, nothing played
        if (Environment.GetEnvironmentVariable("ISOLA_SHOWCASE_MEDIA") is { Length: > 0 } list && File.Exists(list))
        {
            var show = new ShowcaseMedia(list, clock);
            svc.Media = show;
            show.Changed += () => { island.ActivityChanged(); island.UpdateGlowColors(); };
            show.TrackChanged += (o, n) => island.TrackChanged(o, n);
        }
        else
        {
            var media = new MediaService(clock, svc.Audio) { Ignored = () => cfg.MediaIgnoredApps };
            svc.Media = media;
            media.Changed += () => { island.ActivityChanged(); island.UpdateGlowColors(); };
            media.TrackChanged += (o, n) => island.TrackChanged(o, n);
            if (cfg.Media) _ = media.StartAsync();
        }

        svc.Timer = new CountdownTimer(clock) { File = Paths.TimerFile };
        svc.Timer.Changed += () => island.ActivityChanged();
        svc.Timer.Finished += TimerDone;
        svc.Timer.Load();

        svc.Battery = new BatteryService { LowThreshold = () => cfg.LowBatteryPercent };
        svc.Battery.Refresh();
        svc.Battery.Event += e => { if (cfg.BatteryAlerts) BatteryAlert(e); };

        svc.Notes = new NotesStore(Paths.Notes);
        svc.Shelf = new ShelfStore(Paths.Shelf);
        svc.Shelf.Changed += () => host.RequestFrame();
        svc.Notes.Changed += () => host.RequestFrame();
        svc.Clipboard = new ClipboardHistory { Max = cfg.ClipboardHistory };
        if (cfg.Clipboard) svc.Clipboard.Start();
        svc.Clipboard.Changed += () => host.RequestFrame();

        svc.Ha = new HomeAssistant(clock) { BaseUrl = () => cfg.HaBaseUrl, Token = () => cfg.HaToken, Wanted = () => cfg.HaEntities };
        svc.Ha.Changed += () => host.RequestFrame();

        svc.Spotify = new SpotifyService(clock) { ClientId = () => cfg.SpotifyClientId, TokenFile = Paths.SpotifyToken };
        svc.Spotify.Load();
        svc.Spotify.Changed += () => { host.RequestFrame(); settings?.Invalidate(); };
        svc.Weather = new WeatherService(clock, svc.Ha) { Cfg = () => cfg };
        svc.Weather.Changed += () => host.RequestFrame();
        if (cfg.Weather) svc.Weather.Start();

        // ISOLA_NO_EVERYTHING=1 (testing): behave as if Everything weren't running
        var ev = new EverythingSearch(host.Input);
        svc.Search = Environment.GetEnvironmentVariable("ISOLA_NO_EVERYTHING") == "1" ? new FileSearch(ev, null, () => false) : new FileSearch(ev);
        Log.I("search", svc.Search.UsingEverything ? "Everything is running: search through it" : "Everything isn't running: the built-in index searches apps and your folders");
        // built ahead of the first search, after startup has settled
        Ui.After(20, () => svc.Search.Warm());

        svc.Bluetooth = new BluetoothService(clock);
        svc.Bluetooth.RadioChanged += on => { if (cfg.BluetoothAlerts) island.Alert(Alerts.BluetoothRadio(on)); };
        svc.Bluetooth.DeviceChanged += OnBtDevice;
        svc.Bluetooth.Changed += () => host.RequestFrame();
        _ = svc.Bluetooth.StartAsync();

        svc.AirPods = new AirPodsService(clock)
        {
            UseMagicPods = () => cfg.UseMagicPods,
            AirPodsConnected = () => svc.Bluetooth.Devices.Any(d => d.IsAirPods && d.Connected),
        };
        svc.AirPods.Changed += () => host.RequestFrame();
        svc.AirPods.Event += OnAirPodsEvent;
        svc.Bluetooth.Changed += () => { if (cfg.AirPods) svc.AirPods.DevicesChanged(); };
        if (cfg.AirPods) svc.AirPods.Start();

        svc.Updates = new Updater(clock) { Cfg = () => cfg };
        svc.Feedback = new Feedback(Paths.FeedbackState);
        svc.Tools = new DownloadTools(Paths.DownloadTools, clock) { Enabled = () => cfg.Downloads };
        svc.Downloads = new Downloads(Paths.DownloadsHistory, svc.Tools) { Cfg = () => cfg, Shelf = svc.Shelf };
        int downloading = 0;
        svc.Downloads.Changed += () =>
        {
            island.Host.RequestFrame();
            // a download starting or the last one ending changes the island's live activity
            int now = svc.Downloads.ActiveCount;
            if ((now > 0) != (downloading > 0)) island.ActivityChanged();
            downloading = now;
        };
        svc.Tools.Changed += () => { island.Host.RequestFrame(); settings?.Invalidate(); };
        svc.Downloads.Finished += job => island.Alert(job.State == "done"
            ? Alerts.DownloadDone(job, () => Win32.ShellExecute(job.Output), () => Services.Shell.RevealInExplorer(job.Output))
            : Alerts.DownloadFailed(job, () => { try { svc.Downloads.Retry(job); } catch (Exception ex) { Log.W("downloads", ex.Message); } }));
        svc.Tools.Start();
        svc.Updates.Found += v => island.Alert(Alerts.UpdateFound(Updater.Short(v)));
        bool updating = false, fetching = false;
        // starting or finishing changes the island's live activity (a redraw alone kept the song on it)
        svc.Updates.Changed += () => { island.Host.RequestFrame(); if ((svc.Updates.Phase != null) != updating) { updating = !updating; island.ActivityChanged(); } };
        svc.Tools.Changed += () => { if ((svc.Tools.Fetching != null) != fetching) { fetching = !fetching; island.ActivityChanged(); } };
        svc.Tools.Fetched += got => { if (cfg.Downloads) island.Alert(Alerts.ToolsFetched(got)); };
        // a moment of "Installing v..." on the island first (the setup waits for Isola to close anyway)
        svc.Updates.Installing += v => { Log.I("update", "handing over to the installer"); island.Host.RequestFrame(); Ui.After(Updater.ShowInstallingFor, () => { Log.Flush(); quit = true; }); };
        svc.Updates.Start();

        // a previous run that never put Windows' banners back (power loss, both
        // it and its watcher killed): put them back before anything else
        WindowsBanners.RestoreJournal(new RegistryBanners(), Paths.BannerJournal);
        banners = new WindowsBanners(new RegistryBanners(), Paths.BannerJournal) { Exempt = a => WindowsBanners.DefaultExempt(a) || bannerExempt.Contains(a) };
        Ui.After(1.0, BannerTick);
        // the watcher runs always: it also brings the island back after a crash
        Ui.After(2.0, StartBannerGuard);

        svc.Notifications = new NotificationFeed(clock);
        svc.Notifications.Arrived += OnNotification;
        // gone from Windows (dismissed there, or the call ended): gone from the island too, queued or showing
        svc.Notifications.Removed += n => island.DismissAlert((n.IsCall ? "call:" : "notif:") + n.Id);
        island.OpenNotificationCenter = () => Win32.ShellExecute("ms-actioncenter:");
        island.PreviewPopups = PreviewPopups;
        // the chosen sounds, built in the background now so the first one plays without a wait
        { var c0 = cfg; Task.Run(() => { foreach (var k in Sounds.All) Sounds.Warm(k, ToneFor(c0, k)); }); }
        island.SoundOn = a => { if (SoundAllowed(cfg, a)) Sounds.Play(a.Sound, SoundVolumeOf(cfg), ToneFor(cfg, a.Sound)); };
        island.SoundOff = a => { if (a.Sound != null && Sounds.Loops(a.Sound)) Sounds.Stop(); };
        if (FeedWanted(cfg)) _ = svc.Notifications.StartAsync();
    }

    /// <summary>
    /// Phone Link calls arrive as notifications, so the feed runs for either
    /// switch. It used to start only for "Notifications", which is off by
    /// default: with just "Phone calls" on, no call ever reached the island.
    /// </summary>
    internal static bool FeedWanted(Config c) => c.Notifications || c.Calls;

    // ---- Windows' own banners ------------------------------------------------
    WindowsBanners banners;
    readonly HashSet<string> bannerExempt = new(StringComparer.OrdinalIgnoreCase);
    bool bannerRescan, guardStarted;
    double bannerWantSince = -1, bannerScannedAt;

    static IEnumerable<string> BannerKeys(NotificationItem n)
    {
        if (!string.IsNullOrEmpty(n.AppId)) yield return n.AppId;
        if (n.Bundle != null) yield return PhoneLink.Package + "!YourPhoneNotifications_" + n.Bundle;
    }

    // The Store version can't change Windows' real notification settings: its
    // registry writes go to a private copy (Microsoft didn't grant the capability
    // that turns that off), so Settings sends people to Windows' own page instead.
    internal static bool BannersWanted(Config c, bool islandCanShow) => c.Notifications && c.HideWindowsBanners && islandCanShow && !Packaging.IsStore;

    /// <summary>
    /// Every second: banners off only while the island can show notifications.
    /// Back on at once when it can't; off again only after it has been able to
    /// for 2 s, so a flickering fullscreen state doesn't churn the registry.
    /// </summary>
    void BannerTick()
    {
        try
        {
            bool want = BannersWanted(cfg, island.CanShowAlerts);
            double now = clock.Now;
            if (!want) { bannerWantSince = -1; if (banners.Active) banners.Restore(island.CanShowAlerts ? "setting off" : "the island can't show alerts right now"); }
            else
            {
                if (bannerWantSince < 0) bannerWantSince = now;
                bool scan = !banners.Active ? now - bannerWantSince >= 2 : bannerRescan || now - bannerScannedAt > 60;
                if (scan)
                {
                    StartBannerGuard();
                    banners.Suppress();
                    bannerScannedAt = now; bannerRescan = false;
                }
            }
        }
        catch (Exception ex) { Log.E("banners", "banner update failed", ex); }
        Ui.After(1.0, BannerTick);
    }

    void StartBannerGuard()
    {
        if (guardStarted || Environment.ProcessPath == null) return;
        guardStarted = true;
        // Two hops: a launcher that starts the watcher and exits at once, so the
        // watcher has no living parent and "End process tree" on the island
        // can't reach it. Each hop leaves the island's job object where allowed:
        // a tool that ended the island's job took the watcher with it, and the
        // banners stayed off until the next start restored them.
        uint pid = Win32.StartDetached(Environment.ProcessPath, $"--banner-guard-launch {Environment.ProcessId}");
        if (pid != 0) Log.I("banners", "watcher started: Windows' popups come back and a crash restarts the island");
        else Log.W("banners", "couldn't start the watcher; the next start restores anything left");
    }

    void BatteryAlert(string e)
    {
        if (e is "plugged" or "unplugged" or "low" or "full") island.Alert(Alerts.Battery(e, svc.Battery.Percent));
    }

    void OnBtDevice(BtDevice d, bool connected)
    {
        if (!cfg.BluetoothAlerts) return;
        island.Alert(Alerts.Device(d.Id, d.Name, d.Glyph, connected, d.Battery, d.IsAirPods && cfg.AirPods ? svc.AirPods?.State : null));
    }

    void OnAirPodsEvent(string e)
    {
        if (!cfg.AirPods) return;
        if (e == "lid-open")
        {
            // the iPhone-style "your AirPods" card when the case opens
            island.Alert(Alerts.PodsCase(svc.AirPods.State));
        }
    }

    void OnNotification(NotificationItem n)
    {
        if (n.IsCall && cfg.Calls)
        {
            island.Alert(Alerts.Call(n.Id.ToString(), n.Caller.Length > 0 ? n.Caller : n.Title, n.Icon, () => CallButton(false), () => CallButton(true), () => Win32.ShellExecute("ms-phone:")));
            return;
        }
        if (!cfg.Notifications) return;
        // a phone app can be muted by its own name, or all of them as "Phone Link"
        if (cfg.NotificationMutedApps.Any(m => !string.IsNullOrWhiteSpace(m) && (n.App.Contains(m, StringComparison.OrdinalIgnoreCase) || n.Via.Contains(m, StringComparison.OrdinalIgnoreCase))))
        {
            // muted here, so Windows shows it: its banners come back from now on
            foreach (var key in BannerKeys(n)) { bannerExempt.Add(key); banners?.Release(key); }
            return;
        }
        // an app Windows hadn't seen before has a settings key only now
        if (banners is { Active: true } && BannerKeys(n).Any(k => !bannerExempt.Contains(k))) bannerRescan = true;
        var alert = Alerts.Notification(n.Id.ToString(), n.Via.Length > 0 ? $"{n.App} · {n.Via}" : n.App, n.Title, cfg.NotificationPreviews ? n.Body : "", n.Icon, cfg.AlertSeconds);
        island.Alert(Alerts.OfferCode(alert, OneTimeCode.Find(n.Title, n.Body), cfg.NotificationPreviews, CopyCode));
    }

    /// <summary>Copies a one-time code, kept out of clipboard history (Isola's and Windows') and cloud sync.</summary>
    void CopyCode(string code)
    {
        Win32.SetClipboardSecret(code);
        island.Alert(Alerts.Copied());
    }

    /// <summary>
    /// Phone Link owns the call. Answer/Decline press the matching button on its
    /// incoming-call notification through UI Automation; if that window cannot
    /// be found, Phone Link is brought forward instead.
    /// </summary>
    void CallButton(bool answer)
    {
        if (!PhoneLink.PressCallButton(answer)) Win32.ShellExecute("ms-phone:");
    }

    // ======================================================================
    // displays and fullscreen
    // ======================================================================

    void OnLaptopChanged()
    {
        var lap = displays.Laptop;
        host.Place(lap, cfg.Scale);
        island.SetLaptopPresent(lap != null);
        EvaluateFullscreen("display change");
    }

    void OnDisplayChanged(DisplayPath p, bool connected)
    {
        if (!cfg.DisplayAlerts) return;
        string name = string.IsNullOrEmpty(p.FriendlyName) ? p.Connector + " display" : p.FriendlyName;
        string detail = p.Width > 0 ? $"{p.Connector} · {p.Width}×{p.Height}{(p.Hz > 0 ? $" @ {p.Hz:0} Hz" : "")}" : p.Connector;
        island.Alert(Alerts.Display(name, detail, connected));
    }

    void HookWindows()
    {
        // Out-of-context WinEvents arrive as messages on this thread: no DLL is
        // injected anywhere, and nothing here can slow other apps down.
        var ranges = new (uint, uint)[]
        {
            (Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND),
            (Win32.EVENT_SYSTEM_MOVESIZEEND, Win32.EVENT_SYSTEM_MOVESIZEEND),
            (Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND),
            (Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED),
            (Win32.EVENT_OBJECT_SHOW, Win32.EVENT_OBJECT_HIDE),
        };
        var list = new List<nint>();
        foreach (var (a, b) in ranges)
        {
            nint h = Win32.SetWinEventHook(a, b, 0, &WinEvent, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
            if (h != 0) list.Add(h); else Log.W("app", $"WinEvent hook {a:X} failed");
        }
        hooks = list.ToArray();
    }

    [UnmanagedCallersOnly]
    static void WinEvent(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time)
    {
        if (obj != Win32.OBJID_WINDOW || child != 0 || hwnd == 0) return;
        var app = current;
        if (app == null) return;
        if (ev is Win32.EVENT_OBJECT_SHOW or Win32.EVENT_OBJECT_HIDE)
        {
            // show/hide fire for every control; only top-level windows matter
            if (Win32.GetAncestor(hwnd, 2 /*GA_ROOT*/) != hwnd) return;
        }
        try { app.OnWinEvent(ev, hwnd); } catch (Exception ex) { Log.E("app", "WinEvent handler failed", ex); }
    }

    void OnWinEvent(uint ev, nint hwnd)
    {
        if (ev == Win32.EVENT_SYSTEM_FOREGROUND)
        {
            // something else came forward: if it is topmost too, step back in front of it
            if (cfg.KeepOnTop && hwnd != host.Input) host.Reassert();
            UpdateHotkeys();
            UpdateRawInput();
            CheckLayout();
        }
        // coalesce bursts (a window opening fires several events) into one check
        if (fsTimer == 0) fsTimer = Ui.After(0.12, () => { fsTimer = 0; EvaluateFullscreen("window event"); });
    }

    void RegisterAppBar()
    {
        // An app bar is told by the shell when a fullscreen app opens or closes
        // (ABN_FULLSCREENAPP) - the same signal the taskbar uses to get out of
        // the way. It is a hint; the Z-order check decides.
        var abd = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = host.Input, uCallbackMessage = WM_APPBAR };
        Win32.SHAppBarMessage(Win32.ABM_NEW, ref abd);
    }

    void EvaluateFullscreen(string why)
    {
        var lap = displays?.Laptop;
        if (lap == null) return;
        nint fs = fullscreen.FullscreenOn(lap.Bounds);
        bool on = fs != 0;
        if (on != island.Fullscreen)
        {
            string what = on ? $"{Win32.ClassName(fs)} '{Trim(Win32.WindowText(fs))}' (pid {Pid(fs)})" : why;
            island.SetFullscreen(on, what);
        }
        // a slow safety net for transitions no event announces (F11 in some apps)
        Ui.Cancel(ref fsPoll);
        fsPoll = Ui.After(on ? 1.5 : 2.5, () => EvaluateFullscreen("poll"));
    }

    static string Trim(string s) => s.Length > 40 ? s[..40] + "…" : s;
    static uint Pid(nint h) { Win32.GetWindowThreadProcessId(h, out uint p); return p; }

    // ======================================================================
    // hotkeys, raw keyboard (caps lock), layout
    // ======================================================================

    void UpdateHotkeys()
    {
        bool fsFg = cfg.ReleaseHotkeysInFullscreen && fullscreen.ForegroundFullscreen(h => Win32.MonitorRect(Win32.MonitorFromWindow(h, Win32.MONITOR_DEFAULTTONEAREST)));
        bool want = !fsFg;
        if (want == hotkeysOn) return;
        if (want)
        {
            var problems = new List<string>();
            if (cfg.Search && Hotkey.TryParse(cfg.SearchHotkey, out uint m1, out uint k1) && !Win32.RegisterHotKey(host.Input, HK_SEARCH, m1 | Win32.MOD_NOREPEAT, k1))
                problems.Add($"{cfg.SearchHotkey} is already used by another app");
            if (Hotkey.TryParse(cfg.ToggleHotkey, out uint m2, out uint k2) && !Win32.RegisterHotKey(host.Input, HK_TOGGLE, m2 | Win32.MOD_NOREPEAT, k2))
                problems.Add($"{cfg.ToggleHotkey} is already used by another app");
            hotkeyProblem = string.Join("; ", problems);
            if (problems.Count > 0) Log.W("keys", hotkeyProblem);
            else Log.D("keys", $"hotkeys on: search {cfg.SearchHotkey}, toggle {cfg.ToggleHotkey}");
        }
        else
        {
            Win32.UnregisterHotKey(host.Input, HK_SEARCH); Win32.UnregisterHotKey(host.Input, HK_TOGGLE);
            Log.D("keys", "fullscreen app in front: hotkeys released so the game/video gets every key");
        }
        hotkeysOn = want;
    }

    public void ReloadHotkeys()
    {
        Win32.UnregisterHotKey(host.Input, HK_SEARCH); Win32.UnregisterHotKey(host.Input, HK_TOGGLE);
        hotkeysOn = false; UpdateHotkeys();
    }

    void UpdateRawInput()
    {
        // Caps Lock / layout HUDs need to see keystrokes. Raw input is async
        // (never delays a key, unlike a keyboard hook) and is switched off
        // entirely while a fullscreen app is in front.
        bool want = (cfg.CapsLockHud || cfg.KeyboardLayoutHud) && !(island?.Fullscreen ?? false) && !fullscreen.ForegroundFullscreen(h => Win32.MonitorRect(Win32.MonitorFromWindow(h, Win32.MONITOR_DEFAULTTONEAREST)));
        if (want == rawOn) return;
        var dev = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = want ? Win32.RIDEV_INPUTSINK : Win32.RIDEV_REMOVE, hwndTarget = want ? host.Input : 0 };
        if (Win32.RegisterRawInputDevices(&dev, 1, (uint)sizeof(RAWINPUTDEVICE))) rawOn = want;
    }

    void OnRawInput(nint l)
    {
        uint size = 0;
        Win32.GetRawInputData(l, Win32.RID_INPUT, null, ref size, (uint)sizeof(RAWINPUTHEADER));
        if (size == 0 || size > 256) return;
        byte* buf = stackalloc byte[(int)size];
        if (Win32.GetRawInputData(l, Win32.RID_INPUT, buf, ref size, (uint)sizeof(RAWINPUTHEADER)) != size) return;
        var hdr = (RAWINPUTHEADER*)buf;
        if (hdr->dwType != Win32.RIM_TYPEKEYBOARD) return;
        var kb = (RAWKEYBOARD*)(buf + sizeof(RAWINPUTHEADER));
        bool up = (kb->Flags & 1) != 0;
        if (kb->VKey == 0x14 && !up && cfg.CapsLockHud)
        {
            // the toggle state flips after the key is processed
            Ui.After(0.05, () => island.Hud(HudKind.CapsLock, (Win32.GetKeyState(0x14) & 1) != 0 ? 1 : 0));
        }
        if (up && cfg.KeyboardLayoutHud && (kb->VKey is 0x20 or 0x10 or 0xA0 or 0xA1 or 0x12 or 0x5B or 0x5C)) Ui.After(0.25, CheckLayout);
    }

    void CheckLayout()
    {
        if (!cfg.KeyboardLayoutHud) return;
        nint fg = Win32.GetForegroundWindow();
        nint hkl = Win32.GetKeyboardLayout(Win32.GetWindowThreadProcessId(fg, out _));
        if (hkl == 0) return;
        if (lastLayout == 0) { lastLayout = hkl; return; }
        if (hkl == lastLayout) return;
        // switching apps can show a different per-app layout; only announce real switches
        bool changedLang = ((long)hkl & 0xFFFF) != ((long)lastLayout & 0xFFFF);
        lastLayout = hkl;
        if (changedLang) island.Hud(HudKind.Layout, 0, LayoutName(hkl));
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int LCIDToLocaleName(uint lcid, char* name, int len, uint flags);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetLocaleInfoEx(string locale, uint type, char* data, int len);

    static string LayoutName(nint hkl)
    {
        uint lcid = (uint)((long)hkl & 0xFFFF);
        char* name = stackalloc char[85];
        if (LCIDToLocaleName(lcid, name, 85, 0) <= 0) return "Keyboard layout changed";
        string loc = new(name);
        char* disp = stackalloc char[128];
        int n = GetLocaleInfoEx(loc, 0x00000002 /*LOCALE_SLOCALIZEDDISPLAYNAME*/, disp, 128);
        return n > 1 ? new string(disp, 0, n - 1) : loc;
    }

    // ======================================================================
    // system messages
    // ======================================================================

    void OnSystemMessage(uint m, nint w, nint l)
    {
        switch (m)
        {
            case Win32.WM_HOTKEY:
                if (w == HK_SEARCH) { Log.D("keys", "search hotkey"); island.OpenSearch(); }
                else if (w == HK_TOGGLE) { Log.D("keys", "toggle hotkey"); island.Toggle(); }
                break;
            case Win32.WM_COPYDATA:
                svc.Search?.OnCopyData((COPYDATASTRUCT*)l, w);   // wParam: the sending window
                break;
            case Win32.WM_DISPLAYCHANGE:
            case Win32.WM_DPICHANGED:
                Ui.After(0.3, () => displays.Refresh(m == Win32.WM_DPICHANGED ? "dpi change" : "display change"));
                break;
            case Win32.WM_SETTINGCHANGE:
                Ui.After(0.5, () => displays.Refresh("setting change"));
                break;
            case Win32.WM_POWERBROADCAST:
                if (w == (nint)Win32.PBT_APMPOWERSTATUSCHANGE) svc.Battery.Refresh();
                else if (w == (nint)Win32.PBT_APMRESUMEAUTOMATIC) { Log.I("app", "resumed from sleep"); Ui.After(1.5, () => { displays.Refresh("resume"); svc.Battery.Refresh(); host.RequestFrame(); }); }
                else if (w == (nint)Win32.PBT_APMSUSPEND) Log.I("app", "going to sleep");
                break;
            case Win32.WM_INPUT:
                OnRawInput(l);
                break;
            case Win32.WM_CLIPBOARDUPDATE:
                if (cfg.Clipboard) svc.Clipboard.OnUpdate();
                break;
            case WM_APPBAR:
                if (w == (nint)Win32.ABN_FULLSCREENAPP) { Log.D("app", $"shell: fullscreen app {(l != 0 ? "opened" : "closed")}"); EvaluateFullscreen("shell notice"); }
                break;
            case WM_TRAY:
                int ev = Win32.LoWord(l);
                if (ev == 0x205 /*RBUTTONUP*/ || ev == 0x7B) { Win32.GetCursorPos(out var p); Menu(p.X, p.Y); }
                else if (ev == 0x202 /*LBUTTONUP*/) { if (island.Mode == Mode.Hidden) island.Reveal("tray"); else island.Expand(); }
                break;
            case WM_QUIT_ISLAND:
                Log.I("app", "asked to quit (installer or updater)");
                quit = true;
                break;
            case WM_SHOW_EXISTING:
                island.Reveal("second launch"); if (island.Mode == Mode.Rest) island.Expand();
                break;
            case Win32.WM_WTSSESSION_CHANGE:
                if (w == (nint)Win32.WTS_SESSION_UNLOCK) { Log.I("app", "session unlocked"); Ui.After(0.8, () => { displays.Refresh("unlock"); host.RequestFrame(); }); }
                break;
            case Win32.WM_ENDSESSION:
                if (w != 0) { banners?.Restore("signing out"); Log.I("app", "Windows is shutting down"); Log.Flush(); }
                break;
        }
    }

    // ======================================================================
    // tray, menu, settings, autostart
    // ======================================================================

    void Tray(bool add)
    {
        var nid = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = host.Input, uID = 1, uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP, uCallbackMessage = WM_TRAY };
        if (add)
        {
            nid.hIcon = Win32.LoadImageW(Win32.GetModuleHandleW(null), "#32512", 1, 16, 16, 0);
            if (nid.hIcon == 0) nid.hIcon = LoadIconFile();
            string tip = "Isola";
            for (int i = 0; i < tip.Length; i++) nid.szTip[i] = tip[i];
            Win32.Shell_NotifyIconW(Win32.NIM_ADD, &nid);
            nid.uVersion = 4; Win32.Shell_NotifyIconW(Win32.NIM_SETVERSION, &nid);
        }
        else Win32.Shell_NotifyIconW(Win32.NIM_DELETE, &nid);
    }

    static nint LoadIconFile()
    {
        string ico = Path.Combine(AppContext.BaseDirectory, "assets", "island.ico");
        return File.Exists(ico) ? Win32.LoadImageW(0, ico, 1, 16, 16, 0x10 /*LR_LOADFROMFILE*/) : 0;
    }

    void Menu(int x, int y)
    {
        nint m = Win32.CreatePopupMenu();
        Win32.AppendMenuW(m, 0, 1, island.Mode == Mode.Hidden ? "Show island" : "Hide island");
        Win32.AppendMenuW(m, 0, 2, "Settings…");
        Win32.AppendMenuW(m, 0, 3, "Search\t" + cfg.SearchHotkey);
        Win32.AppendMenuW(m, 0x800, 0, null);
        Win32.AppendMenuW(m, cfg.Style == "pill" ? 8u : 0u, 4, "Style: Island (pill)");
        Win32.AppendMenuW(m, cfg.Style == "notch" ? 8u : 0u, 5, "Style: Notch");
        Win32.AppendMenuW(m, 0x800, 0, null);
        Win32.AppendMenuW(m, 0, 6, "Open log");
        Win32.AppendMenuW(m, 0, 7, "Restart");
        Win32.AppendMenuW(m, 0, 8, "Quit");
        Win32.SetForegroundWindow(host.Input);
        int cmd = Win32.TrackPopupMenuEx(m, 0x100 | 0x2, x, y, host.Input, 0);
        Win32.DestroyMenu(m);
        switch (cmd)
        {
            case 1: island.Toggle(); break;
            case 2: OpenSettings(null); break;
            case 3: island.OpenSearch(); break;
            case 4: case 5: cfg.Style = cmd == 4 ? "pill" : "notch"; SaveConfig(); island.ApplyConfig(cfg); break;
            case 6: Log.Flush(); Win32.ShellExecute(Paths.LogFile); break;
            case 7: Restart(); break;
            case 8: quit = true; break;
        }
    }

    /// <summary>
    /// Stops Isola for this session, as the tray menu's Quit does: a clean exit,
    /// so the watcher doesn't bring it back, and autostart is left as it is.
    /// </summary>
    public void Quit(string why)
    {
        Log.I("app", "quitting: " + why);
        quit = true;
        Win32.PostMessageW(host.Input, 0, 0, 0);   // wake the loop if it's waiting
    }

    void Restart()
    {
        Log.I("app", "restarting");
        quit = true;
        restart = true;
    }
    bool restart;

    void OpenSettings(string section)
    {
        if (settings == null || !settings.Alive)
        {
            settings = new SettingsWindow(this, svc, () => cfg, ApplySettings);
        }
        // every sound ready before it's picked, so its preview plays at once
        Task.Run(Sounds.WarmAll);
        if (section != null) settings.GoTo(section);
        settings.Show(displays.Laptop);
    }

    public void ApplySettings(Config c)
    {
        bool mediaWas = cfg.Media, feedWas = FeedWanted(cfg), weatherWas = cfg.Weather;
        string showWas = cfg.ShowOn;
        cfg = c;
        SaveConfig();
        Log.MinLevel = cfg.VerboseLog ? LogLevel.Debug : LogLevel.Info;
        island.ApplyConfig(cfg);
        if (cfg.ShowOn != showWas) displays.Refresh("screen setting"); else host.Place(displays.Laptop, cfg.Scale);
        svc.Clipboard.Max = cfg.ClipboardHistory;
        if (cfg.Media && !mediaWas) _ = ((MediaService)svc.Media).StartAsync();
        if (FeedWanted(cfg) && !feedWas) _ = svc.Notifications.StartAsync();
        if (!FeedWanted(cfg) && feedWas) svc.Notifications.Stop();
        if (cfg.Weather && !weatherWas) svc.Weather.Start();
        ReloadHotkeys();
        UpdateRawInput();
        SyncAutostart();
        island.UpdateGlowColors();
        Log.I("config", "settings applied");
    }

    /// <summary>
    /// A timer ran out: a popup that interrupts anything (like a call) and the
    /// alarm, until it's dismissed or a minute has passed. One that ran out
    /// while the island wasn't running is reported quietly.
    /// </summary>
    void TimerDone(double length, bool whileClosed)
    {
        island.Alert(Alerts.Timer(length, whileClosed,
            again: () => { island.DismissAlert("timer"); svc.Timer.Start(length); },
            stop: () => island.DismissAlert("timer"),
            ended: null));
    }

    /// <summary>Settings -> Notifications & Sounds -> Preview popups: one of each kind, in turn.</summary>
    public void PreviewPopups()
    {
        var m = svc.Media?.Current;
        var accent = m?.Palette is { Length: > 0 } p ? p[0] : Col.Hex(0x8b5cf6);
        Log.I("alert", "previewing every kind of popup");
        foreach (var a in Alerts.Samples(key => island.DismissAlert(key), m?.Art, accent)) island.Alert(a);
    }

    /// <summary>The chosen sound for a kind.</summary>
    internal static string ToneFor(Config c, string kind) => kind switch
    {
        Sounds.Notification => c.NotificationTone,
        Sounds.Call => c.Ringtone,
        Sounds.Timer => c.TimerTone,
        Sounds.Charge => c.ChargeTone,
        Sounds.Low => c.LowTone,
        _ => c.DeviceTone,   // connecting and disconnecting
    };

    /// <summary>Settings: hear a sound as it's picked (a looping one plays a few seconds).</summary>
    public void PreviewSound(string kind, string variant) => Sounds.Preview(kind, SoundVolumeOf(cfg), variant);

    /// <summary>
    /// The level sounds play at: 100% in Settings is the level they were tuned
    /// at (0.6 of full scale); 50% to 150% scale it. At 150% the loudest peak
    /// is still under full scale (0.9 x 0.9), so nothing ever clips.
    /// </summary>
    internal const double BaseSoundVolume = 0.6;
    internal static float SoundVolumeOf(Config c) => (float)(BaseSoundVolume * c.SoundLevel);

    /// <summary>Whether an alert's sound plays, by the settings for its kind.</summary>
    internal static bool SoundAllowed(Config c, Alert a) => c.Sounds && a?.Sound != null && a.Sound switch
    {
        Sounds.Timer => c.TimerSound,
        Sounds.Call => c.CallRingtone,
        Sounds.Notification => c.NotificationSound,
        _ => c.DeviceSounds,
    };

    void SaveConfig()
    {
        ignoreCfgUntil = clock.Now + 1.5;
        try { cfg.Save(Paths.Config); } catch (Exception ex) { Log.E("config", "couldn't save settings", ex); }
    }

    void WatchConfig()
    {
        // hand edits to the JSON apply live
        try
        {
            cfgWatch = new FileSystemWatcher(Paths.Home, Path.GetFileName(Paths.Config)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
            long t = 0;
            cfgWatch.Changed += (_, _) => Ui.Post(() =>
            {
                if (clock.Now < ignoreCfgUntil) return;
                Ui.Cancel(ref t);
                t = Ui.After(0.4, () => { var c = Config.Load(Paths.Config); if (c.LoadError == null) { Log.I("config", "config file edited: reloading"); ApplySettings(c); } else Log.W("config", "edited config is invalid, keeping current settings: " + c.LoadError); });
            });
        }
        catch (Exception ex) { Log.W("config", "can't watch the config file: " + ex.Message); }
    }

    bool? autostartWas;

    void SyncAutostart()
    {
        // A sign-in task, with the Run key as a backup (see Shared.Autostart).
        // A build running from the project folder never touches either: only
        // the installed copy should start with Windows.
        if (IsDevBuild()) { Log.D("app", "development build: leaving autostart alone"); return; }
        bool want = cfg.StartWithWindows;
        if (autostartWas == want) return;
        autostartWas = want;
        // the Store version starts through its package's startup task instead
        if (Packaging.IsStore) { _ = Task.Run(() => Packaging.SyncStartupTaskAsync(want)); return; }
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Isola.exe");
        // schtasks takes a moment: off the UI thread
        Task.Run(() =>
        {
            try
            {
                if (!want) { Shared.Autostart.Disable(m => Log.W("app", m)); Log.I("app", "start with Windows: off"); return; }
                if (string.Equals(Shared.Autostart.TaskTarget(), exe, StringComparison.OrdinalIgnoreCase)) { Shared.Autostart.Enable(exe, null); return; }
                bool task = Shared.Autostart.Enable(exe, m => Log.W("app", m));
                Log.I("app", task ? "start with Windows: on, as a sign-in task (" + exe + ")" : "start with Windows: on, Run entry only");
            }
            catch (Exception ex) { Log.W("app", "couldn't update autostart: " + ex.Message); }
        });
    }

    /// <summary>Choices for Settings -> Screen: built-in, primary, then every external display by name.</summary>
    public List<(string label, string value)> DisplayChoices()
    {
        var l = new List<(string, string)> { ("Built-in", "laptop"), ("Primary", "primary") };
        foreach (var p in displays?.Paths ?? Array.Empty<DisplayPath>())
            if (!p.Internal && !string.IsNullOrEmpty(p.DevicePath))
                l.Add((string.IsNullOrEmpty(p.FriendlyName) ? p.Connector : p.FriendlyName, p.DevicePath));
        return l;
    }

    static bool IsDevBuild()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Isola.csproj"))) return true;
        return false;
    }

    // ======================================================================
    // the loop
    // ======================================================================

    void Loop()
    {
        lastFrame = clock.Now;
        while (!quit)
        {
            while (Win32.PeekMessageW(out var msg, 0, 0, 0, Win32.PM_REMOVE))
            {
                if (msg.message == Win32.WM_QUIT) { quit = true; break; }
                Win32.TranslateMessage(msg);
                Win32.DispatchMessageW(msg);
            }
            if (quit) break;
            Ui.Drain();
            Ui.RunTimers();
            settings?.Tick();
            PollCursor();

            double now = clock.Now;
            bool req = host.FrameRequested, cont = island.NeedsContinuousFrames, anim = AnythingMoving();
            bool moving = host.Shown && (req || cont || anim);
            double amb = island.AmbientInterval;
            bool ambientDue = host.Shown && amb > 0 && now >= nextAmbient;
            if (moving || ambientDue)
            {
                if (req) fReq++; if (cont) fCont++; if (anim) fAnim++; if (ambientDue && !moving) fAmb++;
                // Only report the failure this counter exists for: something still
                // animating for most of ten seconds while the island sits at rest
                // (a spring that never settles burns a core). Normal use is silent.
                if (now - fSince > 10)
                {
                    if (fAnim > 400 && island.Mode is Mode.Rest or Mode.Hidden)
                        Log.W("perf", $"still animating at rest: {fAnim} frames in {now - fSince:0}s ({island.MovingWhat()}); requested {fReq}, continuous {fCont}, ambient {fAmb}");
                    fReq = fCont = fAnim = fAmb = 0; fSince = now;
                }
                // pace to the display: wait on the swap chain, but wake for input
                nint h = host.Surface.FrameWaitHandle;
                if (h != 0)
                {
                    uint r = Win32.MsgWaitForMultipleObjectsEx(1, &h, 100, Win32.QS_ALLINPUT, Win32.MWMO_INPUTAVAILABLE);
                    if (r == 1) continue;   // input first; render on the next pass
                }
                if (moving)
                {
                    // one line per quiet-to-active transition: says what woke the island
                    if (trimmed) Log.D("perf", $"active again: requested={req} continuous={cont} animating={anim} ({island.MovingWhat()}), mode {island.Mode}, page {island.CurrentPage?.Key}");
                    lastActive = clock.Now; trimmed = false;   // blinks and the waveform don't count as activity
                }
                Frame();
                if (amb > 0) nextAmbient = clock.Now + island.AmbientInterval;
                continue;
            }
            // idle: sleep until a message, a timer, ambient work or the cursor poll
            MaybeTrim(now);
            double wait = Math.Min(Ui.NextDue(), amb > 0 ? nextAmbient - now : double.PositiveInfinity);
            if (!trimmed) wait = Math.Min(wait, lastActive + 4.05 - now);
            if (settings?.Alive == true) wait = Math.Min(wait, settings.NextWake());
            wait = Math.Min(wait, CursorPollInterval());
            uint ms = double.IsInfinity(wait) ? Win32.INFINITE : (uint)Math.Clamp(wait * 1000, 0, 60_000);
            Win32.MsgWaitForMultipleObjectsEx(0, null, ms, Win32.QS_ALLINPUT, Win32.MWMO_INPUTAVAILABLE);
        }
    }

    bool AnythingMoving() => island.Step(0) || false;

    // ---- staying small ------------------------------------------------------
    double lastActive;
    int fReq, fCont, fAnim, fAmb; double fSince;
    bool trimmed = true;
    long diagTimer;

    /// <summary>
    /// After a few still seconds, give memory back: a full GC, DXGI's Trim (the
    /// documented "app is going idle" call that releases the driver's scratch
    /// allocations), and a working-set trim. Costs nothing while idle; the next
    /// animation simply pages back what it needs.
    /// </summary>
    void MaybeTrim(double now)
    {
        if (trimmed || now - lastActive < 4) return;
        trimmed = true;
        long before = Environment.WorkingSet;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        try { using var d3 = Gfx.DxgiDevice.QueryInterfaceOrNull<Vortice.DXGI.IDXGIDevice3>(); d3?.Trim(); } catch { }
        Win32.SetProcessWorkingSetSizeEx(Win32.GetCurrentProcess(), -1, -1, 0);
        Log.D("perf", $"idle trim: working set {before / 1048576.0:0} -> {Environment.WorkingSet / 1048576.0:0} MB");
    }

    void Diagnostics()
    {
        var p = System.Diagnostics.Process.GetCurrentProcess();
        Log.I("perf", $"memory: working set {p.WorkingSet64 / 1048576.0:0} MB, private {p.PrivateMemorySize64 / 1048576.0:0} MB, GC heap {GC.GetTotalMemory(false) / 1048576.0:0.0} MB, " +
            $"CPU total {p.TotalProcessorTime.TotalSeconds:0.0} s, threads {p.Threads.Count}, handles {p.HandleCount}");
        Ui.Cancel(ref diagTimer);
        diagTimer = Ui.After(600, Diagnostics);
    }

    void Frame()
    {
        double now = clock.Now;
        double dt = Math.Clamp(now - lastFrame, 0, 0.1);
        lastFrame = now;
        host.FrameRequested = false;
        bool moving = island.Step(dt);
        var ctx = host.Surface.Begin();
        canvas.Begin(ctx);
        try { island.Render(canvas); }
        catch (Exception ex) { Log.E("render", "frame failed", ex); }
        host.Surface.End();
        if (moving) host.RequestFrame();
    }

    double CursorPollInterval()
    {
        if (island == null || !host.Shown && island.Mode != Mode.Hidden) return double.PositiveInfinity;
        // hidden: watch for the pointer approaching the top edge; resting: the eyes follow it;
        // open: make sure a leave isn't missed (see PollCursor)
        if (island.Hovering && island.Mode is Mode.Peek or Mode.Expanded) return 0.1;
        return island.Mode == Mode.Hidden ? 0.08 : island.CursorPollInterval;
    }

    double lastPoll;
    bool buttonWasDown;

    void PollCursor()
    {
        // a click near the top of the screen, beside the island, means someone is working up there:
        // hovering leaves the island alone for a moment instead of opening it over what they clicked
        bool buttonDown = host.MouseButtonDown;
        if (buttonDown && !buttonWasDown)
        {
            var (cx, cy, onLaptop) = host.Cursor();
            if (onLaptop && cy < 80 && !host.PointerOverIsland()) island.WorkingNearby();
        }
        buttonWasDown = buttonDown;
        // Windows' leave notice can go missing (tracking is dropped when another
        // window activates, as Settings does when opened from the gear): the
        // island then stayed open over the settings window for seconds. While
        // it's open and thinks it's hovered, check where the pointer really is.
        if (island.Hovering && island.Mode is Mode.Peek or Mode.Expanded && island.Ux.Dragging == null && !host.PointerOverIsland())
        {
            Log.D("island", "pointer left (noticed by polling)");
            island.MouseLeave();
        }
        if (island.Mode != Mode.Hidden || displays.Laptop == null) { reveal.Reset(); return; }
        double now = clock.Now;
        if (now - lastPoll < 0.07) return;
        lastPoll = now;
        // reveal when the pointer rests near the top center of the laptop panel
        var (x, y, on) = host.Cursor();
        float half = cfg.RevealZoneWidth / 2f;
        bool inZone = on && Math.Abs(x - host.WindowWidth / 2) <= half && y <= cfg.RevealZoneHeight;
        if (!inZone)
        {
            reveal.Reset();
            if (island.RevealSuppressed) { island.RevealSuppressed = false; Log.D("island", "pointer left the top strip: reveal re-armed"); }
            return;
        }
        if (island.RevealSuppressed) return;   // just hidden from up here: wait for the pointer to leave first
        bool button = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Win32.GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (reveal.Update(now, x, y, button)) island.Reveal("pointer resting at the top");
    }

    void Shutdown()
    {
        Log.I("app", "shutting down");
        banners?.Restore("quitting");
        foreach (var h in hooks) Win32.UnhookWinEvent(h);
        var abd = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = host.Input };
        Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref abd);
        Tray(false);
        Win32.RemoveClipboardFormatListener(host.Input);
        Win32.UnregisterHotKey(host.Input, HK_SEARCH); Win32.UnregisterHotKey(host.Input, HK_TOGGLE);
        Win32.RevokeDragDrop(host.Input);
        settings?.Close();
        Win32.DestroyWindow(host.Input); Win32.DestroyWindow(host.Visual);
        if (restart && Environment.ProcessPath != null)
        {
            Log.Flush();
            // the mutex is released when we exit; the new copy waits for it via the launcher arg
            Win32.ShellExecute(Environment.ProcessPath, "--restarted");
        }
    }
}

public static class Hotkey
{
    /// <summary>"Ctrl+Alt+Space" → modifiers + virtual key.</summary>
    public static bool TryParse(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string p = raw.ToLowerInvariant();
            switch (p)
            {
                case "ctrl": case "control": mods |= Win32.MOD_CONTROL; continue;
                case "alt": mods |= Win32.MOD_ALT; continue;
                case "shift": mods |= Win32.MOD_SHIFT; continue;
                case "win": case "windows": mods |= Win32.MOD_WIN; continue;
            }
            vk = p switch
            {
                "space" => 0x20, "enter" => 0x0D, "tab" => 0x09, "esc" or "escape" => 0x1B, "backspace" => 0x08,
                "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27, "home" => 0x24, "end" => 0x23,
                "`" or "grave" => 0xC0, "/" => 0xBF, "\\" => 0xDC, ";" => 0xBA, "." => 0xBE, "," => 0xBC,
                _ when p.Length == 1 && char.IsLetterOrDigit(p[0]) => char.ToUpperInvariant(p[0]),
                _ when p.Length >= 2 && p[0] == 'f' && int.TryParse(p[1..], out int f) && f is >= 1 and <= 24 => (uint)(0x6F + f),
                _ => 0,
            };
            if (vk == 0) return false;
        }
        return vk != 0 && mods != 0;
    }
}
