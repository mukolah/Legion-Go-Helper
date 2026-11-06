using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using LegionHelper.Hardware;
using LegionHelper.UI;
using static LegionHelper.Hardware.LegionController;

namespace LegionHelper;

/// <summary>
/// Centered on-screen pop-up shown when the performance mode changes (like G-Helper's toast).
/// Layered, click-through, never takes focus, top-most (visible over borderless/windowed games).
/// </summary>
public sealed class ModeOsd : Form
{
    static ModeOsd? _instance;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    string _text = "";
    Color _color;
    int _ticks;
    const int ShowTicks = 75, FadeTicks = 20; // ~1.2 s visible + ~0.3 s fade at 60 fps

    ModeOsd()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Back;
        DoubleBuffered = true;
        float k = DeviceDpi / 96f;
        Size = new Size((int)(300 * k), (int)(96 * k));
        _timer.Tick += (_, _) =>
        {
            _ticks++;
            if (_ticks > ShowTicks) Opacity = Math.Max(0, 0.95 * (1 - (_ticks - ShowTicks) / (double)FadeTicks));
            if (_ticks >= ShowTicks + FadeTicks) { _timer.Stop(); Hide(); }
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x20 /*TRANSPARENT*/ | 0x80000 /*LAYERED*/ | 0x8000000 /*NOACTIVATE*/ | 0x8 /*TOPMOST*/;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static void Show(string text, Color color)
    {
        _instance ??= new ModeOsd();
        var f = _instance;
        f._text = text; f._color = color; f._ticks = 0;
        var wa = Screen.PrimaryScreen!.Bounds;
        f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Top + (int)(wa.Height * 0.72) - f.Height / 2);
        f.Opacity = 0.95;
        if (!f.Visible) f.Show();
        SetWindowPos(f.Handle, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
        f.Invalidate();
        f._timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Back);
        float k = DeviceDpi / 96f, d = 40 * k;
        var circle = new RectangleF(24 * k, (Height - d) / 2, d, d);
        using (var ring = new Pen(_color, 6 * k)) g.DrawEllipse(ring, circle);
        using (var dot = new SolidBrush(_color)) g.FillEllipse(dot, circle.X + d * 0.3f, circle.Y + d * 0.3f, d * 0.4f, d * 0.4f);
        using var font = new Font("Segoe UI", 18f, FontStyle.Bold);
        var r = new Rectangle((int)(circle.Right + 18 * k), 0, Width - (int)(circle.Right + 24 * k), Height);
        TextRenderer.DrawText(g, _text, font, r, Theme.Fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var border = new Pen(_color, 2 * k);
        g.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
    }
}

/// <summary>
/// Fades the joystick RGB rings in and out in a colour, then restores the user's lighting exactly.
/// A newer flash interrupts a running one (rapid mode switching always ends on the latest colour); the user's
/// lighting is saved once at the start of a burst and restored only by the last flash.
/// </summary>
public static class RgbFlash
{
    record Saved(byte Enabled, byte Mode, byte Active, RgbProfile Profile);

    static readonly object _gate = new();
    static int _generation;
    static Saved? _saved;   // non-null while a burst of flashes is in progress

    public static void Flash(Color color)
    {
        // In per-LED mode the live engine owns the lamps: flash there instead of rewriting firmware profiles.
        if (Lighting.LiveActive) { Lighting.Flash(color); return; }
        int me = Interlocked.Increment(ref _generation);
        Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (_saved is null)
                    {
                        _saved = Pad.Run(p =>
                        {
                            var enabled = p.GetFeature(Feature.RgbEnable);
                            var profile = p.GetRgbProfile(1);
                            if (enabled is null || profile is null) return null;
                            return new Saved(enabled.Value, p.GetRgbMode() ?? 1, p.GetActiveRgbProfile() ?? 1, profile);
                        });
                        if (_saved is null) return;
                    }
                }

                bool Current() => Volatile.Read(ref _generation) == me;
                bool Step(byte brightness, bool first = false) => Current() && Pad.Run(p =>
                {
                    if (!Current()) return (bool?)false;          // superseded while waiting for the controller
                    p.SetRgbProfile(1, new RgbProfile(RgbEffect.Solid, color, brightness, 0));
                    if (first) { p.UseRgbProfile(1); p.SetFeature(Feature.RgbEnable, 1); }
                    return true;
                }) == true;

                if (!Step(15, first: true)) return;
                foreach (var b in new byte[] { 35, 60, 85, 100 }) { Thread.Sleep(35); if (!Step(b)) return; }
                for (int i = 0; i < 9; i++) { Thread.Sleep(50); if (!Current()) return; }   // hold ~450 ms, abortable
                foreach (var b in new byte[] { 85, 60, 35, 15, 0 }) { Thread.Sleep(45); if (!Step(b)) return; }

                // Last flash of the burst: restore the user's lighting.
                lock (_gate)
                {
                    if (!Current() || _saved is not { } s) return;
                    Pad.Run(p =>
                    {
                        p.SetRgbProfile(1, s.Profile);
                        p.SetRgbMode(s.Mode);
                        p.SelectRgbProfile(s.Active);
                        return (bool?)p.SetFeature(Feature.RgbEnable, s.Enabled);
                    });
                    _saved = null;
                }
            }
            catch (Exception ex) { Log.Write("RGB flash failed: " + ex.Message); }
        });
    }
}
