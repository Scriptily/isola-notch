using System.Diagnostics;
using System.Reflection;

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
                [Services.Sounds.Connect] = "A device connects", [Services.Sounds.Disconnect] = "A device disconnects", [Services.Sounds.Charge] = "Charger plugged in", [Services.Sounds.Low] = "Low battery" };
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
}
