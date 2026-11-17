using System.Net;
using System.Text.Json.Nodes;
using Isola.Services;

namespace Isola.Tests;

/// <summary>Feedback: the user number, the once-a-day and no-repeat limits, scrubbed logs, and what the relay's answers mean.</summary>
public static partial class Scenarios
{
    sealed class FakeRelay : HttpMessageHandler
    {
        public HttpStatusCode Code = HttpStatusCode.Created;
        public string Message = "";
        public readonly List<JsonNode> Bodies = new();
        public bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("offline");
            Bodies.Add(JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Code) { Content = new StringContent(new JsonObject { ["message"] = Message }.ToJsonString()) };
        }
    }

    static (Feedback fb, FakeRelay relay, Func<DateTime> clock, Action<double> advanceHours, string file) NewFeedback(string file = null)
    {
        file ??= Path.Combine(Path.GetTempPath(), "isola-fb-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        var t = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var relay = new FakeRelay();
        Func<DateTime> clock = () => t;
        return (new Feedback(file, relay, clock), relay, clock, h => t = t.AddHours(h), file);
    }

    const string Url = "https://relay.example/feedback";

    [Scenario("the feedback ID is 'User' and nine digits, the same for the same PC, different for another, and never the PC's ID")]
    public static void FeedbackUserId()
    {
        string a = Feedback.UserIdFrom("4c4c4544-0042-3510-8051-b4c04f4e4d32");
        True(Feedback.ValidUserId(a), "format: " + a);
        Eq(a, Feedback.UserIdFrom(" 4C4C4544-0042-3510-8051-B4C04F4E4D32 "), "same PC, however the GUID is written");
        True(a != Feedback.UserIdFrom("4c4c4544-0042-3510-8051-b4c04f4e4d33"), "another PC gets another number");
        True(!a.Contains("4c4c"), "the hardware ID itself isn't in it");
        var many = Enumerable.Range(0, 2000).Select(i => Feedback.UserIdFrom(Guid.NewGuid().ToString())).ToList();
        True(many.All(Feedback.ValidUserId), "always nine digits, never a leading zero");
        Eq(2000, many.Distinct().Count(), "no collisions among 2000 PCs");
    }

    [Scenario("feedback: one a day, the same text never twice (whatever the case or spacing), both remembered across restarts")]
    public static void FeedbackLimits()
    {
        var (fb, relay, _, advance, file) = NewFeedback();
        try
        {
            Eq(null, fb.SendAsync(Url, FeedbackKind.Bug, "Glow flickers", "The glow flickers on my second screen.", "1.3.0", "10.0.26300", false, null).Result, "first one goes");
            Eq(1, relay.Bodies.Count, "sent once");
            var again = fb.SendAsync(Url, FeedbackKind.Feature, "Another idea", "Something else entirely, later.", "1.3.0", "10.0.26300", false, null).Result;
            True(again != null && again.Contains("You can send more in 24 h"), "a second within a day is held back here: " + again);
            Eq(1, relay.Bodies.Count, "and never reaches the relay");

            advance(24.1);
            var dup = fb.SendAsync(Url, FeedbackKind.Bug, "glow  FLICKERS!", "the glow flickers on my second screen", "1.3.0", "10.0.26300", false, null).Result;
            Eq("You've already sent this.", dup, "the same words differently written are the same feedback");
            Eq(1, relay.Bodies.Count, "the repeat isn't sent");

            // the relay computes the same fingerprint (tools/feedback-relay/worker.test.mjs checks this value too)
            Eq("8ca0f056b8b880f2a301253119330f994ef493c90ae3f58622ef626d46bd5426", Feedback.Fingerprint("Glow flickers", "The glow flickers, on my 2nd screen!"), "the relay's fingerprint");

            var (fb2, relay2, _, advance2, _) = NewFeedback(file);
            True(fb2.Wait > TimeSpan.FromHours(23), "a restart remembers when it was sent");
            advance2(24.1);
            True(fb2.Wait == TimeSpan.Zero, "and lets the next one through a day later");
            Eq("You've already sent this.", fb2.Problem("Glow flickers", "The glow flickers on my second screen."), "a restart remembers what was sent");
        }
        finally { File.Delete(file); }
    }

    [Scenario("feedback: a subject and a real description are needed; what's sent carries kind, ID, version and source, and logs only when attached")]
    public static void FeedbackPayload()
    {
        var (fb, relay, _, _, file) = NewFeedback();
        try
        {
            Eq("Add a subject.", fb.Problem("  ", "A long enough description."), "no subject");
            Eq("Describe it in a few more words.", fb.Problem("Idea", "short"), "too short");
            Eq(null, fb.SendAsync(Url, FeedbackKind.Feature, "  Dark tint option  ", "  A darker tint for the settings window, please.  ", "1.3.0", "10.0.26300", true, null).Result, "sent");
            var b = relay.Bodies.Single();
            Eq("feature", (string)b["kind"], "kind");
            Eq("Dark tint option", (string)b["subject"], "subject trimmed");
            Eq("A darker tint for the settings window, please.", (string)b["text"], "text trimmed");
            True(Feedback.ValidUserId((string)b["user"]), "the user number goes along");
            Eq("store", (string)b["source"], "Store or GitHub");
            Eq(null, (string)b["logs"], "no logs unless attached");
            var withLogs = JsonNode.Parse(Feedback.Payload("User123456789", FeedbackKind.Bug, "s", "t", "1", "w", false, "log text"))!;
            Eq("log text", (string)withLogs["logs"], "attached logs go along");
        }
        finally { File.Delete(file); }
    }

    [Scenario("the relay's answers: busy or already sent are remembered as such; an outage changes nothing, so it can be sent again")]
    public static void FeedbackRelayAnswers()
    {
        var (fb, relay, _, _, file) = NewFeedback();
        try
        {
            relay.Fail = true;
            var off = fb.SendAsync(Url, FeedbackKind.Bug, "Crash", "It crashed when I opened the shelf.", "1.3.0", "w", false, null).Result;
            True(off.Contains("Couldn't reach"), "offline: " + off);
            Eq(null, fb.Problem("Crash", "It crashed when I opened the shelf."), "an outage doesn't use up the day or mark it sent");

            relay.Fail = false; relay.Code = HttpStatusCode.Conflict;
            Eq("You've already sent this.", fb.SendAsync(Url, FeedbackKind.Bug, "Crash", "It crashed when I opened the shelf.", "1.3.0", "w", false, null).Result, "the relay knew it already");
            Eq("You've already sent this.", fb.Problem("Crash", "It crashed when I opened the shelf."), "and now this PC knows too");

            relay.Code = HttpStatusCode.TooManyRequests; relay.Message = "One a day, please.";
            Eq("One a day, please.", fb.SendAsync(Url, FeedbackKind.Feature, "Idea", "Some brand new idea here.", "1.3.0", "w", false, null).Result, "the relay's own words");
            True(fb.Wait > TimeSpan.FromHours(23), "and the day starts here as well");

            Eq("Feedback isn't available in this build.", fb.SendAsync("", FeedbackKind.Bug, "a", "long enough text", "1", "w", false, null).Result, "no relay configured");
        }
        finally { File.Delete(file); }
    }

    [Scenario("attached logs lose the account and PC names, profile paths, addresses, emails, tokens and the person's own settings")]
    public static void FeedbackRedactsLogs()
    {
        string log = string.Join("\n",
            @"INF [app] Isola 1.3.0 starting · home C:\Users\Jordan\AppData\Local\Isola",
            "INF [ha] connected to http://homeassistant.local:8123/api as jordan@example.com",
            "WRN [net] 192.168.1.40 refused; machine JORDAN-LAPTOP",
            "DBG [spotify] token eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9abcdefghijklmnopqrstuv refreshed",
            "INF [weather] Springfield 19°C",
            "INF [bt] user Jordanna's speaker connected");
        string r = Feedback.Redact(log, "Jordan", "JORDAN-LAPTOP", @"C:\Users\Jordan", new[] { "Springfield", "abc", null });
        foreach (var gone in new[] { @"C:\Users\Jordan", "homeassistant.local", "jordan@example.com", "192.168.1.40", "JORDAN-LAPTOP", "eyJhbGci", "Springfield" })
            True(!r.Contains(gone, StringComparison.OrdinalIgnoreCase), "still in the log: " + gone + "\n" + r);
        True(r.Contains(@"%USERPROFILE%\AppData\Local\Isola"), "paths keep their shape");
        True(r.Contains("Jordanna's"), "a longer word that only starts with the name stays");
        True(r.Contains("19°C"), "the diagnostics themselves stay");
        True(Feedback.Redact("tabcat", "", "", "", new[] { "abc" }).Contains("abc"), "too-short values aren't treated as secrets");
        string big = string.Concat(Enumerable.Repeat("DBG [perf] idle trim\n", Feedback.LogMaxChars / 10)) + "END";
        True(Feedback.Redact(big, "", "", "").Length <= Feedback.LogMaxChars && Feedback.Redact(big, "", "", "").EndsWith("END"), "the newest part, capped");
    }
}
