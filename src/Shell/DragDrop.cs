using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Isola.App;
using Isola.Native;

namespace Isola.Shell;

[StructLayout(LayoutKind.Sequential)] public struct POINTL { public int x, y; }
[StructLayout(LayoutKind.Sequential)] public struct FORMATETC { public ushort cfFormat; public nint ptd; public uint dwAspect; public int lindex; public uint tymed; }
[StructLayout(LayoutKind.Sequential)] public struct STGMEDIUM { public uint tymed; public nint handle; public nint pUnkForRelease; }

[GeneratedComInterface, Guid("00000122-0000-0000-C000-000000000046")]
public partial interface IDropTarget
{
    [PreserveSig] int DragEnter(nint dataObj, uint keys, POINTL pt, ref uint effect);
    [PreserveSig] int DragOver(uint keys, POINTL pt, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(nint dataObj, uint keys, POINTL pt, ref uint effect);
}

[GeneratedComInterface, Guid("00000121-0000-0000-C000-000000000046")]
public partial interface IDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
    [PreserveSig] int GiveFeedback(uint effect);
}

/// <summary>What was dropped on the island.</summary>
public sealed class DropPayload
{
    public List<string> Files = new();
    public string Text, Url, UrlTitle;
    public byte[] Png;
    public bool Any => Files.Count > 0 || !string.IsNullOrWhiteSpace(Text) || !string.IsNullOrWhiteSpace(Url) || Png != null;
}

/// <summary>Anything dragged onto the island: files, highlighted text, links, images.</summary>
[GeneratedComClass]
public sealed partial class DropTarget : IDropTarget
{
    public Action Enter, Leave;
    public Action<DropPayload> Dropped;
    bool accept;
    const uint DROPEFFECT_COPY = 1, DROPEFFECT_LINK = 4;
    static readonly ushort CF_URLW = (ushort)Win32.RegisterClipboardFormatW("UniformResourceLocatorW");
    static readonly ushort CF_PNG = (ushort)Win32.RegisterClipboardFormatW("PNG");

    public int DragEnter(nint dataObj, uint keys, POINTL pt, ref uint effect)
    {
        accept = Has(dataObj, (ushort)Win32.CF_HDROP) || Has(dataObj, 13) || Has(dataObj, CF_URLW) || Has(dataObj, CF_PNG);
        // the shelf keeps a reference or a copy; it never moves the source
        effect = !accept ? 0 : (effect & DROPEFFECT_COPY) != 0 ? DROPEFFECT_COPY : DROPEFFECT_LINK;
        if (accept) Enter?.Invoke();
        return 0;
    }
    public int DragOver(uint keys, POINTL pt, ref uint effect) { effect = !accept ? 0 : (effect & DROPEFFECT_COPY) != 0 ? DROPEFFECT_COPY : DROPEFFECT_LINK; return 0; }
    public int DragLeave() { if (accept) Leave?.Invoke(); accept = false; return 0; }
    public int Drop(nint dataObj, uint keys, POINTL pt, ref uint effect)
    {
        var p = new DropPayload { Files = Files(dataObj) };
        if (p.Files.Count == 0)
        {
            p.Url = Read(dataObj, CF_URLW);
            p.Text = Read(dataObj, 13);
            if (p.Url == null) p.Png = ReadBytes(dataObj, CF_PNG);
            if (p.Url != null && p.Text != null && p.Text.Trim() != p.Url.Trim()) p.UrlTitle = p.Text.Trim();
        }
        effect = p.Any ? DROPEFFECT_COPY : 0;
        accept = false;
        if (p.Any) Dropped?.Invoke(p); else Leave?.Invoke();
        return 0;
    }

    // IDataObject slots, called directly: 3 = GetData, 5 = QueryGetData
    static unsafe int GetData(nint obj, ushort cf, STGMEDIUM* med)
    {
        var fmt = new FORMATETC { cfFormat = cf, dwAspect = 1, lindex = -1, tymed = 1 /*HGLOBAL*/ };
        var getData = (delegate* unmanaged[Stdcall]<nint, FORMATETC*, STGMEDIUM*, int>)(*(*(void***)obj + 3));
        return getData(obj, &fmt, med);
    }

    static unsafe bool Has(nint obj, ushort cf)
    {
        if (obj == 0 || cf == 0) return false;
        var fmt = new FORMATETC { cfFormat = cf, dwAspect = 1, lindex = -1, tymed = 1 };
        var query = (delegate* unmanaged[Stdcall]<nint, FORMATETC*, int>)(*(*(void***)obj + 5));
        return query(obj, &fmt) == 0;
    }

    static unsafe string Read(nint obj, ushort cf)
    {
        if (!Has(obj, cf)) return null;
        var med = new STGMEDIUM();
        if (GetData(obj, cf, &med) < 0 || med.handle == 0) return null;
        try
        {
            nint p = Win32.GlobalLock(med.handle);
            if (p == 0) return null;
            try
            {
                int max = (int)Math.Min((long)Win32.GlobalSize(med.handle) / 2, 200_000);
                var s = new string((char*)p, 0, max);
                int z = s.IndexOf('\0');
                return z >= 0 ? s[..z] : s;
            }
            finally { Win32.GlobalUnlock(med.handle); }
        }
        finally { Win32.ReleaseStgMedium(&med); }
    }

    static unsafe byte[] ReadBytes(nint obj, ushort cf)
    {
        if (!Has(obj, cf)) return null;
        var med = new STGMEDIUM();
        if (GetData(obj, cf, &med) < 0 || med.handle == 0) return null;
        try
        {
            nint p = Win32.GlobalLock(med.handle); if (p == 0) return null;
            try { int n = (int)Math.Min((long)Win32.GlobalSize(med.handle), 50_000_000); var b = new byte[n]; Marshal.Copy(p, b, 0, n); return b; }
            finally { Win32.GlobalUnlock(med.handle); }
        }
        finally { Win32.ReleaseStgMedium(&med); }
    }

    /// <summary>CF_HDROP file list out of an IDataObject.</summary>
    public static unsafe List<string> Files(nint dataObj)
    {
        var list = new List<string>();
        if (dataObj == 0 || !Has(dataObj, (ushort)Win32.CF_HDROP)) return list;
        var med = new STGMEDIUM();
        if (GetData(dataObj, (ushort)Win32.CF_HDROP, &med) < 0 || med.handle == 0) return list;
        try
        {
            uint n = Win32.DragQueryFileW(med.handle, 0xFFFFFFFF, null, 0);
            char* buf = stackalloc char[32768];
            for (uint i = 0; i < n; i++)
            {
                uint len = Win32.DragQueryFileW(med.handle, i, buf, 32768);
                if (len > 0) list.Add(new string(buf, 0, (int)len));
            }
        }
        finally { Win32.ReleaseStgMedium(&med); }
        return list;
    }
}

[GeneratedComClass]
public sealed partial class DropSource : IDropSource
{
    public int QueryContinueDrag(bool escape, uint keys)
    {
        if (escape) return 0x40101;            // DRAGDROP_S_CANCEL
        if ((keys & 1) == 0) return 0x40100;   // left button up: DRAGDROP_S_DROP
        return 0;
    }
    public int GiveFeedback(uint effect) => 0x40102;   // DRAGDROP_S_USEDEFAULTCURSORS
}

public static unsafe class DragOut
{
    static readonly StrategyBasedComWrappers cw = new();

    /// <summary>Drag a stored text snippet or link out into any app.</summary>
    public static void Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            if (Win32.SHCreateDataObject(0, 0, null, 0, new Guid("0000010e-0000-0000-C000-000000000046"), out nint data) != 0 || data == 0) return;
            nuint bytes = (nuint)((text.Length + 1) * 2);
            nint mem = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, bytes);
            char* p = (char*)Win32.GlobalLock(mem);
            fixed (char* src = text) Buffer.MemoryCopy(src, p, (long)bytes, text.Length * 2);
            p[text.Length] = '\0'; Win32.GlobalUnlock(mem);
            var fmt = new FORMATETC { cfFormat = 13, dwAspect = 1, lindex = -1, tymed = 1 };
            var med = new STGMEDIUM { tymed = 1, handle = mem };
            var setData = (delegate* unmanaged[Stdcall]<nint, FORMATETC*, STGMEDIUM*, int, int>)(*(*(void***)data + 7));   // IDataObject::SetData
            setData(data, &fmt, &med, 1);   // the data object owns mem now
            var src2 = new DropSource();
            nint unk = cw.GetOrCreateComInterfaceForObject(src2, CreateComInterfaceFlags.None);
            Marshal.QueryInterface(unk, typeof(IDropSource).GUID, out nint ds);
            try { int hr = Win32.DoDragDrop(data, ds, 1, out uint eff); Log.I("shelf", $"drag out text -> 0x{hr:X8}"); }
            finally { Marshal.Release(ds); Marshal.Release(unk); Marshal.Release(data); }
        }
        catch (Exception ex) { Log.W("shelf", "drag out text failed: " + ex.Message); }
    }

    /// <summary>Start an OLE drag of files from the shelf into any app or folder (copy/link, never move).</summary>
    public static void Files(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        nint parent = 0; var children = new List<nint>(); var full = new List<nint>();
        try
        {
            foreach (var p in paths)
            {
                if (Win32.SHParseDisplayName(p, 0, out nint pidl, 0, out _) != 0 || pidl == 0) continue;
                full.Add(pidl);
            }
            if (full.Count == 0) return;
            parent = Win32.ILClone(full[0]); Win32.ILRemoveLastID(parent);
            foreach (var f in full) children.Add(Win32.ILFindLastID(f));
            nint data;
            fixed (nint* kids = children.ToArray())
                if (Win32.SHCreateDataObject(parent, (uint)children.Count, kids, 0, new Guid("0000010e-0000-0000-C000-000000000046"), out data) != 0 || data == 0) return;
            var src = new DropSource();
            nint unk = cw.GetOrCreateComInterfaceForObject(src, CreateComInterfaceFlags.None);
            Marshal.QueryInterface(unk, typeof(IDropSource).GUID, out nint ds);
            try
            {
                int hr = Win32.DoDragDrop(data, ds, 1 | 4, out uint eff);
                Log.I("shelf", $"drag out {paths.Count} item(s) -> 0x{hr:X8} effect {eff}");
            }
            finally { Marshal.Release(ds); Marshal.Release(unk); Marshal.Release(data); }
        }
        catch (Exception ex) { Log.W("shelf", "drag out failed: " + ex.Message); }
        finally
        {
            if (parent != 0) Win32.ILFree(parent);
            foreach (var f in full) Win32.CoTaskMemFree(f);
        }
    }
}
