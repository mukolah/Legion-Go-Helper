using System.Drawing.Drawing2D;
using LegionHelper.Hardware;
using LegionHelper.UI;

namespace LegionHelper;

/// <summary>"RGB" window: built-in (firmware) and live (per-LED) lighting effects with a live preview of both rings.</summary>
public class RgbForm : Form
{
    readonly Config _cfg;
    readonly Dictionary<string, RButton> _buttons = new();
    readonly RingPreview _preview = new();
    readonly RButton _color = new() { Text = "Color" };
    readonly Slider _brightness = new(), _speed = new();
    readonly Label _brightVal = new(), _speedVal = new(), _hint = new(), _status = new();
    Label _speedLabel = new();
    readonly Label _dlWarn = new();
    readonly RButton _dlButton = new() { Text = "Open settings", AccentColor = Theme.Custom };
    long _nextDlCheck;

    /// <summary>
    /// "Use Dynamic Lighting on my devices": HKCU\Software\Microsoft\Lighting\AmbientLightingEnabled (global) and the
    /// same value per device under ...\Lighting\Devices\&lt;device id&gt; (checked for the Legion controller, VID_1A86).
    /// </summary>
    static bool WindowsDynamicLightingOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Lighting");
            if (key is null) return false;
            if (key.GetValue("AmbientLightingEnabled") is int g && g != 0) return true;
            using var devices = key.OpenSubKey("Devices");
            foreach (var name in devices?.GetSubKeyNames() ?? [])
            {
                if (!name.Contains("VID_1A86", StringComparison.OrdinalIgnoreCase)) continue;
                using var dev = devices!.OpenSubKey(name);
                if (dev?.GetValue("AmbientLightingEnabled") is int d && d != 0) return true;
            }
        }
        catch { }
        return false;
    }

    void UpdateDynamicLightingWarning()
    {
        bool show = ((Lighting.Find(_cfg.RgbEffect).Live && _cfg.RgbEffect is not null) || Lighting.ChargingDisplayActive) && WindowsDynamicLightingOn();
        _dlWarn.Visible = _dlButton.Visible = show;
    }
    readonly RCheckBox _lowBat = new(), _chargeShow = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 66 };
    bool _loading;
    float _k;

    public RgbForm(Config cfg)
    {
        _cfg = cfg;
        Text = "RGB - stick lighting";
        BackColor = Theme.Back; ForeColor = Theme.Fore; Font = Theme.Normal;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.Manual; ShowInTaskbar = false;
        Icon = TrayIcon.Make(Theme.Custom);
        _k = DeviceDpi / 96f;
        Build();
        _timer.Tick += (_, _) =>
        {
            _preview.Invalidate(); _status.Text = StatusText();
            if (Environment.TickCount64 >= _nextDlCheck) { _nextDlCheck = Environment.TickCount64 + 2000; UpdateDynamicLightingWarning(); }
            // at rest, the Preview slider thumb follows the live value
            if (Lighting.IsDataEffect(_cfg.RgbEffect) && Lighting.PreviewValue is null && !_speed.Capture)
            {
                _loading = true; _speed.Value = 1 + (int)Math.Round(Lighting.LiveFraction * 99); _loading = false;
                _speedVal.Text = "Live";
            }
        };
        VisibleChanged += (_, _) => { _timer.Enabled = Visible; Lighting.PreviewValue = null; if (Visible) RefreshUI(); };
    }

    int S(int px) => (int)(px * _k);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

    void Build()
    {
        int pad = S(14), leftW = S(320), rightW = S(360), gap = S(26), x2 = pad + leftW + gap, y = pad;
        Label L(string t, int x, int yy, int w, Font? f = null, Color? c = null, int h = 26)
        {
            var l = new Label { Text = t, Font = f ?? Theme.Normal, ForeColor = c ?? Theme.Fore };
            l.SetBounds(x, yy, w, S(h)); Controls.Add(l); return l;
        }
        void Grid(IEnumerable<Lighting.Effect> effects)
        {
            int bw = (leftW - S(8)) / 2, i = 0;
            foreach (var e in effects)
            {
                var b = new RButton { Text = e.Name, AccentColor = e.Live ? Theme.Custom : Theme.Quiet };
                b.SetBounds(pad + (i % 2) * (bw + S(8)), y + (i / 2) * S(52), bw, S(44));
                string id = e.Id;
                b.Click += (_, _) => Select(id);
                _buttons[id] = b; Controls.Add(b); i++;
            }
            y += ((i + 1) / 2) * S(52);
        }

        L("Built-in effects", pad, y, leftW, Theme.Title); y += S(30);
        L("Run inside the controller: no CPU use, and they keep working when the app is closed.", pad, y, leftW, null, Theme.ForeDim, 44); y += S(50);
        Grid(Lighting.Effects.Where(e => !e.Live));
        y += S(8);
        L("Live effects", pad, y, leftW, Theme.Title); y += S(30);
        L("Drawn LED by LED by Legion Helper; keep the app running in the tray.", pad, y, leftW, null, Theme.ForeDim, 44); y += S(50);
        Grid(Lighting.Effects.Where(e => e.Live));
        int leftBottom = y;

        // ---- right column
        y = pad;
        L("Preview", x2, y, rightW, Theme.Title); y += S(30);
        _preview.SetBounds(x2, y, rightW, S(170)); Controls.Add(_preview);
        y += S(180);

        _color.SetBounds(x2, y, S(120), S(38));
        _color.Click += (_, _) => PickColor();
        Controls.Add(_color);
        _lowBat.Text = "Low battery warning";
        _lowBat.AccentColor = Theme.Performance;
        _lowBat.SetBounds(x2 + S(132), y + S(4), rightW - S(132), S(30));
        _lowBat.Checked = _cfg.RgbLowBatteryWarning;
        _lowBat.Click += (_, _) => { _cfg.RgbLowBatteryWarning = _lowBat.Checked; _cfg.Save(); };
        Controls.Add(_lowBat);
        y += S(44);
        _chargeShow.Text = "Show charge level while plugged in";
        _chargeShow.AccentColor = Theme.Quiet;
        _chargeShow.SetBounds(x2, y, rightW, S(30));
        _chargeShow.Checked = _cfg.RgbChargingDisplay;
        _chargeShow.Click += (_, _) => { _cfg.RgbChargingDisplay = _chargeShow.Checked; _cfg.Save(); Task.Run(Lighting.Apply); };
        Controls.Add(_chargeShow);
        y += S(44);

        Label SliderRow(Slider s, Label val, string name, int min, int max)
        {
            var label = L(name, x2, y, rightW - S(90));
            val.ForeColor = Theme.Fore; val.TextAlign = ContentAlignment.MiddleRight;
            val.SetBounds(x2 + rightW - S(90), y, S(90), S(26)); Controls.Add(val);
            y += S(26);
            s.Minimum = min; s.Maximum = max; s.AccentColor = Theme.Custom;
            s.SetBounds(x2, y, rightW, S(28)); Controls.Add(s);
            s.ValueChanged += (_, _) => val.Text = $"{s.Value}%";
            y += S(36);
            return label;
        }
        SliderRow(_brightness, _brightVal, "Brightness", 5, 100);
        _speedLabel = SliderRow(_speed, _speedVal, "Speed", 1, 100);
        _brightness.ValueCommitted += (_, _) => { if (_loading) return; _cfg.RgbBrightness = _brightness.Value; _cfg.Save(); ReapplyFirmware(); };
        // Second slider: "Speed" for animations, "Preview" (hold & drag to simulate) for data-driven effects.
        _speed.ValueChanged += (_, _) =>
        {
            if (_loading || !Lighting.IsDataEffect(_cfg.RgbEffect)) return;
            double v = (_speed.Value - 1) / 99.0;
            Lighting.PreviewValue = v;
            _speedVal.Text = _cfg.RgbEffect == "temp" ? $"{30 + v * 70:0} °C" : $"{v * 100:0}%";
        };
        _speed.ValueCommitted += (_, _) =>
        {
            if (_loading) return;
            if (Lighting.IsDataEffect(_cfg.RgbEffect)) { Lighting.PreviewValue = null; _speedVal.Text = "Live"; return; }
            _cfg.RgbSpeed = _speed.Value; _cfg.Save(); ReapplyFirmware();
        };

        _hint.ForeColor = Theme.ForeDim; _hint.SetBounds(x2, y, rightW, S(44)); Controls.Add(_hint);
        y += S(46);
        _status.ForeColor = Theme.ForeDim; _status.SetBounds(x2, y, rightW, S(44)); Controls.Add(_status);
        y += S(48);

        // Warning when Windows Dynamic Lighting could fight the live effects
        _dlWarn.Text = "Windows Dynamic Lighting is on and may override these lights.";
        _dlWarn.ForeColor = Theme.Custom; _dlWarn.SetBounds(x2, y, rightW - S(140), S(44)); Controls.Add(_dlWarn);
        _dlButton.SetBounds(x2 + rightW - S(132), y + S(4), S(132), S(36));
        _dlButton.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true });
        Controls.Add(_dlButton);
        _dlWarn.Visible = _dlButton.Visible = false;
        y += S(48);

        ClientSize = new Size(x2 + rightW + pad, Math.Max(leftBottom, y) + pad);
    }

    void RefreshUI()
    {
        _loading = true;
        foreach (var (id, b) in _buttons) b.Active = id == _cfg.RgbEffect;
        var eff = _cfg.RgbEffect is null ? null : Lighting.Find(_cfg.RgbEffect);
        _hint.Text = eff?.Hint ?? "Choose an effect.";
        bool data = Lighting.IsDataEffect(_cfg.RgbEffect);
        Lighting.PreviewValue = null;
        _brightness.Value = _cfg.RgbBrightness;
        _speed.Value = data ? 50 : _cfg.RgbSpeed;
        _speedLabel.Text = data ? "Preview (hold and drag)" : "Speed";
        _brightVal.Text = $"{_brightness.Value}%";
        _speedVal.Text = data ? "Live" : $"{_speed.Value}%";
        _color.AccentColor = Color.FromArgb(_cfg.RgbColor); _color.Active = true;
        // colour applies to solid/breathe/comet; speed to breathe/chroma/rainbow/comet/wheel
        string cur = _cfg.RgbEffect ?? "";
        _color.Enabled = cur is "solid" or "breathe" or "comet";
        _speed.Enabled = cur is "breathe" or "chroma" or "rainbow" or "comet" or "wheel" || data;
        _brightness.Enabled = cur != "off";
        _status.Text = StatusText();
        UpdateDynamicLightingWarning();
        _loading = false;
    }

    /// <summary>Only transitional or error states are worth showing; steady states repeat the selected button.</summary>
    static string StatusText()
    {
        string s = Lighting.Status;
        return s == "Live" || s == "Lights off" || s.StartsWith("Built-in effect") ? "" : s;
    }

    void Select(string id)
    {
        _cfg.RgbEffect = id; _cfg.Save();
        RefreshUI();
        Task.Run(Lighting.Apply);
    }

    void ReapplyFirmware()
    {
        // live effects read the settings every frame; firmware effects need the profile rewritten
        if (!Lighting.Find(_cfg.RgbEffect).Live) Task.Run(Lighting.Apply);
    }

    void PickColor()
    {
        using var dlg = new ColorDialog { Color = Color.FromArgb(_cfg.RgbColor), FullOpen = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _cfg.RgbColor = Color.FromArgb(255, dlg.Color).ToArgb(); _cfg.Save();
        RefreshUI();
        ReapplyFirmware();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    /// <summary>Draws both stick rings with their 6 LEDs each (lamp N = one LED per ring, right = left rotated 180°).</summary>
    sealed class RingPreview : Control
    {
        static readonly int[] LeftClock = [9, 11, 1, 3, 5, 7];

        public RingPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float r = Height * 0.32f, d = Height * 0.15f;
            for (int ring = 0; ring < 2; ring++)
            {
                float cx = Width * (ring == 0 ? 0.27f : 0.73f), cy = Height * 0.5f;
                using (var pen = new Pen(Theme.Border, d * 0.22f))
                    foreach (var start in new[] { 218f, 338f, 98f }) g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, start, 104);
                TextRenderer.DrawText(g, ring == 0 ? "L" : "R", Theme.Title, new Rectangle((int)(cx - 20), (int)(cy - 14), 40, 28), Theme.ForeDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                for (int lamp = 0; lamp < LampArray.Count; lamp++)
                {
                    int hour = LeftClock[lamp] + (ring == 1 ? 6 : 0);
                    double a = hour / 12.0 * 2 * Math.PI - Math.PI / 2;
                    float x = cx + (float)(r * Math.Cos(a)), y = cy + (float)(r * Math.Sin(a));
                    var c = Lighting.Current[lamp];
                    int lum = Math.Max(c.R, Math.Max(c.G, c.B));
                    if (lum > 40)
                        using (var glow = new SolidBrush(Color.FromArgb(Math.Min(90, lum / 3), c))) g.FillEllipse(glow, x - d * 0.85f, y - d * 0.85f, d * 1.7f, d * 1.7f);
                    using (var dot = new SolidBrush(lum < 12 ? Theme.Button : c)) g.FillEllipse(dot, x - d / 2, y - d / 2, d, d);
                }
            }
        }
    }
}
