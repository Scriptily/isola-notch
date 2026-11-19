using System.Net;
using System.Net.Http;
using Isola.App;
using Isola.Island;
using Isola.Native;
using Isola.Render;
using Isola.Shell;

namespace Isola.Tests;

/// <summary>Time that only moves when a scenario says so.</summary>
public sealed class FakeClock : IClock
{
    public double Now { get; set; } = 1000;
    public DateTime Wall { get; set; } = new DateTime(2026, 11, 14, 15, 0, 0);
    /// <summary>Advance, firing UI timers and stepping the island in small frames, as the real loop does.</summary>
    public void Advance(double seconds, Island.Island isl = null, double frame = 1 / 60.0)
    {
        double end = Now + seconds;
        while (Now < end - 1e-9)
        {
            double dt = Math.Min(frame, end - Now);
            Now += dt; Wall = Wall.AddSeconds(dt);
            Ui.Drain(); Ui.RunTimers();
            isl?.Step(dt);
        }
        Ui.Drain(); Ui.RunTimers();
    }
}

/// <summary>Stands in for the two windows: records what the island asks of them.</summary>
public sealed class FakeHost : IIslandHost
{
    public float Width = 900;
    public nint Fg;   // the window with the keyboard
    public nint Foreground() => Fg;
    public float WindowWidth => Width;
    public float WindowHeight => 560;
    public int Frames, KeyboardTakes;
    public bool Shown = true, Keyboard, ButtonDown;
    public bool MouseButtonDown => ButtonDown;
    public Box Hit; public float HitRadius;
    public (float x, float y, bool on) Pointer = (450, 900, true);
    public int SettingsOpened, Menus;
    public List<string> DraggedFiles = new(), DraggedText = new();
    public void RequestFrame() => Frames++;
    public void SetHitArea(Box island, float radius) { Hit = island; HitRadius = radius; }
    public void SetShown(bool shown) => Shown = shown;
    public void TakeKeyboard(bool on) { Keyboard = on; if (on) KeyboardTakes++; }
    public void SetCursor(CursorKind k) { }
    public (float x, float y, bool onScreen) Cursor() => Pointer;
    public string SettingsSection;
    public void OpenSettings(string section = null) { SettingsOpened++; SettingsSection = section; }
    public void ShowContextMenu(float x, float y) => Menus++;
    public void StartFileDrag(IReadOnlyList<string> paths) => DraggedFiles.AddRange(paths);
    public void StartTextDrag(string text) => DraggedText.Add(text);
}

/// <summary>A desktop of windows, topmost first, that scenarios arrange freely.</summary>
public sealed class FakeWindows : IWindowSource
{
    public readonly List<WinInfo> Stack = new();
    public nint Fg;
    public IEnumerable<WinInfo> ZOrder() => Stack;
    public nint Foreground() => Fg;
    public WinInfo Describe(nint h) => Stack.FirstOrDefault(w => w.Hwnd == h);
    public static WinInfo Win(nint h, RECT r, string cls = "App", bool max = false, bool caption = true, bool clickThrough = false, bool visible = true, bool min = false, bool cloaked = false, uint pid = 4242) =>
        new(h, r, visible, min, cloaked, max, caption, clickThrough, false, cls, pid);
}

/// <summary>The laptop panel plus whatever external displays a scenario plugs in.</summary>
public sealed class FakeDisplays : IDisplaySource
{
    public readonly List<DisplayPath> Paths = new();
    public readonly List<Shell.Monitor> Mons = new();
    public IReadOnlyList<DisplayPath> ActivePaths() => Paths.ToList();
    public IReadOnlyList<Shell.Monitor> Monitors() => Mons.ToList();
    public bool BuiltIn = true;
    public bool HasBuiltInPanel() => BuiltIn;
    public static readonly RECT LaptopRect = new(0, 0, 2880, 1800), TvRect = new(2880, -183, 6720, 1977);
    public void Laptop(bool primary = true)
    {
        Paths.Add(new DisplayPath(@"\\.\DISPLAY1", "Built-in panel", "laptop-path", Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED, 2880, 1800, 120));
        Mons.Add(new Shell.Monitor(1, @"\\.\DISPLAY1", LaptopRect, 144, primary));
    }
    public void Tv(bool primary = false, string gdi = @"\\.\DISPLAY2")
    {
        Paths.Add(new DisplayPath(gdi, "Living Room TV", "tv-path", Win32.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI, 3840, 2160, 144));
        Mons.Add(new Shell.Monitor(2, gdi, TvRect, 192, primary));
    }
    public void Unplug(string friendly) { Paths.RemoveAll(p => p.FriendlyName == friendly); }
}

/// <summary>A Home Assistant that answers from a dictionary, or not at all.</summary>
public sealed class FakeHa : HttpMessageHandler
{
    public readonly Dictionary<string, string> States = new();
    public HttpStatusCode Status = HttpStatusCode.OK;
    public readonly List<string> Calls = new();
    /// <summary>Service calls wait on this before answering, so a test can hold one in flight.</summary>
    public Task Gate = Task.CompletedTask;
    /// <summary>Service calls throw, as when the connection drops mid-call.</summary>
    public bool CallsFail;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string path = req.RequestUri!.AbsolutePath;
        if (Status != HttpStatusCode.OK) return new HttpResponseMessage(Status);
        if (req.Method == HttpMethod.Post)
        {
            Calls.Add(path + " " + await req.Content!.ReadAsStringAsync(ct));
            await Gate;
            if (CallsFail) throw new HttpRequestException("connection dropped");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }
        return await Get(path);
    }
    Task<HttpResponseMessage> Get(string path)
    {
        if (path == "/api/states") return Ok("[" + string.Join(",", States.Values) + "]");
        string id = path.Replace("/api/states/", "");
        return States.TryGetValue(id, out var js) ? Ok(js) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
    static Task<HttpResponseMessage> Ok(string s) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(s) });
    public void Entity(string id, string state, string name, string attrs = "") =>
        States[id] = $"{{\"entity_id\":\"{id}\",\"state\":\"{state}\",\"attributes\":{{\"friendly_name\":\"{name}\"{(attrs.Length > 0 ? "," + attrs : "")}}}}}";
}

/// <summary>GitHub's releases API for one private repo: a release list and its asset download.</summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public string ReleasesJson = "[]";
    public byte[] Asset = Array.Empty<byte>();
    public readonly List<string> Requests = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string path = req.RequestUri!.AbsolutePath;
        Requests.Add($"{path} auth={req.Headers.Authorization?.Scheme}");
        if (path.EndsWith("/releases")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleasesJson) });
        if (path.Contains("/releases/assets/")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Asset) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
