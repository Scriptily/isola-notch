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
        // as a real call arrived: the number first, "Incoming call" under it (1.4.0 missed these)
        var real = FromPhoneLink("+965 5555 0132", "Incoming call");
        True(NotificationFeed.IsPhoneCall(real), "a call from a number: the number, then Incoming call");
        Eq("+965 5555 0132", NotificationFeed.CallerOf(real), "the caller is the number, not the words");
        Eq("Sam", NotificationFeed.CallerOf(FromPhoneLink("Sam", "Incoming call", "Phone Link")), "a saved contact, with a third line");
        Eq("Unknown caller", NotificationFeed.CallerOf(FromPhoneLink("Incoming call")), "no caller line: said so");
        True(NotificationFeed.IsPhoneCall(FromPhoneLink("Incoming call", "Sam")), "Phone Link's call the other way round: Incoming call, then the caller");
        Eq("Sam", NotificationFeed.CallerOf(FromPhoneLink("Incoming call", "Sam")), "and the caller is still Sam");
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
        True(!NotificationFeed.IsPhoneCall(FromPhoneLink("Sam", "Missed call")), "a missed call doesn't ring");
        True(!NotificationFeed.IsPhoneCall(FromPhoneLink("Sam", "Are you free? Incoming call at 5")), "nor a message that mentions one");
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
            Sounds.Warm(Sounds.Low, "sink");
            int built = Sounds.Built;
            Sounds.Play(Sounds.Low, 0.35f, "sink");
            Eq(built, Sounds.Built, "the first play used the warmed one");
            Eq("low:sink", Sounds.Playing, "and it plays");
        }
        finally { Sounds.Output = oldOut; Sounds.Silence = oldSilence; Sounds.Stop(); }
    }

    [Scenario("a sound plays the moment it's asked for when the output is awake (playing, or Isola's last sound was moments ago); only a dozing output gets a lead-in")]
    public static void SoundsStartAtOnceWhenTheOutputIsAwake()
    {
        var (isl, _, clock) = NewIsland();
        var lead = new List<int>();
        var oldOut = Sounds.Output; var oldSilence = Sounds.Silence; var oldBt = Sounds.OutputIsBluetooth; var oldAwake = Sounds.OutputAwake;
        Sounds.Output = (wav, flags) =>
        {
            int bytes = System.Runtime.InteropServices.Marshal.ReadInt32(wav, 40), n = 0;
            while (n < bytes / 2 && System.Runtime.InteropServices.Marshal.ReadInt16(wav, 44 + n * 2) == 0) n++;
            lead.Add(n / 2);   // frames
            return true;
        };
        Sounds.Silence = () => { };
        try
        {
            bool playing = false;
            Sounds.OutputAwake = () => playing;
            Sounds.OutputIsBluetooth = () => true;
            Sounds.Doze();
            // AirPods, idle: the lead-in the link needs to wake
            Sounds.Play(Sounds.Notification, 0.6f, "bubble");
            True(lead[^1] >= Sounds.LeadInBluetooth * Sounds.Rate * 0.95, $"idle Bluetooth: {lead[^1]} frames first");
            // previews one after another: the output is still awake from the last one
            clock.Advance(1.5, isl);
            Sounds.Preview(Sounds.Notification, 0.6f, "pop");
            True(lead[^1] < 100, $"the next preview starts at once ({lead[^1]} frames)");
            clock.Advance(1.0, isl);
            Sounds.Preview(Sounds.Charge, 0.6f, "spark");
            True(lead[^1] < 100, $"and the next ({lead[^1]} frames)");
            // quiet for a while: dozing again
            clock.Advance(Sounds.StaysAwake + 3, isl);
            Sounds.Play(Sounds.Low, 0.6f, "sink");
            True(lead[^1] >= Sounds.LeadInBluetooth * Sounds.Rate * 0.95, $"after a quiet spell: the lead-in again ({lead[^1]} frames)");
            // music playing: a notification over it starts at once
            clock.Advance(30, isl);
            playing = true;
            Sounds.Play(Sounds.Notification, 0.6f, "bubble");
            True(lead[^1] < 100, $"over music: at once ({lead[^1]} frames)");
            // a ringtone that stopped leaves the output awake for a moment too
            playing = false;
            clock.Advance(30, isl);
            Sounds.Play(Sounds.Call, 0.6f, "classic");
            clock.Advance(5, isl);
            Sounds.Stop();
            clock.Advance(1, isl);
            Sounds.Play(Sounds.Notification, 0.6f, "bubble");
            True(lead[^1] < 100, $"just after a ringtone: at once ({lead[^1]} frames)");
            // a broken meter fails open: a lead-in, never an exception or a lost sound
            clock.Advance(30, isl);
            Sounds.OutputAwake = () => throw new InvalidOperationException("meter gone");
            int before = lead.Count;
            Sounds.Play(Sounds.Notification, 0.6f, "pop");
            Eq("notification:pop", Sounds.Playing, "still plays");
            True(lead.Count == before + 1 && lead[^1] >= Sounds.LeadInBluetooth * Sounds.Rate * 0.95, "with the lead-in");
        }
        finally { Sounds.Output = oldOut; Sounds.Silence = oldSilence; Sounds.OutputIsBluetooth = oldBt; Sounds.OutputAwake = oldAwake; Sounds.Stop(); Sounds.Doze(); }
    }

    [Scenario("with Settings open every sound is built ahead, so a preview never waits for the synthesizer; volume and lead-in changes don't rebuild one")]
    public static void PreviewsNeverWaitToBeBuilt()
    {
        var oldOut = Sounds.Output; var oldSilence = Sounds.Silence; var oldBt = Sounds.OutputIsBluetooth;
        try
        {
            Sounds.Output = (wav, flags) => true; Sounds.Silence = () => { };
            Sounds.WarmAll();
            int built = Sounds.Built;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var kind in Sounds.All)
            foreach (var (id, _) in Sounds.Choices(kind))
            {
                Sounds.OutputIsBluetooth = () => id.Length % 2 == 0;   // headphones coming and going
                Sounds.Doze();
                Sounds.Play(kind, 0.37f + id.Length / 100f, id);
            }
            Eq(built, Sounds.Built, "no sound was built again, at any volume or lead-in");
            double each = sw.Elapsed.TotalMilliseconds / Sounds.All.Sum(k => Sounds.Choices(k).Length);
            True(each < 15, $"each one ready to play in {each:0.0} ms");
            // closing Settings lets go of the ones not chosen; the chosen stay built
            Sounds.Keep(new[] { (Sounds.Notification, "bubble") });
            built = Sounds.Built;
            Sounds.Play(Sounds.Notification, 0.6f, "bubble");
            Eq(built, Sounds.Built, "the chosen one is still built");
            Sounds.Play(Sounds.Notification, 0.6f, "bloom");
            Eq(built + 1, Sounds.Built, "one that wasn't is built again when needed");
        }
        finally { Sounds.Output = oldOut; Sounds.Silence = oldSilence; Sounds.OutputIsBluetooth = oldBt; Sounds.Stop(); Sounds.Doze(); }
    }

    [Scenario("the sound volume goes from 50% to 150% with 100% in the middle; 150% never clips; the old 0..1 setting carries over")]
    public static void SoundVolumeIsAMultiplier()
    {
        Eq(1.0, new Config().SoundLevel, "100% by default");
        var c = new Config { SoundLevel = 3 }; c.Normalize(); Eq(1.5, c.SoundLevel, "at most 150%");
        c = new Config { SoundLevel = 0.1 }; c.Normalize(); Eq(0.5, c.SoundLevel, "at least 50%");
        c = new Config { SoundLevel = double.NaN }; c.Normalize(); Eq(1.0, c.SoundLevel, "nonsense: 100%");
        // the setting before: 0.6 (its default) is 100%, louder or quieter carried over within the range
        foreach (var (old, level) in new[] { (0.6, 1.0), (0.75, 1.25), (0.9, 1.5), (1.0, 1.5), (0.45, 0.75), (0.1, 0.5) })
        {
            var o = new Config { SoundVolume = old }; o.Normalize();
            True(Math.Abs(o.SoundLevel - level) < 1e-9, $"an old {old:0.00} becomes {level * 100:0}% ({o.SoundLevel * 100:0.0}%)");
            Eq(null, o.SoundVolume, "and the old setting is gone");
        }
        var json = System.Text.Json.JsonSerializer.Serialize(new Config(), ConfigJson.Default.Config);
        True(!json.Contains("\"SoundVolume\""), "a new config doesn't write the old setting");
        // at 150% the loudest moment of every sound still fits, with room to spare
        float loudest = Isola.App.App.SoundVolumeOf(new Config { SoundLevel = 1.5 });
        foreach (var kind in Sounds.All)
        foreach (var (id, _) in Sounds.Choices(kind))
        {
            var (l, r) = Sounds.Decode(Sounds.Render(kind, loudest, id));
            double peak = Math.Max(l.Max(Math.Abs), r.Max(Math.Abs));
            True(peak < 0.95, $"{kind}/{id} at 150% peaks at {peak:0.000}, under full scale");
        }
        // and the slider's ends are half and one and a half times the middle
        float mid = Isola.App.App.SoundVolumeOf(new Config());
        True(Math.Abs(Isola.App.App.SoundVolumeOf(new Config { SoundLevel = 0.5 }) - mid * 0.5f) < 1e-6, "50% is half the middle");
        True(Math.Abs(Isola.App.App.SoundVolumeOf(new Config { SoundLevel = 1.5 }) - mid * 1.5f) < 1e-6, "150% is one and a half times it");
    }

    [Scenario("Settings names the tab with the sounds Notifications & Sounds, and every tab's name fits beside its icon")]
    public static void SettingsTabNames()
    {
        var names = Isola.App.SettingsWindow.Sections.Select(x => x.name).ToList();
        True(names.Contains("Notifications & Sounds") && !names.Contains("Notifications"), "Notifications & Sounds: " + string.Join(", ", names));
        Eq(names.Count, names.Distinct().Count(), "no two tabs share a name");
        // the sidebar gives a name its row less the icon: the longest has to fit (13 px Segoe UI, about 7 px a character)
        True(names.Max(n => n.Length) * 7 <= 220 - 24 - 50, $"the longest name fits ({names.OrderByDescending(n => n.Length).First()})");
    }
}
