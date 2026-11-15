using System.Globalization;
using System.Runtime.InteropServices;
using Isola.App;
using Isola.Native;
using Isola.Render;
using Isola.Services;
using Vortice.Mathematics;

namespace Isola.Island;

/// <summary>One row in the launcher: an app, a file, a command, an answer.</summary>
public sealed class LItem
{
    public string Title = "", Subtitle = "", Tag = "";
    public string IconPath;           // shell icon for this path
    public string Icon;               // a Segoe Fluent Icons glyph (no emojis in the UI)
    public char Glyph;                // or an icon-font glyph
    public Color4? Swatch;            // color answers show a swatch
    public double Score;
    public Action Run;                // Enter
    public List<(string label, char glyph, string hint, Action run)> Actions = new();
    public bool Confirm;              // destructive: Enter twice
    public SearchResult File;         // when it came from Everything
    public bool Answer;               // calculator / conversion / time: pinned to the top
}

/// <summary>
/// Raycast-style providers that answer the query directly: math, unit and
/// color conversions, time in other cities, system and island commands, Home
/// Assistant by name, quick notes, web shortcuts. All offline except the web
/// shortcuts, which just open the browser.
/// </summary>
public static class Launcher
{
    public static bool IsHelp(string q) => q.Trim().ToLowerInvariant() is "help" or "?" or "commands" or "cmds" or "/help";

    // what the prefixes and answers do, for the help list (picking one types its example)
    static readonly (string title, string sub, string icon, string example)[] HelpTips =
    {
        ("Calculator", "Type math: 12*7, (3+4)^2, 2^10, 15% of 80", "\uE8EF", "15% of 80"),
        ("Convert units", "5 km to mi, 70 f to c, 2 gb in mb, 3 cups to ml", "\uECC6", "5 km to mi"),
        ("Colors", "#ff8800 or rgb(255,136,0): copy it in other formats", "\uE790", "#ff8800"),
        ("Time in a city", "time in tokyo, time in london", "\uE774", "time in tokyo"),
        ("Note", "note buy milk  (or todo …, n …)", "\uE70B", "note "),
        ("Timer", "timer 10m, t 90s, timer 1:30", "\uE916", "timer 10m"),
        ("Shelf", "shelf some text to keep", "\uE718", "shelf "),
        ("Web search", "g, yt, w, gh, maps, ddg, claude… then your search", "\uE721", "g "),
        ("Open a website", "Type an address: github.com", "\uE71B", "github.com"),
        ("Volume", "vol 40", "\uE767", "vol 40"),
        ("End an app", "kill notepad (asks before ending it)", "\uE71A", "kill "),
        ("Password", "password, pw 32", "\uE8D7", "password"),
        ("UUID", "uuid", "🆔", "uuid"),
        ("Date and time", "now, timestamp, epoch 1700000000", "\uE787", "now"),
        ("IP address", "ip", "\uE774", "ip"),
        ("Home Assistant", "Type a device or scene name to switch it", "\uE80F", ""),
        ("Apps and files", "Type part of a name: apps first, then files", "\uE8B7", ""),
    };

    /// <summary>
    /// Everything the palette can do, for "help": the commands (runnable as
    /// they are) and a line per kind of answer or prefix (picking one types its
    /// example into the box).
    /// </summary>
    public static List<LItem> Help(Island isl, Action<string> type)
    {
        var list = new List<LItem>();
        double score = 5000;
        foreach (var (title, sub, icon, example) in HelpTips)
        {
            var it = new LItem { Title = title, Subtitle = sub, Tag = "Tip", Icon = icon, Score = score--, Answer = true };
            if (example.Length > 0) { it.Run = () => type(example); it.Actions.Add(("Try it", Render.Glyph.Play, "Enter", it.Run)); }
            list.Add(it);
        }
        foreach (var c in Cmds)
        {
            var run = c.Make(isl);
            var it = new LItem { Title = c.Title, Subtitle = c.Sub + "  ·  " + string.Join(", ", c.Keys.Take(3)), Tag = "Command", Icon = c.Icon, Score = score--, Confirm = c.Confirm, Answer = true, Run = run };
            it.Actions.Add((c.Confirm ? "Run (press Enter twice)" : "Run", Render.Glyph.Play, "Enter", run));
            list.Add(it);
        }
        return list;
    }

    internal static int CommandCount => Cmds.Length;

    public static List<LItem> Answers(string q, Island isl)
    {
        var list = new List<LItem>();
        q = q.Trim();
        if (q.Length == 0) return list;
        Try(() => Calculator(q, list));
        Try(() => Units(q, list));
        Try(() => Color(q, list));
        Try(() => TimeIn(q, list));
        Try(() => Prefixes(q, isl, list));
        Try(() => Generators(q, list));
        Try(() => Tools(q, isl, list));
        Try(() => Commands(q, isl, list));
        // a hint at "help" for anything that looks like a question about it
        if (Match("help", q) >= 60 && !IsHelp(q)) list.Add(new LItem { Title = "help", Subtitle = "Everything the palette can do", Tag = "Tip", Icon = "\uE897", Score = 500 });
        Try(() => HomeAssistant(q, isl, list));
        return list;
    }

    static void Try(Action a) { try { a(); } catch (Exception ex) { Log.D("launcher", "provider failed: " + ex.Message); } }

    /// <summary>Fuzzy match score: exact, prefix, word-prefix, subsequence. 0 = no match.</summary>
    public static double Match(string text, string q)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(q)) return 0;
        string t = text.ToLowerInvariant(), s = q.ToLowerInvariant().Trim();
        if (t == s) return 100;
        if (t.StartsWith(s)) return 80;
        var words = t.Split(new[] { ' ', '-', '_', '.', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(s))) return 60;
        // initials: "esb" -> "empty recycle bin"? (e r b) - and multi-word queries
        var qs = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (qs.Length > 1 && qs.All(w => words.Any(x => x.StartsWith(w)))) return 55;
        if (t.Contains(s)) return 40;
        int i = 0; foreach (char ch in t) if (i < s.Length && ch == s[i]) i++;
        return i == s.Length && s.Length >= 3 ? 18 : 0;
    }

    static LItem Copyable(string title, string sub, string tag, string icon, string copy)
    {
        var it = new LItem { Title = title, Subtitle = sub, Tag = tag, Icon = icon, Score = 1000, Answer = true };
        it.Run = () => Win32.SetClipboardText(copy);
        it.Actions.Add(("Copy answer", Render.Glyph.Copy, "Enter", it.Run));
        return it;
    }

    // ---- math -------------------------------------------------------------
    static void Calculator(string q, List<LItem> list)
    {
        var r = SearchPage.Calc(q);
        if (r == null) return;
        string v = r.TrimStart('=', ' ').Replace(",", "");
        list.Add(Copyable(r, q.Trim().TrimStart('='), "Calculator", "\uE8EF", v));
    }

    // ---- units -------------------------------------------------------------
    static readonly Dictionary<string, (string dim, double toBase, string name)> U = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mm"] = ("len", 0.001, "mm"), ["cm"] = ("len", 0.01, "cm"), ["m"] = ("len", 1, "m"), ["km"] = ("len", 1000, "km"),
        ["in"] = ("len", 0.0254, "in"), ["inch"] = ("len", 0.0254, "in"), ["inches"] = ("len", 0.0254, "in"), ["ft"] = ("len", 0.3048, "ft"), ["feet"] = ("len", 0.3048, "ft"),
        ["yd"] = ("len", 0.9144, "yd"), ["mi"] = ("len", 1609.344, "mi"), ["mile"] = ("len", 1609.344, "mi"), ["miles"] = ("len", 1609.344, "mi"),
        ["mg"] = ("mass", 0.001, "mg"), ["g"] = ("mass", 1, "g"), ["kg"] = ("mass", 1000, "kg"), ["oz"] = ("mass", 28.349523125, "oz"),
        ["lb"] = ("mass", 453.59237, "lb"), ["lbs"] = ("mass", 453.59237, "lb"), ["st"] = ("mass", 6350.29318, "st"),
        ["ml"] = ("vol", 0.001, "mL"), ["l"] = ("vol", 1, "L"), ["gal"] = ("vol", 3.785411784, "gal"), ["floz"] = ("vol", 0.0295735295625, "fl oz"), ["cup"] = ("vol", 0.2365882365, "cup"),
        ["b"] = ("data", 1, "B"), ["kb"] = ("data", 1e3, "KB"), ["mb"] = ("data", 1e6, "MB"), ["gb"] = ("data", 1e9, "GB"), ["tb"] = ("data", 1e12, "TB"),
        ["kib"] = ("data", 1024, "KiB"), ["mib"] = ("data", 1048576, "MiB"), ["gib"] = ("data", 1073741824, "GiB"), ["tib"] = ("data", 1099511627776, "TiB"),
        ["ms"] = ("time", 0.001, "ms"), ["s"] = ("time", 1, "s"), ["sec"] = ("time", 1, "s"), ["min"] = ("time", 60, "min"), ["h"] = ("time", 3600, "h"),
        ["hr"] = ("time", 3600, "h"), ["day"] = ("time", 86400, "days"), ["days"] = ("time", 86400, "days"), ["week"] = ("time", 604800, "weeks"), ["weeks"] = ("time", 604800, "weeks"),
        ["kmh"] = ("speed", 1 / 3.6, "km/h"), ["km/h"] = ("speed", 1 / 3.6, "km/h"), ["mph"] = ("speed", 0.44704, "mph"), ["m/s"] = ("speed", 1, "m/s"), ["knots"] = ("speed", 0.514444, "kn"),
        ["c"] = ("temp", 0, "°C"), ["°c"] = ("temp", 0, "°C"), ["celsius"] = ("temp", 0, "°C"), ["f"] = ("temp", 0, "°F"), ["°f"] = ("temp", 0, "°F"), ["fahrenheit"] = ("temp", 0, "°F"), ["k"] = ("temp", 0, "K"), ["kelvin"] = ("temp", 0, "K"),
    };

    static readonly System.Text.RegularExpressions.Regex UnitRx = new(@"^\s*(-?[\d.,]+)\s*([a-z°/]+)\s+(?:to|in|as|->|=)\s+([a-z°/]+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"10 km to mi", "100 f in c", "4 gb to mib". Public for tests.</summary>
    public static string Convert(string q)
    {
        var m = UnitRx.Match(q);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        if (!U.TryGetValue(m.Groups[2].Value, out var a) || !U.TryGetValue(m.Groups[3].Value, out var b) || a.dim != b.dim) return null;
        double r;
        if (a.dim == "temp")
        {
            double c = a.name switch { "°F" => (v - 32) * 5 / 9, "K" => v - 273.15, _ => v };
            r = b.name switch { "°F" => c * 9 / 5 + 32, "K" => c + 273.15, _ => c };
        }
        else r = v * a.toBase / b.toBase;
        return $"{Fmt(r)} {b.name}";
    }

    static string Fmt(double r) => Math.Abs(r) >= 1e9 || (Math.Abs(r) < 1e-4 && r != 0) ? r.ToString("G6", CultureInfo.InvariantCulture) : Math.Round(r, 4).ToString("#,0.####", CultureInfo.InvariantCulture);

    static void Units(string q, List<LItem> list)
    {
        var r = Convert(q);
        if (r != null) list.Add(Copyable(r, q.Trim(), "Convert", "\uECC6", r.Split(' ')[0].Replace(",", "")));
    }

    // ---- colors -----------------------------------------------------------
    static readonly System.Text.RegularExpressions.Regex HexRx = new(@"^#?([0-9a-f]{3}|[0-9a-f]{6})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex RgbRx = new(@"^rgb\s*\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static void Color(string q, List<LItem> list)
    {
        int r, g, b;
        var h = HexRx.Match(q.Trim());
        if (h.Success && (q.StartsWith('#') || q.Length == 6))
        {
            string x = h.Groups[1].Value; if (x.Length == 3) x = string.Concat(x.Select(ch => $"{ch}{ch}"));
            r = System.Convert.ToInt32(x[..2], 16); g = System.Convert.ToInt32(x[2..4], 16); b = System.Convert.ToInt32(x[4..], 16);
        }
        else
        {
            var m = RgbRx.Match(q.Trim());
            if (!m.Success) return;
            r = Math.Min(255, int.Parse(m.Groups[1].Value)); g = Math.Min(255, int.Parse(m.Groups[2].Value)); b = Math.Min(255, int.Parse(m.Groups[3].Value));
        }
        string hex = $"#{r:X2}{g:X2}{b:X2}", rgb = $"rgb({r}, {g}, {b})";
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0, mx = Math.Max(rf, Math.Max(gf, bf)), mn = Math.Min(rf, Math.Min(gf, bf)), l = (mx + mn) / 2, d = mx - mn;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        double hue = d == 0 ? 0 : mx == rf ? 60 * (((gf - bf) / d) % 6) : mx == gf ? 60 * ((bf - rf) / d + 2) : 60 * ((rf - gf) / d + 4);
        if (hue < 0) hue += 360;
        string hsl = $"hsl({hue:0}, {s * 100:0}%, {l * 100:0}%)";
        var it = new LItem { Title = hex, Subtitle = $"{rgb}   {hsl}", Tag = "Color", Swatch = new Color4(r / 255f, g / 255f, b / 255f, 1), Score = 1000, Answer = true };
        it.Run = () => Win32.SetClipboardText(hex);
        it.Actions.Add(("Copy HEX", Render.Glyph.Copy, "Enter", it.Run));
        it.Actions.Add(("Copy RGB", Render.Glyph.Copy, "", () => Win32.SetClipboardText(rgb)));
        it.Actions.Add(("Copy HSL", Render.Glyph.Copy, "", () => Win32.SetClipboardText(hsl)));
        list.Add(it);
    }

    // ---- time in other places ---------------------------------------------
    static readonly (string city, string tz)[] Cities =
    {
        ("Kuwait", "Arab Standard Time"), ("Riyadh", "Arab Standard Time"), ("Dubai", "Arabian Standard Time"), ("Doha", "Arab Standard Time"),
        ("Cairo", "Egypt Standard Time"), ("Istanbul", "Turkey Standard Time"), ("London", "GMT Standard Time"), ("Paris", "Romance Standard Time"),
        ("Berlin", "W. Europe Standard Time"), ("Madrid", "Romance Standard Time"), ("Rome", "W. Europe Standard Time"), ("Moscow", "Russian Standard Time"),
        ("New York", "Eastern Standard Time"), ("Toronto", "Eastern Standard Time"), ("Chicago", "Central Standard Time"), ("Denver", "Mountain Standard Time"),
        ("Los Angeles", "Pacific Standard Time"), ("San Francisco", "Pacific Standard Time"), ("Seattle", "Pacific Standard Time"), ("Mexico City", "Central Standard Time (Mexico)"),
        ("Sao Paulo", "E. South America Standard Time"), ("Tokyo", "Tokyo Standard Time"), ("Seoul", "Korea Standard Time"), ("Beijing", "China Standard Time"),
        ("Shanghai", "China Standard Time"), ("Hong Kong", "China Standard Time"), ("Singapore", "Singapore Standard Time"), ("Mumbai", "India Standard Time"),
        ("Delhi", "India Standard Time"), ("Karachi", "Pakistan Standard Time"), ("Bangkok", "SE Asia Standard Time"), ("Jakarta", "SE Asia Standard Time"),
        ("Sydney", "AUS Eastern Standard Time"), ("Melbourne", "AUS Eastern Standard Time"), ("Auckland", "New Zealand Standard Time"), ("Honolulu", "Hawaiian Standard Time"),
        ("Johannesburg", "South Africa Standard Time"), ("Lagos", "W. Central Africa Standard Time"), ("Tehran", "Iran Standard Time"), ("Amman", "Jordan Standard Time"),
    };

    static void TimeIn(string q, List<LItem> list)
    {
        string s = q.Trim().ToLowerInvariant();
        string city = s.StartsWith("time in ") ? s[8..] : s.StartsWith("time ") ? s[5..] : s.EndsWith(" time") ? s[..^5] : null;
        if (string.IsNullOrWhiteSpace(city)) return;
        foreach (var (c, tz) in Cities.Where(x => x.city.ToLowerInvariant().StartsWith(city.Trim())).Take(3))
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(tz);
            var t = TimeZoneInfo.ConvertTime(DateTime.Now, zone);
            var diff = zone.GetUtcOffset(DateTime.UtcNow) - TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
            string rel = diff == TimeSpan.Zero ? "same as here" : $"{(diff > TimeSpan.Zero ? "+" : "-")}{Math.Abs(diff.TotalHours):0.#}h from here";
            list.Add(Copyable($"{t:h:mm tt} in {c}", $"{t:dddd, MMM d} · {rel}", "Time", "\uE823", $"{t:h:mm tt}"));
        }
    }

    // ---- prefixes: note, web --------------------------------------------------
    static void Prefixes(string q, Island isl, List<LItem> list)
    {
        string s = q.Trim();
        int sp = s.IndexOf(' ');
        string head = sp > 0 ? s[..sp].ToLowerInvariant() : "", rest = sp > 0 ? s[(sp + 1)..].Trim() : "";
        if ((head is "note" or "todo" or "n") && rest.Length > 0 && isl.Svc?.Notes != null)
        {
            var it = new LItem { Title = $"Add note: {rest}", Subtitle = "Saved to the island's Notes", Tag = "Note", Icon = "\uE70B", Score = 900, Answer = true };
            it.Run = () => { isl.Svc.Notes.Add(rest); isl.Expand(isl.P.Dashboard.TabIndex("notes")); };
            it.Actions.Add(("Add note", Render.Glyph.Add, "Enter", it.Run));
            list.Add(it);
        }
        // "timer 10m", "timer 1:30", "t 90s"
        if ((head is "timer" or "t" or "countdown") && isl.Svc?.Timer != null && CountdownTimer.Parse(rest) is double secs)
        {
            var it = new LItem { Title = $"Start a {CountdownTimer.Describe(secs)} timer", Subtitle = "Counts down on the island and rings when it's done", Tag = "Timer", Icon = "\uE916", Score = 950, Answer = true };
            it.Run = () => isl.Svc.Timer.Start(secs);
            it.Actions.Add(("Start", Render.Glyph.Play, "Enter", it.Run));
            list.Add(it);
        }
        if ((head is "shelf" or "keep") && rest.Length > 0 && isl.Svc?.Shelf != null)
        {
            var it = new LItem { Title = $"Keep on shelf: {rest}", Subtitle = "Stored on the island's shelf", Tag = "Shelf", Icon = "\uE718", Score = 900, Answer = true };
            it.Run = () => { isl.Svc.Shelf.AddText(rest); isl.Expand(isl.P.Dashboard.TabIndex("shelf")); };
            it.Actions.Add(("Keep it", Render.Glyph.Add, "Enter", it.Run));
            list.Add(it);
        }
        var engines = new Dictionary<string, (string name, string url, string icon)>
        {
            ["g"] = ("Google", "https://www.google.com/search?q=", "\uE721"), ["google"] = ("Google", "https://www.google.com/search?q=", "\uE721"),
            ["yt"] = ("YouTube", "https://www.youtube.com/results?search_query=", "▶️"), ["youtube"] = ("YouTube", "https://www.youtube.com/results?search_query=", "▶️"),
            ["w"] = ("Wikipedia", "https://en.wikipedia.org/w/index.php?search=", "\uE721"), ["wiki"] = ("Wikipedia", "https://en.wikipedia.org/w/index.php?search=", "\uE721"),
            ["gh"] = ("GitHub", "https://github.com/search?q=", "\uE721"), ["maps"] = ("Google Maps", "https://www.google.com/maps/search/", "\uE721"),
            ["amazon"] = ("Amazon", "https://www.amazon.com/s?k=", "\uE721"), ["reddit"] = ("Reddit", "https://www.reddit.com/search/?q=", "\uE721"),
            ["ddg"] = ("DuckDuckGo", "https://duckduckgo.com/?q=", "\uE721"), ["chatgpt"] = ("ChatGPT", "https://chatgpt.com/?q=", "\uE721"), ["claude"] = ("Claude", "https://claude.ai/new?q=", "\uE721"),
        };
        if (engines.TryGetValue(head, out var e) && rest.Length > 0)
        {
            string url = e.url + Uri.EscapeDataString(rest);
            var it = new LItem { Title = $"Search {e.name} for “{rest}”", Subtitle = url, Tag = "Web", Icon = e.icon, Score = 950, Answer = true, Run = () => Win32.ShellExecute(url) };
            it.Actions.Add(("Open in browser", Render.Glyph.Globe, "Enter", it.Run));
            it.Actions.Add(("Copy link", Render.Glyph.Copy, "", () => Win32.SetClipboardText(url)));
            list.Add(it);
        }
        // a bare URL or domain
        if (!s.Contains(' ') && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\w-]+(\.[\w-]+)*\.(com|net|org|io|dev|app|ai|cc|co|uk|tv|gg|me|edu|gov)(/\S*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
        {
            string url = s.Contains("://") ? s : "https://" + s;
            var it = new LItem { Title = $"Open {s}", Subtitle = url, Tag = "Link", Icon = "\uE71B", Score = 940, Answer = true, Run = () => Win32.ShellExecute(url) };
            it.Actions.Add(("Open in browser", Render.Glyph.Globe, "Enter", it.Run));
            list.Add(it);
        }
    }

    /// <summary>Always offered last: search the web for whatever was typed.</summary>
    public static LItem WebFallback(string q)
    {
        string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(q.Trim());
        var it = new LItem { Title = $"Search Google for “{q.Trim()}”", Subtitle = "Tip: g, yt, w, gh, maps, ddg, claude… then your search", Tag = "Web", Icon = "\uE721", Score = -1000, Run = () => Win32.ShellExecute(url) };
        it.Actions.Add(("Open in browser", Render.Glyph.Globe, "Enter", it.Run));
        return it;
    }

    // ---- commands ----------------------------------------------------------
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBinW(nint hwnd, string root, uint flags);

    sealed record Cmd(string Title, string Sub, string Icon, string[] Keys, Func<Island, Action> Make, bool Confirm = false);

    static readonly Cmd[] Cmds =
    {
        new("Lock screen", "Lock this PC", "\uE72E", new[] { "lock" }, i => () => LockWorkStation()),
        new("Sleep", "Put the laptop to sleep", "\uE708", new[] { "sleep", "suspend" }, i => () => SetSuspendState(false, false, false)),
        new("Restart", "Restart Windows", "\uE777", new[] { "restart", "reboot" }, i => () => Win32.ShellExecute("shutdown.exe", "/r /t 0", null, null, 0), true),
        new("Shut down", "Turn off the PC", "⏻", new[] { "shutdown", "shut down", "power off", "turn off" }, i => () => Win32.ShellExecute("shutdown.exe", "/s /t 0", null, null, 0), true),
        new("Sign out", "Sign out of Windows", "\uF3B1", new[] { "sign out", "log off", "logout" }, i => () => Win32.ShellExecute("shutdown.exe", "/l", null, null, 0), true),
        new("Empty Recycle Bin", "Windows asks to confirm", "\uE74D", new[] { "empty recycle bin", "empty trash", "trash", "recycle" }, i => () => SHEmptyRecycleBinW(0, null, 0)),
        new("Toggle Bluetooth", "Turn the Bluetooth radio on or off", "\uE702", new[] { "bluetooth", "bt" }, i => () => { var b = i.Svc?.Bluetooth; if (b != null) _ = b.SetRadio(b.RadioOn != true); }),
        new("Toggle mute", "Mute or unmute the output", "\uE74F", new[] { "mute", "unmute" }, i => () => i.Svc?.Audio?.ToggleMute()),
        new("Play / Pause", "Media", "\uE768", new[] { "play", "pause", "resume" }, i => () => i.Svc?.Media?.PlayPause()),
        new("Next track", "Media", "\uE893", new[] { "next", "skip" }, i => () => i.Svc?.Media?.Next()),
        new("Previous track", "Media", "\uE892", new[] { "previous", "prev", "back" }, i => () => i.Svc?.Media?.Previous()),
        new("Timer", "Set a timer of any length", "\uE916", new[] { "timer", "countdown", "alarm" }, i => () => i.Expand(i.P.Dashboard.TabIndex("timer"))),
        new("Island settings", "Isola", "\uE713", new[] { "settings", "preferences", "island settings" }, i => () => i.Host.OpenSettings()),
        new("Hide the island", "Isola", "\uED1A", new[] { "hide island", "hide" }, i => () => i.SetMode(Mode.Hidden, "command")),
        new("Switch island style", "Pill ⇄ Notch", "\uE8AB", new[] { "style", "notch", "pill" }, i => () => { i.Cfg.Style = i.Cfg.Style == "pill" ? "notch" : "pill"; i.ApplyConfig(i.Cfg); i.SettingsChanged?.Invoke(); }),
        new("Clear clipboard history", "Windows clipboard history", "\uE894", new[] { "clear clipboard" }, i => () => i.Svc?.Clipboard?.Clear()),
        new("Open shelf", "Things you've kept", "\uE718", new[] { "shelf" }, i => () => i.Expand(i.P.Dashboard.TabIndex("shelf"))),
        new("Open notes", "Notes and to-dos", "\uE70B", new[] { "notes", "todo" }, i => () => i.Expand(i.P.Dashboard.TabIndex("notes"))),
        new("Sound mixer", "Devices and app volumes", "\uE9E9", new[] { "mixer", "volume", "sound", "audio" }, i => () => i.Expand(i.P.Dashboard.TabIndex("sound"))),
        new("Display settings", "Windows Settings", "\uE7F4", new[] { "display", "screen", "resolution" }, i => () => Win32.ShellExecute("ms-settings:display")),
        new("Sound settings", "Windows Settings", "\uE767", new[] { "sound settings" }, i => () => Win32.ShellExecute("ms-settings:sound")),
        new("Bluetooth settings", "Windows Settings", "\uE8EA", new[] { "bluetooth settings", "devices" }, i => () => Win32.ShellExecute("ms-settings:bluetooth")),
        new("Wi-Fi settings", "Windows Settings", "\uE701", new[] { "wifi", "wi-fi", "network" }, i => () => Win32.ShellExecute("ms-settings:network-wifi")),
        new("Windows Update", "Windows Settings", "\uE895", new[] { "update", "windows update" }, i => () => Win32.ShellExecute("ms-settings:windowsupdate")),
        new("Installed apps", "Windows Settings", "\uE71D", new[] { "apps", "uninstall", "programs" }, i => () => Win32.ShellExecute("ms-settings:appsfeatures")),
        new("Task Manager", "See what's running", "\uE9D9", new[] { "task manager", "processes" }, i => () => Win32.ShellExecute("taskmgr.exe")),
        new("Terminal", "Windows Terminal", "\uE756", new[] { "terminal", "cmd", "powershell" }, i => () => Win32.ShellExecute("wt.exe")),
        // Windows and its tools
        new("Dark / light mode", "Switch Windows' app and system theme", "\uE793", new[] { "dark mode", "light mode", "theme", "dark", "light" }, i => () => ToggleTheme()),
        new("Screenshot", "Snipping Tool: pick an area", "\uF0B5", new[] { "screenshot", "snip", "capture", "screen clip" }, i => () => Win32.ShellExecute("ms-screenclip:")),
        new("Night light", "Windows Settings", "\uF08C", new[] { "night light", "blue light" }, i => () => Win32.ShellExecute("ms-settings:nightlight")),
        new("Notifications settings", "Windows Settings: Do not disturb and apps", "\uEA8F", new[] { "notifications", "do not disturb", "dnd", "focus" }, i => () => Win32.ShellExecute("ms-settings:notifications")),
        new("Battery and power", "Windows Settings", "\uE83F", new[] { "battery", "power", "power mode" }, i => () => Win32.ShellExecute("ms-settings:powersleep")),
        new("Storage", "Windows Settings: what fills the disk", "\uEDA2", new[] { "storage", "disk space", "free space" }, i => () => Win32.ShellExecute("ms-settings:storagesense")),
        new("Default apps", "Windows Settings", "\uE7AC", new[] { "default apps", "default browser" }, i => () => Win32.ShellExecute("ms-settings:defaultapps")),
        new("Startup apps", "Windows Settings", "\uE7B5", new[] { "startup", "startup apps" }, i => () => Win32.ShellExecute("ms-settings:startupapps")),
        new("Mouse settings", "Windows Settings", "\uE962", new[] { "mouse", "pointer speed" }, i => () => Win32.ShellExecute("ms-settings:mousetouchpad")),
        new("Keyboard and language", "Windows Settings", "\uE774", new[] { "keyboard", "language", "input" }, i => () => Win32.ShellExecute("ms-settings:regionlanguage")),
        new("Control Panel", "The classic one", "\uE80F", new[] { "control panel" }, i => () => Win32.ShellExecute("control.exe")),
        new("Device Manager", "Drivers and hardware", "\uE772", new[] { "device manager", "drivers" }, i => () => Win32.ShellExecute("devmgmt.msc")),
        new("Services", "Windows services", "\uE90F", new[] { "services" }, i => () => Win32.ShellExecute("services.msc")),
        new("Event Viewer", "Windows logs", "\uE7BA", new[] { "event viewer", "events", "logs" }, i => () => Win32.ShellExecute("eventvwr.msc")),
        new("Disk Cleanup", "Free space", "\uE75C", new[] { "disk cleanup", "cleanup", "clean" }, i => () => Win32.ShellExecute("cleanmgr.exe")),
        new("Registry Editor", "Asks for administrator rights", "\uE943", new[] { "registry", "regedit" }, i => () => Win32.ShellExecute("regedit.exe")),
        new("Flush DNS", "Clears Windows' DNS cache", "\uE968", new[] { "flush dns", "dns" }, i => () => Win32.ShellExecute("ipconfig.exe", "/flushdns", null, null, 0)),
        new("Calculator", "Windows Calculator", "\uE8EF", new[] { "calculator", "calc" }, i => () => Win32.ShellExecute("calc.exe")),
        new("Notepad", "Plain text", "\uE70F", new[] { "notepad", "text editor" }, i => () => Win32.ShellExecute("notepad.exe")),
        new("Clock and alarms", "Windows Clock", "\uE823", new[] { "clock", "alarms", "stopwatch", "world clock" }, i => () => Win32.ShellExecute("ms-clock:")),
        new("Recycle Bin", "Open it", "\uE74D", new[] { "recycle bin", "bin", "deleted" }, i => () => Win32.ShellExecute("shell:RecycleBinFolder")),
        // your folders
        new("Downloads", "Folder", "\uE896", new[] { "downloads" }, i => () => Win32.ShellExecute("shell:Downloads")),
        new("Documents", "Folder", "\uE8A5", new[] { "documents", "docs" }, i => () => Win32.ShellExecute("shell:Personal")),
        new("Desktop", "Folder", "\uE7F4", new[] { "desktop" }, i => () => Win32.ShellExecute("shell:Desktop")),
        new("Pictures", "Folder", "\uEB9F", new[] { "pictures", "photos" }, i => () => Win32.ShellExecute("shell:My Pictures")),
        new("Screenshots", "Folder", "\uE722", new[] { "screenshots folder" }, i => () => Win32.ShellExecute("shell:Screenshots")),
        new("AppData", "Folder", "\uE8B7", new[] { "appdata", "roaming" }, i => () => Win32.ShellExecute("shell:AppData")),
        new("Startup folder", "Folder", "\uE8B7", new[] { "startup folder" }, i => () => Win32.ShellExecute("shell:Startup")),
        new("Temp folder", "Folder", "\uE8B7", new[] { "temp", "tmp" }, i => () => Win32.ShellExecute("shell:Local AppData\\Temp")),
        // the island itself
        new("New note", "Isola", "\uE70B", new[] { "new note" }, i => () => i.Expand(i.P.Dashboard.TabIndex("notes"))),
        new("Check for updates", "Isola", "\uE895", new[] { "check for updates", "island update", "updates" }, i => () => { if (i.Svc?.Updates is { } u) _ = u.CheckAsync(true); }),
        new("Preview popups", "Isola: one of each kind", "\uEA8F", new[] { "preview popups", "test popups" }, i => () => i.PreviewPopups?.Invoke()),
        new("Spotify settings", "Isola", "\uE8D6", new[] { "spotify", "connect spotify" }, i => () => i.Host.OpenSettings("Spotify")),
        new("Home Assistant settings", "Isola", "\uE80F", new[] { "home assistant", "ha settings" }, i => () => i.Host.OpenSettings("Home Assistant")),
        new("Island logs", "Isola: open the log", "\uE7C3", new[] { "island log", "logs folder" }, i => () => Win32.ShellExecute(Paths.LogFile)),
    };

    // Windows' theme: both the apps' and the system's, as Settings switches them
    static void ToggleTheme()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        int light = k.GetValue("AppsUseLightTheme") is int v ? v : 1;
        k.SetValue("AppsUseLightTheme", light == 1 ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
        k.SetValue("SystemUsesLightTheme", light == 1 ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
        // tell open windows the setting changed, as Settings does
        nint str = Marshal.StringToHGlobalUni("ImmersiveColorSet");
        try { Win32.SendMessageTimeoutW(0xFFFF /*HWND_BROADCAST*/, 0x001A /*WM_SETTINGCHANGE*/, 0, str, 0x0002, 200, out _); }
        finally { Marshal.FreeHGlobal(str); }
    }

    // ---- generators: answers to copy ----------------------------------------
    internal static string Password(int len, Random rng = null)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%&*?-_";
        len = Math.Clamp(len, 8, 64);
        var b = new char[len];
        for (int i = 0; i < len; i++) b[i] = chars[rng != null ? rng.Next(chars.Length) : System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)];
        return new string(b);
    }

    static void Generators(string q, List<LItem> list)
    {
        string s = q.Trim().ToLowerInvariant();
        string[] w = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0) return;
        if (w[0] is "uuid" or "guid") { var g = Guid.NewGuid().ToString(); list.Add(Copyable(g, "A new UUID", "Generate", "🆔", g)); }
        if (w[0] is "password" or "pw" or "pass")
        {
            int len = w.Length > 1 && int.TryParse(w[1], out int n) ? n : 20;
            var p = Password(len); list.Add(Copyable(p, $"A random {p.Length}-character password", "Generate", "\uE8D7", p));
        }
        if (w[0] is "now" or "timestamp" or "date" or "time" && w.Length == 1)
        {
            var now = DateTime.Now;
            list.Add(Copyable(now.ToString("yyyy-MM-dd HH:mm:ss"), now.ToString("dddd, MMMM d, yyyy"), "Now", "\uE787", now.ToString("yyyy-MM-dd HH:mm:ss")));
            long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var it = Copyable(epoch.ToString(), "Unix time (seconds)", "Now", "\uE916", epoch.ToString()); it.Score = 990; list.Add(it);
        }
        if (w[0] is "epoch" or "unix" && w.Length == 2 && long.TryParse(w[1], out long e))
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(e > 99999999999 ? e / 1000 : e).ToLocalTime();
            list.Add(Copyable(t.ToString("yyyy-MM-dd HH:mm:ss"), $"{e} as local time", "Convert", "\uE916", t.ToString("yyyy-MM-dd HH:mm:ss")));
        }
        if (w[0] is "ip" or "my ip" or "ipconfig")
        {
            var ips = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => (n.Name, a.Address)))
                .Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList();
            foreach (var (name, addr) in ips.Take(3)) list.Add(Copyable(addr.ToString(), "Local IP · " + name, "Network", "\uE774", addr.ToString()));
        }
    }

    // ---- tools that take an argument --------------------------------------
    static void Tools(string q, Island isl, List<LItem> list)
    {
        string s = q.Trim();
        int sp = s.IndexOf(' ');
        string head = sp > 0 ? s[..sp].ToLowerInvariant() : s.ToLowerInvariant(), rest = sp > 0 ? s[(sp + 1)..].Trim() : "";
        // "vol 40", "volume 25%"
        if ((head is "vol" or "volume") && int.TryParse(rest.TrimEnd('%'), out int vol) && isl.Svc?.Audio is { } audio)
        {
            vol = Math.Clamp(vol, 0, 100);
            var it = new LItem { Title = $"Set volume to {vol}%", Subtitle = "The current output", Tag = "Sound", Icon = "\uE767", Score = 960, Answer = true };
            it.Run = () => audio.SetVolume(vol / 100f);
            it.Actions.Add(("Set", Render.Glyph.Play, "Enter", it.Run));
            list.Add(it);
        }
        // "kill chrome": end a running app (confirm first; never Windows' own processes)
        if ((head is "kill" or "end" or "quit") && rest.Length >= 2)
        {
            foreach (var g in System.Diagnostics.Process.GetProcesses()
                .Where(p => { try { return p.SessionId == System.Diagnostics.Process.GetCurrentProcess().SessionId && p.ProcessName.Contains(rest, StringComparison.OrdinalIgnoreCase) && !Protected.Contains(p.ProcessName); } catch { return false; } })
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase).Take(5))
            {
                var procs = g.ToList(); string name = g.Key;
                var it = new LItem { Title = $"End {name}", Subtitle = procs.Count == 1 ? "1 process" : $"{procs.Count} processes", Tag = "Kill", Icon = "\uE71A", Score = 940, Answer = true, Confirm = true };
                it.Run = () => { foreach (var p in procs) { try { p.Kill(true); } catch { } } };
                it.Actions.Add(("End it (press Enter twice)", Render.Glyph.Close, "Enter", it.Run));
                list.Add(it);
            }
        }
    }

    // ending these would break the session or the island itself
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "winlogon", "csrss", "lsass", "services", "svchost", "smss", "wininit", "fontdrvhost", "sihost", "ctfmon",
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "RuntimeBroker", "System", "Idle", "Registry", "Isola", "TextInputHost", "conhost",
    };

    static void Commands(string q, Island isl, List<LItem> list)
    {
        foreach (var c in Cmds)
        {
            double sc = Math.Max(Match(c.Title, q), c.Keys.Max(k => Match(k, q)));
            if (sc <= 0) continue;
            var run = c.Make(isl);
            var it = new LItem { Title = c.Title, Subtitle = c.Sub, Tag = "Command", Icon = c.Icon, Score = sc + 30, Confirm = c.Confirm, Run = run };
            it.Actions.Add((c.Confirm ? "Run (press Enter twice)" : "Run", Render.Glyph.Play, "Enter", run));
            list.Add(it);
        }
    }

    // ---- Home Assistant by name ------------------------------------------------
    static void HomeAssistant(string q, Island isl, List<LItem> list)
    {
        var ha = isl.Svc?.Ha;
        if (ha == null || !ha.Configured || q.Trim().Length < 2) return;
        var pool = ha.All.Count > 0 ? ha.All : ha.Entities.Values.ToList();
        // Learn the full list once. This waited for the status to read Online,
        // which only a refresh sets, and the island refreshes only while the
        // Home tab shows: until that tab had been opened once, searching a
        // device by name found nothing. The refresh probes and sets the status.
        if (ha.All.Count == 0 && !ha.Busy) _ = ha.RefreshAsync(true);
        var doms = new HashSet<string> { "light", "switch", "scene", "script", "fan", "climate", "input_boolean", "button", "input_button", "automation", "cover", "lock", "media_player", "humidifier" };
        // Close matches only, and only a few. Switches, booleans, buttons and
        // automations in Home Assistant are mostly device settings (a tablet's
        // kiosk lock, a dehumidifier's child lock), so "lock" listed three of
        // them above the Lock screen command; those are offered only when
        // they're on the island already. Real devices are always searchable.
        var devices = new HashSet<string> { "light", "scene", "script", "climate", "fan", "cover", "lock", "media_player", "humidifier", "vacuum" };
        var chosen = new HashSet<string>(isl.Cfg.HaEntities);
        var hits = pool.Where(x => doms.Contains(x.Domain) && (devices.Contains(x.Domain) || chosen.Contains(x.Id)))
            .Select(x => (e: x, sc: Match(x.Name, q))).Where(h => h.sc >= 55)
            .OrderByDescending(h => h.sc).ThenBy(h => h.e.Domain is "scene" or "script" or "light" ? 0 : 1).Take(4).ToList();
        foreach (var (e, sc) in hits)
        {
            string verb = e.Domain switch { "scene" or "script" => "Run", "button" or "input_button" => "Press", _ => "Toggle" };
            var ent = e;
            var it = new LItem
            {
                Title = e.Name, Subtitle = e.Domain is "scene" or "script" or "button" or "input_button" ? $"{verb} · {e.Id}" : $"{verb} · {e.Id} · {e.State}", Tag = "Home", Icon = e.Domain switch { "light" => "\uEA80", "scene" => "\uE7C1", "script" => "\uE7BA", "climate" => "\uE9CA", "fan" => "\uE9CA", "media_player" => "\uE7F4", "lock" => "\uE72E", _ => "\uE80F" },
                Score = sc + 20, Run = () => ha.Tap(ent),
            };
            it.Actions.Add(($"{verb} in Home Assistant", Render.Glyph.Home, "Enter", it.Run));
            if (e.Domain == "light") { it.Actions.Add(("Turn on", Render.Glyph.Lightbulb, "", () => _ = ha.Call("light", "turn_on", ent.Id))); it.Actions.Add(("Turn off", Render.Glyph.Power, "", () => _ = ha.Call("light", "turn_off", ent.Id))); }
            list.Add(it);
        }
    }
}
