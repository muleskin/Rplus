using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Rplus;

/// <summary>Central palette and small drawing helpers so every screen looks the same.</summary>
internal static class Theme
{
    // Own palette (not copied artwork): a clean blue accent on a light-grey canvas.
    // The brand colours are the same in light and dark mode.
    public static readonly Color Accent      = Color.FromArgb(0, 113, 255);   // primary blue
    public static readonly Color AccentDark  = Color.FromArgb(0, 92, 214);
    public static readonly Color Ok          = Color.FromArgb(40, 167, 69);
    public static readonly Color Danger      = Color.FromArgb(220, 53, 69);
    public static readonly Color RailText    = Color.FromArgb(205, 212, 220);

    // ---------------- light / dark ----------------

    /// <summary>True while Rplus is drawn dark. Follows the Windows app mode unless --theme forces one.</summary>
    public static bool Dark { get; private set; }
    private static bool _forced;

    private enum T { Canvas, Card, CardBorder, TextMain, TextDim, Rail, RailActive, RailHover, Field, Input, Row, StatusBar, DangerSoft, Track }
    private static readonly Color[] LightPal =
    {
        Color.FromArgb(245, 247, 250), Color.White, Color.FromArgb(226, 230, 236), Color.FromArgb(32, 38, 46),
        Color.FromArgb(128, 138, 150), Color.FromArgb(27, 34, 43), Color.FromArgb(38, 48, 60), Color.FromArgb(33, 41, 52),
        Color.FromArgb(245, 247, 250), Color.White, Color.FromArgb(247, 249, 251), Color.FromArgb(237, 240, 244),
        Color.FromArgb(252, 240, 241), Color.FromArgb(232, 236, 242),
    };
    private static readonly Color[] DarkPal =
    {
        Color.FromArgb(25, 28, 33), Color.FromArgb(35, 39, 46), Color.FromArgb(54, 60, 70), Color.FromArgb(229, 233, 238),
        Color.FromArgb(148, 158, 170), Color.FromArgb(16, 19, 24), Color.FromArgb(38, 46, 58), Color.FromArgb(29, 35, 44),
        Color.FromArgb(29, 33, 39), Color.FromArgb(24, 27, 32), Color.FromArgb(43, 48, 56), Color.FromArgb(19, 22, 27),
        Color.FromArgb(74, 38, 44), Color.FromArgb(54, 60, 70),
    };
    private static Color P(T t) => (Dark ? DarkPal : LightPal)[(int)t];

    public static Color Canvas     => P(T.Canvas);
    public static Color Card       => P(T.Card);
    public static Color CardBorder => P(T.CardBorder);
    public static Color TextMain   => P(T.TextMain);
    public static Color TextDim    => P(T.TextDim);
    public static Color Rail       => P(T.Rail);        // side rail (dark in both modes)
    public static Color RailActive => P(T.RailActive);
    public static Color RailHover  => P(T.RailHover);
    public static Color Field      => P(T.Field);       // read-only fields inside cards (the password)
    public static Color Input      => P(T.Input);       // text boxes, lists and drop-downs
    public static Color Row        => P(T.Row);         // list rows painted inside cards
    public static Color StatusBar  => P(T.StatusBar);
    public static Color DangerSoft => P(T.DangerSoft);  // hover behind a delete button
    public static Color Track      => P(T.Track);       // empty part of a progress bar

    /// <summary>Picks light or dark: "--theme dark|light" wins, else the Windows app mode. Call once at start.</summary>
    public static void Init(string[] args)
    {
        int i = Array.IndexOf(args, "--theme");
        if (i >= 0 && i + 1 < args.Length && (args[i + 1] == "dark" || args[i + 1] == "light"))
        {
            _forced = true; Dark = args[i + 1] == "dark";
            return;
        }
        Dark = WindowsIsDark();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            var any = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.IsHandleCreated);
            if (any == null) { Dark = WindowsIsDark(); return; }
            any.BeginInvoke(() => Switch(WindowsIsDark()));
        };
    }

    private static bool WindowsIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Windows changed between light and dark: recolour every open window in place.</summary>
    private static void Switch(bool dark)
    {
        if (_forced || dark == Dark) return;
        var from = Dark ? DarkPal : LightPal;
        Dark = dark;
        foreach (Form f in Application.OpenForms)
        {
            f.ForeColor = TextMain;
            Recolor(f, from);
            TitleBar(f);
            f.Invalidate(true);
        }
    }

    /// <summary>Makes a window follow the theme: dark title bar, and its controls (now and later) recoloured.
    /// Call at the end of each form's constructor.</summary>
    public static void Attach(Form f)
    {
        f.ForeColor = TextMain;
        f.HandleCreated += (_, _) => TitleBar(f);
        if (f.IsHandleCreated) TitleBar(f);
        Watch(f);
        if (Dark) Recolor(f, LightPal);
    }

    // controls added later (pages built on demand, peer rows, file lists) get the same treatment
    private static void Watch(Control c)
    {
        c.ControlAdded += (_, e) => { if (Dark) Recolor(e.Control, LightPal); Watch(e.Control); };
        foreach (Control k in c.Controls) Watch(k);
    }

    private static readonly T[] BackTokens = { T.Canvas, T.Card, T.StatusBar };
    private static readonly T[] ForeTokens = { T.TextMain, T.TextDim };

    /// <summary>Swaps colours that came from the old palette for the current one, through the whole tree.</summary>
    private static void Recolor(Control c, Color[] from)
    {
        var to = Dark ? DarkPal : LightPal;
        bool input = c is TextBoxBase or ComboBox or ListView or ListBox;
        if (c is RichTextBox { ReadOnly: true, BorderStyle: BorderStyle.None }) input = false;   // the chat log keeps its own colours
        if (input)
        {
            int bk = c.BackColor.ToArgb();
            if (bk == Color.White.ToArgb() || bk == SystemColors.Window.ToArgb() || bk == SystemColors.Control.ToArgb()
                || bk == from[(int)T.Input].ToArgb())
            {
                c.BackColor = to[(int)T.Input];
                c.ForeColor = to[(int)T.TextMain];
            }
        }
        else if (c is TrackBar && c.Parent != null) c.BackColor = c.Parent.BackColor;
        else
        {
            foreach (var t in BackTokens)
                if (c.BackColor.ToArgb() == from[(int)t].ToArgb()) { c.BackColor = to[(int)t]; break; }
        }
        foreach (var t in ForeTokens)
            if (c.ForeColor.ToArgb() == from[(int)t].ToArgb()) { c.ForeColor = to[(int)t]; break; }
        NativeTheme(c);
        foreach (Control k in c.Controls) Recolor(k, from);
    }

    // ---------------- Windows' own parts: title bar, scroll bars, list headers ----------------

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    private static void TitleBar(Form f)
    {
        if (!f.IsHandleCreated) return;
        int on = Dark ? 1 : 0;
        try { DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { }   // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    private static readonly HashSet<ComboBox> FlatCombos = new();

    /// <summary>Draws a details list's column headers in the theme's colours (Windows keeps them light otherwise).</summary>
    public static void ThemedHeaders(ListView lv)
    {
        lv.OwnerDraw = true;
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Input)) g.FillRectangle(b, e.Bounds);
            using (var pen = new Pen(CardBorder))
            {
                g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                g.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            }
            var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height);
            TextRenderer.DrawText(g, e.Header.Text, e.Font, r, TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }

    private static void NativeTheme(Control c)
    {
        if (c is not (ListView or TextBoxBase or ScrollableControl or ComboBox)) return;
        if (c is ScrollableControl sc && !sc.AutoScroll) return;
        if (!c.IsHandleCreated) { c.HandleCreated += (_, _) => NativeTheme(c); return; }
        try
        {
            if (c is ComboBox cb)
            {
                // a flat drop-down paints its own white frame; the standard one takes the dark theme
                if (Dark && cb.FlatStyle == FlatStyle.Flat) { cb.FlatStyle = FlatStyle.Standard; FlatCombos.Add(cb); }
                else if (!Dark && FlatCombos.Remove(cb)) cb.FlatStyle = FlatStyle.Flat;
                SetWindowTheme(c.Handle, Dark ? "DarkMode_CFD" : null, null);
                return;
            }
            if (c is TextBox) { SetWindowTheme(c.Handle, Dark ? "DarkMode_CFD" : null, null); return; }
            SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
            if (c is ListView)
            {
                IntPtr header = SendMessage(c.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
                if (header != IntPtr.Zero) SetWindowTheme(header, Dark ? "DarkMode_ItemsView" : "ItemsView", null);
            }
        }
        catch { }
    }

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

    /// <summary>A 9-digit ID spaced as "123 456 789"; anything else (an address) is returned unchanged.</summary>
    public static string PrettyId(string id) =>
        id != null && id.Length == 9 && id.All(char.IsDigit) ? $"{id[..3]} {id.Substring(3, 3)} {id.Substring(6, 3)}" : id;

    /// <summary>The R+ app icon, embedded as a resource.</summary>
    public static Icon AppIcon()
    {
        using var s = typeof(Theme).Assembly.GetManifestResourceStream("rplus.ico")!;
        return new Icon(s);
    }

    /// <summary>The R+ app icon at a given pixel size (the in-app logo).</summary>
    public static Icon AppIcon(int size)
    {
        using var s = typeof(Theme).Assembly.GetManifestResourceStream("rplus.ico")!;
        return new Icon(s, size, size);
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
