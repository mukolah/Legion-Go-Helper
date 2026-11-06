using LegionHelper.Hardware;
using LegionHelper.UI;

namespace LegionHelper;

/// <summary>"Shortcuts" window: assign actions to back paddle + D-pad / ABXY chords.</summary>
public class ShortcutsForm : Form
{
    readonly Config _cfg;
    readonly Func<bool, bool> _setEnabled;
    readonly Action _legionChanged;
    readonly RCheckBox _enable = new();
    readonly Label _status = new();
    readonly List<RComboBox> _combos = new();
    float _k;

    /// <param name="setEnabled">Turns chords on/off (paddle remap + hook); returns success.</param>
    public ShortcutsForm(Config cfg, Func<bool, bool> setEnabled, Action legionChanged)
    {
        _cfg = cfg; _setEnabled = setEnabled; _legionChanged = legionChanged;
        Text = "Shortcuts - Legion buttons & back-paddle combos";
        BackColor = Theme.Back; ForeColor = Theme.Fore; Font = Theme.Normal;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.Manual; ShowInTaskbar = false;
        Icon = TrayIcon.Make(Theme.Custom);
        _k = DeviceDpi / 96f;
        Build();
    }

    int S(int px) => (int)(px * _k);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

    void Build()
    {
        int pad = S(14), colW = S(330), gap = S(26), labelW = S(100), y = pad;
        Label L(string t, int x, int yy, int w, Font? f = null, Color? c = null, int h = 26)
        {
            var l = new Label { Text = t, Font = f ?? Theme.Normal, ForeColor = c ?? Theme.Fore, TextAlign = ContentAlignment.MiddleLeft };
            l.SetBounds(x, yy, w, S(h)); Controls.Add(l); return l;
        }
        int fullW = 2 * colW + gap;

        // ---- Legion L / Legion R (system buttons: not remappable, but their presses are visible to the app)
        L("Legion buttons", pad, y, fullW, Theme.Title); y += S(32);
        foreach (var (name, x, isLeft) in new[] { ("Legion L", pad, true), ("Legion R", pad + colW + gap, false) })
        {
            L(name, x, y + S(4), labelW);
            var c = new RComboBox { AccentColor = Theme.Custom };
            c.Items.AddRange(Shortcuts.All);
            c.Location = new Point(x + labelW, y); c.Width = colW - labelW;
            c.SelectedItem = Shortcuts.Find(isLeft ? _cfg.LegionLAction : _cfg.LegionRAction);
            c.SelectionChangeCommitted += (_, _) =>
            {
                var a = (Shortcuts.Action)c.SelectedItem!;
                string? v = a == Shortcuts.None ? null : a.Id;
                if (isLeft) _cfg.LegionLAction = v; else _cfg.LegionRAction = v;
                _cfg.Save();
                _legionChanged();
                _status.Text = $"{name} → {a.Name}";
            };
            Controls.Add(c);
        }
        y += S(44);
        L("Works on its own - no need to enable the paddle shortcuts below.", pad, y, fullW, null, Theme.ForeDim);
        y += S(40);

        _enable.Text = "Enable back-paddle shortcuts";
        _enable.AccentColor = Theme.Custom;
        _enable.SetBounds(pad, y, fullW, S(30)); Controls.Add(_enable);
        _enable.Checked = _cfg.ChordsEnabled;
        _enable.Click += (_, _) =>
        {
            bool ok = _setEnabled(_enable.Checked);
            if (!ok) _enable.Checked = !_enable.Checked;
            UpdateEnabled();
        };
        y += S(34);
        L("Hold a back paddle and press a D-pad direction or A / B / X / Y. While enabled, the paddles are " +
          "remapped to hidden keys (F23 / F24) and act only as shortcut modifiers.", pad, y, fullW, null, Theme.ForeDim, 44);
        y += S(52);

        int top = y;
        foreach (var (paddle, title, x) in new[] { ("Y1", "Hold Y1 (left paddle) +", pad), ("Y2", "Hold Y2 (right paddle) +", pad + colW + gap) })
        {
            y = top;
            L(title, x, y, colW, Theme.Title); y += S(34);
            foreach (var (_, id, name) in PaddleChords.Buttons)
            {
                string key = $"{paddle}+{id}";
                L(name, x, y + S(4), labelW);
                var c = new RComboBox { AccentColor = Theme.Custom };
                c.Items.AddRange(Shortcuts.All);
                c.Location = new Point(x + labelW, y); c.Width = colW - labelW;
                c.SelectedItem = Shortcuts.Find(_cfg.Chords.GetValueOrDefault(key));
                c.SelectionChangeCommitted += (_, _) =>
                {
                    var a = (Shortcuts.Action)c.SelectedItem!;
                    if (a == Shortcuts.None) _cfg.Chords.Remove(key); else _cfg.Chords[key] = a.Id;
                    _cfg.Save();
                    _status.Text = $"{paddle} + {name} → {a.Name}";
                };
                Controls.Add(c); _combos.Add(c);
                y += S(44);
            }
        }
        y += S(4);
        _status.ForeColor = Theme.ForeDim; _status.SetBounds(pad, y, fullW, S(26)); Controls.Add(_status);
        ClientSize = new Size(pad * 2 + fullW, y + S(26) + pad);
        UpdateEnabled();
    }

    void UpdateEnabled()
    {
        foreach (var c in _combos) { c.Enabled = _enable.Checked; c.Invalidate(); }
        _status.Text = _enable.Checked ? "Shortcuts active." : "Shortcuts are off; paddles work as mapped in Controller settings.";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }
}
