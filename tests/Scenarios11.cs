using Isola.App;
using Isola.Island;
using Isola.Services;

namespace Isola.Tests;

/// <summary>Calls: only real ones ring, and a ringtone never outlives its call.</summary>
public static partial class Scenarios
{
    static NotificationItem FromPhoneLink(params string[] lines)
    {
        var it = new NotificationItem { App = "Phone Link", AppId = PhoneLink.Package + "!App" };
        NotificationFeed.Shape(it, lines.ToList(), null);
        return it;
    }

    [Scenario("only Phone Link's own call notification rings: a message that says \"calling\" never does")]
    public static void OnlyRealCallsRing()
    {
        True(NotificationFeed.IsPhoneCall(FromPhoneLink("Incoming call", "Sam")), "Phone Link's call: Incoming call, then the caller");
        True(NotificationFeed.IsPhoneCall(FromPhoneLink("Calling…", "Sam")), "or Calling…");
        True(NotificationFeed.IsPhoneCall(FromPhoneLink("Incoming video call", "Sam")), "a video call too");

        // the one that rang forever: Amazon's message, relayed from the phone by Phone Link
        var amazon = new NotificationItem { App = "Phone Link", AppId = PhoneLink.Package + "!App" };
        NotificationFeed.Shape(amazon, new() { "Amazon", "Your cart is calling", "Items in your cart are selling fast" }, "com.amazon.Amazon");
        True(!NotificationFeed.IsPhoneCall(amazon), "a phone app's message relayed by Phone Link");
        True(!NotificationFeed.IsPhoneCall(FromPhoneLink("Amazon", "Your cart is calling")), "even when the app wasn't recognized");
        // a WhatsApp call relayed from the phone: its line reads "Incoming call", but the
        // island's Answer and Decline drive Phone Link's phone calls, not WhatsApp's
        var whatsapp = new NotificationItem { App = "Phone Link", AppId = PhoneLink.Package + "!App" };
        NotificationFeed.Shape(whatsapp, new() { "WhatsApp", "Incoming call", "Sam" }, "net.whatsapp.WhatsApp");
        Eq("Incoming call", whatsapp.Title, "its title line");
        True(!NotificationFeed.IsPhoneCall(whatsapp), "a phone app's own call, relayed, doesn't ring as a phone call");
        True(!NotificationFeed.IsPhoneCall(FromPhoneLink("Your cart is calling")), "the words inside a sentence");
        True(!NotificationFeed.IsPhoneCall(FromPhoneLink("Mom is calling you about dinner")), "nor a message about a call");
        True(!NotificationFeed.IsPhoneCall(FromPhoneLink("Incoming call from the office was missed")), "nor a longer line that starts the same");
        var teams = new NotificationItem { App = "Microsoft Teams", AppId = "MSTeams_8wekyb3d8bbwe!MSTeams" };
        NotificationFeed.Shape(teams, new() { "Incoming call", "Sam" }, null);
        True(!NotificationFeed.IsPhoneCall(teams), "another app's call notification isn't the phone ringing");
    }

    // the island's sound hooks as the app sets them, with PlaySound replaced by a recorder
    static List<string> RecordSounds(Island.Island isl)
    {
        var played = new List<string>();
        Sounds.Output = (wav, flags) => { played.Add("play"); return true; };
        Sounds.Silence = () => played.Add("silence");
        isl.SoundOn = a => Sounds.Play(a.Sound, 0.6f, null);
        isl.SoundOff = a => { if (a.Sound != null && Sounds.Loops(a.Sound)) Sounds.Stop(); };
        return played;
    }

    [Scenario("a ringtone stops when its call leaves the screen any way at all: opened, hidden, ended while open, or timed out")]
    public static void RingtoneNeverOutlivesItsCall()
    {
        var oldOut = Sounds.Output; var oldSilence = Sounds.Silence;
        try
        {
            // opening the island while it rings
            var (isl, _, clock) = NewIsland();
            RecordSounds(isl);
            isl.Alert(Ring("a", "Mom")); clock.Advance(0.3, isl);
            True(Sounds.Playing?.StartsWith("call:") == true, $"ringing ({Sounds.Playing})");
            isl.Expand(); clock.Advance(0.3, isl);
            Eq(null, Sounds.Playing, "opening the island over the call silences it");
            clock.Advance(90, isl);
            Eq(null, Sounds.Playing, "and nothing starts it again");
            True(isl.CurrentAlert == null, "the call alert is over, not lingering behind the open island");

            // the call ends while the island is open over it
            (isl, _, clock) = NewIsland();
            RecordSounds(isl);
            isl.Alert(Ring("b", "Dad")); clock.Advance(0.3, isl);
            isl.Expand(); clock.Advance(0.3, isl);
            isl.DismissAlert("call:b"); clock.Advance(0.3, isl);
            True(isl.CurrentAlert == null, "an ended call is gone even though it wasn't showing");

            // hidden for a fullscreen app while ringing
            (isl, _, clock) = NewIsland();
            RecordSounds(isl);
            isl.Alert(Ring("c", "Sam")); clock.Advance(0.3, isl);
            isl.SetMode(Mode.Off, "test: fullscreen");
            Eq(null, Sounds.Playing, "hidden: silent");

            // timed out the ordinary way
            (isl, _, clock) = NewIsland();
            RecordSounds(isl);
            isl.Alert(Ring("d", "Sam")); clock.Advance(80, isl);
            Eq(null, Sounds.Playing, "a call that times out stops ringing");
        }
        finally { Sounds.Output = oldOut; Sounds.Silence = oldSilence; Sounds.Stop(); }
    }

    [Scenario("failing safe: a looping sound stops by itself after two minutes, whatever forgot to stop it")]
    public static void LoopingSoundsHaveALimit()
    {
        var oldOut = Sounds.Output; var oldSilence = Sounds.Silence;
        try
        {
            var (isl, _, clock) = NewIsland();
            Sounds.Output = (wav, flags) => true; Sounds.Silence = () => { };
            Sounds.Play(Sounds.Call, 0.6f, "marimba");
            clock.Advance(Sounds.MaxLoopSeconds - 5, isl);
            Eq("call:marimba", Sounds.Playing, "still ringing before the limit");
            clock.Advance(10, isl);
            Eq(null, Sounds.Playing, "stopped at the limit");
            // a newer sound's limit isn't cut short by an older one's
            Sounds.Play(Sounds.Call, 0.6f, "classic");
            clock.Advance(60, isl);
            Sounds.Play(Sounds.Timer, 0.6f, "sweep");
            clock.Advance(Sounds.MaxLoopSeconds - 5, isl);
            Eq("timer:sweep", Sounds.Playing, "the timer has its own full limit");
        }
        finally { Sounds.Output = oldOut; Sounds.Silence = oldSilence; Sounds.Stop(); }
    }

    [Scenario("a sound warmed at startup plays without being built again")]
    public static void WarmedSoundsPlayStraightAway()
    {
        var oldOut = Sounds.Output; var oldSilence = Sounds.Silence;
        try
        {
            Sounds.Output = (wav, flags) => true; Sounds.Silence = () => { };
            Sounds.Warm(Sounds.Low, 0.35f, "sink");
            int built = Sounds.Built;
            Sounds.Play(Sounds.Low, 0.35f, "sink");
            Eq(built, Sounds.Built, "the first play used the warmed one");
            Eq("low:sink", Sounds.Playing, "and it plays");
        }
        finally { Sounds.Output = oldOut; Sounds.Silence = oldSilence; Sounds.Stop(); }
    }
}
