using System.Text.Json;
using System.Text.Json.Serialization;
using Isola.App;

namespace Isola.Services;

/// <summary>
/// Development only (ISOLA_SHOWCASE_MEDIA=tracks.json): a stand-in for
/// what's playing, for the README's images and the Store trailer. It shows
/// real tracks (their names and covers from the file) "playing", with a beat
/// for the visualizer, so the recordings look like real use without playing
/// anything aloud or touching anyone's music app. Next and Previous move
/// through the list, so a track change can be recorded too.
/// </summary>
public sealed class ShowcaseMedia : IMediaSourceEx
{
    public sealed class Track
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string App { get; set; } = "Spotify";
        /// <summary>The cover, a JPEG or PNG beside the list.</summary>
        public string Art { get; set; } = "";
        public double Duration { get; set; } = 200;
        public double Position { get; set; } = 40;
        /// <summary>Beats a minute, for the visualizer.</summary>
        public double Bpm { get; set; } = 100;
    }

    readonly IClock clock;
    readonly List<Track> tracks;
    readonly string dir;
    int index;
    MediaInfo current;
    double startedAt;
    public event Action Changed;
    public event Action<MediaInfo, MediaInfo> TrackChanged;

    public ShowcaseMedia(string listFile, IClock c)
    {
        clock = c;
        dir = Path.GetDirectoryName(Path.GetFullPath(listFile))!;
        tracks = JsonSerializer.Deserialize(File.ReadAllText(listFile), ShowcaseJson.Default.ListTrack) ?? new();
        if (tracks.Count == 0) tracks.Add(new Track { Title = "Track", Artist = "Artist" });
        Load(0);
    }

    void Load(int i)
    {
        index = (i % tracks.Count + tracks.Count) % tracks.Count;
        var t = tracks[index];
        byte[] art = null;
        try { if (t.Art.Length > 0) art = File.ReadAllBytes(Path.Combine(dir, t.Art)); } catch (Exception ex) { Log.W("showcase", "cover: " + ex.Message); }
        startedAt = clock.Now;
        current = new MediaInfo
        {
            SessionId = t.App == "Spotify" ? "Spotify.exe" : t.App, AppName = t.App, Title = t.Title, Artist = t.Artist, Album = t.Album,
            Playing = true, Position = t.Position, PositionStamp = clock.Now, Duration = t.Duration, CanSeek = true, CanNext = true, CanPrev = true,
            Art = art, ArtKey = t.Title + t.Artist, Palette = art != null ? MediaService.PaletteOf(art) : null, PlayingSince = clock.Now,
        };
    }

    public MediaInfo Current => current;
    public IReadOnlyList<MediaInfo> Sessions => new[] { current };
    public double LastPlayingAt => clock.Now;

    /// <summary>A kick on every beat and a little in between, like a song's level.</summary>
    public float Level
    {
        get
        {
            if (!current.Playing) return 0;
            double beat = (clock.Now - startedAt) * tracks[index].Bpm / 60.0, f = beat - Math.Floor(beat);
            double kick = Math.Exp(-f * 6), off = 0.35 * Math.Exp(-Math.Abs(f - 0.5) * 9);
            double drift = 0.15 * Math.Sin(beat * 0.7) + 0.1 * Math.Sin(beat * 2.3);
            return (float)Math.Clamp(0.25 + 0.45 * kick + off * 0.5 + drift, 0, 1);
        }
    }

    void Move(int d)
    {
        var old = current;
        Load(index + d);
        TrackChanged?.Invoke(old, current);
        Changed?.Invoke();
    }

    public void PlayPause()
    {
        current.Position = current.Position + (current.Playing ? clock.Now - current.PositionStamp : 0);
        current.PositionStamp = clock.Now;
        current.Playing = !current.Playing;
        current.PlayingSince = current.Playing ? clock.Now : double.NaN;
        Changed?.Invoke();
    }
    public void Next() => Move(1);
    public void Previous() => Move(-1);
    public void Seek(double seconds) { current.Position = seconds; current.PositionStamp = clock.Now; Changed?.Invoke(); }
    public void ToggleShuffle() { }
    public void CycleRepeat() { }
    public void Select(string sessionId) { }
}

[JsonSerializable(typeof(List<ShowcaseMedia.Track>))]
internal partial class ShowcaseJson : JsonSerializerContext { }
