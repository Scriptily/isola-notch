using System.Runtime.InteropServices;
using Isola.Island;
using Isola.Render;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Box = Isola.Render.Box;

namespace Isola.Tests;

/// <summary>UI details from the design rules: click targets, icon swaps, faded scroll edges.</summary>
public static partial class Scenarios
{
    // An offscreen software target: draws exactly what a window would, and can be read back.
    sealed class Offscreen : IDisposable
    {
        public readonly ID2D1DeviceContext Ctx;
        public readonly ID2D1Bitmap1 Target;
        public readonly Canvas Canvas = new();
        readonly int w, h;
        public Offscreen(int w, int h)
        {
            if (Gfx.D2DDevice == null) Gfx.Init(true);
            this.w = w; this.h = h;
            Ctx = Gfx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
            Target = Ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            Ctx.Target = Target;
        }
        public void Draw(Action<Canvas> draw)
        {
            Ctx.BeginDraw();
            Ctx.Clear(new Color4(0, 0, 0, 0));
            Canvas.Begin(Ctx);
            draw(Canvas);
            Ctx.EndDraw().CheckError();
        }
        /// <summary>Alpha (0..255) of each pixel down one column.</summary>
        public byte[] AlphaColumn(int x)
        {
            using var read = Ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
            read.CopyFromBitmap(Target);
            var m = read.Map(MapOptions.Read);
            try { return Enumerable.Range(0, h).Select(y => Marshal.ReadByte(m.Bits, y * (int)m.Pitch + x * 4 + 3)).ToArray(); }
            finally { read.Unmap(); }
        }
        /// <summary>The lines of text drawn: runs of rows with any ink in them.</summary>
        public int InkLines()
        {
            using var read = Ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
            read.CopyFromBitmap(Target);
            var m = read.Map(MapOptions.Read);
            try
            {
                int lines = 0; bool inLine = false;
                for (int y = 0; y < h; y++)
                {
                    bool ink = Enumerable.Range(0, w).Any(x => Marshal.ReadByte(m.Bits, y * (int)m.Pitch + x * 4 + 3) > 60);
                    if (ink && !inLine) lines++;
                    inLine = ink;
                }
                return lines;
            }
            finally { read.Unmap(); }
        }
        public void Dispose() { Target.Dispose(); Ctx.Dispose(); }
    }

    [Scenario("a small round icon button can be clicked a few pixels outside its drawn circle; neighbors 36 apart never steal each other's clicks")]
    public static void IconButtonTargets()
    {
        using var off = new Offscreen(300, 100);
        var ux = new Ux();
        string hit = null;
        off.Draw(c =>
        {
            ux.BeginFrame(c);
            // the Home Assistant entity row's buttons: 28 px circles, 36 apart
            ux.IconButton("up", 100, 50, 28, Glyph.Up, () => hit = "up");
            ux.IconButton("down", 136, 50, 28, Glyph.Down, () => hit = "down");
            ux.EndFrame();
        });
        ux.Move(100, 50 + 16); ux.Down(100, 66); ux.Up(100, 66);
        Eq("up", hit, "16 px below the center of a 28 px button (outside the circle) still clicks it");
        hit = null; ux.Move(117, 50); ux.Down(117, 50); ux.Up(117, 50);
        Eq("up", hit, "just short of halfway goes to the nearer button");
        hit = null; ux.Move(119, 50); ux.Down(119, 50); ux.Up(119, 50);
        Eq("down", hit, "just past halfway goes to the other");
        True(Ux.MinHit >= 36, "targets at least 36 across");
    }

    [Scenario("an icon that changes crossfades through a blur instead of popping; with Reduce motion it changes at once")]
    public static void IconSwapBlurs()
    {
        using var off = new Offscreen(100, 100);
        var ux = new Ux();
        char glyph = Glyph.Play;
        void Frame() { off.Draw(c => { ux.BeginFrame(c); ux.IconButton("play", 50, 50, 44, glyph, () => { }); ux.EndFrame(); }); ux.Step(1 / 60.0); }
        Frame(); Frame();
        True(!ux.MovingKeys().Contains("swap:play"), "nothing moves while the icon stays the same");
        glyph = Glyph.Pause;
        Frame();
        True(ux.MovingKeys().Contains("swap:play"), "the change animates: " + ux.MovingKeys());
        for (int i = 0; i < 40; i++) Frame();
        True(!ux.MovingKeys().Contains("swap:play"), "and settles within the snappy spring's time");

        var calm = new Ux { ReduceMotion = true };
        void Calm() { off.Draw(c => { calm.BeginFrame(c); calm.IconButton("play", 50, 50, 44, glyph, () => { }); calm.EndFrame(); }); calm.Step(1 / 60.0); }
        Calm(); glyph = Glyph.Play; Calm();
        True(!calm.MovingKeys().Contains("swap:play"), "Reduce motion swaps it outright");
    }

    [Scenario("a scrolled list fades out at the edge with more to scroll toward, and stays solid at an edge that has nothing beyond it")]
    public static void ScrollEdgesFade()
    {
        using var off = new Offscreen(60, 200);
        void Fill(bool top, bool bottom) => off.Draw(c =>
        {
            c.FadeClip(new Box(0, 0, 60, 200), top, bottom);
            c.Fill(new Box(0, 0, 60, 200), Col.White);
            c.Unfade();
        });
        Fill(false, true);
        var a = off.AlphaColumn(30);
        True(a[100] > 250, "the middle is solid");
        True(a[0] > 250, "the top, with nothing above it, stays solid");
        True(a[198] < 40, $"the bottom fades out (alpha {a[198]})");
        Fill(true, false);
        a = off.AlphaColumn(30);
        True(a[1] < 40 && a[199] > 250, $"scrolled to the end: the top fades (alpha {a[1]}), the bottom doesn't");
        Fill(false, false);
        a = off.AlphaColumn(30);
        True(a[0] > 250 && a[199] > 250, "a list that fits fades nowhere");
    }
    [Scenario("a bug report's description asks for the steps to reproduce it, laid out as numbered lines in the field")]
    public static void BugHintAsksForSteps()
    {
        True(App.SettingsWindow.BugHint.Contains("Steps to reproduce"), "it names the steps");
        using var off = new Offscreen(520, 150);
        var tb = new TextBox { Multiline = true, Placeholder = App.SettingsWindow.BugHint };
        off.Draw(c => tb.Draw(c, new Box(0, 0, 520, 150), 13, false, 0, Col.White));
        int lines = off.InkLines();
        True(lines >= 5, $"what happened, the heading and three numbered steps, each on its own line ({lines} lines)");
    }
    [Scenario("About credits the author, and the name opens the project's GitHub page")]
    public static void AboutCreditsAuthor()
    {
        Eq("Scriptily", App.SettingsWindow.Author, "the name shown");
        Eq("https://github.com/Scriptily/isola-notch", App.SettingsWindow.RepoPage, "where it goes");
        Eq("https://github.com/sponsors/Scriptily?frequency=one-time&sponsor=Scriptily", App.SettingsWindow.SponsorPage, "Support Isola opens the GitHub Sponsors page, one-time first");
    }
}
