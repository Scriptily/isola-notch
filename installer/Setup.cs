using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32;

namespace IsolaSetup;

/// <summary>
/// Installs, updates and uninstalls Isola for the current user.
///
///   IsolaSetup.exe                 install or update, with a confirmation
///   IsolaSetup.exe /silent         same, no questions
///   IsolaSetup.exe /update /wait N used by the island's updater: waits for
///                                          process N to exit, then updates silently
///   IsolaSetup.exe /uninstall      remove it (asks about your data)
///
/// Program files go to %LOCALAPPDATA%\Programs\Isola; settings, notes,
/// the shelf and logs live in %LOCALAPPDATA%\Isola and survive updates.
/// No administrator rights, nothing outside the user's profile and HKCU.
/// </summary>
public static unsafe partial class Setup
{
    static readonly string Version = typeof(Setup).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Dir = Path.Combine(Local, "Programs", "Isola");
    static readonly string Data = Path.Combine(Local, "Isola");
    static readonly string Exe = Path.Combine(Dir, "Isola.exe");
    static readonly string StartMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Isola.lnk");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Isola";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const uint WM_QUIT_ISLAND = 0x8000 + 9;
    static string logFile;

    [STAThread]
    public static int Main(string[] args)
    {
        var a = args.Select(x => x.ToLowerInvariant().TrimStart('-', '/')).ToList();
        bool silent = a.Contains("silent") || a.Contains("update") || a.Contains("s");
        Directory.CreateDirectory(Data);
        logFile = Path.Combine(Data, "setup.log");
        Log($"setup {Version} started: {string.Join(' ', args)}");
        try
        {
            if (a.Contains("uninstall")) return Uninstall(silent, a.Contains("wipe"));
            int wi = a.IndexOf("wait");
            if (wi >= 0 && wi + 1 < a.Count && int.TryParse(a[wi + 1], out int pid)) WaitFor(pid);
            // Run by hand: the wizard. It installs through this same program
            // (/silent /nolaunch), so there is one install path for everything.
            if (!silent && !a.Contains("classic"))
            {
                int w = Wizard();
                if (w != 3) return w;
                Log("the wizard couldn't open; using the plain dialogs");
            }
            return Install(silent, a.Contains("update"), launch: !a.Contains("nolaunch"), autostart: !a.Contains("noautostart"));
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            if (!silent) Dialog("Isola setup", "Something went wrong", ex.Message + "\n\nDetails are in " + logFile, Icon.Error, false);
            return 1;
        }
    }

    static void Log(string s)
    {
        try { File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}\n"); } catch { }
    }

    static string InstalledVersion()
    {
        try { return File.Exists(Exe) ? FileVersionInfo.GetVersionInfo(Exe).ProductVersion?.Split('+')[0] : null; } catch { return null; }
    }

    /// <summary>
    /// Unpacks the island to a temporary folder and runs its setup wizard from
    /// there, so the wizard is drawn by the island itself. 3 means it couldn't
    /// open (the caller falls back to the plain dialogs).
    /// </summary>
    static int Wizard()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "IsolaSetup-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var zip = typeof(Setup).Assembly.GetManifestResourceStream("payload.zip") ?? throw new InvalidOperationException("payload missing from setup"))
                ZipFile.ExtractToDirectory(zip, tmp, true);
            var exe = Path.Combine(tmp, "Isola.exe");
            using var p = Process.Start(new ProcessStartInfo(exe, $"--setup-wizard --setup \"{Environment.ProcessPath}\"") { UseShellExecute = false, WorkingDirectory = tmp });
            if (p == null) return 3;
            p.WaitForExit();
            Log($"wizard finished: {p.ExitCode}");
            return p.ExitCode;
        }
        catch (Exception ex) { Log("wizard failed to start: " + ex.Message); return 3; }
        // a program that just exited stays locked for a moment (and antivirus may
        // be reading it): keep trying for a while, quietly
        finally { for (int i = 0; i < 30 && Directory.Exists(tmp); i++) { try { Directory.Delete(tmp, true); } catch { Thread.Sleep(500); } } if (Directory.Exists(tmp)) Log("left the temporary folder " + tmp); }
    }

    static int Install(bool silent, bool update, bool launch = true, bool autostart = true)
    {
        string have = InstalledVersion();
        if (!silent)
        {
            string body = have == null
                ? "Installs for your account only - no administrator rights needed.\n\nIt starts with Windows, sits at the top of your laptop screen, and keeps itself up to date once you add a GitHub token in its settings."
                : $"Version {have} is installed. Your settings, notes and shelf are kept.";
            if (!Dialog("Isola setup", have == null ? $"Install Isola {Version}?" : $"Update Isola to {Version}?", body, Icon.Info, true)) return 2;
        }

        StopRunning();
        Legacy.Migrate(Data, cls => FindWindowExW(0, 0, cls, null), h => PostMessageW(h, WM_QUIT_ISLAND, 0, 0), Log);

        // Unpack next to the old copy, then swap: a failure part-way leaves the
        // working install in place.
        string fresh = Dir + ".new", old = Dir + ".old";
        TryDelete(fresh); TryDelete(old);
        using (var zip = typeof(Setup).Assembly.GetManifestResourceStream("payload.zip") ?? throw new InvalidOperationException("payload missing from setup"))
            ZipFile.ExtractToDirectory(zip, fresh, true);
        File.Copy(Environment.ProcessPath!, Path.Combine(fresh, "IsolaSetup.exe"), true);
        if (Directory.Exists(Dir)) Retry(() => Directory.Move(Dir, old), "move the old version aside");
        try { Retry(() => Directory.Move(fresh, Dir), "move the new version in"); }
        catch { if (Directory.Exists(old) && !Directory.Exists(Dir)) Directory.Move(old, Dir); throw; }
        TryDelete(old);
        Log($"files installed to {Dir} ({have ?? "fresh"} -> {Version})");

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Isola");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Scriptily");
            k.SetValue("DisplayIcon", Exe + ",0");
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", $"\"{Path.Combine(Dir, "IsolaSetup.exe")}\" /uninstall");
            k.SetValue("QuietUninstallString", $"\"{Path.Combine(Dir, "IsolaSetup.exe")}\" /uninstall /silent");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(DirSize(Dir) / 1024), RegistryValueKind.DWord);
        }
        // Autostart, unless the user switched it off in the island's settings.
        if (!autostart) { Isola.Shared.Autostart.Disable(Log); MarkAutostart(false); Log("won't start at sign-in (chosen in setup)"); }
        else if (!AutostartDisabled()) Log(Isola.Shared.Autostart.Enable(Exe, Log) ? "starts at sign-in (task)" : "starts at sign-in (Run entry only)");
        try { Shortcut(StartMenuLnk, Exe, "Isola"); } catch (Exception ex) { Log("shortcut failed: " + ex.Message); }

        if (launch)
        {
            Process.Start(new ProcessStartInfo(Exe, update ? "--updated" : "") { UseShellExecute = true, WorkingDirectory = Dir });
            Log("started the island");
        }
        return 0;
    }

    // "Start when I sign in" off in the wizard: the island's own setting says so
    // too, or its first start would turn autostart straight back on.
    static void MarkAutostart(bool on)
    {
        try
        {
            var cfg = Path.Combine(Data, "isola.config.json");
            File.WriteAllText(cfg, Isola.Shared.Autostart.WithSetting(File.Exists(cfg) ? File.ReadAllText(cfg) : "", on));
        }
        catch (Exception ex) { Log("couldn't record the autostart choice: " + ex.Message); }
    }

    static bool AutostartDisabled()
    {
        try
        {
            var cfg = Path.Combine(Data, "isola.config.json");
            return File.Exists(cfg) && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(cfg), "\"StartWithWindows\"\\s*:\\s*false");
        }
        catch { return false; }
    }

    static int Uninstall(bool silent, bool wipeData = false)
    {
        if (!silent && !Dialog("Isola", "Uninstall Isola?", "The island closes and its program files are removed.", Icon.Warning, true)) return 2;
        StopRunning();
        RestoreBanners();
        Isola.Shared.Autostart.Disable(Log);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        TryDeleteFile(StartMenuLnk);
        bool wipe = wipeData || !silent && Dialog("Isola", "Also remove your settings, notes and shelf?", $"They are in {Data}. Keep them if you might reinstall.", Icon.Question, true, "Remove", "Keep");
        if (wipe) TryDelete(Data);
        // this exe is running from the program folder: let cmd remove it after we exit
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{Dir}\"") { CreateNoWindow = true, UseShellExecute = false });
        Log("uninstalled" + (wipe ? " (data removed)" : " (data kept)"));
        return 0;
    }

    // Windows' own notification banners, if the island left any switched off
    // (it puts them back on quit; this covers a crash before uninstalling).
    static void RestoreBanners()
    {
        try
        {
            if (!File.Exists(Path.Combine(Data, "banners.journal")) || !File.Exists(Exe)) return;
            using var p = Process.Start(new ProcessStartInfo(Exe, "--restore-banners") { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(10000);
            Log("restored Windows notification banners");
        }
        catch (Exception ex) { Log("banner restore failed: " + ex.Message); }
    }

    static void StopRunning()
    {
        // ask nicely first: the island saves and exits on this message
        nint h = 0;
        while ((h = FindWindowExW(0, h, "Isola.Input", null)) != 0) PostMessageW(h, WM_QUIT_ISLAND, 0, 0);
        // only the installed island (and its watcher): the setup wizard is a copy
        // of the island too, running from a temp folder, and used to be ended here
        // in the middle of the install it was driving
        var procs = Process.GetProcessesByName("Isola").Where(p =>
        {
            try { return p.MainModule?.FileName is { } f && f.StartsWith(Dir + "\\", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });
        foreach (var p in procs)
        {
            try { if (!p.WaitForExit(6000)) { Log($"pid {p.Id} didn't exit; ending it"); p.Kill(); p.WaitForExit(3000); } }
            catch { }
        }
    }

    static void WaitFor(int pid)
    {
        try { var p = Process.GetProcessById(pid); if (!p.WaitForExit(20000)) { Log($"updater parent {pid} still running; ending it"); p.Kill(); } } catch { }
    }

    static void Retry(Action a, string what)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(250); }       // a file still held by the exiting island
            catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(250); }
            catch (Exception ex) { throw new IOException($"Couldn't {what}: {ex.Message}", ex); }
        }
    }

    static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) { Log($"couldn't delete {dir}: {ex.Message}"); } }
    static void TryDeleteFile(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    static long DirSize(string d) { try { return new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { return 0; } }

    // ---- dialogs (TaskDialog: the modern Windows message box) -------------
    enum Icon { Info, Warning, Error, Question }

    static bool Dialog(string title, string main, string body, Icon icon, bool ask, string yes = null, string no = null)
    {
        int buttons = ask ? 0x1 | 0x8 /*OK|CANCEL*/ : 0x1;
        nint ic = icon switch { Icon.Warning => -1, Icon.Error => -2, Icon.Info => -3, _ => -4 };   // TD_*_ICON
        if (yes != null) { buttons = 0x2 | 0x4; }   // YES|NO
        int hr = TaskDialog(0, 0, title, main, body, buttons, ic, out int res);
        if (hr < 0) return MessageBoxW(0, main + "\n\n" + body, title, ask ? 0x21u : 0x40u) is 1 or 6;
        return res is 1 or 6;   // IDOK / IDYES
    }

    [LibraryImport("comctl32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int TaskDialog(nint owner, nint inst, string title, string main, string content, int buttons, nint icon, out int pressed);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int MessageBoxW(nint h, string text, string caption, uint type);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowExW(nint parent, nint after, string cls, string name);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint h, uint m, nint w, nint l);
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, int ctx, in Guid iid, out nint obj);

    // ---- Start menu shortcut (IShellLink, as Explorer makes them) ---------
    static void Shortcut(string lnk, string target, string description)
    {
        var cw = new StrategyBasedComWrappers();
        int hr = CoCreateInstance(new Guid("00021401-0000-0000-C000-000000000046"), 0, 1, typeof(IShellLinkW).GUID, out nint p);
        if (hr < 0) throw new COMException("ShellLink", hr);
        var link = (IShellLinkW)cw.GetOrCreateObjectForComInstance(p, CreateObjectFlags.None);
        Marshal.Release(p);
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target));
        link.SetDescription(description);
        link.SetIconLocation(target, 0);
        ((IPersistFile)link).Save(lnk, true);
    }
}

[GeneratedComInterface, Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    void GetPath(nint buf, int cch, nint fd, uint flags);
    void GetIDList(out nint pidl);
    void SetIDList(nint pidl);
    void GetDescription(nint buf, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory(nint buf, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    void GetArguments(nint buf, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    void GetHotkey(out ushort hk);
    void SetHotkey(ushort hk);
    void GetShowCmd(out int cmd);
    void SetShowCmd(int cmd);
    void GetIconLocation(nint buf, int cch, out int idx);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int idx);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
    void Resolve(nint hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[GeneratedComInterface, Guid("0000010b-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid clsid);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
    void GetCurFile(out nint file);
}
