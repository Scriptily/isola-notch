using System.Runtime.InteropServices;

namespace Isola.App;

/// <summary>
/// Whether Isola runs from its MSIX package (the Microsoft Store version) or as
/// the GitHub install. The Store version is updated by the Store, so it never
/// updates itself (the Store's policies don't allow it either), and it starts
/// with Windows through the package's startup task instead of a sign-in task
/// and Run entry.
/// </summary>
public static partial class Packaging
{
    [LibraryImport("kernel32.dll")] private static partial int GetCurrentPackageFullName(ref int length, nint name);
    const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    static readonly bool packaged = Detect();

    /// <summary>Stands in for the real answer in tests, and for screenshots (ISOLA_PRETEND_STORE=1).</summary>
    internal static bool? Override = Environment.GetEnvironmentVariable("ISOLA_PRETEND_STORE") == "1" ? true : null;

    public static bool IsStore => Override ?? packaged;

    /// <summary>Really running from the package, whatever ISOLA_PRETEND_STORE says (screenshots must never uninstall anything).</summary>
    public static bool IsPackaged => packaged;

    static bool Detect()
    {
        try { int len = 0; return GetCurrentPackageFullName(ref len, 0) != APPMODEL_ERROR_NO_PACKAGE; }
        catch { return false; }
    }

    /// <summary>The startup task declared in the package manifest (store/AppxManifest.xml).</summary>
    public const string StartupTaskId = "IsolaStartup";

    /// <summary>Turns the package's startup task on or off. Off the UI thread; never throws.</summary>
    public static async Task SyncStartupTaskAsync(bool want)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            var state = task.State;
            if (want && state == Windows.ApplicationModel.StartupTaskState.Disabled)
            {
                var now = await task.RequestEnableAsync();
                Log.I("app", "start with Windows (Store startup task): " + now);
            }
            else if (want && state is Windows.ApplicationModel.StartupTaskState.DisabledByUser or Windows.ApplicationModel.StartupTaskState.DisabledByPolicy)
                Log.W("app", "start with Windows is turned off in Windows' Startup apps (" + state + "); only you can turn it back on there");
            else if (!want && state is Windows.ApplicationModel.StartupTaskState.Enabled)
            {
                task.Disable();
                Log.I("app", "start with Windows (Store startup task): off");
            }
        }
        catch (Exception ex) { Log.W("app", "couldn't update the Store startup task: " + ex.Message); }
    }
}
