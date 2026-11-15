using System.Runtime.InteropServices;
using Isola.App;
using Isola.Native;

namespace Isola.Services;

/// <summary>
/// Answer / decline a Phone Link call by pressing the button on Phone Link's own
/// incoming-call UI through UI Automation (the accessibility API screen readers
/// use - documented and stable). Phone Link keeps owning the call; the island
/// only presses its button. Vtable calls are written out by hand so this stays
/// NativeAOT-safe without a 200-method interop declaration.
/// </summary>
public static unsafe partial class PhoneLink
{
    static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    static readonly Guid IID_IUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
    static readonly Guid IID_Invoke = new("fb377fbe-8ea6-46d5-9c73-6499642d3059");
    [DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid clsid, nint outer, int ctx, in Guid iid, out nint obj);
    [DllImport("oleaut32.dll")] static extern void SysFreeString(nint s);

    static void** V(nint p) => *(void***)p;

    public static bool PressCallButton(bool answer)
    {
        string[] names = answer ? new[] { "Accept", "Answer", "Pick up" } : new[] { "Decline", "Reject", "Hang up", "Dismiss" };
        nint uia = 0;
        try
        {
            if (CoCreateInstance(CLSID_CUIAutomation, 0, 1, IID_IUIAutomation, out uia) < 0 || uia == 0) return false;
            // candidate windows: the toast host and Phone Link's own windows
            var wins = new List<nint>();
            nint h = 0;
            while ((h = Win32.FindWindowExW(0, h, null, null)) != 0)
            {
                if (!Win32.IsWindowVisible(h)) continue;
                string cls = Win32.ClassName(h), title = Win32.WindowText(h);
                Win32.GetWindowThreadProcessId(h, out uint pid);
                string exe = Path.GetFileName(Win32.ProcessPath(pid) ?? "").ToLowerInvariant();
                if (title.Contains("notification", StringComparison.OrdinalIgnoreCase) || exe.Contains("phoneexperiencehost") || exe.Contains("yourphone") || exe.Contains("crossdevice"))
                    wins.Add(h);
            }
            foreach (var w in wins)
                if (PressIn(uia, w, names)) { Log.I("call", $"pressed {(answer ? "answer" : "decline")} via UI Automation"); return true; }
            Log.W("call", "Phone Link's call buttons weren't found");
            return false;
        }
        catch (Exception ex) { Log.W("call", "UI Automation failed: " + ex.Message); return false; }
        finally { if (uia != 0) Marshal.Release(uia); }
    }

    static bool PressIn(nint uia, nint hwnd, string[] names)
    {
        nint el = 0, cond = 0, arr = 0;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)V(uia)[6])(uia, hwnd, &el) < 0 || el == 0) return false;     // ElementFromHandle
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)V(uia)[21])(uia, &cond) < 0) return false;                          // CreateTrueCondition
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)V(el)[6])(el, 4 /*Descendants*/, cond, &arr) < 0 || arr == 0) return false;   // FindAll
            int len = 0;
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)V(arr)[3])(arr, &len);
            for (int i = 0; i < Math.Min(len, 400); i++)
            {
                nint e = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)V(arr)[4])(arr, i, &e) < 0 || e == 0) continue;
                try
                {
                    nint bstr = 0;
                    ((delegate* unmanaged[Stdcall]<nint, nint*, int>)V(e)[23])(e, &bstr);     // get_CurrentName
                    string name = bstr == 0 ? "" : Marshal.PtrToStringBSTR(bstr);
                    if (bstr != 0) SysFreeString(bstr);
                    if (!names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
                    nint pat = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)V(e)[16])(e, 10000 /*Invoke*/, &pat) < 0 || pat == 0) continue;   // GetCurrentPattern
                    try
                    {
                        Marshal.QueryInterface(pat, IID_Invoke, out nint inv);
                        if (inv == 0) continue;
                        int hr = ((delegate* unmanaged[Stdcall]<nint, int>)V(inv)[3])(inv);   // Invoke
                        Marshal.Release(inv);
                        if (hr >= 0) return true;
                    }
                    finally { Marshal.Release(pat); }
                }
                finally { Marshal.Release(e); }
            }
            return false;
        }
        finally { if (arr != 0) Marshal.Release(arr); if (cond != 0) Marshal.Release(cond); if (el != 0) Marshal.Release(el); }
    }
}
