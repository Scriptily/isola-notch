using System.Text.RegularExpressions;
using Isola.App;
using Windows.UI.Notifications;

namespace Isola.Services;

/// <summary>
/// What Phone Link's relayed phone notifications need to read well.
///
/// Phone Link posts every phone notification as its own app ("…!App"), with
/// the phone app's name as the first line, then the notification's title,
/// then its message. Read as an ordinary notification, that gave a popup
/// titled "Home Assistant", from "Phone Link", with the real title glued onto
/// the message, and Phone Link's laptop icon.
///
/// Phone Link also registers one notification group (toast collection) per
/// phone app, "YourPhoneNotifications_{bundle id}", named after the app, and
/// caches each app's icon under that bundle id. The groups can be read through
/// Windows' own API, which gives name → bundle id → icon.
/// </summary>
public static partial class PhoneLink
{
    public const string Package = "Microsoft.YourPhone_8wekyb3d8bbwe";
    const string GroupPrefix = "YourPhoneNotifications_";

    public static bool Is(string aumid) => aumid != null && aumid.StartsWith(Package + "!", StringComparison.OrdinalIgnoreCase);

    /// <summary>App names carry invisible direction marks ("‎WhatsApp"); drop those and the spaces around.</summary>
    public static string Clean(string s) =>
        s == null ? null : new string(s.Where(ch => char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();

    /// <summary>The phone app's bundle id from a group id, or null for groups that aren't one app (calls, messages).</summary>
    internal static string BundleFrom(string groupId) =>
        groupId != null && groupId.StartsWith(GroupPrefix, StringComparison.Ordinal) && groupId.Length > GroupPrefix.Length ? groupId[GroupPrefix.Length..] : null;

    static Dictionary<string, string> apps = new(StringComparer.OrdinalIgnoreCase);
    static double loadedAt = double.NegativeInfinity;

    /// <summary>
    /// The bundle id of the phone app called <paramref name="name"/>, or null.
    /// The groups are read again when a name isn't known (a newly installed
    /// app), but at most once a minute.
    /// </summary>
    public static async Task<string> BundleForAsync(string name, double now)
    {
        name = Clean(name);
        if (string.IsNullOrEmpty(name)) return null;
        if (apps.TryGetValue(name, out var b)) return b;
        if (now - loadedAt < 60) return null;
        loadedAt = now;
        try
        {
            var groups = await ToastNotificationManager.GetDefault().GetToastCollectionManager(Package + "!App").FindAllToastCollectionsAsync().AsTask().ConfigureAwait(false);
            apps = Map(groups.Select(g => (g.Id, g.DisplayName)));
            Log.D("notify", $"Phone Link: {apps.Count} phone app(s) known");
        }
        catch (Exception ex) { Log.D("notify", "Phone Link's app list unavailable: " + ex.Message); }
        return apps.GetValueOrDefault(name);
    }

    internal static Dictionary<string, string> Map(IEnumerable<(string id, string name)> groups)
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, n) in groups)
            if (BundleFrom(id) is { } bundle && Clean(n) is { Length: > 0 } nm) m[nm] = bundle;
        return m;
    }

    internal static string IconDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", Package, "TempState", "app_icons");

    /// <summary>
    /// The cached icon file for a bundle id: "app_icon_{bundle}_1.0.jpg" (some
    /// without the underscore). The pattern is exact so "com.amazon.Amazon"
    /// doesn't pick up another app whose id merely starts the same way.
    /// </summary>
    internal static string IconFile(string dir, string bundle)
    {
        if (string.IsNullOrEmpty(bundle) || !Directory.Exists(dir)) return null;
        var re = new Regex("^app_icon_" + Regex.Escape(bundle) + @"_?\d[\d.]*\.(jpg|jpeg|png)$", RegexOptions.IgnoreCase);
        return Directory.EnumerateFiles(dir).Where(f => re.IsMatch(Path.GetFileName(f)))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public static byte[] Icon(string bundle)
    {
        try { return IconFile(IconDir, bundle) is { } f ? File.ReadAllBytes(f) : null; }
        catch { return null; }
    }
}
