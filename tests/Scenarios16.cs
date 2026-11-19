using System.Net;
using Isola.App;
using Isola.Island;
using Isola.Services;

namespace Isola.Tests;

/// <summary>The command palette's web suggestions and answers, against a stand-in network.</summary>
public static partial class Scenarios
{
    /// <summary>Answers by host, as the real services do (bodies captured from them), and logs every request.</summary>
    sealed class FakeWeb : HttpMessageHandler
    {
        public readonly List<string> Asked = new();
        public bool Fail, Garbage;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            string u = req.RequestUri.ToString();
            lock (Asked) Asked.Add(u);
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            string body = Garbage ? "<html>not json</html>" : req.RequestUri.Host switch
            {
                "suggestqueries.google.com" => "[\"subsidized mea\",[\"subsidized meaning\",\"subsidized meaning in hindi\",\"subsidized meals\",\"subsidized mea\"],[],{}]",
                "api.dictionaryapi.dev" when u.Contains("/subsidized") => "[{\"word\":\"subsidized\",\"phonetics\":[],\"meanings\":[{\"partOfSpeech\":\"verb\",\"definitions\":[{\"definition\":\"To assist (someone or something) by granting a subsidy.\"}]},{\"partOfSpeech\":\"adjective\",\"definitions\":[{\"definition\":\"That receives a subsidy\"}]}],\"sourceUrls\":[\"https://en.wiktionary.org/wiki/subsidize\"]}]",
                "api.dictionaryapi.dev" => null,
                "translate.googleapis.com" => "[[[\"مدعومة\",\"subsidized\",null,null,3]],null,\"en\",null]",
                "en.wikipedia.org" when u.Contains("opensearch") && u.Contains("alan") => "[\"alan turing\",[\"Alan Turing\"],[\"\"],[\"https://en.wikipedia.org/wiki/Alan_Turing\"]]",
                "en.wikipedia.org" when u.Contains("opensearch") => "[\"x\",[],[],[]]",
                "en.wikipedia.org" => "{\"type\":\"standard\",\"title\":\"Alan Turing\",\"description\":\"English computer scientist (1912-1954)\",\"extract\":\"Alan Mathison Turing was an English mathematician and computer scientist.\",\"content_urls\":{\"desktop\":{\"page\":\"https://en.wikipedia.org/wiki/Alan_Turing\"}}}",
                _ => null,
            };
            return Task.FromResult(body == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Scenario("web answers: the questions it recognizes, in the ways people type them, and plain searches it leaves alone")]
    public static void WebIntents()
    {
        void Is(string q, string kind, string text, string lang = null)
        {
            var i = WebAnswers.Intent(q);
            Eq($"{kind}|{text}|{lang}", i == null ? "none" : $"{i.Kind}|{i.Text}|{i.Lang}", $"\"{q}\"");
        }
        Is("subsidized meaning", "define", "subsidized");
        Is("Subsidized Meaning?", "define", "Subsidized");
        Is("define subsidized", "define", "subsidized");
        Is("meaning of ad hoc", "define", "ad hoc");
        Is("what does ephemeral mean", "define", "ephemeral");
        Is("serendipity definition", "define", "serendipity");
        Is("translate subsidized to arabic", "translate", "subsidized", "ar");
        Is("translate good morning into Spanish", "translate", "good morning", "es");
        Is("how do you say thank you in japanese", "translate", "thank you", "ja");
        Is("subsidized meaning in hindi", "translate", "subsidized", "hi");
        Is("hello in arabic", "translate", "hello", "ar");
        Is("translate bonjour", "translate", "bonjour", "en");
        Is("who is alan turing", "wiki", "alan turing");
        Is("what is a black hole", "wiki", "black hole");
        Is("tell me about the roman empire", "wiki", "the roman empire");
        foreach (var plain in new[] { "chrome", "spotify settings", "time in tokyo", "notes", "a", "", "budget 2026.xlsx" })
            Eq("none", WebAnswers.Intent(plain) is { } x ? x.Kind : "none", $"\"{plain}\" is just a search");
    }

    [Scenario("web answers never send a file or folder path, a file name or an address with a password")]
    public static void WebNeverSendsPaths()
    {
        foreach (var q in new[] { @"C:\Users\me\taxes", @"\\server\share", "~/notes", "./build", "tax return 2025.pdf", "notes.txt", "https://user:pw@host/x", "x" })
            True(!WebAnswers.Sendable(q), $"\"{q}\" isn't sent");
        foreach (var q in new[] { "subsidized meaning", "weather tomorrow", "github.com", "c sharp records" })
            True(WebAnswers.Sendable(q), $"\"{q}\" may be");
    }

    [Scenario("web answers read definitions, translations and Wikipedia summaries from the services' real replies")]
    public static void WebAnswersParse()
    {
        var web = new WebAnswers(new FakeWeb());
        var (s, a) = web.LookUp("subsidized meaning", default).GetAwaiter().GetResult();
        Eq("define", a?.Kind, "a definition");
        Eq("subsidized", a.Title, "the word");
        Eq("verb, adjective", a.Meta, "what it is");
        True(a.Body.Contains("verb: To assist") && a.Body.Contains("adjective: That receives a subsidy"), "a line per part of speech: " + a.Body);
        Eq("To assist (someone or something) by granting a subsidy.", a.Copy, "Enter copies the first meaning");

        var (_, t) = web.LookUp("translate subsidized to arabic", default).GetAwaiter().GetResult();
        Eq("مدعومة", t?.Title, "the translation");
        Eq("Arabic, from English", t.Meta, "into what, from what");
        True(t.Url.Contains("tl=ar"), "opens Google Translate on it");

        var (_, w) = web.LookUp("who is alan turing", default).GetAwaiter().GetResult();
        Eq("Alan Turing", w?.Title, "the article");
        Eq("English computer scientist (1912-1954)", w.Meta, "its description");
        True(w.Body.StartsWith("Alan Mathison Turing"), "its summary");

        var (sug, _) = web.LookUp("subsidized mea", default).GetAwaiter().GetResult();
        Eq("subsidized meaning|subsidized meaning in hindi|subsidized meals", string.Join("|", sug), "suggestions, without what was typed");
        var (_, none) = web.LookUp("define qwxzy", default).GetAwaiter().GetResult();
        Eq(null, none, "a word the dictionary doesn't know: no card");
    }

    [Scenario("web answers fail quietly: a service down or replying nonsense means no answer, never an error")]
    public static void WebAnswersFailQuietly()
    {
        var down = new FakeWeb { Fail = true };
        var (s, a) = new WebAnswers(down).LookUp("subsidized meaning", default).GetAwaiter().GetResult();
        True(s.Count == 0 && a == null, "down: nothing");
        var junk = new FakeWeb { Garbage = true };
        var (s2, a2) = new WebAnswers(junk).LookUp("who is alan turing", default).GetAwaiter().GetResult();
        True(s2.Count == 0 && a2 == null, "nonsense: nothing");
        foreach (var bad in new[] { "", "[]", "[1]", "{}", "[[],null]", "[[[1]]]", "null", "\"x\"" })
        {
            Eq(0, WebAnswers.ParseSuggestions(bad, "q").Count, "suggestions from " + bad);
            Eq(null, WebAnswers.ParseDefinition(bad, "q"), "definition from " + bad);
            Eq(null, WebAnswers.ParseTranslation(bad, new WebIntent("translate", "q", "ar", "Arabic")), "translation from " + bad);
            Eq(null, WebAnswers.ParseWiki(bad), "summary from " + bad);
        }
        var net = new FakeWeb();
        var web = new WebAnswers(net);
        web.LookUp("subsidized meaning", default).GetAwaiter().GetResult();
        int n = net.Asked.Count;
        web.LookUp("Subsidized meaning ", default).GetAwaiter().GetResult();
        Eq(n, net.Asked.Count, "asked again: answered from memory, nothing sent");
    }

    static (Island.Island isl, FakeClock clock, FakeWeb net) PaletteWithWeb(bool on)
    {
        var (isl, host, clock) = NewIsland(new Config { WebAnswers = on });
        var net = new FakeWeb();
        isl.Svc.Web = new WebAnswers(net);
        isl.OpenSearch(); clock.Advance(0.3, isl);
        return (isl, clock, net);
    }

    static void Settle(FakeClock clock, Island.Island isl)
    {
        // the lookups run on the thread pool: give them real time, the island fake time
        for (int k = 0; k < 100; k++) { Thread.Sleep(5); clock.Advance(0.02, isl); }
    }

    [Scenario("the palette answers a question in place: a definition card on top, Enter copies it and the palette stays open")]
    public static void PaletteAnswersInPlace()
    {
        var (isl, clock, net) = PaletteWithWeb(true);
        isl.P.Search.Type("subsidized meaning");
        clock.Advance(0.05, isl);
        Eq(0, net.Asked.Count, "nothing sent while still typing");
        Settle(clock, isl);
        var first = isl.P.Search.Items.FirstOrDefault();
        Eq("subsidized", first?.Card?.Title, "the definition is the first row");
        Eq("Dictionary", first.Tag, "tagged with where it came from");
        True(isl.TargetHeight > 62 + SearchPage.CardH, "the palette makes room for the card");
        // (Enter itself isn't pressed here: it writes to the real clipboard)
        Eq("Copy|Enter", $"{first.Actions[0].label}|{first.Actions[0].hint}", "Enter copies it");
        True(first.Actions.Any(x => x.label == "Open in browser"), "and it can open the full entry");
    }

    [Scenario("palette suggestions: shown as you type, a question among them answers in place, and an old reply never replaces a newer query")]
    public static void PaletteSuggestions()
    {
        var (isl, clock, net) = PaletteWithWeb(true);
        isl.P.Search.Type("subsidized mea");
        Settle(clock, isl);
        var sug = isl.P.Search.Items.Where(x => x.Tag == "Suggestion").Select(x => x.Title).ToList();
        True(sug.Contains("subsidized meaning") && sug.Contains("subsidized meals"), "suggestions: " + string.Join(", ", sug));
        // typing on: the ones that still fit stay put while the new ones come
        isl.P.Search.Type("subsidized mean");
        var kept = isl.P.Search.Items.Where(x => x.Tag == "Suggestion").Select(x => x.Title).ToList();
        True(kept.Contains("subsidized meaning") && !kept.Contains("subsidized meals"), "kept what still fits: " + string.Join(", ", kept));
        Settle(clock, isl);
        // a suggestion that's a question: Enter answers it right here
        var row = isl.P.Search.Items.First(x => x.Title == "subsidized meaning");
        True(row.KeepsOpen, "a question among the suggestions answers in place");
        row.Run();
        Settle(clock, isl);
        Eq("subsidized", isl.P.Search.Items.FirstOrDefault()?.Card?.Title, "and its answer shows");
        // typed fast: only the last query's answers land
        isl.P.Search.Type("who is alan turing");
        isl.P.Search.Type("hello in arabic");
        Settle(clock, isl);
        Eq("مدعومة", isl.P.Search.Items.FirstOrDefault()?.Card?.Title, "the newest query's answer, not the one before");
    }

    [Scenario("with web answers off, nothing is sent at all; a question offers to turn them on")]
    public static void PaletteWebOff()
    {
        var (isl, clock, net) = PaletteWithWeb(false);
        foreach (var q in new[] { "subsidized meaning", "spotify", "translate hi to arabic" }) { isl.P.Search.Type(q); Settle(clock, isl); }
        Eq(0, net.Asked.Count, "not one request");
        True(isl.P.Search.Items.Any(x => x.Title == "Answer this right here"), "how to get it answered here");
        var (isl2, clock2, net2) = PaletteWithWeb(true);
        isl2.P.Search.Type(@"C:\Users\me\Documents\taxes"); Settle(clock2, isl2);
        isl2.P.Search.Type("12*7"); Settle(clock2, isl2);
        Eq(0, net2.Asked.Count, "on, but a path or a sum: still nothing sent");
    }

    static (Island.Island isl, FakeHost host, FakeClock clock) Typing(string q)
    {
        var (isl, host, clock) = NewIsland();
        host.Fg = 7;   // the app that was in front before search
        isl.OpenSearch(); clock.Advance(0.3, isl);
        isl.P.Search.Type(q); clock.Advance(0.3, isl);
        return (isl, host, clock);
    }

    [Scenario("a notification while typing in search waits until it closes (nothing covers the field), then shows")]
    public static void NoticesWaitForSearch()
    {
        var (isl, host, clock) = Typing("budget");
        isl.Alert(new Alert { Key = "n1", Kind = "notification", Title = "Sam", Subtitle = "WhatsApp", Detail = "hi", Seconds = 3 });
        isl.Alert(new Alert { Key = "full", Kind = "battery", Title = "Fully charged", Seconds = 3 });
        clock.Advance(1, isl);
        Eq(Mode.Search, isl.Mode, "search stays open");
        Eq(null, isl.InlineAlert, "no banner over the field");
        Eq("budget", isl.P.Search.Query, "what was typed is untouched");
        Eq(2, isl.QueuedAlerts.Count, "both waiting (the bar says so)");
        Eq(2, isl.History.Count, "and already in Recent");
        isl.CloseToRest("search escape"); clock.Advance(0.5, isl);
        Eq(Mode.Alert, isl.Mode, "closed: they show");
        Eq("n1", isl.CurrentAlert?.Key, "in order");
    }

    [Scenario("a call while typing takes over, and when it ends search comes back with what was typed")]
    public static void CallInterruptsSearchAndReturns()
    {
        var (isl, host, clock) = Typing("translate hello to");
        isl.Alert(new Alert { Key = "call", Kind = "call", Title = "Mom", Priority = 3, Sticky = true, MaxSeconds = 60 });
        clock.Advance(0.5, isl);
        Eq(Mode.Alert, isl.Mode, "the call comes first");
        isl.DismissAlert("call"); clock.Advance(0.5, isl);
        Eq(Mode.Search, isl.Mode, "then back to search");
        Eq("translate hello to", isl.P.Search.Query, "with the text as it was");
        True(isl.KeyboardTaken, "ready to keep typing");

        // a timer going off: the same
        isl.Alert(new Alert { Key = "timer", Kind = "timer", Title = "Timer done", Priority = 2, Seconds = 2 });
        clock.Advance(3, isl);
        Eq(Mode.Search, isl.Mode, "after the timer too");
        Eq("translate hello to", isl.P.Search.Query, "text kept");
    }

    [Scenario("moved on to another app during the call: search isn't pulled back, but opening it again soon brings the text back")]
    public static void CallThenSwitchedAppKeepsTextForLater()
    {
        var (isl, host, clock) = Typing("quarterly report");
        isl.Alert(new Alert { Key = "call", Kind = "call", Title = "Mom", Priority = 3, Sticky = true, MaxSeconds = 60 });
        clock.Advance(0.5, isl);
        host.Fg = 9;   // answered it in Phone Link
        isl.DismissAlert("call"); clock.Advance(0.5, isl);
        Eq(Mode.Rest, isl.Mode, "not dragged back over the other app");
        clock.Advance(20, isl);
        isl.OpenSearch(); clock.Advance(0.3, isl);
        Eq("quarterly report", isl.P.Search.Query, "opened again: the search is still there");
        isl.CloseToRest("x"); clock.Advance(1, isl);
        isl.OpenSearch(); clock.Advance(0.3, isl);
        Eq("", isl.P.Search.Query, "once, then it starts fresh");
    }

    [Scenario("web answers don't mistake questions about translating for phrases to translate")]
    public static void WebIntentsAboutTranslating()
    {
        foreach (var q in new[] { "how to translate english to arabic", "how to translate in arabic", "learn to speak in spanish", "what is hello in french" })
            True(WebAnswers.Intent(q)?.Kind != "translate", $"\"{q}\" isn't a phrase to translate");
        Eq("subsidies|ar", WebAnswers.Intent("translate subsidies from english to arabic") is { } i ? i.Text + "|" + i.Lang : "none", "\"from English\" is the source, not part of the phrase");
        Eq("good night|es", WebAnswers.Intent("good night in spanish") is { } j ? j.Text + "|" + j.Lang : "none", "a short phrase in a language still is");
    }

    [Scenario("a caller without a picture shows their initials, as the phone does; a number shows the phone")]
    public static void CallerInitials()
    {
        Eq("SM", Isola.App.Alerts.Initials("Sam Moore"), "first and last");
        Eq("M", Isola.App.Alerts.Initials("Mom"), "one name");
        Eq("AL", Isola.App.Alerts.Initials("Dr. Ana Lee"), "titles skipped");
        Eq("JS", Isola.App.Alerts.Initials("john michael smith"), "first and last, capitalized");
        Eq("أح", Isola.App.Alerts.Initials("أحمد حسن") is { } ar ? ar[0] + "" + ar[1] : null, "any alphabet");
        Eq(null, Isola.App.Alerts.Initials("+1 415 555 0132"), "a number: the phone glyph");
        Eq(null, Isola.App.Alerts.Initials("  "), "nothing");
    }
}
