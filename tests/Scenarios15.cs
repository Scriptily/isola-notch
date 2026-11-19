using Isola.App;
using Isola.Services;


namespace Isola.Tests;

/// <summary>The media dipping under the island's sounds (Ducker), in every order things can happen.</summary>
public static partial class Scenarios
{
    /// <summary>A stand-in audio session: a volume, and who last set it.</summary>
    sealed class Vol
    {
        public float V; public bool Muted; public bool Gone; public int Sets;
        public DuckSession Session(string id = "s1", string exe = @"C:\Apps\Spotify.exe") => new()
        {
            Id = id, Exe = exe,
            Get = () => Gone ? throw new InvalidOperationException("session expired") : Muted ? 0 : V,
            Set = v => { if (Gone) throw new InvalidOperationException("session expired"); V = v; Sets++; },
        };
    }

    static FakeClock Clock() { Ui.Reset(); var c = new FakeClock(); Ui.Clock = c; return c; }
    static bool Near(float a, float b, float tol = 0.005f) => Math.Abs(a - b) <= tol;

    [Scenario("the media dips before a chime, holds under it, then comes back up gradually to exactly where it was")]
    public static void DucksAndComesBack()
    {
        var clock = Clock();
        var spotify = new Vol { V = 0.8f };
        string file = Path.Combine(Path.GetTempPath(), "isola-duck-" + Guid.NewGuid().ToString("N") + ".txt");
        var d = new Ducker(() => new() { spotify.Session() }, file);
        double wait = d.Duck(1.0);
        True(wait > 0.1 && wait <= Ducker.DownSeconds + 1e-9, $"the chime waits for the dip ({wait:0.000} s)");
        True(File.Exists(file), "what's down is written down first (a crash puts it back)");
        clock.Advance(wait + 0.02);
        True(Near(spotify.V, 0.8f * Ducker.Depth), $"down to 30% of where it was when the chime starts ({spotify.V:0.000})");
        clock.Advance(1.0);
        True(Near(spotify.V, 0.8f * Ducker.Depth), $"and stays down while it plays ({spotify.V:0.000})");
        // coming back: gradually, never a jump
        var seen = new List<float>();
        for (int i = 0; i < 90; i++) { clock.Advance(1 / 60.0); seen.Add(spotify.V); }
        float biggest = seen.Zip(seen.Skip(1), (a, b) => b - a).Max();
        True(seen.Zip(seen.Skip(1), (a, b) => b >= a - 1e-6).All(x => x), "it only ever rises on the way back");
        True(biggest < 0.05f, $"in small steps (largest {biggest:0.000} a frame)");
        True(seen.Count(v => v > 0.3f && v < 0.75f) >= 10, "passing through the middle over many frames (gradual, not a snap)");
        Eq(0.8f, spotify.V, "back to exactly where it was");
        True(!d.Active && d.Factor == 1, "and done");
        True(!File.Exists(file), "its note is cleared");
    }

    [Scenario("a second sound while the media is down extends the dip; it never stacks or forgets the real volume")]
    public static void OverlappingSoundsDontStack()
    {
        var clock = Clock();
        var app = new Vol { V = 0.6f };
        var d = new Ducker(() => new() { app.Session() }, null);
        d.Duck(0.5); clock.Advance(0.4);
        double wait = d.Duck(0.5);
        True(wait < 0.01, $"already down: the next chime plays at once ({wait:0.000})");
        True(Near(app.V, 0.6f * Ducker.Depth), $"still at the same dip, not lower ({app.V:0.000})");
        clock.Advance(0.75);
        True(Near(app.V, 0.6f * Ducker.Depth), "held for the second chime too");
        clock.Advance(1.2);   // past the second chime and the way back up
        // a third arrives just as it's coming back up: back down from where it was, and still returns to 0.6
        d.Duck(0.3); clock.Advance(0.1); clock.Advance(0.05);
        d.Duck(0.3);
        clock.Advance(3);
        Eq(0.6f, app.V, "every overlap ends at the original volume");
        True(!d.Active, "done");
    }

    [Scenario("caught on its way back up, a new chime takes the media back down from where it is")]
    public static void DuckWhileRisingGoesBackDown()
    {
        var clock = Clock();
        var app = new Vol { V = 1f };
        var d = new Ducker(() => new() { app.Session() }, null);
        d.Duck(0.2); clock.Advance(0.15 + 0.2 + 0.3 + 0.4);   // dipped, played, tail, then halfway back up
        float mid = app.V;
        True(mid > 0.35f && mid < 0.95f, $"halfway back ({mid:0.00})");
        double wait = d.Duck(0.5);
        True(wait > 0 && wait < Ducker.DownSeconds, $"a shorter dip from part way ({wait:0.000} s)");
        clock.Advance(wait + 0.02);
        True(Near(app.V, Ducker.Depth), $"back down to the same low point ({app.V:0.000})");
        clock.Advance(3);
        Eq(1f, app.V, "and back to full");
    }

    [Scenario("a ringtone keeps the media down until the call is answered or ends, then it comes back")]
    public static void LoopsHoldUntilReleased()
    {
        var clock = Clock();
        var app = new Vol { V = 0.7f };
        var d = new Ducker(() => new() { app.Session() }, null);
        object call = new();
        d.Duck(2.0, call);
        clock.Advance(20);
        True(Near(app.V, 0.7f * Ducker.Depth), "still down 20 s into the ringing (one round of the ringtone is 2 s)");
        d.Release(call);
        clock.Advance(0.2);
        True(app.V < 0.7f * 0.6f, "a short tail after the ring stops");
        clock.Advance(1.5);
        Eq(0.7f, app.V, "then back up");
        d.Release(call);   // a second stop for the same call changes nothing
        True(!d.Active, "done");
    }

    [Scenario("a ringtone whose stop is never seen lets the media back up after two minutes (failing open)")]
    public static void LostLoopLetsGo()
    {
        var clock = Clock();
        var app = new Vol { V = 0.5f };
        var d = new Ducker(() => new() { app.Session() }, null);
        d.Duck(2.0, new object());
        clock.Advance(Ducker.MaxSeconds - 1);
        True(app.V < 0.2f, "down while it could still be ringing");
        clock.Advance(3);
        Eq(0.5f, app.V, "given up on: back to where it was");
        True(!d.Active, "done");
    }

    [Scenario("a volume someone changes while the media is down is theirs: it's left where they put it")]
    public static void SomeoneElsesChangeWins()
    {
        var clock = Clock();
        var a = new Vol { V = 0.8f }; var b = new Vol { V = 0.4f };
        var d = new Ducker(() => new() { a.Session("a"), b.Session("b") }, null);
        d.Duck(1.0); clock.Advance(0.5);
        a.V = 0.55f;   // the Sound tab's slider, or the app's own
        clock.Advance(3);
        Eq(0.55f, a.V, "the one moved stays where it was moved to");
        Eq(0.4f, b.V, "the other comes back as usual");
        // turned all the way down by hand mid-dip (muting counts the same)
        var c = new Vol { V = 0.9f };
        var d2 = new Ducker(() => new() { c.Session() }, null);
        d2.Duck(1.0); clock.Advance(0.5);
        c.Muted = true;
        clock.Advance(3);
        Eq(true, c.Muted, "muted meanwhile: still muted");
        True(!d2.Active, "and the ducker lets go of it");
    }

    [Scenario("nothing to lower (no sessions, muted, at 0) means no dip and no wait; a session that vanishes is let go")]
    public static void NothingToLower()
    {
        var clock = Clock();
        Eq(0.0, new Ducker(() => new(), null).Duck(1.0), "no sessions: plays at once");
        var muted = new Vol { V = 0.8f, Muted = true }; var zero = new Vol { V = 0 };
        var d = new Ducker(() => new() { muted.Session("m"), zero.Session("z") }, null);
        Eq(0.0, d.Duck(1.0), "muted and silent sessions: plays at once");
        Eq(0, muted.Sets + zero.Sets, "and they're never touched");
        Eq(0.0, new Ducker(() => throw new InvalidOperationException("audio service gone"), null).Duck(1.0), "the lookup failing: plays at once, never throws");
        // one tab's session closes mid-dip: the other still comes back
        var tab1 = new Vol { V = 0.6f }; var tab2 = new Vol { V = 0.6f };
        var d2 = new Ducker(() => new() { tab1.Session("t1"), tab2.Session("t2") }, null);
        d2.Duck(1.0); clock.Advance(0.4);
        tab1.Gone = true;
        clock.Advance(3);
        Eq(0.6f, tab2.V, "the open tab comes back");
        True(!d2.Active, "done");
    }

    [Scenario("quitting or turning the setting off mid-dip puts the media straight back")]
    public static void RestoreAllAtOnce()
    {
        var clock = Clock();
        var app = new Vol { V = 0.75f };
        var d = new Ducker(() => new() { app.Session() }, null);
        d.Duck(2.0); clock.Advance(0.5);
        d.RestoreAll("quitting");
        Eq(0.75f, app.V, "back at once");
        int sets = app.Sets;
        clock.Advance(3);
        Eq(sets, app.Sets, "and nothing moves it after");
    }

    [Scenario("after a crash mid-dip, the next start puts the media back up (by session, or by app if it restarted); a volume moved since stays")]
    public static void CrashLeftoversRestored()
    {
        var clock = Clock();
        string file = Path.Combine(Path.GetTempPath(), "isola-duck-" + Guid.NewGuid().ToString("N") + ".txt");
        var spotify = new Vol { V = 0.8f }; var chrome = new Vol { V = 0.5f };
        var d = new Ducker(() => new() { spotify.Session("sp", @"C:\Apps\Spotify.exe"), chrome.Session("ch", @"C:\Apps\chrome.exe") }, file);
        d.Duck(1.0); clock.Advance(0.5);
        True(File.Exists(file), "the note is there while it's down");
        // the run dies here: nothing restores it
        True(spotify.V < 0.3f && chrome.V < 0.2f, "both left down");
        var spotifyAfter = new Vol { V = spotify.V };    // Spotify restarted meanwhile: a new session, the same app
        var chromeMoved = new Vol { V = 0.9f };          // Chrome turned up by hand since
        int n = Ducker.RestoreLeftovers(file, new() { spotifyAfter.Session("new-id", @"C:\Apps\SPOTIFY.EXE"), chromeMoved.Session("ch", @"C:\Apps\chrome.exe") });
        Eq(1, n, "one put back");
        Eq(0.8f, spotifyAfter.V, "Spotify back where it was, found by its app");
        Eq(0.9f, chromeMoved.V, "Chrome, moved since, left alone");
        True(!File.Exists(file), "the note is cleared");
        Eq(0, Ducker.RestoreLeftovers(file, new()), "no note: nothing to do");
    }

    [Scenario("the media dips only when the setting is on and the media is really heard")]
    public static void DucksOnlyWhenHeard()
    {
        Eq(true, Isola.App.App.DuckFor(new Config { DuckMedia = true }, true), "on, and heard");
        Eq(false, Isola.App.App.DuckFor(new Config { DuckMedia = true }, false), "paused or silent: nothing to lower");
        Eq(false, Isola.App.App.DuckFor(new Config { DuckMedia = false }, true), "turned off");
        Eq(true, new Config().DuckMedia, "on by default");
    }

    [Scenario("the waveform keeps its size while the media is turned down under a chime")]
    public static void WaveformIgnoresTheDip()
    {
        var clock = Clock();
        float factor = 1, peak = 0.5f;
        var m = new MediaService(clock, null) { Ducked = () => factor };
        m.AppPeak = _ => peak * factor;
        m.Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "A song", Playing = true };
        float before = 0; for (int i = 0; i < 30; i++) before = m.Level;
        factor = Ducker.Depth;
        float during = 0; for (int i = 0; i < 30; i++) during = m.Level;
        True(Math.Abs(during - before) < 0.02f, $"the same level while it's down ({before:0.00} -> {during:0.00})");
    }

    /// <summary>The island with the real sound path: alerts' sounds go to a log, the music is a fake session.</summary>
    sealed class SoundRig
    {
        public Island.Island Isl; public FakeClock Clock; public FakeMedia Media; public Vol Music = new() { V = 0.8f };
        public bool Heard = true; public readonly List<(string kind, float vol, double at)> Played = new(); public int Stops;
        public Ducker Ducker;
        public SoundRig(Config cfg)
        {
            var (isl, _, clock) = NewIsland(cfg);
            Isl = isl; Clock = clock;
            Media = new FakeMedia(clock) { Current = new MediaInfo { AppName = "Spotify", SessionId = "Spotify.exe", Title = "A song", Playing = true } };
            isl.Svc.Media = Media;
            Ducker = new Ducker(() => new() { Music.Session() }, null);
            var snd = new AlertSounds(() => cfg, () => Media.Current is { Playing: true }, () => Heard, Ducker)
            {
                Play = (k, v, t) => Played.Add((k, v, Clock.Now)), Stop = () => Stops++, Length = (k, t) => 0.5,
            };
            isl.SoundOn = snd.On; isl.SoundOff = snd.Off;
        }
        public static Alert Message(string key = "n1") => new() { Key = key, Kind = "notification", Title = "Sam", Subtitle = "WhatsApp", Detail = "hi", Sound = Sounds.Notification, Seconds = 3 };
        public static Alert Call() => new() { Key = "call", Kind = "call", Title = "Mom", Priority = 3, Sticky = true, MaxSeconds = 60, Sound = Sounds.Call };
    }

    [Scenario("a notification over music: the music dips, the chime plays into it, and the music comes back to its volume")]
    public static void ChimeOverMusic()
    {
        var r = new SoundRig(new Config());
        double t0 = r.Clock.Now;
        r.Isl.Alert(SoundRig.Message());
        r.Clock.Advance(0.05, r.Isl);
        Eq(0, r.Played.Count, "the chime waits for the dip");
        r.Clock.Advance(0.2, r.Isl);
        Eq(1, r.Played.Count, "then plays");
        True(r.Played[0].at - t0 >= Ducker.DownSeconds - 0.02, "once the music is down");
        True(Near(r.Music.V, 0.8f * Ducker.Depth), $"the music under it ({r.Music.V:0.00})");
        Eq(Isola.App.App.SoundVolumeOf(new Config()), r.Played[0].vol, "the chime at its normal level");
        r.Clock.Advance(3, r.Isl);
        Eq(0.8f, r.Music.V, "the music back where it was");
    }

    [Scenario("quieter and muted while media plays: quieter still dips the music, muted plays nothing and leaves the music alone")]
    public static void QuieterAndMutedWithTheDip()
    {
        var q = new SoundRig(new Config { SoundsWhilePlaying = "quiet" });
        q.Isl.Alert(SoundRig.Message()); q.Clock.Advance(0.3, q.Isl);
        Eq(1, q.Played.Count, "quieter: it plays");
        True(q.Played[0].vol < Isola.App.App.SoundVolumeOf(new Config()) * 0.5f, $"quieter ({q.Played[0].vol:0.00})");
        True(q.Music.V < 0.5f, "and the music still dips under it");
        q.Clock.Advance(3, q.Isl); Eq(0.8f, q.Music.V, "then comes back");

        var m = new SoundRig(new Config { SoundsWhilePlaying = "mute" });
        m.Isl.Alert(SoundRig.Message()); m.Clock.Advance(2, m.Isl);
        Eq(0, m.Played.Count, "muted: no sound");
        Eq(0, m.Music.Sets, "and the music is never touched");
    }

    [Scenario("paused or silent media, or the setting off: the chime plays at once, the music untouched")]
    public static void NoDipWithoutMusic()
    {
        var paused = new SoundRig(new Config());
        paused.Media.Current.Playing = false; paused.Heard = false;
        paused.Isl.Alert(SoundRig.Message()); paused.Clock.Advance(0.02, paused.Isl);
        Eq(1, paused.Played.Count, "paused: at once");
        Eq(0, paused.Music.Sets, "nothing turned down");

        var silent = new SoundRig(new Config()); silent.Heard = false;   // playing, but at 0% in the app
        silent.Isl.Alert(SoundRig.Message()); silent.Clock.Advance(0.02, silent.Isl);
        Eq(1, silent.Played.Count, "playing but not heard: at once");
        Eq(0, silent.Music.Sets, "nothing to lower");

        var off = new SoundRig(new Config { DuckMedia = false });
        off.Isl.Alert(SoundRig.Message()); off.Clock.Advance(0.02, off.Isl);
        Eq(1, off.Played.Count, "the setting off: at once");
        Eq(0, off.Music.Sets, "the music left alone");
    }

    [Scenario("a call keeps the music down while it rings, and brings it back when it ends; a call gone before its ring starts never rings")]
    public static void CallHoldsTheDip()
    {
        var r = new SoundRig(new Config());
        r.Isl.Alert(SoundRig.Call());
        r.Clock.Advance(0.3, r.Isl);
        Eq(Sounds.Call, r.Played.Single().kind, "it rings");
        r.Clock.Advance(15, r.Isl);
        True(r.Music.V < 0.3f, "the music down the whole time it rings");
        r.Isl.DismissAlert("call");
        r.Clock.Advance(0.05, r.Isl);
        True(r.Stops >= 1, "the ring stops");
        r.Clock.Advance(2, r.Isl);
        Eq(0.8f, r.Music.V, "and the music comes back");

        var quick = new SoundRig(new Config());
        quick.Isl.Alert(SoundRig.Call());
        quick.Clock.Advance(0.04, quick.Isl);
        quick.Isl.DismissAlert("call");   // answered on the phone in the instant the music was dipping
        quick.Clock.Advance(2, quick.Isl);
        Eq(0, quick.Played.Count, "it never starts ringing");
        Eq(0.8f, quick.Music.V, "and the music comes back");
    }

    [Scenario("held while focused: no sound and no dip while it waits; the summary sounds once the music stops")]
    public static void HoldAndDipTogether()
    {
        var r = new SoundRig(new Config { HoldWhileFocused = true });
        r.Isl.Alert(SoundRig.Message("n1")); r.Isl.Alert(SoundRig.Message("n2"));
        r.Clock.Advance(2, r.Isl);
        Eq(0, r.Played.Count, "held: silent");
        Eq(0, r.Music.Sets, "and the music untouched");
        r.Media.Current.Playing = false; r.Heard = false; r.Isl.ActivityChanged();
        r.Clock.Advance(2, r.Isl);
        Eq("focus:summary", r.Isl.CurrentAlert?.Key, "the summary");
        Eq(1, r.Played.Count, "with one sound");
        Eq(0, r.Music.Sets, "nothing to dip: the music has stopped");
    }

    [Scenario("several notifications in a row: one dip that lasts until the last chime, then the music's exact volume")]
    public static void BurstIsOneDip()
    {
        var r = new SoundRig(new Config());
        for (int k = 0; k < 4; k++) { r.Isl.Alert(SoundRig.Message("n" + k)); r.Clock.Advance(0.9, r.Isl); }
        True(r.Played.Count >= 2, $"chimes played ({r.Played.Count})");
        r.Clock.Advance(20, r.Isl);
        Eq(0.8f, r.Music.V, "the music back at exactly its volume after the burst");
        True(!r.Ducker.Active, "and nothing left turned down");
    }

    /// <summary>
    /// The whole real path: the media service watching how loud the video is (its loudness follows the
    /// session's volume, as the real meters' does), the dip, the sounds, the island and its queue.
    /// </summary>
    sealed class LiveRig
    {
        public Island.Island Isl; public FakeClock Clock; public MediaService Media; public Ducker Ducker;
        public Vol Video = new() { V = 0.9f };
        public readonly List<(string kind, float vol, float videoAt)> Played = new();
        public LiveRig(Config cfg)
        {
            var (isl, _, clock) = NewIsland(cfg);
            Isl = isl; Clock = clock;
            Ducker = new Ducker(() => new() { Video.Session() }, null);
            Media = new MediaService(clock, null) { Ducked = () => Ducker.Factor };
            Media.AppPeak = _ => 0.5f * Video.V;
            Media.OutputPeak = () => 0;
            Media.Current = new MediaInfo { AppName = "Chrome", SessionId = "Chrome", Title = "A video", Playing = true };
            Media.StartWatching();
            isl.Svc.Media = Media;
            isl.SetAnyFullscreen(true);
            var snd = new AlertSounds(() => cfg, () => Media.Current is { Playing: true }, () => Media.Sounding, Ducker)
            {
                Play = (k, v, t) => Played.Add((k, v, Video.V)), Stop = () => { }, Length = (k, t) => 0.6,
            };
            isl.SoundOn = snd.On; isl.SoundOff = snd.Off;
            clock.Advance(1, isl);
        }
        public void Notify(string key) => Isl.Alert(new Alert { Key = key, Kind = "notification", Title = "Sam", Subtitle = "WhatsApp", Detail = "hi " + key, Sound = Sounds.Notification, Seconds = 3 });
        public void Notice(string key) => Isl.Alert(new Alert { Key = key, Kind = "battery", Title = "Fully charged", Sound = Sounds.Notice, Seconds = 3 });
    }

    [Scenario("rapid notifications over a fullscreen video, quieter and dipped: every chime gets the dip, not only the first, and the video comes back")]
    public static void EveryChimeInABurstDips()
    {
        var r = new LiveRig(new Config { SoundsWhilePlaying = "quiet" });
        r.Notify("n1"); r.Clock.Advance(0.6, r.Isl);
        r.Notify("n2"); r.Clock.Advance(0.4, r.Isl);   // one midway through the first
        r.Notice("full"); r.Notify("n3");
        r.Clock.Advance(25, r.Isl);
        True(r.Played.Count >= 3, $"each popup chimes ({r.Played.Count})");
        for (int k = 0; k < r.Played.Count; k++)
            True(r.Played[k].videoAt < 0.9f * 0.5f, $"chime {k + 1} ({r.Played[k].kind}) plays with the video down ({r.Played[k].videoAt:0.00} of 0.90)");
        True(r.Played.All(p => p.vol < Isola.App.App.SoundVolumeOf(new Config()) * 0.5f), "all of them quieter");
        Eq(0.9f, r.Video.V, "and the video back where it was");
    }

    [Scenario("notifications spaced out over a long fullscreen video: the tenth dips like the first")]
    public static void SpacedOutChimesAllDip()
    {
        var r = new LiveRig(new Config());
        for (int k = 0; k < 10; k++) { r.Notify("n" + k); r.Clock.Advance(7, r.Isl); }
        Eq(10, r.Played.Count, "ten chimes");
        True(r.Played.All(p => p.videoAt < 0.45f), "every one with the video down: " + string.Join(", ", r.Played.Select(p => p.videoAt.ToString("0.00"))));
        Eq(0.9f, r.Video.V, "back up after each");
    }

    [Scenario("a call in the middle of a burst: it rings with the video down, the notifications after it dip too, and the video comes back")]
    public static void CallInABurst()
    {
        var r = new LiveRig(new Config { SoundsWhilePlaying = "quiet" });
        r.Notify("n1"); r.Clock.Advance(0.5, r.Isl);
        r.Isl.Alert(new Alert { Key = "call", Kind = "call", Title = "Mom", Priority = 3, Sticky = true, MaxSeconds = 60, Sound = Sounds.Call });
        r.Clock.Advance(0.5, r.Isl);
        r.Notify("n2"); r.Notice("full");
        r.Clock.Advance(8, r.Isl);
        var ring = r.Played.FindIndex(p => p.kind == Sounds.Call);
        True(ring >= 0 && r.Played[ring].videoAt < 0.45f, "the ringtone plays with the video down");
        True(r.Video.V < 0.45f, "and the video stays down while it rings");
        r.Isl.DismissAlert("call");
        r.Clock.Advance(25, r.Isl);
        var after = r.Played.Skip(ring + 1).ToList();
        True(after.Count >= 1, $"the ones that waited behind the call chime after it ({after.Count})");
        True(after.All(p => p.videoAt < 0.45f), "with the dip too: " + string.Join(", ", after.Select(p => p.videoAt.ToString("0.00"))));
        Eq(0.9f, r.Video.V, "the video back where it was");
    }

    [Scenario("held while focused, then a burst comes back as one summary: one sound, dipped if the video is still going")]
    public static void HeldBurstOneSound()
    {
        var r = new LiveRig(new Config { HoldWhileFocused = true, SoundsWhilePlaying = "quiet" });
        for (int k = 0; k < 5; k++) { r.Notify("n" + k); r.Clock.Advance(0.3, r.Isl); }
        r.Notice("full");
        r.Clock.Advance(5, r.Isl);
        Eq(0, r.Played.Count, "silent while held");
        Eq(0.9f, r.Video.V, "the video untouched");
        r.Isl.Expand(); r.Clock.Advance(3, r.Isl);   // opened: they come back (the video is still playing)
        Eq(1, r.Played.Count, "one sound for the summary");
        True(r.Played[0].videoAt < 0.45f, "with the dip, the video still playing");
        r.Clock.Advance(5, r.Isl);
        Eq(0.9f, r.Video.V, "and back up");
    }

    [Scenario("muted while media plays: a burst and a call make no sound and never touch the video")]
    public static void MutedBurstLeavesVideoAlone()
    {
        var r = new LiveRig(new Config { SoundsWhilePlaying = "mute" });
        for (int k = 0; k < 4; k++) { r.Notify("n" + k); r.Clock.Advance(0.4, r.Isl); }
        r.Isl.Alert(new Alert { Key = "call", Kind = "call", Title = "Mom", Priority = 3, Sticky = true, MaxSeconds = 60, Sound = Sounds.Call });
        r.Clock.Advance(3, r.Isl); r.Isl.DismissAlert("call"); r.Clock.Advance(20, r.Isl);
        Eq(0, r.Played.Count, "no sound at all");
        Eq(0, r.Video.Sets, "the video's volume never touched");
    }

    [Scenario("the media counts as heard while a popup covers the waveform (nothing draws it then)")]
    public static void HeardWithoutTheWaveform()
    {
        var clock = Clock();
        float vol = 0.8f;
        var m = new MediaService(clock, null) { AppPeak = _ => 0.4f * vol, OutputPeak = () => 0 };
        m.Current = new MediaInfo { AppName = "Chrome", SessionId = "Chrome", Title = "A video", Playing = true };
        m.StartWatching();
        clock.Advance(10);   // the level never read: no waveform on screen
        True(m.Sounding, "still heard 10 s later");
        vol = 0;   // turned to 0 in the app
        clock.Advance(2);
        True(!m.Sounding, "and silent once it really is");
    }
}
