using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Isola.Shared;

namespace Isola.Tests;

/// <summary>Replacing the GitHub install with the Store version and back (Switchover).</summary>
public static partial class Scenarios
{
    static string TempDir(string name)
    {
        string d = Path.Combine(Path.GetTempPath(), "isola-switch-" + name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        return d;
    }

    static void Put(string dir, string rel, string text)
    {
        string f = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, text);
    }

    static string Get(string dir, string rel) => File.Exists(Path.Combine(dir, rel)) ? File.ReadAllText(Path.Combine(dir, rel)) : null;

    // Windows' publisher ID: the first 8 bytes of SHA-256 over the UTF-16 publisher, as 13 base-32 characters
    static string PublisherId(string publisher)
    {
        byte[] h = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong bits = 0;
        for (int i = 0; i < 8; i++) bits = bits << 8 | h[i];
        const string abc = "0123456789abcdefghjkmnpqrstvwxyz";
        var sb = new StringBuilder();
        for (int i = 0; i < 13; i++)
        {
            int shift = 64 - 5 * (i + 1);
            int v = shift >= 0 ? (int)(bits >> shift & 31) : (int)(bits << -shift & 31);
            sb.Append(abc[v]);
        }
        return sb.ToString();
    }

    [Scenario("the Store package's family name matches the Partner Center publisher in store/identity.json")]
    public static void SwitchoverFamilyMatchesIdentity()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Isola.csproj"))) dir = Path.GetDirectoryName(dir);
        True(dir != null, "repository root found");
        var id = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "store", "identity.json")))!;
        Eq((string)id["Name"] + "_" + PublisherId((string)id["Publisher"]), Switchover.StoreFamily, "family name");
    }

    // what closing the wizard leaves in this thread's queue
    static bool ClosingLeavesQuit(string other)
    {
        var t = typeof(App.SetupWizard);
        var w = Activator.CreateInstance(t, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, [null], null);
        var otherField = t.GetField("other", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        otherField.SetValue(w, Enum.Parse(otherField.FieldType, other));
        while (Native.Win32.PeekMessageW(out _, 0, Native.Win32.WM_QUIT, Native.Win32.WM_QUIT, Native.Win32.PM_REMOVE)) { }
        t.GetMethod("Handle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, [0x0002u /*WM_DESTROY*/, (nint)0, (nint)0]);
        bool left = false;
        while (Native.Win32.PeekMessageW(out _, 0, Native.Win32.WM_QUIT, Native.Win32.WM_QUIT, Native.Win32.PM_REMOVE)) left = true;
        return left;
    }

    [Scenario("the Store version's replace question leaves no quit message for the island that starts after it")]
    public static void SwitchoverQuestionDoesntEndTheIsland()
    {
        True(!ClosingLeavesQuit("GitHub"), "closing the question left WM_QUIT behind: the island would exit the moment it was ready");
        True(ClosingLeavesQuit("None"), "the setup wizard, a process of its own, still ends its message loop with WM_QUIT");
    }

    [Scenario("a GitHub install counts only with its uninstaller beside it")]
    public static void SwitchoverGitHubNeedsItsSetup()
    {
        string d = TempDir("gh");
        try
        {
            True(!Switchover.GitHubInstalled(d), "empty folder");
            Put(d, "Isola.exe", "x");
            True(!Switchover.GitHubInstalled(d), "no IsolaSetup.exe: nothing could uninstall it");
            Put(d, "IsolaSetup.exe", "x");
            True(Switchover.GitHubInstalled(d), "both there");
        }
        finally { Directory.Delete(d, true); }
    }

    [Scenario("replacing the Store version keeping settings: its own copy wins, logs stay behind, then the package goes")]
    public static void SwitchoverStoreKeep()
    {
        string data = TempDir("data"), store = TempDir("store");
        var order = new List<string>();
        try
        {
            Put(data, "isola.config.json", "shared");
            Put(data, "notes.json", "only in the shared folder");
            Put(store, "isola.config.json", "store");
            Put(store, @"shelf\pic.png", "png");
            Put(store, "isola.log", "store log");
            var (ok, _) = Switchover.ReplaceStore(true, data, store, () => { order.Add("remove:" + Get(data, "isola.config.json")); return true; }, _ => { });
            True(ok, "replaced");
            Eq("store", Get(data, "isola.config.json"), "the Store copy is what the user last saw");
            Eq("only in the shared folder", Get(data, "notes.json"), "files only in the shared folder stay");
            Eq("png", Get(data, @"shelf\pic.png"), "folders come along");
            Eq(null, Get(data, "isola.log"), "the Store version's log isn't copied");
            Eq("remove:store", string.Join(",", order), "the package goes only after its settings are copied");
        }
        finally { Directory.Delete(data, true); Directory.Delete(store, true); }
    }

    [Scenario("a Store copy that can't be read leaves the Store version installed and the settings untouched")]
    public static void SwitchoverStoreCopyFails()
    {
        string data = TempDir("data"), store = TempDir("store");
        bool removed = false;
        try
        {
            Put(data, "isola.config.json", "shared");
            Put(store, "isola.config.json", "store");
            using (new FileStream(Path.Combine(store, "isola.config.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var (ok, detail) = Switchover.ReplaceStore(true, data, store, () => removed = true, _ => { });
                True(!ok, "reported as failed");
                True(detail.Contains("left installed"), "says the Store version stays: " + detail);
            }
            True(!removed, "the package isn't removed when its settings didn't make it");
            Eq("shared", Get(data, "isola.config.json"), "settings untouched");
        }
        finally { Directory.Delete(data, true); Directory.Delete(store, true); }
    }

    [Scenario("replacing the Store version from scratch clears settings only once the package is really gone")]
    public static void SwitchoverStoreFresh()
    {
        string data = TempDir("data"), store = TempDir("store");
        try
        {
            Put(data, "isola.config.json", "shared");
            Put(data, @"shelf\a.png", "a");
            Put(data, "setup.log", "the running setup's log");
            var (ok, _) = Switchover.ReplaceStore(false, data, store, () => false, _ => { });
            True(!ok, "removal failed");
            Eq("shared", Get(data, "isola.config.json"), "nothing cleared while the Store version is still there");

            (ok, _) = Switchover.ReplaceStore(false, data, store, () => true, _ => { });
            True(ok, "replaced");
            Eq(null, Get(data, "isola.config.json"), "settings cleared");
            True(!Directory.Exists(Path.Combine(data, "shelf")), "shelf cleared");
            Eq("the running setup's log", Get(data, "setup.log"), "the setup's own log stays");
        }
        finally { Directory.Delete(data, true); Directory.Delete(store, true); }
    }

    [Scenario("the Store version replacing GitHub: its uninstaller runs quietly and keeps the data; fresh clears all but the open log; a failed uninstall clears nothing")]
    public static void SwitchoverGitHub()
    {
        string gh = TempDir("gh"), data = TempDir("data");
        var runs = new List<string>();
        try
        {
            Put(data, "isola.config.json", "settings");
            Put(data, "isola.log", "log in use");
            int code = 0;
            int Run(string exe, string args) { runs.Add(Path.GetFileName(exe) + " " + args); return code; }

            var (ok, _) = Switchover.ReplaceGitHub(true, gh, data, Run, _ => { }, Path.Combine(data, "isola.log"));
            True(ok, "replaced");
            Eq("IsolaSetup.exe /uninstall /silent", runs.Single(), "its own uninstaller, quietly, never with /wipe");
            Eq("settings", Get(data, "isola.config.json"), "kept: the Store version uses this folder");

            code = 1;
            (ok, _) = Switchover.ReplaceGitHub(false, gh, data, Run, _ => { }, Path.Combine(data, "isola.log"));
            True(!ok, "a failed uninstall is reported");
            Eq("settings", Get(data, "isola.config.json"), "nothing cleared when the GitHub install is still there");

            code = 0;
            (ok, _) = Switchover.ReplaceGitHub(false, gh, data, Run, _ => { }, Path.Combine(data, "isola.log"));
            True(ok, "replaced from scratch");
            Eq(null, Get(data, "isola.config.json"), "settings cleared");
            Eq("log in use", Get(data, "isola.log"), "the log the island has open stays");
            // its /wipe would delete the folder under the Store island's open log; clearing is done here instead
            Eq(3, runs.Count(r => r == "IsolaSetup.exe /uninstall /silent"), "never with /wipe, kept or not: " + string.Join(" | ", runs));
        }
        finally { Directory.Delete(gh, true); Directory.Delete(data, true); }
    }
}
