using System.IO.Compression;

namespace Isola.Services;

/// <summary>
/// Just enough PNG to hand shell icons to code that takes encoded images
/// (alert pictures go through WIC's decoder). 8-bit RGBA, no filtering.
/// </summary>
public static class Png
{
    /// <summary>
    /// Encodes 32-bit BGRA pixels, top row first. Shell icons come premultiplied
    /// (a half-transparent edge pixel is darkened by its alpha); PNG wants
    /// straight alpha, so those are divided back out.
    /// </summary>
    /// <summary>
    /// Windows' clipboard bitmap (a BITMAPINFOHEADER and its pixels, 24 or 32 bits) as top-down BGRA; null for
    /// anything else. Copied 32-bit bitmaps often leave alpha at 0 meaning opaque, so an all-zero alpha is made opaque.
    /// </summary>
    public static byte[] DibToBgra(byte[] dib, out int w, out int h)
    {
        w = h = 0;
        if (dib == null || dib.Length < 40) return null;
        int hdr = BitConverter.ToInt32(dib, 0), width = BitConverter.ToInt32(dib, 4), height = BitConverter.ToInt32(dib, 8);
        int bits = BitConverter.ToUInt16(dib, 14), comp = BitConverter.ToInt32(dib, 16), colors = BitConverter.ToInt32(dib, 32);
        if (width <= 0 || height == 0 || width > 16384 || Math.Abs(height) > 16384 || bits is not (24 or 32) || comp is not (0 or 3)) return null;
        int off = hdr + (comp == 3 && hdr == 40 ? 12 : 0) + colors * 4;
        bool bottomUp = height > 0; int hh = Math.Abs(height);
        int stride = ((width * bits + 31) / 32) * 4;
        if (off + (long)stride * hh > dib.Length) return null;
        var o = new byte[width * hh * 4];
        bool anyAlpha = false;
        for (int y = 0; y < hh; y++)
        {
            int src = off + (bottomUp ? hh - 1 - y : y) * stride, dst = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = src + x * (bits / 8);
                o[dst + x * 4] = dib[s]; o[dst + x * 4 + 1] = dib[s + 1]; o[dst + x * 4 + 2] = dib[s + 2];
                byte a = bits == 32 ? dib[s + 3] : (byte)255;
                o[dst + x * 4 + 3] = a; anyAlpha |= a != 0;
            }
        }
        if (!anyAlpha) for (int i = 3; i < o.Length; i += 4) o[i] = 255;
        w = width; h = hh;
        return o;
    }

    public static byte[] Encode(byte[] bgra, int w, int h, bool premultiplied = true)
    {
        var raw = new byte[h * (w * 4 + 1)];
        for (int y = 0; y < h; y++)
        {
            int o = y * (w * 4 + 1);
            raw[o] = 0;   // filter: none
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4, d = o + 1 + x * 4;
                byte b = bgra[s], g = bgra[s + 1], r = bgra[s + 2], a = bgra[s + 3];
                if (premultiplied && a > 0 && a < 255)
                {
                    r = (byte)Math.Min(255, r * 255 / a); g = (byte)Math.Min(255, g * 255 / a); b = (byte)Math.Min(255, b * 255 / a);
                }
                raw[d] = r; raw[d + 1] = g; raw[d + 2] = b; raw[d + 3] = a;
            }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) zs.Write(raw);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 6;   // 8 bits, RGBA
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        var crc = new byte[4]; BE(crc, 0, Crc(td)); s.Write(crc);
    }

    static uint[] table;
    internal static uint Crc(byte[] data)
    {
        if (table == null)
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
            table = t;
        }
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
