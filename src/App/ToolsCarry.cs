namespace Isola.Shared;

/// <summary>
/// The setup's part in downloading (shared with the setup, which compiles this
/// file in). The download tools live in the program folder but aren't in the
/// setup: an update swaps that folder for a new one, so they're carried from
/// the old folder into the new before the old is deleted. Otherwise every
/// update would throw away 130 MB and Isola would fetch it again.
/// </summary>
public static class ToolsCarry
{
    public const string Folder = "download-tools";

    /// <summary>Moves <c>download-tools</c> from the old program folder into the new. Never throws: at worst Isola fetches them again.</summary>
    public static bool Keep(string oldDir, string newDir, Action<string> log)
    {
        string from = Path.Combine(oldDir, Folder), to = Path.Combine(newDir, Folder);
        if (!Directory.Exists(from)) return false;
        try
        {
            if (Directory.Exists(to)) Directory.Delete(to, true);   // a payload never brings its own, but never merge two
            Directory.Move(from, to);
            log("kept the download tools");
            return true;
        }
        catch (Exception ex) { log("couldn't keep the download tools (Isola fetches them again): " + ex.Message); return false; }
    }
}
