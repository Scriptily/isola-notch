using System.Runtime.InteropServices;

namespace Isola.Native;

/// <summary>
/// Windows' own folder picker (IFileOpenDialog with FOS_PICKFOLDERS), the one
/// every app uses. Called through its vtable directly: a handful of calls, no
/// marshalling layer, and it works the same under NativeAOT. Runs on the UI
/// thread, which is an STA.
/// </summary>
public static unsafe partial class FolderPicker
{
    static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    static readonly Guid IID_IFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");
    static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
    const uint SIGDN_FILESYSPATH = 0x80058000;
    const int CANCELLED = unchecked((int)0x800704C7);

    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint ctx, in Guid iid, out nint obj);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int SHCreateItemFromParsingName(string path, nint bc, in Guid iid, out nint item);

    static nint* V(nint o) => *(nint**)o;
    static void Release(nint o) { if (o != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)V(o)[2])(o); }

    /// <summary>The folder chosen, or null if cancelled (or the picker couldn't open).</summary>
    public static string Pick(nint owner, string title, string startIn = null)
    {
        nint dlg = 0, start = 0, result = 0;
        try
        {
            if (CoCreateInstance(CLSID_FileOpenDialog, 0, 1 /* CLSCTX_INPROC_SERVER */, IID_IFileOpenDialog, out dlg) < 0) return null;
            var v = V(dlg);
            uint opts = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint*, int>)v[10])(dlg, &opts);                                   // GetOptions
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)v[9])(dlg, opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);   // SetOptions
            fixed (char* t = title) ((delegate* unmanaged[Stdcall]<nint, char*, int>)v[17])(dlg, t);             // SetTitle
            if (!string.IsNullOrEmpty(startIn) && Directory.Exists(startIn) && SHCreateItemFromParsingName(startIn, 0, IID_IShellItem, out start) >= 0)
                ((delegate* unmanaged[Stdcall]<nint, nint, int>)v[12])(dlg, start);                              // SetFolder
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)v[3])(dlg, owner);                          // Show
            if (hr < 0) return null;   // cancelled, or failed
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)v[20])(dlg, &result) < 0 || result == 0) return null;   // GetResult
            char* path = null;
            if (((delegate* unmanaged[Stdcall]<nint, uint, char**, int>)V(result)[5])(result, SIGDN_FILESYSPATH, &path) < 0 || path == null) return null;   // IShellItem.GetDisplayName
            try { return new string(path); }
            finally { Marshal.FreeCoTaskMem((nint)path); }
        }
        catch { return null; }
        finally { Release(result); Release(start); Release(dlg); }
    }
}
