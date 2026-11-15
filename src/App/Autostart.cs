using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace Isola.Shared;

/// <summary>
/// Starting with Windows. Shared by the island and the installer.
///
/// The Run key alone left the island waiting in Windows' queue of sign-in
/// apps: measured, it launched 22 s after sign-in, although it is ready 0.16 s
/// after launch. A Task Scheduler task with a sign-in trigger fires as soon as
/// the session starts. Standard users may register one for themselves (no
/// administrator rights; verified). Task Scheduler gives tasks below-normal
/// priority by default, so it is set to normal.
///
/// The Run entry stays as a backup: if the task is ever missing or disabled,
/// the island still starts, later. When both fire, the second copy sees the
/// first and exits silently (--autostart).
/// </summary>
public static class Autostart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "Isola", Arg = "--autostart";
    public static string TaskName => "Isola (" + Environment.UserName + ")";

    public static string TaskXml(string exe, string user) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Starts Isola when you sign in.</Description></RegistrationInfo>
          <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
            <AllowHardTerminate>true</AllowHardTerminate>
          </Settings>
          <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{Arg}</Arguments></Exec></Actions>
        </Task>
        """;

    public static string User => Environment.UserDomainName + "\\" + Environment.UserName;

    /// <summary>
    /// The island's config text with "StartWithWindows" set: replaced where it
    /// is, added at the top where it isn't, or a new file. Setup writes it when
    /// "Start when I sign in" is off, so the island's first start doesn't turn
    /// autostart straight back on. Everything else in the file is left as it is.
    /// </summary>
    public static string WithSetting(string json, bool on)
    {
        string val = on ? "true" : "false";
        string text = (json ?? "").Trim();
        var re = new System.Text.RegularExpressions.Regex("\"StartWithWindows\"\\s*:\\s*(true|false)");
        if (re.IsMatch(text)) return re.Replace(text, "\"StartWithWindows\": " + val, 1);
        if (text.StartsWith('{') && text.EndsWith('}') && text[1..^1].Trim().Length > 0) return "{\n  \"StartWithWindows\": " + val + "," + text[1..];
        return "{\n  \"StartWithWindows\": " + val + "\n}";
    }

    /// <summary>Registers the sign-in task (replacing an old one) and the Run backup. True if the task is in place.</summary>
    public static bool Enable(string exe, Action<string> log)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            string want = $"\"{exe}\" {Arg}";
            if (k.GetValue(RunValue) as string != want) k.SetValue(RunValue, want);
        }
        catch (Exception ex) { log?.Invoke("Run entry failed: " + ex.Message); }
        string xml = Path.Combine(Path.GetTempPath(), "island-task-" + Environment.ProcessId + ".xml");
        try
        {
            File.WriteAllText(xml, TaskXml(exe, User), System.Text.Encoding.Unicode);
            var (code, err) = Schtasks($"/create /tn \"{TaskName}\" /xml \"{xml}\" /f");
            if (code != 0) { log?.Invoke($"sign-in task not registered ({code}): {err}"); return false; }
            return true;
        }
        catch (Exception ex) { log?.Invoke("sign-in task failed: " + ex.Message); return false; }
        finally { try { File.Delete(xml); } catch { } }
    }

    public static void Disable(Action<string> log)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey, true); k?.DeleteValue(RunValue, false); } catch { }
        if (TaskExists()) { var (code, err) = Schtasks($"/delete /tn \"{TaskName}\" /f"); if (code != 0) log?.Invoke($"sign-in task not removed ({code}): {err}"); }
    }

    public static bool TaskExists() => Schtasks($"/query /tn \"{TaskName}\"").code == 0;

    /// <summary>The executable the registered task starts, or null.</summary>
    public static string TaskTarget()
    {
        var (code, xml) = Schtasks($"/query /tn \"{TaskName}\" /xml");
        if (code != 0) return null;
        int a = xml.IndexOf("<Command>", StringComparison.Ordinal), b = xml.IndexOf("</Command>", StringComparison.Ordinal);
        return a >= 0 && b > a ? System.Net.WebUtility.HtmlDecode(xml[(a + 9)..b].Trim()) : null;
    }

    static (int code, string output) Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi);
        if (p == null) return (-1, "couldn't start schtasks");
        var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (-1, "schtasks timed out"); }
        string err = errTask.Result.Trim();
        return (p.ExitCode, p.ExitCode == 0 ? outTask.Result : err);
    }
}
