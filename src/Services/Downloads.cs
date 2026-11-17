using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Isola.App;

namespace Isola.Services;

/// <summary>One download or conversion, from the moment it's asked for to its file (or its error).</summary>
public sealed class DownloadJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>video, audio or convert.</summary>
    public string Kind { get; set; } = "video";
    /// <summary>The link, or the file being converted.</summary>
    public string Source { get; set; } = "";
    /// <summary>What it becomes: mp4, mp3, m4a, gif, wav, webm, or small (a smaller MP4).</summary>
    public string Format { get; set; } = "mp4";
    public string Title { get; set; } = "";
    /// <summary>queued, starting, running, done, failed, cancelled.</summary>
    public string State { get; set; } = "queued";
    /// <summary>What it's doing, in words: "Downloading video", "Merging", "Converting to MP3".</summary>
    public string Stage { get; set; } = "Waiting";
    public double Progress { get; set; }
    public double Speed { get; set; }        // bytes a second, while downloading
    public double Eta { get; set; } = -1;    // seconds, or -1
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTime Started { get; set; } = DateTime.Now;
    public DateTime? Finished { get; set; }
    /// <summary>Kept on the shelf when done.</summary>
    public bool ToShelf { get; set; }

    [JsonIgnore] public bool Active => State is "queued" or "starting" or "running";
    [JsonIgnore] public string Label => Title.Length > 0 ? Title : Kind == "convert" ? Path.GetFileName(Source) : Source;
}

/// <summary>A program run for a job: its output a line at a time, its end, and a way to stop it (tests stand in their own).</summary>
public interface IJobProcess
{
    event Action<string> Line;
    Task<int> Exit { get; }
    void Kill();
}

/// <summary>
/// Downloads (yt-dlp) and conversions (FFmpeg), two at a time, each with its
/// progress, and a history of the last hundred. Finished files go to the
/// Downloads folder, a folder of your choice, or the shelf (Settings ->
/// Downloads). Nothing here ever runs a program but the three in
/// DownloadTools, and every argument is passed as its own argument, never
/// through a shell, so a link or a file name can't become a command.
/// </summary>
public sealed class Downloads
{
    public const int MaxRunning = 2, MaxHistory = 100;
    readonly string file;
    readonly DownloadTools tools;
    public List<DownloadJob> Jobs { get; private set; } = new();
    public Func<Config> Cfg = () => new Config();
    public ShelfStore Shelf;
    /// <summary>Where a job's output goes before it's finished (yt-dlp's parts, FFmpeg's file in progress).</summary>
    public string TempDir;
    public string ShelfDir;
    /// <summary>Starts a tool (tests replace it).</summary>
    public Func<string, IReadOnlyList<string>, IJobProcess> Run = RealProcess.Start;
    public event Action Changed;
    public event Action<DownloadJob> Finished;
    readonly Dictionary<string, IJobProcess> running = new();
    readonly object gate = new();

    public Downloads(string historyFile, DownloadTools tools)
    {
        file = historyFile; this.tools = tools;
        string home = Path.GetDirectoryName(historyFile)!;
        TempDir = Path.Combine(home, "download-temp");
        ShelfDir = Path.Combine(home, "shelf", "downloads");
        try { if (File.Exists(file)) Jobs = JsonSerializer.Deserialize(File.ReadAllText(file), DownloadsJson.Default.ListDownloadJob) ?? new(); }
        catch (Exception ex) { Log.W("downloads", "history unreadable: " + ex.Message); }
        // anything that was running when Isola last closed didn't finish
        foreach (var j in Jobs.Where(j => j.Active)) { j.State = "failed"; j.Error = "Isola closed before it finished"; j.Finished ??= DateTime.Now; }
        tools.InUse = () => Jobs.Any(j => j.State is "running");
    }

    public int ActiveCount { get { lock (gate) return Jobs.Count(j => j.Active); } }
    public DownloadJob Current { get { lock (gate) return Jobs.FirstOrDefault(j => j.State == "running") ?? Jobs.FirstOrDefault(j => j.Active); } }
    /// <summary>The overall progress of everything running, 0..1.</summary>
    public double Overall { get { lock (gate) { var a = Jobs.Where(j => j.Active).ToList(); return a.Count == 0 ? 1 : a.Average(j => j.Progress); } } }

    // ---- asking ---------------------------------------------------------------------------------------

    static readonly Regex Http = new(@"^https?://[^\s/$.?#][^\s]*$", RegexOptions.IgnoreCase);
    /// <summary>Whether yt-dlp should be given this: an http(s) link, nothing else (no file:, no options).</summary>
    public static bool IsLink(string s) => s != null && s.Length < 4000 && Http.IsMatch(s.Trim()) && Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    public DownloadJob Download(string url, bool audio)
    {
        url = url?.Trim() ?? "";
        if (!IsLink(url)) throw new ArgumentException("That isn't a link Isola can download from.");
        var cfg = Cfg();
        var job = new DownloadJob { Kind = audio ? "audio" : "video", Source = url, Format = audio ? (cfg.DownloadAudioFormat == "m4a" ? "m4a" : "mp3") : "mp4", ToShelf = cfg.DownloadTo == "shelf" };
        return Add(job);
    }

    public static readonly string[] ConvertFormats = { "mp4", "mp3", "gif", "m4a", "wav", "webm", "small" };
    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".ts", ".3gp", ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".gif" };
    public static bool IsMedia(string path) => !string.IsNullOrEmpty(path) && MediaExtensions.Contains(Path.GetExtension(path));
    public static bool IsAudioOnly(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".m4a" or ".aac" or ".wav" or ".flac" or ".ogg" or ".opus" or ".wma" or ".aiff";
    /// <summary>The formats a file can become: audio can't become a video or a GIF.</summary>
    public static IEnumerable<string> FormatsFor(string path) => ConvertFormats.Where(f => !(IsAudioOnly(path) && f is "mp4" or "gif" or "webm" or "small") && !string.Equals("." + f, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));
    public static string FormatName(string f) => f switch { "small" => "a smaller MP4", "webm" => "WebM", _ => f.ToUpperInvariant() };

    public DownloadJob Convert(string path, string format)
    {
        if (!File.Exists(path)) throw new ArgumentException("That file isn't there any more.");
        if (!IsMedia(path)) throw new ArgumentException("Only video and audio files can be converted.");
        if (!FormatsFor(path).Contains(format)) throw new ArgumentException($"{Path.GetFileName(path)} can't become {FormatName(format)}.");
        var job = new DownloadJob { Kind = "convert", Source = Path.GetFullPath(path), Format = format, Title = Path.GetFileNameWithoutExtension(path), ToShelf = Cfg().DownloadTo == "shelf" };
        return Add(job);
    }

    DownloadJob Add(DownloadJob job)
    {
        lock (gate)
        {
            Jobs.Insert(0, job);
            while (Jobs.Count > MaxHistory) { int last = Jobs.FindLastIndex(j => !j.Active); if (last < 0) break; Jobs.RemoveAt(last); }
        }
        Log.I("downloads", $"{job.Kind} {job.Format} queued");
        Save(); Pump();
        return job;
    }

    public void Cancel(DownloadJob job)
    {
        IJobProcess p;
        lock (gate)
        {
            if (!job.Active) return;
            running.TryGetValue(job.Id, out p);
            job.State = "cancelled"; job.Stage = "Cancelled"; job.Finished = DateTime.Now;
        }
        try { p?.Kill(); } catch { }
        Save(); Pump();
    }

    public DownloadJob Retry(DownloadJob job) => job.Kind == "convert" ? Convert(job.Source, job.Format) : Download(job.Source, job.Kind == "audio");

    public void Remove(DownloadJob job) { if (job.Active) Cancel(job); lock (gate) Jobs.Remove(job); Save(); }
    public void ClearHistory() { lock (gate) Jobs.RemoveAll(j => !j.Active); Save(); }

    // ---- running ------------------------------------------------------------------------------------

    void Pump()
    {
        List<DownloadJob> start;
        lock (gate)
        {
            int free = MaxRunning - Jobs.Count(j => j.State is "starting" or "running");
            start = Jobs.Where(j => j.State == "queued").Reverse().Take(Math.Max(0, free)).ToList();
            foreach (var j in start) { j.State = "starting"; j.Stage = "Starting"; }
        }
        foreach (var j in start) _ = RunAsync(j);
    }

    async Task RunAsync(DownloadJob job)
    {
        try
        {
            if (!tools.Ready)
            {
                job.Stage = "Getting the download tools (once, about 130 MB)"; Changed0();
                if (!await tools.CheckAsync(true).ConfigureAwait(false)) throw new JobFailed(tools.Status);
            }
            if (job.State == "cancelled") return;
            string dest = Destination(job);
            Directory.CreateDirectory(dest);
            Directory.CreateDirectory(TempDir);
            job.State = "running"; Changed0();
            string output = job.Kind == "convert" ? await ConvertAsync(job, dest).ConfigureAwait(false) : await DownloadAsync(job, dest).ConfigureAwait(false);
            if (job.State == "cancelled") return;
            if (string.IsNullOrEmpty(output) || !File.Exists(output)) throw new JobFailed("It finished, but no file came out.");
            job.Output = output; job.Progress = 1; job.State = "done"; job.Stage = "Done"; job.Finished = DateTime.Now;
            if (job.ToShelf && Shelf != null) Ui.Post(() => Shelf.Add(new[] { output }));
            Log.I("downloads", $"{job.Kind} done ({new FileInfo(output).Length / 1024} KB)");
        }
        catch (Exception ex) when (job.State != "cancelled")
        {
            job.State = "failed"; job.Finished = DateTime.Now;
            job.Error = ex is JobFailed ? ex.Message : "Something went wrong: " + ex.Message;
            job.Stage = "Failed";
            Log.W("downloads", $"{job.Kind} failed: {job.Error}");
        }
        catch { }
        finally
        {
            lock (gate) running.Remove(job.Id);
            Save();
            if (job.State is "done" or "failed") Ui.Post(() => Finished?.Invoke(job));
            Pump();
        }
    }

    sealed class JobFailed(string m) : Exception(m);

    string Destination(DownloadJob job)
    {
        if (job.ToShelf) return ShelfDir;
        var cfg = Cfg();
        if (cfg.DownloadTo == "folder" && !string.IsNullOrWhiteSpace(cfg.DownloadFolder) && Path.IsPathFullyQualified(cfg.DownloadFolder)) return cfg.DownloadFolder;
        return KnownFolders.Downloads;
    }

    /// <summary>yt-dlp's arguments for a job: each its own argument (never a command line a link could break out of).</summary>
    public List<string> YtDlpArgs(DownloadJob job, string dest)
    {
        var cfg = Cfg();
        var a = new List<string>
        {
            "--ignore-config", "--no-playlist", "--newline", "--progress", "--no-colors", "--windows-filenames", "--no-mtime", "--quiet",
            "--ffmpeg-location", tools.PathOf("ffmpeg"),
            // YouTube's player needs a JavaScript engine: Node when it's installed (it solves
            // YouTube's challenge in about half the time), else the QuickJS Isola keeps
            "--js-runtimes", "node", "--js-runtimes", "quickjs:" + tools.PathOf("quickjs"),
            "-P", dest, "-P", "temp:" + TempDir,
            "-o", "%(title).150B.%(ext)s",
            "--progress-template", "download:ISOLA|dl|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.vcodec)s|%(info.acodec)s",
            "--progress-template", "postprocess:ISOLA|pp|%(progress.status)s|%(progress.postprocessor)s",
            "--print", "before_dl:ISOLA|title|%(title)s",
            "--print", "after_move:ISOLA|file|%(filepath)s",
        };
        if (job.Kind == "audio")
            a.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", job.Format, "--audio-quality", "0", "--embed-metadata", "--embed-thumbnail" });
        else
        {
            string cap = cfg.DownloadQuality switch { "best" => "", "720" => ",res:720", _ => ",res:1080" };
            // H.264 and AAC first, so the MP4 plays everywhere (Windows' player, phones, editors)
            a.AddRange(new[] { "-f", "bv*+ba/b", "-S", "vcodec:h264" + cap + ",acodec:m4a", "--merge-output-format", "mp4", "--embed-metadata" });
        }
        // recordings only (README, trailer): a real download, slowed so its progress can be seen
        if (Environment.GetEnvironmentVariable("ISOLA_DOWNLOAD_RATE") is { Length: > 0 } rate && System.Text.RegularExpressions.Regex.IsMatch(rate, @"^\d+[KM]?$")) a.AddRange(new[] { "--limit-rate", rate });
        a.Add("--");   // whatever comes next is the link, never an option
        a.Add(job.Source);
        return a;
    }

    async Task<string> DownloadAsync(DownloadJob job, string dest)
    {
        string output = null, last = null;
        var errors = new List<string>();
        job.Stage = "Finding the " + (job.Kind == "audio" ? "audio" : "video");
        int phase = 0; double lastFrac = 0;
        var p = Start(job, tools.PathOf("yt-dlp"), YtDlpArgs(job, dest));
        p.Line += line =>
        {
            if (line.StartsWith("ISOLA|"))
            {
                var f = line.Split('|');
                switch (f[1])
                {
                    case "title" when f.Length > 2: job.Title = string.Join("|", f.Skip(2)).Trim(); break;
                    case "file" when f.Length > 2: output = string.Join("|", f.Skip(2)).Trim(); break;
                    case "dl" when f.Length >= 9:
                    {
                        double done = Num(f[2]), total = Num(f[3]) is var t && t > 0 ? t : Num(f[4]);
                        double frac = total > 0 ? Math.Clamp(done / total, 0, 1) : 0;
                        if (frac + 0.2 < lastFrac) phase++;   // a second stream started (video, then audio)
                        lastFrac = frac;
                        bool videoOnly = f[7] != "none" && f[7] != "NA" && f[8] == "none", audioOnly = f[7] == "none";
                        job.Stage = job.Kind == "audio" ? "Downloading audio" : videoOnly ? "Downloading video" : audioOnly ? "Downloading audio" : "Downloading";
                        // the video is most of it: 85% of the bar, the audio the rest
                        job.Progress = job.Kind == "video" ? (phase == 0 ? frac * 0.85 : 0.85 + frac * 0.1) : frac * 0.9;
                        job.Speed = Num(f[5]); job.Eta = Num(f[6]) is var e && e >= 0 ? e : -1;
                        break;
                    }
                    case "pp" when f.Length >= 4 && f[2] == "started":
                        job.Stage = f[3] switch { "Merger" => "Merging", "ExtractAudio" => "Converting to " + FormatName(job.Format), "EmbedThumbnail" => "Adding the cover", "Metadata" => "Adding details", _ => "Finishing" };
                        job.Progress = Math.Max(job.Progress, 0.95); job.Speed = 0; job.Eta = -1;
                        break;
                }
                Changed0();
            }
            else if (line.StartsWith("ERROR:")) errors.Add(line);
            else if (line.Length > 0) last = line;
        };
        int code = await p.Exit.ConfigureAwait(false);
        if (job.State == "cancelled") { CleanTemp(); return null; }
        if (code != 0) throw new JobFailed(Explain(errors.LastOrDefault() ?? last ?? $"yt-dlp stopped (code {code})"));
        return output;
    }

    /// <summary>yt-dlp's errors, as a person would say them.</summary>
    public static string Explain(string err)
    {
        string e = Regex.Replace(err, @"^ERROR:\s*(\[[^\]]+\]\s*)?([\w-]+:\s*)?", "").Trim();
        // the site answered, so it isn't the connection: say what it said
        if (e.Contains("HTTP Error 404", StringComparison.OrdinalIgnoreCase)) return "There's nothing at that link (the page wasn't found).";
        if (e.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) || e.Contains("HTTP Error 401", StringComparison.OrdinalIgnoreCase)) return "The site refused the download.";
        if (e.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)) return "The site asked to slow down. Try again in a few minutes.";
        if (e.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return "That site isn't supported.";
        if (e.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) || e.Contains("not a bot", StringComparison.OrdinalIgnoreCase)) return "The site wants a signed-in browser for this one. Try again later.";
        if (e.Contains("Private video", StringComparison.OrdinalIgnoreCase)) return "That video is private.";
        if (e.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase) || e.Contains("not available", StringComparison.OrdinalIgnoreCase)) return "That video isn't available.";
        if (e.Contains("Unable to download", StringComparison.OrdinalIgnoreCase) || e.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase) || e.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "Couldn't reach the site. Check your connection.";
        if (e.Contains("No space left", StringComparison.OrdinalIgnoreCase)) return "The disk is full.";
        return e.Length > 160 ? e[..160] + "…" : e;
    }

    static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;

    /// <summary>FFmpeg's arguments for a conversion (each its own argument).</summary>
    public static List<string> FfmpegArgs(string input, string output, string format)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-i", input };
        a.AddRange(format switch
        {
            "mp3" => new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
            "m4a" => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
            "wav" => new[] { "-vn", "-c:a", "pcm_s16le" },
            "webm" => new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k" },
            // full colors in a GIF: a palette made for this clip, dithered, so gradients don't band
            "gif" => new[] { "-vf", "fps=15,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a", "-loop", "0" },
            "small" => new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "28", "-vf", "scale='min(1280,iw)':-2", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart" },
            _ => new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart" },
        });
        a.Add(output);
        return a;
    }

    public static string Extension(string format) => format == "small" ? ".mp4" : "." + format;

    /// <summary>A name in the folder that isn't taken: "clip.mp3", then "clip (2).mp3".</summary>
    public static string Unique(string dir, string stem, string ext)
    {
        string p = Path.Combine(dir, stem + ext);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{stem} ({i}){ext}");
        return p;
    }

    async Task<string> ConvertAsync(DownloadJob job, string dest)
    {
        string stem = Path.GetFileNameWithoutExtension(job.Source) + (job.Format == "small" ? " (smaller)" : "");
        string final = Unique(dest, stem, Extension(job.Format));
        string work = Path.Combine(TempDir, job.Id + Extension(job.Format));
        double duration = 0;
        string last = null;
        job.Stage = "Converting to " + FormatName(job.Format);
        var p = Start(job, tools.PathOf("ffmpeg"), FfmpegArgs(job.Source, work, job.Format));
        p.Line += line =>
        {
            var m = Regex.Match(line, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (m.Success && duration == 0) duration = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            else if (line.StartsWith("out_time_us=") && duration > 0 && long.TryParse(line[12..], out long us)) { job.Progress = Math.Clamp(us / 1e6 / duration, 0, 0.99); Changed0(); }
            else if (line.StartsWith("speed=") && Num(line[6..].TrimEnd('x')) is var sp && sp > 0 && duration > 0) job.Eta = Math.Max(0, (1 - job.Progress) * duration / sp);
            else if (!line.Contains('=') && line.Trim().Length > 0) last = line.Trim();
        };
        int code = await p.Exit.ConfigureAwait(false);
        if (job.State == "cancelled") { try { File.Delete(work); } catch { } return null; }
        if (code != 0) { try { File.Delete(work); } catch { } throw new JobFailed(last != null && last.Length < 200 ? "FFmpeg: " + last : $"FFmpeg stopped (code {code})."); }
        File.Move(work, final);
        return final;
    }

    IJobProcess Start(DownloadJob job, string exe, IReadOnlyList<string> args)
    {
        var p = Run(exe, args);
        lock (gate)
        {
            running[job.Id] = p;
            if (job.State == "cancelled") { try { p.Kill(); } catch { } }
        }
        return p;
    }

    void CleanTemp()
    {
        try { foreach (var f in Directory.EnumerateFiles(TempDir)) if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromSeconds(2)) File.Delete(f); } catch { }
    }

    long lastChanged;
    void Changed0()
    {
        // the island redraws a progress bar at most every 50 ms, however chatty the tool
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref lastChanged) < 50) return;
        Interlocked.Exchange(ref lastChanged, now);
        Ui.Post(() => Changed?.Invoke());
    }

    void Save()
    {
        try
        {
            string json;
            lock (gate) json = JsonSerializer.Serialize(Jobs, DownloadsJson.Default.ListDownloadJob);
            var tmp = file + ".tmp"; File.WriteAllText(tmp, json); File.Move(tmp, file, true);
        }
        catch (Exception ex) { Log.W("downloads", "history save failed: " + ex.Message); }
        Ui.Post(() => Changed?.Invoke());
    }
}

/// <summary>A real program, its output and errors read a line at a time, ended with everything it started.</summary>
sealed class RealProcess : IJobProcess
{
    readonly Process p;
    public event Action<string> Line;
    public Task<int> Exit { get; }
    RealProcess(Process p, Task<int> exit) { this.p = p; Exit = exit; }

    public static IJobProcess Start(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // yt-dlp speaks UTF-8 when asked, so titles in any language come through whole
        psi.Environment["PYTHONIOENCODING"] = "utf-8"; psi.Environment["PYTHONUTF8"] = "1";
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        RealProcess rp = null;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) rp?.Line?.Invoke(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) rp?.Line?.Invoke(e.Data); };
        proc.Start();
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // the computer stays responsive while FFmpeg works
        // Waited for apart from the output callbacks: WaitForExit inside one waits
        // for the reader that's calling it, and the job hung at "Finishing" with
        // its file already saved. WaitForExitAsync also waits for the last line.
        var exit = Task.Run(async () => { await proc.WaitForExitAsync().ConfigureAwait(false); return proc.ExitCode; });
        rp = new RealProcess(proc, exit);
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        return rp;
    }

    public void Kill() { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }
}

/// <summary>The user's known folders.</summary>
public static partial class KnownFolders
{
    [System.Runtime.InteropServices.LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The Downloads folder, wherever it's been moved to (falls back to the one in the user's folder).</summary>
    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, 0, out nint p) == 0)
                {
                    string s = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}

[JsonSerializable(typeof(List<DownloadJob>))]
internal partial class DownloadsJson : JsonSerializerContext { }
