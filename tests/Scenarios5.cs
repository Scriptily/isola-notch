using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Isola.App;
using Isola.Services;

namespace Isola.Tests;

/// <summary>
/// Spotify's accounts service and Web API for one Premium user: PKCE sign-in,
/// token refresh, the player and its volume. Checks what the real service
/// checks (the verifier must hash to the challenge; a stale access token gets
/// 401; a revoked refresh token gets invalid_grant).
/// </summary>
public sealed class FakeSpotify : HttpMessageHandler
{
    public string ExpectedChallenge, Code = "auth-code-1";
    public string Access = "", Refresh = "refresh-1";
    public bool RefreshRevoked, NoDevice;
    public int Volume = 0;
    public string Device = Environment.MachineName;
    public readonly List<int> VolumeWrites = new();
    /// <summary>Player reads wait on this, so a test can hold one in flight.</summary>
    public Task PlayerGate = Task.CompletedTask;
    public readonly List<string> Log = new();
    int issued;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var u = req.RequestUri!;
        Log.Add($"{req.Method} {u.Host}{u.AbsolutePath}");
        if (u.Host == "accounts.spotify.com" && u.AbsolutePath == "/api/token")
        {
            var form = (await req.Content!.ReadAsStringAsync(ct)).Split('&').Select(kv => kv.Split('='))
                .ToDictionary(kv => Uri.UnescapeDataString(kv[0]), kv => Uri.UnescapeDataString(kv[1].Replace('+', ' ')));
            if (form["grant_type"] == "authorization_code")
            {
                if (form["code"] != Code || SpotifyService.Challenge(form["code_verifier"]) != ExpectedChallenge)
                    return Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"code verifier mismatch\"}");
                return Tokens(rotate: true);
            }
            if (form["grant_type"] == "refresh_token")
                return RefreshRevoked || form["refresh_token"] != Refresh
                    ? Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")
                    : Tokens(rotate: false);
        }
        if (u.Host == "api.spotify.com")
        {
            if (req.Headers.Authorization?.Parameter != Access || Access == "") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (u.AbsolutePath == "/v1/me/player" && req.Method == HttpMethod.Get)
            {
                int seen = Volume;                         // what Spotify had when the read arrived
                await PlayerGate;
                if (!NoDevice) return Json(HttpStatusCode.OK, $"{{\"device\":{{\"name\":\"{Device}\",\"volume_percent\":{seen},\"supports_volume\":true}}}}");
            }
            if (u.AbsolutePath == "/v1/me/player" && req.Method == HttpMethod.Get)
                return NoDevice ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : Json(HttpStatusCode.OK, $"{{\"device\":{{\"name\":\"{Device}\",\"volume_percent\":{Volume},\"supports_volume\":true,\"is_active\":true}},\"is_playing\":true}}");
            if (u.AbsolutePath == "/v1/me" && req.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, "{\"display_name\":\"Sample User\",\"id\":\"user1\"}");
            if (u.AbsolutePath == "/v1/me/player/volume" && req.Method == HttpMethod.Put)
            {
                int v = int.Parse(u.Query.Split('=')[1]);
                lock (VolumeWrites) VolumeWrites.Add(v);
                Volume = v;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    HttpResponseMessage Tokens(bool rotate)
    {
        Access = "access-" + (++issued);
        string r = rotate ? $",\"refresh_token\":\"{Refresh}\"" : "";
        return Json(HttpStatusCode.OK, $"{{\"access_token\":\"{Access}\",\"token_type\":\"Bearer\",\"expires_in\":3600{r}}}");
    }
    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public static partial class Scenarios
{
    static (SpotifyService sp, FakeSpotify fake, FakeClock clock, string dir) NewSpotify(bool signedIn)
    {
        Ui.Reset(); var clock = new FakeClock(); Ui.Clock = clock;
        string dir = Path.Combine(Path.GetTempPath(), "di-sp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var fake = new FakeSpotify();
        var sp = new SpotifyService(clock, fake) { ClientId = () => "0123456789abcdef0123456789abcdef", TokenFile = Path.Combine(dir, "spotify.json") };
        if (signedIn) File.WriteAllText(sp.TokenFile, "{\"refresh_token\":\"refresh-1\"}");
        sp.Load();
        return (sp, fake, clock, dir);
    }

    static void Settle(FakeClock clock, Func<bool> done, double seconds = 3)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.Elapsed.TotalSeconds < seconds) { Thread.Sleep(15); clock.Advance(0.02); }
        clock.Advance(0.02);
    }

    [Scenario("Spotify PKCE: the challenge is RFC 7636's, and the sign-in reply is parsed from the loopback request")]
    public static void SpotifyPkceAndCallback()
    {
        Eq("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", SpotifyService.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"), "RFC 7636 appendix B");
        var r = SpotifyService.ParseCallback("GET /callback?code=AQB%2Fx-y&state=s1 HTTP/1.1");
        Eq("AQB/x-y", r?.code, "code, unescaped"); Eq("s1", r?.state, "state"); Eq(null, r?.error, "no error");
        Eq("access_denied", SpotifyService.ParseCallback("GET /callback?error=access_denied&state=s1 HTTP/1.1")?.error, "canceled");
        True(SpotifyService.ParseCallback("GET /favicon.ico HTTP/1.1") == null, "the browser's favicon request isn't the reply");
        var p = SpotifyService.ParsePlayer("{\"device\":{\"name\":\"Kitchen\",\"volume_percent\":37,\"supports_volume\":false}}");
        Eq(37, p.volume, "volume"); Eq("Kitchen", p.device, "device"); Eq(false, p.supports, "device that won't take volume");
        Eq(null, SpotifyService.ParsePlayer("{\"is_playing\":false}").volume, "no device");
    }

    [Scenario("connecting Spotify signs in through the browser and the loopback port, and keeps the sign-in in its own file")]
    public static void SpotifyConnects()
    {
        var (sp, fake, clock, dir) = NewSpotify(signedIn: false);
        try
        {
            True(!sp.Connected, "not connected at first");
            string opened = null;
            sp.OpenBrowser = url =>
            {
                opened = url;
                // the browser: Spotify sends it back to the redirect URI with a code and our state
                var q = new Uri(url).Query.TrimStart('?').Split('&').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv[1]));
                fake.ExpectedChallenge = q["code_challenge"];
                Task.Run(() =>
                {
                    using var c = new TcpClient("127.0.0.1", SpotifyService.Port);
                    var s = c.GetStream();
                    s.Write(Encoding.ASCII.GetBytes("GET /favicon.ico HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
                    s.ReadByte();
                });
                Task.Run(() =>
                {
                    Thread.Sleep(100);
                    using var c = new TcpClient("127.0.0.1", SpotifyService.Port);
                    var s = c.GetStream();
                    s.Write(Encoding.ASCII.GetBytes($"GET /callback?code={fake.Code}&state={q["state"]} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
                    var buf = new byte[4096]; int n = s.Read(buf);
                    lock (fake.Log) fake.Log.Add("browser got: " + Encoding.UTF8.GetString(buf, 0, n).Split("\r\n")[0]);
                });
            };
            fake.Volume = 0;   // a real case: Spotify itself turned all the way down
            var connect = sp.ConnectAsync();
            Settle(clock, () => connect.IsCompleted, 6);
            True(opened != null && opened.StartsWith("https://accounts.spotify.com/authorize?client_id=0123456789abcdef0123456789abcdef"), "opened Spotify's sign-in page: " + opened);
            True(opened.Contains("redirect_uri=" + Uri.EscapeDataString(SpotifyService.RedirectUri)) && opened.Contains("code_challenge_method=S256"), "with the loopback redirect and S256");
            True(sp.Connected, "connected: " + sp.Status);
            True(fake.Log.Any(l => l.StartsWith("browser got: HTTP/1.1 200")), "the browser got a page saying it worked");
            True(File.ReadAllText(sp.TokenFile).Contains("dpapi:") && !File.ReadAllText(sp.TokenFile).Contains("refresh-1"), "the refresh token is in spotify.json, encrypted");
            Eq("Sample User", sp.AccountName, "signed in as the account's display name");
            True(sp.AuthorizedAt is DateTime at && Math.Abs((at - DateTime.Now).TotalMinutes) < 1, "the sign-in time is recorded (Spotify's tokens carry none)");
            Eq(sp.AuthorizedAt.Value.AddMonths(6), sp.ReauthBy, "sign in again six months later");
            var again = new SpotifyService(clock, fake) { ClientId = sp.ClientId, TokenFile = sp.TokenFile }; again.Load();
            True(again.Connected && again.AccountName == "Sample User" && again.ReauthBy == sp.ReauthBy, "all of it survives a restart");
            Settle(clock, () => sp.Volume != null);
            Eq(0, sp.Volume, "read the in-app volume: 0%, the reason it was silent");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Scenario("Spotify's six-month sign-in: the date is shown, Renew appears in the last two weeks, and running out is called that")]
    public static void SpotifySixMonths()
    {
        var (sp, fake, clock, dir) = NewSpotify(signedIn: false);
        try
        {
            var signedIn = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Local);
            File.WriteAllText(sp.TokenFile, "{\"refresh_token\":\"refresh-1\",\"authorized_at\":\"" + signedIn.ToUniversalTime().ToString("o") + "\",\"account\":\"Scriptily\"}");
            sp.Wall = () => signedIn.AddDays(30); sp.Load();
            Eq(new DateTime(2027, 5, 14, 12, 0, 0), sp.ReauthBy, "six calendar months after signing in");
            True(sp.ReauthDaysLeft > 14, "a month in: no nagging");
            sp.Wall = () => new DateTime(2027, 5, 4, 12, 0, 0);
            Eq(10, sp.ReauthDaysLeft, "ten days left: the Renew link shows");
            sp.Wall = () => new DateTime(2027, 5, 15, 9, 0, 0);
            fake.RefreshRevoked = true;
            sp.Poll(); Settle(clock, () => sp.Expired);
            True(sp.Expired && sp.Status.Contains("six-month"), "past the date, Spotify's refusal is explained as the six months: " + sp.Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Scenario("Spotify volume: the slider follows the hand, Spotify gets a request at most every 0.15 s, ending on the value let go at")]
    public static void SpotifyVolumeThrottled()
    {
        var (sp, fake, clock, dir) = NewSpotify(signedIn: true);
        try
        {
            fake.Volume = 0;
            sp.Poll(); Settle(clock, () => sp.Volume != null);
            Eq(0, sp.Volume, "read");
            for (int v = 5; v <= 50; v += 5) { sp.SetVolume(v, false); Eq(v, sp.Volume, "slider follows at once"); clock.Advance(0.02); Thread.Sleep(5); }
            sp.SetVolume(52, true);
            Settle(clock, () => fake.Volume == 52 && fake.VolumeWrites.Count > 0 && fake.VolumeWrites[^1] == 52, 3);
            True(fake.VolumeWrites.Count <= 4, $"throttled: {fake.VolumeWrites.Count} requests for 11 moves ({string.Join(",", fake.VolumeWrites)})");
            Eq(52, fake.VolumeWrites[^1], "the last request is the value let go at");
            Eq(52, fake.Volume, "Spotify ends at 52%");
            fake.Volume = 10;                    // someone moves it in the app...
            clock.Advance(1); sp.Poll(); Settle(clock, () => false, 0.2);
            Eq(52, sp.Volume, "...a read right after letting go doesn't snap the slider back");
            clock.Advance(5); sp.Poll(); Settle(clock, () => sp.Volume == 10);
            Eq(10, sp.Volume, "a later read picks up the change made in the app");
            // a read already on its way when the slider moves must not land on top of it
            var gate = new TaskCompletionSource(); fake.PlayerGate = gate.Task;
            clock.Advance(5); sp.Poll(); Thread.Sleep(50);
            sp.SetVolume(70, true); Settle(clock, () => fake.Volume == 70);
            gate.SetResult(); Settle(clock, () => false, 0.3);
            Eq(70, sp.Volume, "the read that left at 10% doesn't undo the 70% set while it was out");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Scenario("Spotify: an expired access token is refreshed once; a revoked sign-in disconnects instead of retrying forever")]
    public static void SpotifyTokens()
    {
        var (sp, fake, clock, dir) = NewSpotify(signedIn: true);
        try
        {
            fake.Volume = 44;
            sp.Poll(); Settle(clock, () => sp.Volume != null);
            Eq(44, sp.Volume, "first read refreshed the access token and read the player");
            fake.Access = "rotated-by-spotify";   // the access token we hold is now stale
            fake.Volume = 45;
            clock.Advance(5); sp.Poll(); Settle(clock, () => sp.Volume == 45);
            Eq(45, sp.Volume, "401, refresh, retry");
            fake.NoDevice = true;
            clock.Advance(5); sp.Poll(); Settle(clock, () => sp.Volume == null);
            True(sp.Volume == null && sp.Status.Contains("isn't playing"), "nothing playing anywhere: " + sp.Status);
            fake.RefreshRevoked = true; fake.Access = "rotated-again";
            clock.Advance(5); sp.Poll(); Settle(clock, () => !sp.Connected);
            True(!sp.Connected && sp.Expired, "revoked: signed out, and it knows why");
            True(!File.ReadAllText(sp.TokenFile).Contains("refresh-1"), "the dead refresh token is forgotten (Spotify says retrying can't help)");
            var after = new SpotifyService(clock, fake) { ClientId = sp.ClientId, TokenFile = sp.TokenFile }; after.Load();
            True(!after.Connected && after.Expired && after.Status.Contains("sign in again"), "after a restart it still says to sign in again: " + after.Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Scenario("saving the config keeps settings this version doesn't know (an older copy wiped the Spotify client ID)")]
    public static void ConfigKeepsUnknownSettings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "di-cfg-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "c.json");
            File.WriteAllText(f, "{ \"Style\": \"notch\", \"FutureClientId\": \"abc123\", \"FutureOptions\": { \"a\": 1, \"b\": [true, null] } }");
            var c = Config.Load(f);
            Eq(null, c.LoadError, "loads"); Eq("notch", c.Style, "known setting read");
            c.Scale = 1.2; c.Save(f);
            var js = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f))!.AsObject();
            Eq("abc123", (string)js["FutureClientId"], "an unknown string setting survives a save");
            Eq("{\"a\":1,\"b\":[true,null]}", js["FutureOptions"]?.ToJsonString(), "and an unknown object, as it was");
            Eq(1.2, (double)js["Scale"], "while the known ones save normally");
            var clone = Config.Load(f).Clone(); clone.Save(f);
            True(File.ReadAllText(f).Contains("abc123"), "the settings window's working copy (a clone) keeps them too");
        }
        finally { Directory.Delete(dir, true); }
    }
}
