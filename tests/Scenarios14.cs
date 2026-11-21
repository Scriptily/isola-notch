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

    [Scenario("tooltips: the first waits 400 ms, the next ones along a row appear at once, and after a pause they wait again")]
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
        u.Up(20, 20);

        // leaving: the tooltip fades back out over a few frames instead of vanishing at once
        var v = new Ux();
        v.BeginFrame(null); v.Add("q", new Box(0, 0, 40, 40), () => { }).Tooltip = "Quit"; v.EndFrame();
        v.Move(20, 20); v.TipFrame(0);
        double now = 0.5; float shown = 0;
        for (int f = 0; f < 30; f++) { now += 1 / 60.0; v.Step(1 / 60.0); shown = v.TipFrame(now)?.appear ?? 0; }
        True(shown > 0.98f, $"shown ({shown:0.00})");
        v.Move(500, 500);
        now += 1 / 60.0; v.Step(1 / 60.0);
        var leaving = v.TipFrame(now);
        True(leaving is { text: "Quit" } && leaving.Value.appear > 0.5f, $"the frame after leaving it's still there, fading ({leaving?.appear:0.00})");
        int frames = 1;
        while (v.TipFrame(now) != null && frames < 60) { now += 1 / 60.0; v.Step(1 / 60.0); frames++; }
        True(frames > 3 && frames < 30, $"and gone after a short fade ({frames} frames)");
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
        Eq("Chrome", MediaService.Pick(chrome, all, null, "Spotify.exe", none, 100, heard).pick.AppName, "pause the video: it stays (Spotify is silent, so it doesn't take over)");
        Eq("Spotify", MediaService.Pick(chrome, all, null, "Spotify.exe", none, 100, i => i == spotify ? true : null).pick.AppName, "but a Spotify you can hear does");
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

    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern bool PlaySound(nint sound, nint mod, uint flags);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();

    [Scenario("an app's own sound is measured as it leaves for the speakers (a near-silent tone reads at its level, then silence)")]
    public static void AppSoundIsCaptured()
    {
        // a 440 Hz tone at amplitude 0.002 (about -54 dB: practically inaudible), played by this process
        int rate = 48000, n = rate;
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + n * 2); w.Write("WAVEfmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 0.002 * 32767));
        var bytes = ms.ToArray();
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        var cw = new System.Runtime.InteropServices.Marshalling.StrategyBasedComWrappers();
        var cap = Services.ProcessCapture.TryStart((uint)Environment.ProcessId, cw);
        if (cap == null) { Console.WriteLine("        (no audio device here: skipped)"); pin.Free(); return; }
        try
        {
            // a build machine has nothing to play the tone on (capture starts there, but nothing ever sounds)
            if (waveOutGetNumDevs() == 0 || !PlaySound(pin.AddrOfPinnedObject(), 0, 0x0004 /* memory */ | 0x0001 /* async */ | 0x0008 /* loop */))
            { Console.WriteLine("        (no sound output here: skipped)"); return; }
            float peak = 0;
            for (int i = 0; i < 40; i++) { Thread.Sleep(25); peak = Math.Max(peak, cap.Peak); }
            True(Math.Abs(peak - 0.002f) < 0.0006f, $"the tone's own level, not the meters' guess ({peak:0.0000})");
            PlaySound(0, 0, 0);
            Thread.Sleep(400);
            float after = 0;
            for (int i = 0; i < 8; i++) { Thread.Sleep(25); after = Math.Max(after, cap.Peak); }
            True(after < 0.0002f, $"and silence once it stops ({after:0.0000})");
        }
        finally { PlaySound(0, 0, 0); cap.Dispose(); pin.Free(); }
    }

    [Scenario("each media session finds the processes playing its sound, for any app: Store packages, window app ids, helpers a browser starts, program names")]
    public static void MediaSessionsFindTheirProcesses()
    {
        var procs = new Dictionary<uint, ProcInfo>();
        ProcInfo P(uint pid, uint parent, string exe, string family = "", string name = "", params string[] windowIds)
        {
            var pi = new ProcInfo { Pid = pid, Parent = parent, Exe = exe, Family = family, Name = name };
            foreach (var w in windowIds) pi.WindowIds.Add(w);
            return procs[pid] = pi;
        }
        P(4, 0, "explorer.exe", windowIds: "Microsoft.Windows.Explorer");
        P(10, 4, "AppleMusic.exe", "AppleInc.AppleMusicWin_nzyj5cx40ttqa", "AppleMusic");   // its description isn't the name the controls show
        P(20, 4, "Microsoft.Media.Player.exe", "Microsoft.ZuneMusic_8wekyb3d8bbwe", "Microsoft.Media.Player");
        P(30, 4, "chrome.exe", windowIds: "Chrome"); P(31, 30, "chrome.exe");                       // the browser and its audio helper
        P(40, 4, "firefox.exe", name: "Firefox", windowIds: "308046B0AF4A39CB"); P(41, 40, "firefox.exe");
        P(50, 4, "Plex.exe", name: "Plex", windowIds: "tv.plex.desktop");
        P(60, 4, "vlc.exe", name: "VLC media player");
        P(70, 4, "Spotify.exe", name: "Spotify");
        P(80, 4, "foobar2000.exe", name: "foobar2000");
        ProcInfo Look(uint pid) => procs.GetValueOrDefault(pid);
        bool Owns(string aumid, string app, uint pid) => MediaApps.Owns(aumid, app, pid, Look);

        True(Owns("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "Apple Music", 10), "Apple Music, by its Store package");
        True(Owns("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Media Player", 20), "Media Player, whose id shares no word with its program");
        True(Owns("Chrome", "Chrome", 31), "Chrome's sound comes from a helper it started, found through its window's id");
        True(Owns("308046B0AF4A39CB", "Firefox", 41), "Firefox's id is a code: found on its window");
        True(Owns("tv.plex.desktop", "Plex", 50), "Plex, by its window's id");
        True(Owns("vlc.exe", "VLC", 60) || Owns("VLC", "VLC media player", 60), "VLC, by its program");
        True(Owns("Spotify.exe", "Spotify", 70), "Spotify, by its program");
        True(Owns("foobar2000.exe", "foobar2000", 80), "foobar2000, by its program");
        True(!Owns("Chrome", "Chrome", 70) && !Owns("Spotify.exe", "Spotify", 31), "no app takes another's sound");
        True(!Owns("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "Apple Music", 20), "nor one Store app another's");
    }

    [Scenario("a player picked by hand stays until another app starts playing; then the island shows what can be heard again")]
    public static void HandPickLastsUntilSomethingStarts()
    {
        var spotify = Playing("Spotify", "Spotify.exe", since: 10); var chrome = Playing("Chrome", "Chrome", since: 5);
        chrome.Playing = false;
        var all = new List<MediaInfo> { spotify, chrome };
        True(!MediaService.PinReleased("Spotify.exe", 20, all), "picked at 20 s, nothing new: it stays");
        chrome.Playing = true; chrome.PlayingSince = 25;
        True(MediaService.PinReleased("Spotify.exe", 20, all), "the video resumes at 25 s: the pick lets go");
        True(!MediaService.PinReleased("Spotify.exe", 30, all), "picked again after that: it holds");
    }

    [Scenario("three players at once: the one you can hear wins; of two you can hear, the one started last; pausing keeps it over a silent one")]
    public static void ThreePlayersPickSmartly()
    {
        var spotify = Playing("Spotify", "Spotify.exe", since: 10);                      // playing at 0% in its own app
        var mp = Playing("Media Player", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", since: 50);
        var yt = Playing("Chrome", "Chrome", since: 80);
        var all = new List<MediaInfo> { spotify, mp, yt };
        Func<MediaInfo, bool> none = _ => false;
        Func<MediaInfo, bool?> heard = i => i != spotify && i.Playing;
        string Show(MediaInfo current, string windows = "Spotify.exe") => MediaService.Pick(current, all, null, windows, none, 100, heard).pick.AppName;

        Eq("Chrome", Show(mp), "both heard: the video, started last, takes over");
        Eq("Chrome", Show(yt), "and keeps the island while both play");
        Eq("Chrome", Show(yt, "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"), "Windows calling Media Player current (a track change there) doesn't move it");
        yt.Playing = false; yt.PlayingSince = double.NaN;
        Eq("Media Player", Show(yt), "pause the video: Media Player, still heard, shows");
        mp.Playing = false; mp.PlayingSince = double.NaN;
        Eq("Media Player", Show(mp), "pause that too: it stays, paused; the silent Spotify doesn't take over");
        yt.Playing = true; yt.PlayingSince = 95;
        Eq("Chrome", Show(mp), "resume the video: it takes over again");
        yt.Playing = false; yt.PlayingSince = double.NaN;
        Eq("Chrome", Show(yt), "and pausing it keeps it");
    }

    [Scenario("play/pause holds what was asked against the app's late or repeated reports, then believes the app")]
    public static void PlayPauseHoldsItsIntent()
    {
        double until = 10 + MediaService.PlayHold;
        Eq(false, MediaService.Hold<bool>(true, false, 10.2, ref until) ?? true, "paused by the button; the app still says playing a moment later: paused");
        until = 10 + MediaService.PlayHold;
        Eq(false, MediaService.Hold<bool>(false, false, 10.3, ref until) ?? true, "the app agrees");
        Eq(true, MediaService.Hold<bool>(true, false, 10.4, ref until) ?? false, "agreed once, the hold is over: a later \"playing\" is real");
        until = 10 + MediaService.PlayHold;
        Eq(true, MediaService.Hold<bool>(true, false, 10 + MediaService.PlayHold + 0.1, ref until) ?? false, "after the hold, the app is believed (it may have refused)");
    }

    [Scenario("a report from before a seek landed doesn't move the bar back; one at the new spot, or a late one after the hold, is believed")]
    public static void SeekHoldsAgainstStaleReports()
    {
        True(MediaService.StaleAfterSeek(90, 34, 10.2, 10 + MediaService.SeekHold), "sent to 1:30, the app still says 0:34: ignored (no flicker)");
        True(!MediaService.StaleAfterSeek(90, 90.4, 10.3, 10 + MediaService.SeekHold), "it says 1:30: believed");
        True(!MediaService.StaleAfterSeek(90, 34, 10 + MediaService.SeekHold + 0.1, 10 + MediaService.SeekHold), "still 0:34 after the hold: the app refused, believed");
    }

    [Scenario("the glow takes the song's colors only while the island shows it; resting on the eyes it's the theme's")]
    public static void GlowFollowsWhatsShown()
    {
        var (isl, host, clock) = NewIsland(new Config { GlowColors = "auto" });
        var theme = Island.Island.PaletteFor("aurora")[0];
        var red = new Vortice.Mathematics.Color4(0.9f, 0.1f, 0.1f, 1);
        var media = new FakeMedia(clock) { Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "A song", Playing = true, Palette = new[] { red, red, red } } };
        isl.Svc.Media = media; isl.ActivityChanged(); isl.UpdateGlowColors(); clock.Advance(0.5, isl);
        Eq(red, isl.GlowTarget0, "playing in the pill: the song's colors");
        media.Current.Playing = false; media.LastPlayingAt = clock.Now; isl.ActivityChanged(); isl.UpdateGlowColors();
        clock.Advance(70, isl);   // paused a while: the island rests on its eyes
        isl.ActivityChanged(); clock.Advance(0.5, isl);
        True(!isl.ShowsMedia(), "resting on the eyes");
        Eq(theme, isl.GlowTarget0, "the eyes: back to the theme's colors");
        isl.Expand(); clock.Advance(0.5, isl);
        Eq(red, isl.GlowTarget0, "opened: the song's colors again");
        isl.CloseToRest("test"); clock.Advance(0.5, isl);
        Eq(theme, isl.GlowTarget0, "closed onto the eyes: the theme's");
    }

    [Scenario("a copied picture (Windows' clipboard bitmap) becomes a Shelf image: 24- and 32-bit, bottom-up, rows padded")]
    public static void ClipboardBitmapsBecomeImages()
    {
        // 3x2 at 24 bits, bottom-up: rows are padded to 12 bytes; the bottom row comes first
        var dib = new List<byte>();
        void I32(int v) => dib.AddRange(BitConverter.GetBytes(v)); void I16(short v) => dib.AddRange(BitConverter.GetBytes(v));
        I32(40); I32(3); I32(2); I16(1); I16(24); I32(0); I32(0); I32(0); I32(0); I32(0); I32(0);
        dib.AddRange(new byte[] { 0, 0, 255, 0, 255, 0, 255, 0, 0, 0, 0, 0 });       // bottom row: red, green, blue, pad
        dib.AddRange(new byte[] { 255, 255, 255, 0, 0, 0, 128, 128, 128, 0, 0, 0 });  // top row: white, black, gray, pad
        var bgra = Services.Png.DibToBgra(dib.ToArray(), out int w, out int h);
        True(bgra != null && w == 3 && h == 2, "read");
        True(bgra[0] == 255 && bgra[1] == 255 && bgra[2] == 255 && bgra[3] == 255, "top-left is the white of the top row");
        True(bgra[12 + 2] == 255 && bgra[12 + 1] == 0 && bgra[12] == 0, "bottom-left is red");
        True(Services.Png.Encode(bgra, w, h, false) is { Length: > 8 } png && png[1] == (byte)'P', "and encodes to PNG");
        True(Services.Png.DibToBgra(new byte[10], out _, out _) == null, "junk is refused");
    }

    [Scenario("Tab never types into a text box: in search it moves to the actions")]
    public static void TabDoesntType()
    {
        var tb = new TextBox();
        tb.Char('a', 1); tb.Char('\t', 1); tb.Char('b', 1);
        Eq("ab", tb.Text, "no tab typed");
    }

    [Scenario("a list shows a scroll bar only when it scrolls; dim while the pointer is elsewhere, awake when it's inside")]
    public static void ScrollBarsShowAndWake()
    {
        using var o = new Offscreen(200, 200);
        var u = new Ux();
        var view = new Box(0, 0, 200, 200);
        int Brightest(float content)
        {
            int best = 0;
            for (int f = 0; f < 40; f++)
            {
                o.Draw(c => { u.BeginFrame(c); Paint.ScrollBar(c, u, "t", view, 0, 0, content, 1); u.EndFrame(); });
                u.Step(1 / 30.0);
            }
            foreach (var a in o.AlphaColumn(194)) best = Math.Max(best, a);
            return best;
        }
        Eq(0, Brightest(150), "everything fits: no bar");
        u.Move(-50, -50); u.Leave();
        int dim = Brightest(600);
        True(dim > 0, "more than fits: a bar");
        u.Move(100, 100);
        int awake = Brightest(600);
        True(awake > dim * 1.6, $"the pointer inside wakes it ({dim} -> {awake})");
        True(Paint.Scrolls(600, 200) && !Paint.Scrolls(200, 200), "and only scrolling lists give up a lane for it");
    }

    [Scenario("a banner on the open island waits while hovered and goes when clicked; the next one follows")]
    public static void BannersWaitAndDismiss()
    {
        var (isl, host, clock) = NewIsland();
        isl.Expand(); clock.Advance(0.5, isl);
        isl.Alert(new Alert { Key = "a", Title = "First", Seconds = 3 });
        isl.Alert(new Alert { Key = "b", Title = "Second", Seconds = 3 });
        Eq("a", isl.InlineAlert?.Key, "one banner at a time");
        clock.Advance(3.2, isl);
        Eq("b", isl.InlineAlert?.Key, "the first times out, the next follows");
        isl.EndInline("dismissed"); clock.Advance(0.1, isl);
        True(isl.InlineAlert == null, "a click dismisses it");
        Eq(Mode.Expanded, isl.Mode, "without leaving the tab");
    }

    [Scenario("a click on a notice or a call dismisses it, and everything shown is kept for the Recent tab")]
    public static void ClickDismissesAndHistoryKeeps()
    {
        var (isl, host, clock) = NewIsland();
        isl.Alert(new Alert { Key = "n1", Kind = "notification", Title = "Hi", Subtitle = "Messages", Seconds = 5 });
        clock.Advance(0.3, isl);
        Eq(Mode.Alert, isl.Mode, "shown");
        isl.MouseDown(450, 30); isl.MouseUp(450, 30); clock.Advance(0.3, isl);
        True(isl.Mode != Mode.Alert && isl.Mode != Mode.Expanded, $"a click dismissed it, without opening the island ({isl.Mode})");
        isl.Alert(new Alert { Key = "call", Kind = "call", Title = "Mom", Priority = 3, Sticky = true, MaxSeconds = 30 });
        clock.Advance(0.3, isl);
        isl.MouseDown(450, 30); isl.MouseUp(450, 30); clock.Advance(0.3, isl);
        True(isl.Mode != Mode.Alert, "a call dismissed the same way");
        Eq(2, isl.History.Count, "both are in the session history");
        Eq("Mom", isl.History[^1].alert.Title, "newest last (shown first in the tab)");
    }

    [Scenario("the pill names the main artist only: featured artists are left out, band names kept whole")]
    public static void MainArtistOnly()
    {
        Eq("mgk", MediaInfo.MainArtist("mgk, Avril Lavigne"), "a list");
        Eq("Drake", MediaInfo.MainArtist("Drake feat. Rihanna"), "feat.");
        Eq("Travis Scott", MediaInfo.MainArtist("Travis Scott ft. Drake"), "ft.");
        Eq("Kygo", MediaInfo.MainArtist("Kygo x Selena Gomez"), "x");
        Eq("Simon & Garfunkel", MediaInfo.MainArtist("Simon & Garfunkel"), "a band with &");
        Eq("Lil Nas X", MediaInfo.MainArtist("Lil Nas X"), "a name ending in X");
        Eq("", MediaInfo.MainArtist(null), "none");
    }

    [Scenario("a sliding title glides without snapping, and rests on whole pixels at either end")]
    public static void MarqueeGlidesSmoothly()
    {
        using var o = new Offscreen(300, 40);
        string title = "A title long enough that it cannot possibly fit in this pill";
        bool gliding = false, resting = false;
        o.Draw(c => { c.Marquee(title, new Box(10, 10, 150, 20), 13, Col.White, Vortice.DirectWrite.FontWeight.Medium, 50); resting = c.MarqueeMoved && !c.MarqueeGliding; });
        o.Draw(c => { c.Marquee(title, new Box(10, 10, 150, 20), 13, Col.White, Vortice.DirectWrite.FontWeight.Medium, 50 + Canvas.MarqueeHoldStart + 1); gliding = c.MarqueeGliding; });
        True(resting, "resting at the start: drawn, not gliding (a few frames a second)");
        True(gliding, "mid-slide: gliding (every frame)");
    }

    [Scenario("tabs that don't fit scroll sideways and keep the chosen one in view; the open island fits a narrow screen")]
    public static void TabStripFitsAnyScreen()
    {
        Eq(0f, DashboardPage.StripOffset(400, 500, 300, 100), "they fit: no scrolling");
        Eq(0f, DashboardPage.StripOffset(700, 400, 20, 40), "the first tab chosen: at the start");
        Eq(300f, DashboardPage.StripOffset(700, 400, 640, 60), "the last chosen: at the end, not past it");
        float mid = DashboardPage.StripOffset(700, 400, 330, 40);
        True(mid > 0 && mid < 300 && Math.Abs(330 + 20 - mid - 200) < 0.01f, $"one in the middle sits in the middle of the lane ({mid})");
        var (isl, host, clock) = NewIsland();
        host.Width = 520;   // a small screen at a large scale
        isl.Expand(); clock.Advance(0.6, isl);
        True(isl.Shape.W <= 520 - 24 + 0.5f, $"the open island fits the screen ({isl.Shape.W:0})");
    }

    [Scenario("what's running shows when the island is hovered or opened too, not only in the pill")]
    public static void ActivitiesShowEverywhere()
    {
        var (isl, host, clock) = NewIsland();
        var media = new FakeMedia(clock) { Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "A song", Playing = true } };
        isl.Svc.Media = media;
        isl.MouseMove(450, 20); clock.Advance(1, isl);
        Eq(Mode.Peek, isl.Mode, "hovered");
        double plain = isl.TargetHeight;
        isl.Svc.Timer = new Services.CountdownTimer(clock);
        isl.Svc.Timer.Start(300);
        isl.ActivityChanged(); clock.Advance(0.1, isl);
        Eq("timer", string.Join(",", CompactPage.Running(isl)), "the timer is running");
        True(isl.TargetHeight >= plain + CompactPage.ActivityRowH - 0.5, $"the hover card grows a row for it ({plain:0} -> {isl.TargetHeight:0})");
        isl.Expand(); clock.Advance(0.6, isl);
        double withTimer = isl.TargetHeight;
        isl.Svc.Timer.Cancel();
        isl.ActivityChanged(); clock.Advance(0.1, isl);
        True(isl.TargetHeight <= withTimer - CompactPage.ActivityRowH + 0.5, "and the open island shows it too, until it's done");
    }

    [Scenario("weather: Home Assistant's forecast keeps each day's low, rain and the rest, and hours are night by the sun, not the clock")]
    public static void WeatherFromHomeAssistant()
    {
        var now = new DateTime(2026, 11, 19, 15, 10, 0);
        string U(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var daily = System.Text.Json.Nodes.JsonNode.Parse($$"""
            [{"datetime":"{{U(now.Date.AddHours(12))}}","condition":"partlycloudy","temperature":29,"templow":18,"precipitation_probability":20,"precipitation":0.4,"humidity":51,"wind_speed":12,"uv_index":6},
             {"datetime":"{{U(now.Date.AddDays(1).AddHours(12))}}","condition":"rainy","temperature":24,"templow":16,"precipitation_probability":80,"precipitation":6.2}]
            """)!.AsArray();
        var sunsetAt5 = Services.WeatherService.NightFrom(now.Date.AddHours(6.5), now.Date.AddHours(17));
        var days = Services.WeatherService.FromHaForecast(daily, true, sunsetAt5, false, now);
        Eq(2, days.Count, "two days");
        Eq("Today", days[0].Label, "today first");
        Eq(18.0, days[0].Low, "the day's low");
        Eq(80.0, days[1].Precip, "tomorrow's rain chance");
        Eq(6.2, days[1].PrecipAmount, "and amount");
        Eq("rain", days[1].Icon, "rain's icon");
        var hourly = System.Text.Json.Nodes.JsonNode.Parse($$"""
            [{"datetime":"{{U(now.Date.AddHours(16))}}","condition":"sunny","temperature":27},
             {"datetime":"{{U(now.Date.AddHours(18))}}","condition":"sunny","temperature":22},
             {"datetime":"{{U(now.Date.AddHours(19))}}","condition":"partlycloudy","temperature":21,"is_daytime":true}]
            """)!.AsArray();
        var hours = Services.WeatherService.FromHaForecast(hourly, false, sunsetAt5, false, now);
        Eq("sun", hours[0].Icon, "4 pm, before sunset: the sun");
        Eq("moon", hours[1].Icon, "6 pm, after a 5 pm sunset: the moon (the old rule said day until 7)");
        Eq("partly-day", hours[2].Icon, "the source's own daytime flag wins");
        True(double.IsNaN(hours[0].Low), "hours have no low");
    }

    [Scenario("weather: Open-Meteo gives 12 hours (night by its own flag) and 7 days with highs, lows and rain")]
    public static void WeatherFromOpenMeteo()
    {
        var now = new DateTime(2026, 11, 19, 15, 10, 0);
        string H(int h) => now.Date.AddHours(h).ToString("yyyy-MM-ddTHH:mm");
        var times = string.Join(",", Enumerable.Range(0, 30).Select(h => $"\"{H(h)}\""));
        var isDay = string.Join(",", Enumerable.Range(0, 30).Select(h => h % 24 is >= 7 and < 17 ? 1 : 0));
        var temps = string.Join(",", Enumerable.Range(0, 30).Select(h => 20 + h % 5));
        var codes = string.Join(",", Enumerable.Range(0, 30).Select(h => h % 24 == 18 ? 61 : 0));
        var rain = string.Join(",", Enumerable.Range(0, 30).Select(h => h % 24 == 18 ? 70 : 0));
        var dayTimes = string.Join(",", Enumerable.Range(0, 7).Select(d => $"\"{now.Date.AddDays(d):yyyy-MM-dd}\""));
        string Nums(params double[] v) => string.Join(",", v);
        var j = System.Text.Json.Nodes.JsonNode.Parse($$$"""
            {"current":{"temperature_2m":22,"relative_humidity_2m":51,"weather_code":2,"wind_speed_10m":9,"is_day":1,"apparent_temperature":23},
             "hourly":{"time":[{{{times}}}],"temperature_2m":[{{{temps}}}],"weather_code":[{{{codes}}}],"is_day":[{{{isDay}}}],"precipitation_probability":[{{{rain}}}]},
             "daily":{"time":[{{{dayTimes}}}],"weather_code":[{{{Nums(2,61,0,0,3,3,0)}}}],"temperature_2m_max":[{{{Nums(29,24,25,26,27,28,29)}}}],"temperature_2m_min":[{{{Nums(18,16,15,15,16,17,18)}}}],
                      "precipitation_probability_max":[{{{Nums(20,80,0,0,10,10,0)}}}],"sunrise":["{{{now.Date.AddHours(6.5):yyyy-MM-ddTHH:mm}}}"],"sunset":["{{{now.Date.AddHours(17):yyyy-MM-ddTHH:mm}}}"]}}
            """);
        var w = Services.WeatherService.ParseOpenMeteo(j, "Somewhere", false, false, now);
        Eq(12, w.Hourly.Count, "the next 12 hours");
        Eq("3pm", w.Hourly[0].Label, "starting with the current hour");
        Eq("moon", w.Hourly.First(f => f.At.Hour == 20).Icon, "8 pm by its own flag: the moon");
        Eq(70.0, w.Hourly.First(f => f.At.Hour == 18).Precip, "6 pm's rain chance");
        Eq(7, w.Daily.Count, "a week");
        Eq(16.0, w.Daily[1].Low, "tomorrow's low");
        Eq(29.0, w.High, "today's high on top");
        Eq(23.0, w.FeelsLike, "feels like");
        True(w.Sunset?.Hour == 17, "sunset kept for the details");
    }

    [Scenario("a new song at rest pops up with every artist, then tucks back into the one-line pill; opened, it doesn't")]
    public static void NewSongPopsUpAtRest()
    {
        var (isl, host, clock) = NewIsland();
        var media = new FakeMedia(clock) { Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "The Break Up", Artist = "mgk, Avril Lavigne", Playing = true } };
        isl.Svc.Media = media; isl.ActivityChanged(); clock.Advance(0.5, isl);
        Eq(Mode.Rest, isl.Mode, "resting on the song");
        isl.TrackChanged(null, media.Current);
        clock.Advance(Island.Island.TrackSettle + 0.2, isl);
        Eq(Mode.Alert, isl.Mode, "it pops up");
        Eq("mgk, Avril Lavigne", isl.CurrentAlert.Subtitle, "with every artist, featured ones included");
        clock.Advance(5, isl);
        Eq(Mode.Rest, isl.Mode, "then tucks back into the pill");
        Eq("mgk", MediaInfo.MainArtist(media.Current.Artist), "where only the main artist shows");
        isl.Expand(); clock.Advance(0.5, isl);
        media.Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "Next", Artist = "Someone", Playing = true };
        isl.TrackChanged(null, media.Current);
        clock.Advance(Island.Island.TrackSettle + Island.Island.TrackGap, isl);
        Eq(Mode.Expanded, isl.Mode, "opened: no popup, the song is already on screen");
    }

    [Scenario("a popup sliding away into the hidden island keeps the song's colors on its glow until it's gone")]
    public static void HidingKeepsGlowColors()
    {
        var (isl, host, clock) = NewIsland(new Config { GlowColors = "auto" });
        var red = new Vortice.Mathematics.Color4(0.9f, 0.1f, 0.1f, 1);
        var media = new FakeMedia(clock) { Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "A song", Artist = "Someone", Playing = true, Palette = new[] { red, red, red } } };
        isl.Svc.Media = media; media.LastPlayingAt = clock.Now; isl.ActivityChanged(); isl.UpdateGlowColors(); clock.Advance(0.5, isl);
        isl.RightClick(450, 20); clock.Advance(0.5, isl);
        Eq(Mode.Hidden, isl.Mode, "hidden");
        isl.TrackChanged(null, media.Current);
        clock.Advance(Island.Island.TrackSettle + 0.3, isl);
        Eq(Mode.Alert, isl.Mode, "the new song pops up");
        clock.Advance(4.5, isl);
        Eq(Mode.Hidden, isl.Mode, "and slides away");
        Eq(red, isl.GlowTarget0, "still the song's colors as it goes");
    }

    [Scenario("a scroll bar can be dragged and its track pressed; the press is the bar's, never a drag on the island")]
    public static void ScrollBarsDrag()
    {
        using var o = new Offscreen(200, 200);
        var u = new Ux();
        var view = new Box(0, 0, 200, 200);
        float target = 0;
        void Frame() { o.Draw(c => { u.BeginFrame(c); Paint.ScrollBar(c, u, "t", view, target, target, 600, 1, v => target = v); u.EndFrame(); }); }
        Frame(); Frame();
        True(u.HitTest(192, 100) != null, "the bar's lane takes a press (the island doesn't see it as its own)");
        u.Move(192, 20); u.Down(192, 20);
        u.Move(192, 196); Frame();
        True(Math.Abs(target - 400) < 1, $"dragged to the bottom: the list at its end ({target:0} of 400)");
        u.Up(192, 196); Frame();
        u.Move(192, 4); u.Down(192, 4); u.Up(192, 4);
        True(target < 1, $"a press at the top of the track: back to the start ({target:0})");
        // the thumb is 64 tall at the top (4..68): grabbed low on it, nothing moves until the pointer does
        Frame();
        u.Move(192, 60); u.Down(192, 60); Frame();
        True(target < 1, $"grabbed on the thumb: the list stays put ({target:0})");
        u.Move(192, 92); Frame();
        True(Math.Abs(target - 100) < 2, $"then follows the pointer from there: 32 px along the 128 px of free track is 100 px of the 400 ({target:0})");
        u.Up(192, 92);
    }

    [Scenario("opening onto a tall tab settles at its height without swelling past it more than a few pixels")]
    public static void TallTabsDontSwell()
    {
        var (isl, host, clock) = NewIsland(new Config { Weather = true });
        isl.Tab = isl.P.Dashboard.TabIndex("weather");
        isl.Expand();
        double target = 0, peak = 0;
        for (int f = 0; f < 90; f++) { clock.Advance(1 / 60.0, isl); target = isl.TargetHeight; peak = Math.Max(peak, isl.Shape.H); }
        True(peak - target < 6, $"it overshoots its height by {peak - target:0.0} px (looked too big, then shrank)");
        True(Math.Abs(isl.Shape.H - target) < 0.5, "and settles at it");
    }

    static FakeMedia Music(FakeClock clock) => new(clock) { Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "A song", Playing = true } };

    [Scenario("while media plays, notifications, calls and notices wait, then come back as one summary when it stops")]
    public static void HeldWhileFocused()
    {
        var (isl, host, clock) = NewIsland(new Config { HoldWhileFocused = true });
        var media = Music(clock); isl.Svc.Media = media;
        isl.Alert(new Alert { Key = "n1", Kind = "notification", Title = "Sam", Subtitle = "WhatsApp", Detail = "hi", Sound = Services.Sounds.Notification });
        isl.Alert(new Alert { Key = "full", Kind = "battery", Title = "Fully charged", Sound = Services.Sounds.Notice });
        isl.Alert(new Alert { Key = "call", Kind = "call", Title = "Mom", Priority = 3, Sticky = true, MaxSeconds = 30 });
        clock.Advance(1, isl);
        Eq(Mode.Rest, isl.Mode, "nothing pops up over the music");
        Eq(3, isl.HeldAlerts.Count, "three waiting");
        Eq(3, isl.History.Count, "and in Recent at once");
        isl.DismissAlert("call");   // the call ended unanswered while it waited
        // a timer you set and low battery never wait
        isl.Alert(new Alert { Key = "timer", Kind = "timer", Title = "Timer done", Priority = 2, Seconds = 2 });
        clock.Advance(0.2, isl);
        Eq("timer", isl.CurrentAlert?.Kind, "the timer comes through");
        clock.Advance(3, isl);

        media.Current.Playing = false; isl.ActivityChanged();
        clock.Advance(0.5, isl);
        Eq(3, isl.HeldAlerts.Count, "a moment's pause (a skip, a seek) isn't the end");
        clock.Advance(1.3, isl);
        Eq(0, isl.HeldAlerts.Count, "stopped: they come back");
        Eq("focus:summary", isl.CurrentAlert?.Key, "as one summary");
        Eq("3 while you were focused", isl.CurrentAlert.Title, "how many");
        Eq("WhatsApp, Fully charged, Missed call", isl.CurrentAlert.Subtitle, "and what");
    }

    [Scenario("held notices come back when fullscreen ends on every screen, or as soon as the island is used; one alone comes back as itself")]
    public static void HeldUntilFullscreenEndsOrUsed()
    {
        var (isl, host, clock) = NewIsland(new Config { HoldWhileFocused = true });
        isl.SetAnyFullscreen(true);
        isl.Alert(new Alert { Key = "n1", Kind = "notification", Title = "Sam", Subtitle = "WhatsApp", Detail = "hi" });
        clock.Advance(1, isl);
        Eq(1, isl.HeldAlerts.Count, "a game fullscreen on the TV holds it");
        isl.SetAnyFullscreen(false); clock.Advance(1.8, isl);
        Eq("n1", isl.CurrentAlert?.Key, "fullscreen over: one alone shows as itself");
        clock.Advance(10, isl);

        isl.SetAnyFullscreen(true);
        isl.Alert(new Alert { Key = "n2", Kind = "notification", Title = "Ana", Subtitle = "Discord", Detail = "gg" });
        isl.Alert(new Alert { Key = "n3", Kind = "notification", Title = "Ana", Subtitle = "Discord", Detail = "again" });
        clock.Advance(1, isl);
        Eq(2, isl.HeldAlerts.Count, "two waiting");
        isl.Expand(); clock.Advance(0.6, isl);
        Eq(0, isl.HeldAlerts.Count, "opening the island brings them back, though the game is still fullscreen");
        isl.Alert(new Alert { Key = "n4", Kind = "notification", Title = "Ana", Subtitle = "Discord", Detail = "hey" });
        Eq(0, isl.HeldAlerts.Count, "and while it's open, nothing waits");

        var (isl2, _, clock2) = NewIsland(new Config { HoldWhileFocused = false });
        isl2.Svc.Media = Music(clock2);
        isl2.Alert(new Alert { Key = "n1", Kind = "notification", Title = "Sam", Subtitle = "WhatsApp", Detail = "hi" });
        clock2.Advance(0.3, isl2);
        Eq("n1", isl2.CurrentAlert?.Key, "with the setting off it shows over the music as before");
    }

    [Scenario("sounds while media plays: full, quieter or muted; a timer always rings at full")]
    public static void SoundsWhilePlaying()
    {
        var c = new Config { SoundsWhilePlaying = "quiet" };
        Eq(1f, Isola.App.App.WhilePlayingScale(c, Services.Sounds.Notification, false), "no media: full");
        True(Isola.App.App.WhilePlayingScale(c, Services.Sounds.Notification, true) is > 0.2f and < 0.5f, "quieter while media plays");
        c.SoundsWhilePlaying = "mute";
        Eq(0f, Isola.App.App.WhilePlayingScale(c, Services.Sounds.Call, true), "muted");
        Eq(1f, Isola.App.App.WhilePlayingScale(c, Services.Sounds.Timer, true), "the timer still rings");
        c.SoundsWhilePlaying = "full";
        Eq(1f, Isola.App.App.WhilePlayingScale(c, Services.Sounds.Notice, true), "full");
        var bad = new Config { SoundsWhilePlaying = "loud" }; bad.Normalize();
        Eq("full", bad.SoundsWhilePlaying, "an unknown choice is full");
    }

    [Scenario("a video paused but still fullscreen keeps holding; they show once it's paused and fullscreen is closed")]
    public static void PausedFullscreenVideoStillHolds()
    {
        var (isl, host, clock) = NewIsland(new Config { HoldWhileFocused = true });
        var media = new FakeMedia(clock) { Current = new MediaInfo { AppName = "Chrome", SessionId = "Chrome", Title = "A video", Playing = true } };
        isl.Svc.Media = media;
        isl.SetAnyFullscreen(true);
        isl.Alert(new Alert { Key = "n1", Kind = "notification", Title = "Sam", Subtitle = "WhatsApp", Detail = "hi" });
        media.Current.Playing = false; isl.ActivityChanged();
        clock.Advance(3, isl);
        Eq(1, isl.HeldAlerts.Count, "paused, still fullscreen: still held");
        isl.SetAnyFullscreen(false); clock.Advance(1.8, isl);
        Eq("n1", isl.CurrentAlert?.Key, "fullscreen closed too: it shows");
    }

    [Scenario("hovering doesn't release held notices; opening the island shows them as its banner, so the click can't dismiss them unseen")]
    public static void HoverKeepsHeldOpenShowsBanner()
    {
        var (isl, host, clock) = NewIsland(new Config { HoldWhileFocused = true });
        isl.Svc.Media = Music(clock);
        isl.Alert(new Alert { Key = "n1", Kind = "notification", Title = "Sam", Subtitle = "WhatsApp", Detail = "hi" });
        clock.Advance(1, isl);
        isl.MouseMove(450, 20); clock.Advance(1.2, isl);
        Eq(Mode.Peek, isl.Mode, "hovering shows the hover view");
        Eq(1, isl.HeldAlerts.Count, "and leaves it waiting");
        isl.Expand(); clock.Advance(0.3, isl);
        Eq(Mode.Expanded, isl.Mode, "the click opens the island");
        Eq("n1", isl.InlineAlert?.Key, "the notice shows as its banner, there to be seen");
        Eq(0, isl.HeldAlerts.Count, "nothing left waiting");
    }

    [Scenario("switching back and forth: a player you can hear takes over at once from a paused or silent one; a muted preview still waits")]
    public static void HeardNewcomerTakesOverAtOnce()
    {
        static bool none(MediaInfo _) => false;
        var spotify = Playing("Spotify", "Spotify.exe", 10); var chrome = Playing("Chrome", "Chrome", 100);
        var all = new List<MediaInfo> { spotify, chrome };
        // Spotify on screen, silent at 0%; the video starts and is heard 0.3 s in
        var (p1, r1) = MediaService.Pick(spotify, all, null, null, none, 100.3, i => i == chrome ? true : false);
        Eq("Chrome", p1.AppName, "the video shows right away");
        True(double.IsNaN(r1), "no wait");
        // Spotify on screen and paused
        spotify.Playing = false;
        Eq("Chrome", MediaService.Pick(spotify, all, null, null, none, 100.3, i => i == chrome ? true : null).pick.AppName, "over a paused one too");
        // a preview: playing but muted (not heard), over a paused Spotify: it still waits its moment
        var (p2, r2) = MediaService.Pick(spotify, all, null, null, none, 100.3, i => null);
        Eq("Spotify", p2.AppName, "a muted newcomer (a hover preview) waits before taking over");
        True(!double.IsNaN(r2), "and is looked at again after the wait");
        // and over music you can hear, an unheard newcomer doesn't take over at all
        spotify.Playing = true;
        Eq("Spotify", MediaService.Pick(spotify, all, null, null, none, 103, i => i == spotify ? true : null).pick.AppName, "music you can hear stays");
    }
}
