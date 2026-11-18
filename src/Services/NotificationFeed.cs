using Isola.App;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using WinRT;

namespace Isola.Services;

public sealed class NotificationItem
{
    public uint Id;
    public string App = "", AppId = "", Title = "", Body = "";
    /// <summary>"Phone Link" when App is a phone app relayed by it; otherwise empty.</summary>
    public string Via = "";
    /// <summary>The phone app's bundle id, for its icon.</summary>
    public string Bundle;
    public byte[] Icon;
    public DateTime At;
    public bool IsCall;
    /// <summary>The notification's lines as Windows gave them (Title and Body join them).</summary>
    public List<string> Lines = new();
    /// <summary>Who's calling, when it's a call: the line that isn't "Incoming call".</summary>
    public string Caller = "";
}

/// <summary>
/// Windows notifications (toasts) - including Phone Link's, so iPhone messages
/// and calls arrive here - through UserNotificationListener.
///
/// Unpackaged apps may read the notification list but cannot subscribe to its
/// change event (it throws without package identity), so while the setting is
/// on the list is polled every 1.5 s and diffed. Off, it costs nothing.
/// </summary>
public sealed partial class NotificationFeed
{
    readonly IClock clock;
    UserNotificationListener listener;
    readonly Dictionary<uint, NotificationItem> seen = new();
    readonly Dictionary<string, byte[]> icons = new();
    public readonly List<NotificationItem> Recent = new();
    public event Action<NotificationItem> Arrived;     // UI thread
    public event Action<NotificationItem> Removed;     // UI thread
    public string Status { get; private set; } = "Off";
    long timer;
    bool primed, busy, running;

    public NotificationFeed(IClock c) { clock = c; }

    public async Task StartAsync()
    {
        if (running) return;
        running = true;
        try
        {
            listener = UserNotificationListener.Current;
            var access = await listener.RequestAccessAsync().AsTask().ConfigureAwait(false);
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Status = "Windows denied notification access (Settings -> Privacy -> Notifications)";
                Log.W("notify", Status); running = false; return;
            }
            Status = "On";
            Log.I("notify", "notification access allowed; polling");
            Ui.Post(() => Schedule(0.1));
        }
        catch (Exception ex) { Status = "Unavailable: " + ex.Message; Log.E("notify", "notification listener unavailable", ex); running = false; }
    }

    public void Stop() { running = false; Ui.Cancel(ref timer); Status = "Off"; primed = false; seen.Clear(); }

    void Schedule(double s) { Ui.Cancel(ref timer); if (running) timer = Ui.After(s, () => _ = Poll()); }

    async Task Poll()
    {
        if (busy || listener == null) { Schedule(1.5); return; }
        busy = true;
        try
        {
            var list = await listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask().ConfigureAwait(false);
            var items = new List<NotificationItem>();
            foreach (var n in list)
            {
                if (seen.ContainsKey(n.Id)) { items.Add(seen[n.Id]); continue; }
                var it = new NotificationItem { Id = n.Id, At = n.CreationTime.LocalDateTime };
                try
                {
                    it.App = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
                    it.AppId = n.AppInfo?.AppUserModelId ?? "";
                }
                catch { }
                try
                {
                    var b = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                    var texts = b?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new();
                    string bundle = PhoneLink.Is(it.AppId) && texts.Count >= 2 ? await PhoneLink.BundleForAsync(texts[0], clock.Now).ConfigureAwait(false) : null;
                    Shape(it, texts, bundle);
                }
                catch { }
                it.Caller = CallerOf(it) ?? "";
                it.IsCall = it.Caller.Length > 0;
                // relayed phone apps all share Phone Link's id: their icons are keyed by app
                string iconKey = it.Bundle != null ? "phone:" + it.Bundle : it.AppId;
                if (!string.IsNullOrEmpty(iconKey) && !icons.ContainsKey(iconKey))
                    icons[iconKey] = (it.Bundle != null ? PhoneLink.Icon(it.Bundle) : null) ?? await Logo(n).ConfigureAwait(false);
                it.Icon = icons.GetValueOrDefault(iconKey);
                items.Add(it);
            }
            Ui.Post(() => Apply(items));
        }
        catch (Exception ex) { Log.D("notify", "poll failed: " + ex.Message); }
        finally { busy = false; Ui.Post(() => Schedule(1.5)); }
    }

    /// <summary>
    /// Title and message from the notification's lines. For a phone app relayed
    /// by Phone Link (<paramref name="phoneBundle"/> known), the first line is the
    /// app's name, not the title: it becomes the app, and the title and message
    /// follow it. Anything else, or a Phone Link notification whose first line
    /// isn't a known phone app (calls, messages), reads the ordinary way.
    /// </summary>
    internal static void Shape(NotificationItem it, List<string> texts, string phoneBundle)
    {
        it.Lines = texts.ToList();
        it.Title = texts.Count > 0 ? texts[0] : it.App;
        it.Body = string.Join(" ", texts.Skip(1));
        if (phoneBundle == null || texts.Count < 2) return;
        it.App = PhoneLink.Clean(texts[0]);
        it.Via = "Phone Link";
        it.Bundle = phoneBundle;
        it.Title = texts[1];
        it.Body = string.Join(" ", texts.Skip(2));
    }

    /// <summary>
    /// Whether a notification is a phone call ringing. Only Phone Link's own
    /// call notification counts: one of its first lines is exactly "Incoming
    /// call" (or "Calling…"), and the caller is the line beside it. Phone Link
    /// puts the caller (a name or a number) first and "Incoming call" under it;
    /// 1.4.0 looked only at the first line and missed every real call. Matching
    /// "is calling" anywhere made an app's message ("Your cart is calling",
    /// relayed from the phone) ring like a call, so a phone app relayed by
    /// Phone Link never counts, and the words must be a whole line, not part of
    /// a sentence.
    /// </summary>
    internal static bool IsPhoneCall(NotificationItem it) => CallerOf(it) != null;

    /// <summary>Who's calling if it's a call (the line that isn't "Incoming call"), else null.</summary>
    internal static string CallerOf(NotificationItem it)
    {
        bool fromPhoneLink = it.AppId.Contains("YourPhone", StringComparison.OrdinalIgnoreCase) || it.App.Equals("Phone Link", StringComparison.OrdinalIgnoreCase);
        if (!fromPhoneLink || it.Bundle != null || it.Via.Length > 0) return null;
        var lines = it.Lines.Count > 0 ? it.Lines : new List<string> { it.Title };
        for (int i = 0; i < Math.Min(3, lines.Count); i++)
        {
            if (!CallLine.IsMatch(lines[i].Trim())) continue;
            // the caller is the first other line with something in it; a call with no name says so
            string caller = lines.Where((l, j) => j != i && !string.IsNullOrWhiteSpace(l) && !CallLine.IsMatch(l.Trim())).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(caller) ? "Unknown caller" : caller;
        }
        return null;
    }
    static readonly System.Text.RegularExpressions.Regex CallLine = new(@"^(incoming (voice |video |audio )?call|calling(…|\.\.\.)?)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The app's own icon. GetLogo gives one only for packaged apps; desktop apps
    /// (Discord, PowerShell, Spotify) came back empty and showed a generic bell.
    /// shell:AppsFolder\{app id} is where the Start menu takes every app's icon
    /// from, packaged or not, so that comes first; GetLogo is the fallback.
    /// </summary>
    static async Task<byte[]> Logo(UserNotification n)
    {
        string aumid = n.AppInfo?.AppUserModelId;
        if (!string.IsNullOrEmpty(aumid))
        {
            var png = await Task.Run(() => StaIcon(aumid)).ConfigureAwait(false);
            if (png != null) return png;
        }
        try
        {
            var r = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
            if (r == null) return null;
            using var s = await r.OpenReadAsync().AsTask().ConfigureAwait(false);
            using var ns = s.AsStreamForRead(); using var ms = new MemoryStream();
            await ns.CopyToAsync(ms).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // The shell's icon handlers want a single-threaded apartment; give the
    // lookup one of its own for the moment it takes (once per app: cached).
    internal static byte[] StaIcon(string aumid)
    {
        byte[] png = null;
        var t = new Thread(() =>
        {
            try
            {
                var icon = Shell.Icon(@"shell:AppsFolder\" + aumid, 64);
                if (icon is { } i && i.w > 0) png = Png.Encode(i.px, i.w, i.h);
            }
            catch (Exception ex) { Log.D("notify", $"icon for {aumid}: {ex.Message}"); }
        }) { IsBackground = true, Name = "notify icon" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(3000);
        return png;
    }

    internal void Apply(List<NotificationItem> items)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        // removals first, so a call that ended and the next one ringing, seen in
        // the same poll, hand over in that order; then arrivals oldest first
        foreach (var id in seen.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            var gone = seen[id]; seen.Remove(id);
            if (primed) Removed?.Invoke(gone);
        }
        foreach (var it in items.OrderBy(i => i.At))
        {
            if (seen.ContainsKey(it.Id)) continue;
            seen[it.Id] = it;
            // the first poll only learns what is already in Action Center
            if (!primed) continue;
            if ((DateTime.Now - it.At).TotalMinutes > 2) continue;
            Recent.Insert(0, it); if (Recent.Count > 30) Recent.RemoveAt(Recent.Count - 1);
            // the app only: titles are people's names and messages, and logs get shared
            Log.I("notify", $"from {it.App}{(it.IsCall ? " (a call)" : "")}");
            Arrived?.Invoke(it);
        }
        primed = true;
    }
}
