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
    public MediaInfo Current { get; private set; }
    public IReadOnlyList<MediaInfo> Sessions => infos.Values.ToList();
    public double LastPlayingAt { get; private set; } = -1e9;
    public event Action Changed;
    public event Action<MediaInfo, MediaInfo> TrackChanged;
    public Func<IReadOnlyCollection<string>> Ignored = () => Array.Empty<string>();
    public float Level => audio?.Peak ?? 0;

    public MediaService(IClock c, AudioService a) { clock = c; audio = a; }

    public async Task StartAsync()
    {
        try
        {
            mgr = await GSM.RequestAsync().AsTask().ConfigureAwait(false);
            mgr.SessionsChanged += (_, _) => Ui.Post(Resync);
            mgr.CurrentSessionChanged += (_, _) => Ui.Post(Resync);
            Ui.Post(Resync);
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
            bool was = m.Playing;
            m.Playing = pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
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
            double start = tl.StartTime.TotalSeconds, end = tl.EndTime.TotalSeconds;
            m.Duration = Math.Max(0, end - start);
            double pos = tl.Position.TotalSeconds - start;
            // Apps report the position as of LastUpdatedTime; move it to "now".
            double age = (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
            if (age < 0 || age > 86400 || tl.LastUpdatedTime.Year < 2000) age = 0;
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
        var ignored = Ignored();
        var (pick, recheckAt) = Pick(Current, infos.Values.ToList(), pinned, SafeCurrentId(), i => IsIgnored(i, ignored), clock.Now);
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
        string windowsCurrent, Func<MediaInfo, bool> ignored, double now)
    {
        MediaInfo want = null;
        if (pinned != null) want = all.FirstOrDefault(i => i.SessionId == pinned);
        want ??= all.FirstOrDefault(i => i.Playing && !ignored(i) && !string.IsNullOrEmpty(i.Title) && i.SessionId == windowsCurrent);
        want ??= all.FirstOrDefault(i => i.Playing && !ignored(i) && !string.IsNullOrEmpty(i.Title));
        if (want == null)
        {
            var c = all.FirstOrDefault(i => i.SessionId == windowsCurrent);
            if (c != null && !ignored(c)) want = c;
        }
        want ??= all.FirstOrDefault(i => !ignored(i) && !string.IsNullOrEmpty(i.Title));
        bool currentAlive = current != null && all.Contains(current) && !ignored(current);
        if (currentAlive && want != null && !ReferenceEquals(want, current) && want.SessionId != pinned && want.Playing)
        {
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

    public void PlayPause()
    {
        if (Current != null)
        {
            // optimistic: flip now so the button answers on the same frame
            Current.Position = Current.LivePosition(clock.Now); Current.PositionStamp = clock.Now;
            Current.Playing = !Current.Playing; Changed?.Invoke();
        }
        Do("play/pause", s => s.TryTogglePlayPauseAsync());
    }
    public void Next() => Do("next", s => s.TrySkipNextAsync());
    public void Previous() => Do("previous", s => s.TrySkipPreviousAsync());
    public void Seek(double seconds)
    {
        var m = Current; if (m == null) return;
        m.Position = seconds; m.PositionStamp = clock.Now; Changed?.Invoke();
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
    public void Select(string sessionId) { pinned = sessionId; Choose(); }

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
