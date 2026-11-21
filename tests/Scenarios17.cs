using System.Net;
using System.Net.Sockets;
using Isola.App;
using Isola.Island;
using Isola.Services;

namespace Isola.Tests;

/// <summary>Home Assistant tiles: a change shows at once and holds until Home Assistant reports it.</summary>
public static partial class Scenarios
{
    const string AcAttrs = "\"hvac_modes\":[\"off\",\"cool\",\"dry\",\"fan_only\",\"auto\"],\"temperature\":20,\"target_temp_step\":1,\"current_temperature\":27";

    /// <summary>A Home Assistant that answers (the reachability probe needs something listening) with the fake's states.</summary>
    static (HomeAssistant ha, FakeHa fake, FakeClock clock, TcpListener listener) Ha(params string[] wanted)
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        var fake = new FakeHa();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ha = new HomeAssistant(clock, fake) { BaseUrl = () => $"http://127.0.0.1:{port}", Token = () => "t", Wanted = () => wanted };
        return (ha, fake, clock, listener);
    }

    static void Refresh(HomeAssistant ha, FakeClock clock) { ha.RefreshAsync().Wait(); clock.Advance(0.1); }

    [Scenario("tapping an air conditioner on: the tile stays on until Home Assistant reports it (it went off again, then on), then shows the mode it came on in")]
    public static void AcOnHoldsUntilConfirmed()
    {
        var (ha, fake, clock, listener) = Ha("climate.ac");
        try
        {
            fake.Entity("climate.ac", "off", "Air Conditioner", AcAttrs);
            Refresh(ha, clock);
            ha.Tap(ha.Entities["climate.ac"]); clock.Advance(0.05);
            True(ha.Entities["climate.ac"].On, "on at once");
            // the cloud device takes a while to report: Home Assistant still says off
            for (int k = 0; k < 4; k++) { Refresh(ha, clock); clock.Advance(1.5); True(ha.Entities["climate.ac"].On, $"still shown on after {1.5 * (k + 1):0.0} s (Home Assistant hadn't caught up)"); }
            fake.Entity("climate.ac", "dry", "Air Conditioner", AcAttrs);   // it came back on in its last mode
            Refresh(ha, clock);
            Eq("dry", ha.Entities["climate.ac"].State, "then the mode it really came on in");
            True(!ha.IsPending("climate.ac"), "confirmed");
        }
        finally { listener.Stop(); }
    }

    [Scenario("picking modes: each shows at once and never jumps back to the old one while Home Assistant catches up")]
    public static void AcModeHoldsUntilConfirmed()
    {
        var (ha, fake, clock, listener) = Ha("climate.ac");
        try
        {
            fake.Entity("climate.ac", "cool", "Air Conditioner", AcAttrs);
            Refresh(ha, clock);
            ha.SetHvacMode(ha.Entities["climate.ac"], "dry"); clock.Advance(0.05);
            var seen = new List<string>();
            for (int k = 0; k < 3; k++) { Refresh(ha, clock); seen.Add(ha.Entities["climate.ac"].State); }
            Eq("dry|dry|dry", string.Join("|", seen), "dry the whole time, though Home Assistant still said cool");
            ha.SetHvacMode(ha.Entities["climate.ac"], "fan_only"); clock.Advance(0.05);
            fake.Entity("climate.ac", "dry", "Air Conditioner", AcAttrs);   // the first change lands
            Refresh(ha, clock);
            Eq("fan_only", ha.Entities["climate.ac"].State, "the newer pick still shows over the older one landing");
            fake.Entity("climate.ac", "fan_only", "Air Conditioner", AcAttrs);
            Refresh(ha, clock);
            Eq("fan_only", ha.Entities["climate.ac"].State, "and it's confirmed");
            True(!ha.IsPending("climate.ac"), "nothing left waiting");
            Eq("cool|dry|fan_only|auto", string.Join("|", DashboardPage.PickableModes(ha.Entities["climate.ac"])), "the list leaves out off (the tile itself turns it off)");
            Eq("Fan only", DashboardPage.ModeLabel("fan_only"), "written as a person would");
            Eq("Heat/Cool", DashboardPage.ModeLabel("heat_cool"), "and heat_cool");
        }
        finally { listener.Stop(); }
    }

    [Scenario("a change Home Assistant never reports gives way to the real state after 15 s; a call that fails puts the tile back at once")]
    public static void HaUnconfirmedAndFailed()
    {
        var (ha, fake, clock, listener) = Ha("climate.ac", "light.desk");
        try
        {
            fake.Entity("climate.ac", "off", "Air Conditioner", AcAttrs);
            fake.Entity("light.desk", "off", "Desk", "\"brightness\":0,\"supported_color_modes\":[\"brightness\"]");
            Refresh(ha, clock);
            ha.Tap(ha.Entities["climate.ac"]); clock.Advance(0.05);
            Refresh(ha, clock);
            True(ha.Entities["climate.ac"].On, "on while waiting");
            clock.Advance(HomeAssistant.ConfirmWithin + 1);
            Refresh(ha, clock);
            Eq("off", ha.Entities["climate.ac"].State, "never reported: the real state, off");

            fake.CallsFail = true;
            ha.Tap(ha.Entities["light.desk"]);
            Eq("on", ha.Entities["light.desk"].State, "on at once");
            for (int k = 0; k < 100 && ha.Entities["light.desk"].State == "on"; k++) { Thread.Sleep(10); clock.Advance(0.01); }
            Eq("off", ha.Entities["light.desk"].State, "the call failed: back to off");
        }
        finally { listener.Stop(); }
    }

    [Scenario("a temperature set confirms when Home Assistant reports it in its own number format (20 as 21.0)")]
    public static void HaTemperatureConfirms()
    {
        var (ha, fake, clock, listener) = Ha("climate.ac");
        try
        {
            fake.Entity("climate.ac", "cool", "Air Conditioner", AcAttrs);
            Refresh(ha, clock);
            ha.SetTemperature(ha.Entities["climate.ac"], 21); ha.SetTemperature(ha.Entities["climate.ac"], 22); clock.Advance(0.05);
            Refresh(ha, clock);
            Eq(22.0, ha.Entities["climate.ac"].Num("temperature"), "two quick steps up show 22 while it's on its way");
            fake.Entity("climate.ac", "cool", "Air Conditioner", AcAttrs.Replace("\"temperature\":20", "\"temperature\":22.0"));
            Refresh(ha, clock);
            True(!ha.IsPending("climate.ac"), "22.0 confirms 22");
        }
        finally { listener.Stop(); }
    }

    [Scenario("one-time codes: an Arabic delivery code after a colon is found; a message that only mentions an access code, with numbers far from it, isn't one")]
    public static void CodesNearTheirWords()
    {
        string sms = "عزيزي العميل، سيصلكم موزع البريد السعودي | سبُل لتسليمكم شحنتكم CRDSAL1998721326\nرمز التسليم: 3036";
        Eq("3036", OneTimeCode.Find("+1775", sms), "the delivery code after the colon");
        string wa = "Hi, This is Md Sahin Khan, your Noon Minutes driver for order # ISAGBL022JPQJOGQZA. I'm at the security gate. Could you provide an access code or ask security to let me through so I can deliver your order? Thanks!";
        Eq(null, OneTimeCode.Find("Md Sahin Khan", wa), "no code in it");
        Eq(null, OneTimeCode.Find("+966 55 812 4471", wa + " Gate 2, building 4471 street 1290"), "numbers far from \"access code\" aren't a code");
        Eq("4821", OneTimeCode.Find("Bank", "Your code: 4821"), "a code after a colon");
        Eq("775201", OneTimeCode.Find("Courier", "Delivery PIN: 775201. Share it with the driver."), "and with a PIN");
        Eq("5531", OneTimeCode.Find("Store", "Pickup: 5531"), "a number right after a colon counts even with no word about a code");
        Eq(null, OneTimeCode.Find("Store", "Total: 1500 SAR"), "not an amount after a colon");
        Eq(null, OneTimeCode.Find("Store", "Order number: 88213"), "nor an order number");
        Eq("382914", OneTimeCode.Find("Google", "G-382914 is your Google verification code."), "the usual kind still works");
        Eq("1234", OneTimeCode.Find("Bank", "رمز التحقق الخاص بك هو 1234"), "an Arabic verification code without a colon");
    }

    [Scenario("a text from the phone shows as Messages with the sender's initials (a number: the message glyph), never Phone Link's logo; a relayed app without its icon shows its first letter")]
    public static void TextsAndMissingIcons()
    {
        var sms = Alerts.Text("1", "+1775", "Your parcel is on its way", 10);
        Eq("Messages · Phone Link", sms.Subtitle, "Messages, through Phone Link");
        True(sms.Image == null && sms.Message, "no Phone Link logo");
        Eq(null, Alerts.Monogram(sms), "a number: the message glyph");
        Eq(Render.Glyph.Message, sms.Glyph, "which is a message bubble");
        Eq("SM", Alerts.Monogram(Alerts.Text("2", "Sam Moore", "hi", 10)), "a name: their initials");
        Eq("Y", Alerts.Monogram(Alerts.Notification("3", "YouTube · Phone Link", "Ludwig", "New video", null, 5)), "a relayed app without its icon: its first letter");
        Eq(null, Alerts.Monogram(Alerts.Notification("4", "YouTube · Phone Link", "Ludwig", "New video", new byte[] { 1 }, 5)), "with its icon: the icon");
    }

    [Scenario("a long message scrolls up through the rest within the time it's on screen, after a pause, and stays at the end")]
    public static void LongMessagesScrollInTime()
    {
        float over = 48;   // three lines more than fit
        Eq(0f, Render.Canvas.MarqueeVOffset(over, 1.0, 10), "rests at first");
        float mid = Render.Canvas.MarqueeVOffset(over, 3.5, 10);
        True(mid > 0 && mid < over, $"scrolling ({mid:0.0})");
        Eq(over, Render.Canvas.MarqueeVOffset(over, 9.0, 10), "at the end before the 10 s are up");
        Eq(over, Render.Canvas.MarqueeVOffset(over, 9.9, 10), "and stays there");
        True(Render.Canvas.MarqueeVOffset(400, 4.0, 5) >= 399.9f, "a very long one speeds up to get there in time");
        Eq(0f, Render.Canvas.MarqueeVOffset(0, 5, 10), "one that fits doesn't move");
    }

    [Scenario("the popup takes the message's real line count once it's known (an Arabic text counted as two lines wrapped to three)")]
    public static void PopupUsesMeasuredLines()
    {
        string body = "عزيزي العميل، سيصلكم موزع البريد السعودي | سبُل لتسليمكم شحنتكم CRDSAL1998721326 رمز التسليم: 3036";
        AlertPage.Measured.Remove("0|" + body);
        int guess = AlertPage.DetailLines(body);
        AlertPage.Measured["0|" + body] = 3;
        Eq(3, AlertPage.DetailLines(body), $"measured: three lines (the estimate said {guess})");
        AlertPage.Measured["0|" + body] = 7;
        Eq(3, AlertPage.DetailLines(body), "at most three; the rest scrolls");
        AlertPage.Measured.Remove("0|" + body);
    }
}
