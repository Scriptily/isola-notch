using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Isola.App;

namespace Isola.Services;

/// <summary>A process as the media matching sees it: who started it, its program, its Store package, its windows' app ids.</summary>
public sealed class ProcInfo
{
    public uint Pid, Parent;
    public string Exe = "", Name = "", Family;
    public readonly HashSet<string> WindowIds = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Which processes play a media session's sound. Windows names a session's app by an id, not a process,
/// and the sound often comes from another process than the one with the window (a browser plays from a
/// helper it started). Matching by name alone missed apps whose id shares no word with their program
/// (Media Player's id is Microsoft.ZuneMusic, its program Microsoft.Media.Player.exe), so every way an
/// app can be recognized is tried, surest first, on the sound's process and the processes that started it.
/// </summary>
public static partial class MediaApps
{
    /// <summary>
    /// Whether the audio session playing from <paramref name="pid"/> belongs to the media session <paramref name="aumid"/>
    /// (shown as <paramref name="appName"/>), given a way to look up processes.
    /// </summary>
    public static bool Owns(string aumid, string appName, uint pid, Func<uint, ProcInfo> proc)
    {
        if (string.IsNullOrEmpty(aumid)) return false;
        int bang = aumid.IndexOf('!');
        string family = bang > 0 ? aumid[..bang] : null;   // a Store app: "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"
        string id = aumid.ToLowerInvariant();
        var seen = new HashSet<uint>();
        for (uint p = pid; p != 0 && seen.Add(p) && seen.Count <= 8; )
        {
            var pi = proc(p);
            if (pi == null) break;
            // 1. Store apps: the process's package is the app (helpers it starts share it)
            if (family != null && string.Equals(pi.Family, family, StringComparison.OrdinalIgnoreCase)) return true;
            // 2. desktop apps that tag their windows with their id (browsers, Discord, most players)
            if (pi.WindowIds.Contains(aumid)) return true;
            // 3. an id made from the program's name ("Spotify.exe", "MSEdge" for msedge.exe)
            string stem = Path.GetFileNameWithoutExtension(pi.Exe ?? "").ToLowerInvariant();
            if (stem.Length >= 3 && id.Contains(stem)) return true;
            p = pi.Parent;
        }
        // 4. last: the name the media controls show against the program's own description
        var own = proc(pid);
        return own != null && !string.IsNullOrWhiteSpace(appName) && !string.IsNullOrWhiteSpace(own.Name)
            && string.Equals(appName.Trim(), own.Name.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- the live snapshot ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    unsafe struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID; public nint th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        public fixed char szExeFile[260];
    }
    [LibraryImport("kernel32.dll")] private static partial nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW")] [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool Process32First(nint snap, PROCESSENTRY32W* e);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW")] [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool Process32Next(nint snap, PROCESSENTRY32W* e);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint h);
    [LibraryImport("kernel32.dll")] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] private static unsafe partial int GetPackageFamilyName(nint process, ref uint length, char* name);
    [LibraryImport("user32.dll")] private static unsafe partial int EnumWindows(delegate* unmanaged<nint, nint, int> cb, nint lp);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint h, out uint pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(nint h);
    [LibraryImport("shell32.dll")] private static partial int SHGetPropertyStoreForWindow(nint hwnd, in Guid iid, out nint store);
    [LibraryImport("ole32.dll")] private static partial int PropVariantClear(ref PROPVARIANT pv);

    static readonly PROPERTYKEY AppUserModelId = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    /// <summary>Every process, with the app ids of its visible windows; Store packages are looked up as asked for.</summary>
    public static unsafe Dictionary<uint, ProcInfo> Snapshot(StrategyBasedComWrappers cw)
    {
        var map = new Dictionary<uint, ProcInfo>();
        nint snap = CreateToolhelp32Snapshot(0x2 /* processes */, 0);
        if (snap != -1 && snap != 0)
        {
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
                for (bool ok = Process32First(snap, &e); ok; ok = Process32Next(snap, &e))
                    map[e.th32ProcessID] = new ProcInfo { Pid = e.th32ProcessID, Parent = e.th32ParentProcessID, Exe = new string(e.szExeFile) };
            }
            finally { CloseHandle(snap); }
        }
        var windows = new List<nint>();
        var h = GCHandle.Alloc(windows);
        try { EnumWindows(&Collect, GCHandle.ToIntPtr(h)); } finally { h.Free(); }
        var iid = typeof(IPropertyStore).GUID;
        foreach (var w in windows)
        {
            GetWindowThreadProcessId(w, out uint pid);
            if (!map.TryGetValue(pid, out var pi)) continue;
            if (SHGetPropertyStoreForWindow(w, in iid, out nint ps) < 0 || ps == 0) continue;
            try
            {
                var store = (IPropertyStore)cw.GetOrCreateObjectForComInstance(ps, CreateObjectFlags.None);
                if (store.GetValue(AppUserModelId, out var pv) >= 0)
                {
                    if (pv.vt == 31 && pv.p != 0) { string s = Marshal.PtrToStringUni(pv.p); if (!string.IsNullOrEmpty(s)) pi.WindowIds.Add(s); }
                    PropVariantClear(ref pv);
                }
            }
            catch { }
            finally { Marshal.Release(ps); }
        }
        return map;
    }

    [UnmanagedCallersOnly]
    static int Collect(nint hwnd, nint lp)
    {
        if (IsWindowVisible(hwnd)) ((List<nint>)GCHandle.FromIntPtr(lp).Target!).Add(hwnd);
        return 1;
    }

    /// <summary>The process's Store package family, or "" for a desktop program.</summary>
    public static unsafe string FamilyOf(uint pid)
    {
        nint h = OpenProcess(0x1000 /* query limited */, false, pid);
        if (h == 0) return "";
        try
        {
            char* buf = stackalloc char[256];
            uint len = 256;
            return GetPackageFamilyName(h, ref len, buf) == 0 ? new string(buf, 0, (int)Math.Max(0, len - 1)) : "";
        }
        finally { CloseHandle(h); }
    }
}
