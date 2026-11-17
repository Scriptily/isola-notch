using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Isola.App;
using Isola.Services;

namespace Isola.Tests;

/// <summary>Downloading and converting: the tools Isola keeps, and the jobs that use them.</summary>
public static partial class Scenarios
{
    /// <summary>GitHub, as far as the tools manager sees it: each project's latest release and its files.</summary>
    sealed class FakeReleases : HttpMessageHandler
    {
        public readonly Dictionary<string, (string tag, string asset, byte[] bytes, string digest, string url)> Latest = new();
        public readonly List<string> Asked = new();
        public bool Offline;
        public void Put(string repo, string tag, string asset, byte[] bytes, string digest = null, string url = null) =>
            Latest[repo] = (tag, asset, bytes, digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), url ?? $"https://github.com/{repo}/releases/download/{tag}/{asset}");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            string u = r.RequestUri!.ToString();
            Asked.Add(u);
            if (Offline) throw new HttpRequestException("offline");
            foreach (var (repo, v) in Latest)
            {
                if (u == $"https://api.github.com/repos/{repo}/releases/latest")
                {
                    string digest = v.digest == "" ? "" : $"\"digest\":\"{v.digest}\",";
                    string json = $"{{\"tag_name\":\"{v.tag}\",\"assets\":[{{\"name\":\"other.zip\",\"browser_download_url\":\"https://github.com/{repo}/releases/download/{v.tag}/other.zip\",\"size\":1}},{{\"name\":\"{v.asset}\",{digest}\"size\":{v.bytes.Length},\"browser_download_url\":\"{v.url}\"}}]}}";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
                }
                if (u == v.url) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(v.bytes) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    static byte[] FfmpegZip(string marker)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var n in new[] { "ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe", "ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe", "ffmpeg-9.0.2-essentials_build/bin/ffplay.exe" })
                using (var s = z.CreateEntry(n).Open()) s.Write(Encoding.UTF8.GetBytes(marker + ":" + n));
        }
        return ms.ToArray();
    }

    static (FakeReleases gh, string dir) ToolsWorld()
    {
        var gh = new FakeReleases();
        gh.Put("yt-dlp/yt-dlp", "2026.10.26", "yt-dlp.exe", Encoding.UTF8.GetBytes("yt-dlp v1"));
        gh.Put("GyanD/codexffmpeg", "9.0.2", "ffmpeg-9.0.2-essentials_build.zip", FfmpegZip("v9"));
        gh.Put("quickjs-ng/quickjs", "v0.18.0", "qjs-windows-x86_64.exe", Encoding.UTF8.GetBytes("qjs"));
        string dir = Path.Combine(Path.GetTempPath(), "isola-tools-" + Guid.NewGuid().ToString("N")[..8]);
        return (gh, dir);
    }

    static T Wait<T>(Task<T> t, FakeClock clock = null)
    {
        for (int i = 0; i < 400 && !t.IsCompleted; i++) { Thread.Sleep(10); Ui.Drain(); }
        if (!t.IsCompleted) throw new Exception("timed out");
        Ui.Drain();
        return t.Result;
    }

    [Scenario("download tools: fetched from each project's own GitHub release, checked against GitHub's SHA-256, only ffmpeg.exe kept from the FFmpeg zip")]
    public static void ToolsAreFetchedAndVerified()
    {
        var (_, _, clock) = NewIsland();
        var (gh, dir) = ToolsWorld();
        try
        {
            var tools = new DownloadTools(dir, clock, gh);
            True(!tools.Ready, "nothing at first");
            True(Wait(tools.CheckAsync(true)), $"all fetched ({tools.Status})");
            Eq("yt-dlp v1", File.ReadAllText(Path.Combine(dir, "yt-dlp.exe")), "yt-dlp as published");
            Eq("v9:ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe", File.ReadAllText(Path.Combine(dir, "ffmpeg.exe")), "ffmpeg.exe out of the zip");
            True(!File.Exists(Path.Combine(dir, "ffprobe.exe")) && !File.Exists(Path.Combine(dir, "ffplay.exe")), "and nothing else from it (100 MB each)");
            Eq("qjs", File.ReadAllText(Path.Combine(dir, "qjs.exe")), "QuickJS as qjs.exe");
            Eq("2026.10.26", tools.VersionOf("yt-dlp"), "versions kept");
            Eq("0.18.0", tools.VersionOf("quickjs"), "a leading v dropped");
            True(Directory.GetFiles(dir).All(f => !f.EndsWith(".download") && !f.EndsWith(".new")), "no leftovers");
            // the next check finds them current and downloads nothing
            gh.Asked.Clear();
            True(Wait(tools.CheckAsync(true)), "still ready");
            True(gh.Asked.All(u => u.StartsWith("https://api.github.com/")), "only asked what's newest");
            // a new yt-dlp: replaced; the others left alone
            gh.Put("yt-dlp/yt-dlp", "2026.11.20", "yt-dlp.exe", Encoding.UTF8.GetBytes("yt-dlp v2"));
            Wait(tools.CheckAsync(false));
            Eq("yt-dlp v2", File.ReadAllText(Path.Combine(dir, "yt-dlp.exe")), "updated");
            Eq("2026.11.20", new DownloadTools(dir, clock, gh).VersionOf("yt-dlp"), "and remembered across restarts");
            // not while a download is using it
            tools.InUse = () => true;
            gh.Put("yt-dlp/yt-dlp", "2026.12.01", "yt-dlp.exe", Encoding.UTF8.GetBytes("yt-dlp v3"));
            Wait(tools.CheckAsync(false));
            Eq("yt-dlp v2", File.ReadAllText(Path.Combine(dir, "yt-dlp.exe")), "a tool in use waits for the next check");
            tools.InUse = () => false;
            // off: the daily check leaves them alone, though asking still works
            tools.Enabled = () => false;
            Wait(tools.CheckAsync(false));
            Eq("yt-dlp v2", File.ReadAllText(Path.Combine(dir, "yt-dlp.exe")), "downloads off: no updating");
            True(tools.Remove() && !Directory.Exists(dir), "Remove frees the space");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Scenario("download tools fail closed: a wrong checksum, no checksum, a download from anywhere else, or offline installs nothing and keeps what works")]
    public static void ToolsFailClosed()
    {
        var (_, _, clock) = NewIsland();
        var (gh, dir) = ToolsWorld();
        try
        {
            gh.Put("yt-dlp/yt-dlp", "2026.10.26", "yt-dlp.exe", Encoding.UTF8.GetBytes("tampered"), digest: "sha256:" + new string('0', 64));
            var tools = new DownloadTools(dir, clock, gh);
            True(!Wait(tools.CheckAsync(true)), "not ready");
            True(!File.Exists(Path.Combine(dir, "yt-dlp.exe")), "a file that doesn't match is thrown away");
            True(tools.Status.Contains("checksum"), $"and it says why ({tools.Status})");
            gh.Put("yt-dlp/yt-dlp", "2026.10.26", "yt-dlp.exe", Encoding.UTF8.GetBytes("yt-dlp v1"), digest: "");
            Wait(tools.CheckAsync(true));
            True(!File.Exists(Path.Combine(dir, "yt-dlp.exe")), "no checksum from GitHub: not installed");
            gh.Put("yt-dlp/yt-dlp", "2026.10.26", "yt-dlp.exe", Encoding.UTF8.GetBytes("yt-dlp v1"), url: "https://evil.example.com/yt-dlp.exe");
            Wait(tools.CheckAsync(true));
            True(!File.Exists(Path.Combine(dir, "yt-dlp.exe")) && !gh.Asked.Any(u => u.Contains("evil")), "a download from anywhere but the project's releases is never fetched");
            // a good install, then a bad update: the working version stays
            gh.Put("yt-dlp/yt-dlp", "2026.10.26", "yt-dlp.exe", Encoding.UTF8.GetBytes("yt-dlp v1"));
            True(Wait(tools.CheckAsync(true)), "installed");
            gh.Put("yt-dlp/yt-dlp", "2026.11.20", "yt-dlp.exe", Encoding.UTF8.GetBytes("tampered v2"), digest: "sha256:" + new string('1', 64));
            Wait(tools.CheckAsync(true));
            Eq("yt-dlp v1", File.ReadAllText(Path.Combine(dir, "yt-dlp.exe")), "a bad update leaves the working one");
            Eq("2026.10.26", tools.VersionOf("yt-dlp"), "and its version");
            gh.Offline = true;
            True(Wait(tools.CheckAsync(true)), "offline: still ready with what it has");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>A stand-in for yt-dlp or FFmpeg: the test writes its output and decides when it ends.</summary>
    sealed class FakeJob : IJobProcess
    {
        public event Action<string> Line;
        readonly TaskCompletionSource<int> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> Exit => done.Task;
        public string Exe; public IReadOnlyList<string> Args; public bool Killed;
        public void Say(string l) => Line?.Invoke(l);
        public void End(int code) => done.TrySetResult(code);
        public void Kill() { Killed = true; done.TrySetResult(-1); }
    }

    static (Downloads dl, List<FakeJob> started, string home, Config cfg) JobsWorld(FakeClock clock)
    {
        string home = Path.Combine(Path.GetTempPath(), "isola-dl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        string tdir = Path.Combine(home, "download-tools");
        Directory.CreateDirectory(tdir);
        foreach (var t in DownloadTools.All) File.WriteAllText(Path.Combine(tdir, t.Exe), "x");
        var cfg = new Config { DownloadTo = "folder", DownloadFolder = Path.Combine(home, "out") };
        var started = new List<FakeJob>();
        var dl = new Downloads(Path.Combine(home, "downloads.json"), new DownloadTools(tdir, clock)) { Cfg = () => cfg };
        dl.Run = (exe, args) => { var j = new FakeJob { Exe = exe, Args = args }; lock (started) started.Add(j); return j; };
        return (dl, started, home, cfg);
    }

    static void Until(Func<bool> ok, string what)
    {
        for (int i = 0; i < 300 && !ok(); i++) { Thread.Sleep(10); Ui.Drain(); }
        Ui.Drain();
        if (!ok()) throw new Exception("timed out waiting: " + what);
    }

    [Scenario("a download: yt-dlp's progress becomes one bar (video, then audio, then merging), its title and file are read, and it lands where Settings says")]
    public static void DownloadProgressAndResult()
    {
        var (_, _, clock) = NewIsland();
        var (dl, started, home, cfg) = JobsWorld(clock);
        try
        {
            DownloadJob finished = null;
            dl.Finished += j => finished = j;
            var job = dl.Download("https://www.youtube.com/watch?v=jNQXAC9IVRw", audio: false);
            Until(() => started.Count == 1, "yt-dlp started");
            var p = started[0];
            True(p.Exe.EndsWith("yt-dlp.exe"), "yt-dlp");
            Eq("--", p.Args[^2], "the link comes after --, so it's never read as an option");
            Eq("https://www.youtube.com/watch?v=jNQXAC9IVRw", p.Args[^1], "and it's the last argument");
            True(p.Args.Contains("--ignore-config") && p.Args.Contains("--no-playlist"), "no surprises from a config file or a whole playlist");
            True(string.Join(" ", p.Args).Contains("vcodec:h264,res:1080,acodec:m4a"), "H.264 and AAC, up to 1080p by default");
            p.Say("ISOLA|title|Me at the zoo");
            p.Say("ISOLA|dl|500|1000|NA|2048|3|avc1.4d401e|none");
            Eq("Me at the zoo", job.Title, "the title");
            Eq("Downloading video", job.Stage, "the video stream first");
            True(Math.Abs(job.Progress - 0.425) < 1e-9, $"half the video is 42.5% ({job.Progress})");
            Eq(2048.0, job.Speed, "speed"); Eq(3.0, job.Eta, "time left");
            p.Say("ISOLA|dl|1000|1000|NA|2048|0|avc1.4d401e|none");
            p.Say("ISOLA|dl|10|200|NA|100|1|none|mp4a.40.2");
            Eq("Downloading audio", job.Stage, "then the audio");
            True(job.Progress > 0.85 && job.Progress < 0.95, $"the bar keeps going forward ({job.Progress})");
            p.Say("ISOLA|pp|started|Merger");
            Eq("Merging", job.Stage, "merging");
            string outFile = Path.Combine(cfg.DownloadFolder, "Me at the zoo.mp4");
            Directory.CreateDirectory(cfg.DownloadFolder);
            File.WriteAllText(outFile, "video");
            p.Say("ISOLA|file|" + outFile);
            p.End(0);
            Until(() => job.State == "done", "done");
            Eq(outFile, job.Output, "the file");
            Eq(1.0, job.Progress, "full");
            Until(() => finished != null, "announced");
            Eq(job, finished, "the alert is for it");
            // audio, on the shelf
            cfg.DownloadTo = "shelf";
            var shelf = new ShelfStore(Path.Combine(home, "shelf.json"));
            dl.Shelf = shelf;
            var a = dl.Download("https://soundcloud.com/x/y", audio: true);
            Until(() => started.Count == 2, "second run");
            var q = started[1];
            True(q.Args.Contains("-x") && q.Args.Contains("mp3"), "extract audio as MP3");
            True(q.Args.Contains(dl.ShelfDir), "into the shelf's folder");
            Directory.CreateDirectory(dl.ShelfDir);
            string mp3 = Path.Combine(dl.ShelfDir, "Song.mp3"); File.WriteAllText(mp3, "a");
            q.Say("ISOLA|pp|started|ExtractAudio");
            Eq("Converting to MP3", a.Stage, "converting");
            q.Say("ISOLA|file|" + mp3); q.End(0);
            Until(() => a.State == "done", "audio done");
            Until(() => shelf.Items.Any(i => i.Path == mp3), "kept on the shelf");
            // the history survives a restart
            var again = new Downloads(Path.Combine(home, "downloads.json"), new DownloadTools(Path.Combine(home, "download-tools"), clock));
            True(again.Jobs.Count == 2 && again.Jobs.All(j => j.State == "done"), "history kept");
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Scenario("downloads: two at a time, the rest wait; cancelling stops the program; errors read as sentences; anything running when Isola closed shows as not finished")]
    public static void DownloadQueueCancelAndErrors()
    {
        var (_, _, clock) = NewIsland();
        var (dl, started, home, cfg) = JobsWorld(clock);
        try
        {
            var jobs = Enumerable.Range(0, 4).Select(i => dl.Download($"https://example.com/v{i}", false)).ToList();
            Until(() => started.Count == 2, "two started");
            Thread.Sleep(100); Ui.Drain();
            Eq(2, started.Count, "only two at a time");
            Eq("queued", jobs[2].State, "the third waits");
            True(started[0].Args[^1].EndsWith("v0") && started[1].Args[^1].EndsWith("v1"), "in the order asked");
            dl.Cancel(jobs[0]);
            True(started[0].Killed, "cancel ends the program (and what it started)");
            Eq("cancelled", jobs[0].State, "cancelled");
            Until(() => started.Count == 3, "the next one starts in its place");
            started[1].Say("ERROR: [youtube] abc: Private video. Sign in if you've been granted access to this video");
            started[1].End(1);
            Until(() => jobs[1].State == "failed", "failed");
            Eq("That video is private.", jobs[1].Error, "a sentence, not a log line");
            Eq("That site isn't supported.", Downloads.Explain("ERROR: Unsupported URL: https://example.com/x"), "unsupported");
            Eq("There's nothing at that link (the page wasn't found).", Downloads.Explain("ERROR: [generic] Unable to download webpage: HTTP Error 404: Not Found (caused by <HTTPError 404: Not Found>)"), "a 404 isn't a connection problem");
            Eq("Couldn't reach the site. Check your connection.", Downloads.Explain("ERROR: [generic] Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>"), "offline");
            // what was running when Isola closed
            var after = new Downloads(Path.Combine(home, "downloads.json"), new DownloadTools(Path.Combine(home, "download-tools"), clock));
            True(after.Jobs.Where(j => j.Source.EndsWith("v2") || j.Source.EndsWith("v3")).All(j => j.State == "failed" && j.Error.Contains("closed")), "shown as not finished, not stuck running");
            foreach (var s in started) s.End(1);
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Scenario("only real web links are downloaded: no file:, no options, no spaces; conversions only of media files, to formats that make sense")]
    public static void DownloadInputsAreChecked()
    {
        foreach (var good in new[] { "https://www.youtube.com/watch?v=jNQXAC9IVRw", "http://example.com/a.mp4", "https://x.com/user/status/123" })
            True(Downloads.IsLink(good), "a link: " + good);
        foreach (var bad in new[] { "file:///C:/Windows/win.ini", "--exec calc", "-o x https://a.b", "https://a.b --exec calc", "ftp://a.b/c", "javascript:alert(1)", "C:\\video.mp4", "", null, "https://" })
            True(!Downloads.IsLink(bad), "not a link: " + bad);
        var (_, _, clock) = NewIsland();
        var (dl, _, home, _) = JobsWorld(clock);
        try
        {
            bool threw = false; try { dl.Download("--exec calc", false); } catch (ArgumentException) { threw = true; }
            True(threw, "refused before anything runs");
            string txt = Path.Combine(home, "notes.txt"); File.WriteAllText(txt, "x");
            threw = false; try { dl.Convert(txt, "mp3"); } catch (ArgumentException) { threw = true; }
            True(threw, "a text file isn't converted");
            Eq("mp3,m4a,wav", string.Join(",", Downloads.FormatsFor("song.flac")), "audio becomes audio");
            Eq("mp3,gif,m4a,wav,webm,small", string.Join(",", Downloads.FormatsFor("clip.mp4")), "a video becomes anything but itself");
            Eq(Path.Combine(home, "notes (2).txt"), Downloads.Unique(home, "notes", ".txt"), "names don't overwrite");
            var gif = Downloads.FfmpegArgs("in.mp4", "out.gif", "gif");
            True(string.Join(" ", gif).Contains("palettegen") && string.Join(" ", gif).Contains("dither"), "GIFs get their own palette, dithered, so they don't band");
            Eq("in.mp4", gif[gif.IndexOf("-i") + 1], "the input is its own argument");
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Scenario("a conversion: FFmpeg's progress against the length, the file named after the original and never overwriting one")]
    public static void ConversionProgressAndResult()
    {
        var (_, _, clock) = NewIsland();
        var (dl, started, home, cfg) = JobsWorld(clock);
        try
        {
            string src = Path.Combine(home, "Holiday clip.mp4"); File.WriteAllText(src, "v");
            Directory.CreateDirectory(cfg.DownloadFolder);
            File.WriteAllText(Path.Combine(cfg.DownloadFolder, "Holiday clip.mp3"), "older");
            var job = dl.Convert(src, "mp3");
            Until(() => started.Count == 1, "ffmpeg started");
            var p = started[0];
            True(p.Exe.EndsWith("ffmpeg.exe"), "FFmpeg");
            Eq(src, p.Args[p.Args.ToList().IndexOf("-i") + 1], "the file");
            p.Say("  Duration: 00:01:40.00, start: 0.000000, bitrate: 312 kb/s");
            p.Say("out_time_us=25000000");
            True(Math.Abs(job.Progress - 0.25) < 1e-9, $"a quarter of the way ({job.Progress})");
            Eq("Converting to MP3", job.Stage, "says what it's doing");
            File.WriteAllText(p.Args[^1], "mp3 data");   // FFmpeg writes its work file
            p.Say("progress=end"); p.End(0);
            Until(() => job.State == "done", "done");
            Eq(Path.Combine(cfg.DownloadFolder, "Holiday clip (2).mp3"), job.Output, "beside the older one, not over it");
            Eq("older", File.ReadAllText(Path.Combine(cfg.DownloadFolder, "Holiday clip.mp3")), "which is untouched");
            True(!Directory.EnumerateFiles(dl.TempDir).Any(), "nothing left in the work folder");
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Scenario("an update keeps the download tools: they move from the old program folder into the new, and a failure only means fetching them again")]
    public static void SetupKeepsTheTools()
    {
        string root = Path.Combine(Path.GetTempPath(), "isola-carry-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string old = Path.Combine(root, "Isola.old"), fresh = Path.Combine(root, "Isola");
            Directory.CreateDirectory(Path.Combine(old, "download-tools"));
            File.WriteAllText(Path.Combine(old, "download-tools", "yt-dlp.exe"), "yt");
            Directory.CreateDirectory(fresh);
            var log = new List<string>();
            True(Isola.Shared.ToolsCarry.Keep(old, fresh, log.Add), "kept");
            Eq("yt", File.ReadAllText(Path.Combine(fresh, "download-tools", "yt-dlp.exe")), "in the new folder");
            True(!Directory.Exists(Path.Combine(old, "download-tools")), "moved, not copied (no 130 MB copy)");
            True(!Isola.Shared.ToolsCarry.Keep(old, fresh, log.Add), "nothing to keep the second time");
            // a file in the way (locked, odd) never stops the update
            Directory.CreateDirectory(Path.Combine(old, "download-tools"));
            using (File.Open(Path.Combine(fresh, "download-tools", "yt-dlp.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
                True(!Isola.Shared.ToolsCarry.Keep(old, fresh, log.Add), "can't: says so, doesn't throw");
            True(log.Last().Contains("fetches them again"), "logged");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Scenario("a real program run for a job: every line of its output and errors arrives, and its end is seen (it once hung, waiting on its own reader)")]
    public static void RealProcessEnds()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var lines = new List<string>();
        var p = RealProcess.Start(cmd, new[] { "/c", "echo first & echo second 1>&2 & echo third & exit 3" });
        p.Line += l => { lock (lines) lines.Add(l.Trim()); };
        True(p.Exit.Wait(TimeSpan.FromSeconds(10)), "it ends");
        Eq(3, p.Exit.Result, "with its exit code");
        lock (lines) True(lines.Contains("first") && lines.Contains("second") && lines.Contains("third"), "every line, from both streams: " + string.Join(",", lines));
        // and stopping one ends it at once
        var slow = RealProcess.Start(cmd, new[] { "/c", "ping -n 30 127.0.0.1 >nul" });
        Thread.Sleep(200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        slow.Kill();
        True(slow.Exit.Wait(TimeSpan.FromSeconds(5)) && sw.Elapsed.TotalSeconds < 5, "Kill ends it (and what it started)");
    }
}
