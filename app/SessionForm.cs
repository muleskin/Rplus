using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Rplus;

/// <summary>The window that controls a remote Rplus: live screen, input, chat, file transfer.</summary>
internal sealed class SessionForm : Form
{
    private readonly Config _cfg;
    private readonly string _host;
    private readonly int _port;
    private Client _client;

    public Action<string, string> PeerSaved;   // host, id

    private RemoteScreen _screen;
    private ChatPanel _chat;
    private FilePanel _files;
    private Panel _toolbar;
    private Label _status;
    private bool _fileMode;
    private bool _fullscreen;
    private FormBorderStyle _savedBorder;
    private Rectangle _savedBounds;

    private bool _canFile = true;

    private readonly string _peerId;   // set when connecting by ID through the ID server

    public SessionForm(Config cfg, string host, int port, bool fileMode, string peerIdViaServer = null)
    {
        _cfg = cfg; _host = host; _port = port; _fileMode = fileMode; _peerId = peerIdViaServer;
        Text = $"Rplus — {(_peerId ?? host)}";
        base.Icon = Theme.AppIcon();
        BackColor = Color.FromArgb(18, 22, 28);
        ClientSize = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = false;
        DoubleBuffered = true;

        BuildToolbar();
        _screen = new RemoteScreen(_cfg) { Dock = DockStyle.Fill, Visible = !fileMode };
        _chat = new ChatPanel { Dock = DockStyle.Right, Width = 280, Visible = false };
        _files = new FilePanel(_cfg) { Dock = DockStyle.Fill, Visible = fileMode };

        _status = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Theme.RailText,
            BackColor = Color.FromArgb(27, 34, 43), TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0), Font = Theme.F(8.5f), Text = "  Connecting…" };

        Controls.Add(_screen);
        Controls.Add(_files);
        Controls.Add(_chat);
        Controls.Add(_status);
        Controls.Add(_toolbar);

        Shown += (_, _) => StartClient();
    }

    // ---------------- toolbar ----------------
    private Func<float, Font> Icon => sz => new Font(FontAvail("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", sz);
    private static bool FontAvail(string n) { try { using var f = new Font(n, 9f); return string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase); } catch { return false; } }

    private ComboBox _quality, _viewStyle;

    private void BuildToolbar()
    {
        _toolbar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(27, 34, 43) };
        int x = 10;

        _quality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Font = Theme.F(9f),
            FlatStyle = FlatStyle.Flat, Location = new Point(x, 10) };
        _quality.Items.AddRange(new object[] { "Best quality", "Balanced", "Low (faster)", "Custom" });
        _quality.SelectedIndex = _cfg.ImageQuality switch { "Best" => 0, "Low" => 2, "Custom" => 3, _ => 1 };
        _quality.SelectedIndexChanged += (_, _) =>
        {
            _cfg.ImageQuality = _quality.SelectedIndex switch { 0 => "Best", 2 => "Low", 3 => "Custom", _ => "Balanced" };
            _client?.SetQuality(QNum(), _cfg.Fps());
        };
        _toolbar.Controls.Add(_quality); x += 140;

        _viewStyle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Font = Theme.F(9f),
            FlatStyle = FlatStyle.Flat, Location = new Point(x, 10) };
        _viewStyle.Items.AddRange(new object[] { "Adjust window", "Original size" });
        _viewStyle.SelectedIndex = _cfg.ViewStyle == "Original" ? 1 : 0;
        _viewStyle.SelectedIndexChanged += (_, _) =>
        { _cfg.ViewStyle = _viewStyle.SelectedIndex == 1 ? "Original" : "Adjust window"; _screen.Fit = _viewStyle.SelectedIndex == 0; _screen.Invalidate(); };
        _toolbar.Controls.Add(_viewStyle); x += 152;

        x = AddTool(x, "", "Keyboard", true, b => _screen.ForwardKeys = b);
        x = AddTool(x, "", "Clipboard", false, _ => SendClipboard(), toggle: false);
        x = AddTool(x, "", "Files", false, b => ToggleFiles(b));
        x = AddTool(x, "", "Chat", false, b => { _chat.Visible = b; });
        x = AddTool(x, "", "Full screen", false, b => ToggleFullscreen(b));

        var close = new FlatButton { Text = "Disconnect", Primary = true, Fill = Theme.Danger,
            Size = new Size(104, 30), Location = new Point(0, 8) };
        close.Click += (_, _) => Close();
        _toolbar.Controls.Add(close);
        _toolbar.Resize += (_, _) => close.Location = new Point(_toolbar.Width - 114, 8);
        close.Location = new Point(_toolbar.Width - 114, 8);
    }

    private int AddTool(int x, string glyph, string tip, bool on, Action<bool> act, bool toggle = true)
    {
        var b = new ToolButton(Icon) { Glyph = glyph, Tip = tip, Toggleable = toggle, On = on,
            Location = new Point(x, 7), Size = new Size(46, 32) };
        b.Clicked = () => act(b.On);
        _toolbar.Controls.Add(b);
        return x + 50;
    }

    private void ToggleFiles(bool show)
    {
        _fileMode = show;
        _files.Visible = show;
        _screen.Visible = !show;
        if (show && _canFile) { _files.Attach(_client); _files.Start(); }
    }

    private void ToggleFullscreen(bool on)
    {
        _fullscreen = on;
        if (on)
        {
            _savedBorder = FormBorderStyle; _savedBounds = Bounds;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else { FormBorderStyle = _savedBorder; WindowState = FormWindowState.Normal; Bounds = _savedBounds; }
    }

    // ---------------- client wiring ----------------
    private void StartClient()
    {
        _client = new Client();
        _client.OnStatus = s => Ui(() => SetStatus(s));
        _client.OnAuthRequest = (pw, click) => Ui(() => AuthRequested(pw, click));
        _client.OnAuthResult = (ok, reason, retry) => Ui(() => AuthResult(ok, reason, retry));
        _client.OnPerms = (k, c, f, a) => Ui(() => { _canFile = f; });
        _client.OnFrame = (bmp, w, h) => Ui(() => _screen.SetFrame(bmp));
        _client.OnChat = t => Ui(() => _chat.Add("Remote", t));
        _client.OnFileList = (p, e) => Ui(() => _files.ShowRemote(p, e));
        _client.OnFileData = (n, d) => Ui(() => _files.Saved(n, d));
        _client.OnClosed = () => Ui(() => SetStatus("Disconnected"));

        _screen.Send = _client;
        _chat.OnSend = t => { _client.SendChat(t); _chat.Add("Me", t); };

        if (_peerId != null)
        {
            SetStatus($"Connecting to {_peerId} through your ID server…");
            Task.Run(() =>
            {
                try
                {
                    var s = Rv.ConnectById(_cfg, _peerId, msg => Ui(() => SetStatus(msg)));
                    Ui(() => SetStatus("Relay joined, waiting for the remote side…"));
                    _client.ConnectStream(s, _cfg.Id);
                }
                catch (Exception e) { Ui(() => SetStatus(e.Message)); }
            });
            return;
        }

        SetStatus($"Connecting to {_host}:{_port}…");
        if (!_client.Connect(_host, _port, _cfg.Id))
            SetStatus("Could not connect. Is Rplus running and sharing on the other PC?");
    }

    private bool _byPassword, _byClick, _authed;
    private PasswordForm _pwDialog;

    /// <summary>The other PC says how it lets people in: password, its accept window, or either.</summary>
    private void AuthRequested(bool byPassword, bool byClick)
    {
        _byPassword = byPassword; _byClick = byClick;
        if (byClick) SetStatus("Waiting for the person at the other computer to accept…");
        if (byPassword) PromptPassword();
    }

    private void AuthResult(bool ok, string reason, bool retry)
    {
        if (!ok)
        {
            if (retry)
            {
                SetStatus($"{reason}. Try again, or wait for them to accept.");
                PromptPassword();
            }
            else SetStatus("Rejected: " + reason);
            return;
        }
        _authed = true;
        // Accepted from the other side while the password box was still open: close it.
        if (_pwDialog is { IsDisposed: false } d) { d.DialogResult = DialogResult.Abort; d.Close(); }
        SetStatus("Connected");
        _client.SetQuality(QNum(), _cfg.Fps());
        PeerSaved?.Invoke(_peerId != null ? "" : _host, _peerId ?? _host);
        if (_fileMode) ToggleFiles(true);
        else _screen.Focus();
    }

    private void PromptPassword()
    {
        if (_authed || _pwDialog != null) return;
        using var dlg = new PasswordForm(_peerId ?? _host, _byClick);
        _pwDialog = dlg;
        DialogResult r;
        try { r = dlg.ShowDialog(this); }
        finally { _pwDialog = null; }
        if (_authed) return;                                      // they accepted meanwhile
        if (r == DialogResult.OK) _client.SendPassword(dlg.Password);
        else if (_byClick) SetStatus("Waiting for the person at the other computer to accept…");
        else Close();                                             // password-only: nothing to wait for
    }

    private void SendClipboard()
    {
        try { if (Clipboard.ContainsText()) _client?.TypeText(Clipboard.GetText()); } catch { }
    }

    private int QNum() => _cfg.JpegQuality();
    private void SetStatus(string s) => _status.Text = "  " + s;
    private void Ui(Action a) { try { if (IsHandleCreated) BeginInvoke(a); } catch { } }

    protected override void OnFormClosing(FormClosingEventArgs e) { _client?.Close(); base.OnFormClosing(e); }
}

/// <summary>A flat toggleable toolbar button for the dark session toolbar.</summary>
internal sealed class ToolButton : Control
{
    private readonly Func<float, Font> _icon;
    public string Glyph = "", Tip = "";
    public bool Toggleable = true, On;
    public Action Clicked;
    private bool _hover;

    public ToolButton(Func<float, Font> icon)
    {
        _icon = icon;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        var tt = new ToolTip();
        HandleCreated += (_, _) => tt.SetToolTip(this, Tip);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e) { if (Toggleable) On = !On; Clicked?.Invoke(); Invalidate(); base.OnMouseClick(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(27, 34, 43));
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (On) Theme.FillRound(g, r, 7, Theme.Accent);
        else if (_hover) Theme.FillRound(g, r, 7, Color.FromArgb(40, 50, 62));
        TextRenderer.DrawText(g, Glyph, _icon(12.5f), r, On ? Color.White : Theme.RailText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Small password prompt shown when the host asks for one.</summary>
internal sealed class PasswordForm : Form
{
    private readonly TextBox _box;
    public string Password => _box.Text;
    public PasswordForm(string host, bool canWaitForAccept = false)
    {
        Text = "Password required";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(340, 160);
        BackColor = Theme.Card; Font = Theme.F(10f);
        Controls.Add(new Label { Text = $"Enter the password for {host}", AutoSize = true,
            Location = new Point(20, 20), ForeColor = Theme.TextMain, Font = Theme.F(10f) });
        if (canWaitForAccept)
            Controls.Add(new Label { Text = "…or close this and wait for them to accept.", AutoSize = true,
                Location = new Point(20, 82), ForeColor = Theme.TextDim, Font = Theme.F(8.5f) });
        _box = new TextBox { UseSystemPasswordChar = true, Location = new Point(20, 54), Width = 300, Font = Theme.F(12f) };
        Controls.Add(_box);
        var ok = new FlatButton { Text = "Connect", Primary = true, Location = new Point(172, 100), Size = new Size(148, 40) };
        var cancel = new FlatButton { Text = "Cancel", Primary = false, Location = new Point(20, 100), Size = new Size(140, 40) };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
        Shown += (_, _) => _box.Focus();
    }
}
