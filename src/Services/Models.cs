using Vortice.Mathematics;

namespace Isola.Services;

/// <summary>One media session as the island sees it (Spotify, a YouTube tab in Chrome, Plex...).</summary>
public sealed class MediaInfo
{
    public string SessionId = "";      // SourceAppUserModelId
    public string AppName = "";
    public bool IsSpotify => SessionId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) || AppName.Equals("Spotify", StringComparison.OrdinalIgnoreCase);
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public bool Playing;
    public double Position;            // seconds, as of PositionStamp
    public double PositionStamp;       // monotonic seconds when Position was true
    public double Duration;            // 0 = unknown / live
    public double Rate = 1;
    public bool CanSeek, CanNext, CanPrev, CanPlayPause = true;
    public bool? Shuffle; public int Repeat; // 0 none, 1 track, 2 list
    public byte[] Art;                 // encoded image (jpg/png) or null
    public string ArtKey = "";         // changes when the art changes
    public Color4[] Palette;           // dominant colors of the art, vivid
    public bool IsVideo;
    public double PlayingSince = double.NaN;   // monotonic seconds it last started playing; NaN while paused

    public string TrackKey => $"{SessionId}|{Title}|{Artist}";

    /// <summary>
    /// The main artist, without the ones featured: "mgk, Avril Lavigne" and "mgk feat. X" are "mgk". Not cut at
    /// "&amp;", which is part of many band names (Simon &amp; Garfunkel).
    /// </summary>
    public static string MainArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        string a = artist.Trim();
        foreach (var sep in new[] { ", ", "; ", " / ", " feat. ", " feat ", " ft. ", " ft ", " featuring ", " with ", " x " })
        {
            int i = a.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 0) a = a[..i];
        }
        return a.Trim();
    }

    /// <summary>A session with nothing to show but the app: Chrome's "A site is playing media", an empty title, or the app's own name.</summary>
    public bool IsPlaceholder =>
        string.IsNullOrWhiteSpace(Title) || Title.Contains("is playing media", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Title.Trim(), AppName?.Trim(), StringComparison.OrdinalIgnoreCase);
    public double LivePosition(double now) =>
        Duration <= 0 ? Position : Math.Clamp(Position + (Playing ? (now - PositionStamp) * Rate : 0), 0, Duration);
}

public interface IMediaSource
{
    MediaInfo Current { get; }
    IReadOnlyList<MediaInfo> Sessions { get; }
    event Action Changed;                // anything about the current session (UI thread)
    event Action<MediaInfo, MediaInfo> TrackChanged;   // old, new (UI thread)
    void PlayPause(); void Next(); void Previous();
    void Seek(double seconds);
    void ToggleShuffle(); void CycleRepeat();
    void Select(string sessionId);
    float Level { get; }                 // output peak 0..1, for the waveform
    /// <summary>Sound has come from what's shown in the last moment: the visualizer moves only then.</summary>
    bool Sounding => true;
}

public enum HudKind { Volume, Brightness, CapsLock, Layout, Mute, Mic }

public sealed class AlertAction
{
    public string Label; public char Glyph; public Action Run; public Color4 Tint; public bool Primary;
}

/// <summary>A transient banner: Bluetooth connect, a display plugged in, a notification, a call.</summary>
public sealed class Alert
{
    public string Key = "";            // a newer alert with the same key replaces the queued one
    public string Kind = "info";       // bluetooth, airpods, display, battery, notification, call, timer, ha, info, error
    public string Title = "";
    public string Subtitle = "";
    public string Detail = "";         // third line (notification body)
    public char Glyph = '\0';
    public byte[] Image;               // app icon / avatar
    public Color4 Accent = new(0.35f, 0.6f, 1f, 1f);
    public double Seconds = 5;
    public int Priority;               // 3 = call, 2 = timer (both interrupt anything), 1 = high, 0 = normal
    public List<AlertAction> Actions = new();
    public Action Clicked;
    /// <summary>Called once when the alert leaves, however it goes (timeout, dismissed, clicked): the timer's alarm stops here.</summary>
    public Action Ended;
    /// <summary>Called each time it comes on screen (again, after being interrupted): the timer's alarm starts here.</summary>
    public Action Shown;
    /// <summary>Called when something more urgent pushes it off screen; it is queued again and Shown when it returns.</summary>
    public Action Suspended;
    /// <summary>A sticky alert gives up after this long (0: never), in case its end is never seen: a call's notification removal can be missed.</summary>
    public double MaxSeconds;
    /// <summary>For the "N more notifications" summary: how many it stands for, and from which apps.</summary>
    public int Folded;
    public List<string> FoldedFrom = new();
    // AirPods-style accessory: up to three labeled battery rings
    public List<(string label, int percent, bool charging)> Batteries = new();
    /// <summary>The glow's colors while it shows (from the app icon); worked out when it first comes on screen.</summary>
    public Color4[] Palette;
    /// <summary>The island's sound for it (Sounds.*), played when it comes on screen; null for none.</summary>
    public string Sound;
    public double Created;
    public bool Sticky;                // stays until dismissed or resolved (calls)
}
