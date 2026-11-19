using Isola.App;
using Isola.Island;
using Isola.Render;
using Isola.Services;
using Box = Isola.Render.Box;

namespace Isola.Tests;

/// <summary>Touchscreens: what a finger does on the island and in Settings, and the motion that goes with it.</summary>
public static partial class Scenarios
{
    /// <summary>Anything built from Ux zones, as a finger sees it, with a log of what reached it.</summary>
    sealed class FakeTouchTarget : ITouchTarget
    {
        public readonly Ux Ux = new();
        public Ux TouchUx => Ux;
        public readonly List<string> Log = new();
        public void MouseMove(float x, float y) => Ux.Move(x, y);
        public void MouseDown(float x, float y) { if (!Ux.Down(x, y)) Log.Add("background down"); }
        public void MouseUp(float x, float y) { if (!Ux.Up(x, y)) Log.Add("background up"); }
        public void DragTo(float dx, float dy) => Log.Add($"tug {dy:0}");
        public void DragEnd(float dx, float dy, float v) => Log.Add($"tug end {dy:0} at {v:0}");
        public void Swipe(float dx, float dy) => Log.Add($"swipe {dx:0},{dy:0}");
        public void TouchLifted() { Log.Add("lifted"); Ux.Leave(); }
        public void Build(Action<Ux> zones) { Ux.BeginFrame(null); zones(Ux); Ux.EndFrame(); }
    }

    /// <summary>A finger from (x0, y0) to (x1, y1) in <paramref name="steps"/> moves, <paramref name="dt"/> apart.</summary>
    static void Finger(TouchInput t, float x0, float y0, float x1, float y1, int steps = 6, double dt = 1 / 60.0, double t0 = 10)
    {
        t.Down(x0, y0, t0);
        for (int i = 1; i <= steps; i++) t.Move(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps, t0 + i * dt);
        t.Up(x1, y1, t0 + steps * dt + 0.01);
    }

    [Scenario("a tap presses the control under it, and a fingertip just beside a control still presses it")]
    public static void TouchTapsAndSlop()
    {
        var f = new FakeTouchTarget(); int taps = 0;
        f.Build(u => u.Add("btn", new Box(100, 100, 30, 30), () => taps++));
        var t = new TouchInput(f);
        Finger(t, 115, 115, 115, 115, 0);
        Eq(1, taps, "a tap on the button");
        Finger(t, 137, 115, 137, 115, 0);   // 7 px right of its edge
        Eq(2, taps, "a fingertip 7 px off its edge");
        Finger(t, 160, 115, 160, 115, 0);   // 30 px off: that's not aiming at it
        Eq(2, taps, "30 px away isn't a tap on it");
        True(f.Ux.Slop == 0, "the slop is only for while a finger is down");
        f.Ux.Move(137, 115);
        True(f.Ux.HitTest(137, 115) == null, "a mouse 7 px off misses, as it always did");
        True(f.Ux.Hot == null || f.Log.Contains("lifted"), "the finger leaves no hover behind");
    }

    [Scenario("a finger drags a slider sideways, scrolls a list up and down (1:1, a flick carries on), tugs the island's body and swipes")]
    public static void TouchDragsScrollsTugsSwipes()
    {
        var f = new FakeTouchTarget();
        float slider = -1; bool final = false; float scrolled = 0; int clicks = 0;
        f.Build(u =>
        {
            u.Add("list", new Box(0, 200, 300, 200), null, 0).Wheel = (d, h) => scrolled += -d / 120f * 50;
            u.Add("row", new Box(0, 220, 300, 40), () => clicks++);
            var s = u.Add("vol", new Box(20, 100, 200, 20), null); s.Drag = (x, y, fin) => { slider = (x - 20) / 200; final |= fin; };
        });
        var t = new TouchInput(f);
        t.Down(40, 110, 1); for (int i = 1; i <= 6; i++) t.Move(40 + 20 * i, 111, 1 + i / 60.0);
        True(Math.Abs(slider - 0.7f) < 0.01f && !final, $"the slider follows the finger while it's down ({slider:0.00})");
        t.Up(160, 111, 1.2);
        True(final, "and is let go when the finger lifts");
        // up 120 px over the list, starting on a row: the list scrolls, the row isn't clicked
        t.Down(150, 380, 5); for (int i = 1; i <= 6; i++) t.Move(150, 380 - 20 * i, 5 + i * 0.05);
        True(Math.Abs(scrolled - 120) < 1, $"the list follows the finger 1:1 ({scrolled:0})");
        t.Up(150, 260, 5.31);
        Eq(0, clicks, "starting on a row and scrolling doesn't click it");
        scrolled = 0;
        Finger(t, 150, 380, 150, 300, 4, 1 / 120.0);
        True(scrolled > 80 + 100, $"a quick flick carries on past where the finger stopped ({scrolled:0} for 80 px)");
        scrolled = 0;
        t.Down(150, 380, 20); t.Move(150, 340, 20.02); t.Move(150, 300, 20.04); t.Up(150, 300, 20.3);
        True(Math.Abs(scrolled - 80) < 1, $"a finger that stopped before lifting doesn't throw ({scrolled:0})");
        // on the body (no control, no list): a vertical drag tugs, a sideways one swipes
        f.Log.Clear();
        Finger(t, 260, 40, 260, 100);
        True(f.Log.Contains("tug 60") && f.Log.Any(l => l.StartsWith("tug end 60")), "a drag down the body tugs it: " + string.Join(", ", f.Log));
        f.Log.Clear();
        Finger(t, 260, 40, 180, 44);
        True(f.Log.Contains("swipe -80,4"), "sideways is a swipe: " + string.Join(", ", f.Log));
    }

    [Scenario("search results scroll a step per 80 px of finger travel, as a wheel notch does")]
    public static void TouchScrollsNotchedLists()
    {
        var f = new FakeTouchTarget(); int steps = 0;
        f.Build(u => { var z = u.Add("sr", new Box(0, 0, 300, 400), null, 0); z.Notched = true; z.ScrollPx = 80; z.Wheel = (d, h) => steps += d < 0 ? 1 : -1; });
        var t = new TouchInput(f);
        t.Down(100, 350, 1); for (int i = 1; i <= 10; i++) t.Move(100, 350 - 25 * i, 1 + i * 0.05); t.Up(100, 100, 1.6);
        Eq(3, steps, "250 px up is three steps down");
    }

    [Scenario("an island opened with a tap stays open (a lifted finger isn't the pointer leaving), then closes when left alone")]
    public static void TouchKeepsTheIslandOpen()
    {
        var (isl, host, clock) = NewIsland();
        Eq(Mode.Rest, isl.Mode, "at rest");
        var t = new TouchInput(isl);
        t.Down(450, 20, clock.Now); t.Up(450, 20, clock.Now + 0.08);
        clock.Advance(0.5, isl);
        Eq(Mode.Expanded, isl.Mode, "a tap opens it");
        clock.Advance(3, isl);
        Eq(Mode.Expanded, isl.Mode, "still open 3 s later, though no pointer is over it");
        True(isl.Ux.TouchMode, "the island knows the last input was a finger");
        clock.Advance(Island.Island.TouchIdleClose, isl);
        Eq(Mode.Rest, isl.Mode, "left alone, it closes");
    }

    [Scenario("released from a tug, the island keeps the speed it was really moving at (the finger's, through the rubber band)")]
    public static void TugReleaseKeepsItsSpeed()
    {
        var (isl, host, clock) = NewIsland();
        isl.DragTo(0, 30);
        isl.DragEnd(0, 30, 600);   // not far enough to open search
        float want = 600 * Island.Island.RubberSlope(30, 140);
        True(Math.Abs(isl.PullVelocity - want) < 1, $"starts at {want:0} DIP/s, got {isl.PullVelocity:0}");
        True(isl.PullVelocity > 600 * 0.4 && isl.PullVelocity < 600, "more than the old 40% hand-off, less than the finger's own speed (it was banded)");
    }

    [Scenario("a page scrolled by finger follows 1:1, rubber-bands past its ends (never past 64 px) and flings at the finger's speed")]
    public static void PagePanFollowsBandsAndFlings()
    {
        var p = new PagePan();
        p.Begin(100, 500, 400, 0);
        Eq(160f, p.Move(340, 0.05), "60 px up scrolls 60 px");
        p.Begin(0, 500, 300, 1);
        float band = p.Move(500, 1.2);   // pulled 200 px down at the top
        True(band < 0 && band > -64, $"past the top it gives a little ({band:0.0})");
        True(p.Move(1300, 1.4) > -64, "and never more than 64 px however far");
        True(Math.Abs(p.Move(305, 1.5)) < 0.01f, "the first 12 px past an end don't move it at all");
        p.Move(420, 1.6);
        var (to, v) = p.End(1.8);
        Eq(0f, to, "let go past the top, it settles at the top");
        p.Begin(200, 500, 400, 2);
        p.Move(380, 2.016); p.Move(360, 2.032);   // flicking up fast
        (to, v) = p.End(2.04);
        True(v > 600 && to > 240 + 100, $"a flick keeps going ({v:0} px/s, heading for {to:0})");
        p.Begin(200, 500, 400, 3); p.Move(360, 3.05);
        (to, v) = p.End(3.4);
        True(v == 0 && to == 240, "stopped before lifting: it stays");
    }

    [Scenario("tooltips: the first waits 600 ms, the next ones along a row appear at once, and after a pause they wait again")]
    public static void TooltipsWarmUp()
    {
        var u = new Ux();
        u.BeginFrame(null);
        u.Add("a", new Box(0, 0, 40, 40), () => { }).Tooltip = "Copy";
        u.Add("b", new Box(50, 0, 40, 40), () => { }).Tooltip = "Open";
        u.Add("c", new Box(100, 0, 40, 40), () => { });
        u.EndFrame();
        u.Move(20, 20);
        True(u.Tip(0) == null, "not at once");
        True(u.Tip(0.3) == null, "not at 300 ms");
        True(Math.Abs(u.TipDue - 0.4) < 1e-9, "due at 400 ms, so the window wakes for it");
        var t = u.Tip(0.41);
        True(t?.zone.Id == "a" && t.Value.animate, "the first shows at 400 ms and animates in");
        u.Move(70, 20);
        t = u.Tip(0.5);
        True(t?.zone.Id == "b" && !t.Value.animate, "the next one shows at once, without animating");
        u.Move(120, 20); True(u.Tip(0.6) == null, "a control without a tip shows none");
        u.Move(20, 20);
        True(u.Tip(0.75)?.zone.Id == "a", "back within 300 ms: at once");
        u.Move(500, 500); u.Tip(1.0);
        u.Move(20, 20);
        True(u.Tip(1.5) == null && u.Tip(1.8) == null && u.Tip(1.91) != null, "after a pause, the wait again");
        u.Down(20, 20);
        True(u.Tip(1.7) == null, "never while pressing");
    }

    [Scenario("the setup wizard slides forward pages in from the right and back pages in from the left")]
    public static void WizardSlidesByDirection()
    {
        int[] order = { 0, 1, 2, 3 };
        Eq(1, SetupWizard.SlideDir(order, 0, 1), "Next");
        Eq(-1, SetupWizard.SlideDir(order, 1, 0), "Back");
        Eq(0, SetupWizard.SlideDir(order, -1, 0), "the first page just appears");
        Eq(1, SetupWizard.SlideDir(order, 5, 0), "from the replace-the-Store-copy page to Welcome is forward");
    }

    [Scenario("a press takes 3% off a control, whatever its size")]
    public static void PressesShrinkThreePercent()
    {
        using var o = new Offscreen(440, 120);
        var u = new Ux();
        void Frame() { o.Draw(c => { u.BeginFrame(c); u.Pill("p", new Box(20, 40, 400, 30), "Go", () => { }, Col.White); u.EndFrame(); }); u.Step(1 / 60.0); }
        Frame();
        u.Move(220, 55); u.Down(220, 55);
        for (int i = 0; i < 60; i++) Frame();
        float p = u.PressT("p");
        True(p > 0.99f, $"held long enough to settle ({p:0.00})");
        int Width() { var col = o.AlphaRow(55); int n = 0; foreach (var a in col) if (a > 20) n++; return n; }
        int pressed = Width();
        u.Up(220, 55); for (int i = 0; i < 60; i++) Frame();
        int rest = Width();
        float ratio = pressed / (float)rest;
        True(Math.Abs(ratio - 0.97f) < 0.006f, $"pressed {pressed} px, at rest {rest} px: {ratio:0.000}");
    }

    // ---- two players at once -------------------------------------------------------------------

    static MediaInfo Playing(string app, string id, double since = 0) => new() { AppName = app, SessionId = id, Title = app + " track", Playing = true, PlayingSince = since };

    [Scenario("with two apps playing, the island shows the one you can hear: Spotify at 0% under a video doesn't take over when its song changes")]
    public static void TwoPlayersShowTheHeardOne()
    {
        var spotify = Playing("Spotify", "Spotify.exe"); var chrome = Playing("Chrome", "Chrome");
        var all = new List<MediaInfo> { spotify, chrome };
        Func<MediaInfo, bool> none = _ => false;
        Func<MediaInfo, bool?> heard = i => i == chrome;   // the video is loud, Spotify is silent
        // Windows makes Spotify "current" when its track changes
        Eq("Chrome", MediaService.Pick(chrome, all, null, "Spotify.exe", none, 100, heard).pick.AppName, "the video stays on the island");
        Eq("Chrome", MediaService.Pick(spotify, all, null, "Spotify.exe", none, 100, heard).pick.AppName, "and takes it back from a silent Spotify");
        Eq("Spotify", MediaService.Pick(spotify, all, null, "Chrome", none, 100, i => i == spotify).pick.AppName, "Spotify up, the video muted: Spotify");
        Eq("Chrome", MediaService.Pick(chrome, all, null, "Spotify.exe", none, 100, _ => false).pick.AppName, "both quiet for a moment: no flip");
        chrome.Playing = false;
        Eq("Spotify", MediaService.Pick(chrome, all, null, "Spotify.exe", none, 100, heard).pick.AppName, "pause the video: Spotify, silent or not, is what's playing");
        chrome.Playing = true;
        Eq("Spotify", MediaService.Pick(chrome, all, "Spotify.exe", "Chrome", none, 100, heard).pick.AppName, "picking one by hand still wins");
    }

    [Scenario("the visualizer follows the shown app's own sound, not everything the speakers play")]
    public static void VisualizerFollowsTheShownApp()
    {
        var clock = new FakeClock();
        var spotify = Playing("Spotify", "Spotify.exe"); var chrome = Playing("Chrome", "Chrome");
        var ms = new MediaService(clock, null) { AppPeak = m => m == spotify ? 0f : 0.8f, OutputPeak = () => 0.9f };
        ms.Current = chrome;
        float l = 0; for (int i = 0; i < 30; i++) l = ms.Level;
        True(Math.Abs(l - 0.8f) < 0.01f, $"the video's own level ({l:0.00})");
        ms.Current = spotify;
        for (int i = 0; i < 60; i++) l = ms.Level;
        True(l < 0.01f, $"Spotify at 0% shows silence while the video plays loud ({l:0.00})");
        ms.AppPeak = _ => null;
        for (int i = 0; i < 30; i++) l = ms.Level;
        True(Math.Abs(l - 0.9f) < 0.01f, $"an app whose sessions can't be told apart follows the output, as before ({l:0.00})");
    }

    [Scenario("a media session finds its app's audio sessions by exe, registered name or Store package")]
    public static void MediaAppsMatchTheirAudio()
    {
        True(AudioService.AppMatches("Spotify.exe", "Spotify", @"C:\Users\a\AppData\Roaming\Spotify\Spotify.exe", "Spotify"), "Spotify (desktop)");
        True(AudioService.AppMatches("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify", @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.2_x64__zpdnekdrzrea0\Spotify.exe", "Spotify"), "Spotify (Store)");
        True(AudioService.AppMatches("Chrome", "Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome"), "Chrome");
        True(AudioService.AppMatches("MSEdge", "Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Microsoft Edge"), "Edge");
        True(AudioService.AppMatches("308046B0AF4A39CB", "Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", "Firefox"), "Firefox, by name");
        True(!AudioService.AppMatches("Chrome", "Chrome", @"C:\Users\a\AppData\Roaming\Spotify\Spotify.exe", "Spotify"), "Chrome isn't Spotify");
        True(!AudioService.AppMatches("Spotify.exe", "Spotify", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome"), "nor the other way round");
    }

    // ---- out of the way ------------------------------------------------------------------------

    [Scenario("right-click hides the island at once (no menu), and brings a hidden one back")]
    public static void RightClickHides()
    {
        var (isl, host, clock) = NewIsland();
        isl.RightClick(450, 20);
        Eq(Mode.Hidden, isl.Mode, "hidden");
        Eq(0, host.Menus, "no menu");
        isl.RightClick(450, 2);
        Eq(Mode.Rest, isl.Mode, "right-clicking the hidden island brings it back");
        isl.Expand(); clock.Advance(0.5, isl);
        isl.RightClick(450, 60);
        Eq(Mode.Hidden, isl.Mode, "open, it hides too");
    }

    [Scenario("hovering waits for the pointer to settle, never opens during a drag passing under it, and stays quiet after a click nearby")]
    public static void HoverStaysOutOfTheWay()
    {
        var (isl, host, clock) = NewIsland();
        Eq(450, isl.Cfg.HoverDelayMs, "a 450 ms rest before it peeks");
        isl.MouseMove(450, 20); clock.Advance(0.3, isl);
        Eq(Mode.Rest, isl.Mode, "300 ms on it isn't enough");
        clock.Advance(0.25, isl);
        Eq(Mode.Peek, isl.Mode, "450 ms is");
        isl.MouseLeave(); clock.Advance(2, isl);
        Eq(Mode.Rest, isl.Mode, "back to rest");
        host.ButtonDown = true;
        isl.MouseMove(450, 20); clock.Advance(1, isl);
        Eq(Mode.Rest, isl.Mode, "a drag passing under it (button held) doesn't open it");
        host.ButtonDown = false; isl.MouseLeave(); clock.Advance(0.2, isl);
        isl.WorkingNearby();
        isl.MouseMove(450, 20); clock.Advance(1, isl);
        Eq(Mode.Rest, isl.Mode, "just after a click beside it, hovering leaves it alone");
        isl.MouseLeave(); clock.Advance(1, isl);
        isl.MouseMove(450, 20); clock.Advance(0.6, isl);
        Eq(Mode.Peek, isl.Mode, "a moment later it works again");
    }

    [Scenario("a hidden island comes back for a pointer resting on it, not one passing over the top edge")]
    public static void HiddenWaitsForARestingPointer()
    {
        var (isl, host, clock) = NewIsland();
        isl.RightClick(450, 20);
        isl.RevealSuppressed = false;
        isl.MouseLeave();
        for (int i = 0; i < 6; i++) { isl.MouseMove(300 + i * 40, 2); clock.Advance(0.1, isl); }
        Eq(Mode.Hidden, isl.Mode, "passing along the top, moving, it stays hidden");
        clock.Advance(Island.Island.HoverRevealDwell + 0.05, isl);
        Eq(Mode.Rest, isl.Mode, "resting, it comes back");
        True(RevealIntent.Dwell >= 0.5, "the top strip wants half a second's rest too");
        Eq(200, new Config().RevealZoneWidth, "and the strip is about the island's width, not half the screen");
    }

    [Scenario("a config still on the old hover and reveal defaults moves to the new ones; values someone chose stay")]
    public static void OldDefaultsMoveOn()
    {
        var old = new Config { HoverDelayMs = 200, RevealZoneWidth = 420, Revision = 0 };
        old.Normalize();
        True(old.HoverDelayMs == 450 && old.RevealZoneWidth == 200 && old.Revision == 1, "old defaults upgraded");
        var mine = new Config { HoverDelayMs = 120, RevealZoneWidth = 600, Revision = 0 };
        mine.Normalize();
        True(mine.HoverDelayMs == 120 && mine.RevealZoneWidth == 600, "chosen values kept");
        var later = new Config { HoverDelayMs = 200, Revision = 1 };
        later.Normalize();
        Eq(200, later.HoverDelayMs, "chosen after the upgrade (even 200): kept");
    }

    [Scenario("mouse drags on the island: a real pull hides or searches, a pause before letting go doesn't fling, sideways is a swipe and never a click")]
    public static void MouseDragsAreAccurate()
    {
        var (isl, host, clock) = NewIsland();
        isl.DragTo(0, -15); isl.DragEnd(0, -15, 0);
        Eq(Mode.Rest, isl.Mode, "a 15 px nudge up doesn't hide it");
        isl.DragTo(0, -35); isl.DragEnd(0, -35, 0);
        Eq(Mode.Hidden, isl.Mode, "35 px up does");
        isl.RightClick(450, 2); clock.Advance(0.5, isl);
        isl.DragTo(0, -10); isl.DragEnd(0, -10, -600);
        Eq(Mode.Hidden, isl.Mode, "a short quick flick up does too");
        isl.RightClick(450, 2); clock.Advance(0.5, isl);
        isl.DragTo(0, 10); isl.DragEnd(0, 10, 900);
        Eq(Mode.Rest, isl.Mode, "a 10 px twitch down isn't a pull to search, however quick");

        var d = new MouseDrag();
        d.Down(100, 100, 1); d.Move(102, 103, 1.01);
        True(!d.Dragging, "4 px of a shaky click isn't a drag");
        d.Move(100, 130, 1.03); d.Move(100, 140, 1.05);
        True(d.Dragging && !d.Sideways, "down is a pull");
        var (dx, dy, vy) = d.Up(100, 140, 1.3);
        True(dy == 40 && vy == 0, $"stopped before letting go: no fling ({vy:0})");
        d.Down(100, 100, 2); d.Move(130, 102, 2.02); d.Move(180, 104, 2.04);
        True(d.Dragging && d.Sideways, "sideways is a swipe");
        (dx, dy, vy) = d.Up(180, 104, 2.05);
        True(dx > MouseDrag.SideSwipe, "far enough to change track");
    }

    [Scenario("an app turned to 0% or muted in the Volume mixer is silent to the island, though its meter still reads the music")]
    public static void AppVolumeCountsTowardHeard()
    {
        Eq(0f, AudioService.Audible(0.7f, 0f, false), "at 0% nothing reaches the speakers");
        Eq(0f, AudioService.Audible(0.7f, 1f, true), "muted, nothing either");
        True(Math.Abs(AudioService.Audible(0.8f, 0.5f, false) - 0.4f) < 1e-6, "at half volume, half the level");
        Eq(0.8f, AudioService.Audible(0.8f, 1f, false), "full volume: the meter as it reads");
    }

    [Scenario("an update shows on the island: found, downloading with its progress, then installing; one that can't install shows nothing")]
    public static void UpdatesShowOnTheIsland()
    {
        byte[] setup = System.Text.Encoding.ASCII.GetBytes("pretend installer " + Guid.NewGuid());
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(setup)).ToLowerInvariant();
        var (upd, gh, clock, events) = NewUpdater(setup, sha);
        var found = new List<string>();
        upd.Found += v => found.Add(v.ToString(3));
        double during = -2; string phaseDuring = null;
        upd.StartInstaller = (file, args) => { during = upd.Progress; phaseDuring = upd.Phase; return true; };
        upd.CheckAsync(true).Wait(); clock.Advance(0.1);
        Eq("1.1.0", string.Join(",", found), "the island hears an update was found");
        Eq("downloading", phaseDuring, "while it downloads, the island shows it");
        True(during > 0.99, $"the download's progress reached the end ({during:0.00})");
        Eq("installing", upd.Phase, "then that it's installing");
        Eq("1.1.0", upd.Incoming.ToString(3), "which version");
        True(Updater.ShowInstallingFor >= 1, "long enough to read before the island closes for the setup");

        (upd, gh, clock, events) = NewUpdater(setup, sha);
        upd.StartInstaller = (file, args) => false;
        upd.CheckAsync(true).Wait(); clock.Advance(0.1);
        True(upd.Phase == null, "a setup that wouldn't start leaves nothing on the island");

        var (isl, host, _) = NewIsland();
        (upd, gh, clock, events) = NewUpdater(setup, sha);
        upd.StartInstaller = (file, args) => true;
        upd.CheckAsync(true).Wait();
        isl.Svc.Updates = upd;
        Eq("update", CompactPage.Pick(isl), "the island's live activity is the update");
    }

    [Scenario("the visualizer lies still once the shown app has been silent a moment (Spotify at 0% in its own app), and moves again with sound")]
    public static void VisualizerStillWhenSilent()
    {
        var clock = new FakeClock();
        var spotify = Playing("Spotify", "Spotify.exe");
        float peak = 0.6f;
        var ms = new MediaService(clock, null) { AppPeak = _ => peak, OutputPeak = () => 0 };
        ms.Current = spotify;
        _ = ms.Level;
        True(ms.Sounding, "playing with sound: it moves");
        peak = 0;
        clock.Now += 0.5; _ = ms.Level;
        True(ms.Sounding, "half a second of quiet (a gap between songs) doesn't stop it");
        clock.Now += MediaService.QuietFor; _ = ms.Level;
        True(!ms.Sounding, "silent for longer: it lies still");
        peak = 0.3f; _ = ms.Level;
        True(ms.Sounding, "sound again: it moves at once");
        var flat = Paint.BarHeight(new Box(0, 0, 22, 16), 2, 5, 0, false, 1.0);
        True(Paint.BarHeight(new Box(0, 0, 22, 16), 2, 5, 0, false, 1.7) == flat, "and its bars don't move while still");
    }

    [Scenario("a notice replacing another crossfades from the old one: the old page keeps its own text while it fades")]
    public static void NoticesCrossfade()
    {
        var (isl, host, clock) = NewIsland();
        isl.Alert(new Alert { Key = "one", Kind = "info", Title = "First", Seconds = 5 });
        clock.Advance(0.3, isl);
        var first = isl.ShownPage;
        isl.Alert(new Alert { Key = "two", Kind = "info", Title = "Second", Seconds = 5 });
        for (int i = 0; i < 120 && isl.CurrentAlert?.Key != "two"; i++) clock.Advance(0.05, isl);   // the first is hurried along, then the second shows
        Eq("two", isl.CurrentAlert?.Key, "the second notice shows");
        var second = isl.ShownPage;
        True(first != null && second != null && !ReferenceEquals(first, second), "the new notice is drawn by the other alert page");
        True(first.Key.Contains(":one:") && second.Key.Contains(":two:"), $"each keeps its own notice ({first.Key} / {second.Key})");
    }

    [Scenario("a title too long for the island rests, slides slowly to its end, rests, and slides back, easing in and out")]
    public static void LongTitlesSlide()
    {
        float over = 140;
        Eq(0f, Canvas.MarqueeOffset(over, 0), "starts at the start");
        Eq(0f, Canvas.MarqueeOffset(over, Canvas.MarqueeHoldStart - 0.05), "and rests there a moment to be read");
        double glide = over / Canvas.MarqueeSpeed;
        float early = Canvas.MarqueeOffset(over, Canvas.MarqueeHoldStart + glide * 0.1), mid = Canvas.MarqueeOffset(over, Canvas.MarqueeHoldStart + glide * 0.5);
        True(early > 0 && early < over * 0.1f, $"it eases off the mark (slow in): {early:0.0} of {over * 0.1f:0.0} after a tenth of the way");
        True(Math.Abs(mid - over / 2) < 1, $"halfway through the glide, halfway along ({mid:0.0})");
        Eq(over, Canvas.MarqueeOffset(over, Canvas.MarqueeHoldStart + glide + 0.1), "reaches the very end");
        Eq(over, Canvas.MarqueeOffset(over, Canvas.MarqueeHoldStart + glide + Canvas.MarqueeHoldEnd - 0.05), "and rests there too");
        float back = Canvas.MarqueeOffset(over, Canvas.MarqueeHoldStart + glide + Canvas.MarqueeHoldEnd + glide * 0.5);
        True(Math.Abs(back - over / 2) < 1, $"then slides back ({back:0.0})");
        double cycle = Canvas.MarqueeHoldStart + glide + Canvas.MarqueeHoldEnd + glide;
        Eq(0f, Canvas.MarqueeOffset(over, cycle + 0.5), "and starts over");
        Eq(0f, Canvas.MarqueeOffset(0, 5), "a title that fits doesn't move");
        float prev = 0;
        for (double t = Canvas.MarqueeHoldStart; t <= Canvas.MarqueeHoldStart + glide; t += 1 / 60.0)
        {
            float o = Canvas.MarqueeOffset(over, t);
            True(o >= prev - 1e-3 && o - prev < Canvas.MarqueeSpeed * 2.2f / 60, $"smooth, never jumping (at {t:0.00} s: {prev:0.0} -> {o:0.0})");
            prev = o;
        }
    }

    [Scenario("an app's meter reading full while nothing comes out (Spotify at 0% in its own app) counts as silent")]
    public static void SilentAppsAreSilent()
    {
        Eq(0f, MediaService.Audible(1.0f, 0f, null), "Spotify's meter at full, the speakers silent: silent");
        Eq(0f, MediaService.Audible(1.0f, 0.8f, 0), "at 0% in Spotify, silent even while a video plays loud");
        Eq(0.5f, MediaService.Audible(0.5f, 0.8f, null), "an app playing within the mix: its own level");
        True(Math.Abs(MediaService.Audible(0.8f, 0.9f, 50).Value - 0.4f) < 1e-6, "Spotify at 50%: half");
        True(MediaService.Audible(null, 0.5f, null) == null, "an app that can't be told apart stays unknown");
    }

    [Scenario("the open island re-sizes when the media it shows changes (Spotify's volume row needs room)")]
    public static void OpenIslandResizesForNewMedia()
    {
        var cfg = new Config { SpotifyVolume = true };
        var (isl, host, clock) = NewIsland(cfg);
        var media = new FakeMedia(clock) { Current = new MediaInfo { AppName = "Chrome", SessionId = "Chrome", Title = "A video", Playing = true } };
        isl.Svc.Media = media; isl.Svc.Spotify = new Services.SpotifyService(clock);
        isl.Expand(); clock.Advance(0.6, isl);
        double before = isl.TargetHeight;
        media.Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "House Of Cards", Playing = true };
        isl.ActivityChanged(); clock.Advance(0.05, isl);
        True(isl.TargetHeight >= before + 39, $"room for Spotify's volume row ({before:0} -> {isl.TargetHeight:0})");
    }

    [Scenario("a sliding title starts again from rest when its box changes width, instead of jumping while the island grows")]
    public static void MarqueeRestartsOnResize()
    {
        using var o = new Offscreen(400, 60);
        string title = "See U in Hell (from the Netflix Series \"Something Long Enough to Slide\")";
        byte[] Row(float width, double now)
        {
            o.Draw(c => c.Marquee(title, new Box(10, 10, width, 20), 13, Col.White, Vortice.DirectWrite.FontWeight.Medium, now));
            return o.AlphaRow(20);
        }
        var rest = Row(200, 100);
        var sliding = Row(200, 100 + Canvas.MarqueeHoldStart + 1.5);
        True(!rest.SequenceEqual(sliding), "at a steady width it slides");
        var c1 = Row(214, 100 + Canvas.MarqueeHoldStart + 1.6);
        var c2 = Row(214, 100 + Canvas.MarqueeHoldStart + 1.7);
        True(c1.SequenceEqual(c2), "at a new width it rests again (no jumping while the island grows)");
    }
}
