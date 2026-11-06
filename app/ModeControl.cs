using LegionHelper.Hardware;
using Microsoft.Win32;

namespace LegionHelper;

/// <summary>Applies modes, power limits, fan curves and screen settings; re-applies on startup/resume/power change.</summary>
public class ModeControl
{
    public Config Cfg { get; } = Config.Load();
    public Dictionary<uint, FeatureRange> Ranges { get; private set; } = new();
    public event Action? Changed;

    /// <summary>A user-visible mode switch happened (UI, hotkey, hardware button, power source). Not raised at startup/resume.</summary>
    public event Action<ThermalMode>? ModeSwitched;
    bool _quiet = true; // suppress notifications during startup/resume re-apply

    public FeatureRange Range(uint id) =>
        Ranges.TryGetValue(id, out var r) ? r : new FeatureRange(5, 30, 15, 1);

    public ThermalMode CurrentMode { get; private set; } = ThermalMode.Balanced;

    /// <summary>
    /// Built-in (read-only) power limits of a firmware preset, from LENOVO_CAPABILITY_DATA_01 defaults
    /// (IDs 0x01FF_MM00 with MM = mode). Custom returns the user's current values.
    /// </summary>
    public (int spl, int sppt, int fppt)? PresetLimits(ThermalMode m)
    {
        if (m == ThermalMode.Custom) return (Cfg.SPL, Cfg.SPPT, Cfg.FPPT);
        uint Id(uint feature) => 0x01000000u | (feature << 16) | ((uint)m << 8);
        if (!Ranges.TryGetValue(Id(2), out var spl) || !Ranges.TryGetValue(Id(1), out var sppt) || !Ranges.TryGetValue(Id(3), out var fppt))
            return null;
        return (spl.Default, sppt.Default, fppt.Default);
    }

    public string LimitsText(ThermalMode m) =>
        PresetLimits(m) is { } l ? $"SPL {l.spl} W · SPPT {l.sppt} W · FPPT {l.fppt} W" : "limits unknown";

    public void Init()
    {
        Ranges = LenovoWmi.ReadRanges();

        // First run: adopt whatever mode the firmware is in instead of forcing a default.
        if (Cfg.IsNew)
        {
            try { var m = LenovoWmi.GetMode(); if (Enum.IsDefined(m)) Cfg.Mode = (uint)m; } catch { }
        }

        // Seed custom limits from firmware on first run.
        if (Cfg.SPL == 0) Cfg.SPL = LenovoWmi.TryGetFeature(LenovoWmi.CustomSPL) ?? Range(LenovoWmi.CustomSPL).Default;
        if (Cfg.SPPT == 0) Cfg.SPPT = LenovoWmi.TryGetFeature(LenovoWmi.CustomSPPT) ?? Range(LenovoWmi.CustomSPPT).Default;
        if (Cfg.FPPT == 0) Cfg.FPPT = LenovoWmi.TryGetFeature(LenovoWmi.CustomFPPT) ?? Range(LenovoWmi.CustomFPPT).Default;

        if (Cfg.FanCurve is null || Cfg.FanTemps is null)
        {
            try
            {
                var (fan, temp) = LenovoWmi.GetFanTable();
                if (fan.Length == LenovoWmi.FanPoints && temp.Length == LenovoWmi.FanPoints)
                {
                    Cfg.FanCurve = fan; Cfg.FanTemps = temp; Cfg.FactoryFanCurve ??= (int[])fan.Clone();
                }
            }
            catch { }
        }
        Cfg.Save();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        try { LenovoWmi.WatchModeChanges(RefreshMode); } catch { }

        ApplyStartup();
    }

    static bool OnAC => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;

    ThermalMode ModeForPowerSource() => (ThermalMode)((OnAC ? Cfg.ModeAC : Cfg.ModeDC) ?? Cfg.Mode);

    void ApplyStartup()
    {
        _quiet = true;
        SetMode(ModeForPowerSource());
        _quiet = false;
        ApplyRefreshRate();
        ApplyChargePolicy(force: true);
    }

    // ---- Battery charge target ------------------------------------------------------------------
    // The firmware only offers "stop at 80%" (0x03010001). Targets above 80% are emulated: keep the limit off
    // while below the target, turn it on at/above the target (charging stops and the level holds).
    // 0x03010002 was tested on AC (values 1 and 50) and has no effect on charging, so lower targets aren't possible.

    bool? _limitApplied;
    System.Threading.Timer? _chargeTimer;

    /// <summary>Applies the user's charge setting for the current battery level.</summary>
    public void ApplyChargePolicy(bool force = false)
    {
        if (Cfg.ChargeLimit is not bool enabled) return;   // never chosen in this app: leave firmware alone
        int target = Math.Clamp(Cfg.ChargeTarget, 80, 100);
        bool want;
        if (!enabled || target >= 100) want = false;
        else if (target <= 80) want = true;
        else
        {
            int pct = (int)Math.Round(SystemInformation.PowerStatus.BatteryLifePercent * 100);
            // 2% hysteresis so the limit doesn't flap around the target
            want = _limitApplied == true ? pct >= target - 2 : pct >= target;
        }

        // Emulated targets need periodic checks in case battery % events are missed.
        bool emulated = enabled && target is > 80 and < 100;
        if (emulated && _chargeTimer is null) _chargeTimer = new System.Threading.Timer(_ => ApplyChargePolicy(), null, 120_000, 120_000);
        if (!emulated && _chargeTimer is not null) { _chargeTimer.Dispose(); _chargeTimer = null; }

        if (!force && want == _limitApplied) return;
        if (TrySetChargeLimit(want)) _limitApplied = want;
    }

    public void SetChargeSetting(bool enabled, int target)
    {
        Cfg.ChargeLimit = enabled;
        Cfg.ChargeTarget = Math.Clamp(target, 80, 100);
        Cfg.Save();
        ApplyChargePolicy(force: true);
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Task.Delay(3000).ContinueWith(_ => ApplyStartup());
        else if (e.Mode == PowerModes.StatusChange)
        {
            ApplyChargePolicy();   // also fires on battery % changes
            bool ac = OnAC;
            if (ac == _lastAC) return; // StatusChange also fires for battery % changes
            _lastAC = ac;
            Log.Write($"Power source: {(ac ? "AC" : "battery")}");
            // The firmware reports different custom power-limit ranges on AC and on battery.
            try { Ranges = LenovoWmi.ReadRanges(); } catch { }
            var mode = ModeForPowerSource();
            if (mode != CurrentMode) Task.Run(() => SetMode(mode));
            ApplyRefreshRate();
        }
    }

    bool _lastAC = OnAC;

    /// <summary>Re-read mode after a firmware event (e.g. hardware button).</summary>
    public void RefreshMode()
    {
        try
        {
            var prev = CurrentMode;
            CurrentMode = LenovoWmi.GetMode();
            if (Enum.IsDefined(CurrentMode) && CurrentMode != prev)
            {
                // Changed outside the app (hardware button): remember it for this power source.
                Cfg.Mode = (uint)CurrentMode;
                if (OnAC) Cfg.ModeAC = Cfg.Mode; else Cfg.ModeDC = Cfg.Mode;
                Cfg.Save();
                // Changed outside the app (Legion button): give the new mode the right fan table / limits.
                if (CurrentMode == ThermalMode.Custom) { try { ApplyCustom(); } catch { } }
                else ApplyFanTableFor(CurrentMode);
                if (!_quiet) ModeSwitched?.Invoke(CurrentMode);
            }
        }
        catch { }
        Changed?.Invoke();
    }

    /// <summary>User picked a mode: remember it for the current power source, then apply.</summary>
    public void SelectMode(ThermalMode mode)
    {
        if (OnAC) Cfg.ModeAC = (uint)mode; else Cfg.ModeDC = (uint)mode;
        SetMode(mode);
    }

    public void SetMode(ThermalMode mode)
    {
        Cfg.Mode = (uint)mode;
        Cfg.Save();
        try
        {
            LenovoWmi.SetMode(mode);
            if (mode == ThermalMode.Custom)
            {
                Thread.Sleep(150);
                ApplyCustom();
            }
            else ApplyFanTableFor(mode);
            CurrentMode = LenovoWmi.GetMode();
            if (!_quiet) ModeSwitched?.Invoke(CurrentMode);
        }
        catch (Exception ex) { Log.Write("SetMode failed: " + ex.Message); }
        Changed?.Invoke();
    }

    /// <summary>Next mode in the hotkey cycle: Quiet → Balanced → Performance (→ Custom) → Quiet.</summary>
    public void CycleMode()
    {
        var order = new List<ThermalMode> { ThermalMode.Quiet, ThermalMode.Balanced, ThermalMode.Performance };
        if (Cfg.CycleIncludesCustom) order.Add(ThermalMode.Custom);
        int i = order.IndexOf(CurrentMode);
        SelectMode(order[(i + 1) % order.Count]);
    }

    /// <summary>Writes custom power limits (SPL ≤ SPPT ≤ FPPT) and the fan curve, if enabled.</summary>
    public void ApplyCustom()
    {
        var rSpl = Range(LenovoWmi.CustomSPL);
        var rSppt = Range(LenovoWmi.CustomSPPT);
        var rFppt = Range(LenovoWmi.CustomFPPT);

        Cfg.SPL = Math.Clamp(Cfg.SPL, rSpl.Min, rSpl.Max);
        Cfg.SPPT = Math.Clamp(Math.Max(Cfg.SPPT, Cfg.SPL), rSppt.Min, rSppt.Max);
        Cfg.FPPT = Math.Clamp(Math.Max(Cfg.FPPT, Cfg.SPPT), rFppt.Min, rFppt.Max);
        Cfg.Save();

        LenovoWmi.SetFeature(LenovoWmi.CustomSPL, Cfg.SPL);
        LenovoWmi.SetFeature(LenovoWmi.CustomSPPT, Cfg.SPPT);
        LenovoWmi.SetFeature(LenovoWmi.CustomFPPT, Cfg.FPPT);
        Log.Write($"Custom limits SPL={Cfg.SPL} SPPT={Cfg.SPPT} FPPT={Cfg.FPPT}");
        ApplyFanTableFor(ThermalMode.Custom);
    }

    /// <summary>Factory fan table (read from the firmware on first run); fallback = value observed on the Go S.</summary>
    public int[] FactoryCurve => Cfg.FactoryFanCurve is { Length: LenovoWmi.FanPoints } f ? f : [25, 27, 32, 36, 41, 45, 51, 55, 59, 64];

    /// <summary>
    /// The firmware has ONE fan table shared by all modes (verified on device: the last written table drives
    /// Quiet/Balanced/Performance too). So write the user's curve only where it belongs and the factory table otherwise.
    /// </summary>
    public void ApplyFanTableFor(ThermalMode mode)
    {
        if (Cfg.FanTemps is not { Length: LenovoWmi.FanPoints } temps) return;
        bool mine = Cfg.ApplyFanCurve && Cfg.FanCurve is { Length: LenovoWmi.FanPoints }
                    && (mode == ThermalMode.Custom || Cfg.FanCurveAllModes);
        var curve = mine ? Cfg.FanCurve! : FactoryCurve;
        try
        {
            LenovoWmi.SetFanTable(curve, temps);
            Log.Write($"Fan table ({(mine ? "custom" : "factory")}) for {mode}: " + string.Join(",", curve));
        }
        catch (Exception ex) { Log.Write("Fan table write failed: " + ex.Message); }
    }

    public void ApplyRefreshRate()
    {
        if (Cfg.RefreshRate < 0) return;
        int hz = Cfg.RefreshRate;
        if (hz == 0)
        {
            if (SystemControl.GetLowHighRates() is not { } lh) return;
            bool onAC = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
            hz = onAC ? lh.high : lh.low;
        }
        SystemControl.SetRefreshRate(hz);
        Changed?.Invoke();
    }

    /// <summary>
    /// Sets the 80% charge limit. The firmware's getter for this ID always returns 0 on the Go S,
    /// so success is judged by the WMI call alone (verified by observing charging stop/resume).
    /// </summary>
    public bool TrySetChargeLimit(bool on)
    {
        try
        {
            LenovoWmi.SetFeature(LenovoWmi.ChargeLimit, on ? 1 : 0);
            Log.Write($"Charge limit {(on ? "on" : "off")}");
            return true;
        }
        catch (Exception ex) { Log.Write("Charge limit failed: " + ex.Message); return false; }
    }
}

public static class Log
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LegionHelper", "log.txt");

    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 256 * 1024) File.Delete(FilePath);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
