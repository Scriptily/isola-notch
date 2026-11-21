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
}
