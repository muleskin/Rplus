using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Rplus;

/// <summary>Renders the remote screen frames and forwards mouse/keyboard to the host.</summary>
internal sealed class RemoteScreen : Control
{
    private readonly Config _cfg;
    private Bitmap _frame;
    private readonly object _lock = new();
    public bool Fit = true;
    public bool ForwardKeys = true;
    public Client Send;
    private Rectangle _dest;

    public RemoteScreen(Config cfg)
    {
        _cfg = cfg;
        Fit = cfg.ViewStyle != "Original";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.FromArgb(18, 22, 28);
    }

    public void SetFrame(Bitmap bmp)
    {
        lock (_lock) { _frame?.Dispose(); _frame = bmp; }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Bitmap f;
        lock (_lock) f = _frame;
        if (f == null)
        {
            TextRenderer.DrawText(g, "Waiting for the remote screen…", Theme.F(11f),
                ClientRectangle, Theme.RailText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        _dest = DestRect(f.Size);
        try { g.DrawImage(f, _dest); } catch { }
    }

    private Rectangle DestRect(Size img)
    {
        if (!Fit)
        {
            // Original size, centred.
            int x = (Width - img.Width) / 2, y = (Height - img.Height) / 2;
            return new Rectangle(x, y, img.Width, img.Height);
        }
        double s = Math.Min((double)Width / img.Width, (double)Height / img.Height);
        int w = (int)(img.Width * s), h = (int)(img.Height * s);
        return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
    }

    private bool Map(Point p, out double nx, out double ny)
    {
        nx = ny = 0;
        if (_dest.Width <= 0 || _dest.Height <= 0) return false;
        nx = Math.Clamp((double)(p.X - _dest.X) / _dest.Width, 0, 1);
        ny = Math.Clamp((double)(p.Y - _dest.Y) / _dest.Height, 0, 1);
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (Map(e.Location, out var x, out var y)) Send?.MouseDown(Btn(e.Button), x, y);
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (Map(e.Location, out var x, out var y)) Send?.MouseUp(Btn(e.Button), x, y);
        base.OnMouseUp(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Map(e.Location, out var x, out var y)) Send?.MouseMove(x, y);
        base.OnMouseMove(e);
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Send?.Wheel(e.Delta);
        base.OnMouseWheel(e);
    }
    protected override bool IsInputKey(Keys k) => ForwardKeys || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ForwardKeys) { Send?.KeyEv((int)e.KeyCode, true); e.Handled = true; e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (ForwardKeys) { Send?.KeyEv((int)e.KeyCode, false); e.Handled = true; }
        base.OnKeyUp(e);
    }

    private static int Btn(MouseButtons b) => b switch { MouseButtons.Right => 1, MouseButtons.Middle => 2, _ => 0 };
}

/// <summary>Collapsible chat column for the session window.</summary>
internal sealed class ChatPanel : Panel
{
    private readonly RichTextBox _log;
    private readonly TextBox _input;
    public Action<string> OnSend;

    public ChatPanel()
    {
        BackColor = Color.FromArgb(32, 39, 48);
        var header = new Label { Text = "  Chat", Dock = DockStyle.Top, Height = 34, ForeColor = Color.White,
            Font = Theme.F(10f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.FromArgb(27, 34, 43) };
        _log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(32, 39, 48), ForeColor = Theme.RailText, Font = Theme.F(9.5f) };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.FromArgb(27, 34, 43), Padding = new Padding(8, 8, 8, 8) };
        _input = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Font = Theme.F(9.5f),
            Margin = new Padding(6) };
        _input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Fire(); } };
        var send = new FlatButton { Text = "Send", Primary = true, Dock = DockStyle.Right, Width = 64 };
        send.Click += (_, _) => Fire();
        var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 3, 8, 0) };   // centres the box against the button
        wrap.Controls.Add(_input);
        bottom.Controls.Add(wrap);
        bottom.Controls.Add(send);
        // the log sits in a padded holder so text doesn't touch the panel's edges
        var logHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 8, 6), BackColor = Color.FromArgb(32, 39, 48) };
        logHolder.Controls.Add(_log);
        Controls.Add(logHolder);
        Controls.Add(bottom);
        Controls.Add(header);
    }

    private static readonly Font _gapFont = new("Segoe UI", 4f);

    /// <summary>Text waiting in the input box (used by the chat screenshot).</summary>
    public string Draft { set => _input.Text = value; }

    private void Fire()
    {
        var t = _input.Text.Trim();
        if (t.Length == 0) return;
        _input.Clear();
        OnSend?.Invoke(t);
    }

    public void Add(string who, string text)
    {
        _log.SelectionColor = who == "Me" ? Theme.Accent : Color.FromArgb(120, 200, 160);
        _log.AppendText($"{who}: ");
        _log.SelectionColor = Theme.RailText;
        _log.AppendText(text + Environment.NewLine);
        _log.SelectionFont = _gapFont;              // a short blank line between messages
        _log.AppendText(Environment.NewLine);
        _log.SelectionFont = _log.Font;
        _log.ScrollToCaret();
    }
}

/// <summary>Two-pane file transfer: local computer on the left, remote on the right.</summary>
internal sealed class FilePanel : Panel
{
    private readonly Config _cfg;
    private Client _client;
    private ListView _local, _remote;
    private TextBox _localPath, _remotePath;
    private string _localDir = "", _remoteDir = "";

    public FilePanel(Config cfg)
    {
        _cfg = cfg;
        BackColor = Theme.Canvas;
        BuildSide(true);
        BuildSide(false);
        var mid = new Panel { Dock = DockStyle.Left, Width = 60, BackColor = Theme.Canvas };
        var up = new FlatButton { Text = "→", Primary = true, Size = new Size(44, 36), Location = new Point(8, 120) };
        var down = new FlatButton { Text = "←", Primary = false, Size = new Size(44, 36), Location = new Point(8, 164) };
        up.Click += (_, _) => Upload();
        down.Click += (_, _) => Download();
        mid.Controls.Add(up); mid.Controls.Add(down);
        // order: remote (fill), mid, local(left) — add local first (left), then mid(left), then remote(fill)
        Controls.Add(_remotePanel);
        Controls.Add(mid);
        Controls.Add(_localPanel);
        _bar = new TransferBar { Dock = DockStyle.Bottom, Visible = false };
        _bar.CancelClicked = () => { if (_activeId > 0) _client?.CancelTransfer(_activeId); };
        Controls.Add(_bar);
        _bar.SendToBack();   // docked first, so it takes the full width under both panes
    }

    private TransferBar _bar;
    private int _activeId;                       // the transfer in progress (one at a time), 0 = none
    private DateTime _activeStart;
    private readonly System.Windows.Forms.Timer _hideBar = new() { Interval = 5000 };

    /// <summary>Progress and the end of a transfer, on the UI thread.</summary>
    public void TransferUpdate(Client.Transfer x)
    {
        if (x.Id != _activeId) return;
        _hideBar.Stop();
        _bar.Visible = true;
        if (!x.Finished)
        {
            double secs = Math.Max(0.001, (DateTime.Now - _activeStart).TotalSeconds);
            _bar.Show(x.Upload ? "Sending" : "Receiving", x.Name, x.Done, x.Total, x.Done / secs, running: true);
            return;
        }
        _activeId = 0;
        string msg = x.Ok ? (x.Upload ? $"Sent {x.Name}" : $"Saved {x.Name}")
                   : x.Error == "Cancelled" ? $"Cancelled {x.Name}" : $"{x.Name}: {x.Error}";
        _bar.Done(msg, x.Ok);
        if (x.Ok) { if (x.Upload) _client?.RequestFileList(_remoteDir); else LoadLocal(_localDir); }
        _hideBar.Tick -= HideBar; _hideBar.Tick += HideBar;
        _hideBar.Start();
    }
    private void HideBar(object s, EventArgs e) { _hideBar.Stop(); if (_activeId == 0) _bar.Visible = false; }

    private bool Busy()
    {
        if (_activeId == 0) return false;
        _bar.Flash();
        return true;
    }

    /// <summary>Demo progress for --session-screenshot --files --progress.</summary>
    public void ShowDemoProgress(string name, long done, long total, double bytesPerSec)
    {
        _bar.Visible = true;
        _bar.Show("Sending", name, done, total, bytesPerSec, running: true);
    }

    private Panel _localPanel, _remotePanel;

    private void BuildSide(bool local)
    {
        var panel = new Panel { Width = 420, BackColor = Theme.Canvas, Padding = new Padding(8) };
        panel.Dock = local ? DockStyle.Left : DockStyle.Fill;
        var title = new Label { Text = local ? "Local computer" : "Remote computer", Dock = DockStyle.Top, Height = 26,
            Font = Theme.F(10f, FontStyle.Bold), ForeColor = Theme.TextMain };
        var pathBar = new Panel { Dock = DockStyle.Top, Height = 34 };
        var pathBox = new TextBox { Dock = DockStyle.Fill, Font = Theme.F(9f), ReadOnly = true };
        var upBtn = new FlatButton { Text = "Up", Primary = false, Dock = DockStyle.Right, Width = 56 };
        pathBar.Controls.Add(pathBox); pathBar.Controls.Add(upBtn);
        var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            Font = Theme.F(9f), BorderStyle = BorderStyle.FixedSingle };
        lv.Columns.Add("Name", 230);
        lv.Columns.Add("Size", 80);
        lv.Columns.Add("Modified", 120);
        lv.SmallImageList = ListIcons();
        // Name takes whatever width is left, so the pane never needs a horizontal scrollbar.
        void FitColumns()
        {
            int rest = lv.ClientSize.Width - lv.Columns[1].Width - lv.Columns[2].Width;
            if (rest > 80) lv.Columns[0].Width = rest;
        }
        lv.ClientSizeChanged += (_, _) => FitColumns();
        FitColumns();
        Theme.ThemedHeaders(lv);
        panel.Controls.Add(lv);
        panel.Controls.Add(pathBar);
        panel.Controls.Add(title);

        if (local)
        {
            _local = lv; _localPath = pathBox; _localPanel = panel;
            lv.DoubleClick += (_, _) => LocalOpen();
            upBtn.Click += (_, _) => LocalUp();
        }
        else
        {
            _remote = lv; _remotePath = pathBox; _remotePanel = panel;
            lv.DoubleClick += (_, _) => RemoteOpen();
            upBtn.Click += (_, _) => RemoteUp();
        }
    }

    public void Attach(Client c) => _client = c;

    /// <summary>Made-up listings on both sides, for --session-screenshot --files (never reads the disk).</summary>
    public void ShowDemo(string localPath, List<Host.FileEntry> local, string remotePath, List<Host.FileEntry> remote)
    {
        _localDir = localPath; _localPath.Text = localPath; _local.Items.Clear();
        foreach (var e in local.OrderByDescending(x => x.isDir).ThenBy(x => x.name))
            AddItem(_local, e.name, e.isDir, e.size, e.modified);
        ShowRemote(remotePath, remote);
    }

    public void Start()
    {
        LoadLocal("");
        _client?.RequestFileList("");
    }

    // ---- local ----
    private void LoadLocal(string dir)
    {
        _localDir = dir;
        _localPath.Text = string.IsNullOrEmpty(dir) ? "This PC" : dir;
        _local.Items.Clear();
        try
        {
            if (string.IsNullOrEmpty(dir))
            {
                foreach (var d in DriveInfo.GetDrives())
                    AddItem(_local, d.Name, true, 0, "");
            }
            else
            {
                foreach (var d in Directory.GetDirectories(dir))
                    AddItem(_local, Path.GetFileName(d), true, 0, SafeT(d));
                foreach (var f in Directory.GetFiles(dir))
                {
                    var fi = new FileInfo(f);
                    AddItem(_local, fi.Name, false, fi.Length, fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                }
            }
        }
        catch { }
    }
    private static string SafeT(string d) { try { return Directory.GetLastWriteTime(d).ToString("yyyy-MM-dd HH:mm"); } catch { return ""; } }
    private void LocalOpen()
    {
        if (_local.SelectedItems.Count == 0) return;
        var it = _local.SelectedItems[0];
        if ((bool)it.Tag == false) return;
        string next = string.IsNullOrEmpty(_localDir) ? it.Text : Path.Combine(_localDir, it.Text);
        LoadLocal(next.TrimEnd('\\') + (next.EndsWith(":\\") ? "" : ""));
    }
    private void LocalUp()
    {
        if (string.IsNullOrEmpty(_localDir)) return;
        var parent = Directory.GetParent(_localDir.TrimEnd('\\'));
        LoadLocal(parent?.FullName ?? "");
    }

    // ---- remote ----
    public void ShowRemote(string path, List<Host.FileEntry> entries)
    {
        _remoteDir = path;
        _remotePath.Text = string.IsNullOrEmpty(path) ? "This PC" : path;
        _remote.Items.Clear();
        foreach (var e in entries.OrderByDescending(x => x.isDir).ThenBy(x => x.name))
            AddItem(_remote, e.name, e.isDir, e.size, e.modified);
    }
    private void RemoteOpen()
    {
        if (_remote.SelectedItems.Count == 0) return;
        var it = _remote.SelectedItems[0];
        if ((bool)it.Tag == false) return;
        string next = string.IsNullOrEmpty(_remoteDir) ? it.Text : Path.Combine(_remoteDir, it.Text);
        _client?.RequestFileList(next);
    }
    private void RemoteUp()
    {
        if (string.IsNullOrEmpty(_remoteDir)) return;
        var parent = Directory.GetParent(_remoteDir.TrimEnd('\\'));
        _client?.RequestFileList(parent?.FullName ?? "");
    }

    // ---- transfer ----
    private void Upload()   // local -> remote
    {
        if (_client == null || Busy()) return;
        if (_local.SelectedItems.Count == 0 || string.IsNullOrEmpty(_localDir)) return;
        var it = _local.SelectedItems[0];
        if ((bool)it.Tag) return; // only files
        if (string.IsNullOrEmpty(_remoteDir)) { _bar.Visible = true; _bar.Done("Open a folder on the remote computer first", false); return; }
        string path = Path.Combine(_localDir, it.Text);
        string dest = Path.Combine(_remoteDir, it.Text);
        _activeStart = DateTime.Now;
        _activeId = _client.Upload(path, dest);
    }
    private void Download() // remote -> local
    {
        if (_client == null || Busy()) return;
        if (_remote.SelectedItems.Count == 0 || string.IsNullOrEmpty(_remoteDir)) return;
        var it = _remote.SelectedItems[0];
        if ((bool)it.Tag) return;
        string dir = string.IsNullOrEmpty(_localDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : _localDir;
        _activeStart = DateTime.Now;
        _activeId = _client.Download(Path.Combine(_remoteDir, it.Text), dir);
    }

    private static ImageList _listIcons;
    /// <summary>Folder (0) and file (1) icons drawn with the system icon font.</summary>
    private static ImageList ListIcons()
    {
        if (_listIcons != null) return _listIcons;
        _listIcons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        string fontName;
        using (var probe = new Font("Segoe Fluent Icons", 9f))
            fontName = probe.Name == "Segoe Fluent Icons" ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        foreach (var (glyph, color) in new[] { ("", Color.FromArgb(230, 160, 40)), ("", Color.FromArgb(110, 120, 135)) })
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            using (var f = new Font(fontName, 12f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var br = new SolidBrush(color))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(glyph, f, br, new RectangleF(0, 0, 16, 16), sf);
            }
            _listIcons.Images.Add(bmp);
        }
        return _listIcons;
    }

    private static void AddItem(ListView lv, string name, bool dir, long size, string modified)
    {
        var it = new ListViewItem(name) { Tag = dir };
        it.SubItems.Add(dir ? "" : Human(size));
        it.SubItems.Add(modified);
        it.ImageIndex = dir ? 0 : 1;
        lv.Items.Add(it);
    }
    private static string Human(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}

/// <summary>The strip under the file panes: what's moving, how far, how fast, and Cancel.</summary>
internal sealed class TransferBar : Panel
{
    private string _line = "", _detail = "";
    private double _fraction;
    private bool _running, _ok = true;
    private readonly FlatButton _cancel;
    public Action CancelClicked;

    public TransferBar()
    {
        Height = 58;
        BackColor = Theme.Card;
        DoubleBuffered = true;
        Padding = new Padding(12, 0, 12, 0);
        _cancel = new FlatButton { Text = "Cancel", Primary = false, Size = new Size(84, 32) };
        _cancel.Click += (_, _) => CancelClicked?.Invoke();
        Controls.Add(_cancel);
        Resize += (_, _) => _cancel.Location = new Point(Width - _cancel.Width - 12, (Height - _cancel.Height) / 2);
    }

    public void Show(string verb, string name, long done, long total, double bytesPerSec, bool running)
    {
        _running = running; _ok = true;
        _fraction = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
        _line = $"{verb} {name}";
        _detail = $"{(int)Math.Round(_fraction * 100)}%  ·  {Size(done)} of {Size(total)}" +
                  (bytesPerSec > 0 && done > 0 ? $"  ·  {Size((long)bytesPerSec)}/s" : "");
        _cancel.Visible = true;
        Invalidate();
    }

    public void Done(string message, bool ok)
    {
        _running = false; _ok = ok;
        _line = message; _detail = "";
        _fraction = ok ? 1 : _fraction;
        _cancel.Visible = false;
        Invalidate();
    }

    /// <summary>A second transfer was asked for while one is running: draw attention to this one.</summary>
    public void Flash()
    {
        Visible = true;
        _detail = (_detail.Length > 0 ? _detail + "  ·  " : "") + "one transfer at a time";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var pen = new Pen(Theme.CardBorder)) g.DrawLine(pen, 0, 0, Width, 0);
        int right = (_cancel.Visible ? _cancel.Left : Width) - 16;
        int x = 12, w = Math.Max(40, right - x);
        TextRenderer.DrawText(g, _line, Theme.F(9.5f, FontStyle.Bold), new Rectangle(x, 8, w, 20),
            _ok ? Theme.TextMain : Theme.Danger, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (_detail.Length > 0)
            TextRenderer.DrawText(g, _detail, Theme.F(8.5f), new Rectangle(x, 8, w, 20), Theme.TextDim,
                TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        var track = new Rectangle(x, 34, w, 10);
        Theme.FillRound(g, track, 5, Theme.Track);
        if (_fraction > 0)
        {
            var fill = new Rectangle(x, 34, Math.Max(10, (int)(w * _fraction)), 10);
            Theme.FillRound(g, fill, 5, !_ok ? Theme.Danger : _running ? Theme.Accent : Theme.Ok);
        }
    }

    private static string Size(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.0} {u[i]}";
    }
}
