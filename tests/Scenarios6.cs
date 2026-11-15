using Isola.App;
using Isola.Island;
using Isola.Services;

namespace Isola.Tests;

public static partial class Scenarios
{
    [Scenario("timer lengths: plain minutes, units, m:s and h:m:s; nonsense is refused")]
    public static void TimerParse()
    {
        Eq((double?)600, CountdownTimer.Parse("10"), "a plain number is minutes");
        Eq((double?)90, CountdownTimer.Parse("90s"), "seconds");
        Eq((double?)300, CountdownTimer.Parse("5 min"), "minutes");
        Eq((double?)4800, CountdownTimer.Parse("1h 20m"), "hours and minutes");
        Eq((double?)4800, CountdownTimer.Parse("1h20"), "a bare number after hours is minutes");
        Eq((double?)330, CountdownTimer.Parse("5m30"), "...and after minutes, seconds");
        Eq((double?)5400, CountdownTimer.Parse("1.5h"), "decimals");
        Eq((double?)4800, CountdownTimer.Parse("1 hour and 20 minutes"), "words");
        Eq((double?)90, CountdownTimer.Parse("1:30"), "m:s");
        Eq((double?)3723, CountdownTimer.Parse("1:02:03"), "h:m:s");
        Eq((double?)null, CountdownTimer.Parse("soon"), "words alone");
        Eq((double?)null, CountdownTimer.Parse("0"), "zero");
        Eq((double?)null, CountdownTimer.Parse("25h"), "over a day");
        Eq((double?)null, CountdownTimer.Parse("10 apples"), "trailing junk");
        Eq("1 h 20 min", CountdownTimer.Describe(4800), "described");
        Eq("1 min 30 s", CountdownTimer.Describe(90), "described with seconds");
    }

    [Scenario("the timer counts down, pauses, takes +1 min, rings once at the end, and a canceled one never rings")]
    public static void TimerCountsDown()
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var tm = new CountdownTimer(clock);
        var rang = new List<(double len, bool closed)>();
        tm.Finished += (l, c) => rang.Add((l, c));
        tm.Start(600);
        clock.Advance(300);
        Eq(300.0, Math.Round(tm.Remaining(clock.Now)), "half way");
        tm.PauseResume(); clock.Advance(120);
        Eq(300.0, Math.Round(tm.Remaining(clock.Now)), "paused: the time doesn't move");
        tm.AddMinute();
        Eq(360.0, Math.Round(tm.Remaining(clock.Now)), "+1 min while paused");
        tm.PauseResume(); clock.Advance(359);
        Eq(0, rang.Count, "not yet");
        clock.Advance(2);
        Eq(1, rang.Count, "rang once");
        Eq((660.0, false), rang[0], "with the whole length (600 + 60)");
        True(!tm.Running, "and stopped");
        tm.Start(60); clock.Advance(30); tm.Cancel(); clock.Advance(120);
        Eq(1, rang.Count, "a canceled timer never rings");
        Eq(60.0, tm.Last, "the last length is remembered");
    }

    [Scenario("a running timer survives a restart; one that ran out meanwhile is reported as such")]
    public static void TimerSurvivesRestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "di-timer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
            var t0 = new DateTime(2026, 11, 15, 10, 0, 0);
            var a = new CountdownTimer(clock) { File = Path.Combine(dir, "timer.json"), Wall = () => t0 };
            a.Start(600);
            var b = new CountdownTimer(clock) { File = a.File, Wall = () => t0.AddSeconds(200) };
            b.Load();
            True(b.Running && Math.Abs(b.Remaining(clock.Now) - 400) < 1, $"picked up with 400 s left (had {b.Remaining(clock.Now):0})");
            b.PauseResume();
            var c = new CountdownTimer(clock) { File = a.File, Wall = () => t0.AddHours(3) };
            c.Load();
            True(c.Running && c.Paused && Math.Abs(c.Remaining(clock.Now) - 400) < 1, "a paused timer stays paused, however long the island was off");
            c.PauseResume();
            var rang = new List<(double, bool)>();
            var d = new CountdownTimer(clock) { File = a.File, Wall = () => t0.AddHours(5) };
            d.Finished += (l, closed) => rang.Add((l, closed));
            d.Load(); clock.Advance(0.1);
            Eq(1, rang.Count, "ran out while the island was off: reported once");
            Eq((600.0, true), rang[0], "as having ended while closed");
            var e = new CountdownTimer(clock) { File = a.File, Wall = () => t0.AddHours(6) };
            var again = 0; e.Finished += (_, _) => again++;
            e.Load(); clock.Advance(0.1);
            Eq(0, again, "and not again on the next start");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Scenario("search: 'timer 10m' offers to start a 10 min timer, and Enter starts it")]
    public static void TimerFromSearch()
    {
        var (isl, host, clock) = NewIsland();
        isl.Svc.Timer = new CountdownTimer(clock);
        var it = Launcher.Answers("timer 10m", isl).FirstOrDefault(x => x.Tag == "Timer");
        Eq("Start a 10 min timer", it?.Title, "offered");
        it.Run();
        True(isl.Svc.Timer.Running && Math.Abs(isl.Svc.Timer.Remaining(clock.Now) - 600) < 1, "started");
        True(!Launcher.Answers("timer soon", isl).Any(x => x.Tag == "Timer"), "not for something that isn't a length");
    }

    [Scenario("the timer's popup stops its alarm however it leaves: dismissed, or timed out")]
    public static void TimerAlertEnds()
    {
        var (isl, host, clock) = NewIsland();
        int stopped = 0;
        isl.Alert(new Alert { Key = "timer", Kind = "timer", Title = "Timer done", Priority = 2, Seconds = 60, Ended = () => stopped++ });
        Eq(Mode.Alert, isl.Mode, "shows (it interrupts anything)");
        isl.DismissAlert("timer"); clock.Advance(0.3, isl);
        Eq(1, stopped, "dismissed: the alarm stops");
        isl.Alert(new Alert { Key = "timer", Kind = "timer", Title = "Timer done", Priority = 2, Seconds = 60, Ended = () => stopped++ });
        clock.Advance(61, isl);
        Eq(2, stopped, "timed out after a minute: stops too");
    }

    [Scenario("Preview popups shows one of every kind, none can jump the queue, and its buttons only close the popup")]
    public static void PreviewPopupsHarmless()
    {
        var dismissed = new List<string>();
        var list = Alerts.Samples(k => dismissed.Add(k), null, new Vortice.Mathematics.Color4(1, 0, 0, 1));
        var kinds = list.Select(a => a.Kind).Distinct().OrderBy(k => k).ToList();
        foreach (var k in new[] { "airpods", "audio", "battery", "bluetooth", "call", "display", "info", "notification", "timer", "track" })
            True(kinds.Contains(k), "includes a " + k + " popup: " + string.Join(",", kinds));
        True(list.All(a => !a.Sticky && a.Priority == 0 && a.Seconds <= 4), "every one passes on its own, in order");
        True(list.Select(a => a.Key).Distinct().Count() == list.Count, "distinct keys, so none replaces another");
        foreach (var a in list) { a.Clicked?.Invoke(); foreach (var act in a.Actions) act.Run(); }
        True(dismissed.All(k => k.StartsWith("preview:")), "the call's Answer and the timer's Again only close the preview");
        Eq(list.Count + list.Sum(a => a.Actions.Count), dismissed.Count, "every click and button was the dismiss");
    }

    [Scenario("shell icons become valid PNGs with straight alpha, for notification popups")]
    public static void IconPng()
    {
        // 2x1: opaque orange, and white at half alpha stored premultiplied (128,128,128,128)
        var bgra = new byte[] { 0x00, 0x88, 0xFF, 0xFF, 128, 128, 128, 128 };
        var png = Png.Encode(bgra, 2, 1);
        True(png.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        int o = 8; var chunks = new List<(string type, byte[] data)>();
        while (o < png.Length)
        {
            int len = (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
            var td = png.Skip(o + 4).Take(4 + len).ToArray();
            uint crc = (uint)((png[o + 8 + len] << 24) | (png[o + 9 + len] << 16) | (png[o + 10 + len] << 8) | png[o + 11 + len]);
            Eq(Png.Crc(td), crc, "chunk checksum");
            chunks.Add((System.Text.Encoding.ASCII.GetString(td, 0, 4), td.Skip(4).ToArray()));
            o += 12 + len;
        }
        Eq("IHDR,IDAT,IEND", string.Join(",", chunks.Select(c => c.type)), "chunks");
        var ihdr = chunks[0].data;
        Eq(2, (ihdr[0] << 24) | (ihdr[1] << 16) | (ihdr[2] << 8) | ihdr[3], "width"); Eq(1, ihdr[7], "height");
        Eq((byte)8, ihdr[8], "8-bit"); Eq((byte)6, ihdr[9], "RGBA");
        using var z = new System.IO.Compression.ZLibStream(new MemoryStream(chunks[1].data), System.IO.Compression.CompressionMode.Decompress);
        var raw = new MemoryStream(); z.CopyTo(raw); var r = raw.ToArray();
        Eq("0,255,136,0,255,255,255,255,128", string.Join(",", r), "filter byte, then RGBA: orange, and white at half alpha (not gray)");
    }

    [Scenario("a late second leave notice doesn't restart the collapse countdown")]
    public static void DuplicateLeaveIgnored()
    {
        var (isl, host, clock) = NewIsland();
        isl.MouseMove(450, 20); isl.Expand(); clock.Advance(1, isl);
        Eq(Mode.Expanded, isl.Mode, "open");
        isl.MouseLeave();                       // noticed by polling
        clock.Advance(0.5, isl);
        isl.MouseLeave();                       // Windows' own notice, late
        clock.Advance(0.35, isl);
        Eq(Mode.Rest, isl.Mode, "collapsed 0.8 s after the pointer left, not 0.8 s after the late notice");
        isl.MouseMove(450, 20); isl.Expand(); clock.Advance(1, isl);
        isl.MouseLeave(); clock.Advance(0.3, isl); isl.MouseMove(450, 20); clock.Advance(1, isl);
        Eq(Mode.Expanded, isl.Mode, "coming back in time still keeps it open");
    }

    [Scenario("settings shows a scene's last-run timestamp as a short local time; other states as they are")]
    public static void EntityStateShown()
    {
        var utc = new DateTime(2026, 11, 13, 20, 21, 0, DateTimeKind.Utc);
        Eq(utc.ToLocalTime().ToString("d MMM, HH:mm", System.Globalization.CultureInfo.InvariantCulture), SettingsWindow.EntityState("2026-11-13T20:21:00.123456+00:00"), "timestamp");
        Eq("off", SettingsWindow.EntityState("off"), "plain state");
        Eq("unavailable", SettingsWindow.EntityState("unavailable"), "unavailable");
        Eq("21.5", SettingsWindow.EntityState("21.5"), "number");
    }

    [Scenario("AirPods' Find My record doesn't rename them 'AirPods Pro 2 - Find My' in the device list")]
    public static void FindMyNameTrimmed()
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var bt = new BluetoothService(clock);
        bt.TestAdd("rec-a", "AirPods Pro 2", false, true, "pods");
        bt.TestAdd("rec-b", "AirPods Pro 2 - Find My", false, true, "pods");
        Eq("AirPods Pro 2", bt.Devices.Single(d => d.ContainerId == "pods").Name, "one device, its own name");
        bt.TestAdd("rec-c", "Buds - Find My", false, true, "other");
        Eq("Buds", bt.Devices.Single(d => d.ContainerId == "other").Name, "a Find My record seen first is trimmed too");
    }

    [Scenario("a phone notification relayed by Phone Link reads as the phone app, with its own title and message")]
    public static void PhoneLinkNotificationShaped()
    {
        var it = new NotificationItem { App = "Phone Link", AppId = PhoneLink.Package + "!App" };
        NotificationFeed.Shape(it, new() { "Home Assistant", "Isola", "island-test: checking" }, "io.robbie.HomeAssistant");
        Eq("Home Assistant", it.App, "app");
        Eq("Phone Link", it.Via, "via");
        Eq("Isola", it.Title, "title");
        Eq("island-test: checking", it.Body, "message");
        Eq("io.robbie.HomeAssistant", it.Bundle, "bundle");

        // a Phone Link line that isn't a known phone app (a call) reads the ordinary way
        var call = new NotificationItem { App = "Phone Link", AppId = PhoneLink.Package + "!App" };
        NotificationFeed.Shape(call, new() { "Incoming call", "Sam" }, null);
        Eq("Incoming call", call.Title, "call title"); Eq("Sam", call.Body, "call body"); Eq("Phone Link", call.App, "call app"); Eq("", call.Via, "call via");

        // ordinary apps are untouched
        var pc = new NotificationItem { App = "Discord", AppId = "com.squirrel.Discord.Discord" };
        NotificationFeed.Shape(pc, new() { "Alex", "hello", "there" }, null);
        Eq("Alex", pc.Title, "pc title"); Eq("hello there", pc.Body, "pc body");
    }

    [Scenario("Phone Link's app groups give name to bundle id, and the cached icon is matched exactly")]
    public static void PhoneLinkAppsAndIcons()
    {
        var m = PhoneLink.Map(new[]
        {
            ("YourPhoneNotifications_io.robbie.HomeAssistant", "Home Assistant"),
            ("YourPhoneNotifications_net.whatsapp.WhatsApp", "\u200eWhatsApp"),
            ("YourPhoneCalling", "Calls"),
            ("YourPhoneMessages_f41e1b24", "Messages"),
        });
        Eq("io.robbie.HomeAssistant", m.GetValueOrDefault("Home Assistant"), "name");
        Eq("net.whatsapp.WhatsApp", m.GetValueOrDefault("WhatsApp"), "direction mark dropped");
        Eq(false, m.ContainsKey("Calls"), "calls aren't an app");
        Eq(2, m.Count, "only app groups");
        Eq(true, PhoneLink.Is("Microsoft.YourPhone_8wekyb3d8bbwe!App"), "is phone link");
        Eq(false, PhoneLink.Is("Microsoft.YourPhoneX!App"), "not phone link");

        var dir = Path.Combine(Path.GetTempPath(), "island-pl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in new[] { "app_icon_com.amazon.Amazon_1.0.jpg", "app_icon_com.amazon.AmazonMusic_1.0.jpg", "app_icon_com.reddit.Reddit1.0.jpg" })
                File.WriteAllBytes(Path.Combine(dir, f), new byte[] { 1 });
            Eq("app_icon_com.amazon.Amazon_1.0.jpg", Path.GetFileName(PhoneLink.IconFile(dir, "com.amazon.Amazon")), "exact, not a longer id");
            Eq("app_icon_com.reddit.Reddit1.0.jpg", Path.GetFileName(PhoneLink.IconFile(dir, "com.reddit.Reddit")), "without the underscore");
            Eq(null, PhoneLink.IconFile(dir, "com.example.none"), "none cached");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Scenario("the sound output popup drops AirPods' Find My suffix and a name repeated in parentheses")]
    public static void OutputNameClean()
    {
        Eq("AirPods Pro 2", Alerts.Output("AirPods Pro 2 (AirPods Pro 2 - Find My)").Title, "airpods popup");
        Eq("AirPods Pro 2", Alerts.OutputName("AirPods Pro 2 (AirPods Pro 2 - Find My)"), "airpods");
        Eq("Speakers (High Definition Audio)", Alerts.OutputName("Speakers (High Definition Audio)"), "speakers untouched");
        Eq("Headphones (Soundcore Motion+)", Alerts.OutputName("Headphones (Soundcore Motion+)"), "other bluetooth untouched");
    }

    [Scenario("Phone Link calls reach the island with notifications off: the feed runs for either switch")]
    public static void CallsWithoutNotifications()
    {
        Eq(true, Isola.App.App.FeedWanted(new Config { Notifications = false, Calls = true }), "calls only");
        Eq(true, Isola.App.App.FeedWanted(new Config { Notifications = true, Calls = false }), "notifications only");
        Eq(false, Isola.App.App.FeedWanted(new Config { Notifications = false, Calls = false }), "neither");
    }
}
