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
using Vortice.Mathematics;

namespace Isola.Tests;

public static partial class Scenarios
{
    // ---- harness helpers ----------------------------------------------------
    static (Island.Island isl, FakeHost host, FakeClock clock) NewIsland(Config cfg = null, bool hello = false)
    {
        Ui.Reset();
        var clock = new FakeClock(); Ui.Clock = clock;
        cfg ??= new Config();
        cfg.HelloOnLaunch = hello;
        var host = new FakeHost();
        var isl = new Island.Island(cfg, host, clock) { Svc = new Hub() };
        isl.Start();
        clock.Advance(1.0, isl);
        return (isl, host, clock);
    }

    static void Eq<T>(T want, T got, string what) { if (!Equals(want, got)) throw new Exception($"{what}: expected {want}, got {got}"); }
    static void True(bool b, string what) { if (!b) throw new Exception(what); }

    // =====================================================================
    // springs: the measured Apple parameters
    // =====================================================================

    [Scenario("the expand spring overshoots ~6% and peaks ~0.30 s in, as measured from Apple's film")]
    public static void SpringMatchesMeasurement()
    {
        var s = new Spring(445, SpringSpec.Expand, 0.01);
        s.To(761);
        double peak = 0, peakT = 0, t = 0;
        while (t < 2) { s.Step(1 / 240.0); t += 1 / 240.0; if (s.Value > peak) { peak = s.Value; peakT = t; } }
        double overshoot = (peak - 761) / (761 - 445);
        True(overshoot is > 0.04 and < 0.09, $"overshoot {overshoot:P1}");
        True(peakT is > 0.25 and < 0.36, $"peak at {peakT:0.000}s");
        True(s.Settled && Math.Abs(s.Value - 761) < 0.02, "settles on target");
    }

    [Scenario("a spring retargeted mid-flight keeps its value and velocity (no jump)")]
    public static void SpringInterruptible()
    {
        var s = new Spring(0, SpringSpec.Expand);
        s.To(100);
        for (int i = 0; i < 12; i++) s.Step(1 / 120.0);
        double v = s.Value, vel = s.Velocity;
        s.To(-50);
        Eq(v, s.Value, "value at retarget"); Eq(vel, s.Velocity, "velocity at retarget");
        s.Step(1 / 120.0);
        True(Math.Abs(s.Value - v) < 5, "next frame continues from where it was");
    }

    [Scenario("one long frame lands exactly where many short ones do (closed-form solver)")]
    public static void SpringFrameRateIndependent()
    {
        var a = new Spring(0, SpringSpec.Wobbly); a.To(1);
        var b = new Spring(0, SpringSpec.Wobbly); b.To(1);
        a.Step(0.2);
        for (int i = 0; i < 48; i++) b.Step(0.2 / 48);
        True(Math.Abs(a.Value - b.Value) < 1e-6, $"{a.Value} vs {b.Value}");
    }

    // =====================================================================
    // island state machine
    // =====================================================================

    [Scenario("hello plays at launch and settles into the resting pill")]
    public static void HelloThenRest()
    {
        var (isl, host, clock) = NewIsland(hello: true);
        Eq(Mode.Hello, isl.Mode, "after launch");
        clock.Advance(3.5, isl);
        Eq(Mode.Rest, isl.Mode, "after hello");
        Eq("idle", isl.CurrentPage?.Key, "resting page");
    }

    [Scenario("auto-hides after the configured inactivity, leaving the indicator; hovering resets the clock")]
    public static void AutoHide()
    {
        var (isl, host, clock) = NewIsland(new Config { AutoHideMinutes = 2 });
        clock.Advance(100, isl);
        isl.MouseMove(450, 20); clock.Advance(0.3, isl); isl.MouseLeave(); clock.Advance(1.5, isl);   // activity at ~100 s
        clock.Advance(110, isl);
        Eq(Mode.Rest, isl.Mode, "still visible 110 s after the hover");
        clock.Advance(15, isl);
        Eq(Mode.Hidden, isl.Mode, "hidden 2 min after the last activity");
    }

    [Scenario("dragging it up hides it, and the pointer still at the top does NOT bring it straight back")]
    public static void DragUpStaysHidden()
    {
        var (isl, host, clock) = NewIsland();
        isl.MouseDown(450, 20);
        isl.DragTo(0, -20);
        isl.DragEnd(0, -20, -400);
        Eq(Mode.Hidden, isl.Mode, "dragged up");
        // hiding reshapes the input region: Windows sends a leave and a fresh enter
        isl.MouseLeave(); isl.MouseMove(450, 2); clock.Advance(0.5, isl);
        Eq(Mode.Hidden, isl.Mode, "hover on the indicator right after hiding is ignored");
        // the app's poller clears this once the pointer has left the strip
        isl.RevealSuppressed = false; isl.MouseLeave(); isl.MouseMove(450, 2); clock.Advance(0.3, isl);
        Eq(Mode.Hidden, isl.Mode, "a pointer passing over the hidden island doesn't bring it back");
        clock.Advance(0.3, isl);
        Eq(Mode.Rest, isl.Mode, "resting there does");
    }

    [Scenario("a HUD while hidden slides back up with its content, instead of vanishing")]
    public static void HudWhileHiddenSlidesBack()
    {
        var (isl, host, clock) = NewIsland();
        isl.SetMode(Mode.Hidden, "test"); clock.Advance(1, isl);
        isl.Hud(HudKind.CapsLock, 1);
        clock.Advance(0.6, isl);
        Eq(Mode.Hud, isl.Mode, "HUD shows from hidden");
        float shown = isl.Shape.Bottom;
        double t = 0;
        while (isl.Mode == Mode.Hud && t < 3) { clock.Advance(1 / 60.0, isl); t += 1 / 60.0; }
        Eq(Mode.Hidden, isl.Mode, "back to hidden");
        clock.Advance(0.1, isl);
        True(isl.CurrentPage?.Key?.StartsWith("hud") == true, $"keeps the HUD content while sliding (the old code dropped it at once): page {isl.CurrentPage?.Key}");
        True(isl.Presence > 0.95, $"stays opaque while sliding (the old code faded it out): {isl.Presence:0.00}");
        True(isl.Shape.Bottom > shown * 0.2, "is still on its way up 0.1 s after the switch");
        clock.Advance(1.5, isl);
        True(isl.Shape.Bottom <= 0.5, $"ends fully above the screen edge: bottom {isl.Shape.Bottom:0.0}");
    }

    [Scenario("fullscreen on the island's screen steps aside; ending it restores the same state")]
    public static void FullscreenStepsAside()
    {
        var (isl, host, clock) = NewIsland();
        isl.SetFullscreen(true, "video"); clock.Advance(1, isl);
        Eq(Mode.Off, isl.Mode, "off in fullscreen");
        isl.Hud(HudKind.Volume, 0.5f);
        Eq(Mode.Off, isl.Mode, "no HUD over a fullscreen game");
        isl.SetFullscreen(false, "done"); clock.Advance(1, isl);
        Eq(Mode.Rest, isl.Mode, "back after fullscreen");
        isl.SetMode(Mode.Hidden, "test"); isl.SetFullscreen(true, "v"); isl.SetFullscreen(false, "v"); clock.Advance(1, isl);
        Eq(Mode.Hidden, isl.Mode, "was hidden before fullscreen -> hidden after");
    }

    [Scenario("with the island open, a notice drops in as a banner on the tab; a call interrupts anything")]
    public static void AlertQueueing()
    {
        var (isl, host, clock) = NewIsland();
        isl.Expand(); clock.Advance(0.5, isl);
        isl.Alert(new Alert { Key = "bt", Title = "AirPods", Seconds = 3 });
        Eq(Mode.Expanded, isl.Mode, "the island stays on its tab");
        Eq("bt", isl.InlineAlert?.Key, "and the notice drops in as a banner at its top (it used to wait unseen)");
        isl.Alert(new Alert { Key = "call", Kind = "call", Title = "Mom", Priority = 2, Sticky = true });
        Eq(Mode.Alert, isl.Mode, "call interrupts");
        Eq("call", isl.CurrentAlert.Key, "call shown");
        True(isl.InlineAlert == null, "the banner, already seen, goes");
    }

    [Scenario("the album's colors stay on the glow while paused (they used to snap to the default)")]
    public static void PausedKeepsAlbumColors()
    {
        var (isl, host, clock) = NewIsland();
        var media = new FakeMedia(clock) { Current = new MediaInfo { Title = "Golden Hour", Playing = true, Palette = new[] { Col.Hex(0xff66aa), Col.Hex(0xcc3388), Col.Hex(0x993366) } } };
        media.LastPlayingAt = clock.Now;
        isl.Svc.Media = media;
        isl.UpdateGlowColors(); clock.Advance(2, isl);
        var playing = isl.GlowTarget0;
        media.Current.Playing = false;
        isl.UpdateGlowColors(); clock.Advance(2, isl);
        Eq(playing, isl.GlowTarget0, "paused keeps the album color");
        clock.Advance(700, isl); isl.UpdateGlowColors();
        True(!isl.GlowTarget0.Equals(playing), "after ten idle minutes it returns to the theme");
    }

    [Scenario("swipe down opens search; swipe up hides; horizontal swipes skip tracks")]
    public static void Swipes()
    {
        var (isl, host, clock) = NewIsland();
        isl.Swipe(0, 60); Eq(Mode.Search, isl.Mode, "swipe down");
        isl.Swipe(0, -60); Eq(Mode.Rest, isl.Mode, "swipe up from search closes it");
        isl.Swipe(0, -60); Eq(Mode.Hidden, isl.Mode, "swipe up at rest hides");
        var media = new FakeMedia(clock) { Current = new MediaInfo { Title = "x", Playing = true } };
        isl.Svc.Media = media; isl.Reveal("t"); clock.Advance(0.5, isl);
        isl.Swipe(-80, 0); Eq(1, media.Nexts, "left swipe = next");
        isl.Swipe(80, 0); Eq(1, media.Prevs, "right swipe = previous");
    }

    [Scenario("the island has no right-click menu of its own")]
    public static void NoContextMenu()
    {
        var (isl, host, clock) = NewIsland();
        isl.RightClick(450, 20);
        Eq(0, host.Menus, "context menu requests");
    }
}

/// <summary>A media source scenarios drive by hand.</summary>
public sealed class FakeMedia : IMediaSourceEx
{
    readonly IClock clock;
    public FakeMedia(IClock c) { clock = c; }
    public MediaInfo Current { get; set; }
    public IReadOnlyList<MediaInfo> Sessions => Current == null ? Array.Empty<MediaInfo>() : new[] { Current };
    public double LastPlayingAt { get; set; } = -1e9;
    public event Action Changed { add { } remove { } }
    public event Action<MediaInfo, MediaInfo> TrackChanged { add { } remove { } }
    public int Nexts, Prevs, Toggles;
    public void PlayPause() => Toggles++;
    public void Next() => Nexts++;
    public void Previous() => Prevs++;
    public void Seek(double seconds) { }
    public void ToggleShuffle() { }
    public void CycleRepeat() { }
    public void Select(string sessionId) { }
    public float Level { get; set; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ScenarioAttribute : Attribute { public string Name; public ScenarioAttribute(string n) { Name = n; } }
