using System.Drawing;
using System.Windows.Forms;

namespace Rplus;

/// <summary>A simple rounded, flat button with a filled (primary) or outline style.</summary>
internal sealed class FlatButton : Control, IButtonControl
{
    public bool Primary { get; set; } = true;
    public Color Fill { get; set; } = Theme.Accent;
    public int Radius { get; set; } = 8;
    private bool _hover, _down;

    public DialogResult DialogResult { get; set; } = DialogResult.None;
    public void NotifyDefault(bool value) { }
    public void PerformClick() => OnClick(EventArgs.Empty);

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Font = Theme.F(10f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        Height = 40;
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill = Fill;
        if (_down) fill = Blend(fill, Color.Black, 0.15f);
        else if (_hover) fill = Blend(fill, Color.White, 0.08f);

        if (Primary)
        {
            Theme.FillRound(g, r, Radius, fill);
            TextRenderer.DrawText(g, Text, Font, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            using var p = Theme.Round(r, Radius);
            using var pen = new Pen(_hover ? Fill : Theme.CardBorder, 1.4f);
            if (_hover) { using var hb = new SolidBrush(Blend(Fill, Theme.Card, 0.92f)); g.FillPath(hb, p); }
            g.DrawPath(pen, p);
            TextRenderer.DrawText(g, Text, Font, r, Fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));
}
