using System.Globalization;
using Isola.App;
using Isola.Native;
using Isola.Render;
using Isola.Services;
using Vortice.Mathematics;
using Box = Isola.Render.Box;
using FontWeight = Vortice.DirectWrite.FontWeight;

namespace Isola.Island;

/// <summary>
/// Spotlight / Raycast for Windows. Type; ↑↓ move; Enter runs; → (or Tab)
/// shows the selected item's actions. Answers (math, conversions, colors, time,
/// commands, Home Assistant, notes, web) appear instantly; apps and files come
/// from Everything a moment later and are ranked in with them.
/// </summary>
public sealed class SearchPage : Page
{
    readonly TextBox box = new() { Placeholder = "Search apps, files, commands, or type math…", MaxLength = 260 };
    List<LItem> items = new();
    List<LItem> answers = new();
    List<SearchResult> files = new();
    int sel, actSel = -1;
    long debounce;
    Island isl;
    bool waiting;
    LItem armed; double armedAt;     // destructive command waiting for a second Enter

    public SearchPage() { box.Changed = Schedule; }

    /// <summary>Type a query as if from the keyboard (the showcase uses it).</summary>
    internal void Type(string q) { box.Set(q); Schedule(); }

    public override string Key => "search";
    public override bool WantsKeyboard => true;
    public override double AmbientInterval(Island i) => waiting ? 1 / 30.0 : 0.5;   // skeleton shimmer, else caret blink

    public override void Shown(Island i)
    {
        isl = i;
        box.Set("", false);
        items.Clear(); answers.Clear(); files.Clear(); sel = 0; actSel = -1; armed = null;
        box.Submit = () => Activate();
        // fetch Home Assistant's entity list now, so devices are searchable by the time you've typed
        if (i.Svc?.Ha is { Configured: true } ha && ha.All.Count == 0 && !ha.Busy) _ = ha.RefreshAsync(true);
        box.Escape = () => isl.CloseToRest("search escape");
        box.Touch(i.Clock.Now);
    }

    int Visible => Math.Min(items.Count - top, isl?.Cfg.SearchResults ?? 8);
    // first row shown: the list scrolls when it holds more than fit ("help" lists everything)
    int top;
    void KeepSelVisible() { int rows = isl?.Cfg.SearchResults ?? 8; if (sel < top) top = sel; else if (sel >= top + rows) top = sel - rows + 1; top = Math.Clamp(top, 0, Math.Max(0, items.Count - rows)); }

    public override (float, float, float) Size(Island i)
    {
        isl = i;
        bool unavailable = i.Svc?.Search?.Note != null;
        float rows = Visible + (waiting ? Math.Min(3, (isl?.Cfg.SearchResults ?? 8) - Visible) : 0);
        if (box.Text.Trim().Length == 0) rows = 0;
        float listH = rows * 46;
        float actH = items.Count > 0 ? Actions().Count * 34 + 20 : 0;
        float h = 62 + (rows > 0 ? Math.Max(listH, actH) + 18 : 0) + (unavailable && rows > 0 ? 26 : 0);
        return (680, Math.Min(h, 540), 30);
    }

    void Schedule()
    {
        Ui.Cancel(ref debounce);
        string q = box.Text;
        armed = null;
        answers = Launcher.Answers(q, isl);
        Rebuild();
        if (q.Trim().Length == 0) { files.Clear(); waiting = false; Rebuild(); return; }
        // "10 km to mi", "12*4", "#ff8800", "time in tokyo" are questions, not file
        // names: searching the disk for them only adds noise under the answer
        if (answers.Any(x => x.Tag is "Calculator" or "Convert" or "Color" or "Time")) { files.Clear(); waiting = false; Rebuild(); return; }
        waiting = true;
        debounce = Ui.After(0.09, () => Run(q));
    }

    void Run(string q)
    {
        var s = isl?.Svc?.Search;
        if (s == null) { waiting = false; files.Clear(); Rebuild(); return; }
        s.Query(q, 12, r =>
        {
            if (q != box.Text) return;          // a newer query is on its way
            waiting = false;
            files = r ?? new();
            Log.D("search", $"'{q}' -> {files.Count} file result(s), {answers.Count} answer(s)");
            Rebuild();
        });
    }

    void Rebuild()
    {
        string q = box.Text.Trim();
        var all = new List<LItem>(answers);
        foreach (var f in files)
        {
            var ff = f;
            var it = new LItem
            {
                Title = f.Display, Subtitle = f.IsApp ? "Application" : f.Folder, Tag = f.IsApp ? "App" : f.IsFolder ? "Folder" : "File",
                IconPath = f.FullPath, File = f,
                // apps first, as in Raycast; then files by Everything rank
                Score = f.Score + (f.IsApp ? 120 : 0) - (f.IsFolder ? 15 : 0),
                Run = () => Open(ff, null),
            };
            FileActions(it);
            all.Add(it);
        }
        if (q.Length > 0) all.Add(Launcher.WebFallback(q));
        if (Launcher.IsHelp(q) && isl != null)
        {
            items = Launcher.Help(isl, ex => { box.Set(ex); Schedule(); });
            sel = 0; top = 0; actSel = -1;
            isl.Retarget();
            return;
        }
        // answers pin to the top; everything else by score
        items = all.OrderByDescending(x => x.Answer).ThenByDescending(x => x.Score).ToList();
        top = 0;
        int keep = Math.Min(items.Count, isl?.Cfg.SearchResults ?? 8);
        if (items.Count > keep && !items.Take(keep).Any(x => x.Tag == "Web" && !x.Answer)) { var web = items.Last(); items = items.Take(keep - 1).Append(web).ToList(); }
        else items = items.Take(keep).ToList();
        sel = Math.Clamp(sel, 0, Math.Max(0, items.Count - 1)); actSel = -1;
        isl?.Retarget();
    }

    void FileActions(LItem it)
    {
        var r = it.File; string path = r.FullPath;
        it.Actions.Add(("Open", Glyph.OpenFile, "Enter", () => Open(r, null)));
        bool exe = r.IsApp || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        if (exe) it.Actions.Add(("Run as administrator", Glyph.Shield, "Ctrl+Shift+Enter", () => Open(r, "runas")));
        it.Actions.Add(("Open file location", Glyph.Folder, "Ctrl+Enter", () => { Services.Shell.RevealInExplorer(path); isl.CloseToRest("search reveal"); }));
        it.Actions.Add(("Copy path", Glyph.Copy, "", () => { Win32.SetClipboardText(path); isl.CloseToRest("search copy"); }));
        // a video or a song found by search can be converted from here
        var dl = isl?.Svc?.Downloads;
        if (dl != null && isl.Cfg.Downloads && !r.IsFolder && Services.Downloads.IsMedia(path))
            foreach (var f in Services.Downloads.FormatsFor(path).Where(f => f is "mp3" or "mp4" or "gif").Take(2))
            {
                string to = f;
                it.Actions.Add(($"Convert to {Services.Downloads.FormatName(to)}", Glyph.Convert, "", () => { try { dl.Convert(path, to); isl.ShowDownloads(); } catch (Exception ex) { App.Log.W("downloads", ex.Message); } }));
            }
        it.Actions.Add(("Keep on shelf", Glyph.Pin, "", () => { isl.Svc?.Shelf?.Add(new[] { path }); isl.CloseToRest("kept"); }));
        if (exe)
        {
            it.Actions.Add(("Pin to Start", Glyph.Pin, "", () => { if (!Services.Shell.Verb(path, "pintostartscreen", 0)) More(r); else isl.CloseToRest("pin"); }));
            it.Actions.Add(("Pin to taskbar", Glyph.Pin, "", () => { if (!Services.Shell.Verb(path, "taskbarpin", 0)) More(r); else isl.CloseToRest("pin"); }));
        }
        if (r.IsApp) it.Actions.Add(("Uninstall", Glyph.Uninstall, "", () => { Win32.ShellExecute("ms-settings:appsfeatures"); isl.CloseToRest("uninstall"); }));
        it.Actions.Add(("Properties", Glyph.More, "", () => { Services.Shell.Verb(path, "properties", 0); isl.CloseToRest("properties"); }));
        it.Actions.Add(("More options…", Glyph.More, "", () => More(r)));
    }

    List<(string label, char glyph, string hint, Action run)> Actions() => items.ElementAtOrDefault(sel)?.Actions ?? new();

    void More(SearchResult r)
    {
        Win32.GetCursorPos(out var pt);
        isl.ReleaseKeyboard();
        Services.Shell.ContextMenu(r.FullPath, Win32.GetForegroundWindow(), pt.X, pt.Y);
        isl.CloseToRest("context menu");
    }

    void Open(SearchResult r, string verb)
    {
        Log.I("search", $"open {(verb ?? "")} {r.FullPath}");
        isl.CloseToRest("search open");
        Win32.AllowSetForegroundWindow(-1);   // let the launched app take the foreground from us
        // a Store app opens through shell:AppsFolder, with no working folder
        if (!Win32.ShellExecute(r.FullPath, null, verb, r.IsFolder || r.Folder.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? null : r.Folder)) Log.W("search", "ShellExecute failed for " + r.FullPath);
    }

    void Activate()
    {
        var it = items.ElementAtOrDefault(sel);
        if (it == null) return;
        if (actSel >= 0) { var acts = it.Actions; if (actSel < acts.Count) { Run(it, acts[actSel].run); } return; }
        Run(it, it.Run);
    }

    void Run(LItem it, Action a)
    {
        if (a == null) return;
        if (it.Confirm && (armed != it || isl.Clock.Now - armedAt > 4)) { armed = it; armedAt = isl.Clock.Now; isl.Host.RequestFrame(); return; }
        Log.I("search", $"run '{it.Title}' ({it.Tag})");
        bool staysOpen = it.Tag is "Calculator" or "Convert" or "Color" or "Time";
        Ui.Guard("launcher", a);
        if (it.File == null && !staysOpen && isl.Mode == Mode.Search) isl.CloseToRest("ran " + it.Tag);
        if (staysOpen) { copied = it; copiedAt = isl.Clock.Now; isl.Host.RequestFrame(); }
    }
    LItem copied; double copiedAt;

    public override bool OnKey(Island i, int vk, bool ctrl, bool shift)
    {
        int n = items.Count;
        switch (vk)
        {
            case 0x26: if (actSel >= 0) actSel = Math.Max(0, actSel - 1); else if (n > 0) { sel = (sel - 1 + n) % n; KeepSelVisible(); } armed = null; return true;
            case 0x28: if (actSel >= 0) actSel = Math.Min(Actions().Count - 1, actSel + 1); else if (n > 0) { sel = (sel + 1) % n; KeepSelVisible(); } armed = null; return true;
            case 0x09: if (n > 0) actSel = actSel < 0 ? 0 : -1; return true;
            case 0x27 when box.Caret >= box.Text.Length && n > 0: if (actSel < 0) actSel = 0; return true;
            case 0x25 when actSel >= 0: actSel = -1; return true;
            case 0x0D:
                var it = items.ElementAtOrDefault(sel);
                if (it?.File != null && ctrl && shift) { Open(it.File, "runas"); return true; }
                if (it?.File != null && ctrl) { Services.Shell.RevealInExplorer(it.File.FullPath); isl.CloseToRest("search reveal"); return true; }
                Activate(); return true;
        }
        return box.Key(vk, ctrl, shift, i.Clock.Now);
    }

    public override bool OnChar(Island i, char ch) { box.Char(ch, i.Clock.Now); return true; }

    int haSeen;

    public override void Draw(Island i, Canvas c, Ux ux, Box a, float t)
    {
        isl = i;
        // Home Assistant's list arrived while this query was showing: ask again
        int haNow = i.Svc?.Ha?.All.Count ?? 0;
        if (haNow != haSeen) { haSeen = haNow; if (box.Text.Trim().Length >= 2) { answers = Launcher.Answers(box.Text, isl); Rebuild(); } }
        var accent = Paint.Accent(i);
        var bar = new Box(a.X + 18, a.Y + 10, a.W - 36, 42);
        c.Icon(Glyph.Search, bar.X + 14, bar.CY, 17, Col.White.A(0.85f * t));
        box.Draw(c, new Box(bar.X + 36, bar.Y, bar.W - 110, bar.H), 17, true, i.Clock.Now, accent);
        ux.Add("search:field", bar, () => i.TakeKeyboard(), 12, CursorKind.IBeam);
        var s = i.Svc?.Search;
        string note = s?.Note;
        if (!waiting) c.Text("↑↓ select · Tab actions", new Box(bar.Right - 130, bar.Y, 122, bar.H), 10.5f, Col.White.A(0.3f * t), FontWeight.SemiBold, HAlign.Right);

        if (box.Text.Trim().Length == 0) return;
        float y = a.Y + 62;
        c.Fill(new Box(a.X + 18, y - 4, a.W - 36, 1), Col.White.A(0.08f * t));
        y += 6;
        if (note != null)
        {
            // the built-in index is in use: a quiet note, a spinner while it is still building
            if (s.Local.Building) Paint.Spinner(c, a.X + 30, y + 9, 12, Col.White.A(0.6f), i.Clock.Now, t);
            else c.Icon('', a.X + 30, y + 9, 12, Col.White.A(0.45f * t));   // info
            c.Text(note, new Box(a.X + 44, y, a.W - 80, 18), 11, Col.White.A(0.5f * t), FontWeight.Medium);
            y += 26;
        }
        var acts = Actions();
        float listW = a.W - 36 - 240;
        if (items.Count > (isl.Cfg.SearchResults))
        {
            // more than fit: the wheel scrolls, and a thin bar shows where you are
            var view = new Box(a.X + 14, y, listW, Visible * 46);
            var srScroll = ux.Add("sr:scroll", view, null, 0, CursorKind.Arrow); srScroll.Notched = true; srScroll.ScrollPx = 80; srScroll.Wheel = (d, hz) => { top = Math.Clamp(top - Math.Sign(d) * 2, 0, Math.Max(0, items.Count - isl.Cfg.SearchResults)); sel = Math.Clamp(sel, top, top + isl.Cfg.SearchResults - 1); isl.Host.RequestFrame(); };
            float frac = (float)isl.Cfg.SearchResults / items.Count, pos = (float)top / Math.Max(1, items.Count - isl.Cfg.SearchResults);
            float barH = Math.Max(24, view.H * frac);
            c.Fill(new Box(view.Right + 4, view.Y + (view.H - barH) * pos, 3, barH), Col.White.A(0.25f * t), 1.5f);
        }
        for (int k = 0; k < Visible; k++)
        {
            var it = items[top + k];
            var row = new Box(a.X + 14, y + k * 46, listW, 42);
            bool on = top + k == sel;
            float selT = ux.Anim("sr:sel" + k, on ? 1 : 0, SpringSpec.Snappy);
            int kk = top + k;
            ux.Add("sr:" + k, row, () => { if (sel == kk && actSel < 0) Activate(); else { sel = kk; actSel = -1; armed = null; } }, 12);
            float h = ux.HoverT("sr:" + k);
            c.Fill(row, Col.Mix(Col.White.A(0.06f * h), accent.A(0.32f), selT).A(t), 12);
            var ib = Box.Centered(row.X + 22, row.CY, 26, 26);
            if (it.Swatch is Color4 sw) { c.Fill(ib, sw.A(t), 7); c.Stroke(ib, Col.White.A(0.3f * t), 7); }
            else if (!string.IsNullOrEmpty(it.Icon))
            {
                // commands and answers: a Fluent icon on a soft tile, like an app icon's size
                c.Fill(ib, Col.White.A((on ? 0.16f : 0.09f) * t), 7);
                c.Icon(it.Icon[0], ib.CX, ib.CY, 14, Col.White.A(0.92f * t));
            }
            else
            {
                var ic = it.IconPath != null ? DashboardPage.AppIcon(c, it.IconPath, 32) : null;
                if (ic != null) c.Image(ic, ib, t);
                else c.Icon(it.File?.IsFolder == true ? Glyph.Folder : Glyph.File, ib.CX, ib.CY, 18, Col.White.A(0.7f * t));
            }
            bool flash = copied == it && i.Clock.Now - copiedAt < 1.2;
            bool armedNow = armed == it && i.Clock.Now - armedAt < 4;
            c.Text(armedNow ? $"Press Enter again to {it.Title.ToLowerInvariant()}" : it.Title, new Box(row.X + 44, row.Y + 3, row.W - 130, 20), it.Answer ? 15 : 13.5f,
                (armedNow ? Col.Hex(0xff9f0a) : Col.White).A(t), FontWeight.SemiBold);
            c.Text(flash ? "Copied to clipboard" : it.Subtitle, new Box(row.X + 44, row.Y + 21, row.W - 60, 18), 11, (flash ? Col.Hex(0x34c759) : Col.White).A((flash ? 0.9f : 0.5f) * t), FontWeight.Normal);
            float tw = c.Measure(it.Tag, 10, FontWeight.SemiBold).w + 14;
            var tag = new Box(row.Right - tw - 10, row.Y + 5, tw, 17);
            c.Fill(tag, Col.White.A(0.08f * t), 8.5f);
            c.Text(it.Tag, tag, 10, Col.White.A(0.55f * t), FontWeight.SemiBold, HAlign.Center);
            if (flash) isl.Host.RequestFrame();
        }
        // apps and files still on their way: placeholder rows where they will land
        if (waiting)
        {
            int extra = Math.Min(3, (isl.Cfg.SearchResults) - Visible);
            for (int k = 0; k < extra; k++)
                Paint.SkeletonRow(c, new Box(a.X + 14, y + (Visible + k) * 46, listW, 42), i.Clock.Now, t * (1 - 0.22f * k), k + 1);
        }
        // actions for the selection
        if (acts.Count == 0) return;
        var panel = new Box(a.X + 14 + listW + 10, y, a.Right - 14 - (a.X + 14 + listW + 10), acts.Count * 34 + 14);
        c.Fill(panel, Col.White.A(0.06f * t), 14);
        var cur = items.ElementAtOrDefault(sel);
        for (int k = 0; k < acts.Count; k++)
        {
            var act = acts[k];
            var row = new Box(panel.X + 6, panel.Y + 7 + k * 34, panel.W - 12, 32);
            bool on = k == actSel;
            float h = ux.HoverT("sa:" + k);
            c.Fill(row, (on ? accent.A(0.4f) : Col.White.A(0.08f * h)).A(t), 9);
            var run = act.run;
            ux.Add("sa:" + k, row, () => Run(cur, run), 9);
            c.Icon(act.glyph, row.X + 15, row.CY, 13, Col.White.A(0.85f * t));
            c.Text(act.label, new Box(row.X + 32, row.Y, row.W - 40, row.H), 12, Col.White.A(0.92f * t), FontWeight.Medium);
            float lw = c.Measure(act.label, 12, FontWeight.Medium).w;
            float hw = string.IsNullOrEmpty(act.hint) ? 0 : c.Measure(act.hint, 9.5f, FontWeight.SemiBold).w;
            if (hw > 0 && 32 + lw + 12 + hw < row.W - 8) c.Text(act.hint, new Box(row.Right - hw - 10, row.Y, hw + 2, row.H), 9.5f, Col.White.A(0.35f * t), FontWeight.SemiBold, HAlign.Right);
        }
    }

    static string Size(long b) => b < 1024 ? $"{b} B" : b < 1 << 20 ? $"{b / 1024.0:0} KB" : b < 1L << 30 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

    // ---- calculator --------------------------------------------------------
    /// <summary>"= 12*(3+4)" or plain "12*(3+4)" → "= 84". Public for tests.</summary>
    public static string Calc(string q)
    {
        q = q.Trim().TrimStart('=').Trim();
        // "15% of 80"
        var pm = System.Text.RegularExpressions.Regex.Match(q, @"^\s*([0-9.,]+)\s*%\s*of\s*(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (pm.Success && double.TryParse(pm.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
        {
            string of = pm.Groups[2].Value.Trim();
            double? baseV = double.TryParse(of.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double b0) ? b0
                : Calc(of) is { } r0 && double.TryParse(r0.TrimStart('=', ' ').Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double b1) ? b1 : null;
            if (baseV is double bv) return "= " + Math.Round(pct / 100 * bv, 10).ToString("#,0.##########", CultureInfo.InvariantCulture);
        }
        if (q.Length < 3 || !q.Any(ch => "+-*/^%".Contains(ch)) || !q.Any(char.IsDigit)) return null;
        if (q.Any(ch => !"0123456789.+-*/^%() ,".Contains(ch))) return null;
        try
        {
            int p = 0;
            double v = Expr(q.Replace(" ", "").Replace(",", ""), ref p);
            if (p != q.Replace(" ", "").Replace(",", "").Length || double.IsNaN(v) || double.IsInfinity(v)) return null;
            return "= " + (Math.Abs(v) >= 1e15 || (Math.Abs(v) < 1e-6 && v != 0) ? v.ToString("G10", CultureInfo.InvariantCulture) : Math.Round(v, 10).ToString("#,0.##########", CultureInfo.InvariantCulture));
        }
        catch { return null; }
    }
    static double Expr(string s, ref int p) { double v = Term(s, ref p); while (p < s.Length && (s[p] == '+' || s[p] == '-')) { char o = s[p++]; double r = Term(s, ref p); v = o == '+' ? v + r : v - r; } return v; }
    static double Term(string s, ref int p) { double v = Pow(s, ref p); while (p < s.Length && (s[p] == '*' || s[p] == '/' || s[p] == '%')) { char o = s[p++]; double r = Pow(s, ref p); v = o == '*' ? v * r : o == '/' ? v / r : v % r; } return v; }
    static double Pow(string s, ref int p) { double v = Unary(s, ref p); if (p < s.Length && s[p] == '^') { p++; v = Math.Pow(v, Pow(s, ref p)); } return v; }
    static double Unary(string s, ref int p) { if (p < s.Length && s[p] == '-') { p++; return -Unary(s, ref p); } if (p < s.Length && s[p] == '+') { p++; return Unary(s, ref p); } return Atom(s, ref p); }
    static double Atom(string s, ref int p)
    {
        if (p < s.Length && s[p] == '(') { p++; double v = Expr(s, ref p); if (p >= s.Length || s[p] != ')') throw new FormatException(); p++; return v; }
        int st = p; while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.')) p++;
        if (st == p) throw new FormatException();
        return double.Parse(s[st..p], CultureInfo.InvariantCulture);
    }
}
