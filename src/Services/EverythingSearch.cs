using System.Runtime.InteropServices;
using System.Text;
using Isola.App;
using Isola.Native;

namespace Isola.Services;

public sealed class SearchResult
{
    public string Name = "", Folder = "";
    /// <summary>A display name when the file name isn't one (Store apps: Name is their app id).</summary>
    public string Title;
    public bool IsFolder, IsApp;
    public long Size = -1;
    public DateTime Modified;
    public uint RunCount;
    public double Score;
    public string FullPath => IsFolder && Folder.Length == 0 ? Name : Path.Combine(Folder, Name);
    public string Display => Title ?? (IsApp && Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Name[..^4] : Name);
}

/// <summary>
/// System-wide search through Everything (voidtools), using its documented IPC:
/// a WM_COPYDATA "query2" to Everything's hidden window, answered with a
/// WM_COPYDATA back to ours. No DLL, nothing to install. If Everything is not
/// running the search tab says so and stays out of the way.
/// </summary>
public sealed unsafe class EverythingSearch
{
    const int COPYDATA_QUERY2W = 18;
    const uint REQ_NAME = 0x1, REQ_PATH = 0x2, REQ_SIZE = 0x10, REQ_DATE_MODIFIED = 0x40, REQ_RUN_COUNT = 0x400;
    const uint SORT_RUN_COUNT_DESC = 20;
    const uint ITEM_FOLDER = 0x1, ITEM_DRIVE = 0x2;

    readonly nint replyHwnd;
    uint seq = 0x4E00;
    readonly Dictionary<uint, (string query, bool apps, Action<List<SearchResult>> done)> inflight = new();
    public bool Available => Win32.FindWindowW("EVERYTHING_TASKBAR_NOTIFICATION", null) != 0;

    public EverythingSearch(nint replyHwnd)
    {
        this.replyHwnd = replyHwnd;
        // Everything may run elevated; let its replies through UIPI.
        Win32.ChangeWindowMessageFilterEx(replyHwnd, Win32.WM_COPYDATA, 1, 0);
    }

    static readonly string[] StartMenuDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
    };

    /// <summary>
    /// Run two queries - shortcuts (apps), then everything - and hand back
    /// ranked, merged results. Everything works on one IPC search at a time and
    /// drops the one in flight when another arrives, so the second query is only
    /// sent once the first has answered; and nothing waits more than 700 ms.
    /// </summary>
    public void Query(string text, int max, Action<List<SearchResult>> done)
    {
        text = text.Trim();
        if (text.Length == 0) { done(new()); return; }
        List<SearchResult> apps = null, files = null;
        bool finished = false;
        long timeout = 0;
        void Merge(bool force)
        {
            if (finished || (!force && (apps == null || files == null))) return;
            finished = true;
            Ui.Cancel(ref timeout);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = new List<SearchResult>();
            // shortcuts that live in a Start menu are the installed apps
            foreach (var a in apps ?? new())
                if (StartMenuDirs.Any(d => a.Folder.StartsWith(d, StringComparison.OrdinalIgnoreCase)) && seen.Add(a.FullPath)) { a.IsApp = true; all.Add(a); }
            foreach (var f in files ?? new()) if (seen.Add(f.FullPath)) all.Add(f);
            foreach (var r in all) r.Score = Rank(r, text);
            done(all.OrderByDescending(r => r.Score).Take(max).ToList());
        }
        bool filesSent = false;
        void SendFiles() { if (filesSent || finished) return; filesSent = true; Send(Escape(text), 40, r => { files = r; Merge(false); }); }
        if (!Send("ext:lnk " + Escape(text), 60, r => { apps = r; SendFiles(); Merge(false); })) { done(null); return; }
        timeout = Ui.After(0.35, () =>
        {
            if (finished) return;
            if (!filesSent) { Log.D("search", "apps query got no answer; asking for files"); apps ??= new(); SendFiles(); }
            timeout = Ui.After(0.35, () => { if (!finished) Log.D("search", "a reply didn't come; showing what arrived"); Merge(true); });
        });
    }

    static string Escape(string s) => s.Replace("\"", "");

    internal static double Rank(SearchResult r, string q)
    {
        string name = (r.Title ?? Path.GetFileNameWithoutExtension(r.Name)).ToLowerInvariant(), ql = q.ToLowerInvariant();
        double s = 0;
        if (name == ql) s += 100; else if (name.StartsWith(ql)) s += 60; else if (name.Split(' ', '-', '_', '.').Any(w => w.StartsWith(ql))) s += 40; else if (name.Contains(ql)) s += 20;
        if (r.IsApp) s += 45;
        if (r.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s += 10;
        s += Math.Min(30, Math.Log2(1 + r.RunCount) * 8);
        double days = (DateTime.Now - r.Modified).TotalDays;
        if (days >= 0) s += Math.Max(0, 15 - Math.Log2(1 + days) * 2.5);
        string folder = r.Folder.ToLowerInvariant();
        if (folder.Contains("\\windows\\winsxs") || folder.Contains("\\$recycle.bin") || folder.Contains("\\appdata\\local\\temp") || folder.Contains("\\node_modules\\") || folder.Contains("\\.git\\")) s -= 40;
        s -= Math.Min(10, r.FullPath.Length / 30.0);
        return s;
    }

    bool Send(string query, int max, Action<List<SearchResult>> done)
    {
        nint ev = Win32.FindWindowW("EVERYTHING_TASKBAR_NOTIFICATION", null);
        if (ev == 0) return false;
        uint id = ++seq;
        // EVERYTHING_IPC_QUERY2: 7 DWORDs then the zero-terminated UTF-16 search string
        int bytes = 7 * 4 + (query.Length + 1) * 2;
        nint buf = Marshal.AllocHGlobal(bytes);
        try
        {
            uint* p = (uint*)buf;
            p[0] = (uint)replyHwnd; p[1] = id; p[2] = 0; p[3] = 0; p[4] = (uint)max;
            p[5] = REQ_NAME | REQ_PATH | REQ_SIZE | REQ_DATE_MODIFIED | REQ_RUN_COUNT; p[6] = SORT_RUN_COUNT_DESC;
            char* s = (char*)(buf + 28);
            for (int i = 0; i < query.Length; i++) s[i] = query[i];
            s[query.Length] = '\0';
            var cds = new COPYDATASTRUCT { dwData = COPYDATA_QUERY2W, cbData = bytes, lpData = buf };
            inflight[id] = (query, false, done);
            nint ok = Win32.SendMessageTimeoutW(ev, Win32.WM_COPYDATA, replyHwnd, (nint)(&cds), 0x2 /*ABORTIFHUNG*/, 1500, out var res);
            if (ok == 0 || res == 0) { inflight.Remove(id); Log.W("search", $"Everything refused the query (ok={ok}, res={res}, err={Marshal.GetLastPInvokeError()})"); done(new()); return true; }
            Log.D("search", $"query #{id} sent to 0x{ev:X} ({max} max): {query}");
        }
        finally { Marshal.FreeHGlobal(buf); }
        return true;
    }

    /// <summary>
    /// Called from the reply window's WM_COPYDATA. Returns true if it was ours.
    ///
    /// The window accepts WM_COPYDATA from any process (Everything may run as
    /// administrator, and Windows would otherwise drop its replies), so the
    /// reply is trusted only from Everything's own window, and parsing checks
    /// every length against the buffer: a crafted message from another program
    /// used to be able to make the parser read past it.
    /// </summary>
    public bool OnCopyData(COPYDATASTRUCT* cds, nint sender)
    {
        uint id = (uint)cds->dwData;
        if (id < 0x4E00 || id > seq) return false;                              // not one of our queries
        nint ev = Win32.FindWindowW("EVERYTHING_TASKBAR_NOTIFICATION", null);
        if (ev == 0 || !SameProcess(sender, ev)) { Log.W("search", "ignored a search reply that didn't come from Everything"); return true; }
        if (!inflight.Remove(id, out var q)) return true;                         // stale reply: swallow it
        // Parse now (the buffer only lives for this message), act later: Everything
        // sent this with SendMessage and is blocked until we return, so sending it
        // the next query from in here would stall both of us.
        List<SearchResult> res;
        try { res = ParseList2((byte*)cds->lpData, cds->cbData); }
        catch (Exception ex) { Log.E("search", "bad reply from Everything", ex); res = new(); }
        Ui.Post(() => q.done(res));
        return true;
    }

    static bool SameProcess(nint a, nint b)
    {
        if (a == 0 || b == 0) return false;
        Win32.GetWindowThreadProcessId(a, out uint pa);
        Win32.GetWindowThreadProcessId(b, out uint pb);
        return pa != 0 && pa == pb;
    }

    /// <summary>Parse EVERYTHING_IPC_LIST2, never reading past <paramref name="len"/> bytes. Public for tests.</summary>
    public static List<SearchResult> ParseList2(byte* data, int len)
    {
        var list = new List<SearchResult>();
        if (data == null || len < 20) return list;
        uint* h = (uint*)data;
        uint numItems = h[1], reqFlags = h[3];
        long items = 20;
        for (uint i = 0; i < numItems && i < 100_000; i++)
        {
            if (items + (i + 1) * 8L > len) break;
            uint flags = *(uint*)(data + items + i * 8), off = *(uint*)(data + items + i * 8 + 4);
            if (off >= len) continue;
            var rd = new Reader(data, off, len);
            var r = new SearchResult { IsFolder = (flags & (ITEM_FOLDER | ITEM_DRIVE)) != 0 };
            // fields appear in ascending flag order
            if ((reqFlags & REQ_NAME) != 0) r.Name = rd.Str();
            if ((reqFlags & REQ_PATH) != 0) r.Folder = rd.Str();
            if ((reqFlags & 0x4) != 0) rd.Str();
            if ((reqFlags & 0x8) != 0) rd.Str();
            if ((reqFlags & REQ_SIZE) != 0) r.Size = rd.I64();
            if ((reqFlags & 0x20) != 0) rd.I64();
            if ((reqFlags & REQ_DATE_MODIFIED) != 0) { long ft = rd.I64(); try { r.Modified = ft > 0 ? DateTime.FromFileTime(ft) : default; } catch { } }
            if ((reqFlags & 0x80) != 0) rd.I64();
            if ((reqFlags & 0x100) != 0) rd.U32();
            if ((reqFlags & 0x200) != 0) rd.Str();
            if ((reqFlags & REQ_RUN_COUNT) != 0) r.RunCount = rd.U32();
            if (rd.Bad) continue;                                                 // malformed: skip, keep the rest
            list.Add(r);
        }
        return list;
    }

    /// <summary>Reads fields from the reply; any read that would pass the end marks it bad and returns nothing.</summary>
    struct Reader
    {
        readonly byte* data; long pos; readonly long len;
        public bool Bad;
        public Reader(byte* d, long p, long l) { data = d; pos = p; len = l; Bad = false; }
        bool Take(long n) { if (Bad || n < 0 || pos + n > len) { Bad = true; return false; } return true; }
        public uint U32() { if (!Take(4)) return 0; uint v = *(uint*)(data + pos); pos += 4; return v; }
        public long I64() { if (!Take(8)) return 0; long v = *(long*)(data + pos); pos += 8; return v; }
        public string Str()
        {
            uint n = U32();
            if (Bad || n > 32767 || !Take((n + 1) * 2L)) { Bad = true; return ""; }
            var s = new string((char*)(data + pos), 0, (int)n); pos += (n + 1) * 2L; return s;
        }
    }
}
