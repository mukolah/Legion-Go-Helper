using System.Runtime.InteropServices;
using LegionHelper.Hardware;

namespace LegionHelper;

/// <summary>Actions that can be assigned to back-paddle chords (paddle + D-pad / ABXY).</summary>
public static class Shortcuts
{
    public record Action(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    public static readonly Action None = new("none", "-");

    public static readonly Action[] All = Build();

    static Action[] Build()
    {
        var list = new List<Action>
        {
            None,
            new("mode:quiet", "Mode: Quiet"),
            new("mode:balanced", "Mode: Balanced"),
            new("mode:performance", "Mode: Performance"),
            new("mode:custom", "Mode: Custom"),
            new("mode:cycle", "Mode: next"),
            new("lights:toggle", "Lights on / off"),
            new("mouse:off", "Mouse mode: None"),
            new("mouse:gyro", "Mouse mode: Gyro"),
            new("mouse:joystick", "Mouse mode: Joystick"),
            new("mouse:cycle", "Mouse mode: next"),
            new("mouse:joystick-toggle", "Joystick mouse on / off"),
            new("mouse:gyro-toggle", "Gyro mouse on / off"),
            new("gyro:calibrate", "Gyro: calibrate"),
            new("screen:toggle", "Screen: 60 / 120 Hz"),
            new("app:toggle", "Show / hide Legion Helper"),
            new("app:show", "Open Legion Helper"),
            new("charge:toggle", "Charge limit on / off"),
            new("touchkb:toggle", "Touch keyboard show / hide"),
            // keyboard presets (VK codes)
            new("keys:11+10+1B", "Keys: Task Manager (Ctrl+Shift+Esc)"),
            new("keys:12+09", "Keys: Alt+Tab"),
            new("keys:12+73", "Keys: Alt+F4"),
            new("keys:5B+44", "Keys: Show desktop (Win+D)"),
            new("keys:5B+09", "Keys: Task view (Win+Tab)"),
            new("keys:5B+47", "Keys: Game Bar (Win+G)"),
            new("keys:5B+12+52", "Keys: Record clip (Win+Alt+R)"),
            new("keys:5B+A0+53", "Keys: Snip (Win+Shift+S)"),
            new("keys:5B+2C", "Keys: Screenshot to file (Win+PrtScn)"),
            new("keys:5B+A2+4F", "Keys: On-screen keyboard (Win+Ctrl+O)"),
            new("keys:AF", "Keys: Volume up"),
            new("keys:AE", "Keys: Volume down"),
            new("keys:AD", "Keys: Mute"),
            new("keys:B3", "Keys: Media play/pause"),
            new("keys:B0", "Keys: Media next"),
            new("keys:B1", "Keys: Media previous"),
        };
        // any single keyboard key from the catalog (incl. F13-F22; F23/F24 are reserved for the paddles)
        foreach (var (vk, name) in KeyCatalog.KeyboardKeyList)
            if (vk is not (PaddleChords.VkY1 or PaddleChords.VkY2))
                list.Add(new($"keys:{vk:X2}", "Key: " + name));
        return list.ToArray();
    }

    public static Action Find(string? id) => All.FirstOrDefault(a => a.Id == id) ?? None;

    // ---- keyboard output -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct KeybdInput { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct Input { public uint type; public KeybdInput ki; public long pad; } // 40 bytes on x64 (union sized by MOUSEINPUT)
    [DllImport("user32.dll")] static extern uint SendInput(uint n, Input[] inputs, int size);

    static readonly HashSet<int> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x6F, 0x90, 0xA3, 0xA5, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3];

    /// <summary>Presses keys in order, then releases them in reverse (e.g. "11+10+1B" = Ctrl+Shift+Esc).</summary>
    public static void SendKeys(string spec)
    {
        var vks = spec.Split('+').Select(s => Convert.ToUInt16(s, 16)).ToArray();
        var inputs = new List<Input>();
        foreach (var vk in vks) inputs.Add(Key(vk, false));
        foreach (var vk in vks.Reverse()) inputs.Add(Key(vk, true));
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
    }

    static Input Key(ushort vk, bool up) => new()
    {
        type = 1,
        ki = new KeybdInput { vk = vk, flags = (up ? 0x2u : 0u) | (Extended.Contains(vk) ? 0x1u : 0u) },
    };

    // ---- Windows touch keyboard (TabTip) ---------------------------------------------------------
    // The touch keyboard has no hotkey; it is toggled through the undocumented-but-stable
    // ITipInvocation COM interface exposed by TabTip.exe (same mechanism as the taskbar button).

    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITipInvocation { void Toggle(IntPtr hwnd); }

    static readonly Guid TipInvocationClsid = new("4ce576fa-83dc-4f88-951c-9d0782b4e376");

    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();

    static readonly string TabTipPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        @"microsoft shared\ink\TabTip.exe");

    /// <summary>Shows or hides the Windows touch keyboard. Must be called on an STA (UI) thread.</summary>
    public static void ToggleTouchKeyboard()
    {
        try
        {
            if (!TryToggleTip())
            {
                // TabTip not running yet: launching it brings it up (and registers the COM server).
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TabTipPath) { UseShellExecute = true });
            }
        }
        catch (Exception ex) { Log.Write("Touch keyboard: " + ex.Message); }
    }

    static bool TryToggleTip()
    {
        var type = Type.GetTypeFromCLSID(TipInvocationClsid);
        if (type is null) return false;
        object? obj = null;
        try
        {
            obj = Activator.CreateInstance(type);
            if (obj is not ITipInvocation tip) return false;
            tip.Toggle(GetDesktopWindow());
            return true;
        }
        catch (COMException) { return false; }
        finally { if (obj is not null) Marshal.ReleaseComObject(obj); }
    }
}
