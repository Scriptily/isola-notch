using System.Diagnostics;
using System.Reflection;
using Isola.App;

namespace Isola.Tests;

/// <summary>
/// Runs every [Scenario] in the suite against the real island code and fakes.
/// Exit code is the number of failures. Pass a word to run only matching scenarios,
/// or --example-config PATH to regenerate isola.config.example.json.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // regenerate the shipped example from the real defaults
        if (args.Length == 2 && args[0] == "--example-config")
        {
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new App.Config(), App.ConfigJson.Default.Config) + Environment.NewLine);
            Console.WriteLine("wrote " + args[1]);
            return 0;
        }
        // every island sound exactly as Isola plays it, to listen to (and a page to play them)
        if (args.Length == 2 && args[0] == "--render-sounds")
        {
            var labels = new Dictionary<string, string> { [Services.Sounds.Notification] = "Notifications", [Services.Sounds.Call] = "Ringtones (they loop)", [Services.Sounds.Timer] = "Timer alarms (they loop)",
                [Services.Sounds.Connect] = "A device connects", [Services.Sounds.Disconnect] = "A device disconnects", [Services.Sounds.Charge] = "Charger plugged in", [Services.Sounds.Low] = "Low battery", [Services.Sounds.Notice] = "Notices (quieter)" };
            var html = new System.Text.StringBuilder("<!doctype html><meta charset=utf-8><title>Isola's sounds</title><style>body{background:#16161a;color:#eee;font:14px 'Segoe UI',sans-serif;max-width:720px;margin:30px auto;padding:0 16px}h2{font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:#999;margin:26px 0 8px}div{display:flex;gap:12px;align-items:center;margin:6px 0}audio{height:32px}</style><h1>Isola's sounds, as it plays them</h1>");
            foreach (var kind in Services.Sounds.All)
            {
                Directory.CreateDirectory(Path.Combine(args[1], kind));
                html.Append($"<h2>{labels[kind]}</h2>");
                foreach (var (id, label) in Services.Sounds.Choices(kind))
                {
                    var made = System.Diagnostics.Stopwatch.StartNew();
                    var wav = Services.Sounds.Render(kind, 1f, id);
                    Console.WriteLine($"{kind}/{id}: {made.ElapsedMilliseconds} ms to make");
                    File.WriteAllBytes(Path.Combine(args[1], kind, id + ".wav"), wav);
                    html.Append($"<div><audio controls {(Services.Sounds.Loops(kind) ? "loop" : "")} src=\"{kind}/{id}.wav\"></audio><span>{label}</span></div>");
                }
            }
            File.WriteAllText(Path.Combine(args[1], "index.html"), html.ToString());
            Console.WriteLine("wrote " + args[1]);
            return 0;
        }
        // the real thing, end to end, over the network: the tools from GitHub, a real video, every conversion
        //   dotnet run -c Release -- --try-downloads <folder> [link]
        if (args.Length >= 2 && args[0] == "--try-downloads") return TryDownloads(args[1], args.Length > 2 ? args[2] : "https://www.youtube.com/watch?v=jNQXAC9IVRw");
        string filter = args.Length > 0 ? args[0] : null;
        var all = typeof(Scenarios).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (m, a: m.GetCustomAttribute<ScenarioAttribute>()))
            .Where(x => x.a != null && (filter == null || x.m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        // what the services log goes beside the test build, never into a real log
        string log = Path.Combine(AppContext.BaseDirectory, "scenarios.log");
        try { File.Delete(log); } catch { }
        App.Log.Init(log);
        int failed = 0;
        var sw = Stopwatch.StartNew();
        foreach (var (m, a) in all)
        {
            try { m.Invoke(null, null); Console.WriteLine($"  pass  {a.Name}"); }
            catch (TargetInvocationException e)
            {
                failed++;
                var inner = e.InnerException!;
                Console.WriteLine($"  FAIL  {a.Name}\n        {m.Name}: {inner.Message}");
                if (inner is not Exception { Source: "Isola.Tests" } || inner.GetType() != typeof(Exception))
                    Console.WriteLine("        " + (inner.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim());
            }
        }
        App.Log.Flush();
        if (failed > 0) Console.WriteLine("        services' log: " + log);
        Console.WriteLine($"\n{all.Count - failed}/{all.Count} passed in {sw.Elapsed.TotalSeconds:0.0}s");
        return failed;
    }

    static int TryDownloads(string root, string link)
    {
        Directory.CreateDirectory(root);
        var clock = new RealClock(); Ui.Clock = clock;
        App.Log.Init(Path.Combine(root, "try.log"));
        var tools = new Services.DownloadTools(Path.Combine(root, "download-tools"), clock);
        var t0 = Stopwatch.StartNew();
        bool ready = Pump(tools.CheckAsync(true));
        Console.WriteLine($"tools: {(ready ? "ready" : "NOT ready")} in {t0.Elapsed.TotalSeconds:0}s: {tools.Status}, {tools.Bytes / 1_000_000} MB");
        if (!ready) return 1;
        var cfg = new App.Config { DownloadTo = "folder", DownloadFolder = Path.Combine(root, "out") };
        var dl = new Services.Downloads(Path.Combine(root, "downloads.json"), tools) { Cfg = () => cfg };
        int fails = 0;
        Services.DownloadJob Go(Func<Services.DownloadJob> make)
        {
            var sw = Stopwatch.StartNew();
            var j = make();
            string lastStage = "";
            while (j.Active && sw.Elapsed.TotalMinutes < 10) { Ui.Drain(); if (j.Stage != lastStage) { Console.WriteLine($"   {sw.Elapsed.TotalSeconds,5:0.0}s {j.Stage} ({j.Progress:P0})"); lastStage = j.Stage; } Thread.Sleep(50); }
            Ui.Drain();
            string probe = j.State == "done" ? Probe(tools.PathOf("ffmpeg"), j.Output) : "";
            bool ok = j.State == "done" && File.Exists(j.Output) && new FileInfo(j.Output).Length > 1000 && probe.Contains("Duration");
            if (!ok) fails++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {j.Kind} {j.Format}: {j.State} in {sw.Elapsed.TotalSeconds:0.0}s {(j.State == "done" ? Path.GetFileName(j.Output) + $" {new FileInfo(j.Output).Length / 1024} KB  {probe}" : j.Error)}");
            return j;
        }
        var video = Go(() => dl.Download(link, false));
        Go(() => dl.Download(link, true));
        cfg.DownloadAudioFormat = "m4a";
        Go(() => dl.Download(link, true));
        if (video.State == "done")
            foreach (var f in Services.Downloads.FormatsFor(video.Output)) Go(() => dl.Convert(video.Output, f));
        // a cancel mid-way leaves nothing behind
        var c = dl.Convert(video.Output, "webm");
        for (int i = 0; i < 40 && c.State != "running"; i++) { Thread.Sleep(50); Ui.Drain(); }
        dl.Cancel(c);
        Thread.Sleep(1500); Ui.Drain();
        bool clean = !Directory.EnumerateFiles(dl.TempDir).Any();
        Console.WriteLine($"{(c.State == "cancelled" && clean ? "ok  " : "FAIL")} cancel: {c.State}, work folder {(clean ? "empty" : "NOT empty")}");
        if (!(c.State == "cancelled" && clean)) fails++;
        var bad = Go(() => dl.Download("https://example.com/not-a-video", false));
        if (bad.State == "failed") { fails--; Console.WriteLine($"     (that failure was expected: \"{bad.Error}\")"); }
        Console.WriteLine(fails == 0 ? "all good" : $"{fails} FAILED");
        return fails;
    }

    static T Pump<T>(Task<T> t) { while (!t.IsCompleted) { Ui.Drain(); Thread.Sleep(50); } Ui.Drain(); return t.Result; }

    static string Probe(string ffmpeg, string file)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(file);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd(); p.WaitForExit();
        var lines = err.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("Duration") || l.StartsWith("Stream")).Select(l => l.Length > 70 ? l[..70] : l);
        return string.Join(" | ", lines);
    }
}
