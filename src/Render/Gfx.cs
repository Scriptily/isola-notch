using Isola.App;
using Isola.Native;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using D2DFactory = Vortice.Direct2D1.ID2D1Factory1;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Isola.Render;

/// <summary>
/// The process-wide GPU objects: one D3D11 device on the low-power adapter, the
/// Direct2D device over it, DirectWrite, WIC and DirectComposition. Every window
/// shares them. When the device is lost (driver update, GPU reset, a hybrid-GPU
/// switch) <see cref="Recover"/> rebuilds everything and each surface rebuilds its
/// swap chain on the next frame; nothing has to restart.
/// </summary>
public static class Gfx
{
    public static ID3D11Device Device { get; private set; }
    public static IDXGIDevice DxgiDevice { get; private set; }
    public static D2DFactory D2DFactory { get; private set; }
    public static ID2D1Device D2DDevice { get; private set; }
    public static IDWriteFactory DWrite { get; private set; }
    public static IWICImagingFactory Wic { get; private set; }

    /// <summary>
    /// Decodes an image held in memory and hands its first frame to <paramref name="use"/>.
    ///
    /// The bytes go to WIC as its own memory stream over a pinned copy, never as
    /// a .NET Stream: Vortice wraps a .NET stream in a managed COM object, and
    /// WIC can release that object after .NET has disposed it ("Shadow
    /// SharpGen.Runtime.IUnknown is dead"), which ends the process. Media apps
    /// opening and closing sessions every few hundred milliseconds (a website
    /// autoplaying previews) made that race real. Everything is decoded on load
    /// and released before the bytes are unpinned.
    /// </summary>
    public static T DecodeImage<T>(byte[] data, Func<IWICBitmapFrameDecode, T> use)
    {
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var stream = Wic.CreateStream();
            stream.Initialize(data).CheckError();   // WIC reads this array in place: it stays pinned until all is released
            using var dec = Wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
            using var frame = dec.GetFrame(0);
            return use(frame);
        }
        finally { pin.Free(); }
    }
    public static IDCompositionDevice DComp { get; private set; }
    public static IDXGIFactory2 DxgiFactory { get; private set; }
    public static int Generation { get; private set; }
    public static string AdapterName { get; private set; } = "?";
    /// <summary>Software rendering (tests, or a machine with no usable GPU).</summary>
    public static bool Warp { get; private set; }

    /// <summary>Only the imaging factory (tests decode images without a GPU device).</summary>
    public static void InitImaging() => Wic ??= new IWICImagingFactory();

    public static void Init(bool warp = false)
    {
        Warp = warp;
        D2DFactory ??= D2D1.D2D1CreateFactory<D2DFactory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        DWrite ??= Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        Wic ??= new IWICImagingFactory();
        CreateDevice();
    }

    static void CreateDevice()
    {
        DxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        IDXGIAdapter1 adapter = null;
        if (!Warp)
        {
            // The island is tiny 2D work: keep it on the integrated GPU so it
            // never powers the discrete one up. The OLED panel is wired to the
            // integrated GPU on most laptops anyway, so there is no cross-adapter copy.
            try
            {
                using var f6 = DxgiFactory.QueryInterfaceOrNull<IDXGIFactory6>();
                if (f6 != null && f6.EnumAdapterByGpuPreference(0, GpuPreference.MinimumPower, out IDXGIAdapter1 a).Success) adapter = a;
            }
            catch (Exception ex) { Log.W("gfx", "GPU preference query failed, using default adapter: " + ex.Message); }
        }
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        ID3D11Device dev = null;
        if (Warp)
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out dev).CheckError();
            AdapterName = "WARP";
        }
        else
        {
            var r = D3D11.D3D11CreateDevice(adapter, adapter == null ? DriverType.Hardware : DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out dev);
            if (r.Failure)
            {
                Log.W("gfx", $"hardware device failed 0x{r.Code:X8}; falling back to WARP");
                D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out dev).CheckError();
                AdapterName = "WARP (fallback)";
            }
            else AdapterName = adapter?.Description1.Description ?? "default";
        }
        adapter?.Dispose();
        Device = dev;
        DxgiDevice = dev.QueryInterface<IDXGIDevice>();
        D2DDevice = D2DFactory.CreateDevice(DxgiDevice);
        DComp = DCompositionCreate(DxgiDevice);
        Generation++;
        Log.I("gfx", $"device ready on {AdapterName} (generation {Generation})");
    }

    static IDCompositionDevice DCompositionCreate(IDXGIDevice dxgi)
    {
        return Vortice.DirectComposition.DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);
    }

    public static bool IsDeviceLost(int hr) =>
        hr == unchecked((int)0x887A0005) /*REMOVED*/ || hr == unchecked((int)0x887A0007) /*RESET*/ ||
        hr == unchecked((int)0x887A0020) /*DRIVER_INTERNAL*/ || hr == unchecked((int)0x8899000C) /*D2DERR_RECREATE_TARGET*/;

    public static void Recover(string why)
    {
        Log.W("gfx", "recovering GPU device: " + why);
        try { DComp?.Dispose(); } catch { }
        try { D2DDevice?.Dispose(); } catch { }
        try { DxgiDevice?.Dispose(); } catch { }
        try { Device?.Dispose(); } catch { }
        DComp = null; D2DDevice = null; DxgiDevice = null; Device = null;
        for (int i = 0; ; i++)
        {
            try { CreateDevice(); return; }
            catch (Exception ex)
            {
                // During a driver install the adapter can be missing for a few
                // seconds. Fall back to WARP rather than leaving the island dead.
                if (i >= 3) { Log.E("gfx", "device recreation keeps failing; using WARP", ex); Warp = true; CreateDevice(); return; }
                Thread.Sleep(500 * (i + 1));
            }
        }
    }
}

/// <summary>
/// One window's drawing surface: a composition swap chain (premultiplied alpha,
/// so the island is a true per-pixel-alpha overlay with nothing redirected
/// through GDI), its Direct2D context, and the DirectComposition visual that
/// puts it on the window. Rebuilt transparently after a device loss.
/// </summary>
public sealed class Surface : IDisposable
{
    public readonly nint Hwnd;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public float Dpi { get; set; } = 96;
    public ID2D1DeviceContext Ctx { get; private set; }
    public nint FrameWaitHandle { get; private set; }

    IDXGISwapChain1 swap;
    IDCompositionTarget target;
    IDCompositionVisual visual;
    ID2D1Bitmap1 backbuffer;
    int generation;
    bool drawing;

    public Surface(nint hwnd, int w, int h) { Hwnd = hwnd; Width = Math.Max(1, w); Height = Math.Max(1, h); }

    void Build()
    {
        Release();
        generation = Gfx.Generation;
        var desc = new SwapChainDescription1
        {
            Width = (uint)Width, Height = (uint)Height, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
            SwapEffect = SwapEffect.FlipSequential, BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied, Scaling = Scaling.Stretch, Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };
        swap = Gfx.DxgiFactory.CreateSwapChainForComposition(Gfx.Device, desc);
        using (var s2 = swap.QueryInterface<IDXGISwapChain2>())
        {
            s2.MaximumFrameLatency = 1;
            FrameWaitHandle = s2.FrameLatencyWaitableObject;
        }
        Ctx = Gfx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        Ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;   // ClearType cannot blend over transparency
        Ctx.UnitMode = UnitMode.Pixels;
        BindBackbuffer();
        Gfx.DComp.CreateTargetForHwnd(Hwnd, true, out target).CheckError();
        Gfx.DComp.CreateVisual(out visual).CheckError();
        visual.SetContent(swap).CheckError();
        target.SetRoot(visual).CheckError();
        Gfx.DComp.Commit().CheckError();
    }

    void BindBackbuffer()
    {
        backbuffer?.Dispose();
        using var surf = swap.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw);
        backbuffer = Ctx.CreateBitmapFromDxgiSurface(surf, props);
        Ctx.Target = backbuffer;
    }

    public void Resize(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (w == Width && h == Height) return;
        Width = w; Height = h;
        if (swap == null) return;
        Ctx.Target = null; backbuffer?.Dispose(); backbuffer = null;
        var r = swap.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject);
        if (r.Failure) { Log.W("gfx", $"ResizeBuffers 0x{r.Code:X8}; rebuilding surface"); Release(); return; }
        BindBackbuffer();
    }

    // Development only (ISOLA_BACKDROP=rrggbb): an opaque color behind the island,
    // so screenshots don't carry whatever is on the desktop underneath.
    static readonly Color4 Backdrop = int.TryParse(Environment.GetEnvironmentVariable("ISOLA_BACKDROP"), System.Globalization.NumberStyles.HexNumber, null, out int bd)
        ? new Color4(((bd >> 16) & 255) / 255f, ((bd >> 8) & 255) / 255f, (bd & 255) / 255f, 1) : new Color4(0, 0, 0, 0);

    /// <summary>Begin a frame. Coordinates are DIPs (the context's DPI is set).</summary>
    public ID2D1DeviceContext Begin()
    {
        if (swap == null || generation != Gfx.Generation) Build();
        Ctx.Dpi = new Vortice.Mathematics.Size(Dpi, Dpi);
        Ctx.UnitMode = UnitMode.Dips;
        Ctx.BeginDraw();
        Ctx.Clear(Backdrop);
        drawing = true;
        return Ctx;
    }

    public void End()
    {
        if (!drawing) return;
        drawing = false;
        var r = Ctx.EndDraw();
        if (r.Failure)
        {
            if (Gfx.IsDeviceLost(r.Code)) { Gfx.Recover($"EndDraw 0x{r.Code:X8}"); Release(); return; }
            Log.W("gfx", $"EndDraw 0x{r.Code:X8}");
        }
        var p = swap.Present(1, PresentFlags.None);
        if (p.Failure && Gfx.IsDeviceLost(p.Code)) { Gfx.Recover($"Present 0x{p.Code:X8}"); Release(); }
    }

    void Release()
    {
        if (Ctx != null) Ctx.Target = null;
        backbuffer?.Dispose(); backbuffer = null;
        Ctx?.Dispose(); Ctx = null;
        visual?.Dispose(); visual = null;
        target?.Dispose(); target = null;
        if (FrameWaitHandle != 0) { Win32.CloseHandle(FrameWaitHandle); FrameWaitHandle = 0; }
        swap?.Dispose(); swap = null;
    }

    public void Dispose() => Release();
}
