using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace LegionHelper.Hardware;

/// <summary>Windows-side controls: refresh rate, Windows power mode overlay, startup task, battery info.</summary>
public static class SystemControl
{
    // ---- Display refresh rate -----------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsExW(string? device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);

    const int ENUM_CURRENT_SETTINGS = -1, DM_DISPLAYFREQUENCY = 0x400000, CDS_UPDATEREGISTRY = 1;

    static string? InternalDisplay() => Screen.PrimaryScreen?.DeviceName;

    static DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    public static int GetRefreshRate()
    {
        var dm = NewMode();
        return EnumDisplaySettingsW(InternalDisplay(), ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 0;
    }

    /// <summary>Refresh rates available at the current resolution, ascending.</summary>
    public static int[] GetRefreshRates()
    {
        var cur = NewMode();
        if (!EnumDisplaySettingsW(InternalDisplay(), ENUM_CURRENT_SETTINGS, ref cur)) return [];
        var rates = new SortedSet<int>();
        var dm = NewMode();
        for (int i = 0; EnumDisplaySettingsW(InternalDisplay(), i, ref dm); i++)
            if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && dm.dmDisplayFrequency > 1)
                rates.Add(dm.dmDisplayFrequency);
        return rates.ToArray();
    }

    /// <summary>Battery-friendly and maximum rates (prefers 60 Hz over odd low rates like 48 Hz).</summary>
    public static (int low, int high)? GetLowHighRates()
    {
        var rates = GetRefreshRates();
        if (rates.Length == 0) return null;
        int low = rates.Contains(60) ? 60 : rates[0];
        return (low, rates[^1]);
    }

    public static bool SetRefreshRate(int hz)
    {
        var dm = NewMode();
        if (!EnumDisplaySettingsW(InternalDisplay(), ENUM_CURRENT_SETTINGS, ref dm)) return false;
        if (dm.dmDisplayFrequency == hz) return true;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_DISPLAYFREQUENCY;
        return ChangeDisplaySettingsExW(InternalDisplay(), ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == 0;
    }

    // ---- Windows power mode (overlay scheme) ----------------------------------------

    public static readonly (string Name, Guid Id)[] PowerModes =
    [
        ("Best Power Efficiency", new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")),
        ("Balanced", Guid.Empty),
        ("Best Performance", new Guid("ded574b5-45a0-4f42-8737-46345c09c238")),
    ];

    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);

    public static Guid GetPowerMode() => PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : Guid.Empty;
    public static void SetPowerMode(Guid id) => PowerSetActiveOverlayScheme(id);

    // ---- Run on startup (elevated scheduled task, like G-Helper) -----------------------

    const string TaskName = "LegionHelper";

    static (int code, string output) Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    public static bool IsStartupEnabled() => Schtasks($"/Query /TN \"{TaskName}\"").code == 0;

    public static void SetStartup(bool enabled, bool hidden = true)
    {
        if (enabled)
        {
            string exe = Environment.ProcessPath!;
            Schtasks($"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /DELAY 0000:05 /TR \"\\\"{exe}\\\"{(hidden ? " -tray" : "")}\"");
        }
        else Schtasks($"/Delete /F /TN \"{TaskName}\"");
    }

    // ---- Battery -------------------------------------------------------------------------

    /// <summary>Net battery power in watts: negative when discharging, positive when charging; null if unknown.</summary>
    public static double? GetBatteryWatts()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT ChargeRate, DischargeRate FROM BatteryStatus");
            foreach (ManagementObject mo in s.Get())
            {
                int charge = Convert.ToInt32(mo["ChargeRate"]);
                int discharge = Convert.ToInt32(mo["DischargeRate"]);
                if (discharge > 0) return -discharge / 1000.0;
                if (charge > 0) return charge / 1000.0;
                return 0;
            }
        }
        catch { }
        return null;
    }
}
