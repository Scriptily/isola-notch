using System.IO.Enumeration;
using Isola.App;

namespace Isola.Services;

/// <summary>
/// Search for apps and files: Everything when it is running, otherwise the
/// island's own index. Without Everything, search used to say so and find
/// nothing at all - not even apps, which also came from Everything.
/// </summary>
public sealed class FileSearch
{
    public readonly EverythingSearch Everything;
    public readonly LocalIndex Local;
    readonly Func<bool> everythingUp;

    public FileSearch(EverythingSearch everything, LocalIndex local = null, Func<bool> everythingUp = null)
    {
        Everything = everything;
        Local = local ?? new LocalIndex();
        this.everythingUp = everythingUp ?? (() => Everything?.Available == true);
    }

    public bool UsingEverything => everythingUp();
    public bool Available => true;

    /// <summary>A line for the search page when the built-in index is in use, or null.</summary>
    public string Note => UsingEverything ? null
        : Local.Building ? "Everything isn't running: searching your apps and folders (still indexing)"
        : "Everything isn't running: searching your apps and folders";

    public void Query(string text, int max, Action<List<SearchResult>> done)
    {
        if (!UsingEverything) { Local.Query(text, max, done); return; }
        // Everything can close between the check and the query: fall back then
        Everything.Query(text, max, r => { if (r == null) Local.Query(text, max, done); else done(r); });
    }

    /// <summary>Starts the built-in index ahead of the first search, if it will be needed.</summary>
    public void Warm() { if (!UsingEverything) Local.EnsureFresh(); }

    public unsafe void OnCopyData(Native.COPYDATASTRUCT* cds, nint sender) => Everything?.OnCopyData(cds, sender);
}

/// <summary>
/// The island's own index, for when Everything isn't running: installed apps
/// (Start menu shortcuts and Store apps) and the files in your own folders
/// (Desktop, Documents, Downloads, Pictures, Music, Videos, OneDrive). Not a
/// whole-disk index like Everything's, which needs its service to read the
/// drive's file table: a walk of those folders in the background, apps first
/// so they are searchable within a second, refreshed every ten minutes.
/// </summary>
public sealed class LocalIndex
{
    readonly object gate = new();
    List<SearchResult> apps = new(), files = new();
    Task building;
    DateTime builtAt = DateTime.MinValue;
    public TimeSpan RefreshAfter = TimeSpan.FromMinutes(10);
    public int MaxFiles = 250_000;
    public IReadOnlyList<string> Roots = DefaultRoots();
    public bool IncludeApps = true;
    public bool Building => building is { IsCompleted: false };
    public int Count { get { lock (gate) return apps.Count + files.Count; } }

    static readonly string[] StartMenuDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
    };

    public static IReadOnlyList<string> DefaultRoots()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var all = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
        }.Where(d => d.Length > 0 && Directory.Exists(d)).Select(d => Path.GetFullPath(d).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Documents inside OneDrive (folder backup) would be walked twice
        return all.Where(d => !all.Any(o => o != d && d.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public void EnsureFresh()
    {
        if (Building) return;
        if (DateTime.Now - builtAt < RefreshAfter) return;
        building = Task.Run(Build);
    }

    /// <summary>Builds the index on this thread (tests; the app builds in the background).</summary>
    internal void BuildNow() => Build();

    void Build()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (IncludeApps)
            {
                var a = new List<SearchResult>();
                var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in StartMenuDirs.Where(Directory.Exists))
                    foreach (var lnk in SafeFiles(dir, "*.lnk"))
                    {
                        var r = new SearchResult { Name = Path.GetFileName(lnk), Folder = Path.GetDirectoryName(lnk) ?? "", IsApp = true, Modified = DateTime.MinValue };
                        if (titles.Add(r.Display)) a.Add(r);
                    }
                foreach (var (title, aumid) in StoreApps())
                    if (titles.Add(title)) a.Add(new SearchResult { Name = aumid, Title = title, Folder = "shell:AppsFolder", IsApp = true, Modified = DateTime.MinValue });
                lock (gate) apps = a;
            }
            var f = new List<SearchResult>();
            foreach (var root in Roots)
            {
                foreach (var r in Walk(root))
                {
                    f.Add(r);
                    if (f.Count >= MaxFiles) break;
                    if (f.Count % 5000 == 0) lock (gate) files = new List<SearchResult>(f);   // searchable while it grows
                }
                if (f.Count >= MaxFiles) break;
            }
            lock (gate) files = f;
            builtAt = DateTime.Now;
            Log.I("search", $"built-in index: {apps.Count} app(s), {f.Count} file(s) in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { Log.E("search", "built-in index failed", ex); builtAt = DateTime.Now; }
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    // folders that are all noise to a person searching for their own files
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "node_modules", "__pycache__", "$RECYCLE.BIN", "obj", "packages", "venv", ".venv" };

    internal static IEnumerable<SearchResult> Walk(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var en = new FileSystemEnumerable<SearchResult>(root, (ref FileSystemEntry e) => new SearchResult
        {
            Name = e.FileName.ToString(), Folder = e.Directory.ToString(), IsFolder = e.IsDirectory,
            Size = e.IsDirectory ? -1 : e.Length, Modified = e.LastWriteTimeUtc.LocalDateTime,
        }, opts)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => e.FileName.Length > 0 && e.FileName[0] != '.',
            ShouldRecursePredicate = (ref FileSystemEntry e) => e.FileName.Length > 0 && e.FileName[0] != '.' && !SkipDirs.Contains(e.FileName.ToString()),
        };
        using var it = en.GetEnumerator();
        while (true)
        {
            SearchResult cur;
            try { if (!it.MoveNext()) yield break; cur = it.Current; }
            catch { yield break; }
            yield return cur;
        }
    }

    /// <summary>Store apps (Spotify from the Store, Settings, Calculator): what shell:AppsFolder lists, launchable by their app id.</summary>
    static List<(string title, string aumid)> StoreApps()
    {
        var list = new List<(string, string)>();
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var p in pm.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (p.IsFramework || p.IsResourcePackage) continue;
                    foreach (var e in p.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult())
                    {
                        string t = e.DisplayInfo?.DisplayName;
                        if (!string.IsNullOrWhiteSpace(t) && !string.IsNullOrEmpty(e.AppUserModelId)) list.Add((t, e.AppUserModelId));
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.D("search", "Store apps unavailable: " + ex.Message); }
        return list;
    }

    public void Query(string text, int max, Action<List<SearchResult>> done)
    {
        EnsureFresh();
        string q = text.Trim();
        if (q.Length == 0) { done(new()); return; }
        List<SearchResult> a, f;
        lock (gate) { a = apps; f = files; }
        Task.Run(() => Match(a, f, q, max)).ContinueWith(t => Ui.Post(() => done(t.IsCompletedSuccessfully ? t.Result : new())));
    }

    /// <summary>Every word of the query in the name (in any order), ranked as Everything's results are.</summary>
    internal static List<SearchResult> Match(IEnumerable<SearchResult> apps, IEnumerable<SearchResult> files, string q, int max)
    {
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Hit(SearchResult r) { string n = r.Display; foreach (var w in words) if (n.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return false; return true; }
        var hits = new List<SearchResult>();
        foreach (var r in apps) if (Hit(r)) hits.Add(r);
        foreach (var r in files) if (Hit(r)) hits.Add(r);
        foreach (var r in hits) r.Score = EverythingSearch.Rank(r, q);
        return hits.OrderByDescending(r => r.Score).Take(max).ToList();
    }
}
