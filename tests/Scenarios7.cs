using System.Runtime.InteropServices;
using System.Text;
using System.Net;
using System.Net.Sockets;
using Isola.App;
using Isola.Island;
using Isola.Services;
using Isola.Native;
using Isola.Shell;
using Isola.Render;
using Vortice.Mathematics;

namespace Isola.Tests;

/// <summary>Bursts: many notifications at once, calls back to back, and hooks that misbehave.</summary>
public static partial class Scenarios
{
    // Runs the island, recording each alert as it comes on screen (key, time shown).
    static List<(string key, double at)> Watch(Island.Island isl, FakeClock clock, double seconds, Action<double> during = null)
    {
        var seen = new List<(string, double)>();
        Alert last = null;
        double end = clock.Now + seconds, start = clock.Now;
        while (clock.Now < end)
        {
            during?.Invoke(clock.Now - start);
            clock.Advance(0.05, isl);
            if (isl.CurrentAlert != null && !ReferenceEquals(isl.CurrentAlert, last)) seen.Add((isl.CurrentAlert.Key, clock.Now - start));
            last = isl.CurrentAlert;
        }
        return seen;
    }

    static Alert Note(int i, string app = "WhatsApp") => Alerts.Notification(i.ToString(), app, "Message " + i, "hello", null, 5);
    static Alert Ring(string id, string who) => Alerts.Call(id, who, null, () => { }, () => { }, () => { });

    [Scenario("a burst of 30 notifications drains in seconds, not minutes, and every one is shown or counted in the summary")]
    public static void NotificationBurstQueues()
    {
        var (isl, host, clock) = NewIsland();
        for (int i = 0; i < 30; i++) { isl.Alert(Note(i, i % 3 == 0 ? "Discord" : "WhatsApp")); clock.Advance(0.03, isl); }
        True(isl.QueuedAlerts.Count(q => q.Kind == "notification") <= Island.Island.BacklogLimit, $"at most {Island.Island.BacklogLimit} waiting individually, got {isl.QueuedAlerts.Count(q => q.Kind == "notification")}");
        var sum = isl.QueuedAlerts.FirstOrDefault(q => q.Kind == "summary");
        True(sum != null, "the rest are summarized");
        int folded = sum.Folded;
        var shown = Watch(isl, clock, 40);
        int individually = shown.Count(s => s.key.StartsWith("notif:") && s.key != "notif:summary");
        Eq(30, individually + folded, "shown or summarized: none lost");
        True(shown.Any(s => s.key == "notif:summary"), "the summary is shown");
        Eq("Discord, WhatsApp", sum.Subtitle.Contains("Discord") && sum.Subtitle.Contains("WhatsApp") ? "Discord, WhatsApp" : sum.Subtitle, "summary names the apps");
        double drained = shown.Last().at;
        True(drained < 25, $"the last one came on screen within 25 s, not after minutes ({drained:0.0} s)");
        Eq(Mode.Rest, isl.Mode, "back to rest");
        Eq(0, isl.QueuedAlerts.Count, "nothing left waiting");
    }

    [Scenario("while others wait, a showing notification makes way after a few seconds instead of its full time")]
    public static void BusyQueueHurries()
    {
        var (isl, host, clock) = NewIsland();
        isl.Alert(Note(1));
        var seen = Watch(isl, clock, 12, t => { if (Math.Abs(t - 1.0) < 0.026) isl.Alert(Note(2)); });
        var second = seen.First(s => s.key == "notif:2").at;
        True(second <= Island.Island.BriskSeconds + 0.6, $"the second came on after ~{Island.Island.BriskSeconds} s, not 5 ({second:0.00} s)");
    }

    [Scenario("a call pushes a showing notification aside, and the notification comes back after the call")]
    public static void CallInterruptsAndReturns()
    {
        var (isl, host, clock) = NewIsland();
        isl.Alert(Note(1)); clock.Advance(0.5, isl);
        isl.Alert(Ring("a", "Mom")); clock.Advance(0.3, isl);
        Eq("call:a", isl.CurrentAlert?.Key, "call on screen");
        True(isl.QueuedAlerts.Any(q => q.Key == "notif:1"), "the notification waits, not dropped");
        isl.DismissAlert("call:a");
        clock.Advance(0.3, isl);
        Eq("notif:1", isl.CurrentAlert?.Key, "the notification is back");
    }

    [Scenario("calls back to back: each in turn, in the order they rang; the next takes over the moment one ends")]
    public static void BackToBackCalls()
    {
        var (isl, host, clock) = NewIsland();
        isl.Alert(Note(1));
        isl.Alert(Ring("a", "Mom")); isl.Alert(Ring("b", "Dad")); isl.Alert(Ring("c", "Sam"));
        isl.Alert(Note(2));
        clock.Advance(0.3, isl);
        Eq("call:a", isl.CurrentAlert?.Key, "first call first");
        Eq("call:b,call:c", string.Join(",", isl.QueuedAlerts.Where(q => q.Kind == "call").Select(q => q.Key)), "the others in the order they rang");
        isl.DismissAlert("call:a"); clock.Advance(0.2, isl);
        Eq("call:b", isl.CurrentAlert?.Key, "second call");
        isl.DismissAlert("call:c"); clock.Advance(0.2, isl);   // the third hung up while waiting
        Eq("call:b", isl.CurrentAlert?.Key, "still the second");
        isl.DismissAlert("call:b"); clock.Advance(0.2, isl);
        Eq("notif:1", isl.CurrentAlert?.Key, "then the notifications, oldest first");
        var rest = Watch(isl, clock, 15);
        True(rest.Any(s => s.key == "notif:2"), "the second notification too");
        Eq(Mode.Rest, isl.Mode, "and rest");
    }

    [Scenario("a call whose end is never seen gives up after 75 s instead of staying forever")]
    public static void CallNeverEndsTimesOut()
    {
        var (isl, host, clock) = NewIsland();
        isl.Alert(Ring("a", "Mom")); isl.Alert(Note(1));
        clock.Advance(60, isl);
        Eq("call:a", isl.CurrentAlert?.Key, "still ringing at 60 s");
        clock.Advance(20, isl);
        True(isl.CurrentAlert?.Key != "call:a", "gone by 80 s");
        Watch(isl, clock, 10);
        Eq(Mode.Rest, isl.Mode, "and the queue moved on");
    }

    [Scenario("a call over the timer: the alarm stops for the call and rings again when the timer's popup returns")]
    public static void CallOverTimer()
    {
        var (isl, host, clock) = NewIsland();
        int shown = 0, quiet = 0;
        isl.Alert(Alerts.Timer(300, false, () => { }, () => isl.DismissAlert("timer"), () => quiet++, () => shown++));
        clock.Advance(0.3, isl);
        Eq(1, shown, "alarm started");
        isl.Alert(Ring("a", "Mom")); clock.Advance(0.3, isl);
        Eq("call:a", isl.CurrentAlert?.Key, "the call outranks the timer");
        Eq(1, quiet, "alarm silenced for the call");
        isl.DismissAlert("call:a"); clock.Advance(0.3, isl);
        Eq("timer", isl.CurrentAlert?.Key, "timer back");
        Eq(2, shown, "alarm rings again");
        isl.DismissAlert("timer"); clock.Advance(0.3, isl);
        Eq(2, quiet, "and stops on Stop");
    }

    [Scenario("an alert raised by another's ended hook stays on screen (it used to be put straight back to rest)")]
    public static void EndedHookRaisesAlert()
    {
        var (isl, host, clock) = NewIsland();
        isl.Alert(new Alert { Key = "first", Title = "First", Seconds = 1, Ended = () => isl.Alert(new Alert { Key = "second", Title = "Second", Seconds = 3 }) });
        clock.Advance(1.3, isl);
        Eq("second", isl.CurrentAlert?.Key, "second showing");
        Eq(Mode.Alert, isl.Mode, "island still showing it");
        clock.Advance(4, isl);
        Eq(Mode.Rest, isl.Mode, "then rest");
    }

    [Scenario("hooks that throw don't break the queue")]
    public static void ThrowingHooksHarmless()
    {
        var (isl, host, clock) = NewIsland();
        isl.Alert(new Alert { Key = "bad", Title = "Bad", Seconds = 1, Shown = () => throw new Exception("boom"), Ended = () => throw new Exception("boom") });
        isl.Alert(Note(1));
        var seen = Watch(isl, clock, 10);
        True(seen.Any(s => s.key == "notif:1"), "the next alert still shows");
        Eq(Mode.Rest, isl.Mode, "and rest");
    }

    [Scenario("a notification dismissed in Windows while queued never shows; the feed hands over an ended call before the next")]
    public static void RemovedWhileQueuedAndFeedOrder()
    {
        var (isl, host, clock) = NewIsland();
        isl.Alert(Note(1)); isl.Alert(Note(2));
        isl.DismissAlert("notif:2");
        var seen = Watch(isl, clock, 10);
        True(!seen.Any(s => s.key == "notif:2"), "dismissed one never shown");

        var feed = new NotificationFeed(clock);
        var log = new List<string>();
        feed.Arrived += n => log.Add("+" + n.Id);
        feed.Removed += n => log.Add("-" + n.Id);
        var now = DateTime.Now;
        feed.Apply(new() { new NotificationItem { Id = 1, At = now, IsCall = true } });   // primes: already there
        feed.Apply(new() { new NotificationItem { Id = 3, At = now.AddSeconds(2) }, new NotificationItem { Id = 2, At = now.AddSeconds(1), IsCall = true } });
        Eq("-1,+2,+3", string.Join(",", log), "ended call out first, then arrivals oldest first");
    }

    sealed class Power : IPowerSource
    {
        public SYSTEM_POWER_STATUS S;
        public SYSTEM_POWER_STATUS Read() => S;
    }

    [Scenario("plugged in at a charge limit isn't charging; a desktop has no battery at all")]
    public static void BatteryStates()
    {
        Ui.Reset(); Ui.Clock = new FakeClock();
        var p = new Power { S = new SYSTEM_POWER_STATUS { ACLineStatus = 1, BatteryFlag = 1, BatteryLifePercent = 93, BatteryLifeTime = -1 } };
        var b = new BatteryService(p); b.Refresh();
        Eq(false, b.Charging, "93% on the charger at a limit: not charging");
        Eq(true, b.PluggedIn, "plugged in");
        Eq("Plugged in", PeekPage.BatteryCaption(b.Charging, b.PluggedIn, b.SecondsLeft), "caption");
        p.S.BatteryFlag = 1 | 8; b.Refresh();
        Eq(true, b.Charging, "charging flag: charging");
        Eq("Charging", PeekPage.BatteryCaption(b.Charging, b.PluggedIn, b.SecondsLeft), "caption charging");
        p.S = new SYSTEM_POWER_STATUS { ACLineStatus = 0, BatteryFlag = 0, BatteryLifePercent = 60, BatteryLifeTime = 3 * 3600 + 12 * 60 }; b.Refresh();
        Eq("3 h 12 min left", PeekPage.BatteryCaption(b.Charging, b.PluggedIn, b.SecondsLeft), "on battery, time known");
        p.S.BatteryLifeTime = -1; b.Refresh();
        Eq("On battery", PeekPage.BatteryCaption(b.Charging, b.PluggedIn, b.SecondsLeft), "still estimating");

        var desk = new BatteryService(new Power { S = new SYSTEM_POWER_STATUS { ACLineStatus = 1, BatteryFlag = 128, BatteryLifePercent = 255, BatteryLifeTime = -1 } });
        desk.Refresh();
        Eq(false, desk.Present, "desktop: no battery");
        Eq(false, desk.Charging, "nor charging");
        Eq(false, desk.PluggedIn, "nor plugged-in badge");
    }

    [Scenario("a desktop (no built-in panel) shows the island on its main screen; a laptop with its panel off still doesn't")]
    public static void DesktopUsesMainScreen()
    {
        Ui.Reset(); Ui.Clock = new FakeClock();
        var d = new FakeDisplays { BuiltIn = false }; d.Tv(primary: true);
        var w = new DisplayWatcher(d); w.Refresh("t");
        Eq(@"\\.\DISPLAY2", w.Laptop?.GdiName, "desktop, default setting: the main screen");
        var lap = new FakeDisplays { BuiltIn = true }; lap.Tv(primary: true);
        var w2 = new DisplayWatcher(lap); w2.Refresh("t");
        Eq(null, w2.Laptop, "laptop with its panel off: still off");
    }

    sealed class FakeBanners : IBannerStore
    {
        public readonly Dictionary<string, int?> Values = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Sounds = new(StringComparer.OrdinalIgnoreCase);
        public string GetSound(string app) => Sounds.TryGetValue(app, out var v) ? v : null;
        public void SetSound(string app, string value) => Sounds[app] = value;
        public void DeleteSound(string app) => Sounds.Remove(app);
        public string Journal;
        public readonly List<string> SetWithoutJournal = new();
        public IReadOnlyList<string> Apps() => Values.Keys.ToList();
        public int? Get(string app) => Values.TryGetValue(app, out var v) ? v : null;
        public void Set(string app, int value)
        {
            // every change must already be in the journal when it is made
            if (value == 0 && (Journal == null || !File.Exists(Journal) || !File.ReadAllText(Journal).Contains(app + "\t"))) SetWithoutJournal.Add(app);
            Values[app] = value;
        }
        public void Delete(string app) => Values[app] = null;
    }

    static string TempJournal() => Path.Combine(Path.GetTempPath(), "island-banners-" + Guid.NewGuid().ToString("N") + ".journal");

    [Scenario("Windows banners: off for every app except ones already off and Phone Link's calls; journaled first; put back exactly")]
    public static void BannersSuppressAndRestore()
    {
        var j = TempJournal();
        var s = new FakeBanners { Journal = j };
        s.Values["Discord"] = null; s.Values["Mail"] = null; s.Values["UserTurnedOff"] = 0; s.Values["Microsoft.YourPhone_8wekyb3d8bbwe!YourPhoneCalling"] = null;
        var b = new WindowsBanners(s, j);
        b.Suppress();
        Eq(0, s.Values["Discord"], "Discord off"); Eq(0, s.Values["Mail"], "Mail off");
        Eq(null, s.Values["Microsoft.YourPhone_8wekyb3d8bbwe!YourPhoneCalling"], "calls untouched (the island presses that popup's buttons)");
        Eq(0, s.Values["UserTurnedOff"], "the user's own off stays");
        Eq(2, b.Count, "two taken");
        Eq(0, s.SetWithoutJournal.Count, "journaled before changing: " + string.Join(",", s.SetWithoutJournal));
        s.Values["NewApp"] = null; b.Suppress();
        Eq(0, s.Values["NewApp"], "an app that appears later is handled on the next pass");
        s.Values["Mail"] = 1;   // the user turned Mail's banners back on meanwhile (explicitly: a value it didn't have)
        b.Restore("test");
        Eq(null, s.Values["Discord"], "Discord back to no value");
        Eq(1, s.Values["Mail"], "Mail: the user's choice stands");
        Eq(0, s.Values["UserTurnedOff"], "the user's own off untouched");
        Eq(null, s.Values["NewApp"], "NewApp back");
        Eq(false, File.Exists(j), "journal gone after restoring");
    }

    [Scenario("a run that died with banners off (and its watcher too) is undone from the journal at the next start")]
    public static void BannersJournalAfterCrash()
    {
        var j = TempJournal();
        var s = new FakeBanners { Journal = j };
        s.Values["Discord"] = null; s.Values["Mail"] = 1;
        new WindowsBanners(s, j).Suppress();      // and then the process is gone, nothing restored
        Eq(0, s.Values["Discord"], "off after the crash");
        Eq(2, WindowsBanners.RestoreJournal(s, j), "two restored");
        Eq(null, s.Values["Discord"], "Discord back"); Eq(1, s.Values["Mail"], "Mail back");
        Eq(false, File.Exists(j), "journal consumed");
        Eq(0, WindowsBanners.RestoreJournal(s, j), "nothing the second time");
    }

    [Scenario("an app muted in the island gets its Windows banners back")]
    public static void BannersReleaseMuted()
    {
        var j = TempJournal();
        var s = new FakeBanners { Journal = j };
        s.Values["Discord"] = null; s.Values["Mail"] = null;
        var b = new WindowsBanners(s, j); b.Suppress();
        b.Release("Discord");
        Eq(null, s.Values["Discord"], "Discord's banners back");
        Eq(0, s.Values["Mail"], "Mail still off");
        Eq(1, WindowsBanners.RestoreJournal(s, j), "journal now holds only Mail");
    }

    [Scenario("banners stay on whenever the island can't show notifications: setting off, screen off, fullscreen")]
    public static void BannersOnlyWhileIslandCanShow()
    {
        var c = new Config { Notifications = true, HideWindowsBanners = true };
        Eq(true, Isola.App.App.BannersWanted(c, true), "on and able: off in Windows");
        Eq(false, Isola.App.App.BannersWanted(c, false), "island can't show: Windows shows them");
        Eq(false, Isola.App.App.BannersWanted(new Config { Notifications = false, HideWindowsBanners = true }, true), "island notifications off");
        try { Packaging.Override = true; Eq(false, Isola.App.App.BannersWanted(c, true), "the Store version never changes Windows' settings (its registry writes are private)"); }
        finally { Packaging.Override = null; }
        Eq(false, Isola.App.App.BannersWanted(new Config { Notifications = true, HideWindowsBanners = false }, true), "setting off");
        var (isl, host, clock) = NewIsland(new Config { Notifications = true });
        Eq(true, isl.CanShowAlerts, "island up: can show");
        isl.SetLaptopPresent(false);
        Eq(false, isl.CanShowAlerts, "laptop screen off: can't");
    }

    [Scenario("a pointer passing over the island on its way somewhere doesn't open it; one that settles on it does")]
    public static void HoverNeedsToSettle()
    {
        var (isl, host, clock) = NewIsland(new Config { HoverToExpand = true, HoverDelayMs = 200 });
        float y = 14;
        // across the island at a calm 600 DIP/s: over it for ~0.35 s, longer than the delay
        bool opened = false;
        for (float x = 340; x <= 560; x += 10) { isl.MouseMove(x, y); clock.Advance(1 / 60.0, isl); opened |= isl.Mode == Mode.Peek; }
        isl.MouseLeave(); clock.Advance(0.5, isl);
        Eq(false, opened, "passing over: it never opened");
        Eq(Mode.Rest, isl.Mode, "still at rest");
        isl.MouseMove(450, y); clock.Advance(0.1, isl);
        isl.MouseMove(452, y + 1); clock.Advance(0.15, isl);   // a hand's tremor is still "resting"
        Eq(Mode.Peek, isl.Mode, "resting on it: it opens");
    }

    [Scenario("a hidden island comes back for a pointer resting at the top, not for one clicking a tab up there")]
    public static void RevealNeedsARest()
    {
        var r = new RevealIntent();
        double now = 0;
        bool Run(double secs, float x, bool button = false, float drift = 0)
        {
            bool fired = false;
            for (double t = 0; t < secs; t += 0.07) { now += 0.07; x += drift; fired |= r.Update(now, x, 2, button); }
            return fired;
        }
        True(!Run(0.25, 500), "a quick visit: nothing");
        r.Reset();
        True(!Run(0.2, 500, button: true), "clicking a tab: nothing");
        True(!Run(1.2, 500), "and nothing just after the click either");
        True(Run(0.9, 500), "then resting there: it comes back");
        r.Reset();
        True(!Run(1.0, 300, drift: 10), "sliding along the tabs: nothing");
    }

    [Scenario("the sign-in task starts the island at normal priority, at sign-in, with the quiet autostart flag; odd paths survive")]
    public static void SignInTaskDefinition()
    {
        string xml = Isola.Shared.Autostart.TaskXml(@"C:\Users\A&B\Programs\Isola\Isola.exe", @"PC\A&B");
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Eq("4", doc.Descendants(ns + "Priority").Single().Value, "normal priority, not Task Scheduler's below-normal 7");
        Eq(@"PC\A&B", doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")?.Value, "fires when this user signs in");
        Eq(@"C:\Users\A&B\Programs\Isola\Isola.exe", doc.Descendants(ns + "Command").Single().Value, "path escaped and intact");
        Eq("--autostart", doc.Descendants(ns + "Arguments").Single().Value, "the quiet flag");
        Eq("LeastPrivilege", doc.Descendants(ns + "RunLevel").Single().Value, "never elevated");
        Eq("false", doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value, "starts on battery too");
    }

    static string TempTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "island-index-" + Guid.NewGuid().ToString("N"));
        foreach (var f in new[] { @"Work\Quarterly Report.xlsx", @"Work\report-notes.txt", @"Photos\Beach 2026\IMG_0001.jpg",
                                  @"code\app\node_modules\report\index.js", @".secret\report.txt", @"Music\Golden Hour - Sample.mp3" })
        {
            var full = Path.Combine(root, f);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
        var hidden = Path.Combine(root, @"Work\hidden report.docx");
        File.WriteAllText(hidden, "x"); File.SetAttributes(hidden, FileAttributes.Hidden);
        return root;
    }

    [Scenario("without Everything, the built-in index finds your files by every word, skipping hidden and dependency folders")]
    public static void BuiltInIndexFindsFiles()
    {
        Ui.Reset(); Ui.Clock = new FakeClock();
        var root = TempTree();
        try
        {
            var idx = new LocalIndex { Roots = new[] { root }, IncludeApps = false };
            idx.BuildNow();
            var hits = LocalIndex.Match(Array.Empty<SearchResult>(), Walked(root), "report", 20).Select(r => r.Name).ToList();
            True(hits.Contains("Quarterly Report.xlsx"), "found by a word in the middle: " + string.Join(", ", hits));
            True(hits.Contains("report-notes.txt"), "and at the start");
            True(!hits.Contains("index.js") && !hits.Any(h => h == "report" ), "node_modules skipped");
            True(!hits.Contains("report.txt"), "dot-folders skipped");
            True(!hits.Contains("hidden report.docx"), "hidden files skipped");
            var two = LocalIndex.Match(Array.Empty<SearchResult>(), Walked(root), "sample golden", 5);
            Eq("Golden Hour - Sample.mp3", two.FirstOrDefault()?.Name, "words in any order");
            Eq(true, idx.Count >= 5, "indexed");
        }
        finally { Directory.Delete(root, true); }
    }

    static List<SearchResult> Walked(string root) => LocalIndex.Walk(root).ToList();

    [Scenario("search uses Everything while it runs and the built-in index when it doesn't; a Store app is found by its name")]
    public static void SearchFallsBackToIndex()
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var root = TempTree();
        try
        {
            var idx = new LocalIndex { Roots = new[] { root }, IncludeApps = false };
            idx.BuildNow();
            bool up = false;
            var fs = new FileSearch(null, idx, () => up);
            True(fs.Note != null, "a note says the built-in index is in use");
            List<SearchResult> got = null;
            fs.Query("quarterly", 10, r => got = r);
            for (int i = 0; i < 200 && got == null; i++) { Thread.Sleep(5); clock.Advance(0.01); }
            Eq("Quarterly Report.xlsx", got?.FirstOrDefault()?.Name, "found without Everything");
            up = true;
            Eq(null, fs.Note, "no note while Everything runs");

            var store = new SearchResult { Name = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", Title = "Calculator", Folder = "shell:AppsFolder", IsApp = true };
            var apps = LocalIndex.Match(new[] { store }, Array.Empty<SearchResult>(), "calc", 5);
            Eq("Calculator", apps.FirstOrDefault()?.Display, "Store app by its display name");
            Eq(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", apps[0].FullPath, "opened through shell:AppsFolder");
        }
        finally { Directory.Delete(root, true); }
    }

    [Scenario("MagicPods installed but not running: the island reads the AirPods itself at once, not after three minutes")]
    public static void AirPodsWithoutMagicPodsRunning()
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var pods = new AirPodsService(clock) { MagicPodsRunning = () => false };
        pods.TestSetMagicPodsFound(true);
        pods.State.UpdatedAt = clock.Now; pods.State.Source = "MagicPods";   // its log said something a moment ago
        Eq(false, pods.MagicPodsInCharge, "closed MagicPods isn't in charge, however fresh its log");
        var running = new AirPodsService(clock) { MagicPodsRunning = () => true };
        running.TestSetMagicPodsFound(true);
        running.State.UpdatedAt = clock.Now; running.State.Source = "MagicPods";
        Eq(true, running.MagicPodsInCharge, "running and fresh: MagicPods leads");
        clock.Advance(200);
        Eq(false, running.MagicPodsInCharge, "its log stale: the island takes over");
    }

    [Scenario("a notification lends the glow its app icon's colors, then the glow returns to the music - or to the next song's")]
    public static void GlowFollowsNotificationIcon()
    {
        var (isl, host, clock) = NewIsland(new Config { GlowColors = "auto" });
        Color4 pink = Col.Hex(0xff66aa), green = Col.Hex(0x25d366), blue = Col.Hex(0x3355ff);
        var media = new FakeMedia(clock) { Current = new MediaInfo { Title = "Golden Hour", Playing = true, Palette = new[] { pink, pink, pink } } };
        media.LastPlayingAt = clock.Now; isl.Svc.Media = media;
        isl.UpdateGlowColors();
        Eq(pink, isl.GlowTarget0, "album colors");
        var n = Alerts.Notification("1", "WhatsApp", "Sam", "hi", new byte[] { 1 }, 4);
        n.Palette = new[] { green, green, green };            // what the icon gives
        isl.Alert(n); clock.Advance(0.3, isl);
        Eq(green, isl.GlowTarget0, "the notification's app color while it shows");
        media.Current = new MediaInfo { Title = "Next", Playing = true, Palette = new[] { blue, blue, blue } };   // the song changed meanwhile
        isl.TrackChanged(null, media.Current);
        Eq(green, isl.GlowTarget0, "still the notification's while it's up");
        clock.Advance(5, isl);
        Eq(Mode.Rest, isl.Mode, "gone");
        Eq(blue, isl.GlowTarget0, "then the new song's colors");
        var plain = new Alert { Key = "x", Kind = "bluetooth", Title = "AirPods", Seconds = 2 };
        isl.Alert(plain); clock.Advance(0.3, isl);
        Eq(blue, isl.GlowTarget0, "alerts without an app icon leave the glow alone");
    }

    [Scenario("setup: Everything found by its uninstall entry, Windows 10 2004+ required, and 'don't start at sign-in' written into the config")]
    public static void SetupChecksAndChoices()
    {
        Eq(true, SetupChecks.IsEverythingEntry("Everything 1.4.1.1032 (x64)", "voidtools"), "Everything's own entry");
        Eq(false, SetupChecks.IsEverythingEntry("Everything Toolbar", "stnkl"), "someone else's 'Everything'");
        Eq(false, SetupChecks.IsEverythingEntry(null, "voidtools"), "no name");
        Eq(true, SetupChecks.WindowsSupported(new Version(10, 0, 26300)), "Windows 11");
        Eq(true, SetupChecks.WindowsSupported(new Version(10, 0, 19041)), "Windows 10 2004");
        Eq(false, SetupChecks.WindowsSupported(new Version(10, 0, 18363)), "Windows 10 1909");

        string Off(string j) => Isola.Shared.Autostart.WithSetting(j, false);
        bool Reads(string j) => System.Text.Json.JsonDocument.Parse(j).RootElement.GetProperty("StartWithWindows").GetBoolean();
        var existing = "{\n  \"Style\": \"notch\",\n  \"StartWithWindows\": true\n}";
        Eq(false, Reads(Off(existing)), "replaced");
        True(Off(existing).Contains("\"Style\": \"notch\""), "the rest kept");
        var without = "{\n  \"Style\": \"notch\"\n}";
        Eq(false, Reads(Off(without)), "added to a config without it");
        Eq("notch", System.Text.Json.JsonDocument.Parse(Off(without)).RootElement.GetProperty("Style").GetString(), "and still valid JSON with the rest");
        Eq(false, Reads(Off("")), "a new file");
        Eq(false, Reads(Off("{}")), "an empty object");
    }

    [Scenario("over an installed island the wizard offers an update only to something newer, and never repairs backwards")]
    public static void WizardMaintenanceChoices()
    {
        Version v(string s) => Version.Parse(s);
        var older = SetupWizard.Choices(v("1.0.8"), v("1.0.9"), null);
        Eq(true, older.update, "setup newer than installed: update"); Eq(v("1.0.9"), older.updateTo, "to this setup");
        var online = SetupWizard.Choices(v("1.0.8"), v("1.0.9"), v("1.0.11"));
        Eq(v("1.0.11"), online.updateTo, "GitHub has newer still: update to that");
        var same = SetupWizard.Choices(v("1.0.9"), v("1.0.9"), v("1.0.9"));
        Eq(false, same.update, "up to date: no update"); Eq(true, same.repair, "repair the same version");
        var oldSetup = SetupWizard.Choices(v("1.0.9"), v("1.0.5"), null);
        Eq(false, oldSetup.repair, "an older setup doesn't 'repair' backwards"); Eq(false, oldSetup.update, "nor update");
        var oldSetupNewOnline = SetupWizard.Choices(v("1.0.9"), v("1.0.5"), v("1.0.10"));
        Eq(true, oldSetupNewOnline.update, "an old setup can still update to GitHub's newer release");
    }

    [Scenario("the glow's edge colors stay apart as the pools drift past each other, and move without jumps")]
    public static void GlowEdgeStopsApart()
    {
        float[] prev = null;
        for (double now = 0; now < 600; now += 0.05)
        {
            var xs = new[] { 0.20f + 0.13f * (float)Math.Sin(now * 0.23 + 0.4), 0.50f + 0.17f * (float)Math.Sin(now * 0.17 + 2.1), 0.80f + 0.13f * (float)Math.Sin(now * 0.29 + 4.0) };
            var st = Island.Island.EdgeStops(xs);
            True(st[1] - st[0] >= 0.199f && st[2] - st[1] >= 0.199f, $"at {now:0.0} s the stops {st[0]:0.000}, {st[1]:0.000}, {st[2]:0.000} came too close");
            True(st[0] > 0 && st[2] < 1, "inside the line");
            if (prev != null) for (int k = 0; k < 3; k++) True(Math.Abs(st[k] - prev[k]) < 0.02f, $"stop {k} jumped at {now:0.0} s");
            prev = st;
        }
    }

    [Scenario("'help' lists every command and every kind of answer, and each tip's example really gets an answer")]
    public static void PaletteHelp()
    {
        var (isl, host, clock) = NewIsland();
        var typed = new List<string>();
        var help = Launcher.Help(isl, s => typed.Add(s));
        Eq(Launcher.CommandCount, help.Count(h => h.Tag == "Command"), "every command listed");
        True(Launcher.CommandCount >= 60, $"a good number of commands ({Launcher.CommandCount})");
        True(help.Count(h => h.Tag == "Tip") >= 15, "tips for the answers and prefixes");
        foreach (var q in new[] { "help", "?", "commands" }) Eq(true, Launcher.IsHelp(q), q);
        foreach (var ex in new[] { "15% of 80", "5 km to mi", "#ff8800", "time in tokyo", "password", "uuid", "now", "github.com", "epoch 1700000000" })
            True(Launcher.Answers(ex, isl).Any(a => a.Answer), $"the tip's example '{ex}' gets an answer");
        Eq("= 12", SearchPage.Calc("15% of 80"), "percent of");
        Eq("= 2.5", SearchPage.Calc("10% of 5*5"), "percent of an expression");
        var pw = Launcher.Password(32);
        Eq(32, pw.Length, "password length");
        True(pw.Any(char.IsDigit) || pw.Any(ch => !char.IsLetterOrDigit(ch)) || pw.Any(char.IsUpper), "mixed characters");
        var tip = help.First(h => h.Tag == "Tip" && h.Run != null);
        tip.Run(); Eq(1, typed.Count, "picking a tip types its example");
    }

    [Scenario("Windows' chime goes silent with its banner and comes back exactly: a custom sound kept, the default restored, the user's change left")]
    public static void BannersSilenceTheChime()
    {
        var j = TempJournal();
        var s = new FakeBanners { Journal = j };
        s.Values["Discord"] = null; s.Values["Mail"] = null; s.Values["Teams"] = null;
        s.Sounds["Mail"] = @"C:\Windows\Media\chimes.wav";       // a sound the user picked
        var b = new WindowsBanners(s, j);
        b.Suppress();
        Eq("", s.GetSound("Discord"), "Discord silent"); Eq("", s.GetSound("Mail"), "Mail silent");
        s.Sounds["Teams"] = @"C:\Windows\Media\ding.wav";        // the user changed Teams meanwhile
        b.Restore("test");
        Eq(null, s.GetSound("Discord"), "Discord back to Windows' default");
        Eq(@"C:\Windows\Media\chimes.wav", s.GetSound("Mail"), "Mail's own sound back");
        Eq(@"C:\Windows\Media\ding.wav", s.GetSound("Teams"), "the user's change stands");

        // a journal written before sounds were handled still restores the banner
        File.WriteAllLines(j, new[] { "Old\t0\t1" });
        s.Values["Old"] = 0;
        Eq(1, WindowsBanners.RestoreJournal(s, j), "old journal read");
        Eq(null, s.Values["Old"], "its banner back");
    }

    [Scenario("every island sound renders as real audio at the chosen level; looping ones end in silence so they don't click")]
    public static void IslandSoundsRender()
    {
        foreach (var kind in Sounds.All)
        foreach (var (variant, _) in Sounds.Choices(kind))
        {
            var wav = Sounds.Render(kind, 0.5f, variant);
            Eq("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4), kind + ": a WAV");
            int samples = (wav.Length - 44) / 2;
            True(samples > 44100 / 5, kind + ": long enough to hear");
            short peak = 0; for (int i = 44; i + 1 < wav.Length; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(wav, i)));
            True(Math.Abs(peak / 32767f - 0.45f) < 0.02f, $"{kind}/{variant}: peaks at the level asked ({peak / 32767f:0.000})");
            Eq(0, (int)BitConverter.ToInt16(wav, wav.Length - 2), kind + ": ends at zero");
        }
        var quiet = Sounds.Render(Sounds.Notification, 0f);
        True(quiet.Skip(44).All(b => b == 0), "volume 0 is silence");
    }

    [Scenario("sounds start with their popup and a looping one stops when it leaves or a call pushes it aside")]
    public static void SoundsFollowAlerts()
    {
        var (isl, host, clock) = NewIsland();
        var log = new List<string>();
        isl.SoundOn = a => log.Add("+" + a.Sound);
        isl.SoundOff = a => { if (a.Sound != null && Sounds.Loops(a.Sound)) log.Add("-" + a.Sound); };
        isl.Alert(Alerts.Timer(60, false, () => { }, () => isl.DismissAlert("timer"), null)); clock.Advance(0.3, isl);
        isl.Alert(Ring("a", "Mom")); clock.Advance(0.3, isl);
        isl.DismissAlert("call:a"); clock.Advance(0.3, isl);
        isl.DismissAlert("timer"); clock.Advance(0.3, isl);
        isl.Alert(Note(1)); clock.Advance(6, isl);
        Eq("+timer,-timer,+call,-call,+timer,-timer,+notification", string.Join(",", log), "start, step aside, back, stop");

        var c = new Config();
        Eq(true, Isola.App.App.SoundAllowed(c, Note(1)), "notification chime on by default");
        Eq(false, Isola.App.App.SoundAllowed(new Config { Sounds = false }, Note(1)), "master switch off");
        Eq(false, Isola.App.App.SoundAllowed(new Config { CallRingtone = false }, Ring("a", "x")), "ringtone off");
        Eq(false, Isola.App.App.SoundAllowed(new Config { TimerSound = false }, Alerts.Timer(60, false, () => { }, () => { }, null)), "timer sound off");
        Eq(false, Isola.App.App.SoundAllowed(c, Alerts.Timer(60, true, () => { }, () => { }, null)), "a timer that ended while closed is quiet");
        Eq(false, Isola.App.App.SoundAllowed(new Config { DeviceSounds = false }, Alerts.Device("x", "AirPods", 'x', true, -1, null)), "device sounds off");
    }

    [Scenario("apps whose ids hold backslashes (PowerShell's) are found at their nested settings key, not the empty parent")]
    public static void BannersFindNestedApps()
    {
        var tree = new Dictionary<string, (string[] subs, bool values)>
        {
            [""] = (new[] { "Discord", "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", "Microsoft.YourPhone_8wekyb3d8bbwe!App" }, false),
            ["Discord"] = (Array.Empty<string>(), true),
            ["Microsoft.YourPhone_8wekyb3d8bbwe!App"] = (Array.Empty<string>(), true),
            [@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = (new[] { "WindowsPowerShell" }, false),
            [@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell"] = (new[] { "v1.0" }, false),
            [@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0"] = (new[] { "powershell.exe" }, false),
            [@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe"] = (Array.Empty<string>(), true),
        };
        var apps = RegistryBanners.LeafApps(p => tree[p]);
        True(apps.Contains(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe"), "PowerShell's real key: " + string.Join(" | ", apps));
        True(!apps.Contains("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"), "not the empty parent");
        Eq(3, apps.Count, "Discord, Phone Link, PowerShell");
    }

    [Scenario("each sound kind offers several choices, each really different; an unknown choice falls back to the default")]
    public static void SoundChoices()
    {
        foreach (var kind in new[] { Sounds.Notification, Sounds.Call, Sounds.Timer, Sounds.Connect })
        {
            var ch = Sounds.Choices(kind);
            True(ch.Length >= 3, kind + ": at least three to choose from");
            var renders = ch.Select(c => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Sounds.Render(kind, 0.5f, c.id)))).ToList();
            Eq(ch.Length, renders.Distinct().Count(), kind + ": every choice sounds different");
            Eq(ch[0].id, Sounds.Valid(kind, "no-such-sound"), kind + ": unknown falls back to the default");
        }
        var c2 = new Config { Ringtone = "nonsense" }; c2.Normalize();
        Eq("marimba", c2.Ringtone, "a bad ringtone in the config is replaced by the default");
        Eq("classic", Isola.App.App.ToneFor(new Config { Ringtone = "classic" }, Sounds.Call), "the chosen ringtone is used");
    }

    [Scenario("images decode safely under a storm of album art and icons on several threads, with garbage collections in between")]
    public static void ImageDecodeStorm()
    {
        Gfx.InitImaging();
        var imgs = Enumerable.Range(0, 8).Select(k =>
        {
            var px = new byte[48 * 48 * 4];
            for (int i = 0; i < 48 * 48; i++) { px[i * 4] = (byte)(k * 30); px[i * 4 + 1] = (byte)(i % 255); px[i * 4 + 2] = (byte)(255 - k * 20); px[i * 4 + 3] = 255; }
            return Png.Encode(px, 48, 48, false);
        })
        // the formats album art and site icons come in: some decoders keep the stream longer than PNG's
        .Concat(new[] { "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" }.Select(Convert.FromBase64String)).ToArray();
        int ok = 0, bad = 0;
        var threads = Enumerable.Range(0, 4).Select(n => new Thread(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                var p = MediaService.PaletteOf(imgs[(i + n) % imgs.Length]);
                if (p is { Length: 3 }) Interlocked.Increment(ref ok); else Interlocked.Increment(ref bad);
                if (i % 7 == 0) MediaService.PaletteOf(new byte[] { 1, 2, 3, 4 });   // junk data: fails, must not poison anything
            }
        })).ToList();
        threads.ForEach(th => th.Start());
        while (threads.Any(th => th.IsAlive)) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(2); }
        for (int r = 0; r < 5; r++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Eq(1200, ok + bad, "all ran");
        True(ok >= 1200 * 8 / 12, $"the PNGs and the other formats WIC reads gave palettes ({ok} ok, {bad} failed)");
    }

    [Scenario("a site autoplaying silent previews doesn't flood the island with track popups; a real new song is announced once")]
    public static void TrackPopupsCalm()
    {
        var (isl, host, clock) = NewIsland(new Config { ShowOnTrackChange = true });
        isl.SetMode(Mode.Hidden, "test");
        var media = new FakeMedia(clock); isl.Svc.Media = media;
        var spotify = new MediaInfo { AppName = "Spotify", Title = "DARK TIME SUNSHINE", Artist = "Ninth Key", Playing = true, SessionId = "spotify" };
        var site = new MediaInfo { AppName = "Chrome", Title = "A site is playing media", Artist = "", Playing = true, SessionId = "chrome" };
        int popups = 0; string last = null;
        void Flip(MediaInfo m) { var old = media.Current; media.Current = m; isl.TrackChanged(old, m); }
        for (int i = 0; i < 30; i++)
        {
            Flip(i % 2 == 0 ? site : spotify);
            for (int f = 0; f < 24; f++) { clock.Advance(1 / 60.0, isl); if (isl.Mode == Mode.Alert && isl.CurrentAlert?.Kind == "track" && !ReferenceEquals(last, isl.CurrentAlert.Title + popups)) { popups++; last = isl.CurrentAlert.Title + popups; } }
        }
        clock.Advance(10, isl);
        True(popups <= 1, $"flapping for 12 s gave {popups} popup(s)");
        Eq(true, Island.Island.IsPlaceholder(site), "a site's placeholder title is never announced");
        // a new song that keeps playing: announced once
        popups = 0; last = null;
        var next = new MediaInfo { AppName = "Spotify", Title = "Golden Hour", Artist = "Sample", Playing = true, SessionId = "spotify" };
        Flip(next);
        for (int f = 0; f < 300; f++) { clock.Advance(1 / 60.0, isl); if (isl.Mode == Mode.Alert && isl.CurrentAlert?.Kind == "track" && last == null) { popups++; last = isl.CurrentAlert.Title; } }
        Eq(1, popups, "a settled new song is announced");
        Eq("Golden Hour", last, "the right one");
    }

    [Scenario("a crash brings the island back, at most three times in ten minutes; quitting or ending it doesn't")]
    public static void CrashRestarts()
    {
        Eq(true, WindowsBanners.Crashed(0xC0000409), "fail-fast (the crash seen)");
        Eq(true, WindowsBanners.Crashed(0xC0000005), "access violation");
        Eq(true, WindowsBanners.Crashed(0xE0434352), ".NET unhandled exception");
        Eq(false, WindowsBanners.Crashed(0), "quit");
        Eq(false, WindowsBanners.Crashed(1), "Task Manager / taskkill");
        Eq(false, WindowsBanners.Crashed(0xFFFFFFFF), "ended by a program (setup during an update)");
        Eq(false, WindowsBanners.Crashed(0xC000013A), "Ctrl+C");
        var f = Path.Combine(Path.GetTempPath(), "isola-restarts-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var t0 = new DateTime(2026, 11, 15, 12, 0, 0);
            Eq(true, WindowsBanners.AllowRestart(f, t0), "first");
            Eq(true, WindowsBanners.AllowRestart(f, t0.AddMinutes(1)), "second");
            Eq(true, WindowsBanners.AllowRestart(f, t0.AddMinutes(2)), "third");
            Eq(false, WindowsBanners.AllowRestart(f, t0.AddMinutes(3)), "a fourth within ten minutes: no (a crash loop)");
            Eq(true, WindowsBanners.AllowRestart(f, t0.AddMinutes(12)), "later again: yes");
        }
        finally { File.Delete(f); }
    }

    [Scenario("a notification's message wraps to up to three lines and the popup grows to fit, instead of being cut off at one")]
    public static void NotificationMessageWraps()
    {
        Eq(1, AlertPage.DetailLines("ok"), "short: one line");
        Eq(2, AlertPage.DetailLines(new string('x', 70)), "a sentence: two");
        Eq(3, AlertPage.DetailLines(new string('x', 400)), "a paragraph: three at most");
        var (isl, host, clock) = NewIsland();
        isl.Alert(Alerts.Notification("1", "WhatsApp", "Sam", new string('m', 160), null, 5)); clock.Advance(0.6, isl);
        var (w, h, r) = isl.P.AlertPage.Size(isl);
        Eq(70f + 16 * 3, h, "the popup is three message lines tall");
    }

    [Scenario("a Dynamic Island install moves to Isola: settings and sign-ins come along, the GitHub token and old logs don't")]
    public static void LegacyMigration()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "isola-legacy-" + Guid.NewGuid().ToString("N"));
        string oldData = Path.Combine(tmp, "DynamicIsland"), newData = Path.Combine(tmp, "Isola");
        try
        {
            Directory.CreateDirectory(oldData);
            File.WriteAllText(Path.Combine(oldData, "dynamic_island.config.json"), "{\n  \"Style\": \"notch\",\n  \"HaToken\": \"secret-ha\",\n  \"UpdateToken\": \"github_pat_secret\",\n  \"UpdateRepo\": \"someone/private\"\n}");
            File.WriteAllText(Path.Combine(oldData, "spotify.json"), "{\"refresh\":\"r\"}");
            File.WriteAllText(Path.Combine(oldData, "banners.journal"), "Discord\t0\t1");
            File.WriteAllText(Path.Combine(oldData, "dynamic_island.log"), "old log");
            Directory.CreateDirectory(newData);
            File.WriteAllText(Path.Combine(newData, "setup.log"), "new setup log");
            IsolaSetup.Legacy.MoveData(oldData, newData, _ => { });
            var cfg = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(newData, "isola.config.json")))!.AsObject();
            Eq("notch", cfg["Style"]?.ToString(), "settings kept");
            Eq("secret-ha", cfg["HaToken"]?.ToString(), "Home Assistant token kept (the user's own connection)");
            Eq(false, cfg.ContainsKey("UpdateToken"), "the GitHub token is gone");
            Eq(false, cfg.ContainsKey("UpdateRepo"), "and the old repository setting");
            Eq(true, File.Exists(Path.Combine(newData, "spotify.json")), "Spotify sign-in moved");
            Eq(true, File.Exists(Path.Combine(newData, "banners.journal")), "a pending popup restore moved, so Isola finishes it");
            Eq(false, File.Exists(Path.Combine(newData, "dynamic_island.log")), "old logs left behind");
            Eq("new setup log", File.ReadAllText(Path.Combine(newData, "setup.log")), "existing files win");
            Eq(false, Directory.Exists(oldData), "old folder gone");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Scenario("the Home tab shows only when Home Assistant is set up and reachable, and the island stays on its tab when Home comes or goes")]
    public static void HomeTabOnlyWhenUsable()
    {
        var (isl, host, clock) = NewIsland(new Config { HomeAssistant = true });
        var fake = new FakeHa();
        var ha = new HomeAssistant(clock, fake) { BaseUrl = () => "", Token = () => "", Wanted = () => new[] { "light.desk" } };
        isl.Svc.Ha = ha;
        var dash = isl.P.Dashboard;
        Eq(false, dash.TabIds.Contains("home"), "not set up: no Home tab");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            ha.BaseUrl = () => $"http://127.0.0.1:{port}"; ha.Token = () => "t";
            fake.Entity("light.desk", "off", "Desk");
            // nothing opens the Home tab, Settings or search: the island has to notice by itself
            bool Settle(Func<bool> done) { for (int i = 0; i < 100 && !done(); i++) { clock.Advance(0.05); Thread.Sleep(10); } return done(); }
            clock.Advance(61); Settle(() => !ha.Busy && ha.Status == HaStatus.Online);
            Eq(true, dash.TabIds.Contains("home"), "set up and reachable, with the island closed: Home tab");
            isl.Tab = dash.TabIndex("timer");
            fake.Status = HttpStatusCode.Unauthorized;
            clock.Advance(61); Settle(() => !ha.Busy && ha.Status != HaStatus.Online);
            Eq(true, dash.TabIds.Contains("home"), "a short outage keeps the tab");
            clock.Advance(120); Settle(() => !ha.Busy);
            Eq(false, dash.TabIds.Contains("home"), "gone for minutes: the tab goes");
            Eq("timer", dash.CurrentTabId, "and the island stays on the tab it was on");
            fake.Status = HttpStatusCode.OK;
            clock.Advance(61); Settle(() => !ha.Busy && ha.Status == HaStatus.Online);
            Eq(true, dash.TabIds.Contains("home"), "back online: the tab comes back without opening anything");
        }
        finally { listener.Stop(); }
    }

    [Scenario("a crafted search reply (huge lengths, truncated fields) is read safely: nothing past the buffer, good items kept")]
    public static unsafe void EverythingReplyBounds()
    {
        // header: 5 uints (totalFolders, numItems, offset, request flags, sort); then items (flags, offset)
        const uint REQ_NAME = 0x1, REQ_PATH = 0x2;
        var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
        bw.Write(0u); bw.Write(3u); bw.Write(0u); bw.Write(REQ_NAME | REQ_PATH); bw.Write(0u);
        long itemsAt = ms.Position; for (int i = 0; i < 6; i++) bw.Write(0u);
        uint Good(string name, string path)
        {
            uint off = (uint)ms.Position;
            bw.Write((uint)name.Length); bw.Write(Encoding.Unicode.GetBytes(name + "\0"));
            bw.Write((uint)path.Length); bw.Write(Encoding.Unicode.GetBytes(path + "\0"));
            return off;
        }
        uint ok1 = Good("notes.txt", @"C:\Docs");
        uint evil = (uint)ms.Position; bw.Write(0x7FFFFFFFu);            // a name "two billion characters long"
        uint ok2 = Good("photo.jpg", @"C:\Pics");
        uint cut = (uint)ms.Position; bw.Write(40u); bw.Write(Encoding.Unicode.GetBytes("short"));   // claims 40 chars, buffer ends
        ms.Position = itemsAt; bw.Write(0u); bw.Write(ok1); bw.Write(0u); bw.Write(evil); bw.Write(0u); bw.Write(ok2);
        var buf = ms.ToArray();
        // and an item table that claims more items than fit
        List<SearchResult> r;
        fixed (byte* p = buf) r = EverythingSearch.ParseList2(p, buf.Length);
        Eq("notes.txt,photo.jpg", string.Join(",", r.Select(x => x.Name)), "the good items, the crafted one skipped");
        var tail = buf.Take((int)cut + 12).ToArray();
        tail[4] = 200;                                                        // numItems: 200
        fixed (byte* p = tail) r = EverythingSearch.ParseList2(p, tail.Length);
        True(r.Count <= 3, "an item count larger than the reply is cut to what's there");
        fixed (byte* p = buf) r = EverythingSearch.ParseList2(p, 12);
        Eq(0, r.Count, "a reply too short for its header: nothing");
    }

    [Scenario("tokens are saved encrypted for this Windows user, read back, and a plain token from an older config still works")]
    public static void TokensEncryptedAtRest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "isola-secret-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "isola.config.json");
            new Config { HaToken = "eyJhbGciOi-secret-token" }.Save(f);
            string onDisk = File.ReadAllText(f);
            True(!onDisk.Contains("eyJhbGciOi-secret-token"), "the token isn't on disk in plain text");
            True(onDisk.Contains("dpapi:"), "it's there encrypted");
            Eq("eyJhbGciOi-secret-token", Config.Load(f).HaToken, "and reads back");
            File.WriteAllText(f, "{ \"HaToken\": \"plain-old-token\" }");
            Eq("plain-old-token", Config.Load(f).HaToken, "an older plain token still works");
            True(!File.ReadAllText(f).Contains("plain-old-token"), "and loading it once encrypts it on disk");
            Eq("plain-old-token", Config.Load(f).HaToken, "still the same token afterwards");
            Eq("", Secret.Unprotect("dpapi:bm90LWZvci10aGlzLXVzZXI="), "a value encrypted elsewhere reads as empty, never as junk");
            Eq("", Secret.Protect(""), "empty stays empty (the shipped example config)");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Scenario("the log keeps which app a notification or call came from, never the name or message")]
    public static void LogKeepsNoMessageText()
    {
        var (isl, host, clock) = NewIsland();
        string log = Path.Combine(Path.GetTempPath(), "isola-log-" + Guid.NewGuid().ToString("N") + ".log");
        Log.Init(log);
        try
        {
            isl.Alert(Alerts.Notification("1", "WhatsApp", "Jordan Smith", "meet at 6 by the station", null, 5));
            isl.Alert(Ring("c", "Jordan Smith"));
            Log.Flush(); Thread.Sleep(200); Log.Flush();
            string text = File.Exists(log) ? File.ReadAllText(log) : "";
            True(text.Contains("WhatsApp"), "the app is logged");
            True(!text.Contains("Jordan") && !text.Contains("station"), "no names or messages: " + text.Replace("\n", " | "));
        }
        finally { Log.Init(null); try { File.Delete(log); } catch { } }
    }

    [Scenario("Settings warns only about a remote Home Assistant reached over plain http, not local addresses")]
    public static void HaInsecureRemoteWarning()
    {
        foreach (var ok in new[] { "http://homeassistant.local:8123", "http://192.168.1.20:8123", "http://10.0.0.5", "http://172.20.1.1", "http://localhost:8123", "http://homeassistant:8123", "http://100.101.1.2", "https://ha.example.com", "" })
            Eq(false, HomeAssistant.InsecureRemote(ok), ok);
        foreach (var bad in new[] { "http://ha.example.com", "http://203.0.113.9:8123" })
            Eq(true, HomeAssistant.InsecureRemote(bad), bad);
    }

    [Scenario("hovering previews on a web page doesn't flip the island between them and the song; media that keeps playing still takes over")]
    public static void MediaSessionsSettle()
    {
        var spotify = new MediaInfo { SessionId = "Spotify.exe", AppName = "Spotify", Title = "After Death", Artist = "From Ashes to New", Playing = true, PlayingSince = 0 };
        var all = new List<MediaInfo> { spotify };
        Func<MediaInfo, bool> none = _ => false;
        MediaInfo current = spotify;
        double now = 100;
        int flips = 0;
        void Step(string windowsCurrent)
        {
            var (pick, _) = MediaService.Pick(current, all, null, windowsCurrent, none, now);
            if (!ReferenceEquals(pick, current)) flips++;
            current = pick;
        }
        // as logged: Chrome's session appears playing, Windows makes it "current", and it closes 0.7 s later
        for (int i = 0; i < 20; i++)
        {
            var chrome = new MediaInfo { SessionId = "Chrome", AppName = "Chrome", Title = "A site is playing media", Playing = true, PlayingSince = now };
            all.Add(chrome); Step("Chrome");
            now += 0.35; Step("Chrome");
            now += 0.35; all.Remove(chrome); Step("Spotify.exe");
            now += 0.05;
        }
        Eq(0, flips, "the island stays on Spotify through 20 hover previews");
        Eq("Spotify", current.AppName, "and shows Spotify");

        // a video that keeps playing does take over, once it has played a moment
        var video = new MediaInfo { SessionId = "Chrome", AppName = "Chrome", Title = "A site is playing media", Playing = true, PlayingSince = now };
        all.Add(video);
        var (p1, recheck) = MediaService.Pick(current, all, null, "Chrome", none, now + 1);
        Eq("Spotify", p1.AppName, "a placeholder playing for 1 s waits");
        True(Math.Abs(recheck - (now + MediaService.PlaceholderSettle)) < 1e-9, "and asks to be looked at again when its wait is up: " + recheck);
        var (p2, _) = MediaService.Pick(current, all, null, "Chrome", none, now + MediaService.PlaceholderSettle + 0.01);
        Eq("Chrome", p2.AppName, "after the wait it is shown");
        all.Remove(video);

        // a real title waits less
        var yt = new MediaInfo { SessionId = "Chrome", AppName = "Chrome", Title = "Lo-fi beats", Artist = "Some Channel", Playing = true, PlayingSince = now };
        all.Add(yt);
        Eq("Spotify", MediaService.Pick(current, all, null, "Chrome", none, now + 1).pick.AppName, "a titled video playing for 1 s still waits");
        Eq("Chrome", MediaService.Pick(current, all, null, "Chrome", none, now + MediaService.Settle + 0.01).pick.AppName, "and is shown after 1.5 s");

        // picking a session yourself is instant, and the shown one closing falls back at once
        Eq("Chrome", MediaService.Pick(current, all, "Chrome", "Chrome", none, now).pick.AppName, "a session you pick shows at once");
        var (p3, _) = MediaService.Pick(yt, new List<MediaInfo> { spotify }, null, "Spotify.exe", none, now);
        Eq("Spotify", p3.AppName, "when the shown session closes, the island falls back immediately");
        Eq("Chrome", MediaService.Pick(null, all, null, "Chrome", none, now).pick.AppName, "with nothing shown yet, what's playing shows at once");
    }

    [Scenario("sound previews: a new pick isn't cut short by the last one's stop, short sounds start with silence for a dozing output (longer on Bluetooth), and a refused PlaySound is retried")]
    public static void SoundPreviews()
    {
        var (_, _, clock) = NewIsland();
        var calls = new List<(int silentSamples, uint flags)>();
        bool refuseNext = false;
        var oldOut = Sounds.Output; var oldSilence = Sounds.Silence; var oldBt = Sounds.OutputIsBluetooth;
        // count the leading silent samples of the WAV PlaySound is handed (data starts at byte 44)
        Sounds.Output = (wav, flags) =>
        {
            int bytes = Marshal.ReadInt32(wav, 40), n = 0;
            while (n < bytes / 2 && Marshal.ReadInt16(wav, 44 + n * 2) == 0) n++;
            calls.Add((n, flags));
            if (refuseNext) { refuseNext = false; return false; }
            return true;
        };
        Sounds.Silence = () => { };
        try
        {
            Sounds.Stop();
            // a ringtone picked two seconds after another plays its full preview
            Sounds.Preview(Sounds.Call, 0.6f, "marimba");
            clock.Advance(2);
            Sounds.Preview(Sounds.Call, 0.6f, "classic");
            clock.Advance(1.3);
            Eq("call:classic", Sounds.Playing, "still playing 1.3 s in (the first pick's stop used to end it here)");
            clock.Advance(2.0);
            Eq(null, Sounds.Playing, "and stops after its own 3.2 s");
            True(calls[^1].silentSamples < 2205, $"a looping sound has no lead-in, its rhythm stays even ({calls[^1].silentSamples} silent samples)");

            // a chime starts with silence the output can wake up on; longer on Bluetooth
            calls.Clear();
            Sounds.OutputIsBluetooth = () => false;
            Sounds.Play(Sounds.Notification, 0.6f, "glass");
            True(calls[^1].silentSamples >= (int)(Sounds.LeadIn * 44100) * 0.95, $"wired: {calls[^1].silentSamples} silent samples first");
            Sounds.OutputIsBluetooth = () => true;
            Sounds.Play(Sounds.Notification, 0.6f, "glass");
            True(calls[^1].silentSamples >= (int)(Sounds.LeadInBluetooth * 44100) * 0.95, $"Bluetooth: {calls[^1].silentSamples} silent samples first");

            // a refused PlaySound is tried once more
            calls.Clear(); refuseNext = true;
            Sounds.Play(Sounds.Connect, 0.6f, "soft");
            Eq(2, calls.Count, "asked twice");
            Eq("connect:soft", Sounds.Playing, "and it plays");
        }
        finally { Sounds.Stop(); Sounds.Output = oldOut; Sounds.Silence = oldSilence; Sounds.OutputIsBluetooth = oldBt; }
    }

    [Scenario("a Bluetooth output is recognized by its device's container or name, also when its bus says INTELAUDIO (Intel's Bluetooth offload)")]
    public static void BluetoothOutputDetected()
    {
        // as read on a real laptop: AirPods' audio output on INTELAUDIO, sharing the AirPods' container
        var airpods = new BtDevice { Name = "AirPods Pro 2", ContainerId = "{4FBB8107-B5F4-53CB-967E-878471744784}" };
        var paired = new[] { airpods, new BtDevice { Name = "MX Master 3", ContainerId = "{11111111-2222-3333-4444-555555555555}" } };
        var c = new Guid("4fbb8107-b5f4-53cb-967e-878471744784");
        Eq(true, AudioService.IsBluetoothOutput("INTELAUDIO", c, "Headphones (AirPods Pro 2 - Find My)", paired), "INTELAUDIO, same container: Bluetooth");
        Eq(true, AudioService.IsBluetoothOutput("INTELAUDIO", c, "Headphones (Renamed in Sound settings)", paired), "the container alone is enough, whatever the output is called");
        Eq(true, AudioService.IsBluetoothOutput("INTELAUDIO", new Guid("5c043815-6c05-5e1d-9ffd-44f86b015be5"), "Headphones (AirPods Pro 2 - Find My)", paired), "a stale container, but the paired device's name: Bluetooth");
        Eq(true, AudioService.IsBluetoothOutput("BTHENUM", Guid.Empty, "Speaker (Something)", Array.Empty<BtDevice>()), "a Bluetooth bus: Bluetooth");
        Eq(false, AudioService.IsBluetoothOutput("HDAUDIO", new Guid("00000000-0000-0000-ffff-ffffffffffff"), "Speakers (Realtek(R) Audio)", paired), "the built-in speakers aren't");
        Eq(false, AudioService.IsBluetoothOutput("INTELAUDIO", new Guid("cdcae645-6e5e-5f75-a10f-adbbd63226c0"), "Speakers (Intel Smart Sound)", paired), "INTELAUDIO speakers that aren't a paired device aren't");
    }

    [Scenario("the Microsoft Store version never updates itself: no GitHub check, even when asked, and Settings says the Store does it")]
    public static void StoreVersionDoesntSelfUpdate()
    {
        var (_, _, clock) = NewIsland();
        var gh = new FakeGitHub { ReleasesJson = "[{\"tag_name\":\"v9.0.0\",\"draft\":false,\"prerelease\":false,\"body\":\"sha256: " + new string('a', 64) + "\",\"assets\":[]}]" };
        var upd = new Updater(clock, gh) { Current = new Version(1, 1, 0), IsDev = () => false, Cfg = () => new Config() };
        try
        {
            Packaging.Override = true;
            upd.Start();
            upd.CheckAsync(true).Wait();
            clock.Advance(7 * 3600);
            Eq(0, gh.Requests.Count, "no request to GitHub, scheduled or asked for");
            Eq("Updated by the Microsoft Store", upd.Status, "status");
            Packaging.Override = false;
            upd.CheckAsync(true).Wait();
            True(gh.Requests.Count > 0, "the GitHub install still checks");
        }
        finally { Packaging.Override = null; }
    }
}
