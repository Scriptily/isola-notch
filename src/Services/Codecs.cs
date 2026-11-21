using System.Runtime.InteropServices;
using Isola.App;

namespace Isola.Services;

/// <summary>
/// Which video formats this PC can play, so downloads are only offered in ones it can: Windows plays H.264 out of
/// the box, but H.265 needs the HEVC Video Extensions, and VP9 and AV1 their own extensions (or a graphics card
/// that decodes them). Asked of Media Foundation, the same decoders Windows' own players use; once, then kept.
/// </summary>
public static unsafe class Codecs
{
    [StructLayout(LayoutKind.Sequential)]
    struct TypeInfo { public Guid Major, Sub; }

    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFTEnumEx(Guid category, uint flags, TypeInfo* input, TypeInfo* output, nint* activates, uint* count);

    static readonly Guid VideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");   // MFT_CATEGORY_VIDEO_DECODER
    static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");          // MFMediaType_Video
    static Guid FourCC(string cc) => new(BitConverter.ToUInt32(System.Text.Encoding.ASCII.GetBytes(cc)), 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    static readonly Dictionary<string, bool> known = new();
    /// <summary>Tests: what the PC can play, instead of asking it.</summary>
    internal static Func<string, bool> Override;

    /// <summary>Whether a decoder for this codec is installed: "h264", "h265", "vp9", "av1".</summary>
    public static bool CanPlay(string codec)
    {
        if (Override != null) return Override(codec);
        if (codec is "h264" or "any") return true;   // every Windows plays H.264
        lock (known) if (known.TryGetValue(codec, out bool k)) return k;
        bool can = Ask(codec switch { "h265" => "HEVC", "vp9" => "VP90", "av1" => "AV01", _ => null });
        lock (known) known[codec] = can;
        Log.D("codecs", $"{codec}: {(can ? "can play" : "no decoder")}");
        return can;
    }

    static bool Ask(string fourcc)
    {
        if (fourcc == null) return false;
        try
        {
            MFStartup(0x00020070, 0);
            var input = new TypeInfo { Major = Video, Sub = FourCC(fourcc) };
            nint arr = 0; uint n = 0;
            // synchronous, asynchronous and hardware decoders, as Windows' players would find them
            int hr = MFTEnumEx(VideoDecoder, 0x1 | 0x2 | 0x4 | 0x40, &input, null, &arr, &n);
            if (hr < 0) return false;
            for (uint i = 0; i < n; i++) { nint a = ((nint*)arr)[i]; if (a != 0) Marshal.Release(a); }
            if (arr != 0) Marshal.FreeCoTaskMem(arr);
            return n > 0;
        }
        catch (Exception ex) { Log.D("codecs", $"{fourcc}: {ex.Message}"); return false; }
    }
}
