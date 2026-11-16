using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Isola.Shared;

/// <summary>
/// Replacing one kind of install with the other: the GitHub install
/// (%LOCALAPPDATA%\Programs\Isola) and the Microsoft Store package. Only one
/// island runs per session, so having both just means the second never starts.
/// Shared by the island (its setup wizard, and the Store version's first start)
/// and the installer's plain dialogs.
///
/// Where each keeps its data, as measured on Windows 11 with the Store package
/// (no unvirtualizedResources): the Store version reads and writes
/// %LOCALAPPDATA%\Isola directly when that folder already exists (a GitHub
/// install made it), and otherwise Windows gives it a private copy under
/// Packages\&lt;family&gt;\LocalCache\Local\Isola, which goes when the package is
/// removed. Its own registry writes stay private, but programs it starts run
/// outside the package, so the GitHub uninstaller it runs really removes the
/// sign-in task, uninstall entry and shortcut, and restores Windows' banners.
/// </summary>
public static unsafe partial class Switchover
{
    public const string StorePackage = "Scriptily.IsolaNotch";
    /// <summary>Package name plus the hash of the Partner Center publisher (store/identity.json); a test recomputes it.</summary>
    public const string StoreFamily = StorePackage + "_wvq12cd32gztr";

    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Data => Path.Combine(Local, "Isola");
    public static string GitHubDir => Path.Combine(Local, "Programs", "Isola");
    /// <summary>The Store version's private data, used when %LOCALAPPDATA%\Isola didn't exist before it.</summary>
    public static string StoreData => Path.Combine(Local, "Packages", StoreFamily, "LocalCache", "Local", "Isola");

    /// <summary>A GitHub install that can uninstall itself (its setup sits beside it).</summary>
    public static bool GitHubInstalled(string dir = null) =>
        File.Exists(Path.Combine(dir ?? GitHubDir, "Isola.exe")) && File.Exists(Path.Combine(dir ?? GitHubDir, "IsolaSetup.exe"));

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetPackagesByPackageFamily(string family, ref uint count, nint names, ref uint bufferLength, nint buffer);

    public static bool StoreInstalled()
    {
        try
        {
            uint n = 0, len = 0;
            int r = GetPackagesByPackageFamily(StoreFamily, ref n, 0, ref len, 0);
            return (r == 0 || r == 122 /*ERROR_INSUFFICIENT_BUFFER*/) && n > 0;
        }
        catch { return false; }
    }

    // logs belong to the copy that wrote them; settings, notes, shelf and tokens move
    static bool Skipped(string name) =>
        name.StartsWith("isola.log", StringComparison.OrdinalIgnoreCase) || name.Equals("setup.log", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies the Store version's private data over the shared folder: what it
    /// shows is its private copy first, so that copy wins. False if any file
    /// didn't make it (the caller then leaves the Store version installed).
    /// </summary>
    public static bool MergeData(string from, string into, Action<string> log)
    {
        if (!Directory.Exists(from)) return true;
        try
        {
            foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(from, src);
                if (Skipped(Path.GetFileName(rel))) continue;
                string dst = Path.Combine(into, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
            }
            log("settings copied from the Store version");
            return true;
        }
        catch (Exception ex) { log("couldn't copy the Store version's settings: " + ex.Message); return false; }
    }

    /// <summary>Empties the data folder, apart from files still in use by whoever is asking (its log).</summary>
    public static void WipeData(string data, Action<string> log, params string[] except)
    {
        if (!Directory.Exists(data)) return;
        foreach (var f in Directory.EnumerateFiles(data))
            if (!except.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                try { File.Delete(f); } catch (Exception ex) { log($"couldn't remove {Path.GetFileName(f)}: {ex.Message}"); }
        foreach (var d in Directory.EnumerateDirectories(data))
            try { Directory.Delete(d, true); } catch (Exception ex) { log($"couldn't remove {Path.GetFileName(d)}: {ex.Message}"); }
        log("settings removed");
    }

    /// <summary>
    /// The GitHub setup replacing the Store version: settings first (kept or
    /// cleared), then the package. Nothing is cleared unless the package is gone.
    /// </summary>
    public static (bool ok, string detail) ReplaceStore(bool keep, string data, string storeData, Func<bool> removePackage, Action<string> log)
    {
        log("replacing the Microsoft Store version" + (keep ? ", keeping settings" : ""));
        if (keep && !MergeData(storeData, data, log)) return (false, "Couldn't copy its settings, so it was left installed");
        if (!removePackage()) return (false, "Couldn't remove it; uninstall Isola Notch in Windows' Installed apps");
        if (!keep) WipeData(data, log, "setup.log");
        return (true, keep ? "Removed; your settings carried over" : "Removed, with its settings");
    }

    /// <summary>
    /// The Store version replacing the GitHub install: its own uninstaller
    /// closes it, gives Windows' banners back and removes it, keeping the data
    /// folder the Store version then uses. Starting fresh clears that folder
    /// afterwards, apart from the log the caller has open.
    /// </summary>
    public static (bool ok, string detail) ReplaceGitHub(bool keep, string githubDir, string data, Func<string, string, int> run, Action<string> log, string openLog = null)
    {
        log("replacing the GitHub install" + (keep ? ", keeping settings" : ""));
        int code = run(Path.Combine(githubDir, "IsolaSetup.exe"), "/uninstall /silent");
        if (code != 0) { log("its uninstaller returned " + code); return (false, $"Its uninstaller stopped ({code}); uninstall Isola in Windows' Installed apps"); }
        if (!keep) WipeData(data, log, openLog is null ? [] : [Path.GetFileName(openLog)]);
        return (true, keep ? "Removed; your settings carried over" : "Removed, with its settings");
    }

    // ---- removing the package --------------------------------------------

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowExW(nint parent, nint after, string cls, string name);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint h, out uint pid);
    const uint WM_QUIT_ISLAND = 0x8000 + 9;

    [LibraryImport("kernel32.dll")] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint h);
    [LibraryImport("kernel32.dll")] private static partial int GetPackageFamilyName(nint process, ref uint length, char* name);

    // by the process's package identity, not its folder: a development copy registered from a layout runs from anywhere
    static unsafe bool IsStoreProcess(uint pid)
    {
        nint h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (h == 0) return false;
        try
        {
            char* buf = stackalloc char[128];
            uint len = 128;
            return GetPackageFamilyName(h, ref len, buf) == 0 && new string(buf, 0, (int)Math.Max(0, len - 1)).Equals(StoreFamily, StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Asks the Store island to save and quit, then removes the package (which
    /// ends it if it didn't). A per-user package needs no administrator rights.
    /// </summary>
    public static bool RemoveStorePackage(Action<string> log)
    {
        nint h = 0;
        var asked = new List<int>();
        while ((h = FindWindowExW(0, h, "Isola.Input", null)) != 0)
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (IsStoreProcess(pid)) { PostMessageW(h, WM_QUIT_ISLAND, 0, 0); asked.Add((int)pid); }
        }
        foreach (int pid in asked) try { using var p = Process.GetProcessById(pid); p.WaitForExit(6000); } catch { }
        try
        {
            string ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var psi = new ProcessStartInfo(ps, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Get-AppxPackage -Name {StorePackage} | Remove-AppxPackage\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(120_000)) { try { p.Kill(); } catch { } log("removing the Store package took too long"); }
            else if (err.Result.Trim() is { Length: > 0 } e) log("Remove-AppxPackage: " + e);
        }
        catch (Exception ex) { log("couldn't run Remove-AppxPackage: " + ex.Message); }
        bool gone = !StoreInstalled();
        log(gone ? "Store package removed" : "the Store package is still installed");
        return gone;
    }
}
