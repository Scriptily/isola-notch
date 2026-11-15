using Isola.App;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using WinRT;

namespace Isola.Services;

public sealed class BtDevice
{
    public string Id = "", Name = "", ContainerId = "";
    public bool Connected;
    internal bool AepConnected, AepPresent = true;
    public string Kind = "device";     // headphones, phone, mouse, keyboard, controller, speaker, watch, device
    public int Battery = -1;
    public double ChangedAt;
    public bool IsAirPods => Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || Name.Contains("Beats", StringComparison.OrdinalIgnoreCase);
    public char Glyph => Kind switch
    {
        "headphones" => Render.Glyph.Headphones, "phone" => Render.Glyph.Phone, "keyboard" => Render.Glyph.Keyboard,
        "mouse" => '', "controller" => '', "speaker" => Render.Glyph.Volume3, "watch" => Render.Glyph.Clock, _ => Render.Glyph.Bluetooth,
    };
}

/// <summary>
/// The Bluetooth radio and paired devices, through Windows.Devices.Radios and
/// an association-endpoint DeviceWatcher - both documented and event driven.
/// </summary>
public sealed partial class BluetoothService
{
    readonly IClock clock;
    Radio radio;
    DeviceWatcher watcher, leWatcher;
    readonly Dictionary<string, BtDevice> devices = new();
    public bool? RadioOn { get; internal set; }
    public bool Available => radio != null;
    public IReadOnlyCollection<BtDevice> Devices => devices.Values;
    public event Action<bool> RadioChanged;                 // UI thread
    public event Action<BtDevice, bool> DeviceChanged;      // device, connected; UI thread
    public event Action Changed;
    double startedAt;
    readonly Dictionary<string, long> pending = new();
    public double DebounceSeconds = 2.0;

    const string IsConnected = "System.Devices.Aep.IsConnected";
    const string IsPresent = "System.Devices.Aep.IsPresent";
    const string ContainerKey = "System.Devices.Aep.ContainerId";
    const string Category = "System.Devices.Aep.Category";
    static readonly string[] Props = { IsConnected, IsPresent, ContainerKey, "System.Devices.Aep.Bluetooth.Le.IsConnectable", "System.ItemNameDisplay" };

    public BluetoothService(IClock c) { clock = c; }

    public async Task StartAsync()
    {
        startedAt = clock.Now;
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
            radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (radio != null)
            {
                RadioOn = radio.State == RadioState.On;
                radio.StateChanged += (r, _) => { bool on = r.State == RadioState.On; Ui.Post(() => { if (RadioOn == on) return; RadioOn = on; Log.I("bt", "radio " + (on ? "on" : "off")); RadioChanged?.Invoke(on); Changed?.Invoke(); }); };
                Log.I("bt", $"radio {(RadioOn == true ? "on" : "off")}");
            }
            else Log.I("bt", "no Bluetooth radio found");
        }
        catch (Exception ex) { Log.W("bt", "radio query failed: " + ex.Message); }

        try
        {
            watcher = Make(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            leWatcher = Make(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
            watcher.Start(); leWatcher.Start();
        }
        catch (Exception ex) { Log.E("bt", "device watcher failed; no connect/disconnect alerts", ex); }
    }

    DeviceWatcher Make(string aqs)
    {
        var w = DeviceInformation.CreateWatcher(aqs, WinRtStrings(Props), DeviceInformationKind.AssociationEndpoint);
        // Windows keeps stale association records for devices paired over
        // several protocols (an old iPhone shows "connected" on a record that
        // is not even present). Only connected AND present is a real link.
        w.Added += (_, d) => { var id = d.Id; var name = d.Name; var conn = Bool(d.Properties, IsConnected); var pres = !d.Properties.ContainsKey(IsPresent) || Bool(d.Properties, IsPresent); var cont = d.Properties.TryGetValue(ContainerKey, out var c) ? c?.ToString() : ""; Ui.Post(() => Added(id, name, conn, pres, cont)); };
        w.Updated += (_, u) =>
        {
            bool hc = u.Properties.ContainsKey(IsConnected), hp = u.Properties.ContainsKey(IsPresent);
            if (!hc && !hp) return;
            var id = u.Id; bool? conn = hc ? Bool(u.Properties, IsConnected) : null, pres = hp ? Bool(u.Properties, IsPresent) : null;
            Ui.Post(() => Raw(id, conn, pres));
        };
        w.Removed += (_, u) => { var id = u.Id; Ui.Post(() => { records.Remove(id); if (devices.Remove(id)) Changed?.Invoke(); }); };
        w.EnumerationCompleted += (_, _) => Ui.Post(() => Enumerated = true);
        w.EnumerationCompleted += (_, _) => Ui.Post(() => Log.I("bt", $"{devices.Count} paired device(s); connected: {string.Join(", ", devices.Values.Where(d => d.Connected).Select(d => d.Name))}"));
        return w;
    }

    /// <summary>
    /// Property lists for WinRT calls. Compiled ahead of time, CsWinRT needs to
    /// see these call sites to generate the IIterable&lt;string&gt; marshaller
    /// (CsWinRTAotOptimizerEnabled=Auto in the project does that).
    /// </summary>
    static IEnumerable<string> WinRtStrings(params string[] items) => new List<string>(items);

    static bool Bool(IReadOnlyDictionary<string, object> p, string k) => p.TryGetValue(k, out var v) && v is bool b && b;

    // every association endpoint we have seen, so a device made of several
    // records (classic + LE, or stale ones) is connected if any real one is
    readonly Dictionary<string, (string container, bool conn, bool pres)> records = new();

    // AirPods also pair a Find My record named "AirPods Pro 2 - Find My"; being
    // the longer name it won "friendliest", and the list showed that suffix.
    internal static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        int i = name.IndexOf(" - Find My", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? name[..i].Trim() : name.Trim();
    }

    void Added(string id, string name, bool connected, bool present, string container)
    {
        name = CleanName(name);
        records[id] = (container ?? "", connected, present);
        // one entry per physical device (container); prefer the friendliest name
        var dev = devices.Values.FirstOrDefault(d => !string.IsNullOrEmpty(container) && d.ContainerId == container);
        if (dev == null)
        {
            dev = new BtDevice { Id = id, Name = string.IsNullOrWhiteSpace(name) ? "Bluetooth device" : name, ContainerId = container ?? "", ChangedAt = clock.Now };
            dev.Kind = Classify(dev.Name);
            devices[id] = dev;
            _ = ClassifyAsync(dev);
        }
        else if (!string.IsNullOrWhiteSpace(name) && name.Length > dev.Name.Length && name != "iPhone") dev.Name = name;
        dev.Connected = Real(dev);
        Changed?.Invoke();
    }

    /// <summary>What the association-endpoint watcher's Added event delivers; the tests feed records through here.</summary>
    internal void TestAdd(string id, string name, bool connected, bool present, string container) => Added(id, name, connected, present, container);

    void Raw(string id, bool? conn, bool? pres)
    {
        if (!records.TryGetValue(id, out var r)) return;
        records[id] = (r.container, conn ?? r.conn, pres ?? r.pres);
        var dev = devices.Values.FirstOrDefault(d => d.Id == id || (!string.IsNullOrEmpty(r.container) && d.ContainerId == r.container));
        if (dev == null) return;
        Updated(dev.Id, Real(dev));
    }

    bool Real(BtDevice d) => records.Any(kv => (kv.Key == d.Id || (!string.IsNullOrEmpty(d.ContainerId) && kv.Value.container == d.ContainerId)) && kv.Value.conn && kv.Value.pres);

    void Updated(string id, bool connected)
    {
        if (!devices.TryGetValue(id, out var d)) return;
        if (d.Connected == connected) return;
        d.Connected = connected; d.ChangedAt = clock.Now;
        Changed?.Invoke();
        // Ignore the burst while Windows reconnects everything at startup, and
        // debounce flapping (AirPods hopping between phone and laptop).
        if (clock.Now - startedAt < 6) return;
        if (pending.TryGetValue(id, out var t)) { Ui.Cancel(ref t); pending.Remove(id); }
        long timer = Ui.After(DebounceSeconds, () =>
        {
            pending.Remove(id);
            if (d.Connected != connected) return;
            Log.I("bt", $"{d.Name} {(connected ? "connected" : "disconnected")}");
            if (connected) _ = ReadBattery(d);
            DeviceChanged?.Invoke(d, connected);
        });
        pending[id] = timer;
    }

    static string Classify(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("airpods") || n.Contains("buds") || n.Contains("headphone") || n.Contains("wh-") || n.Contains("wf-") || n.Contains("beats")) return "headphones";
        if (n.Contains("iphone") || n.Contains("phone") || n.Contains("galaxy s") || n.Contains("pixel")) return "phone";
        if (n.Contains("keyboard") || n.Contains("keys")) return "keyboard";
        if (n.Contains("mouse") || n.Contains("mx master") || n.Contains("trackpad")) return "mouse";
        if (n.Contains("controller") || n.Contains("xbox") || n.Contains("dualsense") || n.Contains("dualshock")) return "controller";
        if (n.Contains("speaker") || n.Contains("soundcore") || n.Contains("jbl") || n.Contains("sonos") || n.Contains("homepod")) return "speaker";
        if (n.Contains("watch")) return "watch";
        return "device";
    }

    async Task ClassifyAsync(BtDevice d)
    {
        if (d.Kind != "device") return;
        try
        {
            var bt = await BluetoothDevice.FromIdAsync(d.Id).AsTask().ConfigureAwait(false);
            if (bt == null) return;
            var major = bt.ClassOfDevice.MajorClass;
            string k = major switch
            {
                BluetoothMajorClass.AudioVideo => "headphones",
                BluetoothMajorClass.Phone => "phone",
                BluetoothMajorClass.Peripheral => bt.ClassOfDevice.MinorClass.ToString().Contains("Keyboard") ? "keyboard" : "mouse",
                BluetoothMajorClass.Wearable => "watch",
                _ => "device",
            };
            bt.Dispose();
            Ui.Post(() => { d.Kind = k; Changed?.Invoke(); });
        }
        catch { }
    }

    /// <summary>
    /// Hands-free devices report battery through the PnP property Windows shows
    /// in Settings (DEVPKEY_Bluetooth_Battery). Best effort: many devices do not.
    /// </summary>
    async Task ReadBattery(BtDevice d)
    {
        if (string.IsNullOrEmpty(d.ContainerId)) return;
        try
        {
            const string key = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
            string cid = d.ContainerId.Trim('{', '}');
            var found = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{cid}}}\"", WinRtStrings(key), DeviceInformationKind.Device).AsTask().ConfigureAwait(false);
            foreach (var di in found)
                if (di.Properties.TryGetValue(key, out var v) && v is byte b) { Ui.Post(() => { d.Battery = b; Changed?.Invoke(); }); return; }
        }
        catch (Exception ex) { Log.D("bt", "battery read failed: " + ex.Message); }
    }

    /// <summary>The radio is being switched on or off (it can take a second or two).</summary>
    public bool Switching { get; private set; }
    /// <summary>The watcher has listed every paired device at least once.</summary>
    public bool Enumerated { get; private set; }

    public async Task SetRadio(bool on)
    {
        if (radio == null) return;
        Switching = true; Changed?.Invoke();
        try
        {
            var access = await Radio.RequestAccessAsync().AsTask().ConfigureAwait(false);
            if (access != RadioAccessStatus.Allowed) { Log.W("bt", "radio access " + access); Native.Win32.ShellExecute("ms-settings:bluetooth"); return; }
            await radio.SetStateAsync(on ? RadioState.On : RadioState.Off).AsTask().ConfigureAwait(false);
        }
        catch (Exception ex) { Log.W("bt", "set radio failed: " + ex.Message); }
        finally { Ui.Post(() => { Switching = false; Changed?.Invoke(); }); }
    }
}
