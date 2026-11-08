using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Rplus;

/// <summary>Screen capture, JPEG encode, and remote input injection via Win32.</summary>
internal static class Native
{
    // ---------- screen ----------
    public static Rectangle VirtualScreen()
    {
        int x = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        int y = GetSystemMetrics(77);
        int w = GetSystemMetrics(78);
        int h = GetSystemMetrics(79);
        if (w <= 0 || h <= 0) return new Rectangle(0, 0, 1280, 720);
        return new Rectangle(x, y, w, h);
    }

    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        try { g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy); }
        catch { /* leave blank on secure desktop */ }
        return bmp;
    }

    private static ImageCodecInfo _jpeg;
    private static ImageCodecInfo Jpeg()
    {
        if (_jpeg != null) return _jpeg;
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) { _jpeg = c; break; }
        return _jpeg;
    }

    public static byte[] EncodeJpeg(Bitmap bmp, int quality)
    {
        var ci = Jpeg();
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 10, 100));
        using var ms = new MemoryStream();
        if (ci != null) bmp.Save(ms, ci, ep);
        else bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    // ---------- input injection ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOVE = 0x0001, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;
    private const uint LDOWN = 0x0002, LUP = 0x0004, RDOWN = 0x0008, RUP = 0x0010,
                       MDOWN = 0x0020, MUP = 0x0040, WHEEL = 0x0800;
    private const uint KEYUP = 0x0002, UNICODE = 0x0004;

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] p, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);

    /// <summary>Move the pointer to absolute virtual-screen coords (0..65535 mapped).</summary>
    public static void MouseMove(double nx, double ny)
    {
        var inp = new[] { Mouse(MOVE | ABSOLUTE | VIRTUALDESK, Abs(nx), Abs(ny), 0) };
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    public static void MouseButton(int button, bool down, double nx, double ny)
    {
        uint flags = MOVE | ABSOLUTE | VIRTUALDESK;
        flags |= button switch
        {
            0 => down ? LDOWN : LUP,
            1 => down ? RDOWN : RUP,
            _ => down ? MDOWN : MUP,
        };
        var inp = new[] { Mouse(flags, Abs(nx), Abs(ny), 0) };
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    public static void MouseWheel(int delta)
    {
        var inp = new[] { Mouse(WHEEL, 0, 0, (uint)delta) };
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    public static void Key(ushort vk, bool down)
    {
        var ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : KEYUP };
        var inp = new[] { new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = ki } } };
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Type a unicode character (used for text/clipboard paste style input).</summary>
    public static void Char(char c)
    {
        var dn = new KEYBDINPUT { wScan = c, dwFlags = UNICODE };
        var up = new KEYBDINPUT { wScan = c, dwFlags = UNICODE | KEYUP };
        var inp = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = dn } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = up } },
        };
        SendInput(2, inp, Marshal.SizeOf<INPUT>());
    }

    private static int Abs(double n) => (int)Math.Round(Math.Clamp(n, 0, 1) * 65535);
    private static INPUT Mouse(uint flags, int dx, int dy, uint data) =>
        new() { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };
}
