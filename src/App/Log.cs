using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Isola.App;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Verbose, rotating log in the data folder (isola.log, 1 MB x 4; see Paths).
/// Writes go through a queue to a background thread so a slow disk can never
/// stall a frame; Error lines and crash reports are flushed synchronously so the
/// last thing that happened before a crash is always on disk.
/// </summary>
public static class Log
{
    static readonly BlockingCollection<string> queue = new(new ConcurrentQueue<string>(), 4096);
    static string path;
    static Thread writer;
    public static LogLevel MinLevel = LogLevel.Debug;
    public static bool Echo;              // also write to stdout (tests, --console)
    public static Action<string> Sink;    // tests capture lines here
    const long MaxBytes = 1 << 20;
    const int Keep = 4;
    static readonly object fileLock = new();

    public static string FilePath => path;

    public static void Init(string file)
    {
        path = file;
        writer = new Thread(Pump) { IsBackground = true, Name = "log", Priority = ThreadPriority.BelowNormal };
        writer.Start();
    }

    public static void D(string area, string msg) => Write(LogLevel.Debug, area, msg);
    public static void I(string area, string msg) => Write(LogLevel.Info, area, msg);
    public static void W(string area, string msg) => Write(LogLevel.Warn, area, msg);
    public static void E(string area, string msg, Exception ex = null) => Write(LogLevel.Error, area, ex == null ? msg : $"{msg}: {Describe(ex)}");

    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" <- ");
            sb.Append(e.GetType().Name).Append(" 0x").Append(e.HResult.ToString("X8")).Append(": ").Append(e.Message.Trim());
        }
        if (ex.StackTrace != null) sb.Append('\n').Append(ex.StackTrace);
        return sb.ToString();
    }

    public static void Write(LogLevel level, string area, string msg)
    {
        if (level < MinLevel) return;
        string tag = level switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warn => "WRN", _ => "ERR" };
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {tag} [{area,-10}] {msg}";
        Sink?.Invoke(line);
        if (Echo) Console.WriteLine(line);
        if (path == null) return;
        if (level == LogLevel.Error) { Flush(); AppendNow(line); return; }
        queue.TryAdd(line);
    }

    /// <summary>Synchronous write for crash handlers, where the writer thread may never run again.</summary>
    public static void Crash(string what, Exception ex)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ERR [crash     ] {what}: {(ex == null ? "(no exception)" : Describe(ex))}";
        Sink?.Invoke(line);
        if (Echo) Console.WriteLine(line);
        if (path != null) { Flush(); AppendNow(line); }
    }

    public static void Flush()
    {
        while (queue.TryTake(out var l)) AppendNow(l);
    }

    static void Pump()
    {
        foreach (var line in queue.GetConsumingEnumerable())
        {
            var sb = new StringBuilder(line).Append('\n');
            while (sb.Length < 64 * 1024 && queue.TryTake(out var more)) sb.Append(more).Append('\n');
            AppendRaw(sb.ToString());
        }
    }

    static void AppendNow(string line) => AppendRaw(line + "\n");

    static void AppendRaw(string text)
    {
        lock (fileLock)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes) Rotate();
                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch (Exception ex) { Debug.WriteLine("log write failed: " + ex.Message); }
        }
    }

    static void Rotate()
    {
        try
        {
            for (int i = Keep - 1; i >= 1; i--)
            {
                string from = i == 1 ? path : $"{path}.{i - 1}", to = $"{path}.{i}";
                if (File.Exists(from)) { File.Copy(from, to, true); }
            }
            File.WriteAllText(path, "");
        }
        catch { }
    }
}
