using Isola.App;
using Isola.Native;

namespace Isola.Services;

public interface IPowerSource { SYSTEM_POWER_STATUS Read(); }
public sealed class RealPower : IPowerSource { public SYSTEM_POWER_STATUS Read() { Win32.GetSystemPowerStatus(out var s); return s; } }

/// <summary>
/// Laptop battery. Woken by WM_POWERBROADCAST (no polling): reports the charger
/// going in or out and the battery crossing the low threshold.
/// </summary>
public sealed class BatteryService
{
    readonly IPowerSource src;
    public bool Present { get; private set; }
    public int Percent { get; private set; } = -1;
    public bool Charging { get; private set; }
    public bool OnAc { get; private set; }
    /// <summary>Plugged in but not charging: full, or held below full by a charge limit (laptop makers' battery care modes, often 80%).</summary>
    public bool PluggedIn => Present && OnAc && !Charging;
    /// <summary>Seconds of battery left on battery power, or -1 while Windows is still estimating (and on AC).</summary>
    public int SecondsLeft { get; private set; } = -1;
    public event Action<string> Event;   // "plugged", "unplugged", "low", "full"
    bool primed, lowFired;
    public Func<int> LowThreshold = () => 20;

    public BatteryService(IPowerSource s = null) { src = s ?? new RealPower(); }

    public void Refresh()
    {
        var s = src.Read();
        bool present = s.BatteryFlag != 128 && s.BatteryFlag != 255;
        int pct = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : -1;
        bool ac = s.ACLineStatus == 1;
        // Windows' own charging flag. "On AC and under 100%" used to count as
        // charging, which a charge limit makes false: 93% on the charger at a
        // limit charges at 0 mW, and the island still showed it green.
        bool charging = present && (s.BatteryFlag & 8) != 0;
        SecondsLeft = present && !ac && s.BatteryLifeTime > 0 ? s.BatteryLifeTime : -1;
        bool wasAc = OnAc;
        int was = Percent;
        Present = present; Percent = pct; OnAc = ac; Charging = charging;
        if (!primed) { primed = true; lowFired = pct >= 0 && pct <= LowThreshold(); Log.I("battery", $"{pct}% {(ac ? "on AC" : "on battery")}"); return; }
        if (!present) return;
        if (ac != wasAc) { Log.I("battery", ac ? $"charger connected at {pct}%" : $"charger disconnected at {pct}%"); Event?.Invoke(ac ? "plugged" : "unplugged"); }
        if (!ac && pct >= 0 && pct <= LowThreshold() && !lowFired) { lowFired = true; Log.I("battery", $"low: {pct}%"); Event?.Invoke("low"); }
        if (ac || pct > LowThreshold() + 3) lowFired = ac ? lowFired && pct <= LowThreshold() : false;
        if (ac && was < 100 && pct == 100) Event?.Invoke("full");
    }
}
