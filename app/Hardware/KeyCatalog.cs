using static LegionHelper.Hardware.LegionController;

namespace LegionHelper.Hardware;

/// <summary>
/// Button ids and mapping targets for the Legion Go S controller.
/// Button ids come from Legion Space's UI tables (LRH_* codes); keyboard codes are Lenovo's VK→HID usage table
/// from LEGOQHWHandle.dll; mouse/media codes from the same DLL's lookup tables.
/// </summary>
public static class KeyCatalog
{
    public record Target(string Name, MapType Type, byte Code)
    {
        public override string ToString() => Name;
    }

    /// <summary>Remappable physical buttons on the Go S (id, name).</summary>
    public static readonly (byte Id, string Name)[] Buttons =
    [
        (0x1C, "Y1 (left back paddle)"), (0x1D, "Y2 (right back paddle)"),
        (0x12, "A"), (0x13, "B"), (0x14, "X"), (0x15, "Y"),
        (0x16, "LB"), (0x18, "RB"), (0x17, "LT"), (0x19, "RT"),
        (0x03, "Left stick press"), (0x08, "Right stick press"),
        (0x0D, "D-Pad Up"), (0x0E, "D-Pad Down"), (0x0F, "D-Pad Left"), (0x10, "D-Pad Right"),
        (0x23, "View"), (0x24, "Menu"),
        // Legion L (0x01) / Legion R (0x02) are system buttons: the controller rejects remaps (status 1).
    ];

    static readonly (byte Id, string Name)[] GamepadTargets =
    [
        (0x12, "A"), (0x13, "B"), (0x14, "X"), (0x15, "Y"), (0x16, "LB"), (0x18, "RB"), (0x17, "LT"), (0x19, "RT"),
        (0x03, "Left stick press"), (0x08, "Right stick press"),
        (0x0D, "D-Pad Up"), (0x0E, "D-Pad Down"), (0x0F, "D-Pad Left"), (0x10, "D-Pad Right"),
        (0x23, "View"), (0x24, "Menu"), (0x1C, "Y1"), (0x1D, "Y2"),
    ];

    /// <summary>Windows VK code → HID keyboard usage (Lenovo's table).</summary>
    static readonly Dictionary<int, byte> VkToHid = new()
    {
        [0x08] = 0x2A, [0x09] = 0x2B, [0x0D] = 0x28, [0x12] = 0x76, [0x13] = 0x48, [0x14] = 0x39, [0x1B] = 0x29, [0x20] = 0x2C,
        [0x21] = 0x4B, [0x22] = 0x4E, [0x23] = 0x4D, [0x24] = 0x4A, [0x25] = 0x50, [0x26] = 0x52, [0x27] = 0x4F, [0x28] = 0x51,
        [0x2C] = 0x46, [0x2D] = 0x49, [0x2E] = 0x4C, [0x30] = 0x27, [0x31] = 0x1E, [0x32] = 0x1F, [0x33] = 0x20, [0x34] = 0x21,
        [0x35] = 0x22, [0x36] = 0x23, [0x37] = 0x24, [0x38] = 0x25, [0x39] = 0x26, [0x41] = 0x04, [0x42] = 0x05, [0x43] = 0x06,
        [0x44] = 0x07, [0x45] = 0x08, [0x46] = 0x09, [0x47] = 0x0A, [0x48] = 0x0B, [0x49] = 0x0C, [0x4A] = 0x0D, [0x4B] = 0x0E,
        [0x4C] = 0x0F, [0x4D] = 0x10, [0x4E] = 0x11, [0x4F] = 0x12, [0x50] = 0x13, [0x51] = 0x14, [0x52] = 0x15, [0x53] = 0x16,
        [0x54] = 0x17, [0x55] = 0x18, [0x56] = 0x19, [0x57] = 0x1A, [0x58] = 0x1B, [0x59] = 0x1C, [0x5A] = 0x1D, [0x5B] = 0xE3,
        [0x5C] = 0xE7, [0x60] = 0x62, [0x61] = 0x59, [0x62] = 0x5A, [0x63] = 0x5B, [0x64] = 0x5C, [0x65] = 0x5D, [0x66] = 0x5E,
        [0x67] = 0x5F, [0x68] = 0x60, [0x69] = 0x61, [0x6A] = 0x55, [0x6B] = 0x57, [0x6C] = 0x58, [0x6D] = 0x56, [0x6E] = 0x63,
        [0x6F] = 0x54, [0x70] = 0x3A, [0x71] = 0x3B, [0x72] = 0x3C, [0x73] = 0x3D, [0x74] = 0x3E, [0x75] = 0x3F, [0x76] = 0x40,
        [0x77] = 0x41, [0x78] = 0x42, [0x79] = 0x43, [0x7A] = 0x44, [0x7B] = 0x45, [0x90] = 0x53, [0x91] = 0x47, [0xA0] = 0xE1,
        [0xA1] = 0xE5, [0xA2] = 0xE0, [0xA3] = 0xE4, [0xA4] = 0xE2, [0xA5] = 0xE6, [0xBA] = 0x33, [0xBB] = 0x2E, [0xBC] = 0x36,
        [0xBD] = 0x2D, [0xBE] = 0x37, [0xBF] = 0x38, [0xC0] = 0x35, [0xDB] = 0x2F, [0xDC] = 0x31, [0xDD] = 0x30, [0xDE] = 0x34,
        // F13-F24 (VK 0x7C-0x87 → HID 0x68-0x73): not in Lenovo's table, but the controller accepts them (verified).
        [0x7C] = 0x68, [0x7D] = 0x69, [0x7E] = 0x6A, [0x7F] = 0x6B, [0x80] = 0x6C, [0x81] = 0x6D,
        [0x82] = 0x6E, [0x83] = 0x6F, [0x84] = 0x70, [0x85] = 0x71, [0x86] = 0x72, [0x87] = 0x73,
    };

    static readonly (int Vk, string Name)[] KeyboardKeys = BuildKeyboard();
    public static IReadOnlyList<(int Vk, string Name)> KeyboardKeyList => KeyboardKeys;

    static (int, string)[] BuildKeyboard()
    {
        var list = new List<(int, string)>
        {
            (0x1B, "Esc"), (0x0D, "Enter"), (0x20, "Space"), (0x09, "Tab"), (0x08, "Backspace"), (0x2E, "Delete"),
            (0xA0, "Left Shift"), (0xA2, "Left Ctrl"), (0xA4, "Left Alt"), (0x5B, "Windows"),
            (0x26, "Up"), (0x28, "Down"), (0x25, "Left"), (0x27, "Right"),
            (0x24, "Home"), (0x23, "End"), (0x21, "Page Up"), (0x22, "Page Down"), (0x2D, "Insert"), (0x2C, "Print Screen"),
        };
        for (int f = 1; f <= 24; f++) list.Add((0x6F + f, $"F{f}"));
        for (char c = 'A'; c <= 'Z'; c++) list.Add((c, c.ToString()));
        for (char c = '0'; c <= '9'; c++) list.Add((c, c.ToString()));
        return list.ToArray();
    }

    static readonly (byte Code, string Name)[] MouseTargets =
        [(1, "Mouse Left"), (2, "Mouse Right"), (3, "Mouse Middle"), (4, "Wheel Up"), (5, "Wheel Down"), (6, "Mouse Back"), (7, "Mouse Forward")];

    static readonly (byte Code, string Name)[] MediaTargets =
        [(0xCD, "Media Play/Pause"), (0xB5, "Media Next"), (0xB6, "Media Previous"), (0xB7, "Media Stop")];

    public static readonly Target DefaultTarget = new("Default", MapType.System, 0);

    public static readonly Target[] Targets = BuildTargets();

    static Target[] BuildTargets()
    {
        var t = new List<Target> { DefaultTarget };
        t.AddRange(GamepadTargets.Select(g => new Target("Pad: " + g.Name, MapType.Gamepad, g.Id)));
        // F23/F24 are reserved internally for back-paddle shortcuts, so they are not offered as targets.
        t.AddRange(KeyboardKeys.Where(k => k.Vk is not (0x86 or 0x87))
                               .Select(k => new Target("Key: " + k.Name, MapType.Keyboard, VkToHid[k.Vk])));
        t.AddRange(MouseTargets.Select(m => new Target(m.Name, MapType.Mouse, m.Code)));
        t.AddRange(MediaTargets.Select(m => new Target(m.Name, MapType.Media, m.Code)));
        return t.ToArray();
    }

    /// <summary>Keyboard modifiers usable in combinations (HID usages for the left-hand keys).</summary>
    public static readonly (byte Hid, string Name)[] Modifiers = [(0xE0, "Ctrl"), (0xE1, "Shift"), (0xE2, "Alt"), (0xE3, "Win")];

    /// <summary>Finds the catalog entry + modifiers matching a controller mapping (Default if it is the factory mapping).</summary>
    public static (Target target, byte[] mods) Describe(KeyMapping m)
    {
        var def = DefaultMapping(m.Button);
        if (m.Type == def.Type && m.Codes.SequenceEqual(def.Codes)) return (DefaultTarget, []);
        if (m.Type == MapType.Combo)
        {
            var mods = m.Codes.Where(c => Modifiers.Any(x => x.Hid == c)).ToArray();
            var keys = m.Codes.Where(c => !mods.Contains(c)).ToArray();
            if (keys.Length == 1 && Targets.FirstOrDefault(t => t.Type == MapType.Keyboard && t.Code == keys[0]) is { } key)
                return (key, mods);
        }
        else if (m.Codes.Length == 1)
        {
            var hit = Targets.FirstOrDefault(t => t.Type == m.Type && t.Code == m.Codes[0] && t != DefaultTarget);
            if (hit is not null) return (hit, []);
        }
        return (new Target($"Custom ({m.Type}: {string.Join(" ", m.Codes.Select(c => c.ToString("X2")))})", m.Type, m.Codes.FirstOrDefault()), []);
    }

    /// <summary>Builds a mapping; keyboard keys with modifiers become a combination (up to 4 modifiers + key).</summary>
    public static KeyMapping ToMapping(byte button, Target t, byte[]? mods = null)
    {
        if (t == DefaultTarget) return DefaultMapping(button);
        if (t.Type == MapType.Keyboard && mods is { Length: > 0 })
            return new KeyMapping(button, MapType.Combo, [.. mods.Take(4), t.Code]);
        return new KeyMapping(button, t.Type, [t.Code]);
    }

    public static string ComboName(Target t, byte[] mods) =>
        mods.Length == 0 ? t.Name : string.Join("+", Modifiers.Where(x => mods.Contains(x.Hid)).Select(x => x.Name)) + "+" + t.Name.Replace("Key: ", "");
}
