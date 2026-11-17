using System.Net;
using System.Text.Json.Nodes;
using Isola.Services;

namespace Isola.Tests;

/// <summary>Feedback: the user ID and verifier, the once-a-day and no-repeat limits, scrubbed logs, and what the service's answers mean.</summary>
public static partial class Scenarios
{
    sealed class FakeRelay : HttpMessageHandler
    {
        public HttpStatusCode Code = HttpStatusCode.Created;
        public string Message = "";
        public long Report = 41;
        public string User;          // the ID the service answers with; null to echo the one sent
        public readonly List<JsonNode> Bodies = new();
        public bool Fail;
        public bool Paused;          // the developer paused feedback
        public int StatusAsks;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("offline");
            if (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/status"))
            {
                StatusAsks++;
                var st = new JsonObject { ["app"] = "isola", ["paused"] = Paused };
                if (Paused) st["message"] = "Feedback is paused for now. Please try again later.";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(st.ToJsonString()) };
            }
            if (Paused) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"message\":\"Feedback is paused for now. Please try again later.\",\"paused\":true}") };
            var b = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            Bodies.Add(b);
            var answer = new JsonObject { ["message"] = Message, ["user"] = User ?? (string)b["user"] };
            if (Code == HttpStatusCode.Created) answer["report"] = Report++;
            return new HttpResponseMessage(Code) { Content = new StringContent(answer.ToJsonString()) };
        }
    }

    const string TestMachine = "4c4c4544-0042-3510-8051-b4c04f4e4d32";

    static (Feedback fb, FakeRelay relay, Func<DateTime> clock, Action<double> advanceHours, string file) NewFeedback(string file = null)
    {
        Feedback.HardwareOverride = TestMachine;
        file ??= Path.Combine(Path.GetTempPath(), "isola-fb-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        var t = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var relay = new FakeRelay();
        Func<DateTime> clock = () => t;
        return (new Feedback(file, relay, clock), relay, clock, h => t = t.AddHours(h), file);
    }

    const string Url = "https://relay.example/feedback";

    [Scenario("the feedback ID is always 16 digits, shown as @User and the digits, the same for the same machine in every app, different for another, and never the machine's ID")]
    public static void FeedbackUserId()
    {
        string a = Feedback.UserIdFrom(TestMachine);
        True(Feedback.ValidUserId(a), "format: " + a);
        Eq(16, a.Length, "always 16 digits: " + a);
        // the key is shared by every app (the template keeps it as is), so this value is what every app derives on this machine
        Eq("1953124684870734", a, "the same ID in every app");
        Eq(a, Feedback.UserIdFrom(" 4C4C4544-0042-3510-8051-B4C04F4E4D32 "), "same computer, however the GUID is written");
        True(a != Feedback.UserIdFrom("4c4c4544-0042-3510-8051-b4c04f4e4d33"), "another computer gets another number");
        True(!a.Contains("4c4c"), "the hardware ID itself isn't in it");
        string v = Feedback.VerifierFrom(TestMachine);
        True(v.Length == 32 && v.All(Uri.IsHexDigit), "the verifier is 32 hex digits: " + v);
        True(!a.Contains(v[..8]) && v != Feedback.VerifierFrom("other"), "an independent hash");
        var many = Enumerable.Range(0, 20000).Select(i => Feedback.UserIdFrom(Guid.NewGuid().ToString())).ToList();
        True(many.All(id => id.Length == 16 && Feedback.ValidUserId(id)), "always exactly 16 digits");
        Eq(20000, many.Distinct().Count(), "no collisions among 20,000 machines");
        True(!Feedback.ValidUserId("User1640943131873826") && !Feedback.ValidUserId("164094313187382") && !Feedback.ValidUserId("0640943131873826") && !Feedback.ValidUserId("16409431318738264986"), "nothing but 16 digits");
        Eq("@User1640943131873826", Feedback.Handle("1640943131873826"), "shown and copied as @User and the digits");
    }

    [Scenario("feedback: one a day, the same text never twice (whatever the case or spacing), both remembered across restarts")]
    public static void FeedbackLimits()
    {
        var (fb, relay, _, advance, file) = NewFeedback();
        try
        {
            True(fb.SendAsync(Url, FeedbackKind.Bug, "Glow flickers", "The glow flickers on my second screen.", "1.4.0", "10.0.26300", false, null).Result.Ok, "first one goes");
            Eq(1, relay.Bodies.Count, "sent once");
            var again = fb.SendAsync(Url, FeedbackKind.Feature, "Another idea", "Something else entirely, later.", "1.4.0", "10.0.26300", false, null).Result.Error;
            True(again != null && again.Contains("You can send more in 24 h"), "a second within a day is held back here: " + again);
            Eq(1, relay.Bodies.Count, "and never reaches the service");

            advance(24.1);
            var dup = fb.SendAsync(Url, FeedbackKind.Bug, "glow  FLICKERS!", "the glow flickers on my second screen", "1.4.0", "10.0.26300", false, null).Result.Error;
            Eq("You've already sent this.", dup, "the same words differently written are the same feedback");
            Eq(1, relay.Bodies.Count, "the repeat isn't sent");

            // the hub computes the same fingerprint (its test/service.test.mjs checks this value too)
            Eq("8ca0f056b8b880f2a301253119330f994ef493c90ae3f58622ef626d46bd5426", Feedback.Fingerprint("Glow flickers", "The glow flickers, on my 2nd screen!"), "the service's fingerprint");

            var (fb2, _, _, advance2, _) = NewFeedback(file);
            True(fb2.Wait > TimeSpan.FromHours(23), "a restart remembers when it was sent");
            advance2(24.1);
            True(fb2.Wait == TimeSpan.Zero, "and lets the next one through a day later");
            Eq("You've already sent this.", fb2.Problem("Glow flickers", "The glow flickers on my second screen."), "a restart remembers what was sent");
        }
        finally { File.Delete(file); }
    }

    [Scenario("feedback: a subject and a real description are needed; what's sent carries kind, ID, verifier, version and source, and logs only when attached")]
    public static void FeedbackPayload()
    {
        var (fb, relay, _, _, file) = NewFeedback();
        try
        {
            Eq("Add a subject.", fb.Problem("  ", "A long enough description."), "no subject");
            Eq("Describe it in a few more words.", fb.Problem("Idea", "short"), "too short");
            True(fb.SendAsync(Url, FeedbackKind.Feature, "  Dark tint option  ", "  A darker tint for the settings window, please.  ", "1.4.0", "10.0.26300", true, null).Result.Ok, "sent");
            var b = relay.Bodies.Single();
            Eq("feature", (string)b["kind"], "kind");
            Eq("Dark tint option", (string)b["subject"], "subject trimmed");
            Eq("A darker tint for the settings window, please.", (string)b["text"], "text trimmed");
            Eq(Feedback.UserIdFrom(TestMachine), (string)b["user"], "the derived ID goes along");
            Eq(Feedback.VerifierFrom(TestMachine), (string)b["verifier"], "with its verifier");
            Eq(Feedback.Offset(new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc)), (string)b["tz"], "and the sender's UTC offset");
            Eq("isola", (string)b["app"], "and the app");
            Eq("store", (string)b["source"], "Store or GitHub");
            Eq(null, (string)b["logs"], "no logs unless attached");
            var withLogs = JsonNode.Parse(Feedback.Payload("1640943131873826", new string('a', 32), FeedbackKind.Bug, "s", "t", "1", "w", false, "log text"))!;
            Eq("log text", (string)withLogs["logs"], "attached logs go along");
        }
        finally { File.Delete(file); }
    }

    [Scenario("the service's report number is shown and kept; an ID the service assigns replaces the derived one, for good")]
    public static void FeedbackNumbersAndAssignedId()
    {
        var (fb, relay, _, advance, file) = NewFeedback();
        try
        {
            string derived = Feedback.UserIdFrom(TestMachine);
            Eq(derived, fb.UserId, "before any answer, the derived ID");
            relay.User = "2718281828459045";   // another machine already had it: the hub gave this one another
            var r = fb.SendAsync(Url, FeedbackKind.Bug, "Crash", "It crashed when I opened the shelf.", "1.4.0", "w", false, null).Result;
            Eq(41L, r.Report, "the report's number");
            Eq("2718281828459045", fb.UserId, "the ID the hub assigned");
            var (fb2, relay2, _, advance2, _) = NewFeedback(file);
            Eq("2718281828459045", fb2.UserId, "kept across restarts");
            Eq(41L, fb2.Reports.Single(), "and so is the report number");
            advance2(25);
            relay2.User = null;
            fb2.SendAsync(Url, FeedbackKind.Feature, "Idea", "A brand new idea for later.", "1.4.0", "w", false, null).Wait();
            Eq(Feedback.UserIdFrom(TestMachine), (string)relay2.Bodies.Single()["user"], "it still sends the derived ID: the verifier is what the service goes by");
        }
        finally { File.Delete(file); }
    }

    [Scenario("the service's answers: busy or already sent are remembered as such; an outage changes nothing, so it can be sent again")]
    public static void FeedbackRelayAnswers()
    {
        var (fb, relay, _, _, file) = NewFeedback();
        try
        {
            relay.Fail = true;
            var off = fb.SendAsync(Url, FeedbackKind.Bug, "Crash", "It crashed when I opened the shelf.", "1.4.0", "w", false, null).Result.Error;
            True(off.Contains("Couldn't reach"), "offline: " + off);
            Eq(null, fb.Problem("Crash", "It crashed when I opened the shelf."), "an outage doesn't use up the day or mark it sent");

            relay.Fail = false; relay.Code = HttpStatusCode.Conflict;
            Eq("You've already sent this.", fb.SendAsync(Url, FeedbackKind.Bug, "Crash", "It crashed when I opened the shelf.", "1.4.0", "w", false, null).Result.Error, "the service knew it already");
            Eq("You've already sent this.", fb.Problem("Crash", "It crashed when I opened the shelf."), "and now this computer knows too");

            relay.Code = HttpStatusCode.TooManyRequests; relay.Message = "One a day, please.";
            Eq("One a day, please.", fb.SendAsync(Url, FeedbackKind.Feature, "Idea", "Some brand new idea here.", "1.4.0", "w", false, null).Result.Error, "the service's own words");
            True(fb.Wait > TimeSpan.FromHours(23), "and the day starts here as well");

            Eq("Feedback isn't available in this build.", fb.SendAsync("", FeedbackKind.Bug, "a", "long enough text", "1", "w", false, null).Result.Error, "no service configured");
        }
        finally { File.Delete(file); }
    }

    [Scenario("attached logs lose the account and computer names, profile paths, addresses, emails, tokens and the person's own settings")]
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

    [Scenario("reports carry the sender's UTC offset, including half hours and the minus side")]
    public static void FeedbackOffset()
    {
        var t = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        Eq("+03:00", Feedback.Offset(t, TimeZoneInfo.FindSystemTimeZoneById("Arab Standard Time")), "GMT+3");
        Eq("-05:00", Feedback.Offset(t, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")), "New York in November");
        Eq("+05:30", Feedback.Offset(t, TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")), "half hours");
        Eq("+00:00", Feedback.Offset(t, TimeZoneInfo.Utc), "UTC");
    }

    [Scenario("paused feedback: the page asks as it opens (once a minute at most), and a send that finds it paused says so; unreachable means open")]
    public static void FeedbackPausedByTheDeveloper()
    {
        var (fb, relay, _, advance, file) = NewFeedback();
        try
        {
            Eq(null, fb.Paused, "not known before asking");
            fb.CheckPausedAsync(Url).Wait();
            Eq(false, fb.Paused, "open");
            Eq("https://relay.example/feedback/status?app=isola", Url + "/status?app=isola", "where it asks");
            relay.Paused = true;
            fb.CheckPausedAsync(Url).Wait();
            Eq(false, fb.Paused, "asked again within the minute: the earlier answer stands");
            Eq(1, relay.StatusAsks, "without asking the service again");
            fb.CheckPausedAsync(Url, force: true).Wait();
            Eq(true, fb.Paused, "Check again asks right away");
            Eq("Feedback is paused for now. Please try again later.", fb.PausedMessage, "with the developer's words");

            // paused after the page opened: sending finds out
            var (fb2, relay2, _, _, _) = NewFeedback();
            fb2.CheckPausedAsync(Url).Wait();
            relay2.Paused = true;
            var r = fb2.SendAsync(Url, FeedbackKind.Bug, "Crash", "It crashed when I opened the shelf.", "1.4.0", "w", false, null).Result;
            Eq("Feedback is paused for now. Please try again later.", r.Error, "the send says so");
            Eq(true, fb2.Paused, "and the page switches to the paused notice");

            // failing open: the service can't be reached, so the form stays usable
            var (fb3, relay3, _, _, _) = NewFeedback();
            relay3.Fail = true;
            fb3.CheckPausedAsync(Url).Wait();
            Eq(false, fb3.Paused, "unreachable: treated as open");
        }
        finally { File.Delete(file); }
    }
}
