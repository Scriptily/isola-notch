using System.Text;
using System.Numerics;
using Isola.App;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;
using FontStyle = Vortice.DirectWrite.FontStyle;
using FontWeight = Vortice.DirectWrite.FontWeight;

namespace Isola.Render;

public enum HAlign { Left, Center, Right }
public enum VAlign { Top, Middle, Bottom }

/// <summary>A rectangle in DIPs with the small amount of geometry the views need.</summary>
public readonly record struct Box(float X, float Y, float W, float H)
{
    public float Right => X + W; public float Bottom => Y + H;
    public float CX => X + W / 2; public float CY => Y + H / 2;
    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;
    public Box Inset(float d) => new(X + d, Y + d, W - 2 * d, H - 2 * d);
    public Box Inset(float dx, float dy) => new(X + dx, Y + dy, W - 2 * dx, H - 2 * dy);
    public Box Offset(float dx, float dy) => new(X + dx, Y + dy, W, H);
    public Box Scale(float s) => new(CX - W * s / 2, CY - H * s / 2, W * s, H * s);
    public RawRectF Raw => new(X, Y, X + W, Y + H);
    public static Box Centered(float cx, float cy, float w, float h) => new(cx - w / 2, cy - h / 2, w, h);
    public static Box Lerp(Box a, Box b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.W + (b.W - a.W) * t, a.H + (b.H - a.H) * t);
}

public static class Col
{
    public static Color4 Hex(uint rgb, float a = 1) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
    public static Color4 A(this Color4 c, float a) => new(c.R, c.G, c.B, c.A * a);
    public static Color4 Mix(Color4 a, Color4 b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
    public static readonly Color4 White = new(1, 1, 1, 1), Black = new(0, 0, 0, 1), Clear = new(0, 0, 0, 0);
    public static Color4 Gray(float v, float a = 1) => new(v, v, v, a);
    /// <summary>Lift dark colors so a glow taken from dark album art still reads on black.</summary>
    public static Color4 Vivid(Color4 c, float minLum = 0.45f, float sat = 1.25f)
    {
        float l = 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
        float r = l + (c.R - l) * sat, g = l + (c.G - l) * sat, b = l + (c.B - l) * sat;
        float mx = MathF.Max(r, MathF.Max(g, b));
        if (mx < minLum && mx > 0.001f) { float k = minLum / mx; r *= k; g *= k; b *= k; }
        else if (mx <= 0.001f) { r = g = b = minLum; }
        return new(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1), c.A);
    }

    /// <summary>
    /// A color made legible on the black island: its perceived (relative)
    /// luminance is raised to at least <paramref name="minLuma"/> by mixing
    /// toward white, keeping the hue. A navy from album art becomes a clear
    /// periwinkle instead of disappearing into the black.
    /// </summary>
    public static Color4 Legible(Color4 c, float minLuma = 0.32f)
    {
        c = Vivid(c, 0.5f, 1.1f);
        static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        float L = 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
        if (L >= minLuma) return c;
        for (int i = 1; i <= 20; i++)
        {
            var m = Mix(c, new Color4(1, 1, 1, c.A), i / 20f);
            if (0.2126f * Lin(m.R) + 0.7152f * Lin(m.G) + 0.0722f * Lin(m.B) >= minLuma) return m;
        }
        return new Color4(1, 1, 1, c.A);
    }
}

/// <summary>
/// Thin immediate-mode layer over a Direct2D device context, working in DIPs.
/// Text formats and layouts are cached (creating a DirectWrite layout is the
/// one expensive thing views would otherwise do every frame), and one solid
/// brush is recolored per use instead of allocating brushes.
/// </summary>
public sealed class Canvas
{
    public ID2D1DeviceContext C { get; private set; }
    public int Frame { get; private set; }
    /// <summary>
    /// The frame a spinner or skeleton was last drawn on. Windows here draw
    /// only when something moves, so while a loader is on screen its window
    /// keeps animating (see Island.AmbientInterval, SettingsWindow.NextWake).
    /// </summary>
    public int LoaderFrame { get; set; } = -1;
    public bool LoaderDrawn => LoaderFrame == Frame;
    ID2D1SolidColorBrush brush;
    int brushGen;
    readonly Dictionary<string, IDWriteTextFormat> formats = new();
    readonly Dictionary<string, (IDWriteTextLayout layout, int used)> layouts = new();
    IDWriteInlineObject ellipsis; IDWriteTextFormat ellipsisFmt;
    // Effects are evaluated when Direct2D flushes, not when DrawImage is called,
    // so an instance cannot be re-pointed at new input within a frame: each use
    // takes its own from a per-frame pool (reset in Begin).
    readonly List<GaussianBlur>[] blurs = Enumerable.Range(0, 5).Select(_ => new List<GaussianBlur>()).ToArray();
    readonly List<Shadow>[] shadows = Enumerable.Range(0, 5).Select(_ => new List<Shadow>()).ToArray();
    readonly int[] blurUsed = new int[5], shadowUsed = new int[5]; int effectGen;   // per nesting depth = per device context
    readonly Stack<ID2D1Layer> layerPool = new();
    readonly Stack<(ID2D1Layer layer, bool pooled)> pushed = new();

    public const string TextFont = "Segoe UI Variable Text";
    public const string DisplayFont = "Segoe UI Variable Display";
    public const string SmallFont = "Segoe UI Variable Small";
    public const string IconFont = "Segoe Fluent Icons";

    public void Begin(ID2D1DeviceContext ctx)
    {
        Frame++;
        if (C != ctx || brushGen != Gfx.Generation)
        {
            brush?.Dispose(); DropEffects();
            foreach (var l in layerPool) l.Dispose(); layerPool.Clear();
            C = ctx; brushGen = Gfx.Generation;
            brush = C.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        }
        C.Transform = Matrix3x2.Identity;
        Array.Clear(blurUsed); Array.Clear(shadowUsed);
        if ((Frame & 255) == 0) TrimLayouts();
    }

    public ID2D1SolidColorBrush Brush(Color4 c) { brush.Color = c; return brush; }

    // ---- shapes ------------------------------------------------------------
    public void Fill(Box b, Color4 c, float radius = 0)
    {
        if (c.A <= 0.001f || b.W <= 0 || b.H <= 0) return;
        if (radius <= 0) C.FillRectangle(b.Raw, Brush(c));
        else C.FillRoundedRectangle(new RoundedRectangle(b.Raw, radius, radius), Brush(c));
    }
    public void Fill(Box b, ID2D1Brush br, float radius = 0)
    {
        if (radius <= 0) C.FillRectangle(b.Raw, br);
        else C.FillRoundedRectangle(new RoundedRectangle(b.Raw, radius, radius), br);
    }
    public void Stroke(Box b, Color4 c, float radius, float width = 1)
    {
        if (c.A <= 0.001f) return;
        C.DrawRoundedRectangle(new RoundedRectangle(b.Raw, radius, radius), Brush(c), width);
    }
    public void Circle(float cx, float cy, float r, Color4 c)
    {
        if (c.A <= 0.001f || r <= 0) return;
        C.FillEllipse(new Ellipse(new Vector2(cx, cy), r, r), Brush(c));
    }
    public void Ellipse(float cx, float cy, float rx, float ry, Color4 c)
    {
        if (c.A <= 0.001f || rx <= 0 || ry <= 0) return;
        C.FillEllipse(new Ellipse(new Vector2(cx, cy), rx, ry), Brush(c));
    }
    public void Ring(float cx, float cy, float r, Color4 c, float width) =>
        C.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), Brush(c), width);
    public void Line(float x0, float y0, float x1, float y1, Color4 c, float width = 1, ID2D1StrokeStyle style = null) =>
        C.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), Brush(c), width, style);

    /// <summary>
    /// A check mark that draws itself: <paramref name="p"/> 0 is nothing, 1 the
    /// whole mark, traced from its short stroke into the long one (the same
    /// path as the hub's, a 24-unit box). Round caps, so the tip reads as a pen.
    /// </summary>
    public void DrawnCheck(float cx, float cy, float size, float p, Color4 c, float width)
    {
        p = Math.Clamp(p, 0, 1);
        if (p <= 0.001f || c.A <= 0.003f) return;
        float k = size / 24f;
        var a = new Vector2(cx + (6 - 12) * k, cy + (12.5f - 12) * k);
        var b = new Vector2(cx + (10 - 12) * k, cy + (16.5f - 12) * k);
        var e = new Vector2(cx + (18 - 12) * k, cy + (8 - 12) * k);
        float l1 = Vector2.Distance(a, b), l2 = Vector2.Distance(b, e), d = p * (l1 + l2);
        if (d <= l1) { Line(a.X, a.Y, a.X + (b.X - a.X) * d / l1, a.Y + (b.Y - a.Y) * d / l1, c, width, RoundCap); return; }
        Line(a.X, a.Y, b.X, b.Y, c, width, RoundCap);
        float t = (d - l1) / l2;
        Line(b.X, b.Y, b.X + (e.X - b.X) * t, b.Y + (e.Y - b.Y) * t, c, width, RoundCap);
    }

    /// <summary>Arc for progress rings, from angle a0 to a1 in radians (0 = up, clockwise).</summary>
    public void Arc(float cx, float cy, float r, float a0, float a1, Color4 c, float width)
    {
        if (Math.Abs(a1 - a0) < 0.001f) return;
        if (Math.Abs(a1 - a0) >= MathF.PI * 2 - 0.001f) { Ring(cx, cy, r, c, width); return; }
        using var g = Gfx.D2DFactory.CreatePathGeometry();
        using (var s = g.Open())
        {
            var p0 = new Vector2(cx + r * MathF.Sin(a0), cy - r * MathF.Cos(a0));
            var p1 = new Vector2(cx + r * MathF.Sin(a1), cy - r * MathF.Cos(a1));
            s.BeginFigure(p0, FigureBegin.Hollow);
            s.AddArc(new ArcSegment(p1, new Size(r, r), 0, a1 > a0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise,
                Math.Abs(a1 - a0) > MathF.PI ? ArcSize.Large : ArcSize.Small));
            s.EndFigure(FigureEnd.Open); s.Close();
        }
        C.DrawGeometry(g, Brush(c), width, RoundCap);
    }

    ID2D1StrokeStyle roundCap;
    public ID2D1StrokeStyle RoundCap => roundCap ??= Gfx.D2DFactory.CreateStrokeStyle(new StrokeStyleProperties
    { StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round });

    public ID2D1LinearGradientBrush Linear(Vector2 a, Vector2 b, params (float pos, Color4 c)[] stops) =>
        C.CreateLinearGradientBrush(new LinearGradientBrushProperties(a, b), Stops(stops));
    public ID2D1RadialGradientBrush Radial(Vector2 center, float rx, float ry, params (float pos, Color4 c)[] stops) =>
        C.CreateRadialGradientBrush(new RadialGradientBrushProperties(center, Vector2.Zero, rx, ry), Stops(stops));

    // A gradient stop collection is a small GPU texture, and the glow used to
    // make five of them per frame, fifteen frames a second, all with the same
    // colors (they hold still at rest; only the positions drift). Collections
    // are kept by their stops for the device's lifetime, capped so animated
    // alphas can't grow it without bound. Brushes stay per use: they're cheap.
    readonly Dictionary<string, ID2D1GradientStopCollection> stopCache = new();
    int stopGen = -1;
    readonly StringBuilder stopKey = new();

    ID2D1GradientStopCollection Stops((float pos, Color4 c)[] stops)
    {
        if (stopGen != Gfx.Generation || stopCache.Count >= 256) { foreach (var v in stopCache.Values) v.Dispose(); stopCache.Clear(); stopGen = Gfx.Generation; }
        stopKey.Clear();
        foreach (var (pos, c) in stops)
            stopKey.Append((int)(pos * 4096)).Append(',').Append((int)(c.R * 1024)).Append(',').Append((int)(c.G * 1024)).Append(',').Append((int)(c.B * 1024)).Append(',').Append((int)(c.A * 1024)).Append(';');
        string key = stopKey.ToString();
        if (stopCache.TryGetValue(key, out var col)) return col;
        var gs = new GradientStop[stops.Length];
        for (int i = 0; i < stops.Length; i++) gs[i] = new GradientStop { Position = stops[i].pos, Color = stops[i].c };
        col = C.CreateGradientStopCollection(gs, Gamma.StandardRgb, ExtendMode.Clamp);
        stopCache[key] = col;
        return col;
    }

    // ---- layers ------------------------------------------------------------
    ID2D1Layer RentLayer() => layerPool.Count > 0 ? layerPool.Pop() : C.CreateLayer(null);

    /// <summary>Clip to a geometry and/or fade, until <see cref="Pop"/>.</summary>
    public void Push(ID2D1Geometry clip = null, float opacity = 1, ID2D1Brush opacityBrush = null)
    {
        var l = RentLayer();
        var p = new LayerParameters1
        {
            ContentBounds = new RawRectF(-1e6f, -1e6f, 1e6f, 1e6f), GeometricMask = clip, MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity, Opacity = opacity, OpacityBrush = opacityBrush, LayerOptions = LayerOptions1.None,
        };
        C.PushLayer(p, l);
        pushed.Push((l, true));
    }
    public void Pop()
    {
        C.PopLayer();
        var (l, pooled) = pushed.Pop();
        if (pooled) layerPool.Push(l);
    }
    public void ClipRect(Box b) => C.PushAxisAlignedClip(b.Raw, AntialiasMode.PerPrimitive);
    public void Unclip() => C.PopAxisAlignedClip();

    readonly Stack<ID2D1LinearGradientBrush> fades = new();

    /// <summary>
    /// Clips to a scrolling view, fading the edges that have more to scroll
    /// toward, until <see cref="Unfade"/>. A hard clip sliced rows and tiles
    /// off mid-line, which read as broken rather than as "there's more".
    /// </summary>
    public void FadeClip(Box view, bool top, bool bottom, float edge = 18)
    {
        C.PushAxisAlignedClip(view.Raw, AntialiasMode.PerPrimitive);
        if ((!top && !bottom) || view.H < 4 * edge) { fades.Push(null); return; }
        float t = edge / view.H;
        var brush = Linear(new Vector2(view.X, view.Y), new Vector2(view.X, view.Bottom),
            (0, Col.Black.A(top ? 0 : 1)), (t, Col.Black), (1 - t, Col.Black), (1, Col.Black.A(bottom ? 0 : 1)));
        Push(null, 1, brush);
        fades.Push(brush);   // the layer reads the brush until it's popped
    }

    public void Unfade()
    {
        var brush = fades.Pop();
        if (brush != null) { Pop(); brush.Dispose(); }
        C.PopAxisAlignedClip();
    }

    public Matrix3x2 Transform { get => C.Transform; set => C.Transform = value; }

    /// <summary>Scale about a point, composed with the current transform.</summary>
    public Matrix3x2 ScaleAbout(float s, float cx, float cy)
    {
        var old = C.Transform;
        C.Transform = Matrix3x2.CreateScale(s, s, new Vector2(cx, cy)) * old;
        return old;
    }

    // ---- effects -----------------------------------------------------------
    void DropEffects()
    {
        foreach (var l in blurs) { foreach (var b in l) b.Dispose(); l.Clear(); }
        foreach (var l in shadows) { foreach (var b in l) b.Dispose(); l.Clear(); }
        effectGen = Gfx.Generation;
    }
    GaussianBlur NextBlur()
    {
        if (effectGen != Gfx.Generation) DropEffects();
        var pool = blurs[recDepth];
        if (blurUsed[recDepth] == pool.Count) pool.Add(new GaussianBlur(C) { BorderMode = BorderMode.Soft, Optimization = GaussianBlurOptimization.Balanced });
        return pool[blurUsed[recDepth]++];
    }
    Shadow NextShadow()
    {
        if (effectGen != Gfx.Generation) DropEffects();
        var pool = shadows[recDepth];
        if (shadowUsed[recDepth] == pool.Count) pool.Add(new Shadow(C) { Optimization = ShadowOptimization.Balanced });
        return pool[shadowUsed[recDepth]++];
    }

    /// <summary>
    /// Draw <paramref name="draw"/> through a Gaussian blur. Used for the
    /// content cross-fade (Apple blurs content in and out as the island morphs)
    /// and for glows. Records into a command list so it costs nothing extra
    /// when sigma is ~0.
    /// </summary>
    public void Blurred(float sigma, Action draw, float opacity = 1)
    {
        if (opacity <= 0.003f) return;
        if (sigma < 0.35f)
        {
            if (opacity < 0.999f) { Push(null, opacity); draw(); Pop(); } else draw();
            return;
        }
        using var list = Record(draw);
        if (list == null) return;
        var blur = NextBlur();
        blur.SetInput(0, list, true);
        blur.StandardDeviation = sigma;
        var oldT = C.Transform;
        C.Transform = Matrix3x2.Identity;   // the command list already holds world-space content
        if (opacity < 0.999f) Push(null, opacity);
        C.DrawImage(blur, InterpolationMode.Linear, CompositeMode.SourceOver);
        if (opacity < 0.999f) Pop();
        C.Transform = oldT;
    }

    /// <summary>Soft drop shadow of whatever <paramref name="draw"/> paints.</summary>
    public void DropShadow(float sigma, Color4 color, float dy, Action draw)
    {
        using var list = Record(draw);
        if (list == null) return;
        var shadow = NextShadow();
        shadow.SetInput(0, list, true);
        shadow.BlurStandardDeviation = sigma;
        shadow.Color = new Vector4(color.R, color.G, color.B, color.A);
        var oldT = C.Transform;
        C.Transform = Matrix3x2.CreateTranslation(0, dy);
        C.DrawImage(shadow, InterpolationMode.Linear, CompositeMode.SourceOver);
        C.Transform = oldT;
    }

    readonly List<ID2D1DeviceContext> recs = new(); int recGen, recDepth;

    /// <summary>
    /// Record drawing into a command list on a second device context. Direct2D
    /// refuses a target change while clips or layers are pushed on the main
    /// context (EndDraw fails with 0x8899001E), and blurred content is always
    /// drawn inside the island's clip - so it is recorded on its own context,
    /// with the current transform and DPI, and played back as an image.
    /// </summary>
    ID2D1CommandList Record(Action draw)
    {
        if (recGen != Gfx.Generation) { foreach (var r0 in recs) r0.Dispose(); recs.Clear(); recGen = Gfx.Generation; }
        // one recording context per nesting level: a blur drawn inside a blur
        // (artwork glow inside the page cross-fade) needs its own target too
        if (recDepth >= 4) { draw(); return null; }
        while (recs.Count <= recDepth)
        {
            var nc = Gfx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
            nc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            recs.Add(nc);
        }
        var rec = recs[recDepth++];
        var list = C.CreateCommandList();
        var main = C;
        var savedPushed = new Stack<(ID2D1Layer, bool)>(pushed.Reverse());
        pushed.Clear();
        rec.Target = list;
        rec.Dpi = main.Dpi;
        rec.UnitMode = main.UnitMode;
        rec.BeginDraw();
        rec.Transform = main.Transform;
        C = rec;
        try { draw(); }
        finally
        {
            while (pushed.Count > 0) Pop();     // a draw that threw mid-layer must not leak it
            C = main;
            recDepth--;
            foreach (var p in savedPushed.Reverse()) pushed.Push(p);
            var r = rec.EndDraw();
            if (r.Failure) Log.D("canvas", $"record EndDraw 0x{r.Code:X8}");
            rec.Target = null;
            list.Close();
        }
        return list;
    }

    /// <summary>
    /// Render <paramref name="draw"/> once into a bitmap the size of the target
    /// (on a side context, so it works mid-frame). Used to cache the island's
    /// static chrome - glow blurs are the expensive part of a frame, and at rest
    /// they do not change.
    /// </summary>
    public ID2D1Bitmap1 Snapshot(ID2D1Bitmap1 reuse, Action draw)
    {
        var px = C.PixelSize;
        var dpi = C.Dpi;
        if (reuse == null || reuse.PixelSize.Width != px.Width || reuse.PixelSize.Height != px.Height || snapGen != Gfx.Generation)
        {
            reuse?.Dispose();
            reuse = C.CreateBitmap(new SizeI(px.Width, px.Height), new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), dpi.Width, dpi.Height, BitmapOptions.Target));
            snapGen = Gfx.Generation;
        }
        if (recGen != Gfx.Generation) { foreach (var r0 in recs) r0.Dispose(); recs.Clear(); recGen = Gfx.Generation; }
        while (recs.Count <= recDepth) { var nc = Gfx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None); nc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale; recs.Add(nc); }
        var rec = recs[recDepth++];
        var main = C;
        var savedPushed = new Stack<(ID2D1Layer, bool)>(pushed.Reverse()); pushed.Clear();
        rec.Target = reuse; rec.Dpi = dpi; rec.UnitMode = main.UnitMode;
        rec.BeginDraw(); rec.Clear(new Color4(0, 0, 0, 0)); rec.Transform = Matrix3x2.Identity;
        C = rec;
        try { draw(); }
        finally
        {
            while (pushed.Count > 0) Pop();
            C = main; recDepth--;
            foreach (var p in savedPushed.Reverse()) pushed.Push(p);
            var r = rec.EndDraw();
            if (r.Failure) Log.D("canvas", $"snapshot EndDraw 0x{r.Code:X8}");
            rec.Target = null;
        }
        return reuse;
    }
    int snapGen;

    public void DrawBitmapAtOrigin(ID2D1Bitmap1 b)
    {
        var old = C.Transform; C.Transform = Matrix3x2.Identity;
        C.DrawImage(b, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
        C.Transform = old;
    }

    // ---- text --------------------------------------------------------------
    public IDWriteTextFormat Format(float size, FontWeight weight = FontWeight.Normal, string family = null, bool italic = false,
                                    HAlign align = HAlign.Left, bool wrap = false, bool trim = true)
    {
        family ??= size >= 20 ? DisplayFont : size <= 11 ? SmallFont : TextFont;
        string key = $"{family}|{size:0.0}|{(int)weight}|{italic}|{align}|{wrap}|{trim}";
        if (formats.TryGetValue(key, out var f)) return f;
        f = Gfx.DWrite.CreateTextFormat(family, null, weight, italic ? FontStyle.Italic : FontStyle.Normal, FontStretch.Normal, size, "en-us");
        f.TextAlignment = align switch { HAlign.Center => TextAlignment.Center, HAlign.Right => TextAlignment.Trailing, _ => TextAlignment.Leading };
        f.ParagraphAlignment = ParagraphAlignment.Near;
        f.WordWrapping = wrap ? WordWrapping.Wrap : WordWrapping.NoWrap;
        if (trim)
        {
            ellipsisFmt ??= Gfx.DWrite.CreateTextFormat(TextFont, 12);
            ellipsis ??= Gfx.DWrite.CreateEllipsisTrimmingSign(ellipsisFmt);
            f.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, ellipsis);
        }
        formats[key] = f;
        return f;
    }

    public IDWriteTextLayout Layout(string text, IDWriteTextFormat fmt, float maxW, float maxH = 1000)
    {
        text ??= "";
        string key = $"{fmt.NativePointer}|{maxW:0.0}|{maxH:0}|{text}";
        if (layouts.TryGetValue(key, out var e)) { layouts[key] = (e.layout, Frame); return e.layout; }
        var l = Gfx.DWrite.CreateTextLayout(text, fmt, Math.Max(1, maxW), Math.Max(1, maxH));
        layouts[key] = (l, Frame);
        return l;
    }

    void TrimLayouts()
    {
        if (layouts.Count < 400) return;
        foreach (var k in layouts.Where(kv => Frame - kv.Value.used > 240).Select(kv => kv.Key).ToList())
        {
            layouts[k].layout.Dispose(); layouts.Remove(k);
        }
    }

    /// <summary>Draws text inside a box; returns the laid-out width.</summary>
    public float Text(string text, Box b, float size, Color4 color, FontWeight weight = FontWeight.Normal, HAlign align = HAlign.Left,
                      VAlign valign = VAlign.Middle, bool wrap = false, string family = null, bool italic = false)
    {
        if (string.IsNullOrEmpty(text) || color.A <= 0.003f || b.W <= 1) return 0;
        var fmt = Format(size, weight, family, italic, align, wrap);
        var l = Layout(text, fmt, b.W, wrap ? b.H : 1000);
        var m = l.Metrics;
        float y = valign switch { VAlign.Top => b.Y, VAlign.Bottom => b.Bottom - m.Height, _ => b.Y + (b.H - m.Height) / 2 };
        C.DrawTextLayout(new Vector2(b.X, y), l, Brush(color), DrawTextOptions.EnableColorFont | DrawTextOptions.Clip);
        return m.WidthIncludingTrailingWhitespace;
    }

    public (float w, float h) Measure(string text, float size, FontWeight weight = FontWeight.Normal, float maxW = 4000, bool wrap = false, string family = null)
    {
        if (string.IsNullOrEmpty(text)) return (0, size * 1.33f);
        var l = Layout(text, Format(size, weight, family, false, HAlign.Left, wrap), maxW);
        var m = l.Metrics;
        return (m.WidthIncludingTrailingWhitespace, m.Height);
    }

    /// <summary>
    /// Vertical center of the text's actual ink within its layout. Script fonts
    /// carry tall ascenders and descenders, so centering the line box leaves the
    /// letters visibly low; this centers what you actually see.
    /// </summary>
    public static float InkCenter(Vortice.DirectWrite.IDWriteTextLayout l)
    {
        var o = l.OverhangMetrics;
        float top = -o.Top, bottom = l.MaxHeight + o.Bottom;
        return (top + bottom) / 2;
    }

    /// <summary>A Segoe Fluent Icons glyph centered on (cx, cy).</summary>
    public void Icon(char glyph, float cx, float cy, float size, Color4 color)
    {
        if (color.A <= 0.003f) return;
        var fmt = Format(size, FontWeight.Normal, IconFont, false, HAlign.Center, false, false);
        var l = Layout(glyph.ToString(), fmt, size * 2, size * 2);
        var m = l.Metrics;
        C.DrawTextLayout(new Vector2(cx - size, cy - m.Height / 2), l, Brush(color), DrawTextOptions.None);
    }

    /// <summary>A color emoji (Segoe UI Emoji) centered on (cx, cy).</summary>
    public void Emoji(string e, float cx, float cy, float size, float alpha = 1)
    {
        if (string.IsNullOrEmpty(e) || alpha <= 0.003f) return;
        var fmt = Format(size, FontWeight.Normal, "Segoe UI Emoji", false, HAlign.Center, false, false);
        var l = Layout(e, fmt, size * 3, size * 3);
        var m = l.Metrics;
        if (alpha < 0.999f) Push(null, alpha);
        C.DrawTextLayout(new Vector2(cx - size * 1.5f, cy - m.Height / 2), l, Brush(Col.White), DrawTextOptions.EnableColorFont);
        if (alpha < 0.999f) Pop();
    }

    // ---- images ------------------------------------------------------------
    /// <summary>Decode an image (album art, icons) into a GPU bitmap. Call on the UI thread.</summary>
    public static ID2D1Bitmap1 Decode(ID2D1DeviceContext ctx, byte[] data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            return Gfx.DecodeImage(data, frame =>
            {
                using var conv = Gfx.Wic.CreateFormatConverter();
                conv.Initialize(frame, PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom).CheckError();
                return ctx.CreateBitmapFromWicBitmap(conv, null);
            });
        }
        catch (Exception ex) { Log.D("canvas", "image decode failed: " + ex.Message); return null; }
    }

    public static ID2D1Bitmap1 FromBgra(ID2D1DeviceContext ctx, byte[] bgraPremul, int w, int h)
    {
        unsafe
        {
            fixed (byte* p = bgraPremul)
                return ctx.CreateBitmap(new SizeI(w, h), (nint)p, (uint)(w * 4),
                    new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        }
    }

    public void Image(ID2D1Bitmap img, Box dst, float opacity = 1, float radius = 0)
    {
        if (img == null || opacity <= 0.003f) return;
        var sz = img.Size;
        // cover-fit: crop the source to the destination's aspect
        float sa = sz.Width / sz.Height, da = dst.W / dst.H;
        RawRectF src = sa > da
            ? new RawRectF((sz.Width - sz.Height * da) / 2, 0, (sz.Width + sz.Height * da) / 2, sz.Height)
            : new RawRectF(0, (sz.Height - sz.Width / da) / 2, sz.Width, (sz.Height + sz.Width / da) / 2);
        if (radius > 0)
        {
            using var g = Gfx.D2DFactory.CreateRoundedRectangleGeometry(new RoundedRectangle(dst.Raw, radius, radius));
            Push(g, 1);
            C.DrawBitmap(img, dst.Raw, opacity, Vortice.Direct2D1.BitmapInterpolationMode.Linear, src);
            Pop();
        }
        else C.DrawBitmap(img, dst.Raw, opacity, Vortice.Direct2D1.BitmapInterpolationMode.Linear, src);
    }
}

/// <summary>Segoe Fluent Icons code points used across the views.</summary>
public static class Glyph
{
    public const char Play = '', Pause = '', Next = '', Prev = '', Music = '', Volume = '', Mute = '',
        Volume1 = '', Volume2 = '', Volume3 = '', Bluetooth = '', Headphones = '', Display = '', Tv = '',
        Battery = '', Charging = '', Plug = '', Brightness = '', Settings = '', Search = '', Note = '',
        Folder = '', File = '', Clipboard = '', Cloud = '', Sun = '', Moon = '', Rain = '',
        Home = '', Lightbulb = '', Power = '', Timer = '', Close = '', Check = '', Add = '',
        Delete = '', Bell = '', Phone = '', PhoneEnd = '', Keyboard = '', CapsLock = '', Globe = '',
        Thermometer = '', Flame = '', Scene = '', Script = '', Automation = '',
        ToggleOn = '', Up = '', Down = '', Left = '', Right = '', OpenFile = '', Shield = '',
        Pin = '', Unpin = '', Copy = '', More = '', Link = '', App = '', Shuffle = '', Repeat = '',
        Airplay = '', Warning = '', Error = '', Wifi = '', Eye = '', Drop = '', Calendar = '', Clock = '',
        Wind = '', Sparkle = '', Run = '', Uninstall = '', Location = '', Refresh = '',
        Mail = '', Message = '', Back = '', Forward = '', Lock = '', Cast = '', Remote = '', Button = '', Download = '', Video = '', Convert = '', Stop = '';
}
