using Isola.App;
using Isola.Island;
using Isola.Services;
using Isola.Render;

namespace Isola.Tests;

/// <summary>One-time codes in notifications: found, offered to copy, kept private.</summary>
public static partial class Scenarios
{
    [Scenario("one-time codes are found in the ways apps write them")]
    public static void FindsOneTimeCodes()
    {
        foreach (var (app, text, want) in new[]
        {
            ("Google", "G-123456 is your Google verification code.", "123456"),
            ("Messages", "Your WhatsApp code: 482-913. Don't share this code with anyone", "482913"),
            ("Microsoft account", "Use 7391 as Microsoft account security code", "7391"),
            ("Bank", "Your one-time password is 55821094. Valid for 5 minutes.", "55821094"),
            ("Uber", "Your Uber code is 1234. Never share this code.", "1234"),
            ("Instagram", "123 456 is your Instagram code. Don't share it.", "123456"),
            ("Amazon", "Sign-in code: 902187", "902187"),
            ("Discord", "Your Discord login code is 6620 1953", "66201953"),
            ("Steam", "Your verification code is 2024", "2024"),
        })
            Eq(want, OneTimeCode.Find(app, text), $"{app}: \"{text}\"");
        True(OneTimeCode.Find("Your code", "is 381 044") == "381044", "the code can be split across the title and the message");
    }

    [Scenario("numbers that only look like codes are left alone: orders, amounts, phone numbers, times, dates, years, percentages")]
    public static void IgnoresLookalikes()
    {
        foreach (var (app, text) in new[]
        {
            ("Amazon", "Your order #123456 has shipped"),
            ("Amazon", "Order 1234567 is out for delivery. Your verification: sign the slip"),
            ("Bank", "You paid SAR 1500.00 at the store"),
            ("Bank", "Your verification is complete. Balance 25000 SAR"),
            ("Calendar", "Meeting at 10:30 in room 4512"),
            ("Security", "New sign-in on 2026-11-17 from Chrome"),
            ("Messages", "Call me at +1 415 555 0132 about the code"),
            ("Store", "Use code SAVE20 for 20% off, and 1500 points"),
            ("Twitter", "Your PIN was changed at 10:45 PM"),
            ("Fitness", "You walked 10000 steps today"),
            ("Security", "Your password expires in 2027"),
            ("Messages", "Sam: see you at 1930"),
            ("Messages", "Sam: meet me at gate 7741 tomorrow"),
        })
            Eq(null, OneTimeCode.Find(app, text), $"{app}: \"{text}\"");
    }

    [Scenario("a notification with a code gets a Copy button and stays 20 s; with previews off the code isn't shown, only offered")]
    public static void CodeNotificationsOfferCopy()
    {
        string copied = null;
        var a = Alerts.OfferCode(Alerts.Notification("1", "Google", "Verification", "G-123456 is your code", null, 5), "123456", true, c => copied = c);
        Eq(Alerts.CodeSeconds, a.Seconds, "it stays long enough to read and copy");
        Eq(1, a.Actions.Count, "one button");
        Eq(Glyph.Copy, a.Actions[0].Glyph, "a copy button");
        a.Actions[0].Run();
        Eq("123456", copied, "it copies the code, digits only");
        Eq("G-123456 is your code", a.Detail, "the message shows as usual");

        var hidden = Alerts.OfferCode(Alerts.Notification("2", "Google", "Verification", "", null, 5), "123456", false, c => { });
        True(!hidden.Detail.Contains("123"), "previews off: the code isn't on screen");
        Eq("A one-time code is ready to copy", hidden.Detail, "it says a code is there to copy");
        True(hidden.Actions.Count == 1, "but it can still be copied");

        var plain = Alerts.OfferCode(Alerts.Notification("3", "Mail", "Hello", "Lunch?", null, 5), null, true, c => { });
        Eq(0, plain.Actions.Count, "no code, no button");
        Eq(5.0, plain.Seconds, "and the usual time");

        // on the island: copying shows a short "Code copied"
        var (isl, _, clock) = NewIsland();
        isl.Alert(Alerts.OfferCode(Alerts.Notification("4", "Google", "Verification", "G-654321 is your code", null, 5), "654321", true,
            c => { copied = c; isl.Alert(Alerts.Copied()); }));
        clock.Advance(0.3, isl);
        clock.Advance(10, isl);
        Eq("notif:4", isl.CurrentAlert?.Key, "still there after 10 s");
        isl.CurrentAlert.Actions[0].Run(); isl.EndTransient("action");
        clock.Advance(0.3, isl);
        Eq("654321", copied, "copied");
        Eq("copied", isl.CurrentAlert?.Key, "a check confirms it");
    }
}
