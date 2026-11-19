using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Isola.App;

namespace Isola.Services;

/// <summary>
/// Automatic updates from the GitHub repository's Releases.
///
/// A release tagged <c>vX.Y.Z</c> carries
/// <c>IsolaSetup.exe</c> and a <c>sha256:</c> line in its notes. The
/// island checks at startup and every few hours; a newer version is
/// downloaded, its SHA-256 checked against the notes, and the setup run
/// silently - it closes this copy, swaps the files and starts the new one.
///
/// The repository is public, so no account or token is involved. The asset
/// must come from this repository's own release API, and its hash must match
/// both the sha256 line in the notes and GitHub's own digest for the file
/// when GitHub provides one. Any failure (offline, rate-limited, GitHub down)
/// is logged and retried later; the running island is never touched until a
/// verified installer is on disk.
/// </summary>
public sealed class Updater
{
    readonly IClock clock;
    readonly HttpClient http;
    public Func<Config> Cfg;
    public Version Current { get; init; } = typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0);
    /// <summary>
    /// Starts the downloaded setup; true once its process exists. CreateProcess
    /// directly (no shell): it returns only after the process is created, and
    /// the setup's manifest asks for no elevation. Tests replace it.
    /// </summary>
    internal Func<string, string, bool> StartInstaller = (file, args) =>
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file) });
        return p != null;
    };
    /// <summary>Running from the project folder (tests point this elsewhere).</summary>
    internal Func<bool> IsDev = IsDevelopmentBuild;
    public string Status { get; private set; } = "Not checked yet";
    public Version Available { get; private set; }
    public event Action Changed;
    public event Action<Version> Installing;
    /// <summary>A newer version was found and is about to download (the island says so).</summary>
    public event Action<Version> Found;
    /// <summary>"downloading" or "installing" while an update is under way (the island shows it), else null.</summary>
    public string Phase { get; private set; }
    /// <summary>How much of the update has downloaded, 0 to 1; -1 when the size isn't known.</summary>
    public double Progress { get; private set; } = -1;
    /// <summary>The version being downloaded or installed.</summary>
    public Version Incoming { get; private set; }
    long timer;
    bool busy;
    const string Tag = "v", Asset = "IsolaSetup.exe";
    /// <summary>Where releases come from. Fixed here, not a setting: an edited config can't point updates elsewhere.</summary>
    public const string Repo = "Scriptily/isola-notch";
    /// <summary>How long the island shows "Installing" before it closes for the setup.</summary>
    public const double ShowInstallingFor = 1.4;

    public Updater(IClock c, HttpMessageHandler handler = null)
    {
        clock = c;
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Isola-Updater/1.0");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public void Start()
    {
        // the Microsoft Store version is updated by the Store, never by itself
        if (Packaging.IsStore) { Status = "Updated by the Microsoft Store"; return; }
        Schedule(45);   // let startup settle first
        // setups downloaded for earlier updates stayed in the temp folder, one per version
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "IsolaSetup-*.exe"))
                if (Version.TryParse(Path.GetFileNameWithoutExtension(f)["IsolaSetup-".Length..], out var v) && v <= Current) File.Delete(f);
        }
        catch (Exception ex) { Log.D("update", "cleaning old setups: " + ex.Message); }
    }

    void Schedule(double seconds) { Ui.Cancel(ref timer); timer = Ui.After(seconds, () => _ = CheckAsync(false)); }

    void Set(string s) { Status = s; Ui.Post(() => Changed?.Invoke()); }

    /// <summary>A check or download is in flight.</summary>
    public bool Busy => busy;

    /// <summary>Check now; install if newer and allowed (or if <paramref name="userAsked"/>).</summary>
    public async Task CheckAsync(bool userAsked)
    {
        if (Packaging.IsStore) { Set("Updated by the Microsoft Store"); return; }
        var cfg = Cfg();
        Ui.Post(() => Schedule(Math.Max(1, cfg.UpdateCheckHours) * 3600));
        if (!cfg.AutoUpdate && !userAsked) return;
        if (busy) return;
        if (IsDev()) { Set("Running from the project folder: updates are off"); return; }
        busy = true;
        try
        {
            Set("Checking…");
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=30");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden or (System.Net.HttpStatusCode)429) { Set("GitHub asked to wait; will retry later"); return; }
            resp.EnsureSuccessStatusCode();
            var arr = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)) as JsonArray;
            var best = Latest(arr);
            if (best == null) { Set($"Up to date (v{Short(Current)})"); return; }
            var (ver, assetUrl, sha) = best.Value;
            Available = ver;
            if (ver <= Current) { Set($"Up to date (v{Short(Current)})"); return; }
            Log.I("update", $"v{Short(ver)} is available (running v{Short(Current)})");
            Incoming = ver; Phase = "downloading"; Progress = 0;
            Ui.Post(() => Found?.Invoke(ver));
            Set($"Downloading v{Short(ver)}…");
            string file = Path.Combine(Path.GetTempPath(), $"IsolaSetup-{Short(ver)}.exe");
            var dl = new HttpRequestMessage(HttpMethod.Get, assetUrl);
            dl.Headers.Accept.ParseAdd("application/octet-stream");
            using (var r = await http.SendAsync(dl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                r.EnsureSuccessStatusCode();
                long total = r.Content.Headers.ContentLength ?? -1;
                await using var fs = File.Create(file);
                await using var src = await r.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var buf = new byte[1 << 16];
                long done = 0; double shown = 0; int n;
                // copied by hand to show how far it's got (the island's ring, Settings' status)
                while ((n = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                    done += n;
                    if (total > 0 && (double)done / total - shown >= 0.02) { shown = Progress = (double)done / total; Set($"Downloading v{Short(ver)}… {shown * 100:0}%"); }
                }
                if (total <= 0) Progress = -1;
            }
            string got = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            if (string.IsNullOrEmpty(sha) || !string.Equals(got, sha, StringComparison.OrdinalIgnoreCase))
            {
                Log.E("update", $"downloaded installer failed verification (expected {sha}, got {got}); not installing");
                try { File.Delete(file); } catch { }
                Set("Download didn't verify; will retry later");
                return;
            }
            Log.I("update", $"verified v{Short(ver)} ({got[..12]}…); installing");
            // Start the setup first, and only once it is running ask the island
            // to quit. The other order posted the quit, then called ShellExecute
            // on this pool thread; the app was gone 32 ms later and the setup
            // never started, leaving no island at all until the next logon. The
            // setup waits for this process to exit before it touches a file.
            bool started;
            try { started = StartInstaller(file, $"/update /wait {Environment.ProcessId}"); }
            catch (Exception ex) { Log.W("update", "couldn't start the installer: " + ex.Message); started = false; }
            if (!started) { Set("Couldn't start the installer; will retry later"); return; }
            Phase = "installing"; Progress = 1;
            Set($"Installing v{Short(ver)}…");
            Ui.Post(() => Installing?.Invoke(ver));
        }
        catch (Exception ex) { Log.W("update", "check failed: " + ex.Message); Set("Couldn't reach GitHub; will retry later"); }
        finally
        {
            busy = false;
            if (Phase != "installing") { Phase = null; Progress = -1; Ui.Post(() => Changed?.Invoke()); }   // didn't get as far as installing: nothing to show
        }
    }

    /// <summary>Newest release with our tag prefix and a setup asset. Public for tests.</summary>
    public static (Version ver, string assetUrl, string sha)? Latest(JsonArray releases)
    {
        (Version, string, string)? best = null;
        if (releases == null) return null;
        foreach (var r in releases)
        {
            if (r?["draft"]?.GetValue<bool>() == true || r?["prerelease"]?.GetValue<bool>() == true) continue;
            string tag = r?["tag_name"]?.ToString() ?? "";
            if (!tag.StartsWith(Tag, StringComparison.OrdinalIgnoreCase) || !Version.TryParse(tag[Tag.Length..], out var v)) continue;
            var asset = (r["assets"] as JsonArray)?.FirstOrDefault(a => string.Equals(a?["name"]?.ToString(), Asset, StringComparison.OrdinalIgnoreCase));
            if (asset == null) continue;
            string url = asset["url"]?.ToString() ?? "";
            if (!url.StartsWith($"https://api.github.com/repos/{Repo}/releases/assets/", StringComparison.OrdinalIgnoreCase)) continue;   // nowhere else
            string body = r["body"]?.ToString() ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(body, @"sha256:\s*([0-9a-fA-F]{64})");
            string sha = m.Success ? m.Groups[1].Value : null;
            // GitHub's own hash of the uploaded file, when it gives one, must agree with the notes
            var dg = System.Text.RegularExpressions.Regex.Match(asset["digest"]?.ToString() ?? "", @"^sha256:([0-9a-fA-F]{64})$");
            if (dg.Success && sha != null && !string.Equals(dg.Groups[1].Value, sha, StringComparison.OrdinalIgnoreCase)) sha = null;
            if (best == null || v > best.Value.Item1) best = (v, url, sha);
        }
        return best;
    }

    public static string Short(Version v) => v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

    static bool IsDevelopmentBuild()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Isola.csproj"))) return true;
        return false;
    }
}
