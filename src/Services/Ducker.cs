using Isola.App;

namespace Isola.Services;

/// <summary>One audio session the ducker can turn down: its volume (0..1, 0 when muted) read and set.</summary>
public sealed class DuckSession
{
    public string Id = "", Exe = "";
    public Func<float> Get;
    public Action<float> Set;
}

/// <summary>
/// Turns the playing media down while one of the island's chimes or ringtones plays, then gradually back up,
/// the way a phone does under a notification. Only the media app's own sessions move (Windows' volume and
/// every other app stay put), and only by multiplying the volume they had, so it always comes back to exactly
/// where it was.
///
/// Failing open, everywhere: a session someone else moves while it's down (the Sound tab, the app's own
/// slider) is theirs from then on and left alone; a ringtone whose stop is never seen lets go after
/// <see cref="MaxSeconds"/>; quitting puts everything back; and what's down is written to a file first, so
/// after a crash the next start puts it back (<see cref="RestoreLeftovers"/>).
/// </summary>
public sealed class Ducker
{
    /// <summary>The media's volume while a sound plays, of what it was: about 10 dB down, still there.</summary>
    public const float Depth = 0.3f;
    public const double DownSeconds = 0.15, UpSeconds = 0.8, TailSeconds = 0.3, MaxSeconds = 120;
    const float Moved = 0.02f;

    readonly Func<List<DuckSession>> find;
    readonly string stateFile;
    sealed class Held { public DuckSession S; public float Original, Last; }
    readonly List<Held> held = new();
    readonly HashSet<object> loops = new();
    double releaseAt, giveUpAt;
    double rampFrom = 1, rampTo = 1, rampStart, rampLen;
    long timer;

    /// <param name="find">The playing media's audio sessions, right now.</param>
    /// <param name="stateFile">Where what's turned down is written while it is (null: nowhere, in tests).</param>
    public Ducker(Func<List<DuckSession>> find, string stateFile) { this.find = find; this.stateFile = stateFile; }

    public bool Active => held.Count > 0;
    /// <summary>How far down the media is now (1: not at all). The visualizer divides by it, so it doesn't sag.</summary>
    public float Factor { get; private set; } = 1;

    static double Ease(double x) => x * x * (3 - 2 * x);   // slow in, slow out
    static double Now => Ui.Clock.Now;

    float FactorAt(double t) => rampLen <= 0 ? (float)rampTo
        : (float)(rampFrom + (rampTo - rampFrom) * Ease(Math.Clamp((t - rampStart) / rampLen, 0, 1)));

    void RampTo(float to)
    {
        double now = Now;
        float f = FactorAt(now);
        if (Math.Abs(rampTo - to) < 1e-6 && (rampLen <= 0 || now < rampStart + rampLen || Math.Abs(f - to) < 1e-6)) return;   // already going there
        double full = to < f ? DownSeconds : UpSeconds;
        rampFrom = f; rampTo = to; rampStart = now; rampLen = full * Math.Abs(to - f) / (1 - Depth);
    }

    /// <summary>
    /// A sound of <paramref name="seconds"/> is about to play (a loop: until <see cref="Release"/> with its key).
    /// Returns how long to wait before playing it, so the media is already down when it starts; 0 when there's
    /// nothing to turn down. A second sound while one plays extends the dip; it never stacks.
    /// </summary>
    public double Duck(double seconds, object loopKey = null)
    {
        double now = Now;
        if (held.Count == 0)
        {
            List<DuckSession> found;
            try { found = find() ?? new(); } catch (Exception ex) { Log.D("duck", "no sessions: " + ex.Message); return 0; }
            foreach (var s in found)
            {
                float v;
                try { v = s.Get(); } catch { continue; }
                if (v > 0.001f) held.Add(new Held { S = s, Original = v, Last = v });   // muted or at 0: nothing to lower
            }
            if (held.Count == 0) return 0;
            Save();
            giveUpAt = now + MaxSeconds; releaseAt = 0;
            Log.D("duck", $"turning the media down ({held.Count} session(s))");
        }
        RampTo(Depth);
        double wait = Math.Max(0, rampStart + rampLen - now);
        releaseAt = Math.Max(releaseAt, now + wait + seconds + TailSeconds);
        if (loopKey != null) loops.Add(loopKey);
        Schedule();
        return wait;
    }

    /// <summary>A looping sound stopped: the media comes back up after a short tail.</summary>
    public void Release(object loopKey)
    {
        if (loopKey == null || !loops.Remove(loopKey) || held.Count == 0) return;
        releaseAt = Math.Max(releaseAt, Now + TailSeconds);
        Schedule();
    }

    void Schedule() { if (timer == 0) timer = Ui.After(1 / 60.0, Step); }

    void Step()
    {
        timer = 0;
        if (held.Count == 0) { Factor = 1; return; }
        double now = Now;
        if (rampTo < 1 && ((now >= releaseAt && loops.Count == 0) || now >= giveUpAt))
        {
            if (loops.Count > 0) Log.W("duck", $"a looping sound never said it stopped; the media comes back after {MaxSeconds:0} s");
            loops.Clear();
            RampTo(1);
        }
        Factor = FactorAt(now);
        Apply(Factor);
        if (held.Count == 0) { Finish("all moved by someone else"); return; }
        if (rampTo >= 1 && now >= rampStart + rampLen) { RestoreAll("the sound is over"); return; }
        Schedule();
    }

    void Apply(float f)
    {
        for (int i = held.Count - 1; i >= 0; i--)
        {
            var h = held[i];
            float cur;
            try { cur = h.S.Get(); } catch { held.RemoveAt(i); continue; }
            // someone else moved it while it was down: theirs wins, it's left where they put it
            if (Math.Abs(cur - h.Last) > Moved) { Log.D("duck", "a session was moved meanwhile; left as it is"); held.RemoveAt(i); continue; }
            float v = h.Original * f;
            try { h.S.Set(v); h.Last = v; } catch { held.RemoveAt(i); }
        }
    }

    /// <summary>Everything back where it was, now (quitting, the setting turned off).</summary>
    public void RestoreAll(string why)
    {
        foreach (var h in held)
        {
            try { if (Math.Abs(h.S.Get() - h.Last) <= Moved) h.S.Set(h.Original); } catch { }
        }
        if (held.Count > 0) Log.D("duck", "media back up: " + why);
        Finish(why);
    }

    void Finish(string why)
    {
        Ui.Cancel(ref timer);
        held.Clear(); loops.Clear();
        Factor = 1; rampFrom = rampTo = 1; rampLen = 0; releaseAt = 0;
        if (stateFile != null) try { File.Delete(stateFile); } catch { }
    }

    void Save()
    {
        if (stateFile == null) return;
        // once, as it goes down: the original and how low it goes (written per frame it would be a disk write 60 times a second)
        try { File.WriteAllLines(stateFile, held.Select(h => $"{h.Original.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\t{h.S.Exe}\t{h.S.Id}")); }
        catch (Exception ex) { Log.D("duck", "couldn't note what's down: " + ex.Message); }
    }

    /// <summary>
    /// At startup: anything left turned down by a run that ended mid-dip (a crash, power cut) goes back up, by
    /// its session, or by its app when the app restarted meanwhile (Windows keeps an app's volume). Only what
    /// is still somewhere a dip leaves it (between the low point and where it was): a volume moved past that is
    /// someone's choice.
    /// </summary>
    public static int RestoreLeftovers(string stateFile, List<DuckSession> sessions)
    {
        if (stateFile == null || !File.Exists(stateFile)) return 0;
        int n = 0;
        try
        {
            foreach (var line in File.ReadAllLines(stateFile))
            {
                var p = line.Split('\t');
                if (p.Length < 3 || !float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float orig)) continue;
                var matches = sessions.Where(s => s.Id == p[2]).ToList();
                if (matches.Count == 0) matches = sessions.Where(s => s.Exe.Length > 0 && string.Equals(s.Exe, p[1], StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var s in matches)
                {
                    try { float cur = s.Get(); if (cur >= orig * Depth - Moved && cur < orig - 0.001f) { s.Set(orig); n++; } } catch { }
                }
            }
        }
        catch (Exception ex) { Log.D("duck", "leftovers: " + ex.Message); }
        try { File.Delete(stateFile); } catch { }
        if (n > 0) Log.I("duck", $"put {n} session(s) back up, left down by the last run");
        return n;
    }
}
