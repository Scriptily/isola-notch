using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Isola.App;

namespace Isola.Services;

/// <summary>
/// The programs behind downloading and converting, kept beside Isola and up to
/// date by it: yt-dlp (downloads from YouTube and most other sites), FFmpeg
/// (merges, converts and extracts audio; the "essentials" build, and only its
/// ffmpeg.exe, since yt-dlp no longer needs ffprobe) and QuickJS (a 2 MB
/// JavaScript engine yt-dlp uses to read YouTube's player, instead of the
/// 100 MB one it asks for by default). About 130 MB in all.
///
/// They aren't in the installer, which stays small: Isola fetches them the
/// first time they're needed, or soon after it starts when downloads are on,
/// and checks for new versions once a day (yt-dlp changes often, as sites do).
/// Every file comes from that project's own GitHub releases and must match the
/// SHA-256 GitHub publishes for it, or it's thrown away. A new version is put
/// in place only once it's whole and verified, and never while a download is
/// using the old one. They live in Isola's program folder (download-tools),
/// so uninstalling Isola removes them; the Store version keeps them in its own
/// data folder, which Windows removes with it.
/// </summary>
public sealed class DownloadTools
{
    public sealed record Tool(string Id, string Name, string Repo, string Exe, Func<string, bool> IsAsset, string ZipEntry);

    public static readonly Tool[] All =
    {
        new("yt-dlp", "yt-dlp", "yt-dlp/yt-dlp", "yt-dlp.exe", n => n == "yt-dlp.exe", null),
        new("ffmpeg", "FFmpeg", "GyanD/codexffmpeg", "ffmpeg.exe", n => n.EndsWith("-essentials_build.zip", StringComparison.OrdinalIgnoreCase), "bin/ffmpeg.exe"),
        new("quickjs", "QuickJS", "quickjs-ng/quickjs", "qjs.exe", n => n == "qjs-windows-x86_64.exe", null),
    };

    public sealed class Installed
    {
        public string Version { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public DateTime At { get; set; }
    }

    readonly HttpClient http;
    readonly IClock clock;
    public string Dir { get; }
    string StateFile => Path.Combine(Dir, "versions.json");
    Dictionary<string, Installed> installed = new();
    public Func<bool> Enabled = () => true;
    /// <summary>A download or conversion is running (a tool in use can't be replaced).</summary>
    public Func<bool> InUse = () => false;
    public event Action Changed;

    public string Status { get; private set; } = "";
    /// <summary>0..1 while fetching, else -1.</summary>
    public double Progress { get; private set; } = -1;
    public bool Busy { get; private set; }
    public DateTime LastCheck { get; private set; }
    long timer;

    public DownloadTools(string dir, IClock c, HttpMessageHandler handler = null)
    {
        Dir = dir; clock = c;
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Isola-Tools/1.0");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        try { if (File.Exists(StateFile)) installed = JsonSerializer.Deserialize(File.ReadAllText(StateFile), ToolsJson.Default.DictionaryStringInstalled) ?? new(); }
        catch (Exception ex) { Log.W("tools", "versions.json unreadable: " + ex.Message); }
        Status = Ready ? Summary() : "Not installed yet";
    }

    public string PathOf(string id) => Path.Combine(Dir, All.First(t => t.Id == id).Exe);
    public bool Has(Tool t) => File.Exists(Path.Combine(Dir, t.Exe));
    public bool Ready => All.All(Has);
    public string VersionOf(string id) => installed.TryGetValue(id, out var i) && Has(All.First(t => t.Id == id)) ? i.Version : null;
    public long Bytes
    {
        get { try { return Directory.Exists(Dir) ? new DirectoryInfo(Dir).EnumerateFiles("*.exe").Sum(f => f.Length) : 0; } catch { return 0; } }
    }
    public string Summary() => string.Join(" · ", All.Where(Has).Select(t => $"{t.Name} {VersionOf(t.Id) ?? "?"}"));

    void Set(string s, double progress = -1) { Status = s; Progress = progress; Ui.Post(() => Changed?.Invoke()); }

    /// <summary>Soon after start, then once a day: fetch what's missing, update what's old (only while downloads are on).</summary>
    public void Start(double firstCheckSeconds = 90)
    {
        Ui.Cancel(ref timer);
        timer = Ui.After(firstCheckSeconds, () => { _ = CheckAsync(false); });
    }

    /// <summary>Gets any missing tool and updates old ones. Returns whether all are ready.</summary>
    public async Task<bool> CheckAsync(bool userAsked, CancellationToken ct = default)
    {
        Ui.Post(() => { Ui.Cancel(ref timer); timer = Ui.After(24 * 3600, () => { _ = CheckAsync(false); }); });
        if (!Enabled() && !userAsked) return Ready;
        if (Busy) { while (Busy && !ct.IsCancellationRequested) await Task.Delay(250, ct).ConfigureAwait(false); return Ready; }
        Busy = true;
        try
        {
            Directory.CreateDirectory(Dir);
            bool fresh = !Ready;
            for (int i = 0; i < All.Length; i++)
            {
                var t = All[i];
                Set(fresh ? $"Getting {t.Name}…" : $"Checking {t.Name}…", fresh ? (double)i / All.Length : -1);
                var latest = await LatestAsync(t, ct).ConfigureAwait(false);
                if (latest == null) { if (!Has(t)) { Set($"Couldn't reach GitHub for {t.Name}. Try again later."); return false; } continue; }
                var (ver, url, sha, size) = latest.Value;
                if (Has(t) && installed.TryGetValue(t.Id, out var have) && have.Version == ver) continue;
                if (Has(t) && InUse()) { Log.I("tools", $"{t.Name} {ver} waits: a download is using it"); continue; }
                Log.I("tools", $"{t.Name}: {(Has(t) ? "updating to" : "getting")} {ver} ({size / 1_000_000} MB)");
                await FetchAsync(t, ver, url, sha, size, (double)i / All.Length, 1.0 / All.Length, fresh, ct).ConfigureAwait(false);
            }
            LastCheck = DateTime.Now;
            Set(Ready ? Summary() : "Not installed yet");
            return Ready;
        }
        catch (OperationCanceledException) { Set(Ready ? Summary() : "Stopped"); return Ready; }
        catch (Exception ex)
        {
            Log.W("tools", "check failed: " + ex.Message);
            Set(ex is ToolsException ? ex.Message : "Couldn't get the tools. Check your connection and try again.");
            return Ready;
        }
        finally { Busy = false; Ui.Post(() => Changed?.Invoke()); }
    }

    sealed class ToolsException(string m) : Exception(m);

    /// <summary>The newest release's version and its asset: (version, download URL, sha256, size), or null if GitHub can't be reached.</summary>
    async Task<(string, string, string, long)?> LatestAsync(Tool t, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{t.Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { Log.W("tools", $"{t.Name}: GitHub answered {(int)resp.StatusCode}"); return null; }
            var r = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            string ver = (r?["tag_name"]?.ToString() ?? "").TrimStart('v');
            var asset = (r?["assets"] as JsonArray)?.FirstOrDefault(a => t.IsAsset(a?["name"]?.ToString() ?? ""));
            if (ver.Length == 0 || asset == null) throw new ToolsException($"{t.Name}'s newest release has no Windows download.");
            string url = asset["browser_download_url"]?.ToString() ?? "";
            // only from the project's own releases on GitHub
            if (!url.StartsWith($"https://github.com/{t.Repo}/releases/download/", StringComparison.OrdinalIgnoreCase)) throw new ToolsException($"{t.Name}'s download isn't where it should be.");
            string digest = asset["digest"]?.ToString() ?? "";
            if (!digest.StartsWith("sha256:")) throw new ToolsException($"GitHub gave no checksum for {t.Name}, so it isn't installed.");
            return (ver, url, digest[7..].ToLowerInvariant(), asset["size"]?.GetValue<long>() ?? 0);
        }
        catch (ToolsException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.W("tools", $"{t.Name}: {ex.Message}"); return null; }
    }

    async Task FetchAsync(Tool t, string ver, string url, string sha, long size, double from, double span, bool showProgress, CancellationToken ct)
    {
        string part = Path.Combine(Dir, t.Id + ".download");
        try
        {
            using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                r.EnsureSuccessStatusCode();
                long total = r.Content.Headers.ContentLength ?? size;
                await using var src = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(part);
                var buf = new byte[1 << 16];
                long got = 0; double shown = -1;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    got += n;
                    double f = total > 0 ? (double)got / total : 0;
                    if (showProgress && f - shown > 0.01) { shown = f; Set($"Getting {t.Name}… {got / 1_000_000} of {total / 1_000_000} MB", from + span * f); }
                }
            }
            string have;
            await using (var fs = File.OpenRead(part)) have = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (have != sha) throw new ToolsException($"{t.Name} didn't match its checksum, so it was thrown away.");
            // in place only once it's whole: a new file beside the old, then a swap
            string exe = Path.Combine(Dir, t.Exe), next = exe + ".new";
            if (t.ZipEntry == null) File.Move(part, next, true);
            else
            {
                using var zip = ZipFile.OpenRead(part);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("/" + t.ZipEntry, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolsException($"{t.Name}'s download didn't contain {Path.GetFileName(t.ZipEntry)}.");
                entry.ExtractToFile(next, true);
            }
            File.Move(next, exe, true);
            installed[t.Id] = new Installed { Version = ver, Sha256 = sha, At = DateTime.Now };
            Save();
            Log.I("tools", $"{t.Name} {ver} ready ({new FileInfo(exe).Length / 1_000_000} MB, sha256 {sha[..12]}…)");
        }
        finally { try { File.Delete(part); } catch { } }
    }

    void Save()
    {
        try { var tmp = StateFile + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(installed, ToolsJson.Default.DictionaryStringInstalled)); File.Move(tmp, StateFile, true); }
        catch (Exception ex) { Log.W("tools", "couldn't save versions: " + ex.Message); }
    }

    /// <summary>Frees the space (Settings): they come back the next time something is downloaded.</summary>
    public bool Remove()
    {
        if (Busy || InUse()) return false;
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); installed.Clear(); Set("Removed. They come back when you download something."); return true; }
        catch (Exception ex) { Log.W("tools", "remove failed: " + ex.Message); Set("Couldn't remove them all (one may be running)."); return false; }
    }
}

[JsonSerializable(typeof(Dictionary<string, DownloadTools.Installed>))]
internal partial class ToolsJson : JsonSerializerContext { }
