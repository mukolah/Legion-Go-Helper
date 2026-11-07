using System.Runtime.InteropServices;
using LegionHelper.Hardware;
using Microsoft.Win32;
using static LegionHelper.Hardware.LegionController;

namespace LegionHelper;

/// <summary>
/// RGB engine. "Built-in" effects run in the controller firmware (no CPU). "Live" effects are rendered by
/// Legion Helper into the 6 per-LED lamps (HID LampArray mode) at ~30 fps, only while selected.
/// </summary>
public static class Lighting
{
    public record Effect(string Id, string Name, bool Live, string Hint);

    public static readonly Effect[] Effects =
    [
        new("off", "Off", false, "Lights off."),
        new("solid", "Solid", false, "Built into the controller - works without the app."),
        new("breathe", "Breathe", false, "Built into the controller - works without the app."),
        new("chroma", "Chroma", false, "Built into the controller - works without the app."),
        new("rainbow", "Rainbow", false, "Built into the controller - works without the app."),
        new("temp", "CPU temperature", true, "Blue when cool → green → yellow → red when hot (40-90 °C)."),
        new("wheel", "Color wheel", true, "A rainbow wheel turning around the rings."),
        new("load", "CPU load", true, "Segments fill with CPU usage, green → red."),
        new("comet", "Spinning comet", true, "A comet spins around the rings in your colour."),
        new("battery", "Battery gauge", true, "Segments show the charge level; a sweep runs while charging."),
        new("mode", "Mode color", true, "Shows the performance mode colour (Quiet green, Balanced blue, …)."),
    ];

    public static Effect Find(string? id) => Effects.FirstOrDefault(e => e.Id == id) ?? Effects[1];

    /// <summary>Latest colours of the 6 lamps (for the preview). Lamp N = one LED on each ring.</summary>
    public static readonly Color[] Current = new Color[LampArray.Count];
    public static bool LiveActive => _run;

    /// <summary>Data-driven effects (temp / load / battery) that support the Preview slider.</summary>
    public static bool IsDataEffect(string? id) => id is "temp" or "load" or "battery";

    /// <summary>0..1 while the user drags the Preview slider; overrides the live value of data-driven effects.</summary>
    public static volatile object? PreviewValue;

    /// <summary>Live value of the current data effect as 0..1 (temperature mapped 30-100 °C), for the slider thumb.</summary>
    public static double LiveFraction { get; private set; }
    public static string Status { get; private set; } = "";

    static ModeControl? _mc;
    static Config Cfg => _mc!.Cfg;
    static readonly object _applyLock = new();
    static Thread? _thread;
    static volatile bool _run;
    static long _flashStartMs = -1;
    static Color _flashColor;

    /// <summary>True while the charging display overrides the chosen effect (charger plugged in, option on).</summary>
    public static bool ChargingDisplayActive => _charge;
    static volatile bool _charge;
    static bool OnAC => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
    static bool ChargeWanted => Cfg.RgbChargingDisplay && OnAC;

    public static void Init(ModeControl mc)
    {
        _mc = mc;
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            // The controller resets its lighting mode across sleep; re-enter per-LED mode if a live effect is on.
            // (The charger may also have been plugged in or out while asleep.)
            if (e.Mode == PowerModes.Resume && (_run || ChargeWanted || (Cfg.RgbEffect is not null && Find(Cfg.RgbEffect).Live)))
                Task.Delay(4000).ContinueWith(_ => ApplyCore(restart: true));
            // Charger plugged in / unplugged: switch between the charging display and the chosen effect.
            else if (e.Mode == PowerModes.StatusChange && ChargeWanted != _charge)
                Task.Run(Apply);
        };
    }

    /// <summary>Applies Cfg.RgbEffect, or the charging display while plugged in (call from a background thread;
    /// may take a few seconds when switching modes).</summary>
    public static void Apply() => ApplyCore(restart: false);

    static void ApplyCore(bool restart)
    {
        lock (_applyLock)
        {
            bool wasCharge = _charge;
            _charge = ChargeWanted;
            // RgbEffect null = never chosen in this app → leave the controller alone (unless we drove it ourselves)
            if (Cfg.RgbEffect is null && !_charge && !_run) return;
            bool wasLive = _run;
            if (restart) StopThread();
            if (_charge || (Cfg.RgbEffect is not null && Find(Cfg.RgbEffect).Live))
            {
                // charging display starts even when the lights are off: force the master switch on
                if (_charge && !wasCharge && _run) Pad.Run(p => (bool?)p.SetFeature(Feature.RgbEnable, 1));
                if (_charge != wasCharge) Log.Write("RGB: charging display " + (_charge ? "on" : "off"));
                StartThread();
                return;
            }
            StopThread();
            if (wasLive)
            {
                // Lights off first: leaving per-LED mode shows the stored firmware profile for a moment before
                // ApplyFirmware below sets the chosen effect.
                Pad.Run(p => (bool?)p.SetFeature(Feature.RgbEnable, 0));
                ExitPerLedMode();
            }
            else LeaveStrandedPerLedMode();
            if (Cfg.RgbEffect is not null) ApplyFirmware(Find(Cfg.RgbEffect));
            else Status = "";
            if (wasCharge) Log.Write("RGB: charging display off");
        }
    }

    /// <summary>Before quitting: hand the LEDs back to the firmware.</summary>
    public static void Shutdown()
    {
        lock (_applyLock)
        {
            if (!_run) return;
            StopThread();
            ExitPerLedMode();
        }
    }

    /// <summary>Mode-change flash on the per-LED lamps (used by RgbFlash while a live effect runs).</summary>
    public static void Flash(Color c) { _flashColor = c; _flashStartMs = Environment.TickCount64; }

    // ---- firmware (built-in) effects ---------------------------------------------------------------

    static void ApplyFirmware(Effect eff)
    {
        Pad.Run(p =>
        {
            var effect = eff.Id switch { "breathe" => RgbEffect.Breathe, "chroma" => RgbEffect.Chroma, "rainbow" => RgbEffect.Rainbow, _ => RgbEffect.Solid };
            var prof = new RgbProfile(effect, Color.FromArgb(Cfg.RgbColor), (byte)Math.Clamp(Cfg.RgbBrightness, 0, 100), (byte)Math.Clamp(Cfg.RgbSpeed, 0, 100));
            // "Off" still rewrites the stored profile, so a stale colour (e.g. from an interrupted flash) can't reappear.
            if (eff.Id == "off") return p.SetRgbProfile(1, prof) & p.SetFeature(Feature.RgbEnable, 0);
            // Only select the profile: writing the RGB mode (even the same value) makes the controller re-enumerate.
            // Callers make sure it is already in normal (firmware) mode.
            return p.SetRgbProfile(1, prof) & p.SelectRgbProfile(1) & p.SetFeature(Feature.RgbEnable, 1);
        });
        var c = eff.Id == "off" ? Color.Black : Scale(Color.FromArgb(Cfg.RgbColor), Cfg.RgbBrightness / 100.0);
        for (int i = 0; i < Current.Length; i++) Current[i] = c;
        Status = eff.Id == "off" ? "Lights off" : $"Built-in effect: {eff.Name}";
        Log.Write("RGB firmware effect: " + eff.Id);
    }

    // ---- per-LED mode -------------------------------------------------------------------------------

    static LampArray? EnterPerLedMode()
    {
        var lamps = LampArray.Open();
        if (lamps is null)
        {
            Status = "Switching to per-LED mode…";
            // Master switch must be on, then RGB mode 0 re-enumerates mi_04 as a LampArray.
            Pad.Run(p => { p.SetFeature(Feature.RgbEnable, 1); p.SetRgbMode(0); return (bool?)true; });
            for (int i = 0; i < 30 && _run && lamps is null; i++) { Thread.Sleep(300); lamps = LampArray.Open(); }
        }
        if (lamps is null) { Status = "Per-LED mode not available"; Log.Write("RGB: LampArray not found"); return null; }
        Pad.Run(p => (bool?)p.SetFeature(Feature.RgbEnable, 1));
        lamps.SetAutonomous(false);
        Status = "Live";
        Log.Write("RGB: per-LED mode active");
        return lamps;
    }

    static void ExitPerLedMode()
    {
        using (var lamps = LampArray.Open()) lamps?.SetAutonomous(true);
        Log.Write(SwitchToFirmwareMode() ? "RGB: back to firmware mode" : "RGB: could not leave per-LED mode (controller not responding)");
    }

    /// <summary>
    /// Sets RGB mode 1 and checks that it took. Right after resume the controller may not answer yet, and a failed
    /// switch leaves it in per-LED mode with nothing driving the lamps (frozen on the last frame), so retry ~15 s.
    /// </summary>
    static bool SwitchToFirmwareMode()
    {
        for (int i = 0; i < 6; i++)
        {
            byte? mode = Pad.Run(p => p.GetRgbMode());
            if (mode == 1) return true;
            if (mode is not null) Pad.Run(p => (bool?)p.SetRgbMode(1));
            Thread.Sleep(2500); // controller re-enumerates (or is still waking up)
        }
        return false;
    }

    /// <summary>
    /// Repairs the "stuck" state: the controller is in per-LED mode but no live effect is running (e.g. leaving it
    /// failed after sleep). Switches back to normal mode once and re-applies the chosen built-in effect.
    /// </summary>
    public static void RecoverStrandedPerLedMode()
    {
        lock (_applyLock)
        {
            if (_mc is null || _run || !LeaveStrandedPerLedMode()) return;
            if (Cfg.RgbEffect is not null && !Find(Cfg.RgbEffect).Live) ApplyFirmware(Find(Cfg.RgbEffect));
        }
    }

    /// <summary>True if the controller was stranded in per-LED mode and has been switched back.</summary>
    static bool LeaveStrandedPerLedMode()
    {
        if (Pad.Run(p => p.GetRgbMode()) != 0) return false;
        Log.Write("RGB: controller was left in per-LED mode with no live effect, switching back");
        ExitPerLedMode();
        return true;
    }

    static void StartThread()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Lighting" };
        _thread.Start();
    }

    static void StopThread()
    {
        if (!_run) return;
        _run = false;
        _thread?.Join(3000);
        _thread = null;
    }

    // ---- render loop -----------------------------------------------------------------------------

    static void Loop()
    {
        LampArray? lamps = null;
        var colors = new Color[LampArray.Count];
        var scratch = new Color[LampArray.Count];
        var last = new int[LampArray.Count];
        long start = Environment.TickCount64, nextTemp = 0, nextLoad = 0;
        double temp = 50, load = 0;
        int failures = 0;
        try
        {
            while (_run)
            {
                lamps ??= EnterPerLedMode();
                if (lamps is null) { Thread.Sleep(2000); continue; }

                long now = Environment.TickCount64;
                double t = (now - start) / 1000.0;
                if (now >= nextTemp) { if (LenovoWmi.TryGetFeature(LenovoWmi.CpuTemp) is int ct && ct > 0) temp = temp * 0.6 + ct * 0.4; nextTemp = now + 1000; }
                if (now >= nextLoad) { load = load * 0.5 + CpuLoad.Sample() * 0.5; nextLoad = now + 500; }
                var ps = SystemInformation.PowerStatus;
                double battery = Math.Clamp(ps.BatteryLifePercent, 0f, 1f);
                bool charging = ps.PowerLineStatus == PowerLineStatus.Online;
                // plugged in but not taking charge = full, or held by the charge limit
                bool takingCharge = charging && ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.Charging);
                Status = _charge ? "Showing charge level. Your effect returns when you unplug." : "Live";

                LiveFraction = Cfg.RgbEffect switch { "temp" => Math.Clamp((temp - 30) / 70, 0, 1), "load" => load, "battery" => battery, _ => 0 };
                if (PreviewValue is double pv)
                {
                    // simulate: temperature 30-100 °C, load and battery 0-100 % (not charging)
                    Render(Cfg.RgbEffect, t, 30 + pv * 70, pv, pv, false, colors);
                }
                else if (_charge) RenderCharging(t, battery, takingCharge, colors);
                else Render(Cfg.RgbEffect, t, temp, load, battery, charging, colors);
                ToPhysical(colors, scratch);

                double bright = Math.Clamp(Cfg.RgbBrightness, 0, 100) / 100.0;
                for (int i = 0; i < colors.Length; i++) colors[i] = Scale(colors[i], bright);

                // overlays (after brightness, so a warning is always clearly visible): low battery, then mode flash.
                // The warning uses the real battery - except while previewing the Battery gauge, where it follows
                // the simulated level so the user can see it (temp/load previews don't affect it).
                double warnLevel = battery; bool warnCharging = charging;
                if (PreviewValue is double sim && Cfg.RgbEffect == "battery") { warnLevel = sim; warnCharging = false; }
                if (Cfg.RgbLowBatteryWarning && !warnCharging && warnLevel <= 0.15)
                {
                    // slow pulse every 3 s between 7 % and 100 % red, ignoring the brightness slider
                    double s = Math.Sin(Math.PI * t / 3);
                    double e = 0.07 + 0.93 * s * s;
                    for (int i = 0; i < colors.Length; i++) colors[i] = Scale(Color.Red, e);
                }
                if (_flashStartMs >= 0)
                {
                    double ft = (now - _flashStartMs) / 900.0;
                    if (ft >= 1) _flashStartMs = -1;
                    else { double e = Math.Sin(Math.PI * ft); for (int i = 0; i < colors.Length; i++) colors[i] = Lerp(colors[i], _flashColor, e); }
                }

                bool changed = false;
                for (int i = 0; i < colors.Length; i++) { Current[i] = colors[i]; if (colors[i].ToArgb() != last[i]) changed = true; }
                if (changed)
                {
                    if (lamps.Set(colors)) { failures = 0; for (int i = 0; i < colors.Length; i++) last[i] = colors[i].ToArgb(); }
                    else if (++failures >= 3) { Log.Write("RGB: lamp writes failing, re-entering per-LED mode"); lamps.Dispose(); lamps = null; failures = 0; Array.Clear(last); }
                }
                Thread.Sleep(33);
            }
        }
        catch (Exception ex) { Log.Write("RGB loop failed: " + ex); Status = "Error: " + ex.Message; }
        finally { lamps?.Dispose(); }
    }

    /// <summary>
    /// Effects draw in "logical" order: position 0..5 runs clockwise on the left ring from 7 o'clock, the first LED
    /// after the gap at 6 o'clock, so gauges fill whole sectors (7-9, 11-1, 3-5). Physical lamp 0 sits at 9 o'clock,
    /// so logical position i drives lamp (i + 5) % 6. See docs/controller-protocol.md.
    /// </summary>
    const int StartLamp = 5;

    static void ToPhysical(Color[] c, Color[] tmp)
    {
        int n = c.Length;
        Array.Copy(c, tmp, n);
        for (int i = 0; i < n; i++) c[(i + StartLamp) % n] = tmp[i];
    }

    /// <summary>Computes the 6 colours for a live effect in logical order (see <see cref="StartLamp"/>).</summary>
    static void Render(string? id, double t, double temp, double load, double battery, bool charging, Color[] c)
    {
        int n = c.Length;
        double speed = Math.Clamp(Cfg.RgbSpeed, 1, 100);
        var user = Color.FromArgb(Cfg.RgbColor);
        switch (id)
        {
            case "temp":
            {
                var col = Gradient(temp, (40, Color.FromArgb(0, 70, 255)), (55, Color.FromArgb(0, 255, 60)), (70, Color.FromArgb(255, 220, 0)),
                                         (80, Color.FromArgb(255, 90, 0)), (90, Color.FromArgb(255, 0, 0)));
                for (int i = 0; i < n; i++) c[i] = col;
                break;
            }
            case "battery":
            {
                double segs = battery * n;
                var col = battery > 0.5 ? Color.FromArgb(0, 255, 40) : battery > 0.2 ? Color.FromArgb(255, 200, 0) : Color.FromArgb(255, 0, 0);
                double sweep = charging ? (t * 2.0) % (n + 1) : -10;   // charging: a bright pulse runs up the filled segments
                for (int i = 0; i < n; i++)
                {
                    double fill = Math.Clamp(segs - i, 0, 1);                 // partial last segment
                    var baseCol = Scale(col, fill * fill);                     // empty segments fully off (LEDs show even 5-10 %)
                    double pulse = Math.Max(0, 1 - Math.Abs(sweep - i));
                    c[i] = Lerp(baseCol, Color.White, pulse * 0.6);
                }
                break;
            }
            case "load":
            {
                double segs = load * n;
                for (int i = 0; i < n; i++)
                {
                    var col = Gradient(i / (double)(n - 1) * 100, (0, Color.FromArgb(0, 255, 40)), (50, Color.FromArgb(255, 210, 0)), (100, Color.FromArgb(255, 0, 0)));
                    double fill = Math.Clamp(segs - i, 0, 1);
                    c[i] = Scale(col, fill * fill);                            // unused segments fully off
                }
                break;
            }
            case "comet":
            {
                double head = t * (0.6 + speed * 0.12) % n;               // lamps per second
                for (int i = 0; i < n; i++)
                {
                    // signed distance from the head, wrapped to (-n/2, n/2]: >0 = ahead, <0 = behind
                    double d = i - head;
                    if (d > n / 2.0) d -= n; else if (d <= -n / 2.0) d += n;
                    // smooth glow: fades IN over the lamp ahead, peaks at the head, fades OUT along a longer tail
                    double e = d >= 0 ? Math.Max(0, 1 - d / 1.1) : Math.Max(0, 1 + d / 3.0);
                    e = e * e * (3 - 2 * e);                                 // smoothstep for an even ramp
                    c[i] = Scale(user, e * e);                               // squared: LEDs look linear to the eye
                }
                break;
            }
            case "wheel":
            {
                double rot = t * (10 + speed * 3.5);                         // degrees per second
                for (int i = 0; i < n; i++) c[i] = Hsv((i * 60 + rot) % 360);
                break;
            }
            case "mode":
            {
                var col = MainForm.LedColor(_mc!.CurrentMode);
                for (int i = 0; i < n; i++) c[i] = col;
                break;
            }
            default:
                for (int i = 0; i < n; i++) c[i] = user;
                break;
        }
    }

    /// <summary>
    /// Charging display: the battery gauge with the segment currently being filled pulsing between 7 % and 100 %
    /// (one cycle every 2 s). Full, or held at the charge limit: the gauge turns solid green and stops pulsing.
    /// </summary>
    static void RenderCharging(double t, double battery, bool takingCharge, Color[] c)
    {
        int n = c.Length;
        var green = Color.FromArgb(0, 255, 40);
        if (!takingCharge || battery >= 0.995)
        {
            // done: every lit segment solid green (all six at 100 %)
            double segsDone = battery >= 0.995 ? n : battery * n;
            for (int i = 0; i < n; i++) { double f = Math.Clamp(segsDone - i, 0, 1); c[i] = Scale(green, f * f); }
            return;
        }
        var col = battery > 0.5 ? green : battery > 0.2 ? Color.FromArgb(255, 200, 0) : Color.FromArgb(255, 0, 0);
        int active = Math.Min((int)(battery * n), n - 1);               // segment being filled right now
        double s = Math.Sin(Math.PI * t / 2);
        double pulse = 0.07 + 0.93 * s * s;
        for (int i = 0; i < n; i++)
            c[i] = i < active ? col : i == active ? Scale(col, pulse) : Color.Black;
    }

    // ---- colour helpers ------------------------------------------------------------------------------

    public static Color Scale(Color c, double f)
    {
        f = Math.Clamp(f, 0, 1);
        return Color.FromArgb((int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
    }

    public static Color Lerp(Color a, Color b, double f)
    {
        f = Math.Clamp(f, 0, 1);
        return Color.FromArgb((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
    }

    static Color Gradient(double v, params (double at, Color col)[] stops)
    {
        if (v <= stops[0].at) return stops[0].col;
        for (int i = 1; i < stops.Length; i++)
            if (v <= stops[i].at) return Lerp(stops[i - 1].col, stops[i].col, (v - stops[i - 1].at) / (stops[i].at - stops[i - 1].at));
        return stops[^1].col;
    }

    static Color Hsv(double h)
    {
        double x = 1 - Math.Abs(h / 60 % 2 - 1);
        var (r, g, b) = h switch
        {
            < 60 => (1.0, x, 0.0), < 120 => (x, 1.0, 0.0), < 180 => (0.0, 1.0, x),
            < 240 => (0.0, x, 1.0), < 300 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }
}

/// <summary>Whole-system CPU usage from GetSystemTimes (cheap, no performance counters).</summary>
static class CpuLoad
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    static long _idle, _total;

    /// <summary>0..1 since the previous call.</summary>
    public static double Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long total = kernel + user; // kernel time includes idle
        long dTotal = total - _total, dIdle = idle - _idle;
        _idle = idle; _total = total;
        return dTotal <= 0 ? 0 : Math.Clamp(1 - dIdle / (double)dTotal, 0, 1);
    }
}
