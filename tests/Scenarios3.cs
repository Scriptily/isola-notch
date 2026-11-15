using System.Net;
using System.Net.Sockets;
using Isola.App;
using Isola.Island;
using Isola.Render;
using Isola.Services;

namespace Isola.Tests;

public static partial class Scenarios
{
    // =====================================================================
    // regressions found in the audit pass
    // =====================================================================

    [Scenario("searching 'lock' offers the Lock screen command, not every device's child-lock switch")]
    public static void HaSearchSkipsDeviceSettings()
    {
        var (isl, host, clock) = NewIsland(new Config { HaBaseUrl = "http://127.0.0.1:1", HaToken = "t" });
        var fake = new FakeHa();
        fake.Entity("scene.leisure_lights", "unknown", "Leisure Lights");
        fake.Entity("switch.tablet_kiosk_lock", "off", "Tablet Kiosk lock");
        fake.Entity("switch.dehumidifier_child_lock", "off", "Dehumidifier Child lock");
        fake.Entity("lock.front_door", "locked", "Front door lock");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ha = new HomeAssistant(clock, fake) { BaseUrl = () => $"http://127.0.0.1:{port}", Token = () => "t", Wanted = () => isl.Cfg.HaEntities };
            isl.Svc.Ha = ha;
            ha.RefreshAsync(true).Wait(); clock.Advance(0.1);
            Eq(4, ha.All.Count, "entities learned");

            var titles = Launcher.Answers("lock", isl).Select(i => i.Title).ToList();
            True(titles.Contains("Lock screen"), "Lock screen offered: " + string.Join(" | ", titles));
            True(titles.Contains("Front door lock"), "a real lock is still searchable");
            True(!titles.Any(t => t.Contains("Kiosk") || t.Contains("Child")), "device-setting switches left out: " + string.Join(" | ", titles));

            Eq("Leisure Lights", Launcher.Answers("leisure", isl).FirstOrDefault()?.Title, "scenes found by name");

            isl.Cfg.HaEntities = new List<string> { "switch.tablet_kiosk_lock" };
            titles = Launcher.Answers("kiosk", isl).Select(i => i.Title).ToList();
            True(titles.Contains("Tablet Kiosk lock"), "a switch you put on the island is searchable");
        }
        finally { listener.Stop(); }
    }

    [Scenario("the compact waveform keeps moving while playing silently (Spotify's own volume at 0%)")]
    public static void WaveformAliveWithoutSignal()
    {
        var box = new Box(0, 0, 22, 16);   // the compact media pill's waveform box
        float gap = box.W / 9;
        float lo = float.MaxValue, hi = 0;
        for (double t = 0; t < 3; t += 1 / 60.0)
        {
            float h = Paint.BarHeight(box, 2, 5, level: 0, playing: true, now: t);
            lo = Math.Min(lo, h); hi = Math.Max(hi, h);
        }
        True(hi - lo > 1.5f * gap, $"middle bar swings more than its own width (was {hi - lo:0.00} vs width {gap:0.00})");
        True(hi >= box.H * 0.5f, $"and reaches half the box height (max {hi:0.00} of {box.H})");
        for (int i = 0; i < 5; i++)
            Eq(gap, Paint.BarHeight(box, i, 5, 0.4f, playing: false, now: i * 0.37), $"paused bar {i} is a dot");
        float quiet = 0, loud = 0;
        for (double t = 0; t < 3; t += 1 / 60.0) { quiet += Paint.BarHeight(box, 2, 5, 0, true, t); loud += Paint.BarHeight(box, 2, 5, 0.5f, true, t); }
        True(loud > quiet * 1.2f, "real loudness still makes it taller");
    }

    [Scenario("the AirPods card shows only while they're connected, not from an earlier MagicPods reading")]
    public static void AirPodsCardOnlyWhenConnected()
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var reading = new AirPodsState { Left = 15, Right = 15, UpdatedAt = clock.Now - 30 };
        var bt = new BluetoothService(clock) { RadioOn = true };
        bt.TestAdd("pods-rec", "AirPods Pro 2", false, true, "cpods");
        True(!DashboardPage.AirPodsLive(reading, bt, clock.Now), "in the case, reading 30 s old: no card");
        bt.TestAdd("pods-rec2", "AirPods Pro 2", true, true, "cpods");
        True(DashboardPage.AirPodsLive(reading, bt, clock.Now), "connected: card");
        bt.RadioOn = false;
        True(!DashboardPage.AirPodsLive(reading, bt, clock.Now), "radio off: no card");
        True(DashboardPage.AirPodsLive(reading, null, clock.Now), "no Bluetooth service: a fresh reading will do");
        True(!DashboardPage.AirPodsLive(reading, null, clock.Now + 200), "...but not a stale one");
        True(!DashboardPage.AirPodsLive(new AirPodsState(), bt, clock.Now), "no battery numbers: no card");
    }

    [Scenario("the shipped example config lists every setting, at its default, and carries no token")]
    public static void ExampleConfigInSync()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Isola.csproj"))) dir = dir.Parent;
        True(dir != null, "project folder above " + AppContext.BaseDirectory);
        string example = Path.Combine(dir.FullName, "isola.config.example.json");
        True(File.Exists(example), "example config at " + example);
        var shipped = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(example))!.AsObject();
        var defaults = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new Config(), ConfigJson.Default.Config))!.AsObject();
        var missing = defaults.Select(kv => kv.Key).Where(k => !shipped.ContainsKey(k)).ToList();
        True(missing.Count == 0, "missing from the example (run: dotnet run -- --example-config ../isola.config.example.json): " + string.Join(", ", missing));
        var stale = shipped.Select(kv => kv.Key).Where(k => !defaults.ContainsKey(k)).ToList();
        True(stale.Count == 0, "settings that no longer exist: " + string.Join(", ", stale));
        var differ = defaults.Where(kv => kv.Value?.ToJsonString() != shipped[kv.Key]?.ToJsonString()).Select(kv => kv.Key).ToList();
        True(differ.Count == 0, "not at the default: " + string.Join(", ", differ));
        Eq("", (string)shipped["HaToken"], "no Home Assistant token"); Eq(null, shipped["UpdateToken"], "no GitHub token setting at all");
    }

    [Scenario("a Home Assistant call in flight is visible to the tile until it answers or fails (the tile spinner)")]
    public static void HaCallInFlight()
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var fake = new FakeHa();
        fake.Entity("scene.leisure_lights", "unknown", "Leisure Lights");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ha = new HomeAssistant(clock, fake) { BaseUrl = () => $"http://127.0.0.1:{port}", Token = () => "t", Wanted = () => new[] { "scene.leisure_lights" } };
            ha.RefreshAsync().Wait(); clock.Advance(0.1);
            True(double.IsNaN(ha.CallingSince("scene.leisure_lights")), "nothing in flight before the tap");

            var gate = new TaskCompletionSource(); fake.Gate = gate.Task;
            double tapped = clock.Now;
            var call = ha.Call("scene", "turn_on", "scene.leisure_lights");
            Eq(tapped, ha.CallingSince("scene.leisure_lights"), "in flight from the moment of the tap");
            Thread.Sleep(150); clock.Advance(0.5);
            Eq(tapped, ha.CallingSince("scene.leisure_lights"), "still in flight while Home Assistant hasn't answered");
            gate.SetResult();
            for (int i = 0; i < 40 && !double.IsNaN(ha.CallingSince("scene.leisure_lights")); i++) { Thread.Sleep(25); clock.Advance(0.02); }
            True(double.IsNaN(ha.CallingSince("scene.leisure_lights")), "cleared once it answered");
            call.Wait(3000);

            fake.CallsFail = true;
            ha.Call("scene", "turn_on", "scene.leisure_lights").Wait(3000); clock.Advance(0.05);
            True(double.IsNaN(ha.CallingSince("scene.leisure_lights")), "cleared when the call fails, too (no spinner left turning)");
        }
        finally { listener.Stop(); }
    }

    [Scenario("a Home Assistant device is searchable by name before the Home tab has ever been opened")]
    public static void HaSearchBeforeHomeTab()
    {
        var (isl, host, clock) = NewIsland(new Config { HaBaseUrl = "http://127.0.0.1:1", HaToken = "t", HaEntities = new List<string> { "scene.leisure_lights" } });
        var fake = new FakeHa();
        fake.Entity("scene.leisure_lights", "unknown", "Leisure Lights");
        fake.Entity("light.desk", "off", "Desk lamp");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ha = new HomeAssistant(clock, fake) { BaseUrl = () => $"http://127.0.0.1:{port}", Token = () => "t", Wanted = () => isl.Cfg.HaEntities };
            isl.Svc.Ha = ha;                                  // fresh start: nothing fetched, status not Online
            True(ha.Status != HaStatus.Online && ha.All.Count == 0, "nothing known yet");
            Launcher.Answers("desk", isl);                    // the first keystrokes ask for the list...
            for (int i = 0; i < 100 && ha.All.Count == 0; i++) { Thread.Sleep(20); clock.Advance(0.02); }
            True(ha.All.Count > 0, "...and the list is fetched, though the Home tab was never opened");
            Eq("Desk lamp", Launcher.Answers("desk", isl).FirstOrDefault(x => x.Tag == "Home")?.Title, "so a device is found by name");
        }
        finally { listener.Stop(); }
    }
}
