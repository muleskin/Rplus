using System.Drawing;
using System.Windows.Forms;

namespace Rplus;

/// <summary>Settings surface for the config keys (see MECHANICS.md).</summary>
internal sealed class SettingsPanel : Panel
{
    private readonly Config _cfg;
    public Action Changed;
    private int _y;

    public SettingsPanel(Config cfg, Func<float, Font> icon)
    {
        _cfg = cfg;
        BackColor = Theme.Canvas;
        Font = Theme.F(9.5f);
        Width = 560;
        _y = 0;

        Title("Settings");

        Section("Security");
        // approve-mode: "click" (accept window), "password", or both (the default).
        var acceptWin = Check("Accept window (someone at this PC clicks Accept)", _cfg.RequiresClickApproval);
        var needPw = Check("Password", _cfg.RequiresPassword);
        Row("Accept sessions via", acceptWin);
        Row("", needPw);
        var approveNote = new Label
        {
            Text = "With both on, either one lets them in. At least one must stay on.",
            Font = Theme.F(8.5f), ForeColor = Theme.TextDim, AutoSize = true,
            Location = new Point(210, _y - 6), BackColor = Color.Transparent,
        };
        Controls.Add(approveNote);
        _y += 18;
        bool syncing = false;
        void ApplyApprove(CheckBox changed, CheckBox other)
        {
            if (syncing) return;
            syncing = true;
            if (!acceptWin.Checked && !needPw.Checked) other.Checked = true;   // never both off
            syncing = false;
            _cfg.ApproveMode = acceptWin.Checked && needPw.Checked ? "both" : acceptWin.Checked ? "click" : "password";
            Save();
        }
        acceptWin.CheckedChanged += (_, _) => ApplyApprove(acceptWin, needPw);
        needPw.CheckedChanged += (_, _) => ApplyApprove(needPw, acceptWin);

        var verify = Check("Only allow the permanent password (not the one-time one)", _cfg.UsePermanentPassword);
        Row("Verification", verify);
        verify.CheckedChanged += (_, _) => { _cfg.UsePermanentPassword = verify.Checked; Save(); };

        var pw = Text(_cfg.PermanentPassword, true);
        Row("Permanent password", pw);
        pw.TextChanged += (_, _) => { _cfg.PermanentPassword = pw.Text; Save(); };

        var len = Combo(new[] { "6 characters", "8 characters", "10 characters" },
            _cfg.TemporaryPasswordLength switch { 8 => 1, 10 => 2, _ => 0 });
        Row("One-time password length", len);
        len.SelectedIndexChanged += (_, _) => { _cfg.TemporaryPasswordLength = len.SelectedIndex switch { 1 => 8, 2 => 10, _ => 6 }; Save(); };

        var numeric = Check("Use digits only for the one-time password", _cfg.NumericOneTimePassword);
        Row("One-time password style", numeric);
        numeric.CheckedChanged += (_, _) => { _cfg.NumericOneTimePassword = numeric.Checked; Save(); };

        Section("Permissions allowed to the remote side");
        var k = Check("Keyboard & mouse", _cfg.EnableKeyboard);
        var c = Check("Clipboard", _cfg.EnableClipboard);
        var f = Check("File transfer", _cfg.EnableFileTransfer);
        var a = Check("Audio", _cfg.EnableAudio);
        Row("", k); Row("", c); Row("", f); Row("", a);
        k.CheckedChanged += (_, _) => { _cfg.EnableKeyboard = k.Checked; Save(); };
        c.CheckedChanged += (_, _) => { _cfg.EnableClipboard = c.Checked; Save(); };
        f.CheckedChanged += (_, _) => { _cfg.EnableFileTransfer = f.Checked; Save(); };
        a.CheckedChanged += (_, _) => { _cfg.EnableAudio = a.Checked; Save(); };

        Section("Display");
        var q = Combo(new[] { "Best quality", "Balanced", "Low (faster)", "Custom" },
            _cfg.ImageQuality switch { "Best" => 0, "Low" => 2, "Custom" => 3, _ => 1 });
        Row("Image quality", q);
        var custom = Slider(_cfg.CustomQuality, Config.CustomQualityMin, Config.CustomQualityMax, 100);
        Row("Custom quality", custom);
        custom.Enabled = _cfg.ImageQuality == "Custom";
        q.SelectedIndexChanged += (_, _) =>
        {
            _cfg.ImageQuality = q.SelectedIndex switch { 0 => "Best", 2 => "Low", 3 => "Custom", _ => "Balanced" };
            custom.Enabled = _cfg.ImageQuality == "Custom"; Save();
        };
        custom.Scroll += (_, _) => { _cfg.CustomQuality = custom.Value; Save(); };

        var fps = Slider(_cfg.CustomFps, Config.FpsMin, Config.FpsMax, 5);
        var fpsLabel = new Label { Text = $"{_cfg.Fps()} fps", AutoSize = true, Font = Theme.F(9f), ForeColor = Theme.TextDim, BackColor = Color.Transparent };
        Row("Frame rate", fps);
        fpsLabel.Location = new Point(fps.Right + 8, fps.Top + 8);
        Controls.Add(fpsLabel);
        fps.Scroll += (_, _) => { _cfg.CustomFps = fps.Value; fpsLabel.Text = $"{fps.Value} fps"; Save(); };

        var vs = Combo(new[] { "Original size", "Adjust window (scale to fit)" }, _cfg.ViewStyle == "Original" ? 0 : 1);
        Row("View style", vs);
        vs.SelectedIndexChanged += (_, _) => { _cfg.ViewStyle = vs.SelectedIndex == 0 ? "Original" : "Adjust window"; Save(); };

        Section("Network");
        var port = Text(_cfg.DirectPort.ToString(), false); port.Width = 120;
        Row("Direct connection port", port);
        port.TextChanged += (_, _) => { if (int.TryParse(port.Text, out var p) && p is > 0 and < 65536) { _cfg.DirectPort = p; Save(); } };

        var idsrv = Text(_cfg.IdServer, false);
        Row("ID server", idsrv);
        idsrv.TextChanged += (_, _) => { _cfg.IdServer = idsrv.Text.Trim(); Save(); };

        var relay = Text(_cfg.RelayServer, false);
        Row("Relay server", relay);
        relay.TextChanged += (_, _) => { _cfg.RelayServer = relay.Text.Trim(); Save(); };

        var key = Text(_cfg.ServerKey, false);
        Row("Key", key);
        key.TextChanged += (_, _) => { _cfg.ServerKey = key.Text.Trim(); Save(); };

        Controls.Add(new Label
        {
            Text = "Your own ID/relay server, e.g. ID 203.0.113.5:21116 and relay 203.0.113.5:21117. Leave the relay " +
                   "empty to use the ID server's port + 1. Both computers need Rplus and the same server.",
            Font = Theme.F(8.5f), ForeColor = Theme.TextDim, AutoSize = false,
            Location = new Point(210, _y - 4), Size = new Size(340, 48), BackColor = Color.Transparent,
        });
        _y += 50;

        // Warn when the two ports look swapped (ID server 21116, relay = ID port + 1 = 21117).
        var swapWarn = new Label
        {
            Font = Theme.F(8.5f, FontStyle.Bold), ForeColor = Theme.Danger, AutoSize = false,
            Location = new Point(210, _y - 6), Size = new Size(340, 34), BackColor = Color.Transparent,
        };
        Controls.Add(swapWarn);
        _y += 30;
        void CheckSwap()
        {
            var (_, ip) = Rv.Split(idsrv.Text, Rv.IdPort);
            var (rh, rp) = Rv.Split(relay.Text, Rv.RelayPort);
            bool idLooksRelay = !string.IsNullOrWhiteSpace(idsrv.Text) && ip == Rv.RelayPort;
            bool relayLooksId = !string.IsNullOrWhiteSpace(relay.Text) && rp == Rv.IdPort;
            swapWarn.Text = idLooksRelay || relayLooksId
                ? "These look swapped: the ID server normally uses port 21116 and the relay 21117."
                : "";
        }
        idsrv.TextChanged += (_, _) => CheckSwap();
        relay.TextChanged += (_, _) => CheckSwap();
        CheckSwap();

        var wl = Check("Only allow connections from the IP whitelist below", _cfg.UseIpWhitelist);
        Row("IP whitelist", wl);
        wl.CheckedChanged += (_, _) => { _cfg.UseIpWhitelist = wl.Checked; Save(); };
        var wlText = Text(_cfg.Whitelist, false);
        Row("Whitelisted IPs", wlText);
        wlText.TextChanged += (_, _) => { _cfg.Whitelist = wlText.Text; Save(); };

        Height = _y + 20;
    }

    private void Save() { _cfg.Save(); Changed?.Invoke(); }

    // ---- builders ----
    private void Title(string t)
    {
        Controls.Add(new Label { Text = t, Font = Theme.F(15f, FontStyle.Bold), ForeColor = Theme.TextMain,
            AutoSize = true, Location = new Point(0, _y), BackColor = Color.Transparent });
        _y += 44;
    }
    private void Section(string t)
    {
        _y += 8;
        Controls.Add(new Label { Text = t.ToUpperInvariant(), Font = Theme.F(8.5f, FontStyle.Bold),
            ForeColor = Theme.Accent, AutoSize = true, Location = new Point(0, _y), BackColor = Color.Transparent });
        _y += 26;
    }
    private void Row(string label, Control editor)
    {
        if (!string.IsNullOrEmpty(label))
            Controls.Add(new Label { Text = label, Font = Theme.F(9.5f), ForeColor = Theme.TextMain,
                AutoSize = false, Size = new Size(200, 24), Location = new Point(0, _y + 3),
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent });
        editor.Location = new Point(210, _y);
        Controls.Add(editor);
        _y += editor.Height + 10;
    }

    private ComboBox Combo(string[] items, int sel)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Font = Theme.F(9.5f), FlatStyle = FlatStyle.Flat };
        c.Items.AddRange(items);
        c.SelectedIndex = Math.Clamp(sel, 0, items.Length - 1);
        return c;
    }
    private CheckBox Check(string text, bool on) =>
        new() { Text = text, UseMnemonic = false, Checked = on, AutoSize = true, Font = Theme.F(9.5f), ForeColor = Theme.TextMain, BackColor = Color.Transparent };
    private TextBox Text(string val, bool pw) =>
        new() { Text = val, Width = 300, Font = Theme.F(9.5f), UseSystemPasswordChar = pw };
    private TrackBar Slider(int val, int min, int max, int tick) =>
        new() { Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Width = 300, TickFrequency = tick, Height = 40 };
}
