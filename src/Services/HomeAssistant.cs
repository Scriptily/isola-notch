using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Isola.App;

namespace Isola.Services;

public sealed class HaEntity
{
    public string Id = "";
    public string Name = "";
    public string State = "";
    public JsonObject Attr = new();
    public double FetchedAt;
    public string Domain => Id.Split('.')[0];
    public bool On => State is "on" or "open" or "playing" or "home" or "heat" or "cool" or "heat_cool" or "auto" or "dry" or "fan_only" or "unlocked";
    public bool Unavailable => State is "unavailable" or "unknown" && Domain is not ("scene" or "script" or "button" or "input_button");
    public double? Num(string k) => Attr.TryGetPropertyValue(k, out var n) && n is JsonValue v && v.TryGetValue(out double d) ? d : null;
    public string Str(string k) => Attr.TryGetPropertyValue(k, out var n) && n is JsonValue v && v.TryGetValue(out string s) ? s : null;
    public List<string> List(string k) => Attr.TryGetPropertyValue(k, out var n) && n is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList() : new();
    /// <summary>Light brightness 0..1 (HA reports 0..255).</summary>
    public double Brightness => (Num("brightness") ?? (On ? 255 : 0)) / 255.0;
    public bool Dimmable => List("supported_color_modes").Any(m => m is not "onoff") || Attr.ContainsKey("brightness");
    public (byte r, byte g, byte b)? Rgb
    {
        get
        {
            if (Attr.TryGetPropertyValue("rgb_color", out var n) && n is JsonArray a && a.Count == 3)
                return ((byte)a[0].GetValue<double>(), (byte)a[1].GetValue<double>(), (byte)a[2].GetValue<double>());
            return null;
        }
    }
}

public enum HaStatus { NotConfigured, Connecting, Online, Unreachable, Unauthorized, Error }

/// <summary>
/// Home Assistant over its REST API. Treats "not reachable" as an ordinary
/// state - off the home network, HA restarting, a DNS name that only resolves
/// at home - rather than an error: a quick TCP probe decides, requests have
/// short timeouts, and the panel says why it is offline. Polls only while the
/// Home tab is on screen.
/// </summary>
public sealed class HomeAssistant
{
    readonly IClock clock;
    readonly HttpClient http;
    public Func<string> BaseUrl = () => "", Token = () => "";
    public Func<IReadOnlyList<string>> Wanted = () => Array.Empty<string>();
    public HaStatus Status { get; private set; } = HaStatus.NotConfigured;

    /// <summary>
    /// An http:// address that isn't on the local network: the token would cross
    /// the internet unencrypted. Local names and private addresses are fine
    /// (that's how most people reach Home Assistant at home).
    /// </summary>
    public static bool InsecureRemote(string url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp) return false;
        string h = u.Host.ToLowerInvariant();
        if (h is "localhost" || h.EndsWith(".local") || h.EndsWith(".lan") || h.EndsWith(".home") || h.EndsWith(".internal") || !h.Contains('.')) return false;
        if (System.Net.IPAddress.TryParse(h, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return false;
            var b = ip.GetAddressBytes();
            if (b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] >= 64 && b[1] <= 127)) return false;
            if (b.Length == 16 && (ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal)) return false;
        }
        return true;
    }
    /// <summary>When it was last reachable (never: negative infinity).</summary>
    public double LastOnlineAt { get; private set; } = double.NegativeInfinity;

    /// <summary>
    /// Whether the island should offer its Home tab: set up, and reachable or
    /// recently so (a short network blip mustn't make the tab blink away), or
    /// still on its first connection.
    /// </summary>
    public bool Usable => Configured && (Status is HaStatus.Online or HaStatus.Connecting && double.IsNegativeInfinity(LastOnlineAt) || Status == HaStatus.Online || clock.Now - LastOnlineAt < 120);
    public string StatusText { get; private set; } = "Not set up";
    public readonly Dictionary<string, HaEntity> Entities = new();
    public List<HaEntity> All { get; private set; } = new();     // full list, for the settings picker
    public event Action Changed;                                 // UI thread
    bool busy;
    double lastFetch = -1e9;
    public bool Configured => !string.IsNullOrWhiteSpace(BaseUrl()) && !string.IsNullOrWhiteSpace(Token());

    public HomeAssistant(IClock c, HttpMessageHandler handler = null)
    {
        clock = c;
        http = new HttpClient(handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromSeconds(6) };
    }

    void Set(HaStatus s, string text)
    {
        if (s != Status) Log.I("ha", $"{Status} -> {s}: {text}");
        Status = s; StatusText = text;
        if (s == HaStatus.Online) LastOnlineAt = clock.Now;
    }

    /// <summary>TCP connect to the host:port within a short budget.</summary>
    async Task<(bool ok, string why)> Probe(Uri u)
    {
        try
        {
            IPAddress[] addrs;
            try { addrs = await Dns.GetHostAddressesAsync(u.Host).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { return (false, $"can't resolve {u.Host} (not on your home network?)"); }
            using var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(1800);
            await sock.ConnectAsync(addrs, u.Port, cts.Token).ConfigureAwait(false);
            return (true, "");
        }
        catch (OperationCanceledException) { return (false, $"{u.Host} isn't answering"); }
        catch (Exception ex) { return (false, $"{u.Host}: {ex.Message}"); }
    }

    HttpRequestMessage Req(HttpMethod m, string path, string body = null)
    {
        var r = new HttpRequestMessage(m, BaseUrl().TrimEnd('/') + path);
        r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        if (body != null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }

    /// <summary>Refresh the wanted entities (or everything, for the picker).</summary>
    public async Task RefreshAsync(bool all = false)
    {
        if (!Configured) { Ui.Post(() => { Set(HaStatus.NotConfigured, "Add your Home Assistant URL and token in Settings"); Changed?.Invoke(); }); return; }
        if (busy) return;
        busy = true;
        try
        {
            if (!Uri.TryCreate(BaseUrl(), UriKind.Absolute, out var u)) { Ui.Post(() => Set(HaStatus.Error, "The Home Assistant URL isn't valid")); return; }
            if (Status != HaStatus.Online)
            {
                Ui.Post(() => { if (Status != HaStatus.Online) Set(HaStatus.Connecting, "Connecting…"); Changed?.Invoke(); });
                var (ok, why) = await Probe(u).ConfigureAwait(false);
                if (!ok) { Ui.Post(() => { Set(HaStatus.Unreachable, why); Changed?.Invoke(); }); return; }
            }
            var wanted = Wanted();
            List<HaEntity> got;
            if (all || wanted.Count > 12)
            {
                using var resp = await http.SendAsync(Req(HttpMethod.Get, "/api/states")).ConfigureAwait(false);
                if (!await Check(resp).ConfigureAwait(false)) return;
                var arr = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)) as JsonArray;
                got = arr?.Select(Parse).Where(e => e != null).ToList() ?? new();
            }
            else
            {
                got = new();
                foreach (var id in wanted)
                {
                    using var resp = await http.SendAsync(Req(HttpMethod.Get, "/api/states/" + id)).ConfigureAwait(false);
                    if (resp.StatusCode == HttpStatusCode.NotFound) { got.Add(new HaEntity { Id = id, Name = id, State = "missing" }); continue; }
                    if (!await Check(resp).ConfigureAwait(false)) return;
                    var e = Parse(JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)));
                    if (e != null) got.Add(e);
                }
            }
            Ui.Post(() =>
            {
                double now = clock.Now;
                foreach (var e in got) { e.FetchedAt = now; if (!pendingUntil.TryGetValue(e.Id, out var until) || now > until) Entities[e.Id] = e; }
                if (all || wanted.Count > 12) All = got.OrderBy(e => e.Domain).ThenBy(e => e.Name).ToList();
                lastFetch = now;
                Set(HaStatus.Online, $"Connected · {got.Count} entities");
                Changed?.Invoke();
            });
        }
        catch (TaskCanceledException) { Ui.Post(() => { Set(HaStatus.Unreachable, "Home Assistant timed out"); Changed?.Invoke(); }); }
        catch (HttpRequestException ex) { Ui.Post(() => { Set(HaStatus.Unreachable, ex.Message); Changed?.Invoke(); }); }
        catch (Exception ex) { Log.E("ha", "refresh failed", ex); Ui.Post(() => { Set(HaStatus.Error, ex.Message); Changed?.Invoke(); }); }
        finally { busy = false; }
    }

    async Task<bool> Check(HttpResponseMessage r)
    {
        if (r.IsSuccessStatusCode) return true;
        string why = r.StatusCode == HttpStatusCode.Unauthorized ? "The access token was rejected" : $"Home Assistant answered {(int)r.StatusCode}";
        var st = r.StatusCode == HttpStatusCode.Unauthorized ? HaStatus.Unauthorized : HaStatus.Error;
        Ui.Post(() => { Set(st, why); Changed?.Invoke(); });
        await Task.CompletedTask;
        return false;
    }

    static HaEntity Parse(JsonNode n)
    {
        if (n is not JsonObject o) return null;
        var e = new HaEntity { Id = o["entity_id"]?.ToString() ?? "", State = o["state"]?.ToString() ?? "" };
        if (o["attributes"] is JsonObject a) { e.Attr = (JsonObject)a.DeepClone(); e.Name = a["friendly_name"]?.ToString(); }
        if (string.IsNullOrWhiteSpace(e.Name)) e.Name = e.Id;
        return e;
    }

    readonly Dictionary<string, double> pendingUntil = new();
    readonly Dictionary<string, double> calling = new();   // entity -> when its service call went out

    /// <summary>A refresh is in flight.</summary>
    public bool Busy => busy;
    /// <summary>When the service call for this entity went out, or NaN if none is in flight.</summary>
    public double CallingSince(string entityId) => calling.TryGetValue(entityId, out var t) ? t : double.NaN;

    /// <summary>
    /// Call a service. The local state flips immediately (optimistic) so the tile
    /// answers on the same frame; the next refresh, a moment later, confirms it.
    /// </summary>
    public async Task Call(string domain, string service, string entityId, Action<JsonObject> extra = null, Action<HaEntity> optimistic = null)
    {
        if (!Configured) return;
        if (optimistic != null && Entities.TryGetValue(entityId, out var ent)) { optimistic(ent); pendingUntil[entityId] = clock.Now + 2.5; Changed?.Invoke(); }
        calling[entityId] = clock.Now;
        var body = new JsonObject { ["entity_id"] = entityId };
        extra?.Invoke(body);
        string json = body.ToJsonString();
        Log.I("ha", $"call {domain}.{service} {json}");
        try
        {
            using var resp = await http.SendAsync(Req(HttpMethod.Post, $"/api/services/{domain}/{service}", json)).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) Log.W("ha", $"{domain}.{service} -> {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.W("ha", $"{domain}.{service} failed: {ex.Message}");
            Ui.Post(() => { pendingUntil.Remove(entityId); Set(HaStatus.Unreachable, "Couldn't reach Home Assistant"); Changed?.Invoke(); });
            return;
        }
        finally { Ui.Post(() => { calling.Remove(entityId); Changed?.Invoke(); }); }
        await Task.Delay(700).ConfigureAwait(false);
        Ui.Post(() => pendingUntil.Remove(entityId));
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>The obvious "tap" action for an entity.</summary>
    public void Tap(HaEntity e)
    {
        switch (e.Domain)
        {
            case "light": case "switch": case "fan": case "input_boolean": case "humidifier": case "automation": case "siren":
                _ = Call(e.Domain, "toggle", e.Id, null, x => x.State = x.On ? "off" : "on"); break;
            case "scene": _ = Call("scene", "turn_on", e.Id); break;
            case "script": _ = Call("script", "turn_on", e.Id, null, x => x.State = "on"); break;
            case "button": _ = Call("button", "press", e.Id); break;
            case "input_button": _ = Call("input_button", "press", e.Id); break;
            case "cover": _ = Call("cover", "toggle", e.Id); break;
            case "lock": _ = Call("lock", e.State == "locked" ? "unlock" : "lock", e.Id, null, x => x.State = x.State == "locked" ? "unlocked" : "locked"); break;
            case "media_player": _ = Call("media_player", "media_play_pause", e.Id); break;
            case "climate": _ = Call("climate", e.State == "off" ? "turn_on" : "turn_off", e.Id, null, x => x.State = x.State == "off" ? (x.List("hvac_modes").FirstOrDefault(m => m != "off") ?? "on") : "off"); break;
            case "vacuum": _ = Call("vacuum", e.State == "cleaning" ? "return_to_base" : "start", e.Id); break;
            default: _ = Call("homeassistant", "toggle", e.Id, null, x => x.State = x.On ? "off" : "on"); break;
        }
    }

    public void SetBrightness(HaEntity e, double v) =>
        _ = Call("light", "turn_on", e.Id, b => b["brightness_pct"] = (int)Math.Round(Math.Clamp(v, 0.01, 1) * 100), x => { x.State = "on"; x.Attr["brightness"] = (int)(v * 255); });

    public void SetTemperature(HaEntity e, double t) =>
        _ = Call("climate", "set_temperature", e.Id, b => b["temperature"] = t, x => x.Attr["temperature"] = t);

    public void SetHvacMode(HaEntity e, string mode) =>
        _ = Call("climate", "set_hvac_mode", e.Id, b => b["hvac_mode"] = mode, x => x.State = mode);

    public void SetFanPercent(HaEntity e, double p) =>
        _ = Call("fan", "set_percentage", e.Id, b => b["percentage"] = (int)Math.Round(p * 100), x => { x.State = p > 0 ? "on" : "off"; x.Attr["percentage"] = (int)(p * 100); });

    public void SetNumber(HaEntity e, double v) =>
        _ = Call(e.Domain, "set_value", e.Id, b => b["value"] = v, x => x.State = v.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void SelectOption(HaEntity e, string opt) =>
        _ = Call(e.Domain, "select_option", e.Id, b => b["option"] = opt, x => x.State = opt);

    public bool Stale(double seconds) => clock.Now - lastFetch > seconds;

    /// <summary>A weather entity's state, for the weather service.</summary>
    public async Task<HaEntity> GetAsync(string id)
    {
        if (!Configured) return null;
        try
        {
            if (!Uri.TryCreate(BaseUrl(), UriKind.Absolute, out var u)) return null;
            var (ok, _) = await Probe(u).ConfigureAwait(false);
            if (!ok) return null;
            using var resp = await http.SendAsync(Req(HttpMethod.Get, "/api/states" + (string.IsNullOrEmpty(id) ? "" : "/" + id))).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (node is JsonArray arr) return arr.Select(Parse).FirstOrDefault(e => e != null && e.Domain == "weather");
            return Parse(node);
        }
        catch (Exception ex) { Log.D("ha", "get failed: " + ex.Message); return null; }
    }

    /// <summary>weather.get_forecasts (HA 2023.9+), returns the forecast list or null.</summary>
    public async Task<JsonArray> ForecastAsync(string id, string type)
    {
        try
        {
            using var resp = await http.SendAsync(Req(HttpMethod.Post, "/api/services/weather/get_forecasts?return_response",
                new JsonObject { ["entity_id"] = id, ["type"] = type }.ToJsonString())).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var n = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            return n?["service_response"]?[id]?["forecast"] as JsonArray;
        }
        catch { return null; }
    }
}
