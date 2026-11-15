using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Isola.App;
using Isola.Island;
using Isola.Native;
using Isola.Render;
using Isola.Services;
using Isola.Shell;

namespace Isola.Tests;

public static partial class Scenarios
{
    // =====================================================================
    // fullscreen: per screen, by Z order
    // =====================================================================

    static readonly RECT Lap = FakeDisplays.LaptopRect, Tv = FakeDisplays.TvRect;

    [Scenario("a borderless window covering the laptop is fullscreen there, and only there")]
    public static void FullscreenOnLaptop()
    {
        var w = new FakeWindows(); var f = new FullscreenDetector(w);
        w.Stack.Add(FakeWindows.Win(10, Lap, caption: false));
        True(f.FullscreenOn(Lap) == 10, "laptop fullscreen");
        True(f.FullscreenOn(Tv) == 0, "the TV is unaffected");
    }

    [Scenario("a game fullscreen on the TV doesn't hide the island on the laptop")]
    public static void FullscreenOnTvOnly()
    {
        var w = new FakeWindows(); var f = new FullscreenDetector(w);
        w.Stack.Add(FakeWindows.Win(20, Tv, caption: false));
        w.Stack.Add(FakeWindows.Win(21, new RECT(100, 100, 1500, 1000)));
        True(f.FullscreenOn(Lap) == 0, "laptop not fullscreen");
        w.Fg = 20;
        True(f.ForegroundFullscreen(_ => Tv), "but hotkeys are released for the TV game");
    }

    [Scenario("a video left fullscreen on the laptop still counts while you work on the TV")]
    public static void FullscreenNotForeground()
    {
        var w = new FakeWindows(); var f = new FullscreenDetector(w);
        w.Stack.Add(FakeWindows.Win(30, new RECT(3000, 0, 4000, 800)));   // focused app on the TV
        w.Stack.Add(FakeWindows.Win(31, Lap, caption: false));            // video, behind it in Z order but alone on the laptop
        w.Fg = 30;
        True(f.FullscreenOn(Lap) == 31, "laptop fullscreen though not foreground");
    }

    [Scenario("maximized windows, click-through overlays, minimized and cloaked windows are not fullscreen")]
    public static void NotFullscreen()
    {
        var w = new FakeWindows(); var f = new FullscreenDetector(w);
        w.Stack.Add(FakeWindows.Win(40, Lap, cls: "NVIDIA overlay", caption: false, clickThrough: true));
        w.Stack.Add(FakeWindows.Win(41, Lap, max: true, caption: true));   // auto-hidden taskbar: reaches every edge
        True(f.FullscreenOn(Lap) == 0, "maximized with a title bar under a click-through overlay");
        w.Stack.Clear();
        w.Stack.Add(FakeWindows.Win(42, Lap, caption: false, min: true));
        w.Stack.Add(FakeWindows.Win(43, Lap, caption: false, cloaked: true));
        True(f.FullscreenOn(Lap) == 0, "minimized / cloaked");
    }

    [Scenario("the topmost window on the screen decides: a small window over a fullscreen one wins")]
    public static void TopmostDecides()
    {
        var w = new FakeWindows(); var f = new FullscreenDetector(w);
        w.Stack.Add(FakeWindows.Win(50, new RECT(100, 100, 800, 600)));
        w.Stack.Add(FakeWindows.Win(51, Lap, caption: false));
        True(f.FullscreenOn(Lap) == 0, "a normal window is on top");
    }

    // =====================================================================
    // displays
    // =====================================================================

    [Scenario("the laptop is found by output technology, not by position or primary")]
    public static void LaptopByTechnology()
    {
        Ui.Reset(); Ui.Clock = new FakeClock();
        var d = new FakeDisplays(); d.Tv(primary: true, gdi: @"\\.\DISPLAY1"); d.Paths.Clear(); d.Mons.Clear();
        d.Tv(primary: true); d.Laptop(primary: false);
        var w = new DisplayWatcher(d); w.Refresh("t");
        Eq(@"\\.\DISPLAY1", w.Laptop?.GdiName, "laptop picked though the TV is primary");
    }

    [Scenario("TV-only (laptop panel off) leaves the island off - unless a screen is chosen in settings")]
    public static void TvOnly()
    {
        Ui.Reset(); Ui.Clock = new FakeClock();
        var d = new FakeDisplays(); d.Tv(primary: true);
        var w = new DisplayWatcher(d); w.Refresh("t");
        Eq(null, w.Laptop, "built-in setting: no laptop, no island");
        w.ShowOn = () => "primary"; w.Refresh("t");
        Eq(@"\\.\DISPLAY2", w.Laptop?.GdiName, "primary setting follows the TV");
        w.ShowOn = () => "gone-display"; w.Refresh("t");
        Eq(@"\\.\DISPLAY2", w.Laptop?.GdiName, "an unplugged chosen display falls back to the primary");
    }

    [Scenario("a display connecting is announced once, after the topology settles")]
    public static void DisplayConnectSettles()
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var d = new FakeDisplays(); d.Laptop();
        var w = new DisplayWatcher(d); w.Refresh("start");
        var got = new List<(string, bool)>();
        w.DisplayChanged += (p, c) => got.Add((p.FriendlyName, c));
        d.Tv(); w.Refresh("plug"); clock.Advance(0.5);
        d.Unplug("Living Room TV"); w.Refresh("flap"); clock.Advance(0.5);
        d.Tv(); w.Refresh("plug again"); clock.Advance(3);
        Eq(1, got.Count, "one announcement through the flapping");
        Eq(("Living Room TV", true), got[0], "connected");
        d.Unplug("Living Room TV"); w.Refresh("unplug"); clock.Advance(3);
        Eq(("Living Room TV", false), got[^1], "disconnected");
    }

    // =====================================================================
    // AirPods
    // =====================================================================

    [Scenario("MagicPods' log lines give battery, charging, in-ear and lid state")]
    public static void MagicPodsLog()
    {
        var a = new AirPodsService(new FakeClock());
        True(a.ParseLine("2026-11-13 14:26:47    Srv    Info     13      AirPods Pro 2       Apple battery updated l=45, lc=0, r=40, rc=1, c=-1, cc=0"), "battery line changes state");
        Eq(45, a.State.Left, "left"); Eq(40, a.State.Right, "right"); Eq(true, a.State.RightCharging, "right charging"); Eq(-1, a.State.Case, "case unknown kept");
        Eq("AirPods Pro 2", a.State.Name, "name");
        string lid = null; a.Event += e => lid = e;
        a.ParseLine("2026-11-13 14:29:32    Srv    Info     19      AirPods Pro 2 -     Case open (LR) : L out R out");
        Eq("lid-open", lid, "lid event"); Eq(false, a.State.LeftInEar, "out of ear");
        a.ParseLine("2026-11-13 12:57:12    Srv    Info     8       BaseHeadphones      AirPods Pro 2 - Find My has been connected");
        Eq("AirPods Pro 2", a.State.Name, "' - Find My' trimmed");
        True(!a.ParseLine("2026-11-13 14:27:48    Srv    Info     13      TrayTileControll    Try to set cached battery"), "noise ignored");
        True(!a.ParseLine("garbage"), "garbage ignored");
    }

    [Scenario("Apple's BLE proximity broadcast decodes to per-bud battery")]
    public static void BleDecode()
    {
        // type 07, len 19, model 0x2014 (Pro 2), status 0x75 (primary=right), pods 0x9A, case/charging 0x48
        var bytes = new byte[] { 0x07, 0x19, 0x01, 0x14, 0x20, 0x75, 0x9A, 0x48, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var s = AirPodsService.Decode(bytes);
        True(s != null, "decoded");
        Eq(90, s.Left, "left (flip by status bit)"); Eq(100, s.Right, "right"); Eq(80, s.Case, "case");
        Eq(true, s.CaseCharging, "case charging");
        Eq(null, AirPodsService.Decode(new byte[] { 0x10, 0x05, 1, 2, 3, 4, 5, 6, 7, 8 }), "other Apple adverts ignored");
    }

    // =====================================================================
    // search
    // =====================================================================

    [Scenario("Everything's LIST2 reply is parsed field by field")]
    public static unsafe void EverythingParse()
    {
        var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
        uint flags = 0x1 | 0x2 | 0x10 | 0x40 | 0x400;
        bw.Write(2u); bw.Write(2u); bw.Write(0u); bw.Write(flags); bw.Write(20u);   // header
        long itemsAt = ms.Position; bw.Write(new byte[16]);                          // two ITEM2s, patched below
        uint Field(string name, string path, long size, long ft, uint runs)
        {
            uint off = (uint)ms.Position;
            bw.Write((uint)name.Length); bw.Write(Encoding.Unicode.GetBytes(name + "\0"));
            bw.Write((uint)path.Length); bw.Write(Encoding.Unicode.GetBytes(path + "\0"));
            bw.Write(size); bw.Write(ft); bw.Write(runs);
            return off;
        }
        uint o1 = Field("Google Chrome.lnk", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs", 2345, new DateTime(2026, 1, 2).ToFileTime(), 7);
        uint o2 = Field("Chrome", @"C:\Program Files\Google", -1, new DateTime(2026, 3, 4).ToFileTime(), 0);
        ms.Position = itemsAt; bw.Write(0u); bw.Write(o1); bw.Write(1u); bw.Write(o2);
        var buf = ms.ToArray();
        List<SearchResult> r;
        fixed (byte* p = buf) r = EverythingSearch.ParseList2(p, buf.Length);
        Eq(2, r.Count, "items");
        Eq("Google Chrome.lnk", r[0].Name, "name"); Eq(7u, r[0].RunCount, "run count"); Eq(2345L, r[0].Size, "size");
        Eq(true, r[1].IsFolder, "folder flag"); Eq(new DateTime(2026, 3, 4), r[1].Modified, "date");
        Eq("Google Chrome.lnk", r[0].Display, "the parser doesn't decide what's an app");
        r[0].IsApp = true;
        Eq("Google Chrome", r[0].Display, "once ranked as an app, .lnk is hidden in the title");
    }

    [Scenario("launcher answers: math, units, colors; junk is not an answer")]
    public static void LauncherAnswers()
    {
        Eq("= 54", SearchPage.Calc("12*4.5"), "math");
        Eq("= 84", SearchPage.Calc("= 12*(3+4)"), "parentheses");
        Eq("= 1,024", SearchPage.Calc("2^10"), "power");
        Eq(null, SearchPage.Calc("chrome"), "not math");
        Eq(null, SearchPage.Calc("rm -rf"), "not math either");
        Eq("6.2137 mi", Launcher.Convert("10 km to mi"), "length");
        Eq("37.7778 °C", Launcher.Convert("100 f in c"), "temperature");
        Eq("3.7253 GiB", Launcher.Convert("4 gb to gib"), "data");
        Eq(null, Launcher.Convert("10 km to kg"), "mismatched units");
        True(Launcher.Match("Empty Recycle Bin", "recycle") >= 60, "word prefix");
        True(Launcher.Match("Lock screen", "lock") > Launcher.Match("Tablet Kiosk lock", "lock"), "prefix beats a later word");
        Eq(0.0, Launcher.Match("Lock screen", "zzz"), "no match");
    }

    // =====================================================================
    // updater
    // =====================================================================

    [Scenario("the updater picks the newest verified release with our tag and asset")]
    public static void UpdaterPicksRelease()
    {
        string Rel(string tag, bool asset = true, string body = "sha256: ab1200000000000000000000000000000000000000000000000000000000cd34", bool pre = false, string host = null, string digest = null) =>
            $"{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":{(pre ? "true" : "false")},\"body\":\"{body}\",\"assets\":[{(asset ? "{\"name\":\"IsolaSetup.exe\",\"url\":\"" + (host ?? "https://api.github.com/repos/Scriptily/isola-notch/releases/assets/") + tag + "\"" + (digest != null ? ",\"digest\":\"sha256:" + digest + "\"" : "") + "}" : "")}]}}";
        var arr = JsonNode.Parse("[" + string.Join(",", Rel("v1.2.0"), Rel("v1.10.0"), Rel("v2.0.0", asset: false), Rel("other-project-v9.0.0"), Rel("v3.0.0", pre: true),
            Rel("v4.0.0", host: "https://api.github.com/repos/someone/else/releases/assets/")) + "]") as JsonArray;
        var best = Updater.Latest(arr);
        True(best.HasValue, "found one");
        Eq(new Version(1, 10, 0), best.Value.ver, "1.10 > 1.2; no-asset, other-project, pre-releases and another repo's asset skipped");
        True(best.Value.sha?.Length == 64, "sha read from the notes");
        var mismatch = Updater.Latest(JsonNode.Parse("[" + Rel("v1.3.0", digest: "ff00000000000000000000000000000000000000000000000000000000000000") + "]") as JsonArray);
        Eq(null, mismatch?.sha, "GitHub's own digest disagreeing with the notes: no verified hash, so it won't install");
    }

    // =====================================================================
    // Home Assistant: reachability is a state, not an error
    // =====================================================================

    [Scenario("Home Assistant unreachable, unauthorized, then online - each reported plainly")]
    public static void HomeAssistantStates()
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var fake = new FakeHa();
        var ha = new HomeAssistant(clock, fake) { BaseUrl = () => "http://127.0.0.1:1", Token = () => "t", Wanted = () => new[] { "light.desk" } };
        ha.RefreshAsync().Wait(); clock.Advance(0.1);
        Eq(HaStatus.Unreachable, ha.Status, "nothing listening");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            ha.BaseUrl = () => $"http://127.0.0.1:{port}";
            fake.Status = HttpStatusCode.Unauthorized;
            ha.RefreshAsync().Wait(); clock.Advance(0.1);
            Eq(HaStatus.Unauthorized, ha.Status, "bad token");
            fake.Status = HttpStatusCode.OK; fake.Entity("light.desk", "off", "Desk", "\"brightness\":0,\"supported_color_modes\":[\"brightness\"]");
            ha.RefreshAsync().Wait(); clock.Advance(0.1);
            Eq(HaStatus.Online, ha.Status, "online");
            Eq("off", ha.Entities["light.desk"].State, "state read");
            ha.Tap(ha.Entities["light.desk"]); clock.Advance(0.05);
            Eq("on", ha.Entities["light.desk"].State, "optimistic flip on tap");
            Thread.Sleep(900); clock.Advance(0.1);
            True(fake.Calls.Any(c => c.Contains("/api/services/light/toggle") && c.Contains("light.desk")), "service called");
        }
        finally { listener.Stop(); }
    }

    // =====================================================================
    // config, hotkeys, stores
    // =====================================================================

    [Scenario("a broken config runs on defaults and keeps the broken file; values are clamped")]
    public static void ConfigRobust()
    {
        string dir = Path.Combine(Path.GetTempPath(), "di-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "c.json");
        File.WriteAllText(f, "{ \"Style\": \"notch\", oops }");
        var c = Config.Load(f);
        True(c.LoadError != null, "error reported"); Eq("pill", c.Style, "defaults used"); True(File.Exists(f + ".broken"), "broken file kept");
        File.WriteAllText(f, "{ \"Style\": \"notch\", \"Scale\": 9, \"AutoHideMinutes\": -4, \"FutureKey\": 1, // comment\n }");
        c = Config.Load(f);
        Eq(null, c.LoadError, "comments + unknown keys fine"); Eq("notch", c.Style, "style"); Eq(1.6, c.Scale, "scale clamped"); Eq(0.0, c.AutoHideMinutes, "negative clamped");
        c.Save(f); Eq("notch", Config.Load(f).Style, "round trip");
        Directory.Delete(dir, true);
    }

    [Scenario("hotkey strings parse; nonsense is rejected")]
    public static void Hotkeys()
    {
        True(Hotkey.TryParse("Alt+Space", out var m, out var k) && m == Win32.MOD_ALT && k == 0x20, "Alt+Space");
        True(Hotkey.TryParse("ctrl + alt + i", out m, out k) && m == (Win32.MOD_CONTROL | Win32.MOD_ALT) && k == 'I', "Ctrl+Alt+I");
        True(Hotkey.TryParse("Win+Shift+F13", out m, out k) && k == 0x7C, "F13");
        True(!Hotkey.TryParse("Space", out _, out _), "needs a modifier");
        True(!Hotkey.TryParse("Ctrl+Banana", out _, out _), "unknown key");
    }

    [Scenario("the shelf keeps files, text, links and images, survives a restart, and a broken file doesn't lose the app")]
    public static void ShelfStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "di-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "shelf.json");
        var s = new ShelfStore(f);
        s.Add(new[] { @"C:\Windows\notepad.exe", @"C:\Windows\notepad.exe" });
        s.AddText("remember the milk");
        s.AddText("https://example.com/page");
        s.AddLink("https://claude.ai", "Claude");
        s.AddImage(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        Eq(5, s.Items.Count, "dedupe files; text, link (as link), link, image");
        Eq("link", s.Items.First(i => i.Text == "https://example.com/page").Kind, "bare URL text becomes a link");
        var again = new ShelfStore(f);
        Eq(5, again.Items.Count, "persisted");
        File.WriteAllText(f, "not json");
        var broken = new ShelfStore(f);
        Eq(0, broken.Items.Count, "starts empty"); True(File.Exists(f + ".broken"), "keeps the broken file");
        Directory.Delete(dir, true);
    }

    [Scenario("Bluetooth: only connected-AND-present records count (stale 'connected' records are ignored)")]
    public static void BluetoothStaleRecords()
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var bt = new BluetoothService(clock);
        bt.TestAdd("rec-live", "Sam's iPhone", true, true, "c18");
        bt.TestAdd("rec-stale", "Sam's old iPhone", true, false, "c15");     // a stale record, as Windows keeps for devices paired twice
        bt.TestAdd("rec-15-le", "Sam's old iPhone", false, true, "c15");
        var dev = bt.Devices.ToList();
        True(dev.Single(d => d.ContainerId == "c18").Connected, "18 Pro connected");
        True(!dev.Single(d => d.ContainerId == "c15").Connected, "15 Pro not connected despite a stale record");
    }
}
