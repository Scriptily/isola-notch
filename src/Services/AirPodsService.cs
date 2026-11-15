using System.Text;
using System.Text.RegularExpressions;
using Isola.App;
using Windows.Devices.Bluetooth.Advertisement;

namespace Isola.Services;

public sealed class AirPodsState
{
    public string Name = "AirPods";
    public int Left = -1, Right = -1, Case = -1;           // percent, -1 unknown
    public bool LeftCharging, RightCharging, CaseCharging;
    public bool? LeftInEar, RightInEar, LidOpen;
    public bool Connected;
    public double UpdatedAt = -1e9;
    public string Source = "";                             // "MagicPods" or "BLE"
    public int Lowest => new[] { Left, Right }.Where(v => v >= 0).DefaultIfEmpty(-1).Min();
}

/// <summary>
/// Live AirPods detail: per-bud and case battery, charging, in-ear and lid.
///
/// Primary source is MagicPods' own service log, read-only: MagicPods already
/// talks to the AirPods properly (and the island never touches its process,
/// settings or IPC). If MagicPods is not installed, not running, or its log
/// stops making sense, the island decodes Apple's BLE "proximity pairing"
/// broadcast itself - the same public advertisement the iPhone's battery
/// popup reads.
/// </summary>
public sealed partial class AirPodsService
{
    readonly IClock clock;
    public AirPodsState State { get; } = new();
    public event Action Changed;                    // UI thread
    public event Action<string> Event;              // "lid-open", "lid-close", "in-ear", "out-ear", "connected", "disconnected"
    public bool MagicPodsFound { get; private set; }
    internal void TestSetMagicPodsFound(bool v) => MagicPodsFound = v;
    string logPath;
    long offset = -1;
    FileSystemWatcher fsw;
    BluetoothLEAdvertisementWatcher ble;
    long pollTimer;
    public Func<bool> UseMagicPods = () => true;
    /// <summary>
    /// Whether MagicPods' service is actually running. Installed but closed, its
    /// log just stops; the island used to wait three minutes for the log to go
    /// stale before reading the AirPods itself. Checked at most every 10 s.
    /// </summary>
    public Func<bool> MagicPodsRunning = () => System.Diagnostics.Process.GetProcessesByName("MagicPodsService").Length > 0;
    bool runningCache; double runningAt = double.NegativeInfinity;
    bool MagicPodsLive()
    {
        if (clock.Now - runningAt > 10) { runningAt = clock.Now; try { runningCache = MagicPodsRunning(); } catch { runningCache = false; } }
        return runningCache;
    }
    /// <summary>MagicPods is the source right now: installed, allowed in settings, running, and its log fresh.</summary>
    internal bool MagicPodsInCharge => MagicPodsFound && UseMagicPods() && MagicPodsLive() && clock.Now - State.UpdatedAt <= 180;
    public Func<bool> AirPodsConnected = () => false;

    public AirPodsService(IClock c) { clock = c; }

    public void Start()
    {
        logPath = FindMagicPodsLog();
        MagicPodsFound = logPath != null;
        if (MagicPodsFound && UseMagicPods())
        {
            Log.I("airpods", "reading MagicPods log: " + logPath);
            try
            {
                offset = Math.Max(0, new FileInfo(logPath).Length - 64 * 1024);   // catch up on recent state
                ReadNew();
                fsw = new FileSystemWatcher(Path.GetDirectoryName(logPath), Path.GetFileName(logPath))
                { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
                fsw.Changed += (_, _) => Ui.Post(ReadNew);
            }
            catch (Exception ex) { Log.W("airpods", "MagicPods log unreadable; using BLE: " + ex.Message); MagicPodsFound = false; }
        }
        else Log.I("airpods", MagicPodsFound ? "MagicPods integration off in settings; using BLE" : "MagicPods not found; using BLE broadcasts");
        // the FileSystemWatcher can miss writes from another package's process; a slow safety poll covers it
        SchedulePoll();
    }

    /// <summary>
    /// Bluetooth's device list changed (AirPods connected or left): decide now.
    /// The check used to wait for its next timer, two minutes when the AirPods
    /// weren't connected yet as the island started - so without MagicPods the
    /// island showed nothing for them for up to two minutes.
    /// </summary>
    public void DevicesChanged()
    {
        bool now = AirPodsConnected();
        if (now == connectedSeen) return;
        connectedSeen = now;
        if (MagicPodsFound && UseMagicPods()) ReadNew();
        EnsureBle();
        SchedulePoll();
    }
    bool connectedSeen;
    internal bool BleRunning => ble != null;

    void SchedulePoll()
    {
        Ui.Cancel(ref pollTimer);
        pollTimer = Ui.After(AirPodsConnected() ? 20 : 120, () => { if (MagicPodsFound) ReadNew(); EnsureBle(); SchedulePoll(); });
    }

    public static string FindMagicPodsLog()
    {
        try
        {
            string pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            foreach (var d in Directory.EnumerateDirectories(pk, "*MagicPods*"))
            {
                var f = Path.Combine(d, "TempState", "MagicPodsServiceLog.txt");
                if (File.Exists(f)) return f;
            }
        }
        catch { }
        return null;
    }

    void ReadNew()
    {
        if (logPath == null) return;
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset) offset = 0;            // rotated / truncated
            if (fs.Length == offset) return;
            fs.Seek(offset, SeekOrigin.Begin);
            int len = (int)Math.Min(fs.Length - offset, 512 * 1024);
            var buf = new byte[len];
            int n = fs.Read(buf, 0, len);
            offset += n;
            var text = Encoding.UTF8.GetString(buf, 0, n);
            int last = text.LastIndexOf('\n');
            if (last < 0) { offset -= n; return; }         // partial line: wait for the rest
            offset -= n - (last + 1);
            bool changed = false;
            foreach (var line in text[..last].Split('\n')) changed |= ParseLine(line.TrimEnd('\r'));
            if (changed) Changed?.Invoke();
        }
        catch (IOException) { }
        catch (Exception ex) { Log.D("airpods", "log read: " + ex.Message); }
    }

    [GeneratedRegex(@"l=(-?\d+),\s*lc=(-?\d+),\s*r=(-?\d+),\s*rc=(-?\d+),\s*c=(-?\d+),\s*cc=(-?\d+)")] private static partial Regex BatteryRx();
    [GeneratedRegex(@"Case (open|close)\s*\(([^)]*)\)\s*:\s*L (in|out)\s+R (in|out)")] private static partial Regex CaseRx();

    /// <summary>Parse one MagicPods log line. Returns true if state changed. Public for tests.</summary>
    public bool ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        // columns: date time, "Srv", level, thread, source(16 chars), message
        var cols = line.Split("    ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (cols.Length < 5) return false;
        string source = cols.Length >= 6 ? cols[^2] : "", msg = cols[^1];
        bool aboutPods = source.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || source.Contains("Beats", StringComparison.OrdinalIgnoreCase);
        if (msg.StartsWith("Apple battery updated", StringComparison.Ordinal))
        {
            var m = BatteryRx().Match(msg);
            if (!m.Success) return false;
            int l = int.Parse(m.Groups[1].Value), lc = int.Parse(m.Groups[2].Value), r = int.Parse(m.Groups[3].Value), rc = int.Parse(m.Groups[4].Value),
                c = int.Parse(m.Groups[5].Value), cc = int.Parse(m.Groups[6].Value);
            if (aboutPods) State.Name = CleanName(source);
            bool changed = State.Left != l || State.Right != r || (c >= 0 && State.Case != c) || State.LeftCharging != (lc == 1) || State.RightCharging != (rc == 1);
            State.Left = l; State.Right = r; if (c >= 0) State.Case = c;
            State.LeftCharging = lc == 1; State.RightCharging = rc == 1; State.CaseCharging = cc == 1;
            State.UpdatedAt = clock.Now; State.Source = "MagicPods"; State.Connected = true;
            return changed;
        }
        var cm = CaseRx().Match(msg);
        if (cm.Success && aboutPods)
        {
            bool open = cm.Groups[1].Value == "open";
            bool li = cm.Groups[3].Value == "in", ri = cm.Groups[4].Value == "in";
            bool lidChanged = State.LidOpen != open;
            bool earChanged = State.LeftInEar != li || State.RightInEar != ri;
            State.LidOpen = open; State.LeftInEar = li; State.RightInEar = ri;
            State.UpdatedAt = clock.Now;
            if (lidChanged) Event?.Invoke(open ? "lid-open" : "lid-close");
            return lidChanged || earChanged;
        }
        if (msg.EndsWith(" has been connected", StringComparison.Ordinal) && (msg.Contains("AirPods") || msg.Contains("Beats")))
        {
            State.Connected = true; State.Name = CleanName(msg[..^" has been connected".Length]); Event?.Invoke("connected"); return true;
        }
        if (msg.EndsWith(" has been disconnected", StringComparison.Ordinal) && (msg.Contains("AirPods") || msg.Contains("Beats")))
        {
            State.Connected = false; Event?.Invoke("disconnected"); return true;
        }
        return false;
    }

    static string CleanName(string s)
    {
        s = s.Trim().TrimEnd('-').Trim();
        int i = s.IndexOf(" - Find My", StringComparison.Ordinal);
        return i > 0 ? s[..i] : s;
    }

    // ---- BLE fallback ---------------------------------------------------------

    void EnsureBle()
    {
        bool want = AirPodsConnected() && !MagicPodsInCharge;
        if (want && ble == null)
        {
            try
            {
                ble = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                ble.SignalStrengthFilter.InRangeThresholdInDBm = -65;   // only our own, close by
                var f = new BluetoothLEManufacturerData { CompanyId = 0x004C };
                ble.AdvertisementFilter.Advertisement.ManufacturerData.Add(f);
                ble.Received += (_, a) =>
                {
                    foreach (var md in a.Advertisement.ManufacturerData)
                    {
                        if (md.CompanyId != 0x004C) continue;
                        var bytes = new byte[md.Data.Length];
                        using (var rd = Windows.Storage.Streams.DataReader.FromBuffer(md.Data)) rd.ReadBytes(bytes);
                        var d = Decode(bytes);
                        if (d != null) Ui.Post(() => Apply(d));
                    }
                };
                ble.Start();
                Log.I("airpods", "BLE watcher started");
            }
            catch (Exception ex) { Log.W("airpods", "BLE watcher failed: " + ex.Message); ble = null; }
        }
        else if (!want && ble != null) { try { ble.Stop(); } catch { } ble = null; Log.I("airpods", "BLE watcher stopped"); }
    }

    void Apply(AirPodsState d)
    {
        if (MagicPodsInCharge && State.Source == "MagicPods") return;
        bool changed = d.Left != State.Left || d.Right != State.Right || d.Case != State.Case || d.LidOpen != State.LidOpen;
        State.Left = d.Left; State.Right = d.Right; if (d.Case >= 0) State.Case = d.Case;
        State.LeftCharging = d.LeftCharging; State.RightCharging = d.RightCharging; State.CaseCharging = d.CaseCharging;
        State.LeftInEar = d.LeftInEar; State.RightInEar = d.RightInEar; State.LidOpen = d.LidOpen;
        State.UpdatedAt = clock.Now; State.Source = "BLE";
        if (changed) { Log.D("airpods", $"from the broadcast: left {State.Left}% right {State.Right}% case {State.Case}%"); Changed?.Invoke(); }
    }

    /// <summary>
    /// Decode Apple's proximity-pairing manufacturer data (type 0x07, 25 bytes).
    /// Battery nibbles are 0-10 (x10%), 15 = unknown; which nibble is left
    /// depends on which bud is "primary" (status bit 0x20). Public for tests.
    /// </summary>
    public static AirPodsState Decode(byte[] d)
    {
        if (d == null || d.Length < 10 || d[0] != 0x07 || d[1] < 0x19 - 2) return null;
        byte status = d[5];
        bool flipped = (status & 0x20) == 0;
        int pods = d[6], hi = (pods >> 4) & 0xF, lo = pods & 0xF;
        int leftN = flipped ? lo : hi, rightN = flipped ? hi : lo;
        int caseN = d[7] & 0xF, chg = (d[7] >> 4) & 0xF;
        static int Pct(int n) => n <= 10 ? n * 10 : -1;
        var s = new AirPodsState
        {
            Left = Pct(leftN), Right = Pct(rightN), Case = Pct(caseN),
            LeftCharging = (chg & (flipped ? 0b10 : 0b01)) != 0, RightCharging = (chg & (flipped ? 0b01 : 0b10)) != 0, CaseCharging = (chg & 0b100) != 0,
            LidOpen = d.Length > 8 ? (d[8] & 0x08) == 0 : null,
            LeftInEar = (status & (flipped ? 0x08 : 0x02)) != 0, RightInEar = (status & (flipped ? 0x02 : 0x08)) != 0,
            Source = "BLE",
        };
        return s;
    }
}
