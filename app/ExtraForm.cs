using LegionHelper.UI;

namespace LegionHelper;

/// <summary>"Extra" window: mode-change feedback and the mode-cycle hotkey.</summary>
public class ExtraForm : Form
{
    readonly ModeControl _mc;
    readonly Func<bool> _hotkeyChanged;   // re-registers the hotkey; false = Windows refused (in use)
    readonly RCheckBox _osd = new(), _flash = new(), _cycleCustom = new();
    readonly RComboBox _cycleKey = new();
    readonly RCheckBox[] _mods = [new() { Text = "Ctrl" }, new() { Text = "Alt" }, new() { Text = "Shift" }, new() { Text = "Win" }];
    static readonly uint[] ModBits = [0x2 /*MOD_CONTROL*/, 0x1 /*MOD_ALT*/, 0x4 /*MOD_SHIFT*/, 0x8 /*MOD_WIN*/];
    readonly Label _keyStatus = new();
    float _k;

    /// <summary>Selectable keys (VK, name). F23/F24 are reserved for back-paddle shortcuts.</summary>
    static readonly (int Vk, string Name)[] Keys =
    [
        (0, "None"),
        .. Enumerable.Range(13, 10).Select(n => (0x6F + n, $"F{n}")),      // F13-F22: fine without modifiers
        .. Enumerable.Range(1, 12).Select(n => (0x6F + n, $"F{n}")),       // F1-F12
        .. Enumerable.Range('A', 26).Select(c => (c, ((char)c).ToString())),
        .. Enumerable.Range('0', 10).Select(c => (c, ((char)c).ToString())),
    ];

    /// <summary>Keys that are only safe as a hotkey together with a modifier (they are used for normal typing/apps).</summary>
    static bool NeedsModifier(int vk) => vk is >= 0x30 and <= 0x5A || vk is >= 0x70 and <= 0x7B;

    public ExtraForm(ModeControl mc, Func<bool> hotkeyChanged)
    {
        _mc = mc; _hotkeyChanged = hotkeyChanged;
        Text = "Extra - Legion Helper";
        BackColor = Theme.Back; ForeColor = Theme.Fore; Font = Theme.Normal;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.Manual; ShowInTaskbar = false;
        Icon = TrayIcon.Make(Theme.Balanced);
        _k = DeviceDpi / 96f;
        Build();
    }

    int S(int px) => (int)(px * _k);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

    void Build()
    {
        int pad = S(14), w = S(380), y = pad;
        Label L(string t, int yy, Font? f = null, Color? c = null, int h = 26)
        {
            var l = new Label { Text = t, Font = f ?? Theme.Normal, ForeColor = c ?? Theme.Fore };
            l.SetBounds(pad, yy, w, S(h)); Controls.Add(l); return l;
        }

        L("When the mode changes", y, Theme.Title); y += S(32);
        _osd.Text = "Show a pop-up in the middle of the screen";
        _osd.SetBounds(pad, y, w, S(30)); Controls.Add(_osd); y += S(36);
        _osd.Checked = _mc.Cfg.ShowOsd;
        _osd.Click += (_, _) => { _mc.Cfg.ShowOsd = _osd.Checked; _mc.Cfg.Save(); };

        _flash.Text = "Flash the stick lights in the mode colour";
        _flash.SetBounds(pad, y, w, S(30)); Controls.Add(_flash); y += S(32);
        _flash.Checked = _mc.Cfg.RgbModeFlash;
        _flash.Click += (_, _) => { _mc.Cfg.RgbModeFlash = _flash.Checked; _mc.Cfg.Save(); };
        L("Quiet = green · Balanced = blue · Performance = red · Custom = orange. Your lighting is restored afterwards.",
            y, null, Theme.ForeDim, 44);
        y += S(54);

        var preview = new RButton { Text = "Preview" };
        preview.SetBounds(pad, y, S(140), S(36));
        preview.Click += (_, _) => MainForm.ModeFeedback(_mc.CurrentMode, _mc.Cfg, force: true);
        Controls.Add(preview);
        y += S(52);

        L("Mode cycle shortcut", y, Theme.Title); y += S(32);
        int mw = w / 4;
        for (int i = 0; i < _mods.Length; i++)
        {
            _mods[i].SetBounds(pad + i * mw, y, mw, S(30)); Controls.Add(_mods[i]);
            _mods[i].Checked = (_mc.Cfg.CycleMods & ModBits[i]) != 0;
            _mods[i].Click += (_, _) => ApplyShortcut();
        }
        _cycleKey.Items.AddRange(Keys.Select(k => (object)k.Name).ToArray());
        y += S(38);
        L("+ Key", y + S(6), null, null, 26).Width = S(70);
        _cycleKey.Location = new Point(pad + S(70), y); _cycleKey.Width = S(160); Controls.Add(_cycleKey);
        int idx = Array.FindIndex(Keys, k => k.Vk == _mc.Cfg.CycleKey);
        _cycleKey.SelectedIndex = Math.Max(0, idx);
        _cycleKey.SelectionChangeCommitted += (_, _) => ApplyShortcut();
        y += S(42);
        _keyStatus.ForeColor = Theme.ForeDim; _keyStatus.SetBounds(pad, y, w, S(44)); Controls.Add(_keyStatus);
        y += S(46);
        UpdateKeyStatus(true);
        _cycleCustom.Text = "Include Custom in the cycle";
        _cycleCustom.SetBounds(pad, y, w, S(30)); Controls.Add(_cycleCustom); y += S(34);
        _cycleCustom.Checked = _mc.Cfg.CycleIncludesCustom;
        _cycleCustom.Click += (_, _) => { _mc.Cfg.CycleIncludesCustom = _cycleCustom.Checked; _mc.Cfg.Save(); };
        L("Tip: F13-F22 work without modifiers - map a back paddle to one in Controller → Button mapping for a hardware mode button.",
            y, null, Theme.ForeDim, 66);
        y += S(70);

        ClientSize = new Size(w + 2 * pad, y + pad);
    }

    void ApplyShortcut()
    {
        int vk = Keys[Math.Max(0, _cycleKey.SelectedIndex)].Vk;
        uint mods = 0;
        for (int i = 0; i < _mods.Length; i++) if (_mods[i].Checked) mods |= ModBits[i];
        if (vk != 0 && NeedsModifier(vk) && mods == 0)
        {
            // don't hijack a normal key from every app - keep the previous shortcut until a modifier is ticked
            _keyStatus.ForeColor = Theme.Custom;
            _keyStatus.Text = $"Add Ctrl, Alt, Shift or Win: {Keys[_cycleKey.SelectedIndex].Name} alone would stop working in other apps.";
            return;
        }
        _mc.Cfg.CycleKey = vk; _mc.Cfg.CycleMods = mods; _mc.Cfg.Save();
        UpdateKeyStatus(_hotkeyChanged());
    }

    void UpdateKeyStatus(bool registered)
    {
        string combo = ShortcutText(_mc.Cfg.CycleKey, _mc.Cfg.CycleMods);
        _keyStatus.ForeColor = registered ? Theme.ForeDim : Theme.Performance;
        _keyStatus.Text = _mc.Cfg.CycleKey == 0 ? "No shortcut set."
            : registered ? $"{combo} cycles the performance mode from anywhere."
            : $"{combo} is already used by another app - pick a different combination.";
    }

    public static string ShortcutText(int vk, uint mods)
    {
        var parts = new List<string>();
        for (int i = 0; i < ModBits.Length; i++) if ((mods & ModBits[i]) != 0) parts.Add(new[] { "Ctrl", "Alt", "Shift", "Win" }[i]);
        parts.Add(Keys.FirstOrDefault(k => k.Vk == vk).Name ?? "?");
        return string.Join(" + ", parts);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }
}
