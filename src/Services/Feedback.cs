using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Isola.Services;

public enum FeedbackKind { Feature, Bug }

/// <summary>
/// Feedback from Settings -> Feedback. It goes to a small relay
/// (tools/feedback-relay) that files it as an issue in a private repository:
/// the relay holds the only credential, so nothing in this open source build
/// can read the feedback or post around the relay's limits.
///
/// The limits (one a day per person, never the same text twice) are checked
/// here first so the page can say so before anything is sent, and again by the
/// relay, which is what actually enforces them.
/// </summary>
public sealed partial class Feedback
{
    /// <summary>The relay's address; empty until it's deployed (the page then says feedback isn't available).</summary>
    public const string Endpoint = "";

    public const int SubjectMax = 120, TextMin = 10, TextMax = 4000, LogMaxChars = 60_000;
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);

    readonly string stateFile;
    readonly HttpClient http;
    readonly Func<DateTime> now;
    DateTime? lastSent;
    readonly HashSet<string> sent = new();

    public Feedback(string stateFile, HttpMessageHandler handler = null, Func<DateTime> clock = null)
    {
        this.stateFile = stateFile;
        now = clock ?? (() => DateTime.UtcNow);
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Isola-Feedback/1.0");
        Load();
    }

    // ---- who: a number made from this PC, never the PC's ID itself ----------

    /// <summary>
    /// "User" and nine digits, the same every time on this PC. It's a one-way
    /// hash of Windows' machine GUID: the GUID never leaves the PC and the
    /// number can't be turned back into it, which is why there is no decrypt.
    /// A stable number is all that sorting feedback by person needs.
    /// </summary>
    public static string UserIdFrom(string hardwareId)
    {
        byte[] mac = HMACSHA256.HashData("Isola feedback id v1"u8, Encoding.UTF8.GetBytes((hardwareId ?? "").Trim().ToLowerInvariant()));
        ulong v = BitConverter.ToUInt64(mac, 0);
        return "User" + (100_000_000 + v % 900_000_000).ToString(CultureInfo.InvariantCulture);
    }

    public static bool ValidUserId(string id) => id != null && UserIdRx().IsMatch(id);
    [GeneratedRegex(@"^User[1-9]\d{8}$")] private static partial Regex UserIdRx();

    static string userId;
    public static string UserId => userId ??= UserIdFrom(HardwareId());

    /// <summary>Windows' machine GUID (set when Windows is installed); if it can't be read, a random one kept beside the settings.</summary>
    static string HardwareId()
    {
        try
        {
            using var k = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (k?.GetValue("MachineGuid") is string g && g.Length > 0) return g;
        }
        catch { }
        string f = Path.Combine(App.Paths.Home, "feedback.id");
        try { if (File.Exists(f)) return File.ReadAllText(f).Trim(); var id = Guid.NewGuid().ToString(); File.WriteAllText(f, id); return id; }
        catch { return Environment.MachineName + Environment.UserName; }
    }

    // ---- limits ----------------------------------------------------------------

    /// <summary>The same words with different capitals, spacing or punctuation count as the same feedback.</summary>
    public static string Fingerprint(string subject, string text)
    {
        string norm = NonWord().Replace(((subject ?? "") + " " + (text ?? "")).ToLowerInvariant(), " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm))).ToLowerInvariant();
    }
    [GeneratedRegex(@"[^\p{L}\p{N}]+")] private static partial Regex NonWord();

    /// <summary>How long until feedback can be sent again; zero when it can.</summary>
    public TimeSpan Wait => lastSent is DateTime t && now() - t < Cooldown ? Cooldown - (now() - t) : TimeSpan.Zero;

    /// <summary>Why this can't be sent (shown under the button), or null when it can.</summary>
    public string Problem(string subject, string text)
    {
        if (Wait > TimeSpan.Zero) return $"Thanks for your feedback. You can send more in {Span(Wait)}.";
        if (string.IsNullOrWhiteSpace(subject)) return "Add a subject.";
        if ((text ?? "").Trim().Length < TextMin) return "Describe it in a few more words.";
        if (sent.Contains(Fingerprint(subject, text))) return "You've already sent this.";
        return null;
    }

    public static string Span(TimeSpan t) =>
        t.TotalMinutes < 1 ? "a minute" : t.TotalHours < 1 ? $"{Math.Ceiling(t.TotalMinutes):0} min" : $"{(int)t.TotalHours} h {t.Minutes} min";

    void Load()
    {
        try
        {
            if (!File.Exists(stateFile)) return;
            var o = JsonNode.Parse(File.ReadAllText(stateFile));
            if (DateTime.TryParse((string)o?["LastSent"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) lastSent = t.ToUniversalTime();
            if (o?["Sent"] is JsonArray a) foreach (var h in a) if ((string)h is { Length: 64 } s) sent.Add(s);
        }
        catch { }
    }

    void Save()
    {
        try
        {
            var o = new JsonObject { ["LastSent"] = lastSent?.ToString("o"), ["Sent"] = new JsonArray(sent.Select(s => (JsonNode)s).ToArray()) };
            File.WriteAllText(stateFile, o.ToJsonString());
        }
        catch { }
    }

    // ---- logs: diagnostics without the person in them ------------------------

    /// <summary>
    /// The end of the log, with the account name, the PC's name, profile paths,
    /// addresses, emails and tokens taken out. The log already leaves out who a
    /// notification is from and what it says.
    /// </summary>
    public static string Redact(string log, string userName, string machineName, string profile, IEnumerable<string> secrets = null)
    {
        if (string.IsNullOrEmpty(log)) return "";
        if (log.Length > LogMaxChars) log = log[^LogMaxChars..];
        string s = log;
        foreach (var x in (secrets ?? []).Where(x => !string.IsNullOrWhiteSpace(x) && x.Length >= 4).OrderByDescending(x => x.Length))
            s = s.Replace(x, "<removed>", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(profile)) s = s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        s = UrlRx().Replace(s, "<address>");
        s = EmailRx().Replace(s, "<email>");
        s = IpRx().Replace(s, "<ip>");
        s = LongTokenRx().Replace(s, "<token>");
        foreach (var name in new[] { userName, machineName }.Where(n => !string.IsNullOrWhiteSpace(n) && n.Length >= 3))
            s = Regex.Replace(s, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", "<name>", RegexOptions.IgnoreCase);
        return s;
    }
    [GeneratedRegex(@"\b(?:https?|wss?)://[^\s'""<>)]+", RegexOptions.IgnoreCase)] private static partial Regex UrlRx();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")] private static partial Regex EmailRx();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")] private static partial Regex IpRx();
    [GeneratedRegex(@"\b[A-Za-z0-9_\-]{40,}\b")] private static partial Regex LongTokenRx();

    // ---- sending ---------------------------------------------------------------

    public static string Payload(string user, FeedbackKind kind, string subject, string text, string version, string windows, bool store, string logs) =>
        new JsonObject
        {
            ["user"] = user,
            ["kind"] = kind == FeedbackKind.Bug ? "bug" : "feature",
            ["subject"] = subject.Trim(),
            ["text"] = text.Trim(),
            ["version"] = version,
            ["windows"] = windows,
            ["source"] = store ? "store" : "github",
            ["logs"] = string.IsNullOrEmpty(logs) ? null : logs,
        }.ToJsonString();

    /// <summary>Sends it; null on success, otherwise what to tell the person.</summary>
    public async Task<string> SendAsync(string endpoint, FeedbackKind kind, string subject, string text, string version, string windows, bool store, string logs)
    {
        if (string.IsNullOrEmpty(endpoint)) return "Feedback isn't available in this build.";
        subject = (subject ?? "").Trim();
        text = (text ?? "").Trim();
        if (subject.Length > SubjectMax) subject = subject[..SubjectMax];
        if (text.Length > TextMax) text = text[..TextMax];
        if (Problem(subject, text) is { } p) return p;
        string fp = Fingerprint(subject, text);
        try
        {
            using var body = new StringContent(Payload(UserId, kind, subject, text, version, windows, store, logs), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(endpoint, body).ConfigureAwait(false);
            string answer = "";
            try { answer = (string)JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false))?["message"] ?? ""; } catch { }
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted:
                    Record(fp);
                    return null;
                case HttpStatusCode.TooManyRequests:
                    // the relay counts a day from its own clock: take its word and wait it out here too
                    lastSent ??= now(); if (now() - lastSent > Cooldown) lastSent = now(); Save();
                    return answer.Length > 0 ? answer : "You've sent feedback recently. Try again tomorrow.";
                case HttpStatusCode.Conflict:
                    sent.Add(fp); Save();
                    return "You've already sent this.";
                default:
                    App.Log.W("feedback", $"relay answered {(int)resp.StatusCode}");
                    return answer.Length > 0 ? answer : $"Couldn't send it ({(int)resp.StatusCode}). Try again later.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            App.Log.W("feedback", "couldn't reach the relay: " + ex.Message);
            return "Couldn't reach the feedback service. Check your connection and try again.";
        }
    }

    void Record(string fp)
    {
        lastSent = now();
        sent.Add(fp);
        Save();
        App.Log.I("feedback", "feedback sent");
    }
}
