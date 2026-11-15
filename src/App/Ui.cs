using System.Collections.Concurrent;
using System.Diagnostics;
using Isola.Native;

namespace Isola.App;

/// <summary>Time source. Real code reads Stopwatch; tests drive a fake one.</summary>
public interface IClock
{
    /// <summary>Monotonic seconds.</summary>
    double Now { get; }
    DateTime Wall { get; }
}

public sealed class RealClock : IClock
{
    static readonly double freq = Stopwatch.Frequency;
    public double Now => Stopwatch.GetTimestamp() / freq;
    public DateTime Wall => DateTime.Now;
}

/// <summary>
/// The UI thread's dispatcher and one-shot timers. Services raise events on
/// whatever thread WinRT or the thread pool hands them; everything that touches
/// island state is marshalled here with <see cref="Post"/>, so the island
/// itself is single-threaded and needs no locks.
/// </summary>
public static class Ui
{
    static readonly ConcurrentQueue<Action> queue = new();
    static nint wakeHwnd;
    public const uint WM_WAKE = Win32.WM_APP + 1;
    public static int ThreadId { get; private set; }
    public static IClock Clock { get; set; } = new RealClock();

    sealed class Timer { public double Due; public Action Act; public long Id; public bool Dead; }
    static readonly List<Timer> timers = new();
    static long nextId = 1;

    public static void Init(nint hwnd) { wakeHwnd = hwnd; ThreadId = Environment.CurrentManagedThreadId; }
    public static bool OnUiThread => Environment.CurrentManagedThreadId == ThreadId;

    public static void Post(Action a)
    {
        queue.Enqueue(a);
        if (wakeHwnd != 0) Win32.PostMessageW(wakeHwnd, WM_WAKE, 0, 0);
    }

    /// <summary>Run now if already on the UI thread, else post.</summary>
    public static void Run(Action a) { if (OnUiThread) a(); else Post(a); }

    public static void Drain()
    {
        int n = 0;
        while (n++ < 512 && queue.TryDequeue(out var a)) Guard("posted", a);
    }

    /// <summary>One-shot timer on the UI thread. Returns an id for <see cref="Cancel"/>.</summary>
    public static long After(double seconds, Action a)
    {
        var t = new Timer { Due = Clock.Now + Math.Max(0, seconds), Act = a, Id = nextId++ };
        timers.Add(t);
        return t.Id;
    }

    public static void Cancel(ref long id)
    {
        long i = id;
        if (i != 0) foreach (var t in timers) if (t.Id == i) t.Dead = true;
        id = 0;
    }

    /// <summary>Seconds until the next live timer is due, or +inf.</summary>
    public static double NextDue()
    {
        double now = Clock.Now, best = double.PositiveInfinity;
        foreach (var t in timers) if (!t.Dead) best = Math.Min(best, t.Due - now);
        return best;
    }

    public static void RunTimers()
    {
        double now = Clock.Now;
        if (timers.Count == 0) return;
        var due = timers.Where(t => !t.Dead && t.Due <= now).OrderBy(t => t.Due).ToList();
        timers.RemoveAll(t => t.Dead || t.Due <= now);
        foreach (var t in due) Guard("timer", t.Act);
    }

    /// <summary>
    /// A fault in one feature must never take the island down: log it, carry on.
    /// </summary>
    public static void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.E("ui", $"{what} failed", ex); }
    }

    public static void Reset() { timers.Clear(); while (queue.TryDequeue(out _)) { } }
}
