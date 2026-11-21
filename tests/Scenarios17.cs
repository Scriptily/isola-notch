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
}
