using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Isola.Services;

public enum FeedbackKind { Feature, Bug }

/// <summary>
/// Feedback from Settings -> Feedback. It goes to the hub (the apps' service on
/// Cloudflare, its own project), which stores it privately: nothing in this
/// open source build can read feedback or get around the hub's limits.
///
/// The limits (one a day per person, never the same text twice) are checked
/// here first so the page can say so before anything is sent, and again by the
/// relay, which is what actually enforces them.
/// </summary>
public sealed partial class Feedback
{
    /// <summary>The hub's public intake (every app sends here); empty would make the page say feedback isn't available.</summary>
    public const string Endpoint = "https://hub.scriptily.workers.dev/feedback";

    public const int SubjectMax = 120, TextMin = 10, TextMax = 4000, LogMaxChars = 60_000;
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);

    readonly string stateFile;
    readonly HttpClient http;
    readonly Func<DateTime> now;
    DateTime? lastSent;
    readonly HashSet<string> sent = new();
    string assignedId;

    public Feedback(string stateFile, HttpMessageHandler handler = null, Func<DateTime> clock = null)
    {
        this.stateFile = stateFile;
        now = clock ?? (() => DateTime.UtcNow);
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Isola-Feedback/1.0");
        Load();
    }

    // ---- who: a number made from this computer, never the computer's ID itself ----------

    // The keys are the same in every app on purpose: one machine is one person
    // across all of them, so a report in one app and a report in another can be
    // told apart from two people's.
    static ReadOnlySpan<byte> IdKey => "Scriptily device id v1"u8;
    static ReadOnlySpan<byte> VerifierKey => "Scriptily device verifier v1"u8;
    static readonly UInt128 IdLow = UInt128.Parse("10000000000000000000"), IdSpan = UInt128.Parse("90000000000000000000");   // 20-digit numbers: 10^19 .. 10^20 - 1

    /// <summary>
    /// Always 20 digits, the same every time on this machine and in every app:
    /// a one-way hash of Windows' machine GUID. The GUID never leaves the computer and
    /// the number can't be turned back into it (there is no decrypt). 9 x 10^19
    /// possible IDs is about 11 billion for every person alive; the feedback
    /// hub makes "no two machines share one" certain (see <see cref="VerifierFrom"/>).
    /// </summary>
    public static string UserIdFrom(string hardwareId)
    {
        var h = HMACSHA256.HashData(IdKey, Norm(hardwareId));
        var v = new UInt128(BitConverter.ToUInt64(h, 8), BitConverter.ToUInt64(h, 0));
        return (IdLow + v % IdSpan).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A second, independent hash of the same GUID. The hub knows each machine
    /// by it, so if another machine ever derived the same ID, it's given another
    /// free 20-digit one instead of sharing, and an ID once given is never given
    /// to anyone else.
    /// </summary>
    public static string VerifierFrom(string hardwareId) =>
        Convert.ToHexString(HMACSHA256.HashData(VerifierKey, Norm(hardwareId)), 0, 16).ToLowerInvariant();

    static byte[] Norm(string hardwareId) => Encoding.UTF8.GetBytes((hardwareId ?? "").Trim().ToLowerInvariant());

    public static bool ValidUserId(string id) => id != null && UserIdRx().IsMatch(id);
    [GeneratedRegex(@"^[1-9]\d{19}$")] private static partial Regex UserIdRx();

    /// <summary>The sender's UTC offset right now, "+03:00": the hub shows each report in its reader's time and its sender's.</summary>
    public static string Offset(DateTime utcNow, TimeZoneInfo zone = null)
    {
        var o = (zone ?? TimeZoneInfo.Local).GetUtcOffset(utcNow);
        return (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    static string hardware;
    static string Hardware => hardware ??= HardwareId();
    /// <summary>Set by tests in place of this computer's GUID.</summary>
    internal static string HardwareOverride { set => hardware = value; }

    /// <summary>The ID the service gave this computer, once it has answered; until then the one this computer derives.</summary>
    public string UserId => assignedId ?? UserIdFrom(Hardware);

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
            if ((string)o?["User"] is { } u && ValidUserId(u)) assignedId = u;
            if (o?["Reports"] is JsonArray r) foreach (var n in r) if (n?.GetValueKind() == System.Text.Json.JsonValueKind.Number) reports.Add((long)n);
        }
        catch { }
    }

    void Save()
    {
        try
        {
            var o = new JsonObject
            {
                ["LastSent"] = lastSent?.ToString("o"),
                ["Sent"] = new JsonArray(sent.Select(s => (JsonNode)s).ToArray()),
                ["User"] = assignedId,
                ["Reports"] = new JsonArray(reports.Select(n => (JsonNode)n).ToArray()),
            };
            File.WriteAllText(stateFile, o.ToJsonString());
        }
        catch { }
    }

    // ---- logs: diagnostics without the person in them ------------------------

    /// <summary>
    /// The end of the log, with the account name, the computer's name, profile paths,
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

    public static string Payload(string user, string verifier, FeedbackKind kind, string subject, string text, string version, string windows, bool store, string logs, string tz = null) =>
        new JsonObject
        {
            ["app"] = "isola",   // the feedback hub serves several apps
            ["user"] = user,
            ["verifier"] = verifier,
            ["kind"] = kind == FeedbackKind.Bug ? "bug" : "feature",
            ["subject"] = subject.Trim(),
            ["text"] = text.Trim(),
            ["version"] = version,
            ["windows"] = windows,
            ["source"] = store ? "store" : "github",
            ["tz"] = tz,
            ["logs"] = string.IsNullOrEmpty(logs) ? null : logs,
        }.ToJsonString();

    readonly List<long> reports = new();
    /// <summary>The numbers of the reports this computer has sent, newest last.</summary>
    public IReadOnlyList<long> Reports => reports;

    public readonly record struct Result(string Error, long Report)
    {
        public bool Ok => Error == null;
    }

    /// <summary>Sends it: the report's number on success, otherwise what to tell the person.</summary>
    public async Task<Result> SendAsync(string endpoint, FeedbackKind kind, string subject, string text, string version, string windows, bool store, string logs)
    {
        if (string.IsNullOrEmpty(endpoint)) return new("Feedback isn't available in this build.", 0);
        subject = (subject ?? "").Trim();
        text = (text ?? "").Trim();
        if (subject.Length > SubjectMax) subject = subject[..SubjectMax];
        if (text.Length > TextMax) text = text[..TextMax];
        if (Problem(subject, text) is { } p) return new(p, 0);
        string fp = Fingerprint(subject, text);
        try
        {
            using var body = new StringContent(Payload(UserIdFrom(Hardware), VerifierFrom(Hardware), kind, subject, text, version, windows, store, logs, Offset(now())), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(endpoint, body).ConfigureAwait(false);
            JsonNode answer = null;
            try { answer = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)); } catch { }
            string said = (string)answer?["message"] ?? "";
            // whatever it answered, the ID it names is this computer's from now on
            if ((string)answer?["user"] is { } u && ValidUserId(u) && u != assignedId) { assignedId = u; Save(); }
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted:
                    long number = answer?["report"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (long)answer["report"] : 0;
                    Record(fp, number);
                    return new(null, number);
                case HttpStatusCode.TooManyRequests:
                    // the service counts a day from its own clock: take its word and wait it out here too
                    lastSent ??= now(); if (now() - lastSent > Cooldown) lastSent = now(); Save();
                    return new(said.Length > 0 ? said : "You've sent feedback recently. Try again tomorrow.", 0);
                case HttpStatusCode.Conflict:
                    sent.Add(fp); Save();
                    return new("You've already sent this.", 0);
                default:
                    App.Log.W("feedback", $"the feedback service answered {(int)resp.StatusCode}");
                    return new(said.Length > 0 ? said : $"Couldn't send it ({(int)resp.StatusCode}). Try again later.", 0);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            App.Log.W("feedback", "couldn't reach the feedback service: " + ex.Message);
            return new("Couldn't reach the feedback service. Check your connection and try again.", 0);
        }
    }

    void Record(string fp, long number)
    {
        lastSent = now();
        sent.Add(fp);
        if (number > 0) reports.Add(number);
        Save();
        App.Log.I("feedback", number > 0 ? $"feedback sent as report {number}" : "feedback sent");
    }
}
