using System.Drawing;
using System.Drawing.Drawing2D;

namespace Rplus;

/// <summary>Central palette and small drawing helpers so every screen looks the same.</summary>
internal static class Theme
{
    // Own palette (not copied artwork): a clean blue accent on a light-grey canvas.
    public static readonly Color Accent      = Color.FromArgb(0, 113, 255);   // primary blue
    public static readonly Color AccentDark  = Color.FromArgb(0, 92, 214);
    public static readonly Color Ok          = Color.FromArgb(40, 167, 69);
    public static readonly Color Danger      = Color.FromArgb(220, 53, 69);
    public static readonly Color Canvas      = Color.FromArgb(245, 247, 250);
    public static readonly Color Card        = Color.White;
    public static readonly Color CardBorder  = Color.FromArgb(226, 230, 236);
    public static readonly Color TextMain    = Color.FromArgb(32, 38, 46);
    public static readonly Color TextDim     = Color.FromArgb(128, 138, 150);
    public static readonly Color Rail        = Color.FromArgb(27, 34, 43);     // dark side rail
    public static readonly Color RailText    = Color.FromArgb(205, 212, 220);

    public static readonly string FontName = "Segoe UI";

    public static Font F(float size, FontStyle style = FontStyle.Regular) => new(FontName, size, style);

    /// <summary>Round-rectangle path for cards and buttons.</summary>
    public static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); p.CloseFigure(); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The R+ app icon, embedded as a resource.</summary>
    public static Icon AppIcon()
    {
        using var s = typeof(Theme).Assembly.GetManifestResourceStream("rplus.ico")!;
        return new Icon(s);
    }

    public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void Card3(Graphics g, Rectangle r, int radius = 12)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = Round(r, radius);
        using var b = new SolidBrush(Card);
        using var pen = new Pen(CardBorder, 1);
        g.FillPath(b, p);
        g.DrawPath(pen, p);
    }
}
