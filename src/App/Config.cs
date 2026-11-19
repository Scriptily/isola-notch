using System.Text.Json;
using System.Text.Json.Serialization;

namespace Isola.App;

/// <summary>
/// Every setting, with its default. The settings window edits this; the file
/// in the data folder (isola.config.json, gitignored; see Paths) persists it, and
/// isola.config.example.json is generated from these defaults.
/// Unknown keys are ignored and missing keys take the default, so old configs
/// keep working across versions.
/// </summary>
public sealed class Config
{
    // ---- look -------------------------------------------------------------
    public string Style { get; set; } = "pill";            // "pill" (a floating island) or "notch" (blends into the top edge)
    public string IdleFace { get; set; } = "eyes";         // "eyes", "clock" or "none"
    public bool EyesFollowCursor { get; set; } = true;
    public bool Glow { get; set; } = true;                 // colorful rim along the island's lower edge
    public double GlowIntensity { get; set; } = 0.75;
    public string GlowColors { get; set; } = "auto";       // "auto" (album art), "aurora", "sunset", "ocean", "mono"
    public bool GlowDrift { get; set; } = true;            // the colors drift slowly left and right
    public double Scale { get; set; } = 1.0;               // whole island, on top of Windows' display scaling
    public bool ReduceMotion { get; set; } = false;        // springs become short cross-fades
    public bool HelloOnLaunch { get; set; } = true;

    // ---- behavior ---------------------------------------------------------
    public bool HoverToExpand { get; set; } = true;
    public int HoverDelayMs { get; set; } = 450;   // of the pointer resting on the island (see Island.ArmPeek)
    public int CollapseDelayMs { get; set; } = 450;
    public double AutoHideMinutes { get; set; } = 2.0;     // 0 = never hide
    public bool StayWhilePlaying { get; set; } = true;     // playing media holds off the auto-hide
    public bool ShowIndicatorWhenHidden { get; set; } = true;
    public int RevealZoneWidth { get; set; } = 200;        // DIPs either side of center the cursor can reveal it from
    public int RevealZoneHeight { get; set; } = 6;
    public string ShowOn { get; set; } = "laptop";         // "laptop" (built-in panel), "primary", or a display's device path
    public bool HideInFullscreen { get; set; } = true;     // the island's screen only
    public bool AlertsOverFullscreen { get; set; } = false;
    public bool StartWithWindows { get; set; } = true;
    public bool KeepOnTop { get; set; } = true;

    // ---- keys -------------------------------------------------------------
    public string SearchHotkey { get; set; } = "Alt+Space";
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+I";
    public bool ReleaseHotkeysInFullscreen { get; set; } = true;

    // ---- live activities --------------------------------------------------
    public bool Media { get; set; } = true;
    public bool MediaWaveform { get; set; } = true;
    public bool ShowOnTrackChange { get; set; } = true;
    public string SpotifyClientId { get; set; } = "";      // your own app at developer.spotify.com; the sign-in itself is kept in spotify.json
    public bool SpotifyVolume { get; set; } = true;        // Spotify's in-app volume under Now Playing
    public List<string> MediaIgnoredApps { get; set; } = new();
    public bool BluetoothAlerts { get; set; } = true;
    public bool AirPods { get; set; } = true;
    public bool UseMagicPods { get; set; } = true;
    public bool DisplayAlerts { get; set; } = true;
    public bool BatteryAlerts { get; set; } = true;
    public int LowBatteryPercent { get; set; } = 20;
    public bool VolumeHud { get; set; } = true;
    public bool BrightnessHud { get; set; } = false;       // off by default: screen-dimming tools would trip it on every fade
    public bool CapsLockHud { get; set; } = true;
    public bool KeyboardLayoutHud { get; set; } = true;
    public bool Notifications { get; set; } = false;
    public bool NotificationPreviews { get; set; } = true;
    /// <summary>While the island shows notifications, Windows' own banners are off (each one appears once).</summary>
    public bool HideWindowsBanners { get; set; } = true;
    /// <summary>The island's own sounds: master switch and level (0..1).</summary>
    public bool Sounds { get; set; } = true;
    /// <summary>How loud Isola's sounds are, as a multiple of the level they were tuned at: 0.5 to 1.5.</summary>
    public double SoundLevel { get; set; } = 1;
    /// <summary>The volume setting before 1.5 (0 to 1, 0.6 by default): read once and turned into SoundLevel.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? SoundVolume { get; set; }
    public bool NotificationSound { get; set; } = true;
    public bool CallRingtone { get; set; } = true;
    public bool DeviceSounds { get; set; } = true;
    /// <summary>Which of each kind's sounds (Sounds.Choices).</summary>
    public string NotificationTone { get; set; } = "bloom";
    public string Ringtone { get; set; } = "classic-warm";
    public string TimerTone { get; set; } = "sweep-triple";
    /// <summary>Connecting and disconnecting (one choice: each sound has a rising and a falling form).</summary>
    public string DeviceTone { get; set; } = "bubble";
    public string ChargeTone { get; set; } = "energy";
    public string LowTone { get; set; } = "sink";
    /// <summary>A soft sound for notices: fully charged, a display or sound output change, updates, the download tools.</summary>
    public bool NoticeSound { get; set; } = true;
    public string NoticeTone { get; set; } = "drop";
    public string SoundsWhilePlaying { get; set; } = "full";   // "full", "quiet" or "mute": the island's sounds while media plays
    public bool HoldWhileFocused { get; set; } = false;        // notices wait while media plays or something is fullscreen, then one summary
    /// <summary>Downloading and converting (yt-dlp and FFmpeg, fetched and kept up to date by Isola).</summary>
    public bool Downloads { get; set; } = true;
    /// <summary>Where finished files go: downloads (the Downloads folder), shelf, or folder (DownloadFolder).</summary>
    public string DownloadTo { get; set; } = "downloads";
    public string DownloadFolder { get; set; } = "";
    /// <summary>The most a video is downloaded at: 1080, 720 or best.</summary>
    public string DownloadQuality { get; set; } = "1080";
    /// <summary>What "download audio" makes: mp3 or m4a.</summary>
    public string DownloadAudioFormat { get; set; } = "mp3";
    public List<string> NotificationMutedApps { get; set; } = new();
    public bool Calls { get; set; } = true;
    public int AlertSeconds { get; set; } = 5;

    // ---- widgets ----------------------------------------------------------
    public bool Weather { get; set; } = true;
    public string WeatherSource { get; set; } = "auto";    // "auto" (Home Assistant, else Open-Meteo), "homeassistant", "openmeteo"
    public string WeatherEntity { get; set; } = "";        // empty = first weather.* entity
    public double Latitude { get; set; } = double.NaN;
    public double Longitude { get; set; } = double.NaN;
    public string City { get; set; } = "";
    public bool Fahrenheit { get; set; } = false;
    public bool Use24Hour { get; set; } = false;
    public bool Notes { get; set; } = true;
    public bool Shelf { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    public int ClipboardHistory { get; set; } = 24;
    public bool Timer { get; set; } = true;
    public bool TimerSound { get; set; } = true;            // the alarm when a timer ends (until dismissed, a minute at most)
    public bool Search { get; set; } = true;
    public int SearchResults { get; set; } = 8;

    // ---- Home Assistant ---------------------------------------------------
    public bool HomeAssistant { get; set; } = true;
    public string HaBaseUrl { get; set; } = "";
    public string HaToken { get; set; } = "";              // environment ISOLA_HA_TOKEN wins
    public List<string> HaEntities { get; set; } = new();
    public int HaRefreshSeconds { get; set; } = 10;

    // ---- updates ------------------------------------------------------------
    public bool AutoUpdate { get; set; } = true;
    public double UpdateCheckHours { get; set; } = 6;

    // ---- diagnostics ------------------------------------------------------
    public bool VerboseLog { get; set; } = false;   // the detailed log is for diagnosing; off by default

    [JsonIgnore] public string LoadError { get; private set; }

    /// <summary>
    /// Settings this version doesn't know - written by a newer one, or by the
    /// user for one - kept and written back untouched. Without this, saving
    /// dropped them: 1.0.3, starting up, rewrote the file without the Spotify
    /// client ID it had no field for, a minute before updating itself to the
    /// 1.0.4 that needed it, and Spotify answered invalid_client.
    /// </summary>
    [JsonExtensionData] public Dictionary<string, JsonElement> Unknown { get; set; }

    public Config Clone() => JsonSerializer.Deserialize(JsonSerializer.Serialize(this, ConfigJson.Default.Config), ConfigJson.Default.Config);

    public static Config Load(string file)
    {
        Config c;
        string err = null;
        try
        {
            c = File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), ConfigJson.Default.Config) ?? new Config() : new Config();
        }
        catch (Exception ex)
        {
            // A typo in a hand-edited file must not stop the island starting:
            // run on defaults and say so, and keep the broken file for the user.
            err = ex.Message;
            c = new Config();
            try { File.Copy(file, file + ".broken", true); } catch { }
        }
        c.LoadError = err;
        bool plain = !string.IsNullOrWhiteSpace(c.HaToken) && !Secret.IsProtected(c.HaToken);
        c.HaToken = Secret.Unprotect(c.HaToken);   // stored encrypted (see Secret)
        c.Normalize();
        // A token saved before encryption (or typed into the file) is encrypted
        // now, not whenever a setting next changes, which could be never.
        if (plain && err == null) { try { c.Save(file); } catch (Exception ex) { Log.W("config", "couldn't re-save the config encrypted: " + ex.Message); } }
        string envToken = Environment.GetEnvironmentVariable("ISOLA_HA_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken)) c.HaToken = envToken.Trim();
        string envUrl = Environment.GetEnvironmentVariable("ISOLA_HA_URL");
        if (!string.IsNullOrWhiteSpace(envUrl)) c.HaBaseUrl = envUrl.Trim();
        return c;
    }

    /// <summary>Which of the changes to defaults below this file has been brought up to.</summary>
    public int Revision { get; set; }

    public void Normalize()
    {
        // 1.6.0: the island got in the way. It peeked after 200 ms of a resting pointer and came back from
        // hiding anywhere along 840 px of the top edge. A file still on those old defaults moves to the new
        // ones; values someone chose stay.
        if (Revision < 1)
        {
            if (HoverDelayMs == 200) HoverDelayMs = 450;
            if (RevealZoneWidth == 420) RevealZoneWidth = 200;
            Revision = 1;
        }
        Style = Style is "notch" ? "notch" : "pill";
        IdleFace = IdleFace is "clock" or "none" ? IdleFace : "eyes";
        SoundsWhilePlaying = SoundsWhilePlaying is "quiet" or "mute" ? SoundsWhilePlaying : "full";
        GlowIntensity = Math.Clamp(double.IsFinite(GlowIntensity) ? GlowIntensity : 0.75, 0, 1);
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1, 0.7, 1.6);
        AutoHideMinutes = Math.Clamp(double.IsFinite(AutoHideMinutes) ? AutoHideMinutes : 2, 0, 120);
        HoverDelayMs = Math.Clamp(HoverDelayMs, 0, 2000);
        if (SoundVolume is double old) { SoundLevel = double.IsFinite(old) && old > 0 ? old / 0.6 : 1; SoundVolume = null; }
        SoundLevel = double.IsFinite(SoundLevel) ? Math.Clamp(SoundLevel, 0.5, 1.5) : 1;
        NotificationTone = Services.Sounds.Valid(Services.Sounds.Notification, NotificationTone);
        Ringtone = Services.Sounds.Valid(Services.Sounds.Call, Ringtone);
        TimerTone = Services.Sounds.Valid(Services.Sounds.Timer, TimerTone);
        DeviceTone = Services.Sounds.Valid(Services.Sounds.Connect, DeviceTone);
        ChargeTone = Services.Sounds.Valid(Services.Sounds.Charge, ChargeTone);
        LowTone = Services.Sounds.Valid(Services.Sounds.Low, LowTone);
        DownloadTo = DownloadTo is "shelf" or "folder" ? DownloadTo : "downloads";
        DownloadFolder = string.IsNullOrWhiteSpace(DownloadFolder) || !Path.IsPathFullyQualified(DownloadFolder) ? "" : DownloadFolder.Trim();
        if (DownloadTo == "folder" && DownloadFolder.Length == 0) DownloadTo = "downloads";
        DownloadQuality = DownloadQuality is "720" or "best" ? DownloadQuality : "1080";
        DownloadAudioFormat = DownloadAudioFormat == "m4a" ? "m4a" : "mp3";
        CollapseDelayMs = Math.Clamp(CollapseDelayMs, 0, 5000);
        RevealZoneWidth = Math.Clamp(RevealZoneWidth, 80, 2000);
        RevealZoneHeight = Math.Clamp(RevealZoneHeight, 2, 40);
        AlertSeconds = Math.Clamp(AlertSeconds, 2, 30);
        LowBatteryPercent = Math.Clamp(LowBatteryPercent, 5, 50);
        ClipboardHistory = Math.Clamp(ClipboardHistory, 0, 100);
        SearchResults = Math.Clamp(SearchResults, 3, 12);
        HaRefreshSeconds = Math.Clamp(HaRefreshSeconds, 3, 300);
        UpdateCheckHours = Math.Clamp(double.IsFinite(UpdateCheckHours) ? UpdateCheckHours : 6, 1, 168);
        HaBaseUrl = (HaBaseUrl ?? "").Trim().TrimEnd('/');
        HaToken = (HaToken ?? "").Trim();
        SpotifyClientId = (SpotifyClientId ?? "").Trim();
        HaEntities ??= new(); MediaIgnoredApps ??= new(); NotificationMutedApps ??= new();
        WeatherSource = WeatherSource is "homeassistant" or "openmeteo" ? WeatherSource : "auto";
    }

    public void Save(string file)
    {
        Normalize();
        string tmp = file + ".tmp";
        // the token goes to disk encrypted for this Windows user; in memory it stays plain
        var disk = Clone();
        disk.HaToken = Secret.Protect(HaToken);
        File.WriteAllText(tmp, JsonSerializer.Serialize(disk, ConfigJson.Default.Config));
        File.Move(tmp, file, true);   // atomic: a crash mid-save never leaves half a config
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class ConfigJson : JsonSerializerContext { }
