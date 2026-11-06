using System.Drawing.Drawing2D;

namespace LegionHelper.UI;

/// <summary>Dark palette and accent colours modelled on G-Helper.</summary>
public static class Theme
{
    public static readonly Color Back = Color.FromArgb(28, 28, 28);
    public static readonly Color Panel = Color.FromArgb(36, 36, 36);
    public static readonly Color Button = Color.FromArgb(46, 46, 46);
    public static readonly Color ButtonHover = Color.FromArgb(58, 58, 58);
    public static readonly Color Border = Color.FromArgb(62, 62, 62);
    public static readonly Color Fore = Color.FromArgb(240, 240, 240);
    public static readonly Color ForeDim = Color.FromArgb(160, 160, 160);
    public static readonly Color Grid = Color.FromArgb(64, 64, 64);

    public static readonly Color Quiet = Color.FromArgb(6, 180, 138);
    public static readonly Color Balanced = Color.FromArgb(58, 174, 239);
    public static readonly Color Performance = Color.FromArgb(255, 32, 32);
    public static readonly Color Custom = Color.FromArgb(255, 128, 0);
    public static readonly Color Accent = Balanced;

    public static readonly Font Title = new("Segoe UI", 10.5f, FontStyle.Bold);
    public static readonly Font Normal = new("Segoe UI", 9.75f);
    public static readonly Font ButtonFont = new("Segoe UI", 10f, FontStyle.Bold);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE).</summary>
    public static void DarkTitleBar(Form f)
    {
        int on = 1;
        DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int));
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Flat rounded button; draws an accent border when <see cref="Active"/>.</summary>
public class RButton : Button
{
    bool _active, _hover;
    public Color AccentColor { get; set; } = Theme.Accent;
    public bool Active { get => _active; set { _active = value; Invalidate(); } }

    public RButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.ButtonFont;
        ForeColor = Theme.Fore;
        BackColor = Theme.Back;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        var r = new RectangleF(1.5f, 1.5f, Width - 3, Height - 3);
        using var path = Theme.Rounded(r, 5);
        using (var fill = new SolidBrush(_hover ? Theme.ButtonHover : Theme.Button)) g.FillPath(fill, path);
        if (_active)
        {
            using var pen = new Pen(AccentColor, 2.5f);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? ForeColor : Theme.ForeDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }
}

/// <summary>Dark, button-styled drop-down list (owner-drawn closed state and items).</summary>
public class RComboBox : ComboBox
{
    public Color AccentColor { get; set; } = Theme.Accent;

    public RComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Button;
        ForeColor = Theme.Fore;
        Font = Theme.Normal;
        Cursor = Cursors.Hand;
        ItemHeight = (int)(Font.Height * 1.9f);   // tall, touch-friendly rows; also sets control height
        DropDownHeight = ItemHeight * 12;
    }

    // Don't change selection with the mouse wheel while closed (easy to do by accident on touch scrolling).
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!DroppedDown && e is HandledMouseEventArgs h) h.Handled = true;
        else base.OnMouseWheel(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool closedPart = (e.State & DrawItemState.ComboBoxEdit) != 0;
        if (closedPart) return; // closed state is painted in WndProc
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(sel ? Theme.ButtonHover : Theme.Panel)) e.Graphics.FillRectangle(bg, e.Bounds);
        if (sel) using (var bar = new SolidBrush(AccentColor)) e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y + 4, 4, e.Bounds.Height - 8);
        var r = new Rectangle(e.Bounds.X + Font.Height / 2 + 4, e.Bounds.Y, e.Bounds.Width - Font.Height, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, r, Theme.Fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    const int WM_PAINT = 0x000F;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Never touch .Handle here: during CreateHandle that recursively re-creates the window (error 1400).
        if (m.Msg != WM_PAINT || !IsHandleCreated) return;
        using var g = Graphics.FromHwnd(m.HWnd);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        var rect = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var path = Theme.Rounded(rect, 5))
        {
            using var fill = new SolidBrush(Focused || DroppedDown ? Theme.ButtonHover : Theme.Button);
            g.FillPath(fill, path);
            if (DroppedDown) { using var pen = new Pen(AccentColor, 2); g.DrawPath(pen, path); }
        }
        int arrowW = Height;
        var textRect = new Rectangle(Font.Height / 2 + 2, 0, Width - arrowW - Font.Height / 2, Height);
        string text = SelectedIndex >= 0 ? GetItemText(SelectedItem) ?? "" : "";
        TextRenderer.DrawText(g, text, Font, textRect, Enabled ? Theme.Fore : Theme.ForeDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        // chevron
        float cx = Width - arrowW / 2f, cy = Height / 2f, s = Height / 7f;
        using var chev = new Pen(Theme.ForeDim, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(chev, [new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2)]);
    }
}

/// <summary>Large, theme-coloured checkbox.</summary>
public class RCheckBox : CheckBox
{
    bool _hover;
    public Color AccentColor { get; set; } = Theme.Accent;

    public RCheckBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        ForeColor = Theme.Fore;
        Font = Theme.Normal;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        float box = Math.Min(Height - 4, Font.Height * 1.25f);
        var r = new RectangleF(2, (Height - box) / 2, box, box);
        using (var path = Theme.Rounded(r, box / 5))
        {
            if (Checked)
            {
                using var fill = new SolidBrush(Enabled ? AccentColor : Theme.Border);
                g.FillPath(fill, path);
                using var tick = new Pen(Color.White, box / 8) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(tick, [new PointF(r.X + box * 0.24f, r.Y + box * 0.52f), new PointF(r.X + box * 0.43f, r.Y + box * 0.70f), new PointF(r.X + box * 0.77f, r.Y + box * 0.32f)]);
            }
            else
            {
                using var fill = new SolidBrush(_hover ? Theme.ButtonHover : Theme.Button);
                g.FillPath(fill, path);
                using var pen = new Pen(_hover ? AccentColor : Theme.Border, 1.5f);
                g.DrawPath(pen, path);
            }
        }
        var tr = new Rectangle((int)(box + Font.Height * 0.6f), 0, Width - (int)box - Font.Height, Height);
        TextRenderer.DrawText(g, Text, Font, tr, Enabled ? ForeColor : Theme.ForeDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Horizontal slider with integer range.</summary>
public class Slider : Control
{
    int _min, _max = 100, _value;
    bool _drag;
    public Color AccentColor { get; set; } = Theme.Accent;
    public event EventHandler? ValueChanged;
    /// <summary>Raised once when the user releases the thumb.</summary>
    public event EventHandler? ValueCommitted;

    public int Minimum { get => _min; set { _min = value; Value = _value; Invalidate(); } }
    public int Maximum { get => _max; set { _max = value; Value = _value; Invalidate(); } }
    /// <summary>Values snap to Minimum + n·Step; arrow keys move one step.</summary>
    public int Step { get; set; } = 1;

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, Math.Max(_min, _max));
            if (Step > 1) v = Math.Clamp(_min + (int)Math.Round((v - _min) / (double)Step) * Step, _min, _max);
            if (v == _value) return;
            _value = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Height = 28;
        Cursor = Cursors.Hand;
    }

    float Thumb => Height / 2.6f;
    float Left0 => Thumb + 1;
    float Width0 => Width - 2 * Thumb - 2;
    float XFor(int v) => Left0 + (_max == _min ? 0 : (v - _min) * Width0 / (_max - _min));

    void SetFromX(int x) => Value = (int)Math.Round(_min + (x - Left0) / Width0 * (_max - _min));

    protected override void OnMouseDown(MouseEventArgs e) { _drag = true; SetFromX(e.X); Focus(); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_drag) return;
        _drag = false; ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) { Value -= Step; ValueCommitted?.Invoke(this, EventArgs.Empty); }
        if (e.KeyCode == Keys.Right) { Value += Step; ValueCommitted?.Invoke(this, EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        float cy = Height / 2f, x = XFor(_value);
        using (var track = new Pen(Theme.Border, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, Left0, cy, Left0 + Width0, cy);
        var accent = Enabled ? AccentColor : Theme.ForeDim;
        using (var fill = new Pen(accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(fill, Left0, cy, x, cy);
        using var thumb = new SolidBrush(Focused && Enabled ? Color.White : accent);
        g.FillEllipse(thumb, x - Thumb / 2, cy - Thumb / 2, Thumb, Thumb);
    }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

/// <summary>Editable 10-point fan curve: X = temperature (fixed firmware sensor points), Y = fan duty %.</summary>
public class FanCurveChart : Control
{
    public int[] Temps { get; set; } = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
    public int[] Duty { get; set; } = [25, 27, 32, 36, 41, 45, 51, 55, 59, 64];
    public string Title { get; set; } = "Fan Curve, %/°C";
    public event EventHandler? CurveChanged;

    bool _readOnly;
    public bool Dragging => _drag >= 0;

    /// <summary>Display-only curve: no points, no dragging.</summary>
    public bool ReadOnly
    {
        get => _readOnly;
        set { _readOnly = value; _hover = _drag = -1; Cursor = Cursors.Default; Invalidate(); }
    }

    int _drag = -1, _hover = -1;
    const int TMin = 0, TMax = 110;

    public FanCurveChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Font = Theme.Normal;
    }

    Rectangle Plot
    {
        get
        {
            int padL = (int)(Font.Height * 2.6), padT = Font.Height * 2, padB = (int)(Font.Height * 1.8), padR = Font.Height;
            return new Rectangle(padL, padT, Width - padL - padR, Height - padT - padB);
        }
    }

    PointF Pt(int i)
    {
        var p = Plot;
        return new PointF(p.Left + (Temps[i] - TMin) * p.Width / (float)(TMax - TMin),
                          p.Bottom - Duty[i] * p.Height / 100f);
    }

    int Hit(Point m)
    {
        float r = Font.Height * 0.9f;
        for (int i = 0; i < Duty.Length; i++)
        {
            var p = Pt(i);
            if (Math.Abs(p.X - m.X) < r && Math.Abs(p.Y - m.Y) < r * 1.5f) return i;
        }
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e) { if (_readOnly) return; _drag = Hit(e.Location); Capture = _drag >= 0; }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag >= 0) CurveChanged?.Invoke(this, EventArgs.Empty);
        _drag = -1; Capture = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_readOnly) return;
        if (_drag < 0)
        {
            int h = Hit(e.Location);
            if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.SizeNS : Cursors.Default; Invalidate(); }
            return;
        }
        var p = Plot;
        int v = (int)Math.Round((p.Bottom - e.Y) * 100f / p.Height);
        v = Math.Clamp(v, 0, 100);
        Duty[_drag] = v;
        // Keep the curve monotonic: lower points can't exceed, higher points can't be below.
        for (int i = 0; i < _drag; i++) Duty[i] = Math.Min(Duty[i], v);
        for (int i = _drag + 1; i < Duty.Length; i++) Duty[i] = Math.Max(Duty[i], v);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var p = Plot;

        TextRenderer.DrawText(g, Title, Font, new Rectangle(0, 4, Width, Font.Height + 4), Theme.Fore, TextFormatFlags.HorizontalCenter);

        using var grid = new Pen(Theme.Grid);
        var flagsR = TextFormatFlags.Right | TextFormatFlags.VerticalCenter;
        for (int y = 0; y <= 100; y += 20)
        {
            float py = p.Bottom - y * p.Height / 100f;
            g.DrawLine(grid, p.Left, py, p.Right, py);
            TextRenderer.DrawText(g, y == 0 ? "OFF" : $"{y}%", Font, new Rectangle(0, (int)py - Font.Height, p.Left - 6, Font.Height * 2), Theme.ForeDim, flagsR);
        }
        for (int t = 10; t <= TMax; t += 10)
        {
            float px = p.Left + (t - TMin) * p.Width / (float)(TMax - TMin);
            g.DrawLine(grid, px, p.Top, px, p.Bottom);
            TextRenderer.DrawText(g, t.ToString(), Font, new Rectangle((int)px - 20, p.Bottom + 2, 40, Font.Height + 2), Theme.ForeDim, TextFormatFlags.HorizontalCenter);
        }

        var pts = Enumerable.Range(0, Duty.Length).Select(Pt).ToArray();
        using (var line = new Pen(Color.FromArgb(150, 150, 150), 2)) g.DrawLines(line, pts);
        if (_readOnly) return;
        float r = Font.Height * 0.38f;
        for (int i = 0; i < pts.Length; i++)
        {
            bool hot = i == _hover || i == _drag;
            using var b = new SolidBrush(hot ? Color.White : Color.FromArgb(185, 185, 185));
            g.FillEllipse(b, pts[i].X - r, pts[i].Y - r, 2 * r, 2 * r);
            if (hot)
                TextRenderer.DrawText(g, $"{Temps[i]}°C, {Duty[i]}%", Font, new Point((int)pts[i].X + 8, (int)pts[i].Y - Font.Height - 6), Theme.Fore);
        }
    }
}
