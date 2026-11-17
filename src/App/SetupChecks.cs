using System.Diagnostics;
using Microsoft.Win32;

namespace Isola.App;

/// <summary>
/// What the setup wizard checks and installs. The island itself is one
/// self-contained program (no .NET or other runtime to install); what a fresh
/// computer can lack is the icon font (part of Windows 11, missing on Windows 10),
/// a permission or two, and the optional helpers: Everything and MagicPods.
/// </summary>
public static class SetupChecks
{
    public const string EverythingWingetId = "voidtools.Everything";
    public const string MagicPodsFamily = "715AleksandrMaslov.MagicPods_vk1h99z2mpttm";
    public const string MagicPodsStoreId = "9P6SKKFKSHKM";   // apps.microsoft.com/detail/9P6SKKFKSHKM - a paid app, so the Store installs it, not winget
    public const string IconFontUrl = "https://aka.ms/SegoeFluentIcons";

    /// <summary>Windows 10 2004 (build 19041) or later: the media, notification and Bluetooth APIs the island uses.</summary>
    public static bool WindowsSupported(Version v) => v.Major > 10 || (v.Major == 10 && v.Build >= 19041);

    static string WinFonts => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    static string UserFonts => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

    public static bool IconFontPresent() =>
        File.Exists(Path.Combine(WinFonts, "SegoeIcons.ttf")) || File.Exists(Path.Combine(UserFonts, "SegoeIcons.ttf"));

    // ---- Everything -------------------------------------------------------

    internal static bool IsEverythingEntry(string displayName, string publisher) =>
        displayName != null && displayName.StartsWith("Everything", StringComparison.OrdinalIgnoreCase)
        && publisher != null && publisher.Contains("voidtools", StringComparison.OrdinalIgnoreCase);

    public static string EverythingExe()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
        {
            var f = Path.Combine(root, "Everything", "Everything.exe");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>Installed (any edition, per-machine or per-user) or simply running.</summary>
    public static bool EverythingInstalled()
    {
        if (Native.Win32.FindWindowW("EVERYTHING_TASKBAR_NOTIFICATION", null) != 0) return true;
        if (EverythingExe() != null) return true;
        foreach (var (hive, path) in UninstallRoots())
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                if (k == null) continue;
                foreach (var name in k.GetSubKeyNames())
                {
                    using var e = k.OpenSubKey(name);
                    if (IsEverythingEntry(e?.GetValue("DisplayName") as string, e?.GetValue("Publisher") as string)) return true;
                }
            }
            catch { }
        }
        return false;
    }

    static IEnumerable<(RegistryKey, string)> UninstallRoots()
    {
        yield return (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        yield return (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        yield return (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
    }

    // ---- MagicPods --------------------------------------------------------

    public static bool MagicPodsInstalled()
    {
        // a Store app keeps its data folder under Packages while it is installed
        string pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", MagicPodsFamily);
        if (Directory.Exists(pk)) return true;
        try { return new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty, MagicPodsFamily).Any(); }
        catch { return false; }
    }

    // ---- environment ------------------------------------------------------

    public static bool WingetAvailable() => Run("winget", "--version", 8000).code == 0;

    public static string NotificationAccess()
    {
        try { return Windows.UI.Notifications.Management.UserNotificationListener.Current.GetAccessStatus().ToString(); }
        catch (Exception ex) { return "Unavailable: " + ex.Message; }
    }

    public static bool BluetoothPresent()
    {
        try { return Windows.Devices.Radios.Radio.GetRadiosAsync().AsTask().GetAwaiter().GetResult().Any(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth); }
        catch { return false; }
    }

    public static (int code, string output) Run(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "couldn't start " + file);
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-2, file + " took too long"); }
            return (p.ExitCode, (o.Result + "\n" + e.Result).Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    public static Version InstalledVersion(string exe)
    {
        try
        {
            if (!File.Exists(exe)) return null;
            var s = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0];
            return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
        }
        catch { return null; }
    }

    /// <summary>A newer setup from the GitHub release, checked against the hash published with it (as the updater does).</summary>
    public static (string file, string error) DownloadVerified(string url, string sha, Version v)
    {
        try
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha)) return (null, "the release has no verifiable setup");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Isola-Setup/1.0");
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var r = http.Send(req);
            r.EnsureSuccessStatusCode();
            string file = Path.Combine(Path.GetTempPath(), $"IsolaSetup-{v.Major}.{v.Minor}.{v.Build}.exe");
            using (var fs = File.Create(file)) r.Content.ReadAsStream().CopyTo(fs);
            string got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            if (!string.Equals(got, sha, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(file); } catch { } return (null, "it didn't match its published hash"); }
            return (file, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ---- installs ---------------------------------------------------------

    /// <summary>Everything through winget (its signed MSI from voidtools; Windows asks for administrator rights), then started.</summary>
    public static (bool ok, string detail) InstallEverything()
    {
        var (code, output) = Run("winget", $"install --id {EverythingWingetId} -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", 15 * 60 * 1000);
        bool ok = EverythingInstalled();
        if (!ok) return (false, code == -1 ? "winget isn't available" : LastLine(output));
        var exe = EverythingExe();
        if (exe != null && Native.Win32.FindWindowW("EVERYTHING_TASKBAR_NOTIFICATION", null) == 0)
            try { Process.Start(new ProcessStartInfo(exe, "-startup") { UseShellExecute = true }); } catch { }
        return (true, "Installed and running");
    }

    static string LastLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    /// <summary>The per-user copy of Segoe Fluent Icons, from Microsoft's download, for Windows 10.</summary>
    public static (bool ok, string detail) InstallIconFont()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "island-font-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(tmp);
            string zip = Path.Combine(tmp, "font.zip");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                File.WriteAllBytes(zip, http.GetByteArrayAsync(IconFontUrl).GetAwaiter().GetResult());
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, tmp, true);
            var ttf = Directory.EnumerateFiles(tmp, "*.ttf", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileName(f).Contains("Fluent", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("SegoeIcons.ttf", StringComparison.OrdinalIgnoreCase));
            if (ttf == null) return (false, "the download had no font in it");
            Directory.CreateDirectory(UserFonts);
            string dest = Path.Combine(UserFonts, "SegoeIcons.ttf");
            File.Copy(ttf, dest, true);
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts"))
                k.SetValue("Segoe Fluent Icons (TrueType)", dest);
            return (true, "Installed for your account");
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
