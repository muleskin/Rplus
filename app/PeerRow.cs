using System.Drawing;
using System.Windows.Forms;

namespace Rplus;

/// <summary>A single peer line in the Recent/Favourites list.</summary>
internal sealed class PeerRow : Control
{
    private readonly Peer _p;
    private readonly Func<float, Font> _icon;
    public Action OnConnect, OnToggleFav, OnRemove;
    private Rectangle _favR, _delR, _connR;
    private int _hot = -1;

    public PeerRow(Peer p, Func<float, Font> iconFont)
    {
        _p = p; _icon = iconFont;
        Height = 58; Margin = new Padding(0, 0, 0, 10);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Canvas;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Canvas);
        var card = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.Card3(g, card, 10);

        TextRenderer.DrawText(g, Plat(), _icon(16f), new Rectangle(16, 0, 30, Height),
            Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        string title = string.IsNullOrEmpty(_p.Alias) ? Fmt(_p.Id) : _p.Alias;
        TextRenderer.DrawText(g, title, Theme.F(11f, FontStyle.Bold),
            new Rectangle(54, 10, Width - 250, 22), Theme.TextMain, TextFormatFlags.Left);
        // address (or the ID when the title is an alias and there's no address), then the last time used
        string where = !string.IsNullOrEmpty(_p.Host) ? _p.Host : string.IsNullOrEmpty(_p.Alias) ? "" : Fmt(_p.Id);
        string sub = string.Join("   •   ", new[] { where, _p.LastConnected ?? "" }.Where(s => s.Length > 0));
        TextRenderer.DrawText(g, sub, Theme.F(8.5f),
            new Rectangle(54, 31, Width - 250, 18), Theme.TextDim, TextFormatFlags.Left);

        _connR = new Rectangle(Width - 230, 12, 92, 34);
        Theme.FillRound(g, _connR, 8, _hot == 0 ? Theme.AccentDark : Theme.Accent);
        TextRenderer.DrawText(g, "Connect", Theme.F(9f, FontStyle.Bold), _connR, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        _favR = new Rectangle(Width - 120, 12, 44, 34);
        if (_hot == 1) Theme.FillRound(g, _favR, 8, Color.FromArgb(245, 247, 250));
        TextRenderer.DrawText(g, _p.Favorite ? "" : "", _icon(13f), _favR,
            _p.Favorite ? Color.Goldenrod : Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        _delR = new Rectangle(Width - 66, 12, 44, 34);
        if (_hot == 2) Theme.FillRound(g, _delR, 8, Color.FromArgb(252, 240, 241));
        TextRenderer.DrawText(g, "", _icon(12f), _delR,
            _hot == 2 ? Theme.Danger : Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        if (_connR.Contains(e.Location)) h = 0;
        else if (_favR.Contains(e.Location)) h = 1;
        else if (_delR.Contains(e.Location)) h = 2;
        if (h != _hot) { _hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (_connR.Contains(e.Location)) OnConnect?.Invoke();
        else if (_favR.Contains(e.Location)) OnToggleFav?.Invoke();
        else if (_delR.Contains(e.Location)) OnRemove?.Invoke();
        base.OnMouseClick(e);
    }

    private string Plat() => _p.Platform switch { "mac" => "", "android" => "", _ => "" };
    private static string Fmt(string id) => (id?.Length == 9) ? $"{id[..3]} {id.Substring(3,3)} {id.Substring(6,3)}" : id;
}

/// <summary>The incoming-connection accept prompt (approve-mode "click"/"both").</summary>
internal sealed class AcceptForm : Form
{
    public AcceptForm(string peerId)
    {
        Text = "Rplus";
        Icon = Theme.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(360, 170);
        BackColor = Theme.Card;
        Font = Theme.F(10f);

        var l1 = new Label { Text = "Incoming connection", Font = Theme.F(12f, FontStyle.Bold),
            ForeColor = Theme.TextMain, AutoSize = true, Location = new Point(24, 22) };
        string who = string.IsNullOrEmpty(peerId) ? "A remote device" : Theme.PrettyId(peerId);
        var l2 = new Label { Text = $"{who} wants to control this computer.",
            Font = Theme.F(9.5f), ForeColor = Theme.TextDim, AutoSize = false,
            Location = new Point(24, 54), Size = new Size(312, 40) };
        var accept = new FlatButton { Text = "Accept", Primary = true, Fill = Theme.Ok,
            Location = new Point(188, 112), Size = new Size(148, 40) };
        var deny = new FlatButton { Text = "Dismiss", Primary = false, Fill = Theme.Danger,
            Location = new Point(24, 112), Size = new Size(148, 40) };
        accept.Click += (_, _) => { DialogResult = DialogResult.Yes; Close(); };
        deny.Click += (_, _) => { DialogResult = DialogResult.No; Close(); };
        Controls.AddRange(new Control[] { l1, l2, accept, deny });
        AcceptButton = accept;
    }
}
