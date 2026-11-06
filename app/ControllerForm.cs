using LegionHelper.Hardware;
using LegionHelper.UI;
using static LegionHelper.Hardware.LegionController;

namespace LegionHelper;

/// <summary>Shared access to the built-in controller; reopens after sleep/re-enumeration.</summary>
public static class Pad
{
    static LegionController? _pad;
    static readonly object _lock = new();

    public static T? Run<T>(Func<LegionController, T?> f)
    {
        lock (_lock)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    _pad ??= LegionController.Open();
                    if (_pad is null) return default;
                    return f(_pad);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    Log.Write("Controller reopen: " + ex.Message);
                    _pad?.Dispose(); _pad = null;
                }
            }
            return default;
        }
    }
}

/// <summary>
/// Mouse emulation, both in software: joystick-as-mouse (XInput) and gyro-as-mouse (Windows Sensor API gyrometer).
/// The controller's own gyro mapping (0E 01) is kept off: on-device, value 1 drifted (uncorrected bias / tilt-based)
/// and value 2 did nothing.
/// </summary>
public static class MouseModes
{
    public static void Apply(Config cfg)
    {
        JoystickMouse.Speed = cfg.JoystickMouseSpeed;
        JoystickMouse.RightStick = cfg.JoystickMouseRightStick;
        JoystickMouse.UseBumpers = cfg.JoystickMouseBumpers;
        GyroMouse.Speed = cfg.GyroMouseSpeed;
        GyroMouse.HorizontalFromRoll = cfg.GyroHorizontalRoll;
        GyroMouse.InvertX = cfg.GyroInvertX;
        GyroMouse.InvertY = cfg.GyroInvertY;

        if (cfg.MouseMode == "joystick") JoystickMouse.Start(); else JoystickMouse.Stop();
        bool gyro = cfg.MouseMode == "gyro";
        if (gyro) GyroMouse.Start(); else GyroMouse.Stop();
        Pad.Run(p =>
        {
            if (gyro) p.SetFeature(Feature.ImuEnable, 1);
            return (bool?)p.SetGyroMode(0); // firmware gyro mapping off
        });
        Log.Write("Mouse mode: " + cfg.MouseMode);
    }
}

/// <summary>"Controller" window: lighting, rumble, touchpad, gyro, polling, button mapping, mouse mode, deadzones.</summary>
public class ControllerForm : Form
{
    readonly Config _cfg;
    readonly RComboBox _vibration = new(), _poll = new(), _mapButton = new(), _mapTarget = new(), _mouseStick = new();
    readonly Slider _mouseSens = new();
    readonly Label _status = new(), _keyTest = new(), _mouseSensVal = new(), _mouseHint = new();
    readonly RCheckBox _touchpad = new(), _gyro = new(), _mouseBumpers = new();
    readonly RCheckBox[] _mods = KeyCatalog.Modifiers.Select(m => new RCheckBox { Text = m.Name }).ToArray();
    readonly Dictionary<string, RButton> _mouseButtons = new();
    readonly Slider _lsIn = new(), _lsOut = new(), _rsIn = new(), _rsOut = new(), _ltDead = new(), _ltMax = new(), _rtDead = new(), _rtMax = new();
    bool _loading = true, _loaded;
    float _k;
    KeyHook? _hook;

    public ControllerForm(Config cfg)
    {
        _cfg = cfg;
        Text = "Controller - Legion Go S";
        BackColor = Theme.Back; ForeColor = Theme.Fore; Font = Theme.Normal;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.Manual; ShowInTaskbar = false;
        Icon = TrayIcon.Make(Theme.Quiet);
        _k = DeviceDpi / 96f;
        Build();
        Shown += (_, _) => { if (!_loaded) LoadFromDevice(); };
        VisibleChanged += (_, _) =>
        {
            if (Visible && _loaded) LoadFromDevice();
            // Low-level keyboard hook only while this window is open (key tester for F13-F24 etc.).
            if (Visible) _hook ??= new KeyHook(vk => BeginInvoke(() => _keyTest.Text = $"Key test: last key = {KeyName(vk)}"));
            else { _hook?.Dispose(); _hook = null; }
        };
    }

    int S(int px) => (int)(px * _k);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

    static string KeyName(int vk) => vk is >= 0x70 and <= 0x87 ? $"F{vk - 0x6F}" : ((Keys)vk).ToString();


    // ---- Layout ------------------------------------------------------------------------------

    void Build()
    {
        int pad = S(14), colW = S(300), gap = S(26), rowH = S(36);
        int x1 = pad, x2 = x1 + colW + gap, x3 = x2 + colW + gap;

        Label L(string t, int x, int yy, int w, Font? f = null, ContentAlignment a = ContentAlignment.MiddleLeft)
        {
            var l = new Label { Text = t, Font = f ?? Theme.Normal, ForeColor = Theme.Fore, TextAlign = a };
            l.SetBounds(x, yy, w, S(26)); Controls.Add(l); return l;
        }
        void Combo(RComboBox c, object[] items, int x, int yy, int w)
        {
            c.Items.AddRange(items); c.Location = new Point(x, yy); c.Width = w; Controls.Add(c);
        }
        void Check(RCheckBox c, string t, int x, int yy, int w)
        {
            c.Text = t; c.SetBounds(x, yy, w, S(30)); Controls.Add(c);
        }
        void SliderRow(Slider s, Label val, string name, int x, ref int yy, int min, int max, string unit)
        {
            L(name, x, yy, colW - S(60));
            val.ForeColor = Theme.Fore; val.TextAlign = ContentAlignment.MiddleRight;
            val.SetBounds(x + colW - S(60), yy, S(60), S(26)); Controls.Add(val);
            yy += S(26);
            s.Minimum = min; s.Maximum = max; s.AccentColor = Theme.Quiet;
            s.SetBounds(x, yy, colW, S(28)); Controls.Add(s);
            s.ValueChanged += (_, _) => val.Text = $"{s.Value}{unit}";
            yy += S(36);
        }

        // ---- Column 1: general (lighting lives in the RGB window)
        int y = pad;
        L("General", x1, y, colW, Theme.Title); y += S(32);
        L("Vibration", x1, y + S(4), colW / 2);
        Combo(_vibration, ["Off", "Weak", "Medium", "Strong"], x1 + colW / 2, y, colW / 2);
        _vibration.SelectionChangeCommitted += (_, _) =>
        {
            Do(p => p.SetVibration((byte)_vibration.SelectedIndex), "Vibration");
            // The controller plays a test rumble on change; cut it to roughly half its length.
            Task.Delay(200).ContinueWith(_ => Pad360.StopRumble());
        };
        y += rowH + S(8);
        L("Polling rate", x1, y + S(4), colW / 2);
        Combo(_poll, ["125 Hz", "250 Hz", "500 Hz", "1000 Hz"], x1 + colW / 2, y, colW / 2);
        _poll.SelectionChangeCommitted += (_, _) => Do(p => p.SetFeature(Feature.PollRate, (byte)_poll.SelectedIndex), "Polling rate");
        y += rowH + S(10);
        Check(_touchpad, "Touchpad enabled", x1, y, colW); y += S(36);
        _touchpad.Click += (_, _) => Do(p => p.SetFeature(Feature.TouchpadEnable, (byte)(_touchpad.Checked ? 1 : 0)), "Touchpad");
        Check(_gyro, "Gyro (IMU) enabled", x1, y, colW); y += S(36);
        _gyro.Click += (_, _) => Do(p => p.SetFeature(Feature.ImuEnable, (byte)(_gyro.Checked ? 1 : 0)), "Gyro");
        int bottom1 = y;

        // ---- Column 2: button mapping + mouse mode
        y = pad;
        L("Button mapping", x2, y, colW, Theme.Title); y += S(32);
        L("Button", x2, y + S(4), S(80));
        Combo(_mapButton, KeyCatalog.Buttons.Select(b => (object)b.Name).ToArray(), x2 + S(80), y, colW - S(80));
        _mapButton.SelectedIndexChanged += (_, _) => ShowMapping();
        y += rowH + S(8);
        L("Action", x2, y + S(4), S(80));
        Combo(_mapTarget, KeyCatalog.Targets, x2 + S(80), y, colW - S(80));
        _mapTarget.SelectionChangeCommitted += (_, _) => { UpdateModsEnabled(); ApplyMapping(); };
        y += rowH + S(8);
        int mw = colW / 4;
        for (int i = 0; i < _mods.Length; i++)
        {
            _mods[i].SetBounds(x2 + i * mw, y, mw, S(30)); Controls.Add(_mods[i]);
            _mods[i].Click += (_, _) => ApplyMapping();
        }
        y += S(36);
        var resetMap = new RButton { Text = "Reset all buttons" };
        resetMap.SetBounds(x2, y, colW, rowH);
        resetMap.Click += (_, _) => ResetMappings();
        Controls.Add(resetMap);
        y += rowH + S(6);
        _keyTest.ForeColor = Theme.ForeDim; _keyTest.Text = "Key test: press a mapped button…";
        _keyTest.SetBounds(x2, y, colW, S(26)); Controls.Add(_keyTest);
        y += S(36);

        L("Mouse mode", x2, y, colW, Theme.Title); y += S(32);
        int bw = (colW - 2 * S(6)) / 3;
        string[] modes = ["off", "gyro", "joystick"], names = ["None", "Gyro", "Joystick"];
        for (int i = 0; i < 3; i++)
        {
            string m = modes[i];
            var b = new RButton { Text = names[i], AccentColor = Theme.Quiet };
            b.SetBounds(x2 + i * (bw + S(6)), y, bw, S(44));
            b.Click += (_, _) => SetMouseMode(m);
            _mouseButtons[m] = b; Controls.Add(b);
        }
        y += S(52);
        SliderRow(_mouseSens, _mouseSensVal, "Sensitivity", x2, ref y, 1, 100, "");
        _mouseSens.ValueCommitted += (_, _) => ApplyMouseSensitivity();
        _mouseRowLabel = L("Stick", x2, y + S(4), S(90));
        Combo(_mouseStick, [], x2 + S(90), y, colW - S(90));
        _mouseStick.SelectionChangeCommitted += (_, _) =>
        {
            if (_cfg.MouseMode == "gyro") { _cfg.GyroHorizontalRoll = _mouseStick.SelectedIndex == 1; GyroMouse.HorizontalFromRoll = _cfg.GyroHorizontalRoll; }
            else { _cfg.JoystickMouseRightStick = _mouseStick.SelectedIndex == 0; JoystickMouse.RightStick = _cfg.JoystickMouseRightStick; }
            _cfg.Save();
        };
        y += rowH + S(8);
        Check(_mouseBumpers, "Click with bumpers instead of triggers", x2, y, colW);
        _mouseBumpers.AccentColor = Theme.Quiet;
        _mouseBumpers.Click += (_, _) =>
        {
            _cfg.JoystickMouseBumpers = _mouseBumpers.Checked; _cfg.Save();
            JoystickMouse.UseBumpers = _mouseBumpers.Checked;
            UpdateMouseUI();
        };
        Check(_invX, "Invert X", x2, y, colW / 2);
        Check(_invY, "Invert Y", x2 + colW / 2, y, colW / 2);
        _invX.AccentColor = _invY.AccentColor = Theme.Quiet;
        _invX.Click += (_, _) => { _cfg.GyroInvertX = _invX.Checked; GyroMouse.InvertX = _invX.Checked; _cfg.Save(); };
        _invY.Click += (_, _) => { _cfg.GyroInvertY = _invY.Checked; GyroMouse.InvertY = _invY.Checked; _cfg.Save(); };
        y += S(36);
        _recal.SetBounds(x2, y, S(130), rowH);
        _recal.Click += (_, _) => GyroMouse.Recalibrate();
        Controls.Add(_recal);
        _mouseHint.ForeColor = Theme.ForeDim;
        _mouseHint.SetBounds(x2, y, colW, S(44)); Controls.Add(_mouseHint);
        _gyroStatusTimer.Tick += (_, _) =>
        {
            if (_cfg.MouseMode != "gyro" || GyroMouse.Status == _lastGyroStatus) return;
            _lastGyroStatus = GyroMouse.Status;
            _status.Text = "Gyro: " + _lastGyroStatus;
        };
        VisibleChanged += (_, _) => _gyroStatusTimer.Enabled = Visible;
        y += S(48);
        int bottom2 = y;

        // ---- Column 3: deadzones
        y = pad;
        L("Deadzones", x3, y, colW, Theme.Title); y += S(32);
        SliderRow(_lsIn, new Label(), "Left stick inner", x3, ref y, 0, 50, "%");
        SliderRow(_lsOut, new Label(), "Left stick outer", x3, ref y, 50, 100, "%");
        SliderRow(_rsIn, new Label(), "Right stick inner", x3, ref y, 0, 50, "%");
        SliderRow(_rsOut, new Label(), "Right stick outer", x3, ref y, 50, 100, "%");
        SliderRow(_ltDead, new Label(), "Left trigger deadzone", x3, ref y, 0, 50, "%");
        SliderRow(_ltMax, new Label(), "Left trigger max", x3, ref y, 50, 100, "%");
        SliderRow(_rtDead, new Label(), "Right trigger deadzone", x3, ref y, 0, 50, "%");
        SliderRow(_rtMax, new Label(), "Right trigger max", x3, ref y, 50, 100, "%");
        foreach (var s in new[] { _lsIn, _lsOut, _rsIn, _rsOut }) s.ValueCommitted += (_, _) => ApplySticks();
        foreach (var s in new[] { _ltDead, _ltMax, _rtDead, _rtMax }) s.ValueCommitted += (_, _) => ApplyTriggers();
        var reset = new RButton { Text = "Default deadzones" };
        reset.SetBounds(x3, y, colW, rowH);
        reset.Click += (_, _) =>
        {
            _lsIn.Value = _rsIn.Value = 5; _lsOut.Value = _rsOut.Value = 100;
            _ltDead.Value = _rtDead.Value = 5; _ltMax.Value = _rtMax.Value = 95;
            ApplySticks(); ApplyTriggers();
        };
        Controls.Add(reset);
        y += rowH + S(8);
        int bottom3 = y;

        int bottom = Math.Max(bottom1, Math.Max(bottom2, bottom3));
        _status.ForeColor = Theme.ForeDim; _status.SetBounds(pad, bottom, x3 + colW - pad, S(26)); Controls.Add(_status);
        ClientSize = new Size(x3 + colW + pad, bottom + S(26) + pad);
    }

    // ---- Load ----------------------------------------------------------------------------------

    void LoadFromDevice()
    {
        _loading = true;
        var state = Pad.Run(p => new
        {
            vib = p.GetVibration(),
            poll = p.GetFeature(Feature.PollRate),
            tp = p.GetFeature(Feature.TouchpadEnable),
            imu = p.GetFeature(Feature.ImuEnable),
            ls = p.GetStickDeadzone(1), rs = p.GetStickDeadzone(2),
            lt = p.GetTriggerDeadzone(1), rt = p.GetTriggerDeadzone(2),
        });
        if (state is null) { _status.Text = "Controller not found."; _loading = false; return; }

        if (state.vib is byte v && v < 4) _vibration.SelectedIndex = v;
        if (state.poll is byte pr && pr < 4) _poll.SelectedIndex = pr;
        _touchpad.Checked = state.tp == 1;
        _gyro.Checked = state.imu == 1;
        if (state.ls is { } ls) { _lsIn.Value = ls.inner; _lsOut.Value = ls.outer; }
        if (state.rs is { } rs) { _rsIn.Value = rs.inner; _rsOut.Value = rs.outer; }
        if (state.lt is { } lt) { _ltDead.Value = lt.dead; _ltMax.Value = lt.max; }
        if (state.rt is { } rt) { _rtDead.Value = rt.dead; _rtMax.Value = rt.max; }
        UpdateMouseUI();
        _status.Text = "Settings are stored in the controller.";
        _loading = false;
        _loaded = true;
        if (_mapButton.SelectedIndex < 0) _mapButton.SelectedIndex = 0; else ShowMapping();
    }

    void Do(Func<LegionController, bool> action, string what)
    {
        if (_loading) return;
        bool ok = Pad.Run(p => (bool?)action(p)) == true;
        _status.Text = ok ? $"{what} saved." : $"{what}: controller did not acknowledge.";
        Log.Write($"Controller {what}: {(ok ? "ok" : "failed")}");
    }

    // ---- Button mapping -----------------------------------------------------------------------

    byte SelectedButton => KeyCatalog.Buttons[Math.Max(0, _mapButton.SelectedIndex)].Id;

    static readonly KeyCatalog.Target ReservedForShortcuts = new("Reserved for shortcuts", MapType.System, 0);

    /// <summary>While back-paddle shortcuts are on, Y1/Y2 are owned by them (mapped to internal F23/F24).</summary>
    bool PaddleLocked(byte button) => _cfg.ChordsEnabled && button is PaddleChords.PaddleY1 or PaddleChords.PaddleY2;

    /// <summary>Re-reads the selected button's mapping (e.g. after shortcuts were toggled).</summary>
    public void RefreshMapping() { if (_loaded) ShowMapping(); }

    void ShowMapping()
    {
        if (PaddleLocked(SelectedButton))
        {
            _loading = true;
            int idx = _mapTarget.Items.IndexOf(ReservedForShortcuts);
            if (idx < 0) idx = _mapTarget.Items.Add(ReservedForShortcuts);
            _mapTarget.SelectedIndex = idx;
            foreach (var c in _mods) c.Checked = false;
            _mapTarget.Enabled = false; _mapTarget.Invalidate();
            foreach (var c in _mods) { c.Enabled = false; c.Invalidate(); }
            _status.Text = "This paddle is used by back-paddle shortcuts. Turn shortcuts off to remap it.";
            _loading = false;
            return;
        }
        _mapTarget.Enabled = true; _mapTarget.Invalidate();
        _mapTarget.Items.Remove(ReservedForShortcuts);
        var m = Pad.Run(p => p.GetKeyMap(SelectedButton));
        _loading = true;
        if (m is null) { _mapTarget.SelectedIndex = -1; _status.Text = "Couldn't read the button mapping."; }
        else
        {
            var (t, mods) = KeyCatalog.Describe(m);
            int idx = _mapTarget.Items.IndexOf(t);
            if (idx < 0) idx = _mapTarget.Items.Add(t);
            _mapTarget.SelectedIndex = idx;
            for (int i = 0; i < _mods.Length; i++) _mods[i].Checked = mods.Contains(KeyCatalog.Modifiers[i].Hid);
        }
        UpdateModsEnabled();
        _loading = false;
    }

    void UpdateModsEnabled()
    {
        bool kb = _mapTarget.SelectedItem is KeyCatalog.Target { Type: MapType.Keyboard };
        foreach (var c in _mods) { c.Enabled = kb; if (!kb) c.Checked = false; c.Invalidate(); }
    }

    void ApplyMapping()
    {
        if (_loading || _mapTarget.SelectedItem is not KeyCatalog.Target t || t == ReservedForShortcuts) return;
        byte button = SelectedButton;
        if (PaddleLocked(button)) return;
        var mods = KeyCatalog.Modifiers.Where((m, i) => _mods[i].Checked).Select(m => m.Hid).ToArray();
        Do(p => p.SetKeyMap(KeyCatalog.ToMapping(button, t, mods)),
            $"{KeyCatalog.Buttons[_mapButton.SelectedIndex].Name} → {KeyCatalog.ComboName(t, mods)}");
    }

    void ResetMappings()
    {
        string note = _cfg.ChordsEnabled ? "\n\nThe back paddles stay reserved for shortcuts." : "";
        if (MessageBox.Show(this, "Restore every button to its factory mapping?" + note, "Button mapping",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        Do(p => KeyCatalog.Buttons.Where(b => !PaddleLocked(b.Id)).All(b => p.SetKeyMap(DefaultMapping(b.Id))), "Button mappings reset");
        ShowMapping();
    }

    // ---- Mouse mode -----------------------------------------------------------------------------

    readonly RCheckBox _invX = new(), _invY = new();
    readonly RButton _recal = new() { Text = "Calibrate" };
    readonly System.Windows.Forms.Timer _gyroStatusTimer = new() { Interval = 400 };
    Label _mouseRowLabel = new();
    string? _lastGyroStatus;

    void SetMouseMode(string mode)
    {
        _cfg.MouseMode = mode; _cfg.Save();
        MouseModes.Apply(_cfg);
        UpdateMouseUI();
        _status.Text = mode switch { "gyro" => "Gyro mouse on.", "joystick" => "Joystick mouse on.", _ => "Mouse mode off." };
    }

    void UpdateMouseUI()
    {
        foreach (var (m, b) in _mouseButtons) b.Active = m == _cfg.MouseMode;
        bool joy = _cfg.MouseMode == "joystick", gyro = _cfg.MouseMode == "gyro";
        _mouseSens.Enabled = joy || gyro;

        // Row 1: stick (joystick) or horizontal axis (gyro)
        _mouseStick.Items.Clear();
        if (gyro)
        {
            _mouseRowLabel.Text = "Left/right";
            _mouseStick.Items.AddRange(["Turn (yaw)", "Tilt sideways (roll)"]);
            _mouseStick.SelectedIndex = _cfg.GyroHorizontalRoll ? 1 : 0;
        }
        else
        {
            _mouseRowLabel.Text = "Stick";
            _mouseStick.Items.AddRange(["Right stick", "Left stick"]);
            _mouseStick.SelectedIndex = _cfg.JoystickMouseRightStick ? 0 : 1;
        }
        _mouseStick.Enabled = joy || gyro;
        _mouseStick.Invalidate();

        // Row 2: click buttons (shared by joystick and gyro)
        _mouseBumpers.Enabled = joy || gyro; _mouseBumpers.Checked = _cfg.JoystickMouseBumpers; _mouseBumpers.Invalidate();

        // Row 3: joystick/off → hint text; gyro → Recalibrate + Invert X / Invert Y (status goes to the bottom line)
        _recal.Visible = _invX.Visible = _invY.Visible = gyro;
        _mouseHint.Visible = !gyro;
        if (gyro)
        {
            int colRight = _mouseBumpers.Left + _mouseBumpers.Width;
            _recal.Width = S(96);
            int ix = _recal.Right + S(8), iw = (colRight - ix) / 2;
            _invX.SetBounds(ix, _recal.Top + (_recal.Height - _invX.Height) / 2, iw, _invX.Height);
            _invY.SetBounds(ix + iw, _invX.Top, iw, _invX.Height);
            _invX.Checked = _cfg.GyroInvertX; _invY.Checked = _cfg.GyroInvertY;
            _status.Text = "Gyro: " + GyroMouse.Status;
        }
        _mouseHint.SetBounds(_recal.Left, _recal.Top, _mouseBumpers.Width, S(44));

        bool was = _loading; _loading = true;
        _mouseSens.Value = gyro ? _cfg.GyroMouseSpeed : _cfg.JoystickMouseSpeed;
        _mouseSensVal.Text = _mouseSens.Value.ToString();
        _loading = was;
        _mouseHint.Text = joy ? (_cfg.JoystickMouseBumpers ? "Left click: RB · Right click: LB · Other stick scrolls"
                                                          : "Left click: RT · Right click: LT · Other stick scrolls")
                        : "Controller works as a normal gamepad";
    }

    void ApplyMouseSensitivity()
    {
        if (_loading) return;
        if (_cfg.MouseMode == "joystick")
        {
            _cfg.JoystickMouseSpeed = _mouseSens.Value; _cfg.Save();
            JoystickMouse.Speed = _mouseSens.Value;
            _status.Text = $"Joystick mouse sensitivity {_mouseSens.Value}.";
        }
        else if (_cfg.MouseMode == "gyro")
        {
            _cfg.GyroMouseSpeed = _mouseSens.Value; _cfg.Save();
            GyroMouse.Speed = _mouseSens.Value;
            _status.Text = $"Gyro mouse sensitivity {_mouseSens.Value}.";
        }
    }

    // ---- Deadzones ----------------------------------------------------------------------------

    void ApplySticks()
    {
        if (_lsOut.Value <= _lsIn.Value) _lsOut.Value = _lsIn.Value + 1;
        if (_rsOut.Value <= _rsIn.Value) _rsOut.Value = _rsIn.Value + 1;
        Do(p => p.SetStickDeadzone(1, (byte)_lsIn.Value, (byte)_lsOut.Value) & p.SetStickDeadzone(2, (byte)_rsIn.Value, (byte)_rsOut.Value), "Stick deadzones");
    }

    void ApplyTriggers()
    {
        if (_ltMax.Value <= _ltDead.Value) _ltMax.Value = _ltDead.Value + 1;
        if (_rtMax.Value <= _rtDead.Value) _rtMax.Value = _rtDead.Value + 1;
        Do(p => p.SetTriggerDeadzone(1, (byte)_ltDead.Value, (byte)_ltMax.Value) & p.SetTriggerDeadzone(2, (byte)_rtDead.Value, (byte)_rtMax.Value), "Trigger deadzones");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    sealed class KeyHook : IDisposable
    {
        delegate IntPtr Proc(int code, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetWindowsHookExW(int id, Proc p, IntPtr mod, uint tid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
        readonly Proc _proc; readonly IntPtr _h;
        public KeyHook(Action<int> onKey)
        {
            _proc = (code, w, l) =>
            {
                if (code >= 0 && (w == 0x100 || w == 0x104)) onKey(System.Runtime.InteropServices.Marshal.ReadInt32(l));
                return CallNextHookEx(_h, code, w, l);
            };
            _h = SetWindowsHookExW(13 /*WH_KEYBOARD_LL*/, _proc, IntPtr.Zero, 0);
        }
        public void Dispose() => UnhookWindowsHookEx(_h);
    }
}
