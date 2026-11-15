using Isola.App;
using Isola.Native;
using Microsoft.Win32;

namespace Isola.Services;

/// <summary>Per-app "Show notification banners" values (ShowBanner under each app's notification settings key).</summary>
public interface IBannerStore
{
    IReadOnlyList<string> Apps();
    int? Get(string app);
    void Set(string app, int value);
    void Delete(string app);
    /// <summary>The app's notification sound (SoundFile): null = Windows' default, "" = silent.</summary>
    string GetSound(string app);
    void SetSound(string app, string value);
    void DeleteSound(string app);
}

public sealed class RegistryBanners : IBannerStore
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    public IReadOnlyList<string> Apps()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root);
        if (k == null) return Array.Empty<string>();
        return LeafApps(path =>
        {
            using var s = path.Length == 0 ? Registry.CurrentUser.OpenSubKey(Root) : Registry.CurrentUser.OpenSubKey(Root + "\\" + path);
            return s == null ? (Array.Empty<string>(), false) : (s.GetSubKeyNames(), s.ValueCount > 0);
        });
    }

    /// <summary>
    /// Every app's settings key, however deep. An app id with backslashes (a
    /// desktop program's, like PowerShell's "{GUID}\WindowsPowerShell\v1.0\powershell.exe")
    /// is stored as nested keys, and only the innermost holds its settings. Only
    /// the first level used to be read, so such apps got their value written to
    /// an empty parent and kept their banner and chime.
    /// </summary>
    internal static List<string> LeafApps(Func<string, (string[] subkeys, bool hasValues)> open, int maxDepth = 6)
    {
        var apps = new List<string>();
        void Walk(string path, int depth)
        {
            var (subs, values) = open(path);
            if (path.Length > 0 && values) apps.Add(path);
            if (depth >= maxDepth) return;
            foreach (var s in subs) Walk(path.Length == 0 ? s : path + "\\" + s, depth + 1);
        }
        Walk("", 0);
        return apps;
    }
    public int? Get(string app)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root + "\\" + app);
        return k?.GetValue("ShowBanner") is int v ? v : null;
    }
    public void Set(string app, int value)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root + "\\" + app, true);
        k?.SetValue("ShowBanner", value, RegistryValueKind.DWord);
    }
    public void Delete(string app)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root + "\\" + app, true);
        k?.DeleteValue("ShowBanner", false);
    }
    public string GetSound(string app)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root + "\\" + app);
        return k?.GetValue("SoundFile") as string;
    }
    public void SetSound(string app, string value)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root + "\\" + app, true);
        k?.SetValue("SoundFile", value, RegistryValueKind.String);
    }
    public void DeleteSound(string app)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root + "\\" + app, true);
        k?.DeleteValue("SoundFile", false);
    }
}

/// <summary>
/// Windows' own notification banners and their chime, switched off while the
/// island shows notifications (with its own sounds), so each one comes once.
/// The banner going off left the chime: Windows played it with the island's
/// popup. Each app's sound is its SoundFile value; blank silences it at once
/// and removing it brings the default back (both measured).
///
/// Windows' main "Notifications" switch can't be used: written directly it
/// does nothing (measured), and switched off it would also stop notifications
/// reaching the notification center, which is where the island reads them.
/// Each app's "Show notification banners" (ShowBanner = 0) takes effect at
/// once, and notifications still land in the center.
///
/// It must never stay off when the island can't show them. Before anything is
/// changed, the original values go to a journal; they come back when the
/// setting goes off, when the island can't show alerts (its screen is off, a
/// fullscreen app), on quit and sign-out; a small watcher process puts them
/// back if the island crashes or is killed; and whatever a journal still holds
/// is restored at the next start and by the uninstaller. An app whose banners
/// were already off is never touched, and one the user changes in the
/// meantime keeps the user's choice.
/// </summary>
public sealed class WindowsBanners
{
    readonly IBannerStore store;
    readonly string journal;
    readonly Dictionary<string, Taken> taken = new(StringComparer.OrdinalIgnoreCase);
    internal record struct Taken(bool HadBanner, int Banner, bool HadSound, string Sound);
    public bool Active { get; private set; }
    /// <summary>Apps left alone: Phone Link's calls (the island presses buttons on that popup) and apps muted in the island.</summary>
    public Func<string, bool> Exempt = DefaultExempt;

    public static bool DefaultExempt(string app) => app.Contains("YourPhoneCalling", StringComparison.OrdinalIgnoreCase);

    public WindowsBanners(IBannerStore s, string journalPath) { store = s; journal = journalPath; }

    public int Count => taken.Count;

    /// <summary>Banners off for every app not yet handled (again later: new apps appear). Idempotent.</summary>
    public void Suppress()
    {
        var add = new List<string>();
        foreach (var app in store.Apps())
        {
            if (taken.ContainsKey(app) || Exempt(app)) continue;
            int? cur;
            try { cur = store.Get(app); } catch { continue; }
            if (cur == 0) continue;                         // already off, by the user: not ours
            string snd = null;
            try { snd = store.GetSound(app); } catch { }
            taken[app] = new Taken(cur.HasValue, cur ?? 1, snd != null, snd ?? "");
            add.Add(app);
        }
        if (add.Count == 0) { Active = true; return; }
        Save();                                             // the journal first: a crash part-way can still be undone
        foreach (var app in add) { try { store.Set(app, 0); store.SetSound(app, ""); } catch (Exception ex) { Log.D("banners", $"{app}: {ex.Message}"); } }
        if (!Active || add.Count > 0) Log.I("banners", $"Windows banners off for {add.Count} app(s) ({taken.Count} in all)");
        Active = true;
    }

    /// <summary>Gives one app its banners back (it became exempt: muted in the island).</summary>
    public void Release(string app)
    {
        if (!taken.Remove(app, out var e)) return;
        Put(store, app, e);
        Save();
    }

    public void Restore(string why)
    {
        if (taken.Count > 0) Log.I("banners", $"Windows banners back for {taken.Count} app(s) ({why})");
        foreach (var (app, e) in taken) Put(store, app, e);
        taken.Clear();
        Active = false;
        try { File.Delete(journal); } catch { }
    }

    static void Put(IBannerStore store, string app, Taken e)
    {
        try
        {
            // each value only if it is still what we set: a change since is the user's choice
            if (store.Get(app) == 0) { if (e.HadBanner) store.Set(app, e.Banner); else store.Delete(app); }
            if (store.GetSound(app) == "") { if (e.HadSound) store.SetSound(app, e.Sound); else store.DeleteSound(app); }
        }
        catch (Exception ex) { Log.D("banners", $"restoring {app}: {ex.Message}"); }
    }

    void Save()
    {
        // one app per line, "app<TAB>hadBanner<TAB>banner<TAB>hadSound<TAB>sound":
        // nothing a reflection-free build can't read (a sound path has no tabs)
        var tmp = journal + ".tmp";
        File.WriteAllLines(tmp, taken.Select(kv => $"{kv.Key}\t{(kv.Value.HadBanner ? 1 : 0)}\t{kv.Value.Banner}\t{(kv.Value.HadSound ? 1 : 0)}\t{kv.Value.Sound}"));
        File.Move(tmp, journal, true);
    }

    /// <summary>Restores whatever a journal holds (a previous run that never got to). Returns how many apps.</summary>
    public static int RestoreJournal(IBannerStore store, string path)
    {
        if (!File.Exists(path)) return 0;
        int n = 0;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var p = line.Split('\t');
                if (p.Length < 3 || !int.TryParse(p[2], out int v)) continue;
                // three fields: a journal from before sounds were handled
                Put(store, p[0], new Taken(p[1] == "1", v, p.Length >= 5 && p[3] == "1", p.Length >= 5 ? p[4] : "")); n++;
            }
            File.Delete(path);
        }
        catch (Exception ex) { Log.W("banners", "journal restore failed: " + ex.Message); }
        if (n > 0) Log.I("banners", $"Windows banners back for {n} app(s) left over from a previous run");
        return n;
    }

    /// <summary>
    /// The watcher: a second copy of the island, started with --banner-guard,
    /// that waits for the island's process to end however it ends (a crash, Task
    /// Manager) and then restores the journal. After a normal quit the journal
    /// is already gone and it simply exits.
    /// </summary>
    public static int Guard(int pid, string path)
    {
        uint code = 0;
        nint h = Win32.OpenProcess(0x00100000 /*SYNCHRONIZE*/ | 0x1000 /*QUERY_LIMITED_INFORMATION*/, false, (uint)pid);
        if (h != 0) { Win32.WaitForSingleObject(h, 0xFFFFFFFF); Win32.GetExitCodeProcess(h, out code); Win32.CloseHandle(h); }
        RestoreJournal(new RegistryBanners(), path);
        // A crash brings the island back (a quit, Task Manager or setup ending it doesn't)
        if (Crashed(code) && Environment.ProcessPath is { } exe && AllowRestart(Path.Combine(Path.GetDirectoryName(path)!, "restarts.txt"), DateTime.Now))
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--after-crash") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) }); } catch { }
        return 0;
    }

    /// <summary>
    /// An exit code that means the process failed rather than was ended: a
    /// Windows exception (0xC...: access violation, fail-fast) or .NET's
    /// unhandled exception. Not 0 (quit), 1 (Task Manager, taskkill),
    /// 0xFFFFFFFF (a program ending it, as setup does during an update) or
    /// Ctrl+C.
    /// </summary>
    internal static bool Crashed(uint code) =>
        code == 0xE0434352 || ((code & 0xF0000000) == 0xC0000000 && code != 0xC000013A);

    /// <summary>At most three crash restarts in ten minutes: a crash at startup mustn't loop forever.</summary>
    internal static bool AllowRestart(string file, DateTime now)
    {
        try
        {
            var recent = (File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>())
                .Select(l => DateTime.TryParse(l, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                .Where(t => now - t < TimeSpan.FromMinutes(10)).ToList();
            if (recent.Count >= 3) return false;
            recent.Add(now);
            File.WriteAllLines(file, recent.Select(t => t.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
            return true;
        }
        catch { return false; }
    }
}
