using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Isola.App;
using Isola.Native;

namespace Isola.Services;

[StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

[GeneratedComInterface, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
public partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
}

[GeneratedComInterface, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
public partial interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, int flags, out nint hbitmap);
}

[GeneratedComInterface, Guid("000214e4-0000-0000-c000-000000000046")]
public partial interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(nint hmenu, uint index, uint first, uint last, uint flags);
    [PreserveSig] int InvokeCommand(nint info);
    [PreserveSig] int GetCommandString(nuint cmd, uint type, nint reserved, nint name, uint cch);
}

/// <summary>Explorer things: reveal, verbs, the real context menu, icons.</summary>
public static unsafe partial class Shell
{
    static readonly StrategyBasedComWrappers cw = new();
    static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");

    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (Win32.SHParseDisplayName(path, 0, out nint pidl, 0, out _) == 0 && pidl != 0)
        {
            try { Win32.SHOpenFolderAndSelectItems(pidl, 0, null, 0); } finally { Win32.CoTaskMemFree(pidl); }
        }
        else Win32.ShellExecute("explorer.exe", $"/select,\"{path}\"");
    }

    static T Item<T>(string path) where T : class
    {
        if (Win32.SHCreateItemFromParsingName(path, 0, typeof(T).GUID, out nint p) != 0 || p == 0) return null;
        var o = (T)cw.GetOrCreateObjectForComInstance(p, CreateObjectFlags.None);
        Marshal.Release(p);
        return o;
    }

    static IContextMenu Menu(string path)
    {
        var item = Item<IShellItem>(path);
        if (item == null) return null;
        if (item.BindToHandler(0, BHID_SFUIObject, typeof(IContextMenu).GUID, out nint p) != 0 || p == 0) return null;
        var m = (IContextMenu)cw.GetOrCreateObjectForComInstance(p, CreateObjectFlags.None);
        Marshal.Release(p);
        return m;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CMINVOKECOMMANDINFO { public int cbSize; public uint fMask; public nint hwnd; public nint lpVerb; public nint lpParameters; public nint lpDirectory; public int nShow; public uint dwHotKey; public nint hIcon; }

    /// <summary>Invoke a canonical verb ("properties", "runas", "pintohome", "taskbarpin", "uninstall") through the item's own menu, as Explorer does.</summary>
    public static bool Verb(string path, string verb, nint hwnd)
    {
        try
        {
            var m = Menu(path);
            if (m == null) return false;
            nint hm = Win32.CreatePopupMenu();
            try
            {
                m.QueryContextMenu(hm, 0, 1, 0x7FFF, 0x100 /*CMF_EXTENDEDVERBS*/);
                nint v = Marshal.StringToHGlobalAnsi(verb);
                try
                {
                    var ci = new CMINVOKECOMMANDINFO { cbSize = sizeof(CMINVOKECOMMANDINFO), hwnd = hwnd, lpVerb = v, nShow = 1, fMask = 0x400 /*CMIC_MASK_FLAG_NO_UI*/ };
                    int hr = m.InvokeCommand((nint)(&ci));
                    Log.I("shell", $"verb '{verb}' on {path} -> 0x{hr:X8}");
                    return hr >= 0;
                }
                finally { Marshal.FreeHGlobal(v); }
            }
            finally { Win32.DestroyMenu(hm); }
        }
        catch (Exception ex) { Log.W("shell", $"verb '{verb}' failed: {ex.Message}"); return false; }
    }

    /// <summary>Show the item's full Explorer context menu at a screen point.</summary>
    public static void ContextMenu(string path, nint hwnd, int x, int y)
    {
        try
        {
            var m = Menu(path);
            if (m == null) return;
            nint hm = Win32.CreatePopupMenu();
            try
            {
                m.QueryContextMenu(hm, 0, 1, 0x7FFF, 0x100 | 0x0 /*CMF_NORMAL|EXTENDEDVERBS*/);
                Win32.SetForegroundWindow(hwnd);
                int cmd = Win32.TrackPopupMenuEx(hm, 0x100 /*TPM_RETURNCMD*/ | 0x2 /*TPM_RIGHTBUTTON*/, x, y, hwnd, 0);
                if (cmd > 0)
                {
                    var ci = new CMINVOKECOMMANDINFO { cbSize = sizeof(CMINVOKECOMMANDINFO), hwnd = hwnd, lpVerb = cmd - 1, nShow = 1 };
                    int hr = m.InvokeCommand((nint)(&ci));
                    Log.I("shell", $"context command {cmd} on {path} -> 0x{hr:X8}");
                }
            }
            finally { Win32.DestroyMenu(hm); }
        }
        catch (Exception ex) { Log.W("shell", "context menu failed: " + ex.Message); }
    }

    /// <summary>The item's icon as premultiplied BGRA pixels (size x size), or null.</summary>
    public static (byte[] px, int w, int h)? Icon(string path, int size)
    {
        try
        {
            var f = Item<IShellItemImageFactory>(path);
            if (f == null) return null;
            if (f.GetImage(new SIZE { cx = size, cy = size }, 0x1 /*SIIGBF_BIGGERSIZEOK*/ | 0x4 /*ICONONLY*/, out nint hbm) != 0 || hbm == 0) return null;
            try
            {
                BITMAP bm;
                Win32.GetObjectW(hbm, sizeof(BITMAP), &bm);
                int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
                var px = new byte[w * h * 4];
                var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                nint dc = Win32.CreateCompatibleDC(0);
                fixed (byte* p = px) Win32.GetDIBits(dc, hbm, 0, (uint)h, p, &bi, 0);
                Win32.DeleteDC(dc);
                bool anyAlpha = false;
                for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
                if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
                return (px, w, h);
            }
            finally { Win32.DeleteObject(hbm); }
        }
        catch { return null; }
    }
}
