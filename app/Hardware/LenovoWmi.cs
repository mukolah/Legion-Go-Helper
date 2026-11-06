using System.Management;

namespace LegionHelper.Hardware;

/// <summary>Firmware power/thermal modes reported by LENOVO_GAMEZONE_DATA.GetSmartFanMode.</summary>
public enum ThermalMode : uint
{
    Quiet = 1,
    Balanced = 2,
    Performance = 3,
    Custom = 255,
}

public record FeatureRange(int Min, int Max, int Default, int Step);

/// <summary>
/// Lenovo Gaming Zone WMI interface (root\WMI), as used by Legion Space's LegionOSSetting.dll.
/// Feature IDs were taken from that DLL's disassembly and verified on a Legion Go S 8ARP1.
/// See docs/hardware-interface.md.
/// </summary>
public static class LenovoWmi
{
    const string Scope = @"root\WMI";

    // LENOVO_OTHER_METHOD feature IDs: 0xDD FF MM TT (device, feature, mode, type)
    public const uint CustomSPL = 0x0102FF00;   // "CustomTDP" (sustained)
    public const uint CustomSPPT = 0x0101FF00;  // slow / long boost
    public const uint CustomFPPT = 0x0103FF00;  // fast / short boost
    public const uint ChargeLimit = 0x03010001; // "PowerChargeLimit" on/off
    public const uint FullSpeedFan = 0x04020000;
    public const uint FanRpm = 0x04030001;
    public const uint CpuTemp = 0x05040000;
    public const uint CpuVrTemp = 0x05010000;

    public const int FanPoints = 10;

    static readonly object _lock = new();
    static readonly Dictionary<string, ManagementObject> _instances = new();

    static ManagementObject Instance(string cls)
    {
        if (_instances.TryGetValue(cls, out var o)) return o;
        using var searcher = new ManagementObjectSearcher(Scope, $"SELECT * FROM {cls}");
        foreach (ManagementObject mo in searcher.Get())
        {
            _instances[cls] = mo;
            return mo;
        }
        throw new InvalidOperationException($"WMI class {cls} not available");
    }

    static ManagementBaseObject Invoke(string cls, string method, params (string name, object value)[] args)
    {
        lock (_lock)
        {
            var obj = Instance(cls);
            var input = obj.GetMethodParameters(method);
            foreach (var (name, value) in args) input[name] = value;
            return obj.InvokeMethod(method, input, null);
        }
    }

    static uint OutUInt(ManagementBaseObject result, string name) => Convert.ToUInt32(result[name]);

    public static bool IsAvailable()
    {
        try { Instance("LENOVO_GAMEZONE_DATA"); Instance("LENOVO_OTHER_METHOD"); return true; }
        catch { return false; }
    }

    // ---- Modes ---------------------------------------------------------------

    public static ThermalMode GetMode() =>
        (ThermalMode)OutUInt(Invoke("LENOVO_GAMEZONE_DATA", "GetSmartFanMode"), "Data");

    public static void SetMode(ThermalMode mode) =>
        Invoke("LENOVO_GAMEZONE_DATA", "SetSmartFanMode", ("Data", (uint)mode));

    // ---- Generic features ------------------------------------------------------

    public static int GetFeature(uint id) =>
        (int)OutUInt(Invoke("LENOVO_OTHER_METHOD", "GetFeatureValue", ("IDs", id)), "Value");

    public static void SetFeature(uint id, int value) =>
        Invoke("LENOVO_OTHER_METHOD", "SetFeatureValue", ("IDs", id), ("value", (uint)value));

    public static int? TryGetFeature(uint id)
    {
        try { return GetFeature(id); } catch { return null; }
    }

    /// <summary>Reads min/max/default for every ranged feature from LENOVO_CAPABILITY_DATA_01.</summary>
    public static Dictionary<uint, FeatureRange> ReadRanges()
    {
        var map = new Dictionary<uint, FeatureRange>();
        lock (_lock)
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM LENOVO_CAPABILITY_DATA_01");
            foreach (ManagementObject mo in searcher.Get())
            {
                uint id = Convert.ToUInt32(mo["IDs"]);
                map[id] = new FeatureRange(
                    Convert.ToInt32(mo["MinValue"]), Convert.ToInt32(mo["MaxValue"]),
                    Convert.ToInt32(mo["DefaultValue"]), Math.Max(1, Convert.ToInt32(mo["Step"])));
            }
        }
        return map;
    }

    // ---- Fan table ---------------------------------------------------------------

    /// <summary>Returns (fan duty %, temperature °C) for the 10-point curve.</summary>
    public static (int[] fan, int[] temp) GetFanTable()
    {
        var r = Invoke("LENOVO_FAN_METHOD", "Fan_Get_Table", ("FanID", (byte)0), ("SensorID", (byte)0));
        var fan = ((uint[])r["FanTable"]).Select(v => (int)v).ToArray();
        var temp = ((uint[])r["SensorTable"]).Select(v => (int)v).ToArray();
        return (fan, temp);
    }

    /// <summary>
    /// Writes the fan curve. Byte layout copied from Legion Space's WinSdk::SetFanTableEx template:
    /// [0]=0xFF [1]=1 [2..5]=count(10), fan duty u16[10] at 6, [26]=1 [27..30]=count(10), temps u16[10] at 31.
    /// </summary>
    public static void SetFanTable(int[] fan, int[] temp)
    {
        if (fan.Length != FanPoints || temp.Length != FanPoints) throw new ArgumentException("Fan table needs 10 points");
        var b = new byte[64];
        b[0] = 0xFF; b[1] = 1; b[2] = FanPoints;
        b[26] = 1; b[27] = FanPoints;
        for (int i = 0; i < FanPoints; i++)
        {
            b[6 + 2 * i] = (byte)Math.Clamp(fan[i], 0, 100);
            b[31 + 2 * i] = (byte)Math.Clamp(temp[i], 0, 120);
        }
        Invoke("LENOVO_FAN_METHOD", "Fan_Set_Table", ("FanTable", b));
    }

    // ---- Mode change events (hardware Legion button / other apps) -----------------

    public static ManagementEventWatcher WatchModeChanges(Action onChange)
    {
        var watcher = new ManagementEventWatcher(new ManagementScope(Scope),
            new WqlEventQuery("SELECT * FROM LENOVO_GAMEZONE_SMART_FAN_MODE_EVENT"));
        watcher.EventArrived += (_, _) => onChange();
        watcher.Start();
        return watcher;
    }
}
