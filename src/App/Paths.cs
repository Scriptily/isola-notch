namespace Isola.App;

/// <summary>
/// Everything the island writes lives in one folder. Run from the project, that
/// is the project folder, the way the rest of this repository works; a portable
/// copy keeps it beside the exe; an installed copy keeps it in
/// %LOCALAPPDATA%\Isola, apart from the program files an update replaces.
/// ISOLA_HOME overrides all of these.
/// </summary>
public static class Paths
{
    static string home;

    public static string Home
    {
        get
        {
            if (home != null) return home;
            string env = Environment.GetEnvironmentVariable("ISOLA_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return home = Directory.CreateDirectory(env).FullName;
            // Running from bin\<cfg>\... during development: keep data at the
            // project root, beside the config example, rather than in the build
            // output that a clean would delete.
            string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
            var dir = new DirectoryInfo(exeDir);
            for (var d = dir; d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Isola.csproj"))) return home = d.FullName;
            // A portable copy (portable.txt beside the exe) keeps everything beside itself.
            if (File.Exists(Path.Combine(exeDir, "portable.txt"))) return home = exeDir;
            // Installed: settings, notes, the shelf and logs live apart from the
            // program folder, so an update can replace the program freely and an
            // uninstall can choose to leave your data.
            return home = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Isola")).FullName;
        }
        set => home = value;
    }

    public static string Config => Path.Combine(Home, "isola.config.json");
    public static string LogFile => Path.Combine(Home, "isola.log");
    public static string Notes => Path.Combine(Home, "notes.json");
    public static string Shelf => Path.Combine(Home, "shelf.json");
    public static string SpotifyToken => Path.Combine(Home, "spotify.json");
    public static string TimerFile => Path.Combine(Home, "timer.json");
    /// <summary>Windows banner settings the island switched off, to put back (see WindowsBanners).</summary>
    public static string BannerJournal => Path.Combine(Home, "banners.journal");
    public static string State => Path.Combine(Home, "state.json");
    /// <summary>When feedback was last sent and what (as fingerprints), for the once-a-day and no-repeat limits.</summary>
    public static string FeedbackState => Path.Combine(Home, "feedback.json");
    /// <summary>The downloads and conversions so far (the last hundred).</summary>
    public static string DownloadsHistory => Path.Combine(Home, "downloads.json");

    /// <summary>
    /// yt-dlp, FFmpeg and QuickJS (DownloadTools). Installed: in the program
    /// folder, so uninstalling removes them and the setup carries them across
    /// updates. The Store version, a portable copy and a development build keep
    /// them in their data folder (the Store's is removed with the app).
    /// </summary>
    public static string DownloadTools
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ISOLA_TOOLS") is { Length: > 0 } t) return t;
            string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Isola");
            return !Packaging.IsStore && string.Equals(exeDir, installed, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(exeDir, Isola.Shared.ToolsCarry.Folder) : Path.Combine(Home, Isola.Shared.ToolsCarry.Folder);
        }
    }
}
