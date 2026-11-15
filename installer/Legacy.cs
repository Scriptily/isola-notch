using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace IsolaSetup;

/// <summary>
/// Moving an install of the app from before it was named Isola ("Dynamic
/// Island", in %LOCALAPPDATA%\Programs\DynamicIsland with its data in
/// %LOCALAPPDATA%\DynamicIsland) onto Isola, once, keeping everything the
/// user set up.
///
/// The old island is asked to quit (it restores Windows' popups as it goes);
/// its data folder's files move into Isola's, the config renamed, and the
/// settings that no longer exist (a GitHub token among them) are dropped so
/// no credential lingers; then its program folder, sign-in task, startup
/// entry, Start menu shortcut and uninstall entry are removed.
/// </summary>
static class Legacy
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    internal static string OldDir = Path.Combine(Local, "Programs", "DynamicIsland");
    internal static string OldData = Path.Combine(Local, "DynamicIsland");
    const string OldClass = "DynamicIsland.Input", OldProcess = "DynamicIsland", OldRunValue = "DynamicIsland";
    const string OldUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland";
    static string OldTask => "Dynamic Island (" + Environment.UserName + ")";
    static string OldLnk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Dynamic Island.lnk");

    public static bool Present => Directory.Exists(OldDir) || Directory.Exists(OldData);

    /// <summary>Stops, moves and removes the old install. Every step is best effort and logged.</summary>
    public static void Migrate(string newData, Func<string, nint> findWindow, Action<nint> askToQuit, Action<string> log)
    {
        if (!Present) return;
        log("moving an install of Dynamic Island to Isola");
        // 1. the old island quits by itself (and restores Windows' popups) when asked
        nint h;
        while ((h = findWindow(OldClass)) != 0) { askToQuit(h); Thread.Sleep(150); if (findWindow(OldClass) == h) break; }
        foreach (var p in Process.GetProcessesByName(OldProcess))
        {
            try
            {
                if (p.MainModule?.FileName is not { } f || !f.StartsWith(OldDir + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (!p.WaitForExit(8000)) { log($"old island {p.Id} didn't exit; ending it"); p.Kill(); p.WaitForExit(3000); }
            }
            catch { }
        }
        // 2. its data
        MoveData(OldData, newData, log);
        // 3. its traces
        try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); k?.DeleteValue(OldRunValue, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(OldUninstallKey, false); } catch { }
        try { if (File.Exists(OldLnk)) File.Delete(OldLnk); } catch { }
        try
        {
            using var t = Process.Start(new ProcessStartInfo("schtasks.exe", $"/delete /tn \"{OldTask}\" /f") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            t?.WaitForExit(10000);
        }
        catch { }
        for (int i = 0; i < 10 && Directory.Exists(OldDir); i++) { try { Directory.Delete(OldDir, true); } catch { Thread.Sleep(500); } }
        log(Directory.Exists(OldDir) ? "couldn't remove " + OldDir : "old install removed");
    }

    /// <summary>The old data folder's files into the new one (existing new files win), then the old folder goes.</summary>
    internal static void MoveData(string oldData, string newData, Action<string> log)
    {
        if (!Directory.Exists(oldData)) return;
        Directory.CreateDirectory(newData);
        foreach (var src in Directory.EnumerateFiles(oldData, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(oldData, src);
            string name = Path.GetFileName(rel);
            if (name.StartsWith("dynamic_island.log", StringComparison.OrdinalIgnoreCase) || name.Equals("setup.log", StringComparison.OrdinalIgnoreCase)) continue;   // old logs stay behind
            if (name.Equals("dynamic_island.config.json", StringComparison.OrdinalIgnoreCase)) rel = Path.Combine(Path.GetDirectoryName(rel) ?? "", "isola.config.json");
            string dst = Path.Combine(newData, rel);
            if (File.Exists(dst)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (Path.GetFileName(dst) == "isola.config.json") File.WriteAllText(dst, CleanConfig(File.ReadAllText(src)));
                else File.Copy(src, dst);
            }
            catch (Exception ex) { log($"couldn't move {rel}: {ex.Message}"); return; }   // keep the old folder if anything didn't make it
        }
        try { Directory.Delete(oldData, true); log("settings moved to " + newData); }
        catch (Exception ex) { log("old data folder kept: " + ex.Message); }
    }

    /// <summary>The config without settings that no longer exist (the GitHub token above all).</summary>
    internal static string CleanConfig(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return json;
            foreach (var gone in new[] { "UpdateToken", "UpdateRepo" }) o.Remove(gone);
            return o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch { return json; }
    }
}
