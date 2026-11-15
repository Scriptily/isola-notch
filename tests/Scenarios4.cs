using Isola.Island;
using Isola.Render;
using System.Security.Cryptography;
using System.Text;
using Isola.App;
using Isola.Services;

namespace Isola.Tests;

public static partial class Scenarios
{
    static (Updater upd, FakeGitHub gh, FakeClock clock, List<string> events) NewUpdater(byte[] setup, string notesSha)
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var gh = new FakeGitHub
        {
            Asset = setup,
            ReleasesJson = "[{\"tag_name\":\"v1.1.0\",\"draft\":false,\"prerelease\":false,\"body\":\"Isola 1.1.0\\n\\nsha256: " + notesSha + "\"," +
                           "\"assets\":[{\"name\":\"IsolaSetup.exe\",\"url\":\"https://api.github.com/repos/Scriptily/isola-notch/releases/assets/7\"}]}]",
        };
        var upd = new Updater(clock, gh) { Current = new Version(1, 0, 0), IsDev = () => false, Cfg = () => new Config() };
        var events = new List<string>();
        upd.Installing += v => events.Add("quit " + v);
        return (upd, gh, clock, events);
    }

    [Scenario("an update quits the island only once the setup is running; a setup that won't start leaves the island running")]
    public static void UpdaterHandsOverSafely()
    {
        byte[] setup = Encoding.ASCII.GetBytes("pretend installer " + Guid.NewGuid());
        string sha = Convert.ToHexString(SHA256.HashData(setup)).ToLowerInvariant();

        var (upd, gh, clock, events) = NewUpdater(setup, sha);
        upd.StartInstaller = (file, args) => { events.Add("start"); return false; };
        upd.CheckAsync(true).Wait(); clock.Advance(0.1);
        True(!events.Any(e => e.StartsWith("quit")), "the setup didn't start, so the island must not quit: " + string.Join(", ", events));
        True(upd.Status.Contains("installer"), "says why: " + upd.Status);

        (upd, gh, clock, events) = NewUpdater(setup, sha);
        upd.StartInstaller = (file, args) => throw new System.ComponentModel.Win32Exception(5, "Access is denied");
        upd.CheckAsync(true).Wait(); clock.Advance(0.1);
        True(!events.Any(e => e.StartsWith("quit")), "a setup that throws on start doesn't take the island down either");

        (upd, gh, clock, events) = NewUpdater(setup, sha);
        string ranFile = null;
        upd.StartInstaller = (file, args) => { ranFile = file; events.Add("start " + args); return true; };
        upd.CheckAsync(true).Wait(); clock.Advance(0.1);
        Eq(2, events.Count, "start, then quit: " + string.Join(", ", events));
        True(events[0] == "start /update /wait " + Environment.ProcessId, "setup started first and told to wait for this process: " + events[0]);
        Eq("quit 1.1.0", events[1], "then the island quits");
        True(File.Exists(ranFile) && File.ReadAllBytes(ranFile).SequenceEqual(setup), "it ran the verified download");
        True(gh.Requests.All(r => r.EndsWith("auth=")), "no credentials sent: the repository is public");
        try { File.Delete(ranFile); } catch { }

        (upd, gh, clock, events) = NewUpdater(setup, new string('0', 64));
        upd.StartInstaller = (file, args) => { events.Add("start"); return true; };
        upd.CheckAsync(true).Wait(); clock.Advance(0.1);
        Eq(0, events.Count, "a download that doesn't match the notes' sha256 is never started");
        True(upd.Status.Contains("verify"), "says so: " + upd.Status);
    }

    [Scenario("a media timeline report that agrees with the progress bar doesn't wake the island; a seek or a new track length does")]
    public static void TimelineReportsOnlyWhenMoved()
    {
        True(!MediaService.TimelineMoved(42.0, 42.3, 180, 180), "Spotify's routine report while playing (0.3 s off)");
        True(!MediaService.TimelineMoved(42.0, 42.0, 180, 180), "the same report while paused");
        True(MediaService.TimelineMoved(42.0, 90.0, 180, 180), "a seek forward");
        True(MediaService.TimelineMoved(42.0, 10.0, 180, 180), "a seek back");
        True(MediaService.TimelineMoved(0, 0, 0, 212), "the length arriving for a new track");
    }

    [Scenario("the resting eyes draw while the pointer moves near them, and not while it rests there")]
    public static void EyesDrawOnlyWhilePointerMoves()
    {
        var (isl, host, clock) = NewIsland();
        Eq("idle", isl.CurrentPage?.Key, "resting face");
        host.Pointer = (450 + 200, 150, true);                 // near the island, still
        True(isl.CursorPollInterval <= 1 / 30.0 + 1e-9, "pointer near: looked at often");
        _ = isl.NeedsContinuousFrames; clock.Advance(1.0, isl);       // first look at the pointer, then it rests
        True(!isl.NeedsContinuousFrames, "a pointer resting near the island asks for no frames");
        host.Pointer = (450 + 180, 140, true);                 // it moves
        True(isl.NeedsContinuousFrames, "moving: the eyes follow, frame by frame");
        clock.Advance(0.2, isl);
        True(isl.NeedsContinuousFrames, "still drawing just after it stops");
        clock.Advance(0.9, isl);                               // the gaze eases in, then settles
        True(!isl.NeedsContinuousFrames, "and done a moment later");
        host.Pointer = (450, 900, true);                       // far away
        True(!isl.NeedsContinuousFrames, "far away: nothing to follow");
        True(isl.CursorPollInterval > 0.1, "and looked at only now and then");
    }

    [Scenario("shuffle and repeat show what you chose until Spotify catches up, so quick clicks build on each other")]
    public static void MediaHoldsYourChoice()
    {
        double until = 0;
        // click shuffle on at t=10: held for 3 s
        until = 13;
        Eq((bool?)true, MediaService.Hold<bool>(false, true, 10.4, ref until), "Spotify's stale 'off' a moment later doesn't undo the button");
        Eq(13.0, until, "still holding");
        Eq((bool?)true, MediaService.Hold<bool>(true, true, 10.9, ref until), "Spotify agrees");
        Eq(0.0, until, "agreement ends the hold");
        Eq((bool?)false, MediaService.Hold<bool>(false, true, 11.5, ref until), "after that, Spotify's word goes (changed in the app)");
        until = 13;
        Eq((bool?)false, MediaService.Hold<bool>(false, true, 13.1, ref until), "a hold that Spotify never confirms runs out");
        until = 0;
        Eq((int?)2, MediaService.Hold<int>(2, null, 5, ref until), "nothing held: Spotify's value");
    }

    [Scenario("pointing at a row's hover-only button (a note's trash icon) keeps the row hovered, so the button stays to be clicked")]
    public static void HoverButtonsStay()
    {
        var ux = new Ux();
        bool deleted = false;
        float rowHover = 0;
        void Frame(float dt)
        {
            ux.BeginFrame(null);
            ux.Add("note:1", new Box(0, 0, 300, 34), () => { });                     // the row (stops short of the button); clicking it toggles done
            rowHover = ux.HoverT("note:1");
            if (rowHover > 0.05f) ux.Add("note:1:del", new Box(304, 4, 26, 26), () => deleted = true);
            ux.Add("note:2", new Box(0, 38, 300, 34), () => { });
            ux.EndFrame();
            ux.Step(dt);
        }
        ux.Move(150, 17);                                                           // over the row
        for (int i = 0; i < 30; i++) Frame(1 / 60f);
        True(rowHover > 0.9f, "row hovered");
        ux.Move(317, 17);                                                           // over to the trash icon
        for (int i = 0; i < 30; i++) Frame(1 / 60f);
        True(rowHover > 0.9f, $"still hovered while on its button (was {rowHover:0.00}; the button vanished under the pointer)");
        ux.Down(317, 17); ux.Up(317, 17);
        True(deleted, "the click lands on the button");
        ux.Move(150, 55);                                                           // another row
        for (int i = 0; i < 30; i++) Frame(1 / 60f);
        True(rowHover < 0.1f, "leaving the row ends its hover");
    }
}
