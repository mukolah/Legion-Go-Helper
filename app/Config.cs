using System.Text.Json;

namespace LegionHelper;

/// <summary>User settings persisted to %AppData%\LegionHelper\config.json.</summary>
public class Config
{
    public uint Mode { get; set; } = 2; // Balanced (last applied)

    /// <summary>Mode remembered per power source, like G-Helper. null = follow <see cref="Mode"/>.</summary>
    public uint? ModeAC { get; set; }
    public uint? ModeDC { get; set; }

    // Custom-mode power limits (W). 0 = not set yet -> firmware default.
    public int SPL { get; set; }
    public int SPPT { get; set; }
    public int FPPT { get; set; }

    public bool ApplyFanCurve { get; set; }
    /// <summary>Use the custom fan curve in Quiet/Balanced/Performance too (the firmware shares one fan table).</summary>
    public bool FanCurveAllModes { get; set; }
    public int[]? FanCurve { get; set; }
    public int[]? FanTemps { get; set; }
    public int[]? FactoryFanCurve { get; set; }

    /// <summary>0 = auto (max on AC, min on battery), otherwise fixed Hz.</summary>
    public int RefreshRate { get; set; } = -1; // -1 = don't touch

    /// <summary>null = never set in this app (firmware state unknown: its getter always returns 0).</summary>
    public bool? ChargeLimit { get; set; }

    /// <summary>
    /// Charge target when <see cref="ChargeLimit"/> is on: 80 = firmware limit only; 85/90/95 = the app turns the
    /// firmware's 80% limit on once the battery reaches the target (holds there), off below it.
    /// </summary>
    public int ChargeTarget { get; set; } = 80;

    // Startup
    public bool StartHidden { get; set; } = true;

    // RGB (see Lighting.cs). RgbEffect null = never chosen in this app → controller lighting left untouched.
    public string? RgbEffect { get; set; }
    public int RgbColor { get; set; } = Color.FromArgb(0, 80, 255).ToArgb();
    public int RgbBrightness { get; set; } = 100;     // 0-100
    public int RgbSpeed { get; set; } = 50;           // 1-100
    public bool RgbLowBatteryWarning { get; set; } = true;
    /// <summary>While the charger is plugged in, the sticks show the charge level (overrides the chosen effect, even Off).</summary>
    public bool RgbChargingDisplay { get; set; }

    // Mode-change feedback
    public bool ShowOsd { get; set; } = true;
    public bool RgbModeFlash { get; set; }

    /// <summary>Global hotkey (VK code, e.g. F13-F24) that cycles performance modes. 0 = none.</summary>
    public int CycleKey { get; set; }
    /// <summary>RegisterHotKey modifiers for <see cref="CycleKey"/>: 1 Alt, 2 Ctrl, 4 Shift, 8 Win.</summary>
    public uint CycleMods { get; set; }
    public bool CycleIncludesCustom { get; set; }

    // Mouse emulation: "off", "gyro", "joystick"
    public string MouseMode { get; set; } = "off";
    public bool JoystickMouseRightStick { get; set; } = true;
    public int JoystickMouseSpeed { get; set; } = 50;   // 1-100
    /// <summary>Clicks on RB (left) / LB (right) instead of RT / LT.</summary>
    public bool JoystickMouseBumpers { get; set; }

    /// <summary>Back-paddle chords (Y1/Y2 + D-pad/ABXY). Key = "Y1+DUp" etc., value = Shortcuts action id.</summary>
    public bool ChordsEnabled { get; set; }

    /// <summary>Actions for the Legion L / Legion R buttons (Shortcuts action ids; null = none).</summary>
    public string? LegionLAction { get; set; }
    public string? LegionRAction { get; set; }
    public Dictionary<string, string> Chords { get; set; } = new()
    {
        ["Y1+DUp"] = "mode:quiet", ["Y1+DRight"] = "mode:balanced", ["Y1+DDown"] = "mode:performance", ["Y1+DLeft"] = "mode:custom",
        ["Y2+DUp"] = "lights:toggle", ["Y2+DRight"] = "mouse:cycle", ["Y2+DDown"] = "screen:toggle", ["Y2+DLeft"] = "app:toggle",
    };

    public int GyroMouseSpeed { get; set; } = 50;       // 1-100
    /// <summary>Horizontal cursor from roll (steering-wheel tilt) instead of yaw (turning left/right).</summary>
    public bool GyroHorizontalRoll { get; set; }
    public bool GyroInvertX { get; set; }
    public bool GyroInvertY { get; set; }

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LegionHelper");
    static readonly string FilePath = Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>True when no config file existed yet (first run).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNew { get; private set; }

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch { }
        return new Config { IsNew = true };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
    }
}
