using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Isola.App;

namespace Isola.Services;

/// <summary>An answer shown right in the command palette: a definition, a translation, a Wikipedia summary.</summary>
public sealed class WebAnswer
{
    public string Kind = "";        // "define", "translate", "wiki"
    public string Title = "", Meta = "", Body = "";
    public string Copy = "";        // what Enter copies
    public string Url = "";         // where "Open in browser" goes
    public string Source = "";      // shown as the row's tag
}

/// <summary>What a query is asking, when it's a question the palette can answer itself.</summary>
public sealed record WebIntent(string Kind, string Text, string Lang = null, string LangName = null);

/// <summary>
/// Suggestions as you type (Google's, as browsers' address bars get them) and answers right in the palette:
/// definitions from the free dictionary (Wiktionary's), translations from Google Translate, summaries from
/// Wikipedia. Only with Settings -> Search & Keys -> Web suggestions and answers on (it sends what you type),
/// and never for what looks like a file or folder path.
/// </summary>
public sealed class WebAnswers
{
    readonly HttpClient http;
    readonly Dictionary<string, (List<string> suggestions, WebAnswer answer)> cache = new();
    readonly Queue<string> cacheOrder = new();
    const int CacheMax = 80;
    /// <summary>How many requests went out (tests: what is and isn't sent).</summary>
    public int Requests;

    public WebAnswers(HttpMessageHandler handler = null)
    {
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(4);
        // Wikipedia asks API clients to say what they are; nothing about the person
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Isola/1.7 (+https://github.com/Scriptily/isola-notch)");
    }

    // ---- what's being asked -------------------------------------------------

    static readonly Dictionary<string, string> Langs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arabic"] = "ar", ["english"] = "en", ["spanish"] = "es", ["french"] = "fr", ["german"] = "de", ["italian"] = "it",
        ["portuguese"] = "pt", ["russian"] = "ru", ["chinese"] = "zh-CN", ["mandarin"] = "zh-CN", ["japanese"] = "ja", ["korean"] = "ko",
        ["hindi"] = "hi", ["urdu"] = "ur", ["bengali"] = "bn", ["tamil"] = "ta", ["telugu"] = "te", ["marathi"] = "mr", ["punjabi"] = "pa",
        ["turkish"] = "tr", ["dutch"] = "nl", ["polish"] = "pl", ["swedish"] = "sv", ["norwegian"] = "no", ["danish"] = "da",
        ["finnish"] = "fi", ["greek"] = "el", ["hebrew"] = "iw", ["persian"] = "fa", ["farsi"] = "fa", ["indonesian"] = "id",
        ["malay"] = "ms", ["vietnamese"] = "vi", ["thai"] = "th", ["ukrainian"] = "uk", ["romanian"] = "ro", ["czech"] = "cs",
        ["hungarian"] = "hu", ["tagalog"] = "tl", ["filipino"] = "tl", ["swahili"] = "sw", ["somali"] = "so", ["amharic"] = "am",
        ["kurdish"] = "ku", ["pashto"] = "ps", ["nepali"] = "ne", ["sinhala"] = "si", ["burmese"] = "my", ["khmer"] = "km",
        ["serbian"] = "sr", ["croatian"] = "hr", ["bulgarian"] = "bg", ["slovak"] = "sk", ["catalan"] = "ca", ["irish"] = "ga",
        ["latin"] = "la", ["afrikaans"] = "af", ["zulu"] = "zu", ["yoruba"] = "yo", ["hausa"] = "ha", ["armenian"] = "hy", ["georgian"] = "ka",
    };

    internal static string LanguageName(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        foreach (var (name, c) in Langs) if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
        try { return CultureInfo.GetCultureInfo(code).EnglishName; } catch { return code; }
    }

    static readonly string LangAlt = string.Join("|", Langs.Keys.Select(Regex.Escape));
    const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex[] TranslateTo =
    {
        new($@"^translate\s+(.+?)\s+(?:to|into|in)\s+({LangAlt})$", Opt),
        new($@"^how\s+(?:do\s+(?:you|i)|to)\s+say\s+(.+?)\s+in\s+({LangAlt})$", Opt),
        new($@"^(.+?)\s+meaning\s+in\s+({LangAlt})$", Opt),
        new($@"^(.+?)\s+(?:in|to|into)\s+({LangAlt})$", Opt),
    };
    static readonly Regex TranslateAny = new(@"^translate\s+(.+)$", Opt);
    static readonly Regex[] Define =
    {
        new(@"^(?:define|definition\s+of|meaning\s+of|what\s+is\s+the\s+meaning\s+of|what\s+is\s+the\s+definition\s+of)\s+(.+)$", Opt),
        new(@"^what\s+does\s+(.+?)\s+mean$", Opt),
        new(@"^(.+?)\s+(?:meaning|definition|define|def)$", Opt),
    };
    static readonly Regex Wiki = new(@"^(?:(?:who|what)\s+(?:is|was|are|were)(?:\s+(?:a|an|the))?|tell\s+me\s+about)\s+(.+)$", Opt);
    static readonly Regex Wordish = new(@"^[\p{L}][\p{L}\p{M}'’\- ]{0,40}$", Opt);

    /// <summary>The question a query asks the palette, or null when it's just a search.</summary>
    public static WebIntent Intent(string q)
    {
        q = Regex.Replace((q ?? "").Trim().TrimEnd('?', '.', '!').Trim(), @"\s+", " ");
        if (q.Length < 2) return null;
        foreach (var r in TranslateTo)
        {
            var m = r.Match(q);
            if (m.Success && m.Groups[1].Value.Trim().Length > 0)
            {
                string lang = m.Groups[2].Value.ToLowerInvariant();
                return new WebIntent("translate", Unquote(m.Groups[1].Value), Langs[lang], LanguageName(Langs[lang]));
            }
        }
        if (TranslateAny.Match(q) is { Success: true } ta) return new WebIntent("translate", Unquote(ta.Groups[1].Value), "en", "English");
        foreach (var r in Define)
        {
            var m = r.Match(q);
            if (!m.Success) continue;
            string w = Unquote(m.Groups[1].Value);
            if (Wordish.IsMatch(w) && w.Split(' ').Length <= 4) return new WebIntent("define", w);
        }
        if (Wiki.Match(q) is { Success: true } wm && Unquote(wm.Groups[1].Value) is { Length: >= 2 } topic) return new WebIntent("wiki", topic);
        return null;
    }

    static string Unquote(string s) => s.Trim().Trim('"', '\'', '“', '”', '‘', '’').Trim();

    /// <summary>
    /// What may be sent at all: a file or folder path, a long paste or an address with a password never is
    /// (looking for a file by name shouldn't tell anyone its name).
    /// </summary>
    public static bool Sendable(string q)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2 || q.Length > 200) return false;
        if (q.Contains('\\') || Regex.IsMatch(q, @"^[a-z]:", RegexOptions.IgnoreCase) || q.StartsWith('/') || q.StartsWith('~') || q.StartsWith('.')) return false;
        if (Regex.IsMatch(q, @"\.(?:txt|pdf|docx?|xlsx?|pptx?|csv|json|xml|ini|log|exe|msi|zip|rar|7z|png|jpe?g|gif|webp|mp[34]|mkv|mov|wav|flac|ps1|bat|cmd|py|cs|js|ts|md)$", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(q, @"://[^/\s]*@")) return false;
        return true;
    }

    // ---- asking -------------------------------------------------------------

    /// <summary>Suggestions and, when the query is a question, its answer. Never throws: what fails is left out.</summary>
    public async Task<(List<string> suggestions, WebAnswer answer)> LookUp(string q, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        if (!Sendable(q)) return (new(), null);
        lock (cache) if (cache.TryGetValue(q.ToLowerInvariant(), out var hit)) return hit;
        var intent = Intent(q);
        var sugT = Suggest(q, ct);
        var ansT = intent == null ? Task.FromResult<WebAnswer>(null) : Answer(intent, ct);
        List<string> sug; WebAnswer ans;
        try { sug = await sugT.ConfigureAwait(false); } catch { sug = new(); }
        try { ans = await ansT.ConfigureAwait(false); } catch { ans = null; }
        if (ct.IsCancellationRequested) return (sug, ans);
        var r = (sug, ans);
        lock (cache)
        {
            string k = q.ToLowerInvariant();
            if (!cache.ContainsKey(k)) { cacheOrder.Enqueue(k); if (cacheOrder.Count > CacheMax) cache.Remove(cacheOrder.Dequeue()); }
            cache[k] = r;
        }
        return r;
    }

    async Task<string> Get(string url, CancellationToken ct)
    {
        Interlocked.Increment(ref Requests);
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { Log.D("web", "lookup failed: " + ex.GetType().Name); return null; }
    }

    async Task<List<string>> Suggest(string q, CancellationToken ct)
    {
        string json = await Get("https://suggestqueries.google.com/complete/search?client=firefox&hl=en&q=" + Uri.EscapeDataString(q), ct).ConfigureAwait(false);
        return ParseSuggestions(json, q);
    }

    async Task<WebAnswer> Answer(WebIntent i, CancellationToken ct)
    {
        switch (i.Kind)
        {
            case "define":
                return ParseDefinition(await Get("https://api.dictionaryapi.dev/api/v2/entries/en/" + Uri.EscapeDataString(i.Text.ToLowerInvariant()), ct).ConfigureAwait(false), i.Text);
            case "translate":
                return ParseTranslation(await Get($"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={i.Lang}&dt=t&q=" + Uri.EscapeDataString(i.Text), ct).ConfigureAwait(false), i);
            case "wiki":
                string found = ParseWikiSearch(await Get("https://en.wikipedia.org/w/api.php?action=opensearch&limit=1&format=json&search=" + Uri.EscapeDataString(i.Text), ct).ConfigureAwait(false));
                WebAnswer w = found == null ? null
                    : ParseWiki(await Get("https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(found.Replace(' ', '_')), ct).ConfigureAwait(false));
                // "what is subsidized": not an article, but a word
                if (w == null && !i.Text.Contains(' ')) return await Answer(new WebIntent("define", i.Text), ct).ConfigureAwait(false);
                return w;
        }
        return null;
    }

    // ---- reading the answers (each copes with anything: a changed format just means no answer) ----

    internal static List<string> ParseSuggestions(string json, string q)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(json)) return list;
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Array || d.RootElement.GetArrayLength() < 2 || d.RootElement[1].ValueKind != JsonValueKind.Array) return list;
            foreach (var e in d.RootElement[1].EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) continue;
                string s = e.GetString().Trim();
                if (s.Length == 0 || string.Equals(s, q.Trim(), StringComparison.OrdinalIgnoreCase) || list.Contains(s, StringComparer.OrdinalIgnoreCase)) continue;
                list.Add(s);
                if (list.Count == 4) break;
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return list;
    }

    internal static WebAnswer ParseDefinition(string json, string word)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Array || d.RootElement.GetArrayLength() == 0) return null;
            var e = d.RootElement[0];
            string w = e.TryGetProperty("word", out var we) && we.ValueKind == JsonValueKind.String ? we.GetString() : word;
            string phon = e.TryGetProperty("phonetic", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString() : null;
            if (phon == null && e.TryGetProperty("phonetics", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray()) if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && t.GetString().Length > 0) { phon = t.GetString(); break; }
            var kinds = new List<string>(); var lines = new List<string>(); string first = null;
            if (e.TryGetProperty("meanings", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                {
                    string pos = m.TryGetProperty("partOfSpeech", out var po) && po.ValueKind == JsonValueKind.String ? po.GetString() : "";
                    if (pos.Length > 0 && !kinds.Contains(pos)) kinds.Add(pos);
                    if (!m.TryGetProperty("definitions", out var ds) || ds.ValueKind != JsonValueKind.Array) continue;
                    foreach (var df in ds.EnumerateArray())
                    {
                        if (!df.TryGetProperty("definition", out var dv) || dv.ValueKind != JsonValueKind.String) continue;
                        string def = dv.GetString().Trim();
                        first ??= def;
                        if (lines.Count < 3) lines.Add((pos.Length > 0 ? pos + ": " : "") + def);
                        break;   // one per part of speech: the list stays short
                    }
                }
            if (first == null) return null;
            string url = e.TryGetProperty("sourceUrls", out var su) && su.ValueKind == JsonValueKind.Array && su.GetArrayLength() > 0 && su[0].ValueKind == JsonValueKind.String
                ? su[0].GetString() : "https://en.wiktionary.org/wiki/" + Uri.EscapeDataString(w);
            return new WebAnswer
            {
                Kind = "define", Title = w, Meta = string.Join(" · ", new[] { phon, string.Join(", ", kinds) }.Where(x => !string.IsNullOrEmpty(x))),
                Body = string.Join("\n", lines), Copy = first, Url = url, Source = "Dictionary",
            };
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal static WebAnswer ParseTranslation(string json, WebIntent i)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() == 0 || r[0].ValueKind != JsonValueKind.Array) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var seg in r[0].EnumerateArray())
                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String) sb.Append(seg[0].GetString());
            string text = sb.ToString().Trim();
            if (text.Length == 0) return null;
            string from = r.GetArrayLength() > 2 && r[2].ValueKind == JsonValueKind.String ? LanguageName(r[2].GetString()) : null;
            return new WebAnswer
            {
                Kind = "translate", Title = text, Meta = from != null ? $"{i.LangName}, from {from}" : i.LangName, Body = i.Text, Copy = text,
                Url = $"https://translate.google.com/?sl=auto&tl={i.Lang}&op=translate&text=" + Uri.EscapeDataString(i.Text), Source = "Translate",
            };
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal static string ParseWikiSearch(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() < 2 || r[1].ValueKind != JsonValueKind.Array || r[1].GetArrayLength() == 0) return null;
            return r[1][0].ValueKind == JsonValueKind.String ? r[1][0].GetString() : null;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal static WebAnswer ParseWiki(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            string Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Str(r, "type") == "disambiguation") return null;   // "Mercury": too many to pick from
            string extract = Str(r, "extract"), title = Str(r, "title");
            if (string.IsNullOrWhiteSpace(extract) || title == null) return null;
            string url = r.TryGetProperty("content_urls", out var cu) && cu.TryGetProperty("desktop", out var dk) ? Str(dk, "page") : null;
            return new WebAnswer
            {
                Kind = "wiki", Title = title, Meta = Str(r, "description") ?? "", Body = extract.Trim(), Copy = extract.Trim(),
                Url = url ?? "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')), Source = "Wikipedia",
            };
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
