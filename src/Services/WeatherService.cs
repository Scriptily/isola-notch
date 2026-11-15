using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using Isola.App;
using Isola.Render;
using Vortice.Mathematics;

namespace Isola.Services;

public sealed class WeatherNow
{
    public double Temp, High = double.NaN, Low = double.NaN, Humidity = double.NaN, Wind = double.NaN;
    public string Condition = "", Location = "", Source = "", Unit = "°";
    public string Icon = "cloud";
    public Color4 Tint = Col.White;
    public bool Night;
    public List<(string label, double temp, string icon)> Hours = new();
    public DateTime Updated;
}

/// <summary>
/// Weather from your own Home Assistant weather entity when it is reachable (no
/// location leaves the house), otherwise Open-Meteo (free, no key) for the city
/// or coordinates in Settings. Refreshes every 20 minutes, only while running.
/// </summary>
public sealed class WeatherService
{
    readonly IClock clock;
    readonly HomeAssistant ha;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    public WeatherNow Now { get; private set; }
    public string Status { get; private set; } = "Loading…";
    public event Action Changed;
    public Func<Config> Cfg;
    long timer;
    bool busy;

    public WeatherService(IClock c, HomeAssistant h) { clock = c; ha = h; http.DefaultRequestHeaders.UserAgent.ParseAdd("Isola/1.0"); }

    public void Start() { Schedule(2); }
    void Schedule(double s) { Ui.Cancel(ref timer); timer = Ui.After(s, () => _ = RefreshAsync()); }

    /// <summary>A refresh is in flight.</summary>
    public bool Busy => busy;

    public async Task RefreshAsync()
    {
        if (busy) return;
        busy = true;
        var cfg = Cfg();
        WeatherNow w = null; string why = "";
        try
        {
            if (cfg.WeatherSource != "openmeteo" && ha != null && ha.Configured)
            {
                w = await FromHa(cfg).ConfigureAwait(false);
                if (w == null) why = "Home Assistant weather unavailable";
            }
            if (w == null && cfg.WeatherSource != "homeassistant")
            {
                w = await FromOpenMeteo(cfg).ConfigureAwait(false);
                if (w == null && why == "") why = "Set a city in Settings for weather";
            }
        }
        catch (Exception ex) { why = ex.Message; Log.W("weather", "refresh failed: " + ex.Message); }
        finally { busy = false; }
        Ui.Post(() =>
        {
            if (w != null) { Now = w; Status = ""; Log.D("weather", $"{w.Temp:0}{w.Unit} {w.Condition} via {w.Source}"); }
            else { Status = why; Log.D("weather", "no weather: " + why); }
            Changed?.Invoke();
            Schedule(w != null ? 20 * 60 : 5 * 60);
        });
    }

    async Task<WeatherNow> FromHa(Config cfg)
    {
        var e = await ha.GetAsync(string.IsNullOrWhiteSpace(cfg.WeatherEntity) ? null : cfg.WeatherEntity).ConfigureAwait(false);
        if (e == null || e.Domain != "weather" || e.Unavailable) return null;
        var w = new WeatherNow { Source = "Home Assistant", Condition = Pretty(e.State), Location = e.Name, Updated = DateTime.Now };
        w.Temp = e.Num("temperature") ?? double.NaN;
        w.Humidity = e.Num("humidity") ?? double.NaN;
        w.Wind = e.Num("wind_speed") ?? double.NaN;
        w.Unit = e.Str("temperature_unit") ?? "°";
        w.Night = DateTime.Now.Hour is < 6 or >= 19;
        w.Icon = Icon(e.State, w.Night);
        var daily = await ha.ForecastAsync(e.Id, "daily").ConfigureAwait(false);
        if (daily?.Count > 0) { w.High = Get(daily[0], "temperature"); w.Low = Get(daily[0], "templow"); }
        var hourly = await ha.ForecastAsync(e.Id, "hourly").ConfigureAwait(false);
        if (hourly != null)
            foreach (var h in hourly.Take(6))
            {
                var dt = DateTime.TryParse(h?["datetime"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d.ToLocalTime() : DateTime.Now;
                w.Hours.Add((dt.ToString(cfg.Use24Hour ? "HH" : "htt").ToLowerInvariant(), Get(h, "temperature"), Icon(h?["condition"]?.ToString() ?? "", dt.Hour is < 6 or >= 19)));
            }
        if (double.IsNaN(w.Temp)) return null;
        return w;
    }

    static double Get(JsonNode n, string k) => n?[k] is JsonValue v && v.TryGetValue(out double d) ? d : double.NaN;

    async Task<WeatherNow> FromOpenMeteo(Config cfg)
    {
        double lat = cfg.Latitude, lon = cfg.Longitude; string place = cfg.City;
        if ((double.IsNaN(lat) || double.IsNaN(lon)) && !string.IsNullOrWhiteSpace(cfg.City))
        {
            var g = JsonNode.Parse(await http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=1&name=" + Uri.EscapeDataString(cfg.City)).ConfigureAwait(false));
            var r = g?["results"]?[0];
            if (r == null) return null;
            lat = Get(r, "latitude"); lon = Get(r, "longitude"); place = r["name"]?.ToString() ?? cfg.City;
        }
        if (double.IsNaN(lat) || double.IsNaN(lon)) return null;
        string unit = cfg.Fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "";
        string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m,is_day&hourly=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&forecast_days=2&timezone=auto" + unit;
        var j = JsonNode.Parse(await http.GetStringAsync(url).ConfigureAwait(false));
        var cur = j?["current"]; if (cur == null) return null;
        int code = (int)Get(cur, "weather_code");
        bool night = Get(cur, "is_day") == 0;
        var w = new WeatherNow
        {
            Source = "Open-Meteo", Location = place, Temp = Get(cur, "temperature_2m"), Humidity = Get(cur, "relative_humidity_2m"), Wind = Get(cur, "wind_speed_10m"),
            Unit = cfg.Fahrenheit ? "°F" : "°C", Night = night, Condition = WmoText(code), Updated = DateTime.Now,
        };
        w.High = j["daily"]?["temperature_2m_max"]?[0]?.GetValue<double>() ?? double.NaN;
        w.Low = j["daily"]?["temperature_2m_min"]?[0]?.GetValue<double>() ?? double.NaN;
        w.Icon = Icon(WmoCondition(code), night);
        var times = j["hourly"]?["time"] as JsonArray; var temps = j["hourly"]?["temperature_2m"] as JsonArray; var codes = j["hourly"]?["weather_code"] as JsonArray;
        if (times != null && temps != null && codes != null)
        {
            int start = times.Select((t, i) => (t, i)).FirstOrDefault(x => DateTime.Parse(x.t!.ToString(), CultureInfo.InvariantCulture) > DateTime.Now).i;
            for (int i = start; i < Math.Min(times.Count, start + 6); i++)
            {
                var dt = DateTime.Parse(times[i]!.ToString(), CultureInfo.InvariantCulture);
                w.Hours.Add((dt.ToString(cfg.Use24Hour ? "HH" : "htt").ToLowerInvariant(), temps[i]!.GetValue<double>(), Icon(WmoCondition((int)codes[i]!.GetValue<double>()), dt.Hour is < 6 or >= 19)));
            }
        }
        return w;
    }

    static string Pretty(string s) => s switch
    {
        "partlycloudy" => "Partly cloudy", "clear-night" => "Clear", "sunny" => "Sunny", "cloudy" => "Cloudy", "rainy" => "Rain", "pouring" => "Heavy rain",
        "lightning" => "Thunder", "lightning-rainy" => "Thunderstorms", "snowy" => "Snow", "snowy-rainy" => "Sleet", "fog" => "Fog", "hail" => "Hail",
        "windy" or "windy-variant" => "Windy", "exceptional" => "Severe", _ => string.IsNullOrEmpty(s) ? "" : char.ToUpper(s[0]) + s[1..],
    };

    static string WmoCondition(int c) => c switch
    {
        0 => "sunny", 1 or 2 => "partlycloudy", 3 => "cloudy", 45 or 48 => "fog", >= 51 and <= 67 => "rainy", >= 71 and <= 77 => "snowy",
        >= 80 and <= 82 => "pouring", 85 or 86 => "snowy", >= 95 => "lightning-rainy", _ => "cloudy",
    };
    static string WmoText(int c) => Pretty(WmoCondition(c));

    /// <summary>Color emoji for a Home Assistant / WMO condition.</summary>
    /// <summary>
    /// Which icon a condition gets (drawn by Paint.Weather from Fluent glyphs;
    /// the app shows no emojis): sun, moon, partly-day, partly-night, cloud,
    /// rain, pour, thunder, snow, fog, wind, alert.
    /// </summary>
    public static string Icon(string cond, bool night) => cond switch
    {
        "sunny" => night ? "moon" : "sun",
        "clear-night" => "moon",
        "partlycloudy" => night ? "partly-night" : "partly-day",
        "rainy" => "rain",
        "pouring" => "pour",
        "lightning" or "lightning-rainy" => "thunder",
        "snowy" or "snowy-rainy" or "hail" => "snow",
        "fog" => "fog",
        "windy" or "windy-variant" => "wind",
        "exceptional" => "alert",
        _ => "cloud",
    };

}
