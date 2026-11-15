using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Isola.App;

namespace Isola.Services;

/// <summary>
/// Spotify's own volume - the slider inside the Spotify app - through Spotify's
/// Web API. Nothing in Windows reaches it: the mixer's Spotify slider is a
/// separate volume applied after it, the media-session API has no volume at
/// all, and the Spotify window shows UI Automation nothing but its frame while
/// it's minimized, which is most of the time. The Web
/// API works whatever the window is doing. It needs Premium and a one-time
/// sign-in; the sign-in is PKCE, so there is no client secret to keep, only the
/// client ID of the user's own Spotify developer app and a refresh token, kept
/// in its own file in the data folder (never in the config the settings window
/// edits, so an Apply can't overwrite it, and never in the repository).
/// </summary>
public sealed class SpotifyService
{
    public const int Port = 4381;
    public static readonly string RedirectUri = $"http://127.0.0.1:{Port}/callback";
    const string Scopes = "user-read-playback-state user-modify-playback-state";
    const string Accounts = "https://accounts.spotify.com", Api = "https://api.spotify.com/v1";

    readonly IClock clock;
    readonly HttpClient http;
    public Func<string> ClientId = () => "";
    public string TokenFile;
    /// <summary>Opens the sign-in page; tests replace it.</summary>
    internal Action<string> OpenBrowser = url => Native.Win32.ShellExecute(url);

    string refresh, access;
    double accessUntil = double.NegativeInfinity;
    double lastPoll = double.NegativeInfinity, quietUntil = double.NegativeInfinity, backoffUntil = double.NegativeInfinity;
    bool polling;

    public bool Connected => !string.IsNullOrEmpty(refresh);
    /// <summary>Spotify ended the sign-in (six months up, or access revoked); connect again.</summary>
    public bool Expired { get; private set; }
    public string AccountName { get; private set; } = "";
    /// <summary>When you signed in. Spotify's refresh tokens carry no date, and its six-month limit counts from here.</summary>
    public DateTime? AuthorizedAt { get; private set; }
    /// <summary>Spotify requires signing in again six months after authorizing; refreshing doesn't extend it.</summary>
    public DateTime? ReauthBy => AuthorizedAt?.AddMonths(6);
    public int? ReauthDaysLeft => Connected && ReauthBy is DateTime by ? (int)Math.Ceiling((by - Wall()).TotalDays) : null;
    internal Func<DateTime> Wall = () => DateTime.Now;
    public bool Connecting { get; private set; }
    public string Status { get; private set; } = "Not connected";
    /// <summary>The active device's volume, 0-100, or null when nothing is playing anywhere.</summary>
    public int? Volume { get; private set; }
    public string DeviceName { get; private set; } = "";
    public bool SupportsVolume { get; private set; } = true;
    /// <summary>The first read since connecting or opening the panel is still out.</summary>
    public bool Loading => Connected && polling && Volume == null;
    public event Action Changed;

    public SpotifyService(IClock c, HttpMessageHandler handler = null)
    {
        clock = c;
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(10);
    }

    void Raise() => Ui.Post(() => Changed?.Invoke());
    void Set(string s) { Status = s; Raise(); }

    // ---- the refresh token, in its own file --------------------------------------

    public void Load()
    {
        try
        {
            if (TokenFile != null && File.Exists(TokenFile))
            {
                var js = JsonNode.Parse(File.ReadAllText(TokenFile));
                refresh = Secret.Unprotect(js?["refresh_token"]?.ToString());   // stored encrypted (see Secret)
                if (refresh?.Length == 0) refresh = null;
                AccountName = js?["account"]?.ToString() ?? "";
                Expired = js?["expired"]?.GetValue<bool>() == true;
                if (DateTime.TryParse(js?["authorized_at"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) AuthorizedAt = at.ToLocalTime();
            }
        }
        catch (Exception ex) { Log.W("spotify", "couldn't read the saved sign-in: " + ex.Message); refresh = null; }
        Status = Connected ? "Connected" : Expired ? ExpiredText() : "Not connected";
    }

    string ExpiredText() => AuthorizedAt is DateTime at && Wall() >= at.AddMonths(6).AddDays(-1)
        ? "Spotify's six-month sign-in ran out; sign in again"
        : "Spotify signed the island out; sign in again";

    void Save()
    {
        if (TokenFile == null) return;
        try
        {
            var js = new JsonObject();
            if (!string.IsNullOrEmpty(refresh)) js["refresh_token"] = Secret.Protect(refresh);
            if (AuthorizedAt is DateTime at) js["authorized_at"] = at.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            if (AccountName.Length > 0) js["account"] = AccountName;
            if (Expired) js["expired"] = true;
            string tmp = TokenFile + ".tmp";
            File.WriteAllText(tmp, js.ToJsonString());
            File.Move(tmp, TokenFile, true);
        }
        catch (Exception ex) { Log.W("spotify", "couldn't save the sign-in: " + ex.Message); }
    }

    void SaveRefresh(string token) { refresh = token; Save(); }

    public void Disconnect()
    {
        refresh = access = null; accessUntil = double.NegativeInfinity; Volume = null; DeviceName = "";
        Expired = false; AuthorizedAt = null; AccountName = "";
        try { if (TokenFile != null) File.Delete(TokenFile); } catch { }
        Log.I("spotify", "disconnected");
        Set("Not connected");
    }

    /// <summary>Spotify refused the refresh token: forget it (Spotify says retrying can't help) but remember why, so the island can say so.</summary>
    void Expire()
    {
        refresh = access = null; accessUntil = double.NegativeInfinity; Volume = null; DeviceName = "";
        Expired = true;
        Save();
        Log.W("spotify", "the sign-in was refused (six months up, or revoked); asking to sign in again");
        Set(ExpiredText());
    }

    // ---- sign-in: authorization code with PKCE, redirected to a loopback port ----

    /// <summary>RFC 7636's S256: base64url(SHA-256(verifier)), no padding.</summary>
    internal static string Challenge(string verifier) => B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Random(int bytes) => B64Url(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>The query of "GET /callback?code=..&amp;state=.. HTTP/1.1"; null for any other request (a favicon, say).</summary>
    internal static (string code, string state, string error)? ParseCallback(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET" || !parts[1].StartsWith("/callback")) return null;
        int q = parts[1].IndexOf('?');
        string code = null, state = null, error = null;
        if (q >= 0)
            foreach (var kv in parts[1][(q + 1)..].Split('&'))
            {
                int e = kv.IndexOf('=');
                if (e < 0) continue;
                string k = kv[..e], v = Uri.UnescapeDataString(kv[(e + 1)..].Replace('+', ' '));
                if (k == "code") code = v; else if (k == "state") state = v; else if (k == "error") error = v;
            }
        return (code, state, error);
    }

    public async Task ConnectAsync()
    {
        string id = (ClientId() ?? "").Trim();
        if (id.Length == 0) { Set("Paste your app's Client ID first"); return; }
        if (Connecting) return;
        Connecting = true; Set("Waiting for you to sign in in the browser…");
        TcpListener listener = null;
        try
        {
            string verifier = Random(48), state = Random(16);
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            OpenBrowser($"{Accounts}/authorize?client_id={Uri.EscapeDataString(id)}&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                        $"&code_challenge_method=S256&code_challenge={Challenge(verifier)}&scope={Uri.EscapeDataString(Scopes)}&state={state}");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var (code, gotState, error) = await AwaitCallback(listener, cts.Token).ConfigureAwait(false);
            if (error != null) { Set(error == "access_denied" ? "Sign-in was canceled" : "Spotify said: " + error); return; }
            if (gotState != state || string.IsNullOrEmpty(code)) { Set("The sign-in reply didn't match; try again"); return; }
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
                ["client_id"] = id, ["code_verifier"] = verifier,
            });
            using var resp = await http.PostAsync($"{Accounts}/api/token", body).ConfigureAwait(false);
            var js = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!resp.IsSuccessStatusCode) { Set("Spotify refused the sign-in: " + (js?["error_description"] ?? js?["error"])); return; }
            Expired = false; AuthorizedAt = Wall();   // a fresh authorization starts a new six months
            TakeTokens(js);
            await FetchAccountAsync().ConfigureAwait(false);
            Save();
            Log.I("spotify", "connected" + (AccountName.Length > 0 ? " as " + AccountName : ""));
            Set("Connected");
            lastPoll = double.NegativeInfinity;
            await PollAsync(true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Set("Sign-in timed out; try again"); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) { Set($"Port {Port} is busy; close whatever uses it and try again"); }
        catch (Exception ex) { Log.W("spotify", "sign-in failed: " + ex.Message); Set("Sign-in failed: " + ex.Message); }
        finally { try { listener?.Stop(); } catch { } Connecting = false; Raise(); }
    }

    static async Task<(string code, string state, string error)> AwaitCallback(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var s = client.GetStream();
            var buf = new byte[8192];
            int n = await s.ReadAsync(buf, ct).ConfigureAwait(false);
            string line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];
            var r = ParseCallback(line);
            string html = r == null ? "" :
                "<!doctype html><meta charset=utf-8><title>Isola</title><body style=\"font:16px Segoe UI,sans-serif;background:#111;color:#eee;display:grid;place-items:center;height:90vh\">" +
                (r.Value.error == null ? "<p>Spotify is connected to Isola. You can close this tab.</p>" : "<p>Sign-in didn't complete. You can close this tab and try again from the island's settings.</p>");
            string head = r == null ? "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                                    : $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(html)}\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.UTF8.GetBytes(head + html), ct).ConfigureAwait(false);
            if (r != null) return r.Value;
        }
    }

    void TakeTokens(JsonNode js)
    {
        access = js?["access_token"]?.ToString();
        double secs = js?["expires_in"]?.GetValue<double>() ?? 3600;
        accessUntil = clock.Now + secs - 300;   // renew five minutes early, so a call never meets a just-expired token
        string r = js?["refresh_token"]?.ToString();
        if (!string.IsNullOrEmpty(r) && r != refresh) SaveRefresh(r);   // Spotify may rotate it
    }

    async Task<bool> EnsureAccessAsync(bool force = false)
    {
        if (!Connected) return false;
        if (!force && access != null && clock.Now < accessUntil) return true;
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = (ClientId() ?? "").Trim(),
        });
        using var resp = await http.PostAsync($"{Accounts}/api/token", body).ConfigureAwait(false);
        var js = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (resp.StatusCode == HttpStatusCode.BadRequest && js?["error"]?.ToString() == "invalid_grant")
        {
            // Six months since signing in (Spotify's limit since June 2026), or
            // access revoked. Spotify says not to retry: sign in again.
            Ui.Post(Expire);
            return false;
        }
        if (!resp.IsSuccessStatusCode)
        {
            // say which refusal it was: invalid_client means the Client ID in
            // Settings isn't the app's (it once went missing from the config)
            string err = js?["error"]?.ToString() ?? "";
            Log.W("spotify", $"token refresh -> {(int)resp.StatusCode} {err}");
            Set(err == "invalid_client" ? "Spotify doesn't recognize the Client ID in Settings › Spotify" : $"Spotify wouldn't renew access ({(err.Length > 0 ? err : ((int)resp.StatusCode).ToString())})");
            return false;
        }
        TakeTokens(js);
        return true;
    }

    /// <summary>A Web API call with the access token, refreshing it once on 401.</summary>
    async Task<HttpResponseMessage> Call(HttpMethod method, string path)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!await EnsureAccessAsync(attempt > 0).ConfigureAwait(false)) return null;
            var req = new HttpRequestMessage(method, Api + path);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            if (method == HttpMethod.Put) req.Content = new ByteArrayContent(Array.Empty<byte>());
            var resp = await http.SendAsync(req).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { resp.Dispose(); continue; }
            if ((int)resp.StatusCode == 429)
            {
                double wait = resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5;
                backoffUntil = clock.Now + Math.Clamp(wait, 1, 120);
                Log.W("spotify", $"rate limited for {wait:0} s");
            }
            return resp;
        }
        return null;
    }

    /// <summary>The account's display name, for "Signed in as". Needs no extra scope.</summary>
    async Task FetchAccountAsync()
    {
        try
        {
            using var resp = await Call(HttpMethod.Get, "/me").ConfigureAwait(false);
            if (resp?.IsSuccessStatusCode == true)
                AccountName = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false))?["display_name"]?.ToString() ?? "";
        }
        catch (Exception ex) { Log.D("spotify", "profile read failed: " + ex.Message); }
    }

    // ---- the player: read now and then, write as the slider moves ------------------

    internal static (int? volume, string device, bool supports) ParsePlayer(string json)
    {
        var d = JsonNode.Parse(json)?["device"];
        if (d == null) return (null, "", false);
        int? v = d["volume_percent"] is JsonValue jv && jv.TryGetValue(out int i) ? i : null;
        bool supports = d["supports_volume"]?.GetValue<bool>() ?? v != null;
        return (v, d["name"]?.ToString() ?? "", supports);
    }

    /// <summary>
    /// Reads the player at most every few seconds, while the media panel shows
    /// Spotify (the caller decides that). Skipped while the slider is being
    /// dragged or just let go, so a stale read can't snap it back.
    /// </summary>
    public void Poll() { _ = PollAsync(false); }

    async Task PollAsync(bool now)
    {
        double t = clock.Now;
        if (!Connected || polling || t < backoffUntil || t < quietUntil) return;
        if (!now && t - lastPoll < 4) return;
        lastPoll = t; polling = true;
        try
        {
            using var resp = await Call(HttpMethod.Get, "/me/player").ConfigureAwait(false);
            if (resp == null) return;
            if (resp.StatusCode == HttpStatusCode.NoContent) { Ui.Post(() => { Volume = null; DeviceName = ""; }); Set("Spotify isn't playing on any device"); return; }
            if (!resp.IsSuccessStatusCode) { Set($"Spotify answered {(int)resp.StatusCode}"); return; }
            var (v, dev, sup) = ParsePlayer(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (clock.Now < quietUntil) return;   // the slider moved while this was out
            Ui.Post(() => { Volume = v; DeviceName = dev; SupportsVolume = sup; });
            Set(sup ? "Connected" : $"{dev} doesn't let apps change its volume");
        }
        catch (Exception ex) { Log.D("spotify", "player read failed: " + ex.Message); Set("Couldn't reach Spotify"); }
        finally { polling = false; Raise(); }
    }

    int? pending, lastValue;
    double lastSent = double.NegativeInfinity;
    long sendTimer;
    bool sending;

    /// <summary>
    /// Sets the volume as the slider moves: the slider follows the hand at once,
    /// and Spotify gets at most one request every 0.15 s, always ending with the
    /// value it was let go at.
    /// </summary>
    public void SetVolume(int percent, bool final)
    {
        percent = Math.Clamp(percent, 0, 100);
        Volume = percent;
        pending = percent;
        quietUntil = clock.Now + 2.5;
        double wait = lastSent + 0.15 - clock.Now;
        if (wait <= 0 || final) Flush();
        else if (sendTimer == 0) sendTimer = Ui.After(wait, () => { sendTimer = 0; Flush(); });
        Changed?.Invoke();
    }

    void Flush()
    {
        if (pending == null) return;
        if (sending) { Ui.Cancel(ref sendTimer); sendTimer = Ui.After(0.05, () => { sendTimer = 0; Flush(); }); return; }
        int v = pending.Value; pending = null;
        if (v == lastValue && clock.Now - lastSent < 1) return;   // letting go where the drag already was
        lastSent = clock.Now; lastValue = v; sending = true;
        _ = SendAsync(v);
    }

    async Task SendAsync(int v)
    {
        try
        {
            using var resp = await Call(HttpMethod.Put, $"/me/player/volume?volume_percent={v}").ConfigureAwait(false);
            if (resp == null) return;
            if (resp.StatusCode == HttpStatusCode.NotFound) Set("Spotify isn't playing on any device");
            else if (resp.StatusCode == HttpStatusCode.Forbidden) Set("Spotify refused: volume control needs Premium, or this device doesn't allow it");
            else if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 429) Set($"Spotify answered {(int)resp.StatusCode}");
            else if (resp.IsSuccessStatusCode) Log.D("spotify", $"volume -> {v}%");
        }
        catch (Exception ex) { Log.D("spotify", "volume write failed: " + ex.Message); Set("Couldn't reach Spotify"); }
        finally { Ui.Post(() => { sending = false; if (pending != null) Flush(); }); }
    }
}
