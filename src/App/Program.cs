using System.Runtime.InteropServices;
using Isola.Native;

namespace Isola.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The banner watcher and the uninstaller's restore run before anything
        // else, without the log (the running island owns that file).
        int li = Array.IndexOf(args, "--banner-guard-launch");
        if (li >= 0 && li + 1 < args.Length && Environment.ProcessPath != null)
        {
            Win32.StartDetached(Environment.ProcessPath, "--banner-guard " + args[li + 1]);
            return 0;
        }
        int gi = Array.IndexOf(args, "--banner-guard");
        if (gi >= 0 && gi + 1 < args.Length && int.TryParse(args[gi + 1], out int guarded)) return Services.WindowsBanners.Guard(guarded, Paths.BannerJournal);
        if (args.Contains("--restore-banners")) { Services.WindowsBanners.RestoreJournal(new Services.RegistryBanners(), Paths.BannerJournal); return 0; }

        // The setup wizard: run by IsolaSetup.exe from a temporary copy,
        // with its own log (the running island owns the main one).
        if (args.Contains("--setup-wizard"))
        {
            // in the temp folder: uninstalling may remove the data folder
            Log.Init(Path.Combine(Path.GetTempPath(), "Isola-setup-wizard.log"));
            return SetupWizard.Run(args);
        }

        bool console = args.Contains("--console");
        Log.Echo = console;
        Log.Init(Paths.LogFile);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Crash("unhandled", e.ExceptionObject as Exception); Log.Flush(); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.E("task", "unobserved task fault", e.Exception); e.SetObserved(); };

        // One island per user session. A second launch just wakes the first.
        nint mutex = Win32.CreateMutexW(0, true, "Local\\Isola-" + Environment.UserName);
        bool exists = Marshal.GetLastPInvokeError() == 183 /*ERROR_ALREADY_EXISTS*/;
        if (exists && (args.Contains("--restarted") || args.Contains("--updated")))
        {
            // the copy we replace is on its way out: wait for it, then carry on
            uint w = Win32.WaitForSingleObject(mutex, 15000);
            exists = !(w == 0 || w == 0x80 /*WAIT_ABANDONED*/);
        }
        // The Run backup firing after the sign-in task already started us: no
        // need to wake the running island (that would pop it open at sign-in).
        if (exists && args.Contains(Shared.Autostart.Arg)) { Log.D("app", "already started at sign-in"); Log.Flush(); return 0; }
        if (exists)
        {
            nint other = Win32.FindWindowW("Isola.Input", null);
            if (other != 0) Win32.PostMessageW(other, App.WM_SHOW_EXISTING, 0, 0);
            Log.I("app", "already running; asked the running island to show itself");
            Log.Flush();
            return 0;
        }

        int code;
        try
        {
            var app = new App(args);
            code = app.Run();
        }
        catch (Exception ex)
        {
            Log.Crash("fatal", ex);
            Log.Flush();
            code = 1;
        }
        Log.I("app", $"exit {code}");
        Log.Flush();
        GC.KeepAlive(mutex);
        return code;
    }
}
