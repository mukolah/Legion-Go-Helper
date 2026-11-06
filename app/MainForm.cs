using LegionHelper.Hardware;
using LegionHelper.UI;

namespace LegionHelper;

public class MainForm : Form
{
    readonly ModeControl _mc;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };

    readonly Label _lblMode = new(), _lblSensors = new(), _lblScreen = new(), _lblBattery = new(), _lblBatteryInfo = new();
    readonly Dictionary<ThermalMode, RButton> _modeButtons = new();
    readonly List<(RButton btn, int hz)> _rateButtons = new();
    readonly RComboBox _winPower = new();
    readonly RCheckBox _chkCharge = new(), _chkStartup = new(), _chkHidden = new();
    FansForm? _fans;
    ControllerForm? _controller;
    ExtraForm? _extra;
    readonly RButton _lightsBtn = new() { Text = "Lights" };
    readonly ToolTip _modeTips = new() { InitialDelay = 300, AutoPopDelay = 8000 };
    bool? _lightsOn;
    bool _sensorBusy;
    float _k = 1f;

    static readonly (ThermalMode mode, string name, Color color)[] Modes =
    [
        (ThermalMode.Quiet, "Quiet", Theme.Quiet),
        (ThermalMode.Balanced, "Balanced", Theme.Balanced),
        (ThermalMode.Performance, "Performance", Theme.Performance),
        (ThermalMode.Custom, "Custom", Theme.Custom),
    ];

    public static string ModeName(ThermalMode m) => Modes.FirstOrDefault(x => x.mode == m).name ?? $"Mode {(uint)m}";
    public static Color ModeColor(ThermalMode m) => Modes.FirstOrDefault(x => x.mode == m).color;

    public MainForm(ModeControl mc)
    {
        _mc = mc;
        Text = "Legion Helper - Legion Go S";
        BackColor = Theme.Back;
        ForeColor = Theme.Fore;
        Font = Theme.Normal;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        Icon = TrayIcon.Make(Theme.Balanced);
        _k = DeviceDpi / 96f;

        BuildLayout();

        _tray = new NotifyIcon { Text = "Legion Helper", Visible = true, Icon = Icon, ContextMenuStrip = BuildTrayMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleVisible(); };

        _poll.Tick += (_, _) => PollSensors();
        _mc.Changed += () => BeginInvokeSafe(RefreshUI);
        _mc.ModeSwitched += m => BeginInvokeSafe(() => ModeFeedback(m, _mc.Cfg));
        HandleCreated += (_, _) =>
        {
            RegisterCycleHotkey();
            UpdateLegionWatcher();
            if (_mc.Cfg.ChordsEnabled) StartChordHook();
        };
        VisibleChanged += (_, _) => { _poll.Enabled = Visible; if (Visible) { RefreshUI(); PollSensors(); RefreshLights(); } };
    }

    int S(int px) => (int)(px * _k);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

    void BeginInvokeSafe(Action a)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
    }

    // ---- Layout -------------------------------------------------------------------------

    void BuildLayout()
    {
        int w = S(460), pad = S(12), y = pad, inner = w - 2 * pad, gap = S(6);
        ClientSize = new Size(w, 100);

        Label Header(Label l, string text, int x, int yy, int width, ContentAlignment align = ContentAlignment.MiddleLeft, Font? f = null)
        {
            l.Text = text; l.Font = f ?? Theme.Title; l.ForeColor = Theme.Fore; l.TextAlign = align;
            l.SetBounds(x, yy, width, S(26)); Controls.Add(l); return l;
        }

        // Mode
        Header(_lblMode, "Mode", pad, y, inner / 2);
        Header(_lblSensors, "", pad + inner / 2, y, inner / 2, ContentAlignment.MiddleRight);
        y += S(30);
        int bw = (inner - 3 * gap) / 4, bh = S(64);
        for (int i = 0; i < Modes.Length; i++)
        {
            var (mode, name, color) = Modes[i];
            var b = new RButton { Text = name, AccentColor = color };
            b.SetBounds(pad + i * (bw + gap), y, bw, bh);
            b.Click += (_, _) => SetMode(mode);
            _modeButtons[mode] = b;
            Controls.Add(b);
        }
        y += bh + gap;

        var fansBtn = new RButton { Text = "Fans + Power", AccentColor = Theme.Custom };
        fansBtn.SetBounds(pad, y, bw * 2 + gap, S(40));
        fansBtn.Click += (_, _) => OpenFans();
        Controls.Add(fansBtn);

        _winPower.Items.AddRange(SystemControl.PowerModes.Select(p => (object)p.Name).ToArray());
        _winPower.Location = new Point(pad + 2 * (bw + gap), y);
        _winPower.Width = bw * 2 + gap;
        Controls.Add(_winPower);
        _winPower.Top = y + (S(40) - _winPower.Height) / 2;
        _winPower.SelectionChangeCommitted += (_, _) =>
        {
            if (_winPower.SelectedIndex >= 0) SystemControl.SetPowerMode(SystemControl.PowerModes[_winPower.SelectedIndex].Id);
        };
        Controls.Add(_winPower);
        y += S(40) + S(18);

        // Screen
        Header(_lblScreen, "Screen", pad, y, inner);
        y += S(30);
        var rates = new List<int> { 0 };
        if (SystemControl.GetLowHighRates() is { } lh) rates.AddRange(new[] { lh.low, lh.high }.Distinct());
        int rw = (inner - (rates.Count - 1) * gap) / rates.Count;
        for (int i = 0; i < rates.Count; i++)
        {
            int hz = rates[i];
            var b = new RButton { Text = hz == 0 ? "Auto" : $"{hz}Hz" };
            b.SetBounds(pad + i * (rw + gap), y, rw, S(44));
            b.Click += (_, _) => { _mc.Cfg.RefreshRate = hz; _mc.Cfg.Save(); _mc.ApplyRefreshRate(); RefreshUI(); };
            _rateButtons.Add((b, hz));
            Controls.Add(b);
        }
        y += S(44) + S(18);

        // Controller
        Header(new Label(), "Controller", pad, y, inner);
        y += S(30);
        int cw = (inner - 3 * gap) / 4;
        _lightsBtn.SetBounds(pad, y, cw, S(44));
        _lightsBtn.AccentColor = Theme.Quiet;
        _lightsBtn.Click += (_, _) => ToggleLights();
        Controls.Add(_lightsBtn);
        var colorBtn = new RButton { Text = "RGB", AccentColor = Theme.Custom };
        colorBtn.SetBounds(pad + cw + gap, y, cw, S(44));
        colorBtn.Click += (_, _) => OpenRgb();
        Controls.Add(colorBtn);
        var padBtn = new RButton { Text = "Controller" };
        padBtn.SetBounds(pad + 2 * (cw + gap), y, cw, S(44));
        padBtn.Click += (_, _) => OpenController();
        Controls.Add(padBtn);
        _shortcutsBtn.SetBounds(pad + 3 * (cw + gap), y, cw, S(44));
        _shortcutsBtn.AccentColor = Theme.Custom;
        _shortcutsBtn.Click += (_, _) => OpenShortcuts();
        Controls.Add(_shortcutsBtn);
        y += S(44) + S(18);

        // Battery
        Header(_lblBattery, "Battery", pad, y, inner / 2);
        Header(_lblBatteryInfo, "", pad + inner / 2, y, inner / 2, ContentAlignment.MiddleRight, Theme.Normal);
        y += S(30);
        StyleCheck(_chkCharge, "Limit battery charge", pad, y, inner - S(70));
        _chkCharge.Checked = _mc.Cfg.ChargeLimit == true;
        _chkCharge.Click += (_, _) => ToggleChargeLimit();
        _lblChargeTarget.Font = Theme.Title; _lblChargeTarget.ForeColor = Theme.Fore; _lblChargeTarget.TextAlign = ContentAlignment.MiddleRight;
        _lblChargeTarget.SetBounds(pad + inner - S(70), y, S(70), S(30)); Controls.Add(_lblChargeTarget);
        y += S(34);
        // 80 = firmware limit (works without the app); 85-95 = app-managed (needs Legion Helper running).
        _chargeSlider.Minimum = 80; _chargeSlider.Maximum = 95; _chargeSlider.Step = 5; _chargeSlider.AccentColor = Theme.Quiet;
        _chargeSlider.SetBounds(pad, y, inner, S(28));
        _chargeSlider.Value = Math.Clamp(_mc.Cfg.ChargeTarget, 80, 95);
        _chargeSlider.ValueChanged += (_, _) => UpdateChargeLabel();
        _chargeSlider.ValueCommitted += (_, _) =>
        {
            int target = _chargeSlider.Value;
            Task.Run(() => _mc.SetChargeSetting(true, target));
        };
        Controls.Add(_chargeSlider);
        y += S(30);
        _lblChargeHint.ForeColor = Theme.ForeDim; _lblChargeHint.SetBounds(pad, y, inner, S(24)); Controls.Add(_lblChargeHint);
        y += S(24) + S(10);
        UpdateChargeLabel();

        // Footer
        StyleCheck(_chkStartup, "Run on startup", pad, y, inner / 2);
        _chkStartup.Checked = SystemControl.IsStartupEnabled();
        _chkStartup.Click += (_, _) =>
        {
            SystemControl.SetStartup(_chkStartup.Checked, _mc.Cfg.StartHidden);
            _chkHidden.Visible = _chkStartup.Checked;
        };
        var extra = new RButton { Text = "Extra" };
        extra.SetBounds(w - pad - 2 * bw - gap, y - S(4), bw, S(38));
        extra.Click += (_, _) => OpenExtra();
        Controls.Add(extra);
        var quit = new RButton { Text = "Quit" };
        quit.SetBounds(w - pad - bw, y - S(4), bw, S(38));
        quit.Click += (_, _) => Quit();
        Controls.Add(quit);
        y += S(38);
        StyleCheck(_chkHidden, "Start hidden in tray", pad + S(30), y, inner / 2);
        _chkHidden.Checked = _mc.Cfg.StartHidden;
        _chkHidden.Visible = _chkStartup.Checked;
        _chkHidden.Click += (_, _) =>
        {
            _mc.Cfg.StartHidden = _chkHidden.Checked; _mc.Cfg.Save();
            SystemControl.SetStartup(true, _mc.Cfg.StartHidden);
        };
        y += S(34);
        var ver = new Label
        {
            Text = $"Version {Application.ProductVersion.Split('+')[0]}",
            ForeColor = Theme.ForeDim, Font = Theme.Normal, AutoSize = true, Location = new Point(pad, y),
        };
        Controls.Add(ver);
        y += S(26) + pad;

        ClientSize = new Size(w, y);
    }

    void StyleCheck(RCheckBox c, string text, int x, int y, int width)
    {
        c.Text = text;
        c.SetBounds(x, y, width, S(30));
        Controls.Add(c);
    }

    // ---- Mode feedback & hotkey ---------------------------------------------------------------

    static DateTime _lastFeedback;
    static ThermalMode? _lastFeedbackMode;

    /// <summary>On-screen pop-up and/or controller light flash for a mode change.</summary>
    public static void ModeFeedback(ThermalMode mode, Config cfg, bool force = false)
    {
        // de-duplicate (UI change + firmware event can both report the same switch)
        if (!force && _lastFeedbackMode == mode && (DateTime.Now - _lastFeedback).TotalSeconds < 1.5) return;
        _lastFeedback = DateTime.Now; _lastFeedbackMode = mode;
        if (cfg.ShowOsd || force) ModeOsd.Show(ModeName(mode), ModeColor(mode));
        if (cfg.RgbModeFlash || force) RgbFlash.Flash(LedColor(mode));
    }

    /// <summary>
    /// Saturated colours for the joystick LEDs. The UI palette's teal-green and sky-blue look almost identical on
    /// the rings, so the flash uses pure primaries (and a low-green orange, which reads as orange on LEDs).
    /// </summary>
    public static Color LedColor(ThermalMode m) => m switch
    {
        ThermalMode.Quiet => Color.FromArgb(0, 255, 0),
        ThermalMode.Balanced => Color.FromArgb(0, 40, 255),
        ThermalMode.Performance => Color.FromArgb(255, 0, 0),
        ThermalMode.Custom => Color.FromArgb(255, 70, 0),
        _ => Color.White,
    };

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    const int HotkeyId = 0x4C47;

    /// <summary>(Re)registers the mode-cycle hotkey; false if Windows refused it (combination in use).</summary>
    bool RegisterCycleHotkey()
    {
        UnregisterHotKey(Handle, HotkeyId);
        if (_mc.Cfg.CycleKey == 0) return true;
        bool ok = RegisterHotKey(Handle, HotkeyId, 0x4000 /*MOD_NOREPEAT*/ | _mc.Cfg.CycleMods, (uint)_mc.Cfg.CycleKey);
        Log.Write($"Cycle hotkey {ExtraForm.ShortcutText(_mc.Cfg.CycleKey, _mc.Cfg.CycleMods)}: {(ok ? "registered" : "failed (in use?)")}");
        return ok;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 /*WM_HOTKEY*/ && m.WParam == HotkeyId)
        {
            Task.Run(_mc.CycleMode);
            return;
        }
        base.WndProc(ref m);
    }

    // ---- Back-paddle shortcuts -------------------------------------------------------------------

    readonly RButton _shortcutsBtn = new() { Text = "Shortcuts" };
    ShortcutsForm? _shortcuts;
    PaddleChords? _chords;

    RgbForm? _rgb;

    void OpenRgb()
    {
        if (_rgb is null || _rgb.IsDisposed) _rgb = new RgbForm(_mc.Cfg) { Owner = this };
        ShowChild(_rgb);
    }

    void OpenShortcuts()
    {
        if (_shortcuts is null || _shortcuts.IsDisposed) _shortcuts = new ShortcutsForm(_mc.Cfg, SetChordsEnabled, UpdateLegionWatcher) { Owner = this };
        ShowChild(_shortcuts);
    }

    /// <summary>Enables/disables chords: remaps the paddles to F23/F24 (or back to default) and installs the key hook.</summary>
    bool SetChordsEnabled(bool on)
    {
        bool ok = Pad.Run(p => (bool?)(on
            ? p.SetKeyMap(new(PaddleChords.PaddleY1, LegionController.MapType.Keyboard, [PaddleChords.HidY1]))
              & p.SetKeyMap(new(PaddleChords.PaddleY2, LegionController.MapType.Keyboard, [PaddleChords.HidY2]))
            : p.SetKeyMap(LegionController.DefaultMapping(PaddleChords.PaddleY1))
              & p.SetKeyMap(LegionController.DefaultMapping(PaddleChords.PaddleY2)))) == true;
        Log.Write($"Paddle shortcuts {(on ? "on" : "off")}: {(ok ? "ok" : "controller did not acknowledge")}");
        if (!ok) return false;
        _mc.Cfg.ChordsEnabled = on; _mc.Cfg.Save();
        if (on) StartChordHook(); else { _chords?.Dispose(); _chords = null; }
        _shortcutsBtn.Active = on;
        if (_controller is { IsDisposed: false }) _controller.RefreshMapping();
        return true;
    }

    ControllerState? _legion;

    /// <summary>Watches the Legion L/R buttons only while at least one of them has an action.</summary>
    void UpdateLegionWatcher()
    {
        bool want = !string.IsNullOrEmpty(_mc.Cfg.LegionLAction) || !string.IsNullOrEmpty(_mc.Cfg.LegionRAction);
        if (want && _legion is null)
        {
            _legion = new ControllerState();
            _legion.Pressed += b =>
            {
                string? id = b == ControllerState.LegionButton.L ? _mc.Cfg.LegionLAction : _mc.Cfg.LegionRAction;
                if (!string.IsNullOrEmpty(id)) BeginInvokeSafe(() => RunShortcut(id));
            };
            Log.Write("Legion button watcher started");
        }
        else if (!want && _legion is not null) { _legion.Dispose(); _legion = null; Log.Write("Legion button watcher stopped"); }
    }

    void StartChordHook()
    {
        if (_chords is not null) return;
        _chords = new PaddleChords();
        _chords.Chord += (paddle, button) =>
        {
            if (_mc.Cfg.Chords.TryGetValue($"{paddle}+{button}", out var id)) BeginInvokeSafe(() => RunShortcut(id));
        };
        _shortcutsBtn.Active = true;
    }

    void Notify(string text, Color color) { if (_mc.Cfg.ShowOsd) ModeOsd.Show(text, color); }

    /// <summary>Executes a shortcut action (UI thread).</summary>
    void RunShortcut(string id)
    {
        Log.Write("Shortcut: " + id);
        switch (id)
        {
            case "mode:quiet": SetMode(ThermalMode.Quiet); break;
            case "mode:balanced": SetMode(ThermalMode.Balanced); break;
            case "mode:performance": SetMode(ThermalMode.Performance); break;
            case "mode:custom": SetMode(ThermalMode.Custom); break;
            case "mode:cycle": Task.Run(_mc.CycleMode); break;
            case "lights:toggle":
                ToggleLights();
                Notify(_lightsOn == true ? "Lights on" : "Lights off", Theme.Quiet);
                break;
            case "mouse:off" or "mouse:gyro" or "mouse:joystick" or "mouse:cycle" or "mouse:joystick-toggle" or "mouse:gyro-toggle":
            {
                string[] order = ["off", "gyro", "joystick"];
                string next = id switch
                {
                    "mouse:cycle" => order[(Array.IndexOf(order, _mc.Cfg.MouseMode) + 1) % 3],
                    "mouse:joystick-toggle" => _mc.Cfg.MouseMode == "joystick" ? "off" : "joystick",
                    "mouse:gyro-toggle" => _mc.Cfg.MouseMode == "gyro" ? "off" : "gyro",
                    _ => id[6..],
                };
                _mc.Cfg.MouseMode = next; _mc.Cfg.Save();
                Task.Run(() => MouseModes.Apply(_mc.Cfg));
                Notify(next switch { "gyro" => "Mouse: Gyro", "joystick" => "Mouse: Joystick", _ => "Mouse: Off" }, Theme.Quiet);
                break;
            }
            case "gyro:calibrate":
                GyroMouse.Recalibrate();
                Notify("Gyro: hold still", Theme.Quiet);
                break;
            case "screen:toggle":
            {
                var lh = SystemControl.GetLowHighRates();
                if (lh is null) break;
                int hz = SystemControl.GetRefreshRate() == lh.Value.high ? lh.Value.low : lh.Value.high;
                _mc.Cfg.RefreshRate = hz; _mc.Cfg.Save(); _mc.ApplyRefreshRate();
                Notify($"Screen {hz} Hz", Theme.Balanced);
                break;
            }
            case "app:toggle": ToggleVisible(); break;
            case "app:show": ShowNearTray(); break;
            case "touchkb:toggle": Shortcuts.ToggleTouchKeyboard(); break;
            case "charge:toggle":
            {
                bool on = _mc.Cfg.ChargeLimit != true;
                _mc.SetChargeSetting(on, _mc.Cfg.ChargeTarget);
                _chkCharge.Checked = on; UpdateChargeLabel();
                Notify(on ? $"Charge limit {_mc.Cfg.ChargeTarget}%" : "Charge limit off", Theme.Quiet);
                break;
            }
            default:
                if (id.StartsWith("keys:")) Shortcuts.SendKeys(id[5..]);
                break;
        }
    }

    void OpenExtra()
    {
        if (_extra is null || _extra.IsDisposed) _extra = new ExtraForm(_mc, RegisterCycleHotkey) { Owner = this };
        ShowChild(_extra);
    }

    /// <summary>Shows a sub-window owned by the main window (stays above it, restores with it from the taskbar).</summary>
    void ShowChild(Form child)
    {
        ShowNearTray();
        var wa = Screen.FromControl(this).WorkingArea;
        if (!child.Visible)
        {
            int x = Left - child.Width - S(6);
            if (x < wa.Left) x = Math.Max(wa.Left, Math.Min(Right + S(6), wa.Right - child.Width));
            int y = Math.Clamp(Bottom - child.Height, wa.Top, Math.Max(wa.Top, wa.Bottom - child.Height));
            child.Location = new Point(x, y);
        }
        child.Show();
        child.Activate();
    }

    ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip { BackColor = Theme.Panel, ForeColor = Theme.Fore, ShowImageMargin = false };
        foreach (var (mode, name, _) in Modes)
            menu.Items.Add(name, null, (_, _) => SetMode(mode));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Fans + Power", null, (_, _) => OpenFans());
        menu.Items.Add("Controller", null, (_, _) => OpenController());
        menu.Items.Add("RGB", null, (_, _) => OpenRgb());
        menu.Items.Add("Shortcuts", null, (_, _) => OpenShortcuts());
        menu.Items.Add("Extra", null, (_, _) => OpenExtra());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        menu.Opening += (_, _) =>
        {
            for (int i = 0; i < Modes.Length; i++)
                ((ToolStripMenuItem)menu.Items[i]).Checked = _mc.CurrentMode == Modes[i].mode;
        };
        return menu;
    }

    // ---- Actions -------------------------------------------------------------------------

    void SetMode(ThermalMode mode)
    {
        foreach (var b in _modeButtons.Values) b.Enabled = false;
        Task.Run(() => _mc.SelectMode(mode)).ContinueWith(_ => BeginInvokeSafe(() =>
        {
            foreach (var b in _modeButtons.Values) b.Enabled = true;
            RefreshUI();
        }));
    }

    readonly Slider _chargeSlider = new();
    readonly Label _lblChargeTarget = new(), _lblChargeHint = new();

    void UpdateChargeLabel()
    {
        bool on = _chkCharge.Checked;
        _chargeSlider.Visible = _lblChargeHint.Visible = _lblChargeTarget.Visible = on;
        int t = _chargeSlider.Value;
        _lblChargeTarget.Text = $"{t}%";
        _lblChargeHint.Text = t <= 80 ? "Handled by the firmware (works even when the app is closed)"
                                      : "Managed by Legion Helper (keep it running in the tray)";
    }

    void ToggleChargeLimit()
    {
        bool want = _chkCharge.Checked;
        int target = _chargeSlider.Value;
        UpdateChargeLabel();
        _chkCharge.Enabled = false;
        Task.Run(() => { _mc.SetChargeSetting(want, target); return true; }).ContinueWith(t => BeginInvokeSafe(() =>
        {
            _chkCharge.Enabled = true;
            if (!t.Result)
            {
                _chkCharge.Checked = !want;
                UpdateChargeLabel();
                MessageBox.Show(this, "The firmware rejected the charge-limit change. See %AppData%\\LegionHelper\\log.txt.",
                    "Battery charge limit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }));
    }

    public void OpenFans()
    {
        if (_fans is null || _fans.IsDisposed) _fans = new FansForm(_mc) { Owner = this };
        ShowChild(_fans);
    }

    void ToggleLights()
    {
        bool on = !(_lightsOn ?? true);
        if (Pad.Run(p => (bool?)p.SetFeature(Hardware.LegionController.Feature.RgbEnable, (byte)(on ? 1 : 0))) == true)
            _lightsOn = on;
        UpdateLightsButton();
    }

    void UpdateLightsButton()
    {
        _lightsBtn.Text = _lightsOn switch { true => "Lights On", false => "Lights Off", _ => "Lights" };
        _lightsBtn.Active = _lightsOn == true;
    }

    public void OpenControllerWindow() => OpenController();
    public void OpenRgbWindow() => OpenRgb();

    void OpenController()
    {
        if (_controller is null || _controller.IsDisposed)
        {
            _controller = new ControllerForm(_mc.Cfg) { Owner = this };
            _controller.VisibleChanged += (_, _) => { if (_controller is { Visible: false }) RefreshLights(); };
        }
        ShowChild(_controller);

    }

    void RefreshLights()
    {
        Task.Run(() => Pad.Run(p => p.GetFeature(Hardware.LegionController.Feature.RgbEnable)))
            .ContinueWith(t => BeginInvokeSafe(() => { _lightsOn = t.Result is byte b ? b == 1 : null; UpdateLightsButton(); }));
    }

    void ToggleVisible()
    {
        if (Visible && WindowState != FormWindowState.Minimized) Hide();
        else ShowNearTray();
    }

    public void ShowNearTray()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        if (!Visible) Location = new Point(wa.Right - Width - S(8), wa.Bottom - Height - S(8));
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void Quit()
    {
        _tray.Visible = false;
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; Hide(); _fans?.Hide(); _controller?.Hide(); _extra?.Hide(); _rgb?.Hide(); _shortcuts?.Hide();
            return;
        }
        UnregisterHotKey(Handle, HotkeyId);
        JoystickMouse.Stop();
        _chords?.Dispose();
        _legion?.Dispose();
        Lighting.Shutdown();
        GyroMouse.Stop();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    /// <summary>
    /// A plain WM_CLOSE (e.g. taskkill without /f) is not CloseReason.UserClosing, so the main form really closes.
    /// End the message loop too, otherwise the process lingers without a window or tray icon.
    /// </summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (e.CloseReason != CloseReason.ApplicationExitCall) Application.ExitThread();
    }

    // ---- Refresh -------------------------------------------------------------------------

    public void RefreshUI()
    {
        var mode = _mc.CurrentMode;
        foreach (var (m, b) in _modeButtons)
        {
            b.Active = m == mode;
            _modeTips.SetToolTip(b, $"{ModeName(m)}: {_mc.LimitsText(m)}" +
                (m == ThermalMode.Custom ? " (your values)" : " (firmware preset, read-only)"));
        }
        string extra = mode == ThermalMode.Custom ? $" {_mc.Cfg.SPL}W" : "";
        _lblMode.Text = $"Mode: {ModeName(mode)}{extra}";

        var icon = TrayIcon.Make(ModeColor(mode));
        _tray.Icon = icon; Icon = icon;
        _tray.Text = $"Legion Helper - {ModeName(mode)}";

        var cur = SystemControl.GetPowerMode();
        _winPower.SelectedIndex = Array.FindIndex(SystemControl.PowerModes, p => p.Id == cur);

        int hzNow = SystemControl.GetRefreshRate();
        _lblScreen.Text = $"Screen: {hzNow}Hz";
        foreach (var (b, hz) in _rateButtons) b.Active = hz == _mc.Cfg.RefreshRate;

        _fans?.RefreshUI();
    }

    void PollSensors()
    {
        if (_sensorBusy) return;
        _sensorBusy = true;
        Task.Run(() =>
        {
            int? temp = LenovoWmi.TryGetFeature(LenovoWmi.CpuTemp);
            int? rpm = LenovoWmi.TryGetFeature(LenovoWmi.FanRpm);
            double? watts = SystemControl.GetBatteryWatts();
            var ps = SystemInformation.PowerStatus;
            return (temp, rpm, watts, ps.BatteryLifePercent, ps.PowerLineStatus);
        }).ContinueWith(t => BeginInvokeSafe(() =>
        {
            _sensorBusy = false;
            if (t.IsFaulted) return;
            var (temp, rpm, watts, pct, line) = t.Result;
            _lblSensors.Text = $"CPU: {(temp?.ToString() ?? "-")}°C  Fan: {(rpm?.ToString() ?? "-")}RPM";
            string state = watts switch
            {
                < 0 => $"Discharging: {-watts:0.0}W",
                > 0 => $"Charging: {watts:0.0}W",
                _ => line == PowerLineStatus.Online ? "Plugged in" : "",
            };
            _lblBatteryInfo.Text = $"{state}   {pct * 100:0}%";
            _fans?.UpdateSensors(temp, rpm);
        }));
    }
}

/// <summary>Generates a small round tray icon in the mode's colour (no icon assets needed).</summary>
public static class TrayIcon
{
    static readonly Dictionary<int, Icon> Cache = new();

    public static Icon Make(Color c)
    {
        int key = c.ToArgb();
        if (Cache.TryGetValue(key, out var ic)) return ic;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var ring = new Pen(c, 5);
            g.DrawEllipse(ring, 4, 4, 23, 23);
            using var dot = new SolidBrush(c);
            g.FillEllipse(dot, 11, 11, 10, 10);
        }
        ic = Icon.FromHandle(bmp.GetHicon());
        Cache[key] = ic;
        return ic;
    }
}
