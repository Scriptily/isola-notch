using System.Numerics;
using Isola.App;
using Isola.Render;
using Isola.Services;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Box = Isola.Render.Box;
using FontWeight = Vortice.DirectWrite.FontWeight;

namespace Isola.Island;

/// <summary>All the pages, created once.</summary>
public sealed class Pages
{
    public readonly HelloPage Hello = new();
    public readonly IdlePage Idle = new();
    public readonly CompactPage Compact = new();
    public readonly PeekPage Peek = new();
    public readonly AlertPage AlertPage = new();
    public readonly HudPage Hud = new();
    public readonly DashboardPage Dashboard;
    public readonly SearchPage Search = new();
    public Pages(Island isl) { Dashboard = new DashboardPage(isl); }
}

/// <summary>Shared drawing bits.</summary>
public static class Paint
{
    static readonly Dictionary<string, (ID2D1Bitmap1 bmp, int gen)> images = new();

    /// <summary>GPU bitmap for encoded bytes, cached by key for the device's lifetime.</summary>
    public static ID2D1Bitmap1 Image(Canvas c, string key, byte[] data)
    {
        if (data == null || string.IsNullOrEmpty(key)) return null;
        if (images.TryGetValue(key, out var e) && e.gen == Gfx.Generation) return e.bmp;
        var b = Canvas.Decode(c.C, data);
        if (images.Count > 48) { foreach (var kv in images.Take(16).ToList()) { kv.Value.bmp?.Dispose(); images.Remove(kv.Key); } }
        images[key] = (b, Gfx.Generation);
        return b;
    }

    public static void Art(Canvas c, MediaInfo m, Box b, float radius, float alpha = 1)
    {
        var bmp = m == null ? null : Image(c, "art:" + m.ArtKey, m.Art);
        if (bmp != null) { c.Image(bmp, b, alpha, radius); return; }
        // no artwork: a gradient tile in the session's colors with a note glyph
        var p = m?.Palette is { Length: > 0 } ? m.Palette : Island.PaletteFor("aurora");
        using var br = c.Linear(new Vector2(b.X, b.Y), new Vector2(b.Right, b.Bottom), (0, p[0].A(alpha)), (1, p[^1].A(alpha * 0.8f)));
        c.Fill(b, br, radius);
        c.Icon(m?.IsVideo == true ? Glyph.Tv : Glyph.Music, b.CX, b.CY, b.H * 0.42f, Col.White.A(0.9f * alpha));
    }

    public static string Time(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Clock(DateTime t, bool h24) => h24 ? t.ToString("HH:mm") : t.ToString("h:mm");

    public static Color4 Accent(Island isl)
    {
        var m = isl.Svc?.Media?.Current;
        if (m?.Palette is { Length: > 0 }) return Col.Legible(m.Palette[0]);
        return Col.Hex(0x7aa2ff);
    }

    /// <summary>
    /// A weather icon made from Fluent glyphs: a cloud with rain strokes, snow
    /// dots or a bolt; a sun or moon alone or peeking over a cloud; fog bars.
    /// White, with the sun and the bolt warm.
    /// </summary>
    public static void Weather(Canvas c, string kind, float cx, float cy, float size, float alpha = 1)
    {
        var white = Col.White.A(0.92f * alpha);
        var warm = Col.Hex(0xffd60a).A(alpha);
        const char Cloud = '\uE753', Sun = '\uE706', Moon = '\uE708', Bolt = '\uE945';
        float s = size;
        switch (kind)
        {
            case "sun": c.Icon(Sun, cx, cy, s, warm); return;
            case "moon": c.Icon(Moon, cx, cy, s * 0.9f, white); return;
            case "partly-day": c.Icon(Sun, cx + s * 0.22f, cy - s * 0.2f, s * 0.7f, warm); c.Icon(Cloud, cx - s * 0.06f, cy + s * 0.1f, s * 0.86f, white); return;
            case "partly-night": c.Icon(Moon, cx + s * 0.24f, cy - s * 0.22f, s * 0.58f, white.A(0.75f * alpha)); c.Icon(Cloud, cx - s * 0.06f, cy + s * 0.1f, s * 0.86f, white); return;
            case "fog":
                for (int i = 0; i < 3; i++) c.Fill(new Box(cx - s * 0.42f + (i == 1 ? s * 0.1f : 0), cy - s * 0.24f + i * s * 0.24f, s * (i == 1 ? 0.74f : 0.84f), Math.Max(1.5f, s * 0.08f)), white, s * 0.04f);
                return;
            case "wind":
                for (int i = 0; i < 3; i++) c.Fill(new Box(cx - s * 0.44f, cy - s * 0.22f + i * s * 0.22f, s * (0.88f - i * 0.18f), Math.Max(1.5f, s * 0.08f)), white, s * 0.04f);
                return;
            case "alert": c.Icon('\uE7BA', cx, cy, s * 0.9f, Col.Hex(0xff9f0a).A(alpha)); return;
        }
        // a cloud, raised to make room for what falls from it
        float up = kind is "rain" or "pour" or "snow" or "thunder" ? s * 0.16f : 0;
        c.Icon(Cloud, cx, cy - up, s, white);
        float baseY = cy - up + s * 0.34f, w = Math.Max(1.4f, s * 0.07f);
        switch (kind)
        {
            case "rain":
            case "pour":
                int n = kind == "pour" ? 4 : 3;
                for (int i = 0; i < n; i++)
                {
                    float x = cx - s * 0.24f + i * s * (0.48f / (n - 1));
                    c.Fill(new Box(x - w / 2, baseY + (i % 2) * s * 0.06f, w, s * 0.2f), white, w / 2);
                }
                break;
            case "snow":
                for (int i = 0; i < 3; i++) c.Circle(cx - s * 0.22f + i * s * 0.22f, baseY + s * 0.1f + (i % 2) * s * 0.07f, Math.Max(1.4f, s * 0.06f), white);
                break;
            case "thunder":
                c.Icon(Bolt, cx + s * 0.02f, baseY + s * 0.12f, s * 0.42f, warm);
                break;
        }
    }

    /// <summary>Battery ring with a percentage inside (AirPods style).</summary>
    public static void BatteryRing(Canvas c, float cx, float cy, float r, int pct, bool charging, float alpha = 1, bool plugged = false, Color4? normal = null)
    {
        var col = pct < 0 ? Col.Gray(0.5f) : charging ? Col.Hex(0x34c759) : pct <= 20 && !plugged ? Col.Hex(0xff453a) : normal ?? Col.Hex(0x34c759);
        c.Ring(cx, cy, r, Col.White.A(0.16f * alpha), 3);
        if (pct > 0) c.Arc(cx, cy, r, 0, MathF.PI * 2 * pct / 100f, col.A(alpha), 3);
        c.Text(pct < 0 ? "–" : pct.ToString(), Box.Centered(cx, cy, r * 2.4f, r * 2), r * 0.72f, Col.White.A(0.92f * alpha), FontWeight.SemiBold, HAlign.Center);
        // Charging: a bolt badge on the ring's shoulder, as on the iPhone. It
        // used to replace the number, so a charging AirPods case showed no level.
        // Plugged in but not charging (full, or at a charge limit): a plug badge.
        if (charging || plugged)
        {
            float bx = cx + r * 0.78f, by = cy - r * 0.78f, br = r * 0.5f;
            c.Circle(bx, by, br + 1.2f, Col.Black.A(alpha));
            c.Circle(bx, by, br, (charging ? col : Col.Gray(0.82f)).A(alpha));
            if (charging) c.Icon(Glyph.Charging, bx, by, br * 1.3f, Col.Black.A(alpha));
            else
            {
                // a plug, drawn: the icon font's "plug" is a battery with a plug, unreadable this small
                var ink = Col.Black.A(alpha);
                float u = br / 6f;
                c.Fill(new Box(bx - 2.2f * u, by - 4.2f * u, 1.1f * u, 2.4f * u), ink, 0.4f * u);   // prongs
                c.Fill(new Box(bx + 1.1f * u, by - 4.2f * u, 1.1f * u, 2.4f * u), ink, 0.4f * u);
                c.Fill(new Box(bx - 3.2f * u, by - 2.0f * u, 6.4f * u, 3.6f * u), ink, 1.2f * u);   // body
                c.Fill(new Box(bx - 0.6f * u, by + 1.4f * u, 1.2f * u, 2.8f * u), ink);             // cord
            }
        }
    }

    /// <summary>
    /// A loading placeholder in the shape of what is coming: a soft block with
    /// a highlight sweeping across it (all skeletons share one sweep, so a
    /// column of them shimmers together).
    /// </summary>
    public static void Skeleton(Canvas c, Box b, float radius, double now, float alpha = 1)
    {
        if (alpha <= 0.003f || b.W <= 0 || b.H <= 0) return;
        c.LoaderFrame = c.Frame;
        c.Fill(b, Col.White.A(0.075f * alpha), radius);
        float phase = (float)(now / 1.4 % 1.0);
        float sweepX = -300 + phase * 1600;            // in window space, so blocks line up
        using var hi = c.Linear(new Vector2(sweepX - 120, 0), new Vector2(sweepX + 120, 0),
            (0, Col.White.A(0)), (0.5f, Col.White.A(0.085f * alpha)), (1, Col.White.A(0)));
        c.Fill(b, hi, radius);
    }

    /// <summary>
    /// The iOS activity indicator: eight rounded spokes, the brightest stepping
    /// round once every 0.8 s. For a wait with no shape to preview - an action
    /// in flight, a connection being made. Content on its way gets a skeleton
    /// in its own shape instead, so the layout doesn't jump when it lands.
    /// </summary>
    public static void Spinner(Canvas c, float cx, float cy, float size, Color4 color, double now, float alpha = 1)
    {
        if (alpha <= 0.003f) return;
        c.LoaderFrame = c.Frame;
        const int n = 8;
        int head = (int)(now / 0.8 * n) % n;
        float w = Math.Max(1.4f, size * 0.12f), r0 = size * 0.25f + w / 2, r1 = size * 0.5f - w / 2;
        for (int k = 0; k < n; k++)
        {
            float a = k * MathF.PI * 2 / n, sx = MathF.Sin(a), sy = -MathF.Cos(a);
            int age = (head - k + n) % n;                  // 0 = the bright one, then trailing
            c.Line(cx + sx * r0, cy + sy * r0, cx + sx * r1, cy + sy * r1, color.A(alpha * (1 - age * 0.105f)), w, c.RoundCap);
        }
    }

    /// <summary>A skeleton list row: an icon block and two text bars.</summary>
    public static void SkeletonRow(Canvas c, Box row, double now, float alpha = 1, int seed = 0)
    {
        Skeleton(c, Box.Centered(row.X + 22, row.CY, 26, 26), 7, now, alpha);
        float w1 = 0.38f + 0.18f * ((seed * 37) % 7) / 7f, w2 = 0.55f + 0.2f * ((seed * 53) % 5) / 5f;
        Skeleton(c, new Box(row.X + 44, row.Y + 9, (row.W - 60) * w1, 10), 5, now, alpha);
        Skeleton(c, new Box(row.X + 44, row.Y + 25, (row.W - 60) * w2, 8), 4, now, alpha * 0.8f);
    }

    /// <summary>Animated equalizer bars driven by the real output level.</summary>
    public static void Waveform(Canvas c, Box b, float level, bool playing, double now, Color4 color, int bars = 5)
    {
        float gap = b.W / (bars * 2 - 1);
        for (int i = 0; i < bars; i++)
        {
            float h = BarHeight(b, i, bars, level, playing, now);
            c.Fill(new Box(b.X + i * gap * 2, b.CY - h / 2, gap, h), color, gap / 2);
        }
    }

    /// <summary>Height of one waveform bar, in the same units as the box.</summary>
    internal static float BarHeight(Box b, int i, int bars, float level, bool playing, double now)
    {
        float gap = b.W / (bars * 2 - 1);
        double ph = i * 1.7 + (i % 2) * 0.9;
        // two out-of-step oscillators per bar, so it never looks like a loop
        float wob = (float)(0.5 + 0.3 * Math.Sin(now * (5.1 + i * 1.3) + ph) + 0.2 * Math.Sin(now * (8.3 - i * 0.7) + ph * 2.1));
        float shape = 1 - Math.Abs(i - (bars - 1) / 2f) / bars * 0.9f;
        // Always alive while playing (as Apple's is), taller with the real
        // loudness; flat dots when paused. Playback can be silent here while
        // "playing" - Spotify's own volume at 0% (a real case),
        // or playing on another device - so the motion alone carries it then.
        // The old formula leaned on the level: at level 0 the middle bar swung
        // 1.7 DIP, less than its own 2.4 DIP width: five still dots.
        float lv = playing ? Math.Clamp(0.22f + 0.38f * wob * shape + level * 1.3f * wob * shape, 0.15f, 1f) : 0.14f;
        return Math.Max(gap, b.H * lv);
    }
}

// ============================================================================
// Hello
// ============================================================================

public sealed class HelloPage : Page
{
    double start;
    public override string Key => "hello";
    public override (float, float, float) Size(Island isl) => (264, 50, 25);
    public override void Shown(Island isl) => start = isl.Clock.Now;
    public override bool WantsContinuousFrames(Island isl) => isl.Clock.Now - start < 3.2;

    public override void Draw(Island isl, Canvas c, Ux ux, Box a, float t)
    {
        double el = isl.Clock.Now - start;
        // "hello" written in, left to right, in the aurora colors; the reveal
        // edge is soft so it reads as ink going down rather than a wipe.
        float reveal = (float)Ease.InOutSine(Ease.Range(el, 0.35, 1.9));
        var pal = Island.PaletteFor(isl.Cfg.GlowColors == "auto" ? "aurora" : isl.Cfg.GlowColors);
        var tb = new Box(a.X + 78, a.Y + 1, a.W - 98, a.H - 2);
        var (tw, _) = c.Measure("hello", 30, FontWeight.Normal, 1000, false, "Segoe Script");
        float x0 = tb.X + (tb.W - tw) / 2;
        // lifted toward white so it reads over any glow (media colors included)
        using var ink = c.Linear(new Vector2(x0, 0), new Vector2(x0 + tw, 0), (0, Col.Mix(pal[0], Col.White, 0.35f)), (0.5f, Col.Mix(pal[1], Col.White, 0.45f)), (1, Col.Mix(pal[2], Col.White, 0.35f)));
        using var mask = c.Linear(new Vector2(x0 + tw * reveal - 14, 0), new Vector2(x0 + tw * reveal + 4, 0), (0, Col.White), (1, Col.White.A(0)));
        c.Push(null, 1, mask);
        var fmt = c.Format(30, FontWeight.Normal, "Segoe Script", false, HAlign.Left, false, false);
        var lay = c.Layout("hello", fmt, 1000, 120);
        c.C.DrawTextLayout(new Vector2(x0, a.CY - Canvas.InkCenter(lay)), lay, ink, DrawTextOptions.EnableColorFont);
        c.Pop();
        // the eyes say hi first: open, then a wink
        float eyesIn = (float)Ease.OutCubic(Ease.Range(el, 0.05, 0.45));
        double wink = el is > 1.9 and < 2.25 ? Math.Sin((el - 1.9) / 0.35 * Math.PI) : 0;
        IdlePage.Eyes(c, a.X + 46, a.CY, 1.0f * eyesIn, 0, 0, 1, (float)(1 - 0.9 * wink), happy: el > 2.4 ? (float)Ease.Range(el, 2.4, 2.7) : 0);
    }
}

// ============================================================================
// Idle: the pill at rest. Eyes that blink, look at the cursor, doze, and wake.
// ============================================================================

public sealed class IdlePage : Page
{
    double nextBlink, blinkStart = -10, lastNear = double.NaN, happyStart = -10;
    bool asleep;
    static readonly Random rng = new();
    public override string Key => "idle";
    public override (float, float, float) Size(Island isl) => isl.Cfg.Style == "notch" ? (200, 32, 12) : (186, 34, 17);

    // Eyes look at the cursor while it's near and moving. Their direction comes
    // straight from the pointer's position, so a pointer resting near the top of
    // the screen (over browser tabs, say) needs no frames at all; asking for
    // them whenever it was merely near redrew at the display's full rate, 17%
    // of a core, for as long as it sat there. The app polls the pointer cheaply
    // (CursorPollInterval) so movement is still noticed without drawing.
    double cursorX = double.NaN, cursorY, cursorMovedAt = -10;
    const double LookTail = 0.6;    // keep drawing while the gaze settles after the pointer stops

    public override bool WantsContinuousFrames(Island isl)
    {
        if (isl.Cfg.IdleFace != "eyes" || !isl.Cfg.EyesFollowCursor) return false;
        var (x, y, _) = isl.Host.Cursor();
        if (x != cursorX || y != cursorY) { cursorX = x; cursorY = y; cursorMovedAt = isl.Clock.Now; }
        return isl.Clock.Now - cursorMovedAt < LookTail && Near(isl, out _, out _);
    }

    /// <summary>How often the app should look at the pointer for these eyes: often when it's near, now and then otherwise.</summary>
    public double CursorPoll(Island isl) =>
        isl.Cfg.IdleFace != "eyes" || !isl.Cfg.EyesFollowCursor ? double.PositiveInfinity : Near(isl, out _, out _) ? 1 / 30.0 : 0.15;
    public override double AmbientInterval(Island isl)
    {
        if (isl.Cfg.IdleFace == "clock") return 1.0;
        if (isl.Cfg.IdleFace != "eyes") return 0;
        double now = isl.Clock.Now;
        if (now - blinkStart < 0.26 || now - happyStart < 1.3) return 1 / 45.0;
        if (asleep) return 0.9;                              // the z drifts slowly
        return Math.Clamp(nextBlink - now, 1 / 45.0, 8);     // nothing to draw until then
    }

    // "Near" was 520 DIP either side and 420 below: the eyes turned and woke
    // whenever the pointer worked anywhere around the top of the screen.
    internal const float NearX = 300, NearY = 220, WakeRadius = 140;

    bool Near(Island isl, out float dx, out float dy)
    {
        var (x, y, on) = isl.Host.Cursor();
        var s = isl.Shape;
        dx = x - s.CX; dy = y - s.CY;
        return on && Math.Abs(dx) < NearX && dy < NearY;
    }

    public override void Draw(Island isl, Canvas c, Ux ux, Box a, float t)
    {
        double now = isl.Clock.Now;
        if (isl.Cfg.IdleFace == "none") return;
        if (isl.Cfg.IdleFace == "clock")
        {
            var w = isl.Clock.Wall;
            c.Text(Paint.Clock(w, isl.Cfg.Use24Hour), a, 14, Col.White.A(0.9f * t), FontWeight.SemiBold, HAlign.Center);
            return;
        }
        bool near = Near(isl, out float dx, out float dy);
        if (double.IsNaN(lastNear)) lastNear = now;   // awake when we first appear
        // dozing eyes only wake for a pointer that comes close, not one passing by
        if (near && (!asleep || dx * dx + dy * dy < WakeRadius * WakeRadius)) { if (asleep) { asleep = false; happyStart = now; } lastNear = now; }
        else if (now - lastNear > 90) asleep = true;
        if (now > nextBlink) { blinkStart = now; nextBlink = now + 2.8 + rng.NextDouble() * 4.5; }
        double bt = (now - blinkStart) / 0.22;
        float blink = bt is >= 0 and < 1 ? (float)(1 - Math.Sin(bt * Math.PI) * 0.92) : 1;
        float lookX = 0, lookY = 0;
        if (near && !asleep && isl.Cfg.EyesFollowCursor)
        {
            float d = MathF.Sqrt(dx * dx + dy * dy) + 1;
            lookX = Math.Clamp(dx / d, -1, 1) * Math.Min(1, d / 60) * 3.2f;
            lookY = Math.Clamp(dy / d, -1, 1) * Math.Min(1, d / 60) * 2.2f;
        }
        // The gaze eases toward where it should look (and back to center): set
        // straight from the pointer, it jumped the moment the pointer came near.
        lookX = isl.Ux.Anim("eyes:lx", lookX, Gaze);
        lookY = isl.Ux.Anim("eyes:ly", lookY, Gaze);
        float sleep = isl.Ux.Anim("eyes:sleep", asleep ? 1 : 0, SpringSpec.Slow);
        float happy = (float)Math.Max(0, 1 - Math.Abs((now - happyStart) - 0.6) / 0.6);
        Eyes(c, a.CX, a.CY + 0.5f, t, lookX, lookY, 1 - sleep * 0.88f, blink, happy);
        if (sleep > 0.6f)
        {
            float zt = (float)((now * 0.5) % 1.0);
            c.Text("z", new Box(a.CX + 16 + zt * 8, a.Y + 2 - zt * 4, 14, 14), 9 + zt * 3, Col.White.A((sleep - 0.6f) * 2 * (1 - zt) * 0.7f), FontWeight.SemiBold);
        }
    }

    // critically damped, about a third of a second: a glance, not a snap or a wobble
    static readonly SpringSpec Gaze = new(0.34, 1.0);

    /// <summary>Two soft white eyes. open: 0 closed .. 1 open; happy morphs them into ^ ^.</summary>
    public static void Eyes(Canvas c, float cx, float cy, float alpha, float lx, float ly, float open, float blink, float happy = 0)
    {
        if (alpha <= 0.01f) return;
        float sep = 13f, rx = 5.2f, ry = 8.2f;
        float o = Math.Clamp(open * blink, 0.06f, 1);
        var col = Col.White.A(alpha * 0.96f);
        for (int s = -1; s <= 1; s += 2)
        {
            float ex = cx + s * sep + lx, ey = cy + ly;
            if (happy > 0.05f)
            {
                // ^ shaped: draw an arc via two thick round lines
                float hw = rx * 1.1f, hh = ry * 0.55f;
                var a0 = new Vector2(ex - hw, ey + hh * 0.3f); var a1 = new Vector2(ex, ey - hh); var a2 = new Vector2(ex + hw, ey + hh * 0.3f);
                c.Ellipse(ex, ey, rx, ry * o * (1 - happy), col.A(1 - happy));
                c.C.DrawLine(a0, a1, c.Brush(col.A(happy)), 2.6f, c.RoundCap);
                c.C.DrawLine(a1, a2, c.Brush(col.A(happy)), 2.6f, c.RoundCap);
            }
            else
            {
                // soft halo, then the eye
                c.Ellipse(ex, ey, rx + 2.2f, ry * o + 2.2f, col.A(0.10f));
                c.Ellipse(ex, ey, rx, Math.Max(0.9f, ry * o), col);
            }
        }
    }
}

// ============================================================================
// Compact: the live activity at rest (media, timer, call)
// ============================================================================

public sealed class CompactPage : Page
{
    public override string Key => "compact:" + kind;
    string kind = "media";

    public bool HasActivity(Island isl)
    {
        kind = Pick(isl);
        return kind != null;
    }

    static string Pick(Island isl)
    {
        var s = isl.Svc;
        if (s == null) return null;
        if (s.Timer != null && s.Timer.Running) return "timer";
        var m = s.Media?.Current;
        if (isl.Cfg.Media && m != null && !string.IsNullOrEmpty(m.Title) && (m.Playing || isl.Clock.Now - s.Media.LastPlayingAt < 60)) return "media";
        return null;
    }

    public override (float, float, float) Size(Island isl) => kind switch
    {
        "timer" => (250, 34, 17),
        _ => (318, 34, 17),
    };
    public override bool WantsContinuousFrames(Island isl) => false;
    public override double AmbientInterval(Island isl)
    {
        if (kind == "timer") return 0.5;
        var m = isl.Svc?.Media?.Current;
        return m != null && m.Playing && isl.Cfg.MediaWaveform ? 1 / 30.0 : 0;
    }

    public override void Draw(Island isl, Canvas c, Ux ux, Box a, float t)
    {
        double now = isl.Clock.Now;
        if (kind == "timer")
        {
            var tm = isl.Svc.Timer;
            float left = 1 - (float)tm.Fraction(now);
            var col = tm.Paused ? Col.White.A(0.55f) : Col.Hex(0xff9f0a);
            // the ring empties as the time runs down, as in the Timer tab
            c.Ring(a.X + 19, a.CY, 9, col.A(0.25f * t), 2.6f);
            c.Arc(a.X + 19, a.CY, 9, 0, MathF.PI * 2 * Math.Max(0.001f, left), col.A(t), 2.6f);
            c.Text(tm.Paused ? "Paused" : "Timer", new Box(a.X + 36, a.Y, 80, a.H), 12.5f, Col.White.A(0.75f * t), FontWeight.SemiBold);
            c.Text(Paint.Time(tm.Remaining(now)), new Box(a.Right - 92, a.Y, 78, a.H), 14, col.A(t), FontWeight.SemiBold, HAlign.Right);
            return;
        }
        var m = isl.Svc.Media.Current;
        if (m == null) return;
        // a track change gives the artwork a little hop
        float bump = isl.Ux.Anim("trackbump", 0, SpringSpec.Wobbly);
        var art = Box.Centered(a.X + 19, a.CY, 22, 22).Scale(1 + 0.25f * bump);
        Paint.Art(c, m, art, 6, t);
        var accent = m.Palette is { Length: > 0 } ? Col.Legible(m.Palette[0]) : Col.Hex(0x7aa2ff);
        c.Text(m.Title, new Box(a.X + 38, a.Y, a.W - 38 - 48, a.H), 12.5f, Col.White.A(0.78f * t), FontWeight.Medium);
        Paint.Waveform(c, new Box(a.Right - 38, a.CY - 8, 22, 16), isl.Svc.Media.Level, m.Playing, now, accent.A(t));
    }
}

// ============================================================================
// Peek: hovering. Media with controls, or a glance at the time and weather.
// ============================================================================

public sealed class PeekPage : Page
{
    public override string Key => "peek:" + (HasMedia(null) ? "media" : "glance");
    Island last;
    bool HasMedia(Island isl)
    {
        isl ??= last;
        var m = isl?.Svc?.Media?.Current;
        return isl != null && isl.Cfg.Media && m != null && !string.IsNullOrEmpty(m.Title);
    }
    public override (float, float, float) Size(Island isl) { last = isl; return HasMedia(isl) ? (440, 186, 40) : (380, 96, 34); }
    public override double AmbientInterval(Island isl) => HasMedia(isl) ? (isl.Svc.Media.Current.Playing ? 1 / 30.0 : 0.5) : 1.0;

    public override void Draw(Island isl, Canvas c, Ux ux, Box a, float t)
    {
        last = isl;
        if (HasMedia(isl)) MediaCard(isl, c, ux, a, t, false);
        else Glance(isl, c, a, t);
    }

    static void Glance(Island isl, Canvas c, Box a, float t)
    {
        var w = isl.Clock.Wall;
        string date = w.ToString("dddd, MMMM d");
        c.Text(Paint.Clock(w, isl.Cfg.Use24Hour), new Box(a.X + 24, a.Y + 14, 150, 44), 34, Col.White.A(t), FontWeight.SemiBold, HAlign.Left, VAlign.Top);
        c.Text(date, new Box(a.X + 26, a.Y + 56, 200, 20), 12.5f, Col.White.A(0.6f * t), FontWeight.Medium);
        // The weather mirrors the clock: the temperature on the clock's line at
        // the same inset from the right, the condition on the date's line flush
        // with it, and the icon just before the temperature. It used to float at
        // its own sizes and heights, with the icon well away to the left.
        var wx = isl.Svc?.Weather?.Now;
        float weatherLeft = a.Right;
        if (wx != null)
        {
            string temp = $"{wx.Temp:0}°";
            float right = a.Right - 24;
            float tw = c.Measure(temp, 30, FontWeight.SemiBold).w;
            c.Text(temp, new Box(right - tw - 4, a.Y + 16, tw + 4, 40), 30, Col.White.A(t), FontWeight.SemiBold, HAlign.Right, VAlign.Top);
            float iconX = right - tw - 18;
            Paint.Weather(c, wx.Icon, iconX, a.Y + 36, 24, t);
            string cond = wx.Condition ?? "";
            float cw = Math.Min(150, c.Measure(cond, 12.5f, FontWeight.Medium).w);
            c.Text(cond, new Box(right - 150, a.Y + 56, 150, 20), 12.5f, Col.White.A(0.6f * t), FontWeight.Medium, HAlign.Right);
            weatherLeft = Math.Min(iconX - 13, right - cw);
        }
        // A desktop has no battery: nothing is drawn for it.
        var bat = isl.Svc?.Battery;
        if (bat != null && bat.Present && bat.Percent >= 0)
        {
            // A ring, as the iPhone's battery widget, centered in the gap between
            // the date and the weather. It was a bare "93%" tucked after the date,
            // green on the charger whether it charged or not.
            string cap = BatteryCaption(bat.Charging, bat.PluggedIn, bat.SecondsLeft);
            float capW = c.Measure(cap, 10.5f, FontWeight.Medium).w;
            float left = a.X + 26 + c.Measure(date, 12.5f, FontWeight.Medium).w + 10;
            float right = wx != null ? weatherLeft - 10 : a.Right - 20;
            float half = Math.Max(16, capW / 2);
            float cx = Math.Clamp((left + right) / 2, left + half, Math.Max(left + half, right - half));
            var green = Col.Hex(0x34c759);
            Paint.BatteryRing(c, cx, a.Y + 38, 15, bat.Percent, bat.Charging, t, bat.PluggedIn, Col.White.A(0.85f));
            c.Text(cap, Box.Centered(cx, a.Y + 66, capW + 8, 16), 10.5f, (bat.Charging ? green : Col.White).A((bat.Charging ? 0.85f : 0.5f) * t), FontWeight.Medium, HAlign.Center);
        }
    }

    internal static string BatteryCaption(bool charging, bool plugged, int secondsLeft)
    {
        if (charging) return "Charging";
        if (plugged) return "Plugged in";
        if (secondsLeft <= 0) return "On battery";
        var ts = TimeSpan.FromSeconds(secondsLeft);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} h {ts.Minutes} min left" : $"{Math.Max(1, ts.Minutes)} min left";
    }

    /// <summary>The media card, shared by Peek and the dashboard's Now Playing tab.</summary>
    public static void MediaCard(Island isl, Canvas c, Ux ux, Box a, float t, bool large)
    {
        var media = isl.Svc.Media;
        var m = media.Current;
        double now = isl.Clock.Now;
        var accent = m.Palette is { Length: > 0 } ? Col.Legible(m.Palette[0]) : Col.Hex(0x7aa2ff);
        float pad = 24;   // the same on every side
        float artS = large ? 92 : 64;
        float bump = isl.Ux.Anim("trackbump", 0, SpringSpec.Wobbly);
        var art = new Box(a.X + pad, a.Y + pad, artS, artS);
        // the artwork casts its own colored glow, like Apple Music's backdrop
        if (m.Palette is { Length: > 0 })
        {
            // the artwork's glow breathes with the music
            float lv = m.Playing ? Math.Clamp(media.Level * 1.5f, 0, 1) : 0;
            float grow = isl.Ux.Anim("art:breath", lv, SpringSpec.Snappy) * 9;
            c.Blurred(14 + grow, () => c.Fill(art.Inset(4 - grow).Offset(0, 5), accent.A(0.45f + 0.35f * lv), 14), t * 0.9f);
        }
        Paint.Art(c, m, art.Scale(1 + 0.12f * bump), large ? 16 : 13, t);
        float tx = art.Right + 16, tw = a.Right - pad - tx - 34;   // leave the waveform its corner
        c.Text(m.Title, new Box(tx, art.Y + (large ? 6 : 3), tw, 22), large ? 17 : 15, Col.White.A(t), FontWeight.SemiBold, HAlign.Left, VAlign.Top);
        c.Text(string.IsNullOrEmpty(m.Artist) ? m.AppName : m.Artist, new Box(tx, art.Y + (large ? 30 : 24), tw, 20), large ? 13.5f : 12.5f, Col.White.A(0.6f * t), FontWeight.Medium, HAlign.Left, VAlign.Top);
        if (large) c.Text(m.AppName + (string.IsNullOrEmpty(m.Album) ? "" : "  ·  " + m.Album), new Box(tx, art.Y + 52, tw, 18), 11.5f, Col.White.A(0.38f * t), FontWeight.Medium, HAlign.Left, VAlign.Top);
        {
            // only a picture of the sound: it used to light up on hover like a
            // button, and read as a "more" menu
            var wb = new Box(a.Right - pad - 30, art.Y - 2, 38, 30);
            Paint.Waveform(c, new Box(wb.CX - 9, wb.CY - 7, 18, 14), media.Level, m.Playing, now, accent.A(t), 4);
        }

        // scrubber: drag to seek; shows the drag position while held
        float by = art.Bottom + (large ? 22 : 16);
        var track = new Box(a.X + pad + 46, by - 6, a.W - 2 * pad - 92, 12);
        double pos = m.LivePosition(now);
        float frac = m.Duration > 0 ? (float)(pos / m.Duration) : 0;
        if (scrubbing != null && ux.Dragging == "seek") frac = scrubbing.Value;
        if (m.Duration > 0 && m.CanSeek)
            ux.Slider("seek", track, frac, (v, fin) => { scrubbing = v; if (fin) { media.Seek(v * m.Duration); scrubbing = null; } }, Col.White.A(0.9f * t), 4.5f);
        else ux.Slider("seek-ro", track, frac, (v, f) => { }, Col.White.A(0.55f * t), 4.5f);
        c.Text(Paint.Time(scrubbing.HasValue && ux.Dragging == "seek" ? scrubbing.Value * m.Duration : pos), new Box(a.X + pad, by - 9, 44, 18), 11, Col.White.A(0.55f * t), FontWeight.Medium, HAlign.Left);
        c.Text(m.Duration > 0 ? "-" + Paint.Time(m.Duration - pos) : "LIVE", new Box(a.Right - pad - 44, by - 9, 44, 18), 11, Col.White.A(0.55f * t), FontWeight.Medium, HAlign.Right);

        // transport
        float btn = large ? 50 : 44;
        // transport: centered between the seekbar and the bottom, with the same
        // margin below the buttons as the card has on its sides
        float cy = large ? (by + 8 + a.Bottom) / 2 : a.Bottom - pad - btn / 2;
        float cx = a.CX;
        float gap = large ? 62 : 56;
        if (large && m.Shuffle.HasValue) ux.IconButton("shuffle", cx - gap * 2.1f, cy, 30, Glyph.Shuffle, media.ToggleShuffle, null, 13, true, m.Shuffle == true ? accent : null);
        ux.IconButton("prev", cx - gap, cy, large ? 40 : 36, Glyph.Prev, media.Previous, null, large ? 18 : 16, m.CanPrev);
        ux.IconButton("play", cx, cy, large ? 50 : 44, m.Playing ? Glyph.Pause : Glyph.Play, () => { media.PlayPause(); isl.Ux.Kick("press:play", 0); }, accent, large ? 22 : 20);
        ux.IconButton("next", cx + gap, cy, large ? 40 : 36, Glyph.Next, media.Next, null, large ? 18 : 16, m.CanNext);
        if (large && m.Repeat >= 0) ux.IconButton("repeat", cx + gap * 2.1f, cy, 30, m.Repeat == 1 ? '' /*repeat one*/ : Glyph.Repeat, media.CycleRepeat, null, 13, true, m.Repeat > 0 ? accent : null);
    }
    static float? scrubbing;
}

// ============================================================================
// Alerts
// ============================================================================

public sealed class AlertPage : Page
{
    public override string Key => "alert:" + (cur?.Key ?? "") + ":" + (cur?.Created ?? 0);
    Alert cur;
    public override (float, float, float) Size(Island isl)
    {
        cur = isl.CurrentAlert;
        if (cur == null) return (300, 40, 20);
        if (cur.Kind == "call") return (440, 80, 40);
        if (cur.Batteries.Count > 0) return (400, 70, 35);
        if (!string.IsNullOrEmpty(cur.Detail)) return (440, 70 + 16 * DetailLines(cur.Detail), 32);
        return (Math.Clamp(140 + Math.Max(TextW(cur.Title, 14), TextW(cur.Subtitle, 12)), 290, 440), 62, 31);
    }
    static float TextW(string s, float px) => string.IsNullOrEmpty(s) ? 0 : s.Length * px * 0.55f;

    // The message wraps to up to three lines (it was one, cut off). The popup
    // is sized before it is drawn, so the line count is estimated from the
    // text's length at the width it gets; the text itself wraps exactly.
    internal const float DetailWidth = 340;
    internal static int DetailLines(string s) => string.IsNullOrEmpty(s) ? 0 : Math.Clamp((int)Math.Ceiling(TextW(s, 12) / DetailWidth), 1, 3);

    public override void Draw(Island isl, Canvas c, Ux ux, Box a, float t)
    {
        var al = isl.CurrentAlert ?? cur;
        if (al == null) return;
        float bump = isl.Ux.Anim("alertbump", 0, SpringSpec.Wobbly);
        float iconS = al.Kind == "call" ? 48 : 40;
        var ib = new Box(a.X + 13, a.CY - iconS / 2, iconS, iconS).Scale(1 + 0.15f * bump);
        // icon: image if we have one, else a tinted glyph tile
        var img = Paint.Image(c, "alert:" + al.Key + al.Created, al.Image);
        if (img != null) c.Image(img, ib, t, al.Kind == "call" ? ib.H / 2 : 10);
        else
        {
            var la = Col.Legible(al.Accent, 0.22f);
            using var br = c.Linear(new Vector2(ib.X, ib.Y), new Vector2(ib.Right, ib.Bottom), (0, la.A(t)), (1, la.A(0.7f * t)));
            c.Fill(ib, br, al.Kind == "call" ? ib.H / 2 : 11);
            c.Icon(al.Glyph == '\0' ? Glyph.Bell : al.Glyph, ib.CX, ib.CY, iconS * 0.45f, Col.White.A(t));
        }
        float tx = ib.Right + 12;
        float right = a.Right - 14;
        // accessory: battery rings (AirPods), or action buttons (calls)
        if (al.Batteries.Count > 0)
        {
            float rx = right - 16;
            for (int i = al.Batteries.Count - 1; i >= 0; i--)
            {
                var (label, pct, chg) = al.Batteries[i];
                Paint.BatteryRing(c, rx, a.CY - 5, 12.5f, pct, chg, t);
                c.Text(label, Box.Centered(rx, a.CY + 18, 40, 12), 9.5f, Col.White.A(0.55f * t), FontWeight.SemiBold, HAlign.Center);
                rx -= 40;
            }
            right = rx + 16;
        }
        if (al.Actions.Count > 0)
        {
            float bx = right - 22;
            for (int i = al.Actions.Count - 1; i >= 0; i--)
            {
                var act = al.Actions[i];
                ux.IconButton("alert:act" + i, bx, a.CY, 44, act.Glyph, () => { Ui.Guard("alert action", act.Run); isl.EndTransient("action"); }, act.Tint, 18, true);
                bx -= 54;
            }
            right = bx + 22;
        }
        int dl = al.Kind == "call" ? 0 : DetailLines(al.Detail);
        float textH = 37 + (dl > 0 ? 16 * dl + 2 : 0);
        float ty = a.CY - textH / 2;
        // Text runs to 16 DIP from the edge when nothing sits to its right, about
        // the icon's inset on the left; 22 plus the ellipsis' slack looked lopsided.
        float tw = right - tx - (right >= a.Right - 14.5f ? 2 : 8);
        c.Text(al.Title, new Box(tx, ty, tw, 20), 14, Col.White.A(t), FontWeight.SemiBold);
        c.Text(al.Subtitle, new Box(tx, ty + 19, tw, 18), 12, (al.Kind == "call" ? Col.Hex(0x34c759) : Col.White).A(0.62f * t), FontWeight.Medium);
        if (dl > 0) c.Text(al.Detail, new Box(tx, ty + 37, tw, 16 * dl + 2), 12, Col.White.A(0.8f * t), FontWeight.Normal, HAlign.Left, VAlign.Top, true);
    }
}

// ============================================================================
// HUD (volume, brightness, caps lock, keyboard layout)
// ============================================================================

public sealed class HudPage : Page
{
    public override string Key => "hud:" + kind;
    HudKind kind;
    public override (float, float, float) Size(Island isl)
    {
        kind = isl.CurrentHud.kind;
        return kind is HudKind.CapsLock or HudKind.Layout ? (250, 34, 17) : (300, 34, 17);
    }
    public override void Draw(Island isl, Canvas c, Ux ux, Box a, float t)
    {
        var (k, v, label) = isl.CurrentHud;
        var accent = Col.White;
        if (k is HudKind.CapsLock)
        {
            bool on = v > 0.5f;
            c.Icon(Glyph.CapsLock, a.X + 20, a.CY, 15, (on ? Col.Hex(0x34c759) : Col.White).A(t));
            c.Text(on ? "Caps Lock On" : "Caps Lock Off", new Box(a.X + 36, a.Y, a.W - 50, a.H), 13, Col.White.A(0.9f * t), FontWeight.SemiBold, HAlign.Left);
            c.Circle(a.Right - 18, a.CY, 4, (on ? Col.Hex(0x34c759) : Col.Gray(0.35f)).A(t));
            return;
        }
        if (k is HudKind.Layout)
        {
            c.Icon(Glyph.Keyboard, a.X + 20, a.CY, 15, Col.White.A(t));
            c.Text(label ?? "", new Box(a.X + 36, a.Y, a.W - 50, a.H), 13, Col.White.A(0.9f * t), FontWeight.SemiBold);
            return;
        }
        char g = k switch
        {
            HudKind.Brightness => Glyph.Brightness,
            HudKind.Mute => Glyph.Mute,
            _ => v <= 0.001f ? Glyph.Mute : v < 0.34f ? Glyph.Volume1 : v < 0.67f ? Glyph.Volume2 : Glyph.Volume3,
        };
        float shown = isl.Ux.Anim("hud:val", k == HudKind.Mute ? 0 : v, SpringSpec.Snappy);
        c.Icon(g, a.X + 20, a.CY, 14, Col.White.A(t));
        var track = new Box(a.X + 38, a.CY - 3, a.W - 38 - 50, 6);
        c.Fill(track, Col.White.A(0.18f * t), 3);
        c.Fill(new Box(track.X, track.Y, Math.Max(6, track.W * shown), 6), (k == HudKind.Mute ? Col.Gray(0.5f) : accent).A(t), 3);
        c.Text(k == HudKind.Mute ? "Muted" : $"{Math.Round(v * 100)}", new Box(a.Right - 46, a.Y, 34, a.H), 12, Col.White.A(0.8f * t), FontWeight.SemiBold, HAlign.Right);
    }
}
