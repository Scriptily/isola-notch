using System.Runtime.InteropServices;
using Isola.App;
using Isola.Native;

namespace Isola.Services;

/// <summary>
/// The island's own sounds, synthesized here rather than shipped as files or
/// borrowed from a phone: soft mallet notes (a sine with a quieter, faster
/// fading overtone, like a marimba) and a glassier bell for the timer. While
/// the island shows notifications, Windows' own chime is off (see
/// WindowsBanners), so these are what you hear.
///
/// Played with PlaySound from memory, asynchronously; looping ones (a call's
/// ringtone, the timer) play until stopped. One sound at a time, as PlaySound
/// allows: a newer one replaces what's playing, which is what a call
/// interrupting a chime should do anyway.
/// </summary>
public static class Sounds
{
    public const string Notification = "notification", Call = "call", Timer = "timer",
        Connect = "connect", Disconnect = "disconnect", Charge = "charge", Low = "low";
    public static readonly string[] All = { Notification, Call, Timer, Connect, Disconnect, Charge, Low };
    public static bool Loops(string kind) => kind is Call or Timer;

    /// <summary>The choices for each kind, as (id, label); the first is the default.</summary>
    public static (string id, string label)[] Choices(string kind) => kind switch
    {
        Notification => new[] { ("glass", "Glass"), ("pop", "Pop"), ("chord", "Chord"), ("bubble", "Bubble"), ("bell", "Bell") },
        Call => new[] { ("marimba", "Marimba"), ("classic", "Classic"), ("pulse", "Pulse"), ("bells", "Bells"), ("rise", "Rise") },
        Timer => new[] { ("bells", "Bells"), ("beeps", "Beeps"), ("marimba", "Marimba"), ("sweep", "Sweep") },
        _ => new[] { ("soft", "Soft"), ("click", "Click"), ("glass", "Glass") },   // devices and the charger
    };

    /// <summary>A choice that exists for the kind, or the default (an unknown id from an old or hand-edited config).</summary>
    public static string Valid(string kind, string id) => Choices(kind).Any(c => c.id == id) ? id : Choices(kind)[0].id;

    const int Rate = 44100;
    static readonly Dictionary<(string, int), byte[]> cache = new();

    /// <summary>A sine whose pitch glides from one note to another: the "bubble" and "sweep".</summary>
    static void Glide(float[] buf, double at, double f0, double f1, double dur, double amp)
    {
        int s0 = (int)(at * Rate), n = (int)(dur * Rate);
        double phase = 0;
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate, k = t / dur;
            double f = f0 * Math.Pow(f1 / f0, Math.Min(1, k * 1.6));
            phase += 2 * Math.PI * f / Rate;
            double env = Math.Min(1, t / 0.006) * Math.Exp(-t * 9);
            buf[s0 + i] += (float)(amp * env * Math.Sin(phase));
        }
    }

    /// <summary>A short, soft-edged electronic beep (alarm clocks, the "classic" ring).</summary>
    static void Beep(float[] buf, double at, double f, double dur, double amp)
    {
        int s0 = (int)(at * Rate), n = (int)(dur * Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Min(1, t / 0.005) * Math.Min(1, (dur - t) / 0.01);
            double v = Math.Sin(2 * Math.PI * f * t) + 0.25 * Math.Sin(2 * Math.PI * f * 2 * t);
            buf[s0 + i] += (float)(amp * env * v);
        }
    }
    static GCHandle playing;
    static readonly object gate = new();

    /// <summary>Plays a sound (its chosen <paramref name="variant"/>) at <paramref name="volume"/> (0..1). Never throws.</summary>
    public static void Play(string kind, float volume, string variant = null)
    {
        try
        {
            if (kind == null || volume <= 0.001f) return;
            variant = Valid(kind, variant);
            int vkey = (int)Math.Round(Math.Clamp(volume, 0, 1) * 20);   // cached in 5% steps
            byte[] wav;
            lock (gate)
            {
                if (!cache.TryGetValue((kind + ":" + variant, vkey), out wav)) cache[(kind + ":" + variant, vkey)] = wav = Render(kind, vkey / 20f, variant);
                Stop();
                // PlaySound reads the memory while it plays: keep it pinned until the next sound
                playing = GCHandle.Alloc(wav, GCHandleType.Pinned);
                uint flags = Win32.SND_MEMORY | Win32.SND_ASYNC | Win32.SND_NODEFAULT | (Loops(kind) ? Win32.SND_LOOP : 0);
                Win32.PlaySoundMem(playing.AddrOfPinnedObject(), 0, flags);
            }
        }
        catch (Exception ex) { Log.D("sound", $"{kind}: {ex.Message}"); }
    }

    public static void Stop()
    {
        lock (gate)
        {
            try { Win32.PlaySound(null, 0, 0); } catch { }
            if (playing.IsAllocated) playing.Free();
        }
    }

    // ---- synthesis ------------------------------------------------------------

    static double Hz(int semitonesFromA4) => 440 * Math.Pow(2, semitonesFromA4 / 12.0);
    // note names: semitones from A4
    const int C5 = 3, D5 = 5, E5 = 7, Fs5 = 9, G5 = 10, A5 = 12, B5 = 14, C6 = 15, D6 = 17, E6 = 19, Fs6 = 21, G6 = 22, A6 = 24, B6 = 26, E4 = -5, B4 = 2, Gs5 = 11, Cs6 = 16;

    /// <summary>A mallet note: fundamental plus the 4th partial (a marimba's), soft attack, exponential fade.</summary>
    static void Mallet(float[] buf, double at, int note, double dur, double amp)
    {
        double f = Hz(note);
        int s0 = (int)(at * Rate), n = (int)(dur * Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Min(1, t / 0.004) * Math.Exp(-t * 7.5);
            double v = Math.Sin(2 * Math.PI * f * t) + 0.22 * Math.Exp(-t * 22) * Math.Sin(2 * Math.PI * f * 4 * t) + 0.08 * Math.Exp(-t * 40) * Math.Sin(2 * Math.PI * f * 9.8 * t);
            buf[s0 + i] += (float)(amp * env * v);
        }
    }

    /// <summary>A bell: inharmonic partials that fade at their own rates, for the timer.</summary>
    static void Bell(float[] buf, double at, int note, double dur, double amp)
    {
        double f = Hz(note);
        (double ratio, double a, double decay)[] partials = { (1, 1, 3.2), (2.0, 0.45, 4.5), (2.76, 0.35, 6), (5.4, 0.15, 9), (8.93, 0.06, 14) };
        int s0 = (int)(at * Rate), n = (int)(dur * Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate, v = 0;
            foreach (var (r, a, d) in partials) v += a * Math.Exp(-t * d) * Math.Sin(2 * Math.PI * f * r * t);
            buf[s0 + i] += (float)(amp * Math.Min(1, t / 0.003) * v);
        }
    }

    /// <summary>The sound as a 16-bit mono WAV, peaking at <paramref name="volume"/> of full scale. Public for tests.</summary>
    public static byte[] Render(string kind, float volume, string variant = null)
    {
        variant = Valid(kind, variant);
        float[] buf;
        switch (kind, variant)
        {
            // ---- notifications
            case (Notification, "glass"):   // a light rising two-note chime
                buf = new float[(int)(0.75 * Rate)];
                Mallet(buf, 0.00, B5, 0.7, 0.8); Mallet(buf, 0.11, E6, 0.64, 0.9);
                break;
            case (Notification, "pop"):     // one short, high pluck
                buf = new float[(int)(0.4 * Rate)];
                Mallet(buf, 0, A6, 0.38, 1);
                break;
            case (Notification, "chord"):   // three notes rolled upward
                buf = new float[(int)(0.8 * Rate)];
                Mallet(buf, 0, E5, 0.75, 0.7); Mallet(buf, 0.05, Gs5, 0.72, 0.7); Mallet(buf, 0.10, B5, 0.7, 0.8);
                break;
            case (Notification, "bubble"):  // a quick upward glide
                buf = new float[(int)(0.45 * Rate)];
                Glide(buf, 0, Hz(E5), Hz(E6), 0.42, 1);
                break;
            case (Notification, "bell"):    // a single small bell
                buf = new float[(int)(1.0 * Rate)];
                Bell(buf, 0, B5, 1.0, 0.8);
                break;
            // ---- ringtones (looped)
            case (Call, "marimba"):         // a phrase and its answer, then a breath
                buf = new float[(int)(2.6 * Rate)];
                int[] phrase = { E5, Gs5, B5, E6, B5, Gs5 };
                for (int i = 0; i < phrase.Length; i++) Mallet(buf, i * 0.13, phrase[i], 0.6, 0.85);
                int[] answer = { Fs5, A5, Cs6, Fs6 };
                for (int i = 0; i < answer.Length; i++) Mallet(buf, 1.0 + i * 0.13, answer[i], 0.6, 0.85);
                break;
            case (Call, "classic"):         // an old phone's double trill
                buf = new float[(int)(3.0 * Rate)];
                for (int burst = 0; burst < 2; burst++)
                    for (int i = 0; i < 10; i++) Beep(buf, burst * 0.5 + i * 0.04, i % 2 == 0 ? Hz(E6) : Hz(Cs6), 0.035, 0.7);
                break;
            case (Call, "pulse"):           // soft pulses, rising in threes
                buf = new float[(int)(2.4 * Rate)];
                for (int i = 0; i < 3; i++) { Beep(buf, i * 0.22, Hz(B5 + i * 2), 0.16, 0.6); Beep(buf, 0.9 + i * 0.22, Hz(B5 + i * 2), 0.16, 0.6); }
                break;
            case (Call, "bells"):           // a bell arpeggio
                buf = new float[(int)(2.6 * Rate)];
                int[] bells = { E6, B5, Gs5, B5, E6, Gs5 };
                for (int i = 0; i < bells.Length; i++) Bell(buf, i * 0.18, bells[i], 1.0, 0.6);
                break;
            case (Call, "rise"):            // climbing, then a pause
                buf = new float[(int)(2.4 * Rate)];
                int[] rise = { C5, E5, G5, C6, E6, G6 };
                for (int i = 0; i < rise.Length; i++) Mallet(buf, i * 0.1, rise[i], 0.55, 0.85);
                break;
            // ---- timer (looped)
            case (Timer, "bells"):          // three bright bells, then quiet
                buf = new float[(int)(1.8 * Rate)];
                Bell(buf, 0.00, E6, 0.9, 0.7); Bell(buf, 0.22, E6, 0.9, 0.7); Bell(buf, 0.44, B6, 1.2, 0.75);
                break;
            case (Timer, "beeps"):          // an alarm clock's four beeps
                buf = new float[(int)(1.4 * Rate)];
                for (int i = 0; i < 4; i++) Beep(buf, i * 0.16, Hz(A6), 0.09, 0.7);
                break;
            case (Timer, "marimba"):        // a bouncy mallet figure
                buf = new float[(int)(1.6 * Rate)];
                int[] fig = { E6, B5, E6, B5, Gs5, B5 };
                for (int i = 0; i < fig.Length; i++) Mallet(buf, i * 0.12, fig[i], 0.5, 0.85);
                break;
            case (Timer, "sweep"):          // two rising sweeps
                buf = new float[(int)(1.5 * Rate)];
                Glide(buf, 0, Hz(E5), Hz(B6), 0.5, 0.9); Glide(buf, 0.55, Hz(E5), Hz(B6), 0.5, 0.9);
                break;
            // ---- devices and the charger: the variant sets the voice, the kind the shape
            case (Connect or Disconnect or Charge or Low, _):
                buf = DeviceSound(kind, variant);
                break;
            default: throw new ArgumentException($"no such sound: {kind}/{variant}");
        }
        // normalize to the requested peak, with a short fade at the very end (no click when it loops)
        float peak = buf.Max(Math.Abs);
        float g = peak > 0 ? Math.Clamp(volume, 0, 1) * 0.9f / peak : 0;
        int fade = Math.Min(buf.Length, Rate / 100);
        for (int i = 0; i < buf.Length; i++)
        {
            float edge = i >= buf.Length - fade ? (buf.Length - 1 - i) / (float)fade : 1;
            buf[i] *= g * edge;
        }
        return Wav(buf);
    }

    static float[] DeviceSound(string kind, string voice)
    {
        float[] buf = new float[(int)(0.8 * Rate)];
        void Note(double at, int n, double amp)
        {
            if (voice == "click") Beep(buf, at, Hz(n + 12), 0.03, amp * 0.8);
            else if (voice == "glass") Bell(buf, at, n + 12, 0.6, amp * 0.7);
            else Mallet(buf, at, n, 0.55, amp);
        }
        switch (kind)
        {
            case Connect: Note(0, E5, 0.8); Note(0.09, B5, 0.85); break;
            case Disconnect: Note(0, B5, 0.8); Note(0.09, E5, 0.8); break;
            case Charge: Note(0, E5, 0.6); Note(0.015, B5, 0.45); Note(0.03, E6, 0.3); break;
            default: Note(0, E5, 0.8); Note(0.18, B4, 0.8); break;   // low battery
        }
        return buf;
    }

    static byte[] Wav(float[] s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int data = s.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var v in s) w.Write((short)Math.Clamp(v * 32767, -32768, 32767));
        w.Flush();
        return ms.ToArray();
    }
}
