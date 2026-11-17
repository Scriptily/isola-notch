using System.Runtime.InteropServices;

namespace Isola.App;

/// <summary>
/// Tokens at rest (the Home Assistant token, the Spotify sign-in), encrypted
/// with Windows' data protection for the signed-in user (DPAPI). A copied
/// config file, or one attached to a bug report, holds nothing another
/// account or computer can use. A plain value (an older config, or one typed into
/// the file by hand) is still read, and is encrypted the next time it's saved.
/// </summary>
public static partial class Secret
{
    const string Prefix = "dpapi:";
    static readonly byte[] Entropy = "Isola token v1"u8.ToArray();

    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public nint Data; }
    [LibraryImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref Blob input, nint description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob output);
    [LibraryImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref Blob input, nint description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob output);
    [LibraryImport("kernel32.dll")] private static partial nint LocalFree(nint h);
    const int UiForbidden = 0x1;

    public static bool IsProtected(string s) => s != null && s.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The value encrypted for this user; empty stays empty. Falls back to the plain value if Windows refuses.</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return plain ?? "";
        var r = Transform(System.Text.Encoding.UTF8.GetBytes(plain), protect: true);
        return r == null ? plain : Prefix + Convert.ToBase64String(r);
    }

    /// <summary>The plain value; a plain input is returned as is. Empty if it was encrypted for another user or computer.</summary>
    public static string Unprotect(string stored)
    {
        if (!IsProtected(stored)) return stored ?? "";
        try
        {
            var r = Transform(Convert.FromBase64String(stored[Prefix.Length..]), protect: false);
            if (r == null) { Log.W("secret", "a saved token can't be read on this account or computer; it needs entering again"); return ""; }
            return System.Text.Encoding.UTF8.GetString(r);
        }
        catch { return ""; }
    }

    static unsafe byte[] Transform(byte[] data, bool protect)
    {
        fixed (byte* d = data) fixed (byte* e = Entropy)
        {
            var input = new Blob { Size = data.Length, Data = (nint)d };
            var entropy = new Blob { Size = Entropy.Length, Data = (nint)e };
            bool ok = protect
                ? CryptProtectData(ref input, 0, ref entropy, 0, 0, UiForbidden, out var output)
                : CryptUnprotectData(ref input, 0, ref entropy, 0, 0, UiForbidden, out output);
            if (!ok) return null;
            try { var r = new byte[output.Size]; Marshal.Copy(output.Data, r, 0, output.Size); return r; }
            finally { LocalFree(output.Data); }
        }
    }
}
