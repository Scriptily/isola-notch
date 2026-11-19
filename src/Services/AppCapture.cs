using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Isola.App;

namespace Isola.Services;

[GeneratedComInterface, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
public partial interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, nint format, nint session);
    [PreserveSig] int GetBufferSize(out uint n);
    [PreserveSig] int GetStreamLatency(out long l);
    [PreserveSig] int GetCurrentPadding(out uint p);
    [PreserveSig] int IsFormatSupported(int mode, nint f, out nint closest);
    [PreserveSig] int GetMixFormat(out nint f);
    [PreserveSig] int GetDevicePeriod(out long d, out long m);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint h);
    [PreserveSig] int GetService(in Guid iid, out nint o);
}

[GeneratedComInterface, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
public partial interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out long pos, out long qpc);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[GeneratedComInterface, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
public partial interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int hr, out nint iface);
}

[GeneratedComInterface, Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
public partial interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
}

/// <summary>Marks the handler free-threaded: Windows calls it back on its own thread.</summary>
[GeneratedComInterface, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
public partial interface IAgileObject { }

[GeneratedComClass]
internal sealed partial class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public readonly ManualResetEventSlim Done = new();
    public int Hr = -1; public nint Iface;
    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
    {
        try { op.GetActivateResult(out Hr, out Iface); } catch (Exception ex) { Hr = ex.HResult; }
        Done.Set();
        return 0;
    }
}

/// <summary>
/// What one app actually sends to the speakers, measured by capturing its audio the way a recorder would
/// (Windows' process loopback) and keeping only the loudness: nothing is recorded, kept or sent anywhere.
/// The audio session meters can't see an app's own volume control: Spotify at 0% in its app read full
/// on them while silent, so the island showed it as playing and its visualizer moved to the music.
/// </summary>
internal sealed unsafe partial class ProcessCapture : IDisposable
{
    public readonly uint Pid;
    volatile float blockPeak;
    volatile bool alive = true;
    public bool Alive => alive;
    public double LastUsed;
    readonly Thread thread;
    IAudioClient client;
    IAudioCaptureClient capture;

    [StructLayout(LayoutKind.Sequential)] struct LoopbackParams { public uint ActivationType, TargetProcessId, ProcessLoopbackMode; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct BlobVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public uint cbSize; [FieldOffset(16)] public nint pBlobData; }
    [StructLayout(LayoutKind.Sequential)] struct WaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSec; public ushort BlockAlign, Bits, Extra; }

    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ActivateAudioInterfaceAsync(string path, in Guid iid, BlobVariant* parms, nint handler, out nint op);

    ProcessCapture(uint pid, IAudioClient c, IAudioCaptureClient cap)
    {
        Pid = pid; client = c; capture = cap;
        thread = new Thread(Run) { IsBackground = true, Name = "app level " + pid, Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>Starts measuring <paramref name="pid"/> and the processes it started; null where Windows can't (before Windows 11, no audio device).</summary>
    public static ProcessCapture TryStart(uint pid, StrategyBasedComWrappers cw)
    {
        nint pHandler = 0, op = 0;
        try
        {
            var lp = new LoopbackParams { ActivationType = 1 /* process loopback */, TargetProcessId = pid, ProcessLoopbackMode = 0 /* include its children */ };
            var blob = new BlobVariant { vt = 65 /* VT_BLOB */, cbSize = (uint)sizeof(LoopbackParams), pBlobData = (nint)(&lp) };
            var handler = new ActivationHandler();
            nint unk = cw.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
            var hiid = typeof(IActivateAudioInterfaceCompletionHandler).GUID;
            int qhr = Marshal.QueryInterface(unk, in hiid, out pHandler);
            Marshal.Release(unk);
            if (qhr < 0) return null;
            int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, &blob, pHandler, out op);
            if (hr < 0 || !handler.Done.Wait(3000) || handler.Hr < 0 || handler.Iface == 0) { Log.D("audio", $"app level capture unavailable for pid {pid} (0x{(hr < 0 ? hr : handler.Hr):X8})"); return null; }
            var client = (IAudioClient)cw.GetOrCreateObjectForComInstance(handler.Iface, CreateObjectFlags.None);
            Marshal.Release(handler.Iface);
            var fmt = new WaveFormat { Tag = 3 /* float */, Channels = 2, Rate = 48000, Bits = 32, BlockAlign = 8, BytesPerSec = 48000 * 8 };
            hr = client.Initialize(0, 0x00020000 /* loopback */ | 0x80000000 /* convert */, 2_000_000, 0, (nint)(&fmt), 0);
            if (hr < 0) { Log.D("audio", $"app level capture for pid {pid} didn't start (0x{hr:X8})"); return null; }
            if (client.GetService(typeof(IAudioCaptureClient).GUID, out nint pc) < 0) return null;
            var cap = (IAudioCaptureClient)cw.GetOrCreateObjectForComInstance(pc, CreateObjectFlags.None);
            Marshal.Release(pc);
            if (client.Start() < 0) return null;
            return new ProcessCapture(pid, client, cap);
        }
        catch (Exception ex) { Log.D("audio", $"app level capture for pid {pid} failed: {ex.Message}"); return null; }
        finally { if (pHandler != 0) Marshal.Release(pHandler); if (op != 0) Marshal.Release(op); }
    }

    /// <summary>The loudest sample in the last ~50 ms of the app's own sound, 0 to 1.</summary>
    public float Peak => alive ? blockPeak : 0;

    void Run()
    {
        float block = 0; int blockFrames = 0;
        try
        {
            while (alive)
            {
                Thread.Sleep(15);
                bool any = false;
                while (alive && capture.GetNextPacketSize(out uint n) >= 0 && n > 0)
                {
                    if (capture.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _) < 0) { alive = false; break; }
                    any = true;
                    if ((flags & 2) == 0)   // not flagged silent
                    {
                        float* f = (float*)data;
                        for (uint i = 0; i < frames * 2; i++) { float v = f[i] < 0 ? -f[i] : f[i]; if (v > block) block = v; }
                    }
                    capture.ReleaseBuffer(frames);
                    blockFrames += (int)frames;
                    if (blockFrames >= 2400) { blockPeak = Math.Min(block, 1); block = 0; blockFrames = 0; }   // 50 ms at 48 kHz
                }
                if (!any) { blockPeak = 0; block = 0; blockFrames = 0; }   // nothing rendered at all: silent
            }
        }
        catch { alive = false; }
    }

    public void Dispose()
    {
        alive = false;
        try { thread.Join(500); } catch { }
        try { client?.Stop(); } catch { }
    }
}

public sealed partial class AudioService
{
    readonly Dictionary<uint, ProcessCapture> captures = new();
    readonly HashSet<uint> capturing = new();
    double capturesSwept;

    /// <summary>
    /// The app's own sound as it leaves for the speakers (its volume control applied), from capturing its
    /// processes; null until a capture is running, or where Windows can't do it (the meters then stand in).
    /// </summary>
    public float? CapturedPeak(string aumid, string appName, double now)
    {
        float? best = null;
        foreach (var pid in metered.Where(m => m.Pid != 0 && AppMatches(aumid, appName, m.Exe, m.Name)).Select(m => m.Pid).Distinct())
        {
            ProcessCapture c;
            lock (captures) captures.TryGetValue(pid, out c);
            if (c == null) { StartCapture(pid); continue; }
            if (!c.Alive) continue;
            c.LastUsed = now;
            best = Math.Max(best ?? 0, c.Peak);
        }
        if (now - capturesSwept > 5) SweepCaptures(now);
        return best;
    }

    void StartCapture(uint pid)
    {
        lock (captures) { if (!capturing.Add(pid)) return; }
        Task.Run(() =>
        {
            var c = ProcessCapture.TryStart(pid, cw);
            lock (captures)
            {
                if (c != null) { c.LastUsed = double.PositiveInfinity; captures[pid] = c; Log.D("audio", $"measuring the sound of pid {pid}"); }
                else capturing.Remove(pid);   // tried and failed: the meters stand in (it's tried again after the next sweep)
            }
        });
    }

    /// <summary>Stops measuring apps nothing has asked about for 15 s, and ones that have closed.</summary>
    void SweepCaptures(double now)
    {
        capturesSwept = now;
        List<ProcessCapture> done;
        lock (captures)
        {
            foreach (var c in captures.Values) if (double.IsPositiveInfinity(c.LastUsed)) c.LastUsed = now;
            done = captures.Values.Where(c => !c.Alive || now - c.LastUsed > 15).ToList();
            foreach (var c in done) { captures.Remove(c.Pid); capturing.Remove(c.Pid); }
        }
        foreach (var c in done) { Log.D("audio", $"stopped measuring pid {c.Pid}"); Task.Run(c.Dispose); }
    }
}
