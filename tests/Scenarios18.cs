using Isola.App;
using Isola.Island;
using Isola.Services;

namespace Isola.Tests;

/// <summary>Camera and microphone use, named as apps start and stop (from Windows' record of it).</summary>
public static partial class Scenarios
{
    sealed class FakeRecord
    {
        public readonly Dictionary<string, DeviceUse> Uses = new();
        public void Set(string device, string app, bool on) => Uses[device + app] = new DeviceUse(device, app, app.ToLowerInvariant(), on);
        public IEnumerable<DeviceUse> Read() => Uses.Values.ToList();
    }

    static (PrivacyWatch w, FakeRecord rec, FakeClock clock, List<string> told) Watch()
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var rec = new FakeRecord();
        var w = new PrivacyWatch(clock, rec.Read);
        var told = new List<string>();
        w.Changed += (d, a, on) => told.Add($"{a} {(on ? "on" : "off")} {d}");
        return (w, rec, clock, told);
    }

    [Scenario("camera and microphone: an app starting and stopping is named (a moment after, so a flicker isn't); what's in use when Isola starts isn't news")]
    public static void DeviceUseIsNamed()
    {
        var (w, rec, clock, told) = Watch();
        rec.Set("microphone", "NVIDIA Container", true);   // recording before Isola started
        w.Start(); clock.Advance(5);
        Eq(0, told.Count, "already in use at the start: not news");
        rec.Set("camera", "Microsoft Teams", true); clock.Advance(0.5);
        Eq(0, told.Count, "not after half a second (a flicker isn't told)");
        clock.Advance(1);
        Eq("Microsoft Teams on camera", told.LastOrDefault(), "then named");
        rec.Set("camera", "Microsoft Teams", false); clock.Advance(3);
        Eq("Microsoft Teams off camera", told.LastOrDefault(), "and when it stops");
        rec.Set("camera", "Zoom", true); clock.Advance(0.3); rec.Set("camera", "Zoom", false); clock.Advance(3);
        Eq(2, told.Count, "a camera opened and closed in a blink: nothing (no stop for a start never told)");
    }

    [Scenario("an app that turns the microphone on and off all day (Medal, OBS) is named once, then left quiet for a minute at a time")]
    public static void BackgroundRecordersStayQuiet()
    {
        var (w, rec, clock, told) = Watch();
        w.Start(); clock.Advance(1);
        for (int k = 0; k < 36; k++) { rec.Set("microphone", "Medal", k % 2 == 0); clock.Advance(5); }   // 3 minutes of on, off, on...
        True(told.Count <= 6, $"three minutes of toggling: {told.Count} notices, not 36 ({string.Join("; ", told)})");
        True(told.Count >= 2, "but it's still named now and then");
    }

    [Scenario("camera and microphone notices are brief and silent; Store apps read as words")]
    public static void DeviceUseNotices()
    {
        var on = Alerts.DeviceUse("camera", "Microsoft Teams", true);
        Eq("Using the camera", on.Subtitle, "what it's doing");
        True(on.Seconds <= 3 && on.Sound == null, "brief, and no sound");
        Eq("Stopped using the microphone", Alerts.DeviceUse("microphone", "OBS Studio", false).Subtitle, "stopping");
        Eq("Windows Camera", PrivacyWatch.StoreName("Microsoft.WindowsCamera_8wekyb3d8bbwe"), "a Store app's name");
        Eq("obs64", PrivacyWatch.NameOf(@"Z:#nowhere#obs64.exe", true), "a program that isn't there: its file name");
        Eq(true, new Config().DeviceUseNotices, "on by default");
    }

    [Scenario("an app that drops off what's live (it closed the camera) counts as stopped")]
    public static void DeviceUseGoneIsStopped()
    {
        var (w, rec, clock, told) = Watch();
        w.Start(); clock.Advance(1);
        rec.Set("camera", "Windows Hello", true); clock.Advance(2);
        Eq("Windows Hello on camera", told.LastOrDefault(), "named");
        rec.Uses.Clear(); clock.Advance(3);
        Eq("Windows Hello off camera", told.LastOrDefault(), "gone from the list: stopped");
    }

    [Scenario("with Stay while media plays it stays while playing, hides after inactivity once stopped, comes back when media starts; hidden by hand, it stays hidden; Stay off, it hides mid-song")]
    public static void StayAndComeBackTogether()
    {
        var (isl, _, clock) = NewIsland(new Config { AutoHideMinutes = 2, StayWhilePlaying = true });
        var media = new FakeMedia(clock) { Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "A song", Playing = true } };
        isl.Svc.Media = media; isl.ActivityChanged(); isl.MarkActive();
        clock.Advance(600, isl);
        Eq(Mode.Rest, isl.Mode, "playing: it stays (10 minutes)");
        media.Current.Playing = false; isl.ActivityChanged();
        clock.Advance(130, isl);
        Eq(Mode.Hidden, isl.Mode, "stopped: it hides itself after 2 minutes");
        media.Current.Playing = true; isl.ActivityChanged(); clock.Advance(0.5, isl);
        Eq(Mode.Rest, isl.Mode, "it starts again: back");
        clock.Advance(600, isl);
        Eq(Mode.Rest, isl.Mode, "and stays while it plays");
        isl.RightClick(450, 20); clock.Advance(0.5, isl);
        Eq(Mode.Hidden, isl.Mode, "hidden by hand while playing");
        media.Current.Playing = false; isl.ActivityChanged(); clock.Advance(5, isl);
        media.Current.Playing = true; isl.ActivityChanged(); clock.Advance(0.5, isl);
        Eq(Mode.Hidden, isl.Mode, "a pause and play doesn't bring back one you hid");

        // Stay off: it hides after inactivity even while playing, and only comes back on the next start
        var (isl2, _, clock2) = NewIsland(new Config { AutoHideMinutes = 2, StayWhilePlaying = false });
        var m2 = new FakeMedia(clock2) { Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "A song", Playing = true } };
        isl2.Svc.Media = m2; isl2.ActivityChanged(); isl2.MarkActive();
        clock2.Advance(130, isl2);
        Eq(Mode.Hidden, isl2.Mode, "Stay off: it hides while the music plays");
        clock2.Advance(60, isl2);
        Eq(Mode.Hidden, isl2.Mode, "and doesn't keep coming straight back (the music didn't start, it was already playing)");
        m2.Current.Playing = false; isl2.ActivityChanged(); m2.Current.Playing = true; isl2.ActivityChanged(); clock2.Advance(0.5, isl2);
        Eq(Mode.Rest, isl2.Mode, "paused and played: it comes back for the start");
    }

    [Scenario("a list's edge fades come in gradually as it scrolls away from that end, and go as it arrives (they switched on in one frame)")]
    public static void ScrollFadesRamp()
    {
        var (t0, b0) = Render.Canvas.ScrollFade(0, 300);
        Eq(0f, t0, "at the top: no top fade"); Eq(1f, b0, "the bottom fades (more below)");
        var (t1, _) = Render.Canvas.ScrollFade(4, 300);
        True(t1 > 0 && t1 < 0.3f, $"4 px down: the top fade has only just begun ({t1:0.00})");
        var (t2, _) = Render.Canvas.ScrollFade(13, 300);
        True(t2 > 0.4f && t2 < 0.6f, $"halfway in at 13 px ({t2:0.00})");
        Eq(1f, Render.Canvas.ScrollFade(40, 300).top, "full by 27 px");
        var (_, b3) = Render.Canvas.ScrollFade(290, 300);
        True(b3 > 0.3f && b3 < 0.45f, $"10 px from the bottom: the bottom fade easing off ({b3:0.00})");
        Eq(0f, Render.Canvas.ScrollFade(300, 300).bottom, "at the bottom: none");
        Eq((0f, 0f), Render.Canvas.ScrollFade(0, -50), "a list that fits: no fades");
    }

    [Scenario("an app's first notification ever, which Windows showed too, is recognized; apps whose banners are already off (by Isola or by you) aren't")]
    public static void FirstNotificationRecognized()
    {
        var store = new FakeBanners();
        store.Values["App.Known"] = 1;
        var b = new WindowsBanners(store, Path.Combine(Path.GetTempPath(), "isola-banners-" + Guid.NewGuid().ToString("N")));
        Eq(false, b.WouldShow("App.Known"), "not active yet: nothing to say");
        b.Suppress();
        Eq(false, b.WouldShow("App.Known"), "an app Isola turned off: no banner");
        Eq(true, b.WouldShow("MicrosoftWindows.Client.StudioEffects"), "a brand-new app: Windows showed its banner");
        store.Values["App.UserOff"] = 0;
        Eq(false, b.WouldShow("App.UserOff"), "banners off by you: none showed");
        b.Restore("test");
    }

    [Scenario("a camera user that can't be looked into (a protected system process) still shows: Windows Hello on the IR camera")]
    public static void ProtectedCameraUserNamed()
    {
        Eq("Windows Hello", PrivacyWatch.AppOf(0xFFFFFFF0, true), "no path, on the IR camera: Windows Hello's face recognition");
        Eq("Windows", PrivacyWatch.AppOf(0xFFFFFFF0, false), "no path, another camera: Windows");
        var uses = PrivacyWatch.Live(() => new(), () => new List<CameraUse> { new(0xFFFFFFF0, "IR Camera", true) }).ToList();
        Eq("Windows Hello", uses.Single().App, "and it reaches the watcher instead of being dropped");
    }
}
