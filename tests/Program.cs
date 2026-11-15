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
