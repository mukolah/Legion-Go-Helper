using System.Runtime.InteropServices;

namespace LegionHelper.Hardware;

/// <summary>XInput state of the first connected controller.</summary>
static class Pad360
{
    [StructLayout(LayoutKind.Sequential)]
    public struct State { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int user, out State s);
    [StructLayout(LayoutKind.Sequential)] struct Vibration { public ushort Left, Right; }
    [DllImport("xinput1_4.dll")] static extern int XInputSetState(int user, ref Vibration v);

    /// <summary>Stops rumble on all connected controllers.</summary>
    public static void StopRumble()
    {
        var off = new Vibration();
        for (int i = 0; i < 4; i++) XInputSetState(i, ref off);
    }

    public const ushort LB = 0x0100, RB = 0x0200;

    public static bool TryGet(out State s)
    {
        for (int i = 0; i < 4; i++) if (XInputGetState(i, out s) == 0) return true;
        s = default;
        return false;
    }
}

/// <summary>
/// Mouse clicks from the controller, shared by joystick- and gyro-mouse:
/// RT = left / LT = right, or RB = left / LB = right when <see cref="UseBumpers"/> is set.
/// </summary>
sealed class PadClicks
{
    public static bool UseBumpers { get; set; }
    bool _l, _r;

    public void Update(in Pad360.State s)
    {
        bool left = UseBumpers ? (s.Buttons & Pad360.RB) != 0 : s.RT > 100;
        bool right = UseBumpers ? (s.Buttons & Pad360.LB) != 0 : s.LT > 100;
        if (left != _l) { MouseOut.Buttons(left ? MouseOut.LDOWN : MouseOut.LUP); _l = left; }
        if (right != _r) { MouseOut.Buttons(right ? MouseOut.RDOWN : MouseOut.RUP); _r = right; }
    }

    /// <summary>Release anything still held (when the mode stops).</summary>
    public void Release()
    {
        if (_l) MouseOut.Buttons(MouseOut.LUP);
        if (_r) MouseOut.Buttons(MouseOut.RUP);
        _l = _r = false;
    }
}

/// <summary>
/// Software "joystick as mouse": one stick moves the cursor, the other scrolls, triggers (or bumpers) click.
/// Reads the controller through XInput, so it only runs (one 125 Hz thread) while enabled.
/// </summary>
public static class JoystickMouse
{
    static Thread? _thread;
    static volatile bool _run;

    /// <summary>1-100.</summary>
    public static int Speed { get; set; } = 50;
    public static bool RightStick { get; set; } = true;
    /// <summary>Click with RB/LB (true) or RT/LT (false). Shared with the gyro mouse.</summary>
    public static bool UseBumpers { get => PadClicks.UseBumpers; set => PadClicks.UseBumpers = value; }
    public static bool Running => _run;

    public static void Start()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "JoystickMouse", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public static void Stop() => _run = false;

    static void Loop()
    {
        var clicks = new PadClicks();
        double accX = 0, accY = 0, accWheel = 0;
        while (_run)
        {
            Thread.Sleep(8);
            if (!Pad360.TryGet(out var s)) { Thread.Sleep(500); continue; }

            short mx = RightStick ? s.RX : s.LX, my = RightStick ? s.RY : s.LY;
            short wy = RightStick ? s.LY : s.RY;

            // Cursor: radial deadzone + power curve for fine control at small deflection.
            double nx = mx / 32767.0, ny = my / 32767.0, mag = Math.Sqrt(nx * nx + ny * ny);
            const double dead = 0.12;
            if (mag > dead)
            {
                double scaled = (Math.Min(mag, 1) - dead) / (1 - dead);
                double speed = Math.Pow(scaled, 2.2) * (2 + Speed * 0.38);   // px per 8 ms tick at full tilt: ~2.4 .. 40
                accX += nx / mag * speed;
                accY += -ny / mag * speed;
            }
            int dx = (int)accX, dy = (int)accY;
            accX -= dx; accY -= dy;
            if (dx != 0 || dy != 0) MouseOut.Move(dx, dy);

            // Wheel from the other stick's vertical axis.
            double w = wy / 32767.0;
            if (Math.Abs(w) > 0.25) accWheel += Math.Sign(w) * Math.Pow((Math.Abs(w) - 0.25) / 0.75, 2) * 24;
            int wheel = (int)accWheel; accWheel -= wheel;
            if (wheel != 0) MouseOut.Wheel(wheel);

            clicks.Update(s);
        }
        clicks.Release();
    }
}
