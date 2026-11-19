using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using Isola.App;
using Isola.Render;
using Vortice.Mathematics;

namespace Isola.Services;

/// <summary>One hour or one day of the forecast, with everything the source gives for it.</summary>
public sealed class Forecast
{
    public DateTime At;
    public string Label = "", Condition = "", Icon = "cloud";
    /// <summary>The hour's temperature, or the day's high.</summary>
    public double Temp = double.NaN;
    /// <summary>The day's low (NaN for an hour).</summary>
    public double Low = double.NaN;
    public double FeelsLike = double.NaN, Precip = double.NaN, PrecipAmount = double.NaN, Humidity = double.NaN, Wind = double.NaN, Uv = double.NaN;
    public bool Night;
}

public sealed class WeatherNow
{
    public double Temp, High = double.NaN, Low = double.NaN, Humidity = double.NaN, Wind = double.NaN;
    public double FeelsLike = double.NaN, Precip = double.NaN, Uv = double.NaN;
    public string Condition = "", Location = "", Source = "", Unit = "°", WindUnit = "", PrecipUnit = "mm";
    public string Icon = "cloud";
    public Color4 Tint = Col.White;
    public bool Night;
    public DateTime? Sunrise, Sunset;
    public List<Forecast> Hourly = new(), Daily = new();
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
        w.WindUnit = e.Str("wind_speed_unit") ?? "";
        w.PrecipUnit = e.Str("precipitation_unit") ?? "mm";
        w.FeelsLike = e.Num("apparent_temperature") ?? double.NaN;
        w.Uv = e.Num("uv_index") ?? double.NaN;
        // day and night from the sun's real times (it was before 6 and after 7 all year)
        try
        {
            var sun = await ha.GetAsync("sun.sun").ConfigureAwait(false);
            w.Sunrise = Local(sun?.Str("next_rising")); w.Sunset = Local(sun?.Str("next_setting"));
        }
        catch { }
        var night = NightFrom(w.Sunrise, w.Sunset);
        w.Night = night(DateTime.Now);
        w.Icon = Icon(e.State, w.Night);
        var daily = await ha.ForecastAsync(e.Id, "daily").ConfigureAwait(false);
        if (daily != null) w.Daily = FromHaForecast(daily, true, night, cfg.Use24Hour, DateTime.Now);
        if (w.Daily.Count > 0) { w.High = w.Daily[0].Temp; w.Low = w.Daily[0].Low; w.Precip = w.Daily[0].Precip; }
        var hourly = await ha.ForecastAsync(e.Id, "hourly").ConfigureAwait(false);
        if (hourly != null) w.Hourly = FromHaForecast(hourly, false, night, cfg.Use24Hour, DateTime.Now);
        if (double.IsNaN(w.Temp)) return null;
        return w;
    }

    static double Get(JsonNode n, string k) => n?[k] is JsonValue v && v.TryGetValue(out double d) ? d : double.NaN;
    static DateTime? Local(string s) => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d.ToLocalTime() : null;

    /// <summary>Night by the sun's times of day when known (today's sunrise to sunset is day), else the old guess.</summary>
    internal static Func<DateTime, bool> NightFrom(DateTime? rise, DateTime? set)
    {
        if (rise is DateTime r && set is DateTime s && r.TimeOfDay != s.TimeOfDay)
            return t => !(t.TimeOfDay >= r.TimeOfDay && t.TimeOfDay < s.TimeOfDay);
        return t => t.Hour is < 6 or >= 19;
    }

    internal static string HourLabel(DateTime t, bool h24) => t.ToString(h24 ? "HH:00" : "htt", CultureInfo.InvariantCulture).ToLowerInvariant();
    internal static string DayLabel(DateTime t, DateTime now) => t.Date == now.Date ? "Today" : t.ToString("ddd", CultureInfo.CurrentCulture);

    /// <summary>Home Assistant's forecast (hourly or daily) with every field it carries; the next 12 hours or 7 days.</summary>
    internal static List<Forecast> FromHaForecast(JsonArray arr, bool daily, Func<DateTime, bool> night, bool h24, DateTime now)
    {
        var list = new List<Forecast>();
        foreach (var h in arr)
        {
            if (h == null) continue;
            var dt = Local(h["datetime"]?.ToString()) ?? now;
            if (!daily && dt < now.AddMinutes(-59)) continue;   // an hour already gone
            string cond = h["condition"]?.ToString() ?? "";
            bool isNight = !daily && (h["is_daytime"] is JsonValue dv && dv.TryGetValue(out bool day) ? !day : night(dt));
            list.Add(new Forecast
            {
                At = dt, Label = daily ? DayLabel(dt, now) : HourLabel(dt, h24), Condition = Pretty(cond), Night = isNight, Icon = Icon(cond, isNight),
                Temp = Get(h, "temperature"), Low = daily ? Get(h, "templow") : double.NaN, FeelsLike = Get(h, "apparent_temperature"),
                Precip = Get(h, "precipitation_probability"), PrecipAmount = Get(h, "precipitation"), Humidity = Get(h, "humidity"),
                Wind = Get(h, "wind_speed"), Uv = Get(h, "uv_index"),
            });
            if (list.Count >= (daily ? 7 : 12)) break;
        }
        return list;
    }

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
        string unit = cfg.Fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph&precipitation_unit=inch" : "";
        string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m,is_day,apparent_temperature" +
            "&hourly=temperature_2m,weather_code,is_day,precipitation_probability,precipitation,relative_humidity_2m,wind_speed_10m,apparent_temperature,uv_index" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum,wind_speed_10m_max,uv_index_max,sunrise,sunset" +
            "&forecast_days=7&timezone=auto" + unit;
        var j = JsonNode.Parse(await http.GetStringAsync(url).ConfigureAwait(false));
        return ParseOpenMeteo(j, place, cfg.Fahrenheit, cfg.Use24Hour, DateTime.Now);
    }

    /// <summary>An Open-Meteo forecast: now, the next 12 hours (day or night by its own flag), the next 7 days.</summary>
    internal static WeatherNow ParseOpenMeteo(JsonNode j, string place, bool fahrenheit, bool h24, DateTime now)
    {
        var cur = j?["current"]; if (cur == null) return null;
        int code = (int)Get(cur, "weather_code");
        bool night = Get(cur, "is_day") == 0;
        var w = new WeatherNow
        {
            Source = "Open-Meteo", Location = place, Temp = Get(cur, "temperature_2m"), Humidity = Get(cur, "relative_humidity_2m"), Wind = Get(cur, "wind_speed_10m"),
            FeelsLike = Get(cur, "apparent_temperature"), Unit = fahrenheit ? "°F" : "°C", WindUnit = fahrenheit ? "mph" : "km/h", PrecipUnit = fahrenheit ? "in" : "mm",
            Night = night, Condition = WmoText(code), Updated = now,
        };
        w.Icon = Icon(WmoCondition(code), night);
        double At(JsonNode block, string k, int i) => block?[k] is JsonArray a && i < a.Count && a[i] is JsonValue v && v.TryGetValue(out double d) ? d : double.NaN;
        var hourly = j["hourly"]; var times = hourly?["time"] as JsonArray;
        if (times != null)
            for (int i = 0; i < times.Count && w.Hourly.Count < 12; i++)
            {
                var dt = DateTime.Parse(times[i]!.ToString(), CultureInfo.InvariantCulture);
                if (dt < now.AddMinutes(-59)) continue;
                int c = (int)At(hourly, "weather_code", i);
                bool n = At(hourly, "is_day", i) == 0;
                w.Hourly.Add(new Forecast
                {
                    At = dt, Label = HourLabel(dt, h24), Condition = WmoText(c), Night = n, Icon = Icon(WmoCondition(c), n),
                    Temp = At(hourly, "temperature_2m", i), FeelsLike = At(hourly, "apparent_temperature", i), Precip = At(hourly, "precipitation_probability", i),
                    PrecipAmount = At(hourly, "precipitation", i), Humidity = At(hourly, "relative_humidity_2m", i), Wind = At(hourly, "wind_speed_10m", i), Uv = At(hourly, "uv_index", i),
                });
            }
        var daily = j["daily"]; var days = daily?["time"] as JsonArray;
        if (days != null)
            for (int i = 0; i < days.Count && i < 7; i++)
            {
                var dt = DateTime.Parse(days[i]!.ToString(), CultureInfo.InvariantCulture);
                int c = (int)At(daily, "weather_code", i);
                w.Daily.Add(new Forecast
                {
                    At = dt, Label = DayLabel(dt, now), Condition = WmoText(c), Icon = Icon(WmoCondition(c), false),
                    Temp = At(daily, "temperature_2m_max", i), Low = At(daily, "temperature_2m_min", i), Precip = At(daily, "precipitation_probability_max", i),
                    PrecipAmount = At(daily, "precipitation_sum", i), Wind = At(daily, "wind_speed_10m_max", i), Uv = At(daily, "uv_index_max", i),
                });
            }
        if (w.Daily.Count > 0) { w.High = w.Daily[0].Temp; w.Low = w.Daily[0].Low; w.Precip = w.Daily[0].Precip; w.Uv = w.Daily[0].Uv; }
        if (daily?["sunrise"] is JsonArray sr && sr.Count > 0 && DateTime.TryParse(sr[0]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var rise)) w.Sunrise = rise;
        if (daily?["sunset"] is JsonArray ss && ss.Count > 0 && DateTime.TryParse(ss[0]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var set)) w.Sunset = set;
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
