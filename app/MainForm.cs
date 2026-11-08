using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Rplus;

internal sealed class MainForm : Form
{
    private readonly Config _cfg;
    private readonly Host _host;

    private enum Nav { Home, Peers, Settings }
    private Nav _nav = Nav.Home;

    // side rail
    private readonly (Nav nav, string glyph, string label)[] _items =
    {
        (Nav.Home, "", "Home"),
        (Nav.Peers, "", "Recent"),
        (Nav.Settings, "", "Settings"),
    };
    private int _railHover = -1;
    private const int RailW = 84;
    private Icon _logo;   // R+ logo in the side rail, same as the app icon

    // home controls
    private TextBox _remoteId;
    private FlatButton _connectBtn, _transferBtn, _refreshBtn, _copyId, _copyPw;
    private Panel _home, _peers, _settingsHost;
    private FlowLayoutPanel _peerList;
    private Label _statusLabel;

    private string _oneTimePw;

    // ID-server registration
    private ServerLink _link;
    private string _linkSig = "";
    private System.Windows.Forms.Timer _linkTimer;
    private string _serverText = "No ID server set — direct IP only";
    private bool? _serverOk;

    private string LinkSignature() => $"{_cfg.IdServer}|{_cfg.RelayServer}|{_cfg.ServerKey}|{_cfg.Id}";

    private void RestartLink()
    {
        _link?.Stop();
        _linkSig = LinkSignature();
        _serverText = "No ID server set — direct IP only"; _serverOk = null;
        _link = new ServerLink(_cfg, s => _host.ServeRelay(s));
        _link.OnState = (t, ok) => OnUi(() => { _serverText = t; _serverOk = ok; _home.Invalidate(); });
        _link.OnIdChanged = () => OnUi(() => { _linkSig = LinkSignature(); _home.Invalidate(); });
        _link.OnServerFixed = msg => OnUi(() =>
        {
            _linkSig = LinkSignature();     // the running link already uses the corrected address
            SetStatus(msg);
            RefreshSettingsPage();
        });
        _link.Start();
        _home?.Invalidate();
    }

    public MainForm(Config cfg)
    {
        _cfg = cfg;
        _oneTimePw = _cfg.RuntimeOneTimePassword;
        if (string.IsNullOrEmpty(_oneTimePw)) { _oneTimePw = _cfg.NewOneTimePassword(); _cfg.RuntimeOneTimePassword = _oneTimePw; }

        _host = new Host(_cfg) { Quality = QualityNumber(), Fps = _cfg.Fps() };
        _host.ApproveConnection = ApproveOnUi;
        _host.OnStatus = s => OnUi(() => SetStatus(s));
        _host.OnSessionState = c => OnUi(() => SetStatus(c ? "Peer connected" : "Ready"));
        _host.OnChat = t => OnUi(() => { }); // host-side chat shown in session window if open

        Text = "Rplus";
        Icon = Theme.AppIcon();
        BackColor = Theme.Canvas;
        Font = Theme.F(9.5f);
        ClientSize = new Size(1000, 660);
        MinimumSize = new Size(860, 560);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        BuildHome();
        BuildPeers();
        BuildSettingsHost();
        Controls.Add(_home);
        Controls.Add(_peers);
        Controls.Add(_settingsHost);

        _statusLabel = new Label
        {
            AutoSize = false, Dock = DockStyle.Bottom, Height = 26,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(RailW + 16, 0, 0, 0),
            ForeColor = Theme.TextDim, BackColor = Color.FromArgb(237, 240, 244),
            Font = Theme.F(8.5f),
        };
        Controls.Add(_statusLabel);

        Resize += (_, _) => DoLayout();
        Shown += (_, _) => { _host.Start(); RestartLink(); DoLayout(); ShowNav(Nav.Home); };

        // Re-register with the ID server shortly after its settings change.
        _linkTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _linkTimer.Tick += (_, _) => { if (LinkSignature() != _linkSig) RestartLink(); };
        _linkTimer.Start();
        DoLayout();
        ShowNav(Nav.Home);
    }

    // ---------------- side rail ----------------
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // rail background
        using (var b = new SolidBrush(Theme.Rail))
            g.FillRectangle(b, 0, 0, RailW, ClientSize.Height);

        // logo "R+"
        _logo ??= Theme.AppIcon(40);
        g.DrawIcon(_logo, new Rectangle(RailW / 2 - 20, 18, 40, 40));

        // items
        int y = 92;
        using var iconFont = new Font("Segoe Fluent Icons", 15f);
        using var iconFont2 = new Font("Segoe MDL2 Assets", 15f);
        for (int i = 0; i < _items.Length; i++)
        {
            var it = _items[i];
            var r = new Rectangle(10, y, RailW - 20, 56);
            bool active = it.nav == _nav;
            if (active)
                using (var ab = new SolidBrush(Color.FromArgb(38, 48, 60)))
                using (var p = Theme.Round(r, 10)) g.FillPath(ab, p);
            else if (i == _railHover)
                using (var hb = new SolidBrush(Color.FromArgb(33, 41, 52)))
                using (var p = Theme.Round(r, 10)) g.FillPath(hb, p);

            if (active)
                using (var acc = new SolidBrush(Theme.Accent))
                    g.FillRectangle(acc, 10, y + 14, 3, 28);

            Color fg = active ? Color.White : Theme.RailText;
            var ir = new Rectangle(r.X, y + 6, r.Width, 26);
            TextRenderer.DrawText(g, it.glyph, FontAvail("Segoe Fluent Icons") ? iconFont : iconFont2, ir, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var tr = new Rectangle(r.X, y + 32, r.Width, 18);
            TextRenderer.DrawText(g, it.label, Theme.F(7.8f), tr, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            y += 62;
        }
    }

    private static readonly Dictionary<string, bool> _fontCache = new();
    private static bool FontAvail(string name)
    {
        if (_fontCache.TryGetValue(name, out var v)) return v;
        bool ok = false;
        try { using var f = new Font(name, 10f); ok = string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase); } catch { }
        _fontCache[name] = ok; return ok;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int prev = _railHover;
        _railHover = -1;
        if (e.X < RailW)
        {
            int y = 92;
            for (int i = 0; i < _items.Length; i++)
            {
                if (new Rectangle(10, y, RailW - 20, 56).Contains(e.Location)) { _railHover = i; break; }
                y += 62;
            }
        }
        if (prev != _railHover) Invalidate(new Rectangle(0, 0, RailW, ClientSize.Height));
        Cursor = _railHover >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.X < RailW && _railHover >= 0) ShowNav(_items[_railHover].nav);
    }

    private void ShowNav(Nav n)
    {
        _nav = n;
        _home.Visible = n == Nav.Home;
        _peers.Visible = n == Nav.Peers;
        _settingsHost.Visible = n == Nav.Settings;
        if (n == Nav.Peers) PopulatePeers();
        if (n == Nav.Settings) BuildSettingsContent();
        Invalidate();
    }

    // ---------------- home ----------------
    private void BuildHome()
    {
        _home = new DoubleBufferedPanel { BackColor = Theme.Canvas };
        _home.Paint += PaintHome;

        _copyId = new FlatButton { Primary = false, Text = "Copy", Height = 30, Width = 64, Fill = Theme.Accent };
        _copyId.Click += (_, _) => CopyTo(_cfg.Id);
        _copyPw = new FlatButton { Primary = false, Text = "Copy", Height = 30, Width = 64, Fill = Theme.Accent };
        _copyPw.Click += (_, _) => CopyTo(_oneTimePw);
        _refreshBtn = new FlatButton { Primary = false, Text = "↻ New", Height = 30, Width = 72, Fill = Theme.Accent };
        _refreshBtn.Click += (_, _) => { _oneTimePw = _cfg.NewOneTimePassword(); _cfg.RuntimeOneTimePassword = _oneTimePw; _home.Invalidate(); };

        _remoteId = new TextBox { BorderStyle = BorderStyle.None, Font = Theme.F(15f), ForeColor = Theme.TextMain };
        _remoteId.KeyDown += (_, ev) => { if (ev.KeyCode == Keys.Enter) { ev.SuppressKeyPress = true; DoConnect(false); } };

        _connectBtn = new FlatButton { Primary = true, Text = "Connect", Height = 42, Fill = Theme.Accent };
        _connectBtn.Click += (_, _) => DoConnect(false);
        _transferBtn = new FlatButton { Primary = false, Text = "Transfer File", Height = 42, Fill = Theme.Accent };
        _transferBtn.Click += (_, _) => DoConnect(true);

        _home.Controls.Add(_copyId);
        _home.Controls.Add(_copyPw);
        _home.Controls.Add(_refreshBtn);
        _home.Controls.Add(_remoteId);
        _home.Controls.Add(_connectBtn);
        _home.Controls.Add(_transferBtn);
    }

    private Rectangle _leftCard, _rightCard, _idFieldRect;

    private void PaintHome(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // ---- left card: Your Desktop ----
        Theme.Card3(g, _leftCard, 14);
        int lx = _leftCard.X + 24, lw = _leftCard.Width - 48;
        int y = _leftCard.Y + 22;
        TextRenderer.DrawText(g, "Your Desktop", Theme.F(13f, FontStyle.Bold),
            new Rectangle(lx, y, lw, 26), Theme.TextMain, TextFormatFlags.Left); y += 30;
        TextRenderer.DrawText(g, "This desktop can be reached with the ID and password below.",
            Theme.F(9f), new Rectangle(lx, y, lw, 40), Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.WordBreak); y += 46;

        // ID
        TextRenderer.DrawText(g, "ID", Theme.F(8.5f, FontStyle.Bold),
            new Rectangle(lx, y, lw, 18), Theme.TextDim, TextFormatFlags.Left); y += 20;
        var idRow = new Rectangle(lx, y, lw, 40);
        TextRenderer.DrawText(g, FormatId(_cfg.Id), Theme.F(20f, FontStyle.Bold),
            new Rectangle(lx, y - 2, lw - 80, 40), Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        _copyId.Location = new Point(_leftCard.Right - 24 - _copyId.Width, y + 4);
        y += 52;

        // one-time password
        TextRenderer.DrawText(g, "One-time password", Theme.F(8.5f, FontStyle.Bold),
            new Rectangle(lx, y, lw, 18), Theme.TextDim, TextFormatFlags.Left); y += 20;
        var pwField = new Rectangle(lx, y, lw, 44);
        Theme.FillRound(g, pwField, 9, Color.FromArgb(245, 247, 250));
        using (var pen = new Pen(Theme.CardBorder)) { using var p = Theme.Round(pwField, 9); g.DrawPath(pen, p); }
        TextRenderer.DrawText(g, _oneTimePw, new Font("Consolas", 16f, FontStyle.Bold),
            new Rectangle(pwField.X + 14, pwField.Y, 150, pwField.Height), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        _copyPw.Location = new Point(pwField.Right - 8 - _copyPw.Width, pwField.Y + (pwField.Height - _copyPw.Height) / 2);
        _refreshBtn.Location = new Point(_copyPw.Left - 8 - _refreshBtn.Width, pwField.Y + (pwField.Height - _refreshBtn.Height) / 2);
        y += 58;

        // security line
        string mode = _cfg.ApproveMode switch
        {
            "password" => "password only (no accept window)",
            "click" => "accept window only",
            _ => "password or accept window",
        };
        TextRenderer.DrawText(g, "", IconFont(9f),
            new Rectangle(lx, y, 20, 18), Theme.Ok, TextFormatFlags.Left);
        // wraps onto a second line when the card is narrow
        var secFlags = TextFormatFlags.Left | TextFormatFlags.WordBreak;
        string secText = "Accept sessions via " + mode;
        using (var secFont = Theme.F(8.5f))
        {
            int secH = Math.Max(18, TextRenderer.MeasureText(g, secText, secFont, new Size(lw - 22, 0), secFlags).Height);
            TextRenderer.DrawText(g, secText, secFont,
                new Rectangle(lx + 22, y, lw - 22, secH), Theme.TextDim, secFlags);
            y += secH + 8;
        }

        // ID server state
        Color dot = _serverOk == true ? Theme.Ok : _serverOk == false ? Theme.Danger : Color.FromArgb(230, 160, 40);
        using (var db = new SolidBrush(dot)) g.FillEllipse(db, lx + 3, y + 5, 9, 9);
        TextRenderer.DrawText(g, _serverText, Theme.F(8.5f),
            new Rectangle(lx + 22, y, lw - 22, 36), _serverOk == false ? Theme.Danger : Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.WordBreak);

        // ---- right card: Control Remote Desktop ----
        Theme.Card3(g, _rightCard, 14);
        int rx = _rightCard.X + 24, rw = _rightCard.Width - 48;
        int ry = _rightCard.Y + 22;
        TextRenderer.DrawText(g, "Control Remote Desktop", Theme.F(13f, FontStyle.Bold),
            new Rectangle(rx, ry, rw, 26), Theme.TextMain, TextFormatFlags.Left); ry += 34;
        TextRenderer.DrawText(g, "Enter remote ID", Theme.F(8.5f, FontStyle.Bold),
            new Rectangle(rx, ry, rw, 18), Theme.TextDim, TextFormatFlags.Left); ry += 22;

        _idFieldRect = new Rectangle(rx, ry, rw, 48);
        Theme.FillRound(g, _idFieldRect, 10, Color.White);
        using (var pen = new Pen(Theme.Accent, 1.4f)) { using var p = Theme.Round(_idFieldRect, 10); g.DrawPath(pen, p); }
        _remoteId.Location = new Point(_idFieldRect.X + 14, _idFieldRect.Y + (_idFieldRect.Height - _remoteId.Height) / 2);
        _remoteId.Width = _idFieldRect.Width - 28;
        ry += 60;

        _connectBtn.SetBounds(rx, ry, (rw - 12) / 2, 44);
        _transferBtn.SetBounds(rx + (rw - 12) / 2 + 12, ry, (rw - 12) / 2, 44);
        ry += 60;

        TextRenderer.DrawText(g, "Recent", Theme.F(9.5f, FontStyle.Bold),
            new Rectangle(rx, ry, rw, 20), Theme.TextMain, TextFormatFlags.Left); ry += 26;
        // mini recent list painted inline
        var recents = _cfg.Peers.OrderByDescending(p => p.LastConnected).Take(4).ToList();
        if (recents.Count == 0)
            TextRenderer.DrawText(g, "No recent sessions yet.", Theme.F(9f),
                new Rectangle(rx, ry, rw, 22), Theme.TextDim, TextFormatFlags.Left);
        foreach (var p in recents)
        {
            var row = new Rectangle(rx, ry, rw, 36);
            Theme.FillRound(g, row, 8, Color.FromArgb(247, 249, 251));
            TextRenderer.DrawText(g, PlatGlyph(p.Platform), IconFont(11f),
                new Rectangle(row.X + 10, row.Y, 22, row.Height), Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, string.IsNullOrEmpty(p.Alias) ? FormatId(p.Id) : p.Alias,
                Theme.F(9.5f, FontStyle.Bold), new Rectangle(row.X + 38, row.Y, rw - 150, row.Height),
                Theme.TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, p.Favorite ? "" : "", IconFont(9f),
                new Rectangle(row.Right - 30, row.Y, 22, row.Height), Color.Goldenrod, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            ry += 42;
        }
    }

    private void DoLayout()
    {
        if (_home == null) return;
        var area = new Rectangle(RailW, 0, ClientSize.Width - RailW, ClientSize.Height - _statusLabel.Height);
        foreach (Control c in new Control[] { _home, _peers, _settingsHost })
            c.Bounds = area;

        int pad = 28, gap = 24;
        int innerW = area.Width - pad * 2 - gap;
        int leftW = Math.Max(330, (int)(innerW * 0.42));
        int rightW = innerW - leftW;
        int top = pad + 30;
        int h = area.Height - top - pad;
        _leftCard = new Rectangle(pad, top, leftW, Math.Min(h, 430));
        _rightCard = new Rectangle(pad + leftW + gap, top, rightW, h);
        _home.Invalidate();

        if (_peerList != null) _peerList.Width = area.Width - 56;
    }

    // ---------------- peers tab ----------------
    private void BuildPeers()
    {
        _peers = new DoubleBufferedPanel { BackColor = Theme.Canvas, Visible = false };
        var title = new Label { Text = "Recent & Favourites", Font = Theme.F(14f, FontStyle.Bold),
            ForeColor = Theme.TextMain, AutoSize = true, Location = new Point(28, 24), BackColor = Color.Transparent };
        _peers.Controls.Add(title);
        _peerList = new FlowLayoutPanel { Location = new Point(28, 64), AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Canvas };
        _peers.Controls.Add(_peerList);
        _peers.Resize += (_, _) =>
        {
            _peerList.Size = new Size(_peers.Width - 56, _peers.Height - 88);
        };
    }

    private void PopulatePeers()
    {
        _peerList.Controls.Clear();
        var list = _cfg.Peers.OrderByDescending(p => p.Favorite).ThenByDescending(p => p.LastConnected).ToList();
        if (list.Count == 0)
        {
            _peerList.Controls.Add(new Label { Text = "No peers yet. Connect to a remote ID to add one here.",
                ForeColor = Theme.TextDim, Font = Theme.F(10f), AutoSize = true, Margin = new Padding(4, 8, 0, 0) });
            return;
        }
        foreach (var p in list)
        {
            var row = new PeerRow(p, IconFont);
            row.Width = _peerList.Width - 24;
            row.OnConnect = () => { _remoteId.Text = p.Id; ShowNav(Nav.Home); DoConnect(false, p.Host, p.Id); };
            row.OnToggleFav = () => { p.Favorite = !p.Favorite; _cfg.Save(); PopulatePeers(); };
            row.OnRemove = () => { _cfg.Peers.Remove(p); _cfg.Save(); PopulatePeers(); };
            _peerList.Controls.Add(row);
        }
    }

    // ---------------- settings tab ----------------
    private void BuildSettingsHost()
    {
        _settingsHost = new DoubleBufferedPanel { BackColor = Theme.Canvas, Visible = false, AutoScroll = true };
    }
    private bool _settingsBuilt;

    /// <summary>Rebuild the Settings page so it shows values changed outside it.</summary>
    private void RefreshSettingsPage()
    {
        if (!_settingsBuilt) return;
        foreach (Control c in _settingsHost.Controls.Cast<Control>().ToList()) { _settingsHost.Controls.Remove(c); c.Dispose(); }
        _settingsBuilt = false;
        if (_nav == Nav.Settings) BuildSettingsContent();
    }

    private void BuildSettingsContent()
    {
        if (_settingsBuilt) return;
        _settingsBuilt = true;
        var panel = new SettingsPanel(_cfg, IconFont) { Location = new Point(28, 20) };
        panel.Changed = () => { _host.Quality = QualityNumber(); _host.Fps = _cfg.Fps(); _home.Invalidate(); };
        _settingsHost.Controls.Add(panel);
        _settingsPanel = panel;
        if (!_settingsResizeHooked)
        {
            _settingsResizeHooked = true;
            _settingsHost.Resize += (_, _) => { if (_settingsPanel != null && !_settingsPanel.IsDisposed) _settingsPanel.Width = Math.Max(480, _settingsHost.Width - 80); };
        }
        panel.Width = Math.Max(480, _settingsHost.Width - 80);
    }
    private SettingsPanel _settingsPanel;
    private bool _settingsResizeHooked;

    // ---------------- connect ----------------
    private void DoConnect(bool fileMode, string hostOverride = null, string idOverride = null)
    {
        string id = (idOverride ?? _remoteId.Text).Trim();
        if (string.IsNullOrWhiteSpace(id)) { SetStatus("Enter a remote ID or address first."); return; }
        if (string.IsNullOrEmpty(hostOverride)) hostOverride = null;

        // An ID (no dots or colons) goes through the ID server when one is set;
        // an IP address or host name ("ip" or "ip:port") connects directly.
        string compact = id.Replace(" ", "");
        bool looksLikeAddress = compact.Contains('.') || compact.Contains(':');
        if (hostOverride == null && !looksLikeAddress)
        {
            if (Rv.IdServerOf(_cfg) == "")
            {
                SetStatus("To connect by ID, set your ID server in Settings ▸ Network (or type the computer's IP address).");
                return;
            }
            var viaServer = new SessionForm(_cfg, "", 0, fileMode, compact);
            viaServer.PeerSaved = (h, pid) => RememberPeer(pid, h);
            viaServer.Show(this);
            return;
        }

        string host = hostOverride ?? compact;
        int port = _cfg.ListenPort();
        if (host.Contains(':'))
        {
            var parts = host.Split(':');
            host = parts[0];
            if (int.TryParse(parts[1], out var pp)) port = pp;
        }

        var sess = new SessionForm(_cfg, host, port, fileMode);
        sess.PeerSaved = (h, pid) => RememberPeer(pid, h);
        sess.Show(this);
    }

    private void RememberPeer(string id, string host)
    {
        var p = _cfg.Peers.FirstOrDefault(x => x.Id == id);
        if (p == null) { p = new Peer { Id = id, Host = host, Platform = "win" }; _cfg.Peers.Add(p); }
        p.Host = host;
        p.LastConnected = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        _cfg.Save();
        OnUi(() => { if (_nav == Nav.Home) _home.Invalidate(); });
    }

    // ---------------- helpers ----------------
    private bool ApproveOnUi(string peerId, CancellationToken cancel)
    {
        bool ok = false;
        try
        {
            if (InvokeRequired)
                Invoke(new Action(() => ok = AcceptDialog(peerId, cancel)));
            else ok = AcceptDialog(peerId, cancel);
        }
        catch { ok = false; }   // never let someone in because the window couldn't open
        return ok;
    }

    private bool AcceptDialog(string peerId, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested) return false;   // their password was already accepted
        using var dlg = new AcceptForm(peerId);
        // If the visitor's password is accepted first, the window closes by itself.
        using var reg = cancel.Register(() => OnUi(() =>
        {
            if (!dlg.IsDisposed && dlg.Visible) { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); }
        }));
        return dlg.ShowDialog(this) == DialogResult.Yes;
    }

    private void SetStatus(string s) { if (_statusLabel != null) _statusLabel.Text = "  " + s; }
    private void OnUi(Action a) { try { if (IsHandleCreated) BeginInvoke(a); } catch { } }

    private int QualityNumber() => _cfg.JpegQuality();

    private static void CopyTo(string s) { try { if (!string.IsNullOrEmpty(s)) Clipboard.SetText(s); } catch { } }
    private static string FormatId(string id) => Config.FormatId(id);
    private static string PlatGlyph(string p) => p switch { "mac" => "", "linux" => "", "android" => "", _ => "" };
    private Font IconFont(float sz) => new(FontAvail("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", sz);

    protected override void OnFormClosing(FormClosingEventArgs e) { _linkTimer?.Stop(); _link?.Stop(); _host.Stop(); _cfg.Save(); base.OnFormClosing(e); }
}

internal class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }
}
