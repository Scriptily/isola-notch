using System.Text.Json;
using System.Text.Json.Serialization;
using Isola.App;
using Isola.Native;
using Vortice.Mathematics;
using WinRT;

namespace Isola.Services;

/// <summary>
/// Every service the island draws from. Each one is optional: a feature whose
/// dependency is missing (Everything not running, MagicPods not installed, Home
/// Assistant unreachable, no Bluetooth radio) is simply absent or reports
/// itself unavailable; nothing else is affected.
/// </summary>
public sealed class Hub
{
    public IMediaSourceEx Media;
    public CountdownTimer Timer;
    public WeatherService Weather;
    public BatteryService Battery;
    public ShelfStore Shelf;
    public ClipboardHistory Clipboard;
    public NotesStore Notes;
    public HomeAssistant Ha;
    public FileSearch Search;
    public BluetoothService Bluetooth;
    public AirPodsService AirPods;
    public NotificationFeed Notifications;
    public AudioService Audio;
    public Updater Updates;
    public SpotifyService Spotify;
}

public interface IMediaSourceEx : IMediaSource
{
    double LastPlayingAt { get; }
}

// ============================================================================
// Focus timer (Pomodoro)
// ============================================================================

/// <summary>
/// A countdown timer of any length. It keeps going across a restart or an
/// update: the end time is saved as a wall-clock moment, so a timer that ran
/// out while the island wasn't running is reported as such when it starts.
/// </summary>
public sealed class CountdownTimer
{
    readonly IClock clock;
    public Func<DateTime> Wall = () => DateTime.Now;
    public string File;
    public bool Running { get; private set; }
    public bool Paused { get; private set; }
    /// <summary>The whole length, in seconds (grows with +1 min).</summary>
    public double Length { get; private set; }
    /// <summary>The length last started: the island offers it again.</summary>
    public double Last { get; private set; }
    double endsAt, pausedLeft;
    long timer;
    /// <summary>The timer ran out: its length, and whether that happened while the island wasn't running.</summary>
    public event Action<double, bool> Finished;
    public event Action Changed;

    public CountdownTimer(IClock c) { clock = c; }

    public double Remaining(double now) => !Running ? 0 : Math.Max(0, Paused ? pausedLeft : endsAt - now);
    public double Fraction(double now) => !Running || Length <= 0 ? 0 : 1 - Remaining(now) / Length;
    public DateTime EndsAt(double now) => Wall().AddSeconds(Remaining(now));

    public void Start(double seconds)
    {
        Length = Last = Math.Clamp(seconds, 1, 24 * 3600);
        endsAt = clock.Now + Length; Running = true; Paused = false;
        Arm(); Save();
        Log.I("timer", $"started: {Describe(Length)}");
        Changed?.Invoke();
    }

    public void PauseResume()
    {
        if (!Running) return;
        if (Paused) { endsAt = clock.Now + pausedLeft; Paused = false; Arm(); }
        else { pausedLeft = Math.Max(0, endsAt - clock.Now); Paused = true; Ui.Cancel(ref timer); }
        Save(); Changed?.Invoke();
    }

    public void AddMinute()
    {
        if (!Running) return;
        Length += 60;
        if (Paused) pausedLeft += 60; else { endsAt += 60; Arm(); }
        Save(); Changed?.Invoke();
    }

    public void Cancel()
    {
        if (!Running) return;
        Running = Paused = false; Ui.Cancel(ref timer);
        Log.I("timer", "canceled");
        Save(); Changed?.Invoke();
    }

    void Arm()
    {
        Ui.Cancel(ref timer);
        timer = Ui.After(Math.Max(0, endsAt - clock.Now), Fire);
    }

    void Fire()
    {
        if (!Running || Paused) return;
        Running = false; Save();
        Log.I("timer", $"finished: {Describe(Length)}");
        Finished?.Invoke(Length, false);
        Changed?.Invoke();
    }

    // ---- surviving a restart -------------------------------------------------------

    void Save()
    {
        if (File == null) return;
        try
        {
            var js = new System.Text.Json.Nodes.JsonObject { ["length"] = Length, ["last"] = Last };
            if (Running && Paused) js["paused_left"] = pausedLeft;
            else if (Running) js["ends_at"] = Wall().AddSeconds(endsAt - clock.Now).ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            System.IO.File.WriteAllText(File + ".tmp", js.ToJsonString());
            System.IO.File.Move(File + ".tmp", File, true);
        }
        catch (Exception ex) { Log.W("timer", "couldn't save: " + ex.Message); }
    }

    /// <summary>Picks up a timer from before a restart; one that ran out meanwhile is reported (Finished, whileClosed).</summary>
    public void Load()
    {
        if (File == null || !System.IO.File.Exists(File)) return;
        try
        {
            var js = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(File));
            Length = js?["length"]?.GetValue<double>() ?? 0;
            Last = js?["last"]?.GetValue<double>() ?? Length;
            if (js?["paused_left"] is { } pl) { pausedLeft = pl.GetValue<double>(); Running = Paused = true; Log.I("timer", $"restored paused, {Describe(pausedLeft)} left"); }
            else if (js?["ends_at"] is { } ea && DateTime.TryParse(ea.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
            {
                double left = (at.ToLocalTime() - Wall()).TotalSeconds;
                if (left > 0) { endsAt = clock.Now + left; Running = true; Paused = false; Arm(); Log.I("timer", $"restored, {Describe(left)} left"); }
                else { Running = false; Save(); Log.I("timer", "ran out while the island wasn't running"); Ui.Post(() => Finished?.Invoke(Length, true)); }
            }
        }
        catch (Exception ex) { Log.W("timer", "couldn't restore: " + ex.Message); }
        Changed?.Invoke();
    }

    // ---- reading and writing lengths ---------------------------------------------

    /// <summary>
    /// A typed length in seconds: "10" (minutes), "90s", "5m", "1h 20m", "1h20",
    /// "1.5h", "1:30" (m:s), "1:02:03" (h:m:s). Null for anything else, or for zero.
    /// </summary>
    public static double? Parse(string text)
    {
        string s = (text ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length is < 2 or > 3 || parts.Any(x => !int.TryParse(x.Trim(), System.Globalization.NumberStyles.None, inv, out _))) return null;
            var n = parts.Select(x => int.Parse(x.Trim(), inv)).ToArray();
            double secs = n.Length == 2 ? n[0] * 60 + n[1] : n[0] * 3600 + n[1] * 60 + n[2];
            return secs > 0 && secs <= 24 * 3600 ? secs : null;
        }
        var m = System.Text.RegularExpressions.Regex.Matches(s, @"(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)?");
        string rest = System.Text.RegularExpressions.Regex.Replace(s, @"(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)?", "").Replace("and", "").Replace(",", "");
        if (m.Count == 0 || rest.Trim().Length > 0) return null;
        double total = 0; string prev = null;
        for (int i = 0; i < m.Count; i++)
        {
            double v = double.Parse(m[i].Groups[1].Value, inv);
            string u = m[i].Groups[2].Value;
            if (u.Length == 0) u = m.Count == 1 ? "m" : prev == "h" ? "m" : prev == "m" ? "s" : null;   // "10", "1h20", "5m30"
            if (u == null) return null;
            char k = u[0];
            total += k == 'h' ? v * 3600 : k == 'm' ? v * 60 : v;
            prev = k.ToString();
        }
        return total > 0 && total <= 24 * 3600 ? Math.Round(total) : null;
    }

    /// <summary>"10 min", "1 h 20 min", "1 min 30 s", "45 s".</summary>
    public static string Describe(double seconds)
    {
        int t = (int)Math.Round(seconds), h = t / 3600, mi = t % 3600 / 60, se = t % 60;
        var parts = new List<string>();
        if (h > 0) parts.Add($"{h} h");
        if (mi > 0) parts.Add($"{mi} min");
        if (se > 0 && h == 0) parts.Add($"{se} s");
        return parts.Count == 0 ? "0 s" : string.Join(" ", parts);
    }
}

// ============================================================================
// Notes / to-dos
// ============================================================================

public sealed class Note
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

public sealed class NotesStore
{
    readonly string file;
    public List<Note> Items { get; private set; } = new();
    public event Action Changed;
    public NotesStore(string path)
    {
        file = path;
        try { if (File.Exists(file)) Items = JsonSerializer.Deserialize(File.ReadAllText(file), StoreJson.Default.ListNote) ?? new(); }
        catch (Exception ex) { Log.W("notes", "notes.json unreadable, starting empty (kept as .broken): " + ex.Message); try { File.Copy(file, file + ".broken", true); } catch { } }
    }
    public void Add(string text) { text = text?.Trim(); if (string.IsNullOrEmpty(text)) return; Items.Insert(0, new Note { Text = text }); Save(); }
    public void Toggle(Note n) { n.Done = !n.Done; Save(); }
    public void Remove(Note n) { Items.Remove(n); Save(); }
    public void ClearDone() { Items.RemoveAll(n => n.Done); Save(); }
    void Save()
    {
        try { var tmp = file + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(Items, StoreJson.Default.ListNote)); File.Move(tmp, file, true); }
        catch (Exception ex) { Log.W("notes", "save failed: " + ex.Message); }
        Changed?.Invoke();
    }
}

// ============================================================================
// Shelf: your own safe storage (files, text, links, images)
// ============================================================================

public sealed class ShelfItem
{
    public string Kind { get; set; } = "file";     // file, text, link, image
    public string Path { get; set; } = "";         // file/folder, or the stored image file
    public string Text { get; set; } = "";         // text snippet, or the link URL
    public string Title { get; set; } = "";        // link title when the browser gave one
    public DateTime Added { get; set; } = DateTime.Now;
    [JsonIgnore] public string Name => Kind switch
    {
        "text" => OneLine(Text, 80),
        "link" => string.IsNullOrWhiteSpace(Title) ? Text : Title,
        "image" => "Image",
        _ => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path,
    };
    static string OneLine(string t, int max) { t = t.Replace("\r", " ").Replace("\n", " ").Trim(); return t.Length > max ? t[..max] + "…" : t; }
    [JsonIgnore] public bool IsFolder => Kind == "file" && Directory.Exists(Path);
    [JsonIgnore] public bool Exists => Kind is "text" or "link" || File.Exists(Path) || Directory.Exists(Path);
}

/// <summary>
/// The shelf: things you drop on the island to keep while you work, separate
/// from the clipboard so nothing you copy later can push them out. Files are
/// kept by reference (dropping a 40 GB folder costs nothing); text and links
/// are stored in shelf.json; images are saved under shelf\ beside it. All of it
/// survives restarts until you remove it.
/// </summary>
public sealed class ShelfStore
{
    readonly string file, imageDir;
    public List<ShelfItem> Items { get; private set; } = new();
    public event Action Changed;
    public ShelfStore(string path)
    {
        file = path;
        imageDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "shelf");
        try { if (File.Exists(file)) Items = JsonSerializer.Deserialize(File.ReadAllText(file), StoreJson.Default.ListShelfItem) ?? new(); }
        catch (Exception ex) { Log.W("shelf", "shelf.json unreadable (kept as .broken): " + ex.Message); try { File.Copy(file, file + ".broken", true); } catch { } }
    }
    public void Add(IReadOnlyList<string> paths)
    {
        int n = 0;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p) || Items.Any(i => i.Kind == "file" && string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Insert(0, new ShelfItem { Kind = "file", Path = p }); n++;
        }
        Log.I("shelf", $"kept {n} file(s)");
        Save();
    }
    public void AddText(string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        bool url = Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https") && !text.Contains(' ');
        if (Items.Any(i => i.Text == text)) return;
        Items.Insert(0, new ShelfItem { Kind = url ? "link" : "text", Text = text });
        Log.I("shelf", url ? "kept a link" : $"kept {text.Length} chars of text");
        Save();
    }
    public void AddLink(string url, string title)
    {
        url = url?.Trim();
        if (string.IsNullOrWhiteSpace(url) || Items.Any(i => i.Text == url)) return;
        Items.Insert(0, new ShelfItem { Kind = "link", Text = url, Title = title?.Trim() ?? "" });
        Log.I("shelf", "kept a link");
        Save();
    }
    public void AddImage(byte[] png)
    {
        if (png == null || png.Length == 0) return;
        try
        {
            Directory.CreateDirectory(imageDir);
            string f = System.IO.Path.Combine(imageDir, $"image-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            File.WriteAllBytes(f, png);
            Items.Insert(0, new ShelfItem { Kind = "image", Path = f });
            Log.I("shelf", $"kept an image ({png.Length / 1024} KB)");
            Save();
        }
        catch (Exception ex) { Log.W("shelf", "couldn't store image: " + ex.Message); }
    }
    public void Remove(ShelfItem i)
    {
        Items.Remove(i);
        if (i.Kind == "image") { try { File.Delete(i.Path); } catch { } }
        Save();
    }
    public void Clear()
    {
        foreach (var i in Items.Where(i => i.Kind == "image").ToList()) { try { File.Delete(i.Path); } catch { } }
        Items.Clear(); Save();
    }
    /// <summary>Click: open files and links, copy text. Returns a word to flash, or null.</summary>
    public string Activate(ShelfItem i)
    {
        switch (i.Kind)
        {
            case "text": Win32.SetClipboardText(i.Text); return "Copied";
            case "link": Win32.ShellExecute(i.Text); return "Opened";
            default: if (i.Exists) Win32.ShellExecute(i.Path); return null;
        }
    }
    /// <summary>Right-click: show files in Explorer; copy text and links.</summary>
    public string Secondary(ShelfItem i)
    {
        if (i.Kind is "file" or "image") { Shell.RevealInExplorer(i.Path); return null; }
        Win32.SetClipboardText(i.Text); return "Copied";
    }
    void Save()
    {
        try { var tmp = file + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(Items, StoreJson.Default.ListShelfItem)); File.Move(tmp, file, true); }
        catch (Exception ex) { Log.W("shelf", "save failed: " + ex.Message); }
        Changed?.Invoke();
    }
}

// ============================================================================
// Clipboard history: Windows' own (the Win+V list)
// ============================================================================

public sealed class ClipItem
{
    public string Id = "";
    public string Kind = "text";          // text, image, files, other
    public string Text = "";              // text, or file names
    public byte[] Image;                  // encoded thumbnail for images
    public DateTime At;
    internal Windows.ApplicationModel.DataTransfer.ClipboardHistoryItem Source;
}

/// <summary>
/// The Win+V clipboard history, through Windows.ApplicationModel.DataTransfer:
/// the same list, so copying, deleting and clearing here is the same as doing
/// it there. If clipboard history is switched off in Windows, a memory-only list
/// of copied text stands in (it never touches the disk).
/// </summary>
public sealed class ClipboardHistory
{
    public List<ClipItem> Items { get; private set; } = new();
    public int Max = 24;
    public bool WindowsHistory { get; private set; }
    public event Action Changed;
    uint lastSeq;
    string selfSet;
    long refreshTimer;
    bool busy, pending;
    /// <summary>The history has been read once (whatever came back), so an empty list really is empty.</summary>
    public bool Loaded { get; private set; }

    public void Start()
    {
        try
        {
            WindowsHistory = Windows.ApplicationModel.DataTransfer.Clipboard.IsHistoryEnabled();
            Windows.ApplicationModel.DataTransfer.Clipboard.HistoryEnabledChanged += (_, _) => Ui.Post(() => { WindowsHistory = Windows.ApplicationModel.DataTransfer.Clipboard.IsHistoryEnabled(); Refresh(); });
            Windows.ApplicationModel.DataTransfer.Clipboard.HistoryChanged += (_, _) => Ui.Post(Refresh);
            Log.I("clip", WindowsHistory ? "using Windows clipboard history" : "Windows clipboard history is off; keeping a memory-only list");
        }
        catch (Exception ex) { WindowsHistory = false; Log.W("clip", "clipboard history API unavailable: " + ex.Message); }
        Refresh();
    }

    /// <summary>Debounced re-read of Windows' list.</summary>
    public void Refresh()
    {
        if (!WindowsHistory) return;
        Ui.Cancel(ref refreshTimer);
        refreshTimer = Ui.After(0.15, () => _ = Load());
    }

    async Task Load()
    {
        if (busy) { pending = true; return; }
        busy = true;
        try
        {
            var r = await Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync().AsTask().ConfigureAwait(false);
            if (r.Status != Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResultStatus.Success) { Log.D("clip", "history read: " + r.Status); return; }
            var list = new List<ClipItem>();
            var known = Items.ToDictionary(i => i.Id);
            foreach (var it in r.Items.Take(Max))
            {
                if (known.TryGetValue(it.Id, out var have)) { have.Source = it; list.Add(have); continue; }
                var ci = new ClipItem { Id = it.Id, At = it.Timestamp.LocalDateTime, Source = it };
                var c = it.Content;
                try
                {
                    if (c.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) { ci.Kind = "text"; ci.Text = await c.GetTextAsync().AsTask().ConfigureAwait(false); }
                    else if (c.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
                    {
                        ci.Kind = "image"; ci.Text = "Image";
                        var bmp = await c.GetBitmapAsync().AsTask().ConfigureAwait(false);
                        using var s = await bmp.OpenReadAsync().AsTask().ConfigureAwait(false);
                        if (s.Size < 30_000_000) { using var ns = s.AsStreamForRead(); using var ms = new MemoryStream(); await ns.CopyToAsync(ms).ConfigureAwait(false); ci.Image = ms.ToArray(); }
                    }
                    else if (c.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                    {
                        ci.Kind = "files";
                        var files = await c.GetStorageItemsAsync().AsTask().ConfigureAwait(false);
                        ci.Text = string.Join(", ", files.Select(f => f.Name));
                    }
                    else { ci.Kind = "other"; ci.Text = string.Join(", ", c.AvailableFormats.Take(2)); }
                }
                catch (Exception ex) { ci.Kind = "other"; ci.Text = "(unreadable: " + ex.Message + ")"; }
                list.Add(ci);
            }
            Ui.Post(() => { Items = list; Loaded = true; Changed?.Invoke(); });
        }
        catch (Exception ex) { Log.D("clip", "history read failed: " + ex.Message); }
        finally { busy = false; Ui.Post(() => { if (!Loaded) { Loaded = true; Changed?.Invoke(); } }); if (pending) { pending = false; Ui.Post(Refresh); } }
    }

    /// <summary>WM_CLIPBOARDUPDATE: only used when Windows' history is off.</summary>
    public void OnUpdate()
    {
        if (WindowsHistory) return;
        uint seq = Win32.GetClipboardSequenceNumber();
        if (seq == lastSeq) return;
        lastSeq = seq;
        // Password managers mark secrets with this format; honor it.
        if (Win32.IsClipboardFormatAvailable(Win32.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"))) return;
        string s = null;
        try { s = Win32.GetClipboardText(); } catch { }
        if (string.IsNullOrWhiteSpace(s) || s.Length > 20000) return;
        if (s == selfSet) { selfSet = null; return; }
        Items.RemoveAll(i => i.Text == s);
        Items.Insert(0, new ClipItem { Id = Guid.NewGuid().ToString(), Kind = "text", Text = s, At = DateTime.Now });
        if (Items.Count > Max) Items.RemoveRange(Max, Items.Count - Max);
        Changed?.Invoke();
    }

    public void Copy(ClipItem i)
    {
        if (i.Source != null)
        {
            var r = Windows.ApplicationModel.DataTransfer.Clipboard.SetHistoryItemAsContent(i.Source);
            Log.D("clip", "copy from history -> " + r);
            if (r == Windows.ApplicationModel.DataTransfer.SetHistoryItemAsContentStatus.Success) return;
        }
        if (i.Kind == "text") { selfSet = i.Text; Win32.SetClipboardText(i.Text); }
    }

    public void Delete(ClipItem i)
    {
        if (i.Source != null) { try { Windows.ApplicationModel.DataTransfer.Clipboard.DeleteItemFromHistory(i.Source); } catch (Exception ex) { Log.W("clip", "delete failed: " + ex.Message); } }
        Items.Remove(i);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (WindowsHistory) { try { Windows.ApplicationModel.DataTransfer.Clipboard.ClearHistory(); } catch (Exception ex) { Log.W("clip", "clear failed: " + ex.Message); } }
        Items.Clear();
        Log.I("clip", "clipboard history cleared");
        Changed?.Invoke();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<Note>))]
[JsonSerializable(typeof(List<ShelfItem>))]
public partial class StoreJson : JsonSerializerContext { }
