using LegionHelper.Hardware;
using LegionHelper.UI;

namespace LegionHelper;

/// <summary>"Fans + Power" window: custom-mode power limits and the fan curve.</summary>
public class FansForm : Form
{
    readonly ModeControl _mc;
    readonly Slider _spl = new(), _sppt = new(), _fppt = new();
    readonly Label _splVal = new(), _spptVal = new(), _fpptVal = new(), _status = new(), _lblSensors = new();
    readonly FanCurveChart _chart = new();
    readonly RCheckBox _chkFanCurve = new(), _chkFullSpeed = new(), _chkAllModes = new();
    readonly Dictionary<ThermalMode, RButton> _tabs = new();
    readonly RButton _resetBtn = new() { Text = "Factory Defaults" };
    readonly ToolTip _tips = new() { InitialDelay = 300, AutoPopDelay = 8000 };
    ThermalMode _view = ThermalMode.Custom;
    ThermalMode? _lastMode;
    bool _loading;
    float _k;

    public FansForm(ModeControl mc)
    {
        _mc = mc;
        Text = "Fans and Power - " + CpuName();
        BackColor = Theme.Back; ForeColor = Theme.Fore; Font = Theme.Normal;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.Manual; ShowInTaskbar = false;
        Icon = TrayIcon.Make(Theme.Custom);
        _k = DeviceDpi / 96f;
        Build();
        LoadValues();
    }

    int S(int px) => (int)(px * _k);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

    static string CpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim().Replace(" w/ Radeon Graphics", "") ?? "CPU";
        }
        catch { return "CPU"; }
    }

    void Build()
    {
        int pad = S(14), leftW = S(300), chartW = S(520), y = pad;

        Label L(string text, int x, int yy, int w, Font? f = null, ContentAlignment a = ContentAlignment.MiddleLeft)
        {
            var l = new Label { Text = text, Font = f ?? Theme.Normal, ForeColor = Theme.Fore, TextAlign = a };
            l.SetBounds(x, yy, w, S(24)); Controls.Add(l); return l;
        }

        L("Power Limits", pad, y, leftW, Theme.Title);
        y += S(30);
        // View tabs: Custom is editable; firmware presets are shown read-only. Viewing never changes the mode.
        (ThermalMode m, string text)[] tabs = [(ThermalMode.Custom, "Custom"), (ThermalMode.Quiet, "Quiet"), (ThermalMode.Balanced, "Balanced"), (ThermalMode.Performance, "Perf.")];
        int tg = S(6), tw = (leftW - 3 * tg) / 4;
        for (int i = 0; i < tabs.Length; i++)
        {
            var (m, text) = tabs[i];
            var b = new RButton { Text = text, AccentColor = MainForm.ModeColor(m), Font = Theme.Normal };
            b.SetBounds(pad + i * (tw + tg), y, tw, S(34));
            b.Click += (_, _) => SetView(m);
            _tips.SetToolTip(b, $"{MainForm.ModeName(m)}: {_mc.LimitsText(m)}" + (m == ThermalMode.Custom ? " (editable)" : " (firmware preset, read-only)"));
            _tabs[m] = b; Controls.Add(b);
        }
        y += S(44);

        void Row(Slider s, Label val, string name, uint id)
        {
            var r = _mc.Range(id);
            L(name, pad, y, leftW - S(60), Theme.Title);
            val.Font = Theme.Title; val.ForeColor = Theme.Fore; val.TextAlign = ContentAlignment.MiddleRight;
            val.SetBounds(pad + leftW - S(60), y, S(60), S(24)); Controls.Add(val);
            y += S(26);
            s.Minimum = r.Min; s.Maximum = r.Max; s.AccentColor = Theme.Custom;
            s.SetBounds(pad, y, leftW, S(30));
            s.ValueChanged += (_, _) => { val.Text = $"{s.Value}W"; if (!_loading) Enforce(s); };
            s.ValueCommitted += (_, _) => Commit();
            Controls.Add(s);
            var rangeL = L($"{r.Min}W", pad, y + S(28), S(60), null);
            rangeL.ForeColor = Theme.ForeDim;
            var rangeR = L($"{r.Max}W", pad + leftW - S(60), y + S(28), S(60), null, ContentAlignment.MiddleRight);
            rangeR.ForeColor = Theme.ForeDim;
            _rangeRows.Add((s, rangeL, rangeR, id));
            y += S(62);
        }

        Row(_spl, _splVal, "SPL (sustained)", LenovoWmi.CustomSPL);
        Row(_sppt, _spptVal, "SPPT (long boost)", LenovoWmi.CustomSPPT);
        Row(_fppt, _fpptVal, "FPPT (short boost)", LenovoWmi.CustomFPPT);

        Check(_chkFullSpeed, "Fans at full speed", pad, y, leftW);
        _chkFullSpeed.Click += (_, _) =>
        {
            bool on = _chkFullSpeed.Checked;
            Task.Run(() => { try { LenovoWmi.SetFeature(LenovoWmi.FullSpeedFan, on ? 1 : 0); } catch { } });
        };
        y += S(34);

        _status.ForeColor = Theme.ForeDim; _status.Font = Theme.Normal;
        _status.SetBounds(pad, y, leftW, S(48)); Controls.Add(_status);
        y += S(52);

        // Right column: fan curve
        int cx = pad + leftW + S(20), cy = pad;
        _lblSensors.SetBounds(cx, cy, chartW, S(24));
        _lblSensors.Font = Theme.Title; _lblSensors.ForeColor = Theme.Fore; _lblSensors.TextAlign = ContentAlignment.MiddleRight;
        L("Fan Curve", cx, cy, chartW / 2, Theme.Title);
        Controls.Add(_lblSensors);
        cy += S(30);
        _chart.SetBounds(cx, cy, chartW, S(330));
        _chart.Title = "CPU Fan Profile, %/°C";
        _chart.CurveChanged += (_, _) =>
        {
            _mc.Cfg.FanCurve = (int[])_chart.Duty.Clone(); _mc.Cfg.Save();
            if (!_mc.Cfg.ApplyFanCurve) return;
            // In a preset with "all modes" on, apply in place; otherwise editing the curve means Custom.
            if (_mc.Cfg.FanCurveAllModes && _mc.CurrentMode != ThermalMode.Custom) ApplyFanNow(); else Commit();
        };
        Controls.Add(_chart);
        cy += S(340);

        var reset = _resetBtn;
        reset.SetBounds(cx, cy, S(160), S(38));
        reset.Click += (_, _) => FactoryDefaults();
        Controls.Add(reset);

        Check(_chkAllModes, "Use in all modes", cx + chartW - S(340), cy + S(5), S(165));
        _tips.SetToolTip(_chkAllModes, "The firmware has one fan table for every mode. Off: Quiet/Balanced/Performance use the factory curve. On: your curve is used everywhere.");
        _chkAllModes.Click += (_, _) => { _mc.Cfg.FanCurveAllModes = _chkAllModes.Checked; _mc.Cfg.Save(); ApplyFanNow(); SetView(_view); };
        Check(_chkFanCurve, "Apply Fan Curve", cx + chartW - S(170), cy + S(5), S(170));
        _chkFanCurve.Click += (_, _) =>
        {
            _mc.Cfg.ApplyFanCurve = _chkFanCurve.Checked; _mc.Cfg.Save();
            if (_chkFanCurve.Checked && !_mc.Cfg.FanCurveAllModes && _mc.CurrentMode != ThermalMode.Custom) Commit(); // switches to Custom
            else ApplyFanNow();                                                                                     // writes custom or factory table
        };
        cy += S(38) + pad;

        ClientSize = new Size(cx + chartW + pad, Math.Max(y, cy));
    }

    void Check(RCheckBox c, string text, int x, int y, int w)
    {
        c.Text = text; c.AccentColor = Theme.Custom;
        c.SetBounds(x, y, w, S(30)); Controls.Add(c);
    }

    void LoadValues()
    {
        _loading = true;
        _spl.Value = _mc.Cfg.SPL; _sppt.Value = _mc.Cfg.SPPT; _fppt.Value = _mc.Cfg.FPPT;
        _splVal.Text = $"{_spl.Value}W"; _spptVal.Text = $"{_sppt.Value}W"; _fpptVal.Text = $"{_fppt.Value}W";
        if (_mc.Cfg.FanCurve is { Length: LenovoWmi.FanPoints } f) _chart.Duty = (int[])f.Clone();
        if (_mc.Cfg.FanTemps is { Length: LenovoWmi.FanPoints } t) _chart.Temps = (int[])t.Clone();
        _chkFanCurve.Checked = _mc.Cfg.ApplyFanCurve;
        _chkAllModes.Checked = _mc.Cfg.FanCurveAllModes;
        _chkFullSpeed.Checked = LenovoWmi.TryGetFeature(LenovoWmi.FullSpeedFan) == 1;
        _loading = false;
        RefreshUI();
    }

    /// <summary>Keep SPL ≤ SPPT ≤ FPPT while dragging.</summary>
    void Enforce(Slider moved)
    {
        _loading = true;
        if (moved == _spl) { if (_sppt.Value < _spl.Value) _sppt.Value = _spl.Value; if (_fppt.Value < _sppt.Value) _fppt.Value = _sppt.Value; }
        if (moved == _sppt) { if (_spl.Value > _sppt.Value) _spl.Value = _sppt.Value; if (_fppt.Value < _sppt.Value) _fppt.Value = _sppt.Value; }
        if (moved == _fppt) { if (_sppt.Value > _fppt.Value) _sppt.Value = _fppt.Value; if (_spl.Value > _sppt.Value) _spl.Value = _sppt.Value; }
        _loading = false;
    }

    /// <summary>Saves values and applies them by switching to (or re-applying) Custom mode.</summary>
    void Commit()
    {
        // Sliders hold preset values while a firmware preset is being viewed; only take them from the Custom view.
        if (_view == ThermalMode.Custom) { _mc.Cfg.SPL = _spl.Value; _mc.Cfg.SPPT = _sppt.Value; _mc.Cfg.FPPT = _fppt.Value; }
        _mc.Cfg.FanCurve = (int[])_chart.Duty.Clone();
        _mc.Cfg.Save();
        _status.Text = "Applying…";
        Task.Run(() =>
        {
            if (_mc.CurrentMode == ThermalMode.Custom) { try { _mc.ApplyCustom(); } catch (Exception ex) { return ex.Message; } }
            else _mc.SelectMode(ThermalMode.Custom);
            return null;
        }).ContinueWith(t => { if (IsHandleCreated) BeginInvoke(() => { RefreshUI(); if (t.Result is { } err) _status.Text = "Error: " + err; }); });
    }

    /// <summary>
    /// Viewing a firmware preset without "Use in all modes": show that preset's factory fan curve, read-only and
    /// without points, and hide the custom-curve controls. Otherwise the editable custom curve.
    /// </summary>
    void UpdateFanView(ThermalMode view)
    {
        bool factory = view != ThermalMode.Custom && !_mc.Cfg.FanCurveAllModes;
        if (_chart.Dragging) return; // never clobber a point the user is moving
        _chart.ReadOnly = factory;
        _chart.Title = factory ? $"Factory fan curve for {MainForm.ModeName(view)}" : "CPU Fan Profile, %/°C";
        _chart.Duty = (int[])(factory ? _mc.FactoryCurve
                                      : _mc.Cfg.FanCurve is { Length: LenovoWmi.FanPoints } c ? c : _mc.FactoryCurve).Clone();
        _chart.Invalidate();
        _chkFanCurve.Visible = _chkAllModes.Visible = _resetBtn.Visible = !factory;
    }

    /// <summary>Writes the fan table appropriate for the current mode (custom curve or factory) without changing mode.</summary>
    void ApplyFanNow() => Task.Run(() => _mc.ApplyFanTableFor(_mc.CurrentMode));

    void FactoryDefaults()
    {
        SetView(ThermalMode.Custom);
        _loading = true;
        _spl.Value = _mc.Range(LenovoWmi.CustomSPL).Default;
        _sppt.Value = _mc.Range(LenovoWmi.CustomSPPT).Default;
        _fppt.Value = _mc.Range(LenovoWmi.CustomFPPT).Default;
        _loading = false;
        if (_mc.Cfg.FactoryFanCurve is { Length: LenovoWmi.FanPoints } f) { _chart.Duty = (int[])f.Clone(); _chart.Invalidate(); }
        Commit();
    }

    readonly List<(Slider s, Label min, Label max, uint id)> _rangeRows = new();

    public void RefreshUI()
    {
        // Ranges differ on AC vs battery; keep sliders in sync with what the firmware currently allows.
        _loading = true;
        foreach (var (s, min, max, id) in _rangeRows)
        {
            var r = _mc.Range(id);
            if (s.Minimum != r.Min || s.Maximum != r.Max)
            {
                s.Minimum = r.Min; s.Maximum = r.Max;
                min.Text = $"{r.Min}W"; max.Text = $"{r.Max}W";
            }
        }
        _loading = false;

        // Follow the active mode when it changes (e.g. via a shortcut); otherwise keep the tab the user picked.
        if (_lastMode != _mc.CurrentMode) { _lastMode = _mc.CurrentMode; SetView(_mc.CurrentMode); return; }
        SetView(_view);
    }

    /// <summary>Shows Custom (editable) or a firmware preset (read-only) in the power-limit sliders.</summary>
    void SetView(ThermalMode view)
    {
        if (!_tabs.ContainsKey(view)) view = ThermalMode.Custom;
        _view = view;
        foreach (var (m, b) in _tabs) b.Active = m == view;
        bool custom = view == ThermalMode.Custom;

        _loading = true;
        var lim = _mc.PresetLimits(view);
        if (lim is { } l)
        {
            _spl.Value = l.spl; _sppt.Value = l.sppt; _fppt.Value = l.fppt;      // clamped to the slider range
            _splVal.Text = $"{l.spl}W"; _spptVal.Text = $"{l.sppt}W"; _fpptVal.Text = $"{l.fppt}W"; // true values
        }
        foreach (var s in new[] { _spl, _sppt, _fppt }) s.Enabled = custom;
        _loading = false;
        UpdateFanView(view);

        bool active = _mc.CurrentMode == view;
        _status.Text = custom
            ? (active ? $"Custom mode active: {_mc.Cfg.SPL}W / {_mc.Cfg.SPPT}W / {_mc.Cfg.FPPT}W" + (_mc.Cfg.ApplyFanCurve ? ", custom fan curve" : "")
                      : $"Current mode: {MainForm.ModeName(_mc.CurrentMode)}. Changing a value switches to Custom.")
            : $"{MainForm.ModeName(view)} preset (built into the firmware, read-only)" + (active ? " - active now." : ".");
    }

    public void UpdateSensors(int? temp, int? rpm) =>
        _lblSensors.Text = $"CPU: {(temp?.ToString() ?? "-")}°C  Fan: {(rpm?.ToString() ?? "-")}RPM";

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }
}
