using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Isola.App;
using Isola.Native;

namespace Isola.Services;

/// <summary>
/// The island's own sounds, synthesized here rather than shipped as files:
/// struck bars, plucked strings, FM bells, glides and soft clicks, through a
/// stereo room (a Freeverb), each one fading to silence rather than stopping,
/// and all matched in loudness. The same synthesizer as tools/sound-lab, where
/// they were chosen by ear. While the island shows notifications, Windows' own
/// chime is off (see WindowsBanners), so these are what you hear.
///
/// Played with PlaySound from memory, asynchronously; looping ones (a call's
/// ringtone, the timer) play until stopped. One sound at a time, as PlaySound
/// allows: a newer one replaces what's playing, which is what a call
/// interrupting a chime should do anyway.
/// </summary>
public static class Sounds
{
    public const string Notification = "notification", Call = "call", Timer = "timer",
        Connect = "connect", Disconnect = "disconnect", Charge = "charge", Low = "low", Notice = "notice";
    public static readonly string[] All = { Notification, Call, Timer, Connect, Disconnect, Charge, Low, Notice };
    public static bool Loops(string kind) => kind is Call or Timer;

    /// <summary>The choices for each kind, as (id, label); the first is the default. Connecting and disconnecting share theirs.</summary>
    public static (string id, string label)[] Choices(string kind) => kind switch
    {
        Notification => new[] { ("bubble", "Bubble"), ("bubble-pair", "Pair"), ("pop", "Pop"), ("duo", "Duo"), ("bloom", "Bloom") },
        Call => new[] { ("classic", "Classic"), ("classic-warm", "Warm"), ("marimba", "Marimba"), ("harp", "Harp"), ("digital", "Digital"), ("pulse", "Pulse") },
        Timer => new[] { ("sweep", "Sweep"), ("sweep-triple", "Triple"), ("steps", "Steps"), ("beacon", "Beacon"), ("clock", "Clock"), ("marimba", "Marimba") },
        Connect or Disconnect => new[] { ("click", "Click"), ("click-soft", "Soft"), ("rise", "Rise"), ("bubble", "Bubble") },
        Charge => new[] { ("click", "Click"), ("spark", "Spark"), ("chord", "Chord"), ("energy", "Energy") },
        Notice => new[] { ("drop", "Drop"), ("tap", "Tap"), ("glass", "Glass"), ("soft", "Soft") },
        _ => new[] { ("click", "Click"), ("fall", "Fall"), ("sink", "Sink"), ("tap", "Tap") },   // low battery
    };

    /// <summary>A choice that exists for the kind, or the default (an unknown id from an old or hand-edited config).</summary>
    public static string Valid(string kind, string id) => Choices(kind).Any(c => c.id == id) ? id : Choices(kind)[0].id;

    internal const int Rate = 48000;
    // Each sound is built once (the synthesizer is the slow part), loudness-matched
    // at full level; a play only scales it and puts the lead-in in front, which
    // takes well under a millisecond. It used to be cached per volume step and
    // lead-in, so connecting headphones or previewing an unpicked sound built
    // one on the spot, on the UI thread, before it could play.
    static readonly Dictionary<string, short[]> pcm = new();
    static GCHandle playing;
    static readonly object gate = new();

    // An idle output dozes: Bluetooth headphones drop the start of a sound while
    // the link wakes (several hundred ms), and laptop speakers power down too. A
    // short chime then came out clipped, or not at all until a second click
    // found the device awake. Short sounds start with a little silence for the
    // wake-up to eat; looping ones don't, so their rhythm stays even.
    //
    // But only an idle output dozes. When something is already playing (music,
    // a video, the previous preview) or Isola played a sound in the last few
    // seconds, the output is awake and a sound starts at once: the lead-in was
    // added to every one-shot before, which made each preview and every
    // notification over music late by 0.15 s, or 0.4 s on Bluetooth.
    internal const double LeadIn = 0.15, LeadInBluetooth = 0.4;
    /// <summary>How long an output stays awake after Isola's last sound ends.</summary>
    internal const double StaysAwake = 3;
    static double awakeUntil = double.NegativeInfinity;

    /// <summary>Whether the default output is a Bluetooth device (set by the app from AudioService).</summary>
    public static Func<bool> OutputIsBluetooth = () => false;
    /// <summary>Whether the default output is playing something right now (its meter; set by the app).</summary>
    public static Func<bool> OutputAwake = () => false;

    /// <summary>The silence a sound starts with: none for a loop or an awake output, else enough for it to wake.</summary>
    internal static double LeadFor(string kind)
    {
        if (Loops(kind) || Ui.Clock.Now < awakeUntil) return 0;
        bool awake = false;
        try { awake = OutputAwake(); } catch { }
        return awake ? 0 : OutputIsBluetooth() ? LeadInBluetooth : LeadIn;
    }

    /// <summary>Forgets that the output was recently in use (tests).</summary>
    internal static void Doze() => awakeUntil = double.NegativeInfinity;

    /// <summary>Where a sound goes: PlaySound from memory, or a stand-in in tests (which mustn't make noise).</summary>
    internal static Func<nint, uint, bool> Output = (wav, flags) => Win32.PlaySoundMem(wav, 0, flags);
    internal static Action Silence = () => Win32.PlaySound(null, 0, 0);

    /// <summary>"kind:variant" of the sound playing, or null.</summary>
    public static string Playing { get; private set; }

    /// <summary>Plays a sound (its chosen <paramref name="variant"/>) at <paramref name="volume"/> (0..1). Never throws.</summary>
    public static void Play(string kind, float volume, string variant = null)
    {
        try
        {
            if (kind == null || volume <= 0.001f) return;
            variant = Valid(kind, variant);
            var samples = Samples(kind, variant);
            double lead = LeadFor(kind);
            byte[] wav = Wav(samples, Math.Clamp(volume, 0, 1), (int)Math.Round(lead * Rate));
            lock (gate)
            {
                Stop();
                // PlaySound reads the memory while it plays: keep it pinned until the next sound
                playing = GCHandle.Alloc(wav, GCHandleType.Pinned);
                uint flags = Win32.SND_MEMORY | Win32.SND_ASYNC | Win32.SND_NODEFAULT | (Loops(kind) ? Win32.SND_LOOP : 0);
                bool ok = Output(playing.AddrOfPinnedObject(), flags);
                if (!ok)
                {
                    // refused (the device was busy switching): once more, a moment later
                    Log.W("sound", $"{kind}: PlaySound refused; trying again");
                    Thread.Sleep(40);
                    ok = Output(playing.AddrOfPinnedObject(), flags);
                }
                Playing = ok ? kind + ":" + variant : null;
                // awake through this sound and a little after (a loop: until it stops, see Stop)
                if (ok) awakeUntil = Loops(kind) ? double.PositiveInfinity : Ui.Clock.Now + lead + samples.Length / 2.0 / Rate + StaysAwake;
            }
            // Failing safe: a looping sound stops by itself after a while, so a
            // missed stop (anywhere) can never leave a ringtone going forever.
            Ui.Cancel(ref loopLimit);
            if (Loops(kind) && Playing != null)
            {
                string mine = Playing;
                loopLimit = Ui.After(MaxLoopSeconds, () => { if (Playing == mine) { Log.W("sound", $"{mine} still looping after {MaxLoopSeconds} s; stopped"); Stop(); } });
            }
        }
        catch (Exception ex) { Log.D("sound", $"{kind}: {ex.Message}"); }
    }

    /// <summary>
    /// Builds a sound ahead of time (off the UI thread), so its first play is
    /// instant: the app warms the chosen ones at startup and when one is picked.
    /// </summary>
    public static void Warm(string kind, string variant)
    {
        try { if (kind != null) Samples(kind, Valid(kind, variant)); }
        catch (Exception ex) { Log.D("sound", $"warming {kind}: {ex.Message}"); }
    }

    /// <summary>Every sound, so each preview in Settings plays the moment it's picked (off the UI thread).</summary>
    public static void WarmAll() { foreach (var k in All) foreach (var (id, _) in Choices(k)) Warm(k, id); }

    /// <summary>Lets go of the sounds not in use (Settings closed): about 10 MB with all of them built.</summary>
    public static void Keep(IEnumerable<(string kind, string variant)> chosen)
    {
        var keep = chosen.Select(c => c.kind + ":" + Valid(c.kind, c.variant)).ToHashSet();
        lock (gate) foreach (var k in pcm.Keys.Where(k => !keep.Contains(k)).ToList()) pcm.Remove(k);
    }

    /// <summary>The longest a ringtone or timer alarm loops (a call's alert gives up at 75 s).</summary>
    internal const double MaxLoopSeconds = 120;
    static long loopLimit;

    public static void Stop()
    {
        lock (gate)
        {
            if (double.IsPositiveInfinity(awakeUntil)) awakeUntil = Ui.Clock.Now + StaysAwake;
            try { Silence(); } catch { }
            if (playing.IsAllocated) playing.Free();
            Playing = null;
        }
    }

    static long previewStop;

    /// <summary>
    /// Settings: hear a sound as it's picked; a looping one plays a few seconds.
    /// Each preview cancels the previous one's stop, which otherwise cut short a
    /// ringtone picked within three seconds of another.
    /// </summary>
    public static void Preview(string kind, float volume, string variant)
    {
        Ui.Cancel(ref previewStop);
        Play(kind, volume, variant);
        if (Loops(kind)) previewStop = Ui.After(3.2, () => { previewStop = 0; Stop(); });
    }

    // ==== the synthesizer ===================================================================
    // A port of tools/sound-lab/synth.mjs: the same voices, the same seeded
    // noise, the same room, so what was chosen there is what plays here.

    sealed class Track
    {
        public double[] L, R;
        public Track(int n) { L = new double[n]; R = new double[n]; }
        public int N => L.Length;
    }
    static Track New(double sec) => new((int)Math.Ceiling(sec * Rate));
    const double Tau = Math.PI * 2;

    static double Hz(int n) => 440 * Math.Pow(2, n / 12.0);
    // note names: semitones from A4
    const int B4 = 2, Cs5 = 4, E5 = 7, Fs5 = 9, Gs5 = 11, A5 = 12, B5 = 14, C6 = 15, Cs6 = 16, D6 = 17, Ds6 = 18, E6 = 19, Fs6 = 21, Gs6 = 23, A6 = 24, B6 = 26, E7 = 31, E4 = -5, G6 = 22;
    const int C5 = 3, G5 = 10, D5 = 5;

    static void Put(Track t, int i, double v, double pan)
    {
        if (i < 0 || i >= t.N) return;
        double a = (pan + 1) * Math.PI / 4;
        t.L[i] += v * Math.Cos(a); t.R[i] += v * Math.Sin(a);
    }

    // deterministic noise, so a sound renders the same every time (the lab's generator, bit for bit)
    sealed class Rng
    {
        uint s;
        public Rng(int seed) { s = (uint)seed; if (s == 0) s = 1; }
        public double Next() { s = unchecked(s * 1664525u + 1013904223u); return s / 4294967296.0 * 2 - 1; }
    }

    // how a voice ends: the last 20 ms fade out, so no note stops dead at the end of its length
    static double Release(int i, int n) { int r = (int)Math.Round(0.02 * Rate); return i > n - r ? Math.Pow(Math.Max(0, (double)(n - i) / r), 2) : 1; }

    /// <summary>A struck bar: a soft thump and decaying partials, higher ones dying first.</summary>
    static void Mallet(Track t, double at, double f, double dur = 1.2, double amp = 0.8, double pan = 0, double bright = 1)
    {
        (double ratio, double a, double decayMul)[] partials = { (1, 1, 1), (3.93, 0.28, 2.4), (9.7, 0.07, 5) };
        int s0 = (int)Math.Round(at * Rate), n = (int)Math.Round(dur * Rate);
        var noise = new Rng((int)Math.Round(f * 7));
        for (int i = 0; i < n; i++)
        {
            double tt = (double)i / Rate, v = 0;
            foreach (var (ratio, a, decayMul) in partials)
            {
                if (f * ratio > Rate / 2.2) continue;
                double env = Math.Exp(-tt * (2.2 + 3.2 * decayMul * bright) / dur * 1.2);
                v += a * env * Math.Sin(Tau * f * ratio * tt);
            }
            double attack = Math.Min(1, tt / 0.0015);
            double thump = Math.Exp(-tt / 0.004) * noise.Next() * 0.05 * bright;
            Put(t, s0 + i, amp * (attack * v + thump) * Release(i, n), pan);
        }
    }

    /// <summary>A plucked string (Karplus-Strong): warm, with a natural decay.</summary>
    static void Pluck(Track t, double at, double f, double dur = 1.6, double amp = 0.7, double pan = 0, double damp = 0.996, double bright = 0.5, int seed = 3)
    {
        int period = Math.Max(2, (int)Math.Round(Rate / f));
        var line = new double[period];
        var noise = new Rng(seed + (int)Math.Round(f));
        double last = 0;
        for (int i = 0; i < period; i++) { double x = noise.Next(); last += (x - last) * (0.15 + 0.8 * bright); line[i] = last; }
        int s0 = (int)Math.Round(at * Rate), n = (int)Math.Round(dur * Rate), idx = 0;
        double prev = 0;
        for (int i = 0; i < n; i++)
        {
            double cur = line[idx];
            line[idx] = damp * 0.5 * (cur + prev);
            prev = cur; idx = (idx + 1) % period;
            double fadeIn = Math.Min(1, i / (Rate * 0.0008));
            Put(t, s0 + i, amp * cur * fadeIn * Release(i, n), pan);
        }
    }

    /// <summary>FM: a carrier whose brightness fades: electric piano, glassy bells.</summary>
    static void Fm(Track t, double at, double f, double dur = 1.2, double amp = 0.6, double pan = 0, double ratio = 3.5, double index = 2.2, double indexDecay = 4, double decay = 3, double attack = 0.002)
    {
        int s0 = (int)Math.Round(at * Rate), n = (int)Math.Round(dur * Rate);
        for (int i = 0; i < n; i++)
        {
            double tt = (double)i / Rate;
            double idx = index * Math.Exp(-tt * indexDecay);
            double env = Math.Min(1, tt / attack) * Math.Exp(-tt * decay);
            Put(t, s0 + i, amp * env * Math.Sin(Tau * f * tt + idx * Math.Sin(Tau * f * ratio * tt)) * Release(i, n), pan);
        }
    }

    /// <summary>A sine gliding from one pitch to another, shaped like a droplet or a sweep.</summary>
    static void Glide(Track t, double at, double f0, double f1, double dur = 0.4, double amp = 0.8, double pan = 0, double curve = 1.6, double decay = 7, double attack = 0.004, double harmonic = 0.12)
    {
        int s0 = (int)Math.Round(at * Rate), n = (int)Math.Round(dur * Rate);
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double tt = (double)i / Rate, k = Math.Min(1, tt / dur * curve);
            double f = f0 * Math.Pow(f1 / f0, 1 - (1 - k) * (1 - k));
            ph += Tau * f / Rate;
            double env = Math.Min(1, tt / attack) * Math.Exp(-tt * decay) * Math.Min(1, (dur - tt) / 0.02);
            Put(t, s0 + i, amp * env * (Math.Sin(ph) + harmonic * Math.Sin(2 * ph)), pan);
        }
    }

    /// <summary>A soft tone with a few odd harmonics (a rounded square): rings, beeps.</summary>
    static void Tone(Track t, double at, double f, double dur = 0.1, double amp = 0.5, double pan = 0, double attack = 0.004, double release = 0.012, int odd = 4)
    {
        int s0 = (int)Math.Round(at * Rate), n = (int)Math.Round(dur * Rate);
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double tt = (double)i / Rate;
            ph += Tau * f / Rate;
            double v = 0;
            for (int h = 1; h <= odd * 2 - 1; h += 2) if (f * h < Rate / 2.4) v += Math.Sin(h * ph) / (h * h * 0.8 + 0.2 * h);
            double env = Math.Min(1, Math.Min(tt / attack, (dur - tt) / release));
            Put(t, s0 + i, amp * Math.Max(0, env) * v, pan);
        }
    }

    /// <summary>A tiny click: a short filtered transient with a little body.</summary>
    static void Click(Track t, double at, double amp = 0.6, double pan = 0, double pitch = 2400, double body = 900, double len = 0.012, int seed = 7)
    {
        int s0 = (int)Math.Round(at * Rate), n = (int)Math.Round(len * 6 * Rate);
        var noise = new Rng(seed);
        double lp = 0, a = 1 - Math.Exp(-Tau * pitch / Rate);
        for (int i = 0; i < n; i++)
        {
            double tt = (double)i / Rate;
            lp += (noise.Next() - lp) * a;
            double env = Math.Exp(-tt / (len * 0.35));
            double knock = Math.Sin(Tau * body * tt) * Math.Exp(-tt / (len * 0.9));
            Put(t, s0 + i, amp * (lp * env * 0.9 + knock * 0.55), pan);
        }
    }

    /// <summary>A short noise breath (airy layers).</summary>
    static void Air(Track t, double at, double dur = 0.5, double amp = 0.15, double pan = 0, double cutoff = 3000, int seed = 11)
    {
        int s0 = (int)Math.Round(at * Rate), n = (int)Math.Round(dur * Rate);
        var noise = new Rng(seed);
        double lp = 0, a = 1 - Math.Exp(-Tau * cutoff / Rate);
        for (int i = 0; i < n; i++)
        {
            double tt = (double)i / Rate;
            lp += (noise.Next() - lp) * a;
            double env = Math.Pow(Math.Sin(Math.PI * Math.Min(1, tt / dur)), 2);
            Put(t, s0 + i, amp * lp * env, pan);
        }
    }

    // ---- the room: Freeverb (8 combs, 4 all-passes a side, the right side's delays longer for width)
    static double[] Freeverb(double[] input, int side, double size, double damp)
    {
        int[] combs = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 }, alls = { 556, 441, 341, 225 };
        double scale = Rate / 44100.0;
        const int spread = 23;
        var cb = combs.Select(l => new double[(int)Math.Round((l + side * spread) * scale)]).ToArray();
        var ab = alls.Select(l => new double[(int)Math.Round((l + side * spread) * scale)]).ToArray();
        var ci = new int[cb.Length]; var cs = new double[cb.Length]; var ai = new int[ab.Length];
        var o = new double[input.Length];
        for (int n = 0; n < input.Length; n++)
        {
            double x = input[n] * 0.015, y = 0;
            for (int k = 0; k < cb.Length; k++)
            {
                double outv = cb[k][ci[k]];
                cs[k] = outv * (1 - damp) + cs[k] * damp;
                cb[k][ci[k]] = x + cs[k] * size;
                ci[k] = (ci[k] + 1) % cb[k].Length;
                y += outv;
            }
            for (int k = 0; k < ab.Length; k++)
            {
                double b = ab[k][ai[k]];
                ab[k][ai[k]] = y + b * 0.5;
                ai[k] = (ai[k] + 1) % ab[k].Length;
                y = b - y;
            }
            o[n] = y;
        }
        return o;
    }

    static void Room(Track t, double wet, double size = 0.78, double damp = 0.35)
    {
        var mono = new double[t.N];
        for (int i = 0; i < t.N; i++) mono[i] = (t.L[i] + t.R[i]) / 2;
        var wl = Freeverb(mono, 0, size, damp); var wr = Freeverb(mono, 1, size, damp);
        for (int i = 0; i < t.N; i++) { t.L[i] = t.L[i] * (1 - wet * 0.5) + wl[i] * wet * 3; t.R[i] = t.R[i] * (1 - wet * 0.5) + wr[i] * wet * 3; }
    }

    /// <summary>High-pass (no rumble or DC) then a gentle low-pass to soften any edge.</summary>
    static void Polish(Track t, double hp = 60, double lpf = 14000)
    {
        foreach (var ch in new[] { t.L, t.R })
        {
            double x1 = 0, y1 = 0, l = 0, r = Math.Exp(-Tau * hp / Rate), a = 1 - Math.Exp(-Tau * lpf / Rate);
            for (int i = 0; i < ch.Length; i++) { double y = r * (y1 + ch[i] - x1); x1 = ch[i]; y1 = y; l += (y - l) * a; ch[i] = l; }
        }
    }

    /// <summary>Cuts trailing silence once the tail is truly gone (-72 dB), then fades the last 40 ms: nothing ends abruptly.</summary>
    static Track Trim(Track t, double floorDb = -72, double fadeMs = 40)
    {
        double thr = Math.Pow(10, floorDb / 20);
        int end = t.N - 1;
        while (end > 0 && Math.Abs(t.L[end]) < thr && Math.Abs(t.R[end]) < thr) end--;
        int f = (int)Math.Round(Rate * fadeMs / 1000), len = Math.Min(t.N, end + f);
        var o = new Track(len);
        Array.Copy(t.L, o.L, len); Array.Copy(t.R, o.R, len);
        for (int i = 0; i < f && i < len; i++) { double g = Math.Pow((double)i / f, 2); o.L[len - 1 - i] *= g; o.R[len - 1 - i] *= g; }
        return o;
    }

    /// <summary>A one-shot: drawn, put in the room, finished.</summary>
    static Track Shot(double sec, Action<Track, double> draw, double wet)
    {
        var t = New(sec + 2.5);
        draw(t, 0);   // from the first sample: no silence of its own before the sound
        Room(t, wet); Polish(t);
        return Trim(t);
    }

    /// <summary>A seamless loop: three repeats rendered, the middle kept, so its start carries the tail of the one before.</summary>
    static Track Loop(double period, Action<Track, double> draw, double wet)
    {
        var t = New(period * 3 + 3);
        for (int k = 0; k < 3; k++) draw(t, k * period);
        Room(t, wet); Polish(t);
        int s = (int)Math.Round(period * Rate);
        var o = new Track(s);
        Array.Copy(t.L, s, o.L, 0, s); Array.Copy(t.R, s, o.R, 0, s);
        return o;
    }

    // ---- loudness: every sound as loud as the others ------------------------------------------
    // Peak level says little about how loud something sounds: a held tone and a
    // click peaking alike are far apart to the ear. So each sound is measured
    // as a loudness meter does (ITU-R BS.1770: weighted toward the frequencies
    // the ear favors, over short windows) and set to the same level. A ceiling
    // keeps the peak from clipping, which only ever makes a sound quieter.

    /// <summary>The loudest moment of a sound: BS.1770 K-weighted, in 100 ms windows (LUFS-like dB).</summary>
    internal static double Loudness(double[] l, double[] r)
    {
        double[] K(double[] x)
        {
            // the two BS.1770 stages at 48 kHz: a high shelf, then a high-pass
            var y = Biquad(x, 1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);
            return Biquad(y, 1, -2, 1, -1.99004745483398, 0.99007225036621);
        }
        var kl = K(l); var kr = K(r);
        int win = Rate / 10, step = Rate / 40;
        double best = 1e-12;
        for (int s = 0; s + win <= kl.Length || s == 0; s += step)
        {
            int e = Math.Min(kl.Length, s + win);
            double sum = 0;
            for (int i = s; i < e; i++) sum += kl[i] * kl[i] + kr[i] * kr[i];
            best = Math.Max(best, sum / win);
            if (e == kl.Length) break;
        }
        return -0.691 + 10 * Math.Log10(best);
    }
    static double[] Biquad(double[] x, double b0, double b1, double b2, double a1, double a2)
    {
        var y = new double[x.Length]; double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++) { double v = b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2; x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = v; }
        return y;
    }

    /// <summary>The loudness every sound is set to at full volume, and the ceiling no peak goes over.</summary>
    internal const double TargetLoudness = -15, Ceiling = 0.9;
    /// <summary>How many sounds have been built (tests: warming means the first play builds nothing).</summary>
    internal static int Built;

    /// <summary>The sound as a 16-bit stereo WAV at <paramref name="volume"/> (0..1), loudness-matched. Public for tests.</summary>
    public static byte[] Render(string kind, float volume, string variant = null, double leadIn = 0) =>
        Wav(Samples(kind, Valid(kind, variant)), Math.Clamp(volume, 0, 1), (int)Math.Round(leadIn * Rate));

    /// <summary>A sound's samples, interleaved stereo, loudness-matched at full level: built the first time, then kept.</summary>
    static short[] Samples(string kind, string variant)
    {
        string key = kind + ":" + variant;
        lock (gate) if (pcm.TryGetValue(key, out var have)) return have;
        var made = Build(kind, variant);   // outside the lock: building takes tens of milliseconds
        lock (gate) return pcm.TryAdd(key, made) ? made : pcm[key];
    }

    static short[] Build(string kind, string variant)
    {
        Interlocked.Increment(ref Built);
        var t = Draw(kind, variant);
        double loud = Loudness(t.L, t.R), peak = 0;
        for (int i = 0; i < t.N; i++) peak = Math.Max(peak, Math.Max(Math.Abs(t.L[i]), Math.Abs(t.R[i])));
        // notices (a status, nothing to answer) sit 5 dB under the rest: there, not calling for attention
        double target = TargetLoudness - (kind == Notice ? 5 : 0);
        double gain = peak > 0 ? Math.Min(Math.Pow(10, (target - loud) / 20), Ceiling / peak) : 0;
        var s = new short[t.N * 2];
        for (int i = 0; i < t.N; i++)
        {
            s[2 * i] = (short)Math.Clamp(Math.Round(t.L[i] * gain * 32767), -32767, 32767);
            s[2 * i + 1] = (short)Math.Clamp(Math.Round(t.R[i] * gain * 32767), -32767, 32767);
        }
        return s;
    }

    /// <summary>
    /// How loud a sound comes out at full volume, measured on the WAV itself
    /// (what PlaySound gets), and whether its peak held it under the target. For tests.
    /// </summary>
    internal static (double loudness, bool atCeiling) Measure(string kind, string variant)
    {
        var (l, r) = Decode(Render(kind, 1f, variant));
        double peak = 0;
        for (int i = 0; i < l.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        return (Loudness(l, r), peak >= Ceiling - 0.002);
    }

    /// <summary>The two channels of one of these WAVs, as -1..1 (tests).</summary>
    internal static (double[] l, double[] r) Decode(byte[] wav)
    {
        int frames = (wav.Length - 44) / 4;
        var l = new double[frames]; var r = new double[frames];
        for (int f = 0; f < frames; f++) { l[f] = BitConverter.ToInt16(wav, 44 + f * 4) / 32767.0; r[f] = BitConverter.ToInt16(wav, 46 + f * 4) / 32767.0; }
        return (l, r);
    }

    static Track Draw(string kind, string v) => (kind, v) switch
    {
        // ---- notifications
        (Notification, "bubble") => Shot(0.6, (t, a) =>
        {
            Glide(t, a, Hz(E5), Hz(E6 + 0), dur: 0.32, amp: 0.85, decay: 8, curve: 1.8, harmonic: 0.08);
            Glide(t, a + 0.012, Hz(B5), Hz(B6), dur: 0.18, amp: 0.18, decay: 14, pan: 0.2);
        }, 0.14),
        (Notification, "bubble-pair") => Shot(0.7, (t, a) =>
        {
            Glide(t, a, Hz(Cs5), Hz(Cs6), dur: 0.22, amp: 0.8, decay: 11, curve: 2);
            Glide(t, a + 0.13, Hz(Gs5), Hz(Gs5 + 12), dur: 0.24, amp: 0.75, decay: 10, curve: 2, pan: 0.15);
        }, 0.16),
        (Notification, "pop") => Shot(0.8, (t, a) =>
        {
            Mallet(t, a, Hz(A6), dur: 0.7, amp: 0.9, bright: 0.7);
            Click(t, a, amp: 0.15, pitch: 5000, body: 1800, len: 0.006);
        }, 0.12),
        (Notification, "duo") => Shot(1.0, (t, a) =>
        {
            Mallet(t, a, Hz(B5), dur: 0.9, amp: 0.7, pan: -0.15);
            Mallet(t, a + 0.1, Hz(E6), dur: 1.0, amp: 0.75, pan: 0.15);
        }, 0.18),
        // a soft chord that swells in; held longer than the lab's first try, which ended too soon
        (Notification, "bloom") => Shot(2.2, (t, a) =>
        {
            int[] notes = { E5, Gs5, B5, Ds6 };
            for (int i = 0; i < notes.Length; i++) Fm(t, a + i * 0.025, Hz(notes[i]), dur: 2.0, amp: 0.3, ratio: 1, index: 0.6, indexDecay: 2, decay: 1.8, attack: 0.05, pan: -0.3 + i * 0.2);
            Air(t, a, dur: 0.5, amp: 0.05, cutoff: 5000);
        }, 0.25),

        // ---- ringtones (looped)
        (Call, "classic") => Loop(3.0, (t, a) =>
        {
            for (int b = 0; b < 2; b++) for (int i = 0; i < 12; i++)
                Tone(t, a + b * 0.55 + i * 0.038, i % 2 == 1 ? Hz(Cs6) : Hz(E6), dur: 0.034, amp: 0.4, attack: 0.003, release: 0.008, odd: 3);
        }, 0.12),
        (Call, "classic-warm") => Loop(3.0, (t, a) =>
        {
            for (int b = 0; b < 2; b++) for (int i = 0; i < 12; i++)
                Tone(t, a + b * 0.55 + i * 0.04, i % 2 == 1 ? Hz(Gs5) : Hz(B5), dur: 0.036, amp: 0.45, attack: 0.004, release: 0.01, odd: 2);
        }, 0.14),
        (Call, "marimba") => Loop(2.8, (t, a) =>
        {
            int[] notes = { E5, B5, Gs5, E6, B5, Gs5, Fs5, Cs6, A5, Fs6 };
            double[] at = { 0, 0.13, 0.26, 0.39, 0.52, 0.65, 1.1, 1.23, 1.36, 1.49 };
            for (int i = 0; i < notes.Length; i++) Mallet(t, a + at[i], Hz(notes[i]), dur: 0.7, amp: 0.7, pan: (i % 3 - 1) * 0.25);
        }, 0.15),
        (Call, "harp") => Loop(3.0, (t, a) =>
        {
            int[] notes = { C5, E5, G5, B5, D6, E6, G6, B6 };
            for (int i = 0; i < notes.Length; i++) Pluck(t, a + i * 0.075, Hz(notes[i]), dur: 1.6, amp: 0.55, bright: 0.55, damp: 0.998, pan: -0.4 + i * 0.11, seed: i + 5);
        }, 0.25),
        (Call, "digital") => Loop(2.6, (t, a) =>
        {
            int[] notes = { B5, Ds6, Fs6 };
            for (int r = 0; r < 2; r++) for (int i = 0; i < 3; i++) Fm(t, a + r * 0.5 + i * 0.075, Hz(notes[i]), dur: 0.25, amp: 0.55, ratio: 2, index: 1.2, indexDecay: 8, decay: 12);
        }, 0.12),
        (Call, "pulse") => Loop(2.4, (t, a) =>
        {
            for (int r = 0; r < 2; r++) for (int i = 0; i < 3; i++) Tone(t, a + r * 0.9 + i * 0.22, Hz(B5 + i * 2), dur: 0.16, amp: 0.45, attack: 0.02, release: 0.06, odd: 2);
        }, 0.16),

        // ---- timer alarms (looped)
        (Timer, "sweep") => Loop(1.6, (t, a) =>
        {
            for (int i = 0; i < 2; i++) Glide(t, a + i * 0.55, Hz(E5), Hz(B6), dur: 0.5, amp: 0.8, decay: 3, curve: 1.2, harmonic: 0.1);
        }, 0.18),
        (Timer, "sweep-triple") => Loop(1.7, (t, a) =>
        {
            for (int i = 0; i < 3; i++) Glide(t, a + i * 0.32, Hz(Gs5), Hz(E7), dur: 0.3, amp: 0.75, decay: 5, curve: 1.3);
        }, 0.15),
        (Timer, "steps") => Loop(1.8, (t, a) =>
        {
            int[] notes = { E6, Gs6, B6, E7 };
            for (int i = 0; i < notes.Length; i++) Mallet(t, a + i * 0.12, Hz(notes[i]), dur: 0.8, amp: 0.7, pan: -0.3 + i * 0.2);
        }, 0.18),
        (Timer, "beacon") => Loop(2.0, (t, a) =>
        {
            for (int i = 0; i < 2; i++) Fm(t, a + i * 0.45, Hz(A6), dur: 0.42, amp: 0.55, ratio: 1, index: 0.4, decay: 6, attack: 0.03);
        }, 0.2),
        (Timer, "clock") => Loop(1.5, (t, a) =>
        {
            for (int i = 0; i < 4; i++) Tone(t, a + i * 0.15, Hz(A6), dur: 0.085, amp: 0.45, attack: 0.004, release: 0.02, odd: 3);
        }, 0.1),
        (Timer, "marimba") => Loop(1.7, (t, a) =>
        {
            int[] notes = { E6, B5, E6, B5, Gs5, B5 };
            for (int i = 0; i < notes.Length; i++) Mallet(t, a + i * 0.12, Hz(notes[i]), dur: 0.6, amp: 0.7, pan: i % 2 == 1 ? 0.2 : -0.2);
        }, 0.15),

        // ---- a device connects (rising) or disconnects (the same, falling)
        (Notice, "drop") => Shot(0.5, (t, a) => Glide(t, a, Hz(B5), Hz(E5), dur: 0.18, amp: 0.7, decay: 14, curve: 1.4, harmonic: 0.04), 0.12),
        (Notice, "tap") => Shot(0.35, (t, a) => Click(t, a, amp: 0.55, pitch: 1600, body: Hz(A5), len: 0.016), 0.08),
        (Notice, "glass") => Shot(0.7, (t, a) => Mallet(t, a, Hz(E6), dur: 0.6, amp: 0.5, bright: 0.4), 0.15),
        (Notice, "soft") => Shot(0.7, (t, a) => { Mallet(t, a, Hz(C6), dur: 0.5, amp: 0.45, bright: 0.3); Mallet(t, a + 0.08, Hz(G6), dur: 0.5, amp: 0.35, bright: 0.3); }, 0.15),
        (Connect, "click") => Shot(0.35, (t, a) => { Click(t, a, amp: 0.6, pitch: 3000, body: Hz(E6), len: 0.01); Click(t, a + 0.085, amp: 0.7, pitch: 3800, body: Hz(B6), len: 0.01, seed: 9); }, 0.08),
        (Disconnect, "click") => Shot(0.35, (t, a) => { Click(t, a, amp: 0.7, pitch: 3800, body: Hz(B6), len: 0.01, seed: 9); Click(t, a + 0.085, amp: 0.6, pitch: 3000, body: Hz(E6), len: 0.01); }, 0.08),
        (Connect, "click-soft") => Shot(0.35, (t, a) => { Click(t, a, amp: 0.5, pitch: 1800, body: Hz(E5), len: 0.014); Click(t, a + 0.09, amp: 0.55, pitch: 2200, body: Hz(B5), len: 0.014, seed: 9); }, 0.08),
        (Disconnect, "click-soft") => Shot(0.35, (t, a) => { Click(t, a, amp: 0.55, pitch: 2200, body: Hz(B5), len: 0.014, seed: 9); Click(t, a + 0.09, amp: 0.5, pitch: 1800, body: Hz(E5), len: 0.014); }, 0.08),
        (Connect, "rise") => Shot(0.8, (t, a) => { Mallet(t, a, Hz(E5), dur: 0.6, amp: 0.75); Mallet(t, a + 0.09, Hz(B5), dur: 0.7, amp: 0.8, pan: 0.15); }, 0.15),
        (Disconnect, "rise") => Shot(0.8, (t, a) => { Mallet(t, a, Hz(B5), dur: 0.6, amp: 0.8, pan: 0.15); Mallet(t, a + 0.09, Hz(E5), dur: 0.7, amp: 0.75); }, 0.15),
        (Connect, "bubble") => Shot(0.5, (t, a) => Glide(t, a, Hz(B4), Hz(B5), dur: 0.25, amp: 0.85, decay: 10, curve: 2), 0.12),
        (Disconnect, "bubble") => Shot(0.5, (t, a) => Glide(t, a, Hz(B5), Hz(B4), dur: 0.28, amp: 0.85, decay: 10, curve: 2), 0.12),

        // ---- the charger
        (Charge, "click") => Shot(0.4, (t, a) =>
        {
            int[] notes = { E6, B6, E7 };
            for (int i = 0; i < 3; i++) Click(t, a + i * 0.035, amp: 0.55 - i * 0.08, pitch: 3200 + i * 600, body: Hz(notes[i]), len: 0.009, seed: i + 3);
        }, 0.1),
        (Charge, "spark") => Shot(0.5, (t, a) => { Glide(t, a, Hz(E5), Hz(E7), dur: 0.16, amp: 0.7, decay: 9, curve: 1.4); Click(t, a, amp: 0.2, pitch: 6000, body: 2500, len: 0.005); }, 0.14),
        (Charge, "chord") => Shot(0.9, (t, a) =>
        {
            int[] notes = { E5, B5, E6 };
            for (int i = 0; i < 3; i++) Mallet(t, a + i * 0.018, Hz(notes[i]), dur: 0.7, amp: 0.6 - i * 0.1, pan: -0.2 + i * 0.2);
        }, 0.16),
        (Charge, "energy") => Shot(0.8, (t, a) => { Air(t, a, dur: 0.25, amp: 0.12, cutoff: 6000); Glide(t, a + 0.08, Hz(B4), Hz(B5), dur: 0.4, amp: 0.6, decay: 4, attack: 0.03, curve: 1.2); }, 0.18),

        // ---- low battery
        (Low, "click") => Shot(0.5, (t, a) => { Click(t, a, amp: 0.6, pitch: 2800, body: Hz(E6), len: 0.012); Click(t, a + 0.18, amp: 0.6, pitch: 2000, body: Hz(B5), len: 0.012, seed: 4); }, 0.1),
        (Low, "fall") => Shot(1.0, (t, a) => { Mallet(t, a, Hz(E5), dur: 0.7, amp: 0.8); Mallet(t, a + 0.18, Hz(B4), dur: 0.8, amp: 0.8, pan: -0.15); }, 0.16),
        (Low, "sink") => Shot(0.8, (t, a) => Glide(t, a, Hz(E5), Hz(E4), dur: 0.5, amp: 0.75, decay: 4, curve: 1, attack: 0.01), 0.15),
        (Low, "tap") => Shot(0.6, (t, a) =>
        {
            for (int i = 0; i < 2; i++) { Click(t, a + i * 0.16, amp: 0.6, pitch: 1500, body: Hz(E4), len: 0.02, seed: i + 8); Mallet(t, a + i * 0.16, Hz(E5), dur: 0.3, amp: 0.3); }
        }, 0.1),
        _ => throw new ArgumentException($"no such sound: {kind}/{v}"),
    };

    /// <summary>16-bit stereo PCM, the sound scaled by <paramref name="gain"/>, after <paramref name="silence"/> silent frames.</summary>
    static byte[] Wav(short[] s, double volume, int silence = 0)
    {
        int data = (silence * 2 + s.Length) * 2;
        var b = new byte[44 + data];
        var h = b.AsSpan();
        "RIFF"u8.CopyTo(h); BinaryPrimitives.WriteInt32LittleEndian(h[4..], 36 + data); "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]); BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16); BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1); BinaryPrimitives.WriteInt16LittleEndian(h[22..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], Rate); BinaryPrimitives.WriteInt32LittleEndian(h[28..], Rate * 4); BinaryPrimitives.WriteInt16LittleEndian(h[32..], 4); BinaryPrimitives.WriteInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]); BinaryPrimitives.WriteInt32LittleEndian(h[40..], data);
        var outp = MemoryMarshal.Cast<byte, short>(h[(44 + silence * 4)..]);   // the silence is already zeros
        if (volume >= 0.9999) s.CopyTo(outp);
        else for (int i = 0; i < s.Length; i++) outp[i] = (short)Math.Round(s[i] * volume);
        return b;
    }
}
