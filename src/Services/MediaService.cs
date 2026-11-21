using Isola.App;
using Isola.Render;
using Vortice.Mathematics;
using Vortice.WIC;
using Windows.Media.Control;
using WinRT;
using GSM = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using GSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace Isola.Services;

/// <summary>
/// Now Playing for anything that reports to Windows' media controls (the same
/// source as the volume flyout): Spotify, Chrome/Edge tabs (YouTube, Plex Web),
/// Plex for Windows, VLC, Apple Music, etc. One documented WinRT API, no per-app
/// integrations to break.
/// </summary>
public sealed partial class MediaService : IMediaSourceEx
{
    readonly IClock clock;
    readonly AudioService audio;
    GSM mgr;
    readonly Dictionary<string, GSession> sessions = new();
    readonly Dictionary<string, MediaInfo> infos = new();
    string pinned;          // user picked a session explicitly
    readonly Dictionary<string, Action> unhook = new();
    long recheck;
    public MediaInfo Current { get; internal set; }
    public IReadOnlyList<MediaInfo> Sessions => infos.Values.ToList();
    public double LastPlayingAt { get; private set; } = -1e9;
    public event Action Changed;
    public event Action<MediaInfo, MediaInfo> TrackChanged;
    public Func<IReadOnlyCollection<string>> Ignored = () => Array.Empty<string>();
    /// <summary>
    /// The level the visualizer and glow follow: the shown app's own sound, not the whole output's. With
    /// Spotify playing at 0% under a video, Spotify's visualizer moved with the video. When Isola can't tell
    /// which audio sessions are the app's, it falls back to the output. Smoothed: fast attack, slow release.
    /// </summary>
    public float Level
    {
        get
        {
            var m = Current;
            float p = HeardLevel(m);
            level = p > level ? p : level * 0.82f + p * 0.18f;
            if (p > Silence) lastSoundAt = clock.Now;
            return level;
        }
    }
    float level;
    double lastSoundAt = double.NegativeInfinity;

    /// <summary>How loud what's shown is right now; turned down under a chime it counts as it was (the waveform keeps its size).</summary>
    float HeardLevel(MediaInfo m)
    {
        float? app = m == null ? null : AppPeak(m);
        if (app is float a) app = a / Math.Max(0.05f, Ducked());
        return LevelFrom(app, OutputPeak());
    }

    /// <summary>Tests: a session seen (or changed), as Windows' events report it.</summary>
    internal void Track(MediaInfo i) { infos[i.SessionId] = i; Choose(); }

    /// <summary>Tests: the quarter-second watch that StartAsync starts.</summary>
    internal void StartWatching() { Ui.Cancel(ref heardTimer); WatchHeard(); }
    /// <summary>How long without sound before the visualizer settles (a gap between songs is shorter).</summary>
    internal const double QuietFor = 1.2;
    /// <summary>
    /// Sound has come from what's shown within <see cref="QuietFor"/>. Spotify "playing" at 0% in its own
    /// app sends nothing, and its visualizer kept dancing anyway: it moved whenever something was playing.
    /// </summary>
    public bool Sounding => clock.Now - lastSoundAt < QuietFor;
    /// <summary>Sound has come from what's shown within <paramref name="seconds"/> (a quiet moment in a song isn't silence).</summary>
    public bool HeardWithin(double seconds) => clock.Now - lastSoundAt < seconds;
    /// <summary>How long without sound before playing media counts as silent for the island's sounds.</summary>
    internal const double SilentAfter = 3;
    internal static float LevelFrom(float? app, float output) => app ?? output;

    /// <summary>An app's own sound level right now, or null when its audio sessions can't be told apart (tests replace it).</summary>
    public Func<MediaInfo, float?> AppPeak;
    /// <summary>How far down the media is turned under a chime right now (1: not at all).</summary>
    public Func<float> Ducked = () => 1;
    /// <summary>The whole output's level right now (tests replace it).</summary>
    public Func<float> OutputPeak;
    /// <summary>Spotify's own volume, 0-100, when Isola knows it (connected to Spotify).</summary>
    public Func<int?> SpotifyVolume = () => null;
    /// <summary>Asks Spotify for its volume again (it throttles itself to one read every few seconds).</summary>
    public Action PollSpotify = () => { };

    /// <summary>
    /// What of an app's sound actually comes out. Its meter can read the music at full level while nothing
    /// plays: Spotify's own volume slider turns the sound down after that meter (its level read 1.0 at 0%,
    /// with the speakers silent). So it's scaled by that slider when Isola knows it, and never counts as
    /// louder than everything the speakers play.
    /// </summary>
    internal static float? Audible(float? app, float output, int? appVolume)
    {
        if (app == null) return null;
        float v = app.Value;
        if (appVolume is int pct) v *= Math.Clamp(pct, 0, 100) / 100f;
        return Math.Min(v, output);
    }

    // ---- which playing apps can be heard: one playing at 0% mustn't take the island from one you're listening to ----
    readonly Dictionary<string, double> heardAt = new();
    readonly HashSet<string> retrying = new();
    double levelLogAt = double.NegativeInfinity;
    string heardSet = "";
    long heardTimer;
    /// <summary>An app counts as heard for this long after its sound last rose above silence (quiet passages don't count as stopping).</summary>
    internal const double HeardFor = 3;
    const float Silence = 0.002f;

    bool? Heard(MediaInfo i)
    {
        if (AppPeak(i) == null) return null;   // can't tell
        if (heardAt.TryGetValue(i.SessionId, out var t) && clock.Now - t < HeardFor) return true;
        // only just started: not listened to long enough to call it silent (counted as heard until it has been)
        if (i.Playing && !double.IsNaN(i.PlayingSince) && clock.Now - i.PlayingSince < HeardFor) return null;
        return false;
    }

    /// <summary>While two or more apps play, checks four times a second which of them can be heard, and re-picks when that changes.</summary>
    void WatchHeard()
    {
        heardTimer = Ui.After(0.25, WatchHeard);
        // whether it's heard is kept up to date here, not only when the waveform is drawn: under a popup nothing
        // draws it, so a second chime a moment later took the video for silent and played without the dip
        if (Current is { Playing: true } cur)
        {
            audio?.RefreshAppMeters(clock.Now);   // the sessions the dip turns down stay current too
            if (HeardLevel(cur) > Silence) lastSoundAt = clock.Now;
        }
        var playing = infos.Values.Where(i => i.Playing).ToList();
        // Spotify's slider is invisible to Windows' meters: while Spotify plays on the island or beside another
        // app, keep reading it, or a volume of 0% set in Spotify goes unnoticed until its panel is opened
        // (only where its sound can't be captured: captured, its volume is already in the level)
        var spotify = playing.FirstOrDefault(i => i.IsSpotify);
        if (spotify != null && (playing.Count > 1 || Current?.IsSpotify == true) && audio?.CapturedPeak(spotify.SessionId, spotify.AppName, clock.Now) == null) PollSpotify();
        if (Current is { Playing: true } c && clock.Now - levelLogAt > 5)
        {
            levelLogAt = clock.Now;
            audio?.RefreshAppMeters(clock.Now);
            float? app = AppPeak(c);
            float? cap = audio?.CapturedPeak(c.SessionId, c.AppName, clock.Now);
            Log.D("media", $"level of {c.SessionId}: {(cap != null ? "captured" : "metered")} {(app == null ? "unknown" : app.Value.ToString("0.0000"))}, output {OutputPeak():0.0000}, sounding {Sounding}");
        }
        // which playing apps can be heard, kept for every one of them, one alone included: it was only kept
        // while two or more played, so a video playing by itself never counted as heard, and a Spotify paused
        // for hours (the one on screen) kept the island, the sounds' dip and their volume from the video
        if (playing.Count == 0) { heardSet = ""; return; }
        audio?.RefreshAppMeters(clock.Now);
        double now = clock.Now;
        foreach (var i in playing) if (AppPeak(i) is float p && p > Silence) heardAt[i.SessionId] = now;
        string set = string.Join("|", playing.Where(i => Heard(i) == true).Select(i => i.SessionId).OrderBy(x => x));
        if (set != heardSet) { heardSet = set; Log.D("media", "heard: " + (set.Length > 0 ? set : "none")); Choose(); }
    }


    public MediaService(IClock c, AudioService a)
    {
        clock = c; audio = a;
        // the app's own sound when Windows can capture it (exact, its volume control included); else its meters
        AppPeak = m => audio?.CapturedPeak(m.SessionId, m.AppName, clock.Now) ?? Audible(audio?.AppPeak(m.SessionId, m.AppName), OutputPeak(), m.IsSpotify ? SpotifyVolume() : null);
        OutputPeak = () => audio?.PeakNow() ?? 0;
    }

    public async Task StartAsync()
    {
        try
        {
            mgr = await GSM.RequestAsync().AsTask().ConfigureAwait(false);
            mgr.SessionsChanged += (_, _) => Ui.Post(Resync);
            mgr.CurrentSessionChanged += (_, _) => Ui.Post(Resync);
            Ui.Post(Resync);
            Ui.Post(() => { audio?.RefreshAppMeters(clock.Now); WatchHeard(); });
            Log.I("media", "media session manager ready");
        }
        catch (Exception ex) { Log.E("media", "media sessions unavailable; Now Playing disabled", ex); }
    }

    void Resync()
    {
        if (mgr == null) return;
        IReadOnlyList<GSession> list;
        try { list = mgr.GetSessions(); } catch (Exception ex) { Log.W("media", "GetSessions failed: " + ex.Message); return; }
        var seen = new HashSet<string>();
        foreach (var s in list)
        {
            string id;
            try { id = s.SourceAppUserModelId ?? "?"; } catch { continue; }
            // two tabs of the same browser share an AUMID; keep the first
            if (!seen.Add(id)) continue;
            if (!sessions.ContainsKey(id))
            {
                sessions[id] = s;
                infos[id] = new MediaInfo { SessionId = id, AppName = FriendlyApp(id) };
                Hook(s, id);
                Log.I("media", $"session appeared: {id}");
                _ = RefreshProps(id);
            }
            else sessions[id] = s;
            RefreshPlayback(id); RefreshTimeline(id);
        }
        foreach (var gone in sessions.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            // A site's preview opens and closes a session every few hundred ms;
            // without this every one left three event subscriptions behind.
            if (unhook.Remove(gone, out var off)) { try { off(); } catch { } }
            sessions.Remove(gone); infos.Remove(gone);
            if (pinned == gone) pinned = null;
            Log.I("media", $"session closed: {gone}");
        }
        Choose();
    }

    void Hook(GSession s, string id)
    {
        Windows.Foundation.TypedEventHandler<GSession, MediaPropertiesChangedEventArgs> props = (_, _) => _ = RefreshProps(id);
        Windows.Foundation.TypedEventHandler<GSession, PlaybackInfoChangedEventArgs> playback = (_, _) => Ui.Post(() => { RefreshPlayback(id); Choose(); });
        Windows.Foundation.TypedEventHandler<GSession, TimelinePropertiesChangedEventArgs> timeline = (_, _) => Ui.Post(() => { if (RefreshTimeline(id) && Current?.SessionId == id) Changed?.Invoke(); });
        s.MediaPropertiesChanged += props;
        s.PlaybackInfoChanged += playback;
        s.TimelinePropertiesChanged += timeline;
        unhook[id] = () => { s.MediaPropertiesChanged -= props; s.PlaybackInfoChanged -= playback; s.TimelinePropertiesChanged -= timeline; };
    }

    async Task RefreshProps(string id)
    {
        if (!sessions.TryGetValue(id, out var s)) return;
        try
        {
            var p = await s.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            byte[] art = null;
            if (p?.Thumbnail != null)
            {
                try
                {
                    using var stream = await p.Thumbnail.OpenReadAsync().AsTask().ConfigureAwait(false);
                    if (stream.Size > 0 && stream.Size < 8_000_000)
                    {
                        using var ns = stream.AsStreamForRead();
                        using var ms = new MemoryStream();
                        await ns.CopyToAsync(ms).ConfigureAwait(false);
                        art = ms.ToArray();
                    }
                }
                catch (Exception ex) { Log.D("media", $"thumbnail read failed for {id}: {ex.Message}"); }
            }
            string title = p?.Title ?? "", artist = p?.Artist ?? "", album = p?.AlbumTitle ?? "";
            Ui.Post(() =>
            {
                if (!infos.TryGetValue(id, out var m)) return;
                string oldKey = m.TrackKey;
                var before = Clone(m);
                m.Title = title.Trim(); m.Artist = artist.Trim(); m.Album = album.Trim();
                // YouTube in a browser reports "Channel" as artist and nothing else; fine as is
                if (art != null)
                {
                    string key = id + ":" + art.Length + ":" + Hash(art);
                    if (key != m.ArtKey) { m.Art = art; m.ArtKey = key; m.Palette = PaletteOf(art); }
                }
                else if (oldKey != m.TrackKey) { m.Art = null; m.ArtKey = ""; m.Palette = null; }
                Log.D("media", $"[{m.AppName}] \"{m.Title}\" - {m.Artist} art={(m.Art?.Length ?? 0)}B");
                if (Current?.SessionId == id && oldKey != m.TrackKey && !string.IsNullOrEmpty(m.Title)) TrackChanged?.Invoke(before, m);
                Choose();
                Changed?.Invoke();
            });
        }
        catch (Exception ex) { Log.D("media", $"properties failed for {id}: {ex.Message}"); }
    }

    void RefreshPlayback(string id)
    {
        if (!sessions.TryGetValue(id, out var s) || !infos.TryGetValue(id, out var m)) return;
        try
        {
            var pi = s.GetPlaybackInfo();
            if (pi == null)   // the app was mid-change: ask again in a moment, once
            {
                if (retrying.Add(id)) Ui.After(0.3, () => { RefreshPlayback(id); Choose(); });
                return;
            }
            retrying.Remove(id);
            bool was = m.Playing;
            bool reported = pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            m.Playing = id == heldPlayingFor ? Hold<bool>(reported, heldPlaying, clock.Now, ref playingHoldUntil) ?? reported : reported;
            if (m.Playing && !was) m.PlayingSince = clock.Now;
            else if (!m.Playing) m.PlayingSince = double.NaN;
            m.CanNext = pi.Controls.IsNextEnabled; m.CanPrev = pi.Controls.IsPreviousEnabled;
            m.CanSeek = pi.Controls.IsPlaybackPositionEnabled; m.CanPlayPause = pi.Controls.IsPlayPauseToggleEnabled || pi.Controls.IsPlayEnabled || pi.Controls.IsPauseEnabled;
            int? repeat = pi.AutoRepeatMode switch { Windows.Media.MediaPlaybackAutoRepeatMode.Track => 1, Windows.Media.MediaPlaybackAutoRepeatMode.List => 2, null => -1, _ => 0 };
            bool mine = id == heldFor;
            m.Shuffle = Hold(pi.IsShuffleActive, mine ? heldShuffle : null, clock.Now, ref shuffleHoldUntil);
            m.Repeat = Hold(repeat, mine ? heldRepeat : null, clock.Now, ref repeatHoldUntil) ?? -1;
            m.IsVideo = pi.PlaybackType == Windows.Media.MediaPlaybackType.Video;
            m.Rate = pi.PlaybackRate ?? 1;
            if (m.Playing) LastPlayingAt = clock.Now;
            if (was != m.Playing)
            {
                Log.D("media", $"[{m.AppName}] {(m.Playing ? "playing" : "paused")}");
                // re-anchor the extrapolated position at the moment of the change
                m.Position = m.LivePosition(clock.Now); m.PositionStamp = clock.Now;
                RefreshTimeline(id);
            }
        }
        catch (Exception ex) { Log.D("media", $"playback info failed for {id}: {ex.Message}"); }
    }

    /// <summary>Re-reads the session's timeline; true if that moved what the island shows.</summary>
    bool RefreshTimeline(string id)
    {
        if (!sessions.TryGetValue(id, out var s) || !infos.TryGetValue(id, out var m)) return false;
        try
        {
            double predicted = m.LivePosition(clock.Now), oldDuration = m.Duration;
            var tl = s.GetTimelineProperties();
            if (tl == null) return false;   // Windows has none for a moment (a track changing): read again next time
            double start = tl.StartTime.TotalSeconds, end = tl.EndTime.TotalSeconds;
            m.Duration = Math.Max(0, end - start);
            double pos = tl.Position.TotalSeconds - start;
            // Apps report the position as of LastUpdatedTime; move it to "now".
            double age = (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
            if (age < 0 || age > 86400 || tl.LastUpdatedTime.Year < 2000) age = 0;
            if (id == seekFor)
            {
                double target = seekTo + (m.Playing ? clock.Now - seekAt : 0), reported = pos + (m.Playing ? age : 0);
                if (StaleAfterSeek(target, reported, clock.Now, seekHoldUntil)) return false;   // from before the seek: keep the bar where it was sent
                seekFor = null;   // it arrived (or the hold ran out): reports are believed again
            }
            m.Position = Math.Max(0, pos);
            m.PositionStamp = clock.Now - (m.Playing ? age : 0);
            return TimelineMoved(predicted, m.LivePosition(clock.Now), oldDuration, m.Duration);
        }
        catch (Exception ex) { Log.D("media", $"timeline failed for {id}: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Whether a timeline report changes anything on screen. Spotify reports its
    /// position every four or five seconds, paused or not, and the progress bar
    /// already extrapolates between reports - so a report that agrees with that
    /// is news to nobody. Treating every one as a change re-laid-out and redrew
    /// the resting island each time, which also counted as activity and kept
    /// re-arming the idle trim: a full GC every 4.5 s, all day. Only a seek (more
    /// than a second off the prediction) or a new duration counts.
    /// </summary>
    internal static bool TimelineMoved(double predicted, double reported, double oldDuration, double newDuration) =>
        Math.Abs(reported - predicted) > 1.0 || Math.Abs(newDuration - oldDuration) > 0.5;

    void Choose()
    {
        if (PinReleased(pinned, pinnedAt, infos.Values)) { Log.D("media", "another app started playing: the hand-picked one lets go"); pinned = null; }
        var ignored = Ignored();
        var (pick, recheckAt) = Pick(Current, infos.Values.ToList(), pinned, SafeCurrentId(), i => IsIgnored(i, ignored), clock.Now, Heard);
        Ui.Cancel(ref recheck);
        if (!double.IsNaN(recheckAt)) recheck = Ui.After(Math.Max(0.05, recheckAt - clock.Now + 0.02), Choose);
        if (!ReferenceEquals(pick, Current))
        {
            var old = Current;
            Current = pick;
            Log.D("media", $"showing {(pick == null ? "nothing" : pick.AppName + ": " + pick.Title)}");
            if (pick != null && !string.IsNullOrEmpty(pick.Title) && old?.TrackKey != pick.TrackKey) TrackChanged?.Invoke(old, pick);
        }
        Changed?.Invoke();
    }

    /// <summary>How long a session that just started playing has to keep playing before it takes over the island.</summary>
    internal const double Settle = 1.5, PlaceholderSettle = 4;

    /// <summary>
    /// Which session to show: one the user picked; else whatever is playing
    /// (Windows' "current" is often a paused tab); else Windows' current.
    /// Among those playing, one that can be heard comes first: Windows makes
    /// a session "current" when its track changes, so Spotify playing at 0%
    /// took the island from the video being watched at every new song.
    ///
    /// A session that has only just started playing doesn't take over from the
    /// one on screen until it has kept playing a moment (longer if it's only a
    /// "site is playing media" placeholder). Hovering videos on a profile page
    /// opens and closes a session every few hundred ms; without the wait the
    /// island switched to it and back each time, re-coloring its glow on every
    /// switch, two or three times a second. Returns the pick and, while a
    /// newcomer is waiting, when to look again (NaN otherwise).
    /// </summary>
    internal static (MediaInfo pick, double recheckAt) Pick(MediaInfo current, IReadOnlyList<MediaInfo> all, string pinned,
        string windowsCurrent, Func<MediaInfo, bool> ignored, double now, Func<MediaInfo, bool?> heard = null)
    {
        heard ??= _ => null;
        if (pinned != null && all.FirstOrDefault(i => i.SessionId == pinned) is { } pin) return (pin, double.NaN);
        var cand = all.Where(i => !ignored(i) && !string.IsNullOrEmpty(i.Title)).ToList();
        bool currentAlive = current != null && cand.Contains(current);
        // 4 playing and heard, 3 playing where Isola can't measure it (counted as heard, as before),
        // 2 the one on screen whatever it's doing (pausing it mustn't swap it for something silent),
        // 1 playing but silent (Spotify at 0% in its own app), 0 the rest
        int Rank(MediaInfo i)
        {
            if (i.Playing) { var h = heard(i); if (h == true) return 4; if (h == null) return 3; }
            if (ReferenceEquals(i, current)) return 2;
            return i.Playing ? 1 : 0;
        }
        // ties: among those playing aloud, the one started last (starting something is a choice);
        // otherwise the one on screen, then the one Windows calls current
        bool Before(MediaInfo a, MediaInfo b, int r)
        {
            if (r >= 3)
            {
                double sa = double.IsNaN(a.PlayingSince) ? double.NegativeInfinity : a.PlayingSince;
                double sb = double.IsNaN(b.PlayingSince) ? double.NegativeInfinity : b.PlayingSince;
                if (sa != sb) return sa > sb;
            }
            if (ReferenceEquals(a, current)) return true;
            if (ReferenceEquals(b, current)) return false;
            return a.SessionId == windowsCurrent && b.SessionId != windowsCurrent;
        }
        MediaInfo want = null; int best = -1;
        foreach (var i in cand)
        {
            int r = Rank(i);
            if (r > best || (r == best && Before(i, want, r))) { want = i; best = r; }
        }
        if (want == null)
        {
            var c = all.FirstOrDefault(i => i.SessionId == windowsCurrent);
            if (c != null && !ignored(c)) want = c;
        }
        // something that has only just started playing waits a moment before it takes over (a site's previews
        // open and close sessions every few hundred ms; without the wait the island flickered between them)
        if (currentAlive && want != null && !ReferenceEquals(want, current) && want.Playing)
        {
            // the one on screen is paused or silent and this one can be heard: it's what's being listened to, so it
            // shows now (it waited 1.5 s like a preview, which felt stuck when switching back and forth). Previews
            // play muted, so they still wait.
            if (heard(want) == true && (!current.Playing || heard(current) == false)) return (want, double.NaN);
            double settle = want.IsPlaceholder && !current.IsPlaceholder ? PlaceholderSettle : Settle;
            double since = double.IsNaN(want.PlayingSince) ? now : want.PlayingSince;
            if (now - since < settle) return (current, since + settle);
        }
        return (want, double.NaN);
    }

    static bool IsIgnored(MediaInfo i, IReadOnlyCollection<string> ignored) =>
        ignored.Any(x => !string.IsNullOrWhiteSpace(x) && (i.SessionId.Contains(x, StringComparison.OrdinalIgnoreCase) || i.AppName.Contains(x, StringComparison.OrdinalIgnoreCase)));

    string SafeCurrentId() { try { return mgr?.GetCurrentSession()?.SourceAppUserModelId; } catch { return null; } }

    GSession Target() => Current != null && sessions.TryGetValue(Current.SessionId, out var s) ? s : null;

    // Commands go to the app one after another: sent side by side, a quick
    // double click could reach Spotify in the wrong order.
    Task queue = Task.CompletedTask;

    void Do(string what, Func<GSession, Windows.Foundation.IAsyncOperation<bool>> op)
    {
        var s = Target();
        if (s == null) return;
        queue = queue.ContinueWith(async _ =>
        {
            try
            {
                bool ok = await op(s).AsTask().ConfigureAwait(false);
                Log.D("media", $"{what} -> {(ok ? "ok" : "refused by app")}");
            }
            catch (Exception ex) { Log.W("media", $"{what} failed: {ex.Message}"); }
        }, TaskScheduler.Default).Unwrap();
    }

    // What the user just chose for shuffle and repeat, held over the app's
    // reports for a few seconds. Right after a change Spotify still reports the
    // old value for a moment; taking that report undid the button, and a second
    // click a second later started from the stale state - so two clicks on
    // shuffle sent "on" twice, and three on repeat left it off instead of on
    // repeat-one (both seen in practice).
    string heldFor;
    bool? heldShuffle; int? heldRepeat;
    double shuffleHoldUntil, repeatHoldUntil;
    const double HoldSeconds = 3;

    /// <summary>The value to show: the held choice until the app reports the same or the hold runs out, then the app's.</summary>
    internal static T? Hold<T>(T? reported, T? held, double now, ref double holdUntil) where T : struct
    {
        if (held == null || now >= holdUntil) return reported;
        if (reported.HasValue && reported.Value.Equals(held.Value)) { holdUntil = 0; return reported; }
        return held;
    }

    // what play/pause asked for, held a moment against the app's late or repeated reports
    bool? heldPlaying; string heldPlayingFor; double playingHoldUntil;
    internal const double PlayHold = 1.5;

    /// <summary>
    /// Play or pause the shown session. The button answers at once; what was asked is sent as "play" or
    /// "pause", not "toggle" (a toggle sent while the app's report lagged flipped it the wrong way), held
    /// against stale reports for a moment, then read back from the app in case it never says.
    /// </summary>
    public void PlayPause()
    {
        var m = Current;
        if (m == null) return;
        bool play = !m.Playing;
        m.Position = m.LivePosition(clock.Now); m.PositionStamp = clock.Now;
        m.Playing = play; m.PlayingSince = play ? clock.Now : double.NaN;
        heldPlaying = play; heldPlayingFor = m.SessionId; playingHoldUntil = clock.Now + PlayHold;
        Changed?.Invoke();
        var s = Target();
        if (s != null)
            queue = queue.ContinueWith(async _ =>
            {
                try
                {
                    bool ok = await (play ? s.TryPlayAsync() : s.TryPauseAsync()).AsTask().ConfigureAwait(false);
                    if (!ok) ok = await s.TryTogglePlayPauseAsync().AsTask().ConfigureAwait(false);   // apps that only know "toggle"
                    Log.D("media", $"{(play ? "play" : "pause")} -> {(ok ? "ok" : "refused by app")}");
                }
                catch (Exception ex) { Log.W("media", $"play/pause failed: {ex.Message}"); }
            }, TaskScheduler.Default).Unwrap();
        string id = m.SessionId;
        Ui.After(0.4, () => { RefreshPlayback(id); Choose(); });
        Ui.After(PlayHold + 0.2, () => { RefreshPlayback(id); Choose(); });
    }
    public void Next() => Do("next", s => s.TrySkipNextAsync());
    public void Previous() => Do("previous", s => s.TrySkipPreviousAsync());
    // where a seek went, held a moment against reports from before the app applied it
    double seekTo, seekAt, seekHoldUntil; string seekFor;
    internal const double SeekHold = 1.5;

    /// <summary>
    /// A report after a seek that's still far from where it was sent: the app hasn't applied it yet. Held
    /// against for <see cref="SeekHold"/>, it would move the bar back for a moment (the flicker).
    /// </summary>
    internal static bool StaleAfterSeek(double target, double reported, double now, double holdUntil) => now < holdUntil && Math.Abs(reported - target) > 1.5;

    public void Seek(double seconds)
    {
        var m = Current; if (m == null) return;
        m.Position = seconds; m.PositionStamp = clock.Now;
        seekTo = seconds; seekAt = clock.Now; seekFor = m.SessionId; seekHoldUntil = clock.Now + SeekHold;
        Changed?.Invoke();
        Do($"seek {seconds:0.0}s", s =>
        {
            var tl = s.GetTimelineProperties();
            return s.TryChangePlaybackPositionAsync((long)((tl.StartTime.TotalSeconds + seconds) * 10_000_000));
        });
    }
    public void ToggleShuffle()
    {
        var m = Current; if (m?.Shuffle == null) return;
        bool want = !(m.Shuffle ?? false);
        m.Shuffle = want; heldFor = m.SessionId; heldShuffle = want; shuffleHoldUntil = clock.Now + HoldSeconds;
        Changed?.Invoke();
        Do($"shuffle {want}", s => s.TryChangeShuffleActiveAsync(want));
    }
    public void CycleRepeat()
    {
        var m = Current; if (m == null) return;
        // off -> all -> one -> off, as in Spotify; the button shows the new state at once
        var next = m.Repeat switch { 0 => Windows.Media.MediaPlaybackAutoRepeatMode.List, 2 => Windows.Media.MediaPlaybackAutoRepeatMode.Track, _ => Windows.Media.MediaPlaybackAutoRepeatMode.None };
        m.Repeat = next switch { Windows.Media.MediaPlaybackAutoRepeatMode.List => 2, Windows.Media.MediaPlaybackAutoRepeatMode.Track => 1, _ => 0 };
        heldFor = m.SessionId; heldRepeat = m.Repeat; repeatHoldUntil = clock.Now + HoldSeconds;
        Changed?.Invoke();
        Do($"repeat {next}", s => s.TryChangeAutoRepeatModeAsync(next));
    }
    public void Select(string sessionId) { pinned = sessionId; pinnedAt = clock.Now; Choose(); }
    double pinnedAt;

    /// <summary>
    /// A session picked by hand stays on the island until another one starts playing: starting something is
    /// a choice too, so the island then goes back to showing what can be heard.
    /// </summary>
    internal static bool PinReleased(string pinned, double pinnedAt, IEnumerable<MediaInfo> all) =>
        pinned != null && all.Any(i => i.SessionId != pinned && i.Playing && !double.IsNaN(i.PlayingSince) && i.PlayingSince > pinnedAt);

    // ---- helpers -----------------------------------------------------------

    static MediaInfo Clone(MediaInfo m) => new() { SessionId = m.SessionId, AppName = m.AppName, Title = m.Title, Artist = m.Artist, Album = m.Album, Art = m.Art, ArtKey = m.ArtKey, Palette = m.Palette };

    static int Hash(byte[] b)
    {
        unchecked { int h = 17; for (int i = 0; i < b.Length; i += Math.Max(1, b.Length / 512)) h = h * 31 + b[i]; return h; }
    }

    public static string FriendlyApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "Media";
        string a = aumid.ToLowerInvariant();
        if (a.Contains("spotify")) return "Spotify";
        if (a.Contains("chrome")) return "Chrome";
        if (a.Contains("msedge") || a.Contains("microsoftedge")) return "Edge";
        if (a.Contains("firefox")) return "Firefox";
        if (a.Contains("plex")) return "Plex";
        if (a.Contains("vlc")) return "VLC";
        if (a.Contains("applemusic") || a.Contains("apple.music")) return "Apple Music";
        if (a.Contains("zunemusic") || a.Contains("media.player")) return "Media Player";
        if (a.Contains("youtube")) return "YouTube";
        try
        {
            var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid);
            if (!string.IsNullOrWhiteSpace(info?.DisplayInfo?.DisplayName)) return info.DisplayInfo.DisplayName;
        }
        catch { }
        string n = Path.GetFileNameWithoutExtension(aumid.Split('!')[0].Split('\\')[^1]);
        return n.Length > 0 ? char.ToUpperInvariant(n[0]) + n[1..] : "Media";
    }

    /// <summary>Up to three vivid dominant colors of the artwork, for the glow and accents.</summary>
    public static Color4[] PaletteOf(byte[] img)
    {
        try
        {
            var px = Gfx.DecodeImage(img, frame =>
            {
                using var scaler = Gfx.Wic.CreateBitmapScaler();
                scaler.Initialize(frame, 24, 24, BitmapInterpolationMode.Fant);
                using var conv = Gfx.Wic.CreateFormatConverter();
                conv.Initialize(scaler, PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
                var buf = new byte[24 * 24 * 4];
                conv.CopyPixels(24 * 4, buf);
                return buf;
            });
            // coarse histogram over hue buckets, weighted by saturation, so the
            // glow picks the colors that make the cover recognizable, not the black border
            var buckets = new (double r, double g, double b, double w)[12];
            double gr = 0, gg = 0, gb = 0, gw = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                // app icons are mostly transparent corners, whose color means nothing
                if (px[i + 3] < 128) continue;
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                double sat = mx <= 0 ? 0 : (mx - mn) / mx;
                gr += r; gg += g; gb += b; gw++;
                if (mx < 0.12 || sat < 0.18) continue;
                double h = HueOf(r, g, b, mx, mn);
                int k = (int)(h / 30) % 12;
                double w = sat * sat * mx;
                buckets[k].r += r * w; buckets[k].g += g * w; buckets[k].b += b * w; buckets[k].w += w;
            }
            var top = buckets.Where(x => x.w > 0.4).OrderByDescending(x => x.w).Take(3)
                .Select(x => Col.Vivid(new Color4((float)(x.r / x.w), (float)(x.g / x.w), (float)(x.b / x.w), 1), 0.55f, 1.3f)).ToList();
            if (top.Count == 0 && gw == 0) return null;
            if (top.Count == 0) top.Add(Col.Vivid(new Color4((float)(gr / gw), (float)(gg / gw), (float)(gb / gw), 1), 0.5f, 1.2f));
            while (top.Count < 3) top.Add(Col.Mix(top[0], top[^1], 0.5f));
            return top.ToArray();
        }
        catch (Exception ex) { Log.D("media", "palette failed: " + ex.Message); return null; }
    }

    static double HueOf(double r, double g, double b, double mx, double mn)
    {
        double d = mx - mn; if (d <= 0) return 0;
        double h = mx == r ? (g - b) / d % 6 : mx == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60; return h < 0 ? h + 360 : h;
    }
}
