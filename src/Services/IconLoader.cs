using System.Collections.Concurrent;
using Isola.App;

namespace Isola.Services;

/// <summary>
/// Shell icons, fetched on a background STA thread. Asking Explorer for an
/// icon can take tens of milliseconds (more for a file on a slow drive), and
/// doing it on the UI thread froze transitions mid-flight. Callers get null and
/// a frame later the icon, delivered on the UI thread.
/// </summary>
public static class IconLoader
{
    static readonly ConcurrentDictionary<string, bool> pending = new();
    static readonly BlockingCollection<(string path, int size)> queue = new(new ConcurrentStack<(string, int)>());   // newest first: what's on screen now
    static Thread worker;
    public static Action<string, int, (byte[] px, int w, int h)?> Delivered;   // UI thread

    public static void Request(string path, int size)
    {
        string key = path + "|" + size;
        if (!pending.TryAdd(key, true)) return;
        Start();
        queue.Add((path, size));
    }

    static void Start()
    {
        if (worker != null) return;
        worker = new Thread(Run) { IsBackground = true, Name = "icons", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA);   // the shell's icon handlers want an STA
        worker.Start();
    }

    static void Run()
    {
        foreach (var (path, size) in queue.GetConsumingEnumerable())
        {
            (byte[] px, int w, int h)? r = null;
            try { r = Shell.Icon(path, size); } catch (Exception ex) { Log.D("icons", $"icon for {path}: {ex.Message}"); }
            Ui.Post(() => { pending.TryRemove(path + "|" + size, out _); Delivered?.Invoke(path, size, r); });
        }
    }
}
