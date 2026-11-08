using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Rplus;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
        Theme.Init(args);   // light or dark, following Windows (--theme dark|light to force one)

        string shot = null;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--screenshot" && i + 1 < args.Length) shot = args[i + 1];

        // --transfer-selftest <out.txt>: chunked file transfer between a local host and controller
        int xt = Array.IndexOf(args, "--transfer-selftest");
        if (xt >= 0 && xt + 1 < args.Length) { File.WriteAllText(args[xt + 1], TransferSelfTest()); return; }

        // --login-selftest <out.txt>: approve-mode rules on this PC only (no server).
        int lt = Array.IndexOf(args, "--login-selftest");
        if (lt >= 0 && lt + 1 < args.Length) { File.WriteAllText(args[lt + 1], LoginSelfTest()); return; }

        // --relay-selftest <idServer> <relayServer|-> <key|-> <out.txt>
        int st = Array.IndexOf(args, "--relay-selftest");
        if (st >= 0 && st + 4 < args.Length)
        {
            string N(string s) => s == "-" ? "" : s;
            File.WriteAllText(args[st + 4], RelaySelfTest(args[st + 1], N(args[st + 2]), N(args[st + 3])));
            return;
        }

        // --session-screenshot <out.png>: the session window with a made-up remote desktop (no connection)
        int ss = Array.IndexOf(args, "--session-screenshot");
        if (ss >= 0 && ss + 1 < args.Length) { RunSessionScreenshot(args[ss + 1], Array.IndexOf(args, "--files") >= 0 || Array.IndexOf(args, "--progress") >= 0, Array.IndexOf(args, "--chat") >= 0, Array.IndexOf(args, "--toolbar") >= 0, Array.IndexOf(args, "--progress") >= 0); return; }

        // --accept-screenshot <out.png>: the accept window for a made-up visitor (no connection)
        int asx = Array.IndexOf(args, "--accept-screenshot");
        if (asx >= 0 && asx + 1 < args.Length) { RunAcceptScreenshot(args[asx + 1]); return; }

        // --password-screenshot <out.png>: the password prompt for a made-up remote ID (no connection)
        int psx = Array.IndexOf(args, "--password-screenshot");
        if (psx >= 0 && psx + 1 < args.Length) { RunPasswordScreenshot(args[psx + 1]); return; }

        // --demo: throwaway settings (random ID, no server, default approval), e.g. for README screenshots
        var cfg = Array.IndexOf(args, "--demo") >= 0 ? Config.Demo() : Config.Load();

        if (shot != null)
        {
            int pg = Array.IndexOf(args, "--page");
            if (cfg.Ephemeral && pg >= 0 && pg + 1 < args.Length && args[pg + 1].Equals("recent", StringComparison.OrdinalIgnoreCase))
                cfg.AddDemoPeers();   // --demo only: an empty Recent page shows nothing
            RunScreenshot(cfg, shot, pg >= 0 && pg + 1 < args.Length ? args[pg + 1] : null);
            return;
        }

        // One copy at a time: if Rplus is already open, bring its window to the front instead of starting again.
        using var single = new Mutex(true, "Local\\Rplus.SingleInstance", out bool first);
        if (!first) { BringExistingToFront(); return; }

        Application.Run(new MainForm(cfg));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    private static void BringExistingToFront()
    {
        var me = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
        {
            if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
            ShowWindow(p.MainWindowHandle, 9 /*SW_RESTORE*/);
            SetForegroundWindow(p.MainWindowHandle);
            return;
        }
    }

    /// <summary>Sends files both ways between a host and a controller on this PC (temp folders only),
    /// checks every byte, and checks that cancelled transfers leave nothing behind.</summary>
    private static string TransferSelfTest()
    {
        var log = new System.Text.StringBuilder();
        int passed = 0, total = 0;
        void Check(string name, bool ok, string detail)
        {
            total++; if (ok) passed++;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}  ({detail})");
        }

        string root = Path.Combine(Path.GetTempPath(), "rplus-xfer-" + Guid.NewGuid().ToString("N")[..8]);
        string local = Path.Combine(root, "local"), remote = Path.Combine(root, "remote");
        Directory.CreateDirectory(local); Directory.CreateDirectory(remote);

        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start(); int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var cfg = Config.Temporary("", "", "");
        cfg.ApproveMode = "password"; cfg.DirectPort = port;
        var host = new Host(cfg);
        host.Start();

        var client = new Client();
        var authed = new ManualResetEventSlim();
        client.OnAuthRequest = (pw, _) => { if (pw) client.SendPassword(cfg.RuntimeOneTimePassword); };
        client.OnAuthResult = (ok, _, _) => { if (ok) authed.Set(); };
        client.OnFrame = (bmp, _, _) => bmp.Dispose();

        // one transfer at a time: the test waits for each to finish
        var finished = new ManualResetEventSlim();
        Client.Transfer last = null;
        Action<Client.Transfer> onProgress = null;
        client.OnTransfer = x =>
        {
            if (x.Finished) { last = new Client.Transfer { Id = x.Id, Ok = x.Ok, Error = x.Error, Done = x.Done, Total = x.Total }; finished.Set(); }
            else onProgress?.Invoke(x);
        };
        Client.Transfer Run(Func<int> start, int timeoutMs = 60000)
        {
            finished.Reset(); last = null;
            start();
            return finished.Wait(timeoutMs) ? last : new Client.Transfer { Ok = false, Error = "timed out" };
        }

        static string Hash(string p)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(p);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }
        static void MakeFile(string p, long size)
        {
            var rnd = new Random(1234);
            var buf = new byte[1 << 20];
            using var fs = File.Create(p);
            for (long left = size; left > 0; left -= buf.Length)
            {
                rnd.NextBytes(buf);
                fs.Write(buf, 0, (int)Math.Min(buf.Length, left));
            }
        }

        try
        {
            client.Connect("127.0.0.1", port, "selftest");
            if (!authed.Wait(10000)) { Check("connect", false, "no login"); return log.ToString(); }

            // 1. upload 5 MB (not a multiple of the chunk size)
            string src = Path.Combine(local, "five.bin"); MakeFile(src, 5L * 1024 * 1024 + 12345);
            var r = Run(() => client.Upload(src, Path.Combine(remote, "five.bin")));
            string dst = Path.Combine(remote, "five.bin");
            Check("upload 5 MB arrives intact", r.Ok && File.Exists(dst) && Hash(dst) == Hash(src),
                  r.Ok ? "same SHA-256" : r.Error);

            // 2. download it back under another folder
            string back = Path.Combine(root, "back"); Directory.CreateDirectory(back);
            r = Run(() => client.Download(dst, back));
            string got = Path.Combine(back, "five.bin");
            Check("download 5 MB arrives intact", r.Ok && File.Exists(got) && Hash(got) == Hash(src),
                  r.Ok ? "same SHA-256" : r.Error);

            // 3. bigger than the old single-message limit (64 MB)
            string big = Path.Combine(local, "big.bin"); MakeFile(big, 100L * 1024 * 1024);
            r = Run(() => client.Upload(big, Path.Combine(remote, "big.bin")), 120000);
            string bigDst = Path.Combine(remote, "big.bin");
            Check("upload 100 MB (over the old 64 MB limit)", r.Ok && File.Exists(bigDst) && Hash(bigDst) == Hash(big),
                  r.Ok ? "same SHA-256" : r.Error);

            // 4. cancel an upload partway: no file, no .part
            int id = 0;
            onProgress = x => { if (x.Done > 0 && x.Done < x.Total) client.CancelTransfer(x.Id); };
            r = Run(() => id = client.Upload(big, Path.Combine(remote, "cancelled.bin")), 120000);
            onProgress = null;
            Thread.Sleep(500);   // the host deletes its .part when the cancel reaches it
            bool noneLeft = !File.Exists(Path.Combine(remote, "cancelled.bin")) && !File.Exists(Path.Combine(remote, "cancelled.bin.part"));
            Check("cancelled upload leaves nothing", !r.Ok && r.Error == "Cancelled" && noneLeft,
                  $"result: {(r.Ok ? "ok" : r.Error)}, leftovers: {(noneLeft ? "none" : "found")}");

            // 5. cancel a download partway
            string dl = Path.Combine(root, "dl"); Directory.CreateDirectory(dl);
            onProgress = x => { if (x.Done > 0 && x.Done < x.Total) client.CancelTransfer(x.Id); };
            r = Run(() => client.Download(bigDst, dl), 120000);
            onProgress = null;
            Thread.Sleep(300);
            noneLeft = !File.Exists(Path.Combine(dl, "big.bin")) && !File.Exists(Path.Combine(dl, "big.bin.part"));
            Check("cancelled download leaves nothing", !r.Ok && r.Error == "Cancelled" && noneLeft,
                  $"result: {(r.Ok ? "ok" : r.Error)}, leftovers: {(noneLeft ? "none" : "found")}");

            // 6. a file that isn't there
            r = Run(() => client.Download(Path.Combine(remote, "missing.bin"), dl));
            Check("missing file is reported", !r.Ok && r.Error.Contains("missing.bin"), r.Ok ? "said ok" : r.Error);
        }
        catch (Exception e) { Check("no exceptions", false, e.Message); }
        finally
        {
            try { client.Close(); } catch { }
            try { host.Stop(); } catch { }
            Thread.Sleep(300);
            try { Directory.Delete(root, true); } catch { }
        }
        log.AppendLine($"RESULT: {passed}/{total}");
        return log.ToString();
    }

    /// <summary>Runs one login per approve-mode case against a local host and reports each.</summary>
    private static string LoginSelfTest()
    {
        var log = new System.Text.StringBuilder();
        int passed = 0, total = 0;

        // (mode, password to send or null, local user clicks Accept after ms (-1 = never), expect ok, expect window cancelled)
        var cases = new (string name, string mode, bool goodPw, bool sendPw, int clickAfterMs, bool expectOk, bool expectCancel)[]
        {
            ("both: right password wins, window closes", "both", true, true, -1, true, true),
            ("both: wrong password, then local Accept",   "both", false, true, 1500, true, false),
            ("password only: wrong password refused",     "password", false, true, -1, false, false),
            ("click only: local Accept, no password",     "click", false, false, 500, true, false),
        };
        foreach (var c in cases)
        {
            total++;
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start(); int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var cfg = Config.Temporary("", "", "");
            cfg.ApproveMode = c.mode; cfg.DirectPort = port;
            var host = new Host(cfg);
            bool cancelled = false;
            host.ApproveConnection = (_, tok) =>
            {
                if (c.clickAfterMs < 0) { cancelled = tok.WaitHandle.WaitOne(5000); return false; }
                Thread.Sleep(c.clickAfterMs); return true;
            };
            host.Start();
            var client = new Client();
            var done = new ManualResetEventSlim();
            bool ok = false; bool askedPw = false;
            client.OnAuthRequest = (pw, click) =>
            {
                askedPw = pw;
                if (pw && c.sendPw) client.SendPassword(c.goodPw ? cfg.RuntimeOneTimePassword : "nope");
            };
            client.OnAuthResult = (o, reason, retry) => { if (o || !retry) { ok = o; done.Set(); } };
            client.OnClosed = () => done.Set();
            client.Connect("127.0.0.1", port, "selftest");
            done.Wait(8000);
            Thread.Sleep(200);
            bool good = ok == c.expectOk && (!c.expectCancel || cancelled) && (c.mode != "click" || !askedPw);
            if (good) passed++;
            log.AppendLine($"{(good ? "PASS" : "FAIL")}  {c.name}  (ok={ok}, window closed by itself={cancelled}, asked for password={askedPw})");
            client.Close(); host.Stop();
        }
        log.AppendLine($"RESULT: {passed}/{total}");
        return log.ToString();
    }

    /// <summary>Register a throwaway host ID on the server, then connect to it by ID through the
    /// relay and log every step. Proves the server path end to end without any window.</summary>
    private static string RelaySelfTest(string idServer, string relay, string key)
    {
        var log = new System.Text.StringBuilder();
        void L(string s) { lock (log) log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }

        var hostCfg = Config.Temporary(idServer, relay, key);
        var host = new Host(hostCfg) { OnStatus = s => L("host: " + s) };
        host.ApproveConnection = (_, _) => true;
        host.RelayOnly = true;  // don't need the direct listener
        host.Start();
        var ready = new ManualResetEventSlim();
        var link = new ServerLink(hostCfg, s => { L("host: relay stream arrived"); host.ServeRelay(s); });
        link.OnState = (t, ok) => { L($"link: {t} ({ok})"); if (ok == true) ready.Set(); };
        link.OnServerFixed = m => L("link fixed: " + m);
        link.Start();
        L($"host id {hostCfg.Id}, server {Rv.IdServerOf(hostCfg)}, relay {Rv.RelayOf(hostCfg)}");
        if (!ready.Wait(15000)) { L("RESULT: FAIL — host never registered"); link.Stop(); return log.ToString(); }

        // The app shares one config, so the controller sees any correction the link made.
        var cliCfg = Config.Temporary(hostCfg.IdServer, hostCfg.RelayServer, key);
        L($"client uses server {Rv.IdServerOf(cliCfg)}, relay {Rv.RelayOf(cliCfg)}");
        var done = new ManualResetEventSlim();
        bool ok2 = false;
        var client = new Client
        {
            OnStatus = s => L("client: " + s),
        };
        client.OnAuthRequest = (pw, click) =>
        {
            L($"client: host asks for password={pw} accept-window={click}");
            if (pw) client.SendPassword(hostCfg.RuntimeOneTimePassword);
        };
        client.OnAuthResult = (ok, reason, retry) => { L($"client: auth {ok} {reason}"); ok2 = ok; if (!ok && !retry) done.Set(); };
        client.OnFrame = (bmp, w, h) => { L($"client: first frame {w}x{h}"); bmp.Dispose(); done.Set(); };
        client.OnClosed = () => { L("client: closed"); done.Set(); };
        try
        {
            var s = Rv.ConnectById(cliCfg, hostCfg.Id, m => L("client: " + m));
            L("client: relay joined");
            client.ConnectStream(s, cliCfg.Id);
            done.Wait(25000);
        }
        catch (Exception e) { L("client error: " + e.Message); }
        L(ok2 ? "RESULT: PASS" : "RESULT: FAIL");
        client.Close(); link.Stop(); host.Stop();
        return log.ToString();
    }

    /// <summary>Start normally, draw ~120 frames, save the window as PNG, then exit.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    /// <summary>Saves the session window showing a drawn demo desktop. Nothing real is captured:
    /// the settings are throwaway, there is no connection, and the window renders itself.</summary>
    private static void RunSessionScreenshot(string path, bool files = false, bool chatOnly = false, bool toolbarOnly = false, bool progress = false)
    {
        var cfg = Config.Demo();
        cfg.ViewStyle = "Adjust window";   // show the whole demo desktop
        string peer = Config.Demo().Id;
        var form = new SessionForm(cfg, "", 0, false, peer) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        if (toolbarOnly) form.ClientSize = new Size(760, form.ClientSize.Height);   // keeps Disconnect close to the other buttons
        using var desk = DemoDesktop(1440, 900);
        if (files)
        {
            var now = DateTime.Now;
            Host.FileEntry D(string n, double daysAgo) => new() { name = n, isDir = true, modified = now.AddDays(-daysAgo).ToString("yyyy-MM-dd HH:mm") };
            Host.FileEntry F(string n, long size, double daysAgo) => new() { name = n, size = size, modified = now.AddDays(-daysAgo).ToString("yyyy-MM-dd HH:mm") };
            form.ShowDemoFiles(@"C:\Users\Public\Documents",
                new() { D("Invoices", 3), D("Photos", 12), F("Budget 2026.xlsx", 48_210, 1.2), F("Meeting notes.docx", 23_552, 0.3), F("Team photo.jpg", 2_457_600, 9), F("Setup guide.pdf", 1_310_720, 30), F("Holiday video.mp4", 769_654_784, 4) },
                @"D:\Shared\Reports",
                new() { D("Archive", 40), F("March figures.xlsx", 61_440, 0.1), F("Q1 summary.pdf", 845_000, 2), F("Sales chart.png", 312_000, 0.5), F("Readme.txt", 1_200, 60) });
            if (progress) form.ShowDemoProgress("Holiday video.mp4", 477_185_966, 769_654_784, 11.4 * 1024 * 1024);
        }
        else if (chatOnly)
        {
            form.ShowDemo(desk, new[]
            {
                ("Remote", "Hi! Can you see my screen?"),
                ("Me", "Yes, all good. Which file is it?"),
                ("Remote", "The sales report, it won't print."),
                ("Me", "Found it. The printer was set to the old office one, switching it now."),
                ("Remote", "Brilliant, thank you!"),
                ("Me", "Done. I'll send you the March figures too."),
                ("Remote", "Perfect, they're in the Reports folder."),
            });
            form.ChatDraft = "Got them, sending over now";
            form.ClientSize = new Size(form.ClientSize.Width, 520);   // the close-up doesn't need the full height
        }
        else form.ShowDemo(desk, new[]
        {
            ("Remote", "Hi! Can you see my screen?"),
            ("Me", "Yes, all good. Opening the report now."),
            ("Remote", "Thanks, the March numbers are in the second sheet."),
        });
        form.Show();
        for (int i = 0; i < 120; i++) { Application.DoEvents(); Thread.Sleep(8); }
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var bmp = new Bitmap(form.Width, form.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(form.Handle, hdc, 2 /*PW_RENDERFULLCONTENT*/); } finally { g.ReleaseHdc(hdc); }
            }
            if (chatOnly || toolbarOnly)
            {
                var r = Rectangle.Intersect(chatOnly ? form.ChatBoundsInWindow() : form.ToolbarBoundsInWindow(), new Rectangle(0, 0, bmp.Width, bmp.Height));
                using var crop = bmp.Clone(r, bmp.PixelFormat);
                crop.Save(path, ImageFormat.Png);
            }
            else bmp.Save(path, ImageFormat.Png);
        }
        catch { }
        try { form.Close(); } catch { }
        Application.Exit();
    }

    /// <summary>Saves the accept window for a random visitor ID. Nothing connects; the window renders itself.</summary>
    private static void RunAcceptScreenshot(string path) =>
        SaveDialogShot(new AcceptForm(Config.Demo().Id), path);

    /// <summary>Saves the password prompt for a random remote ID, with a few hidden characters typed.</summary>
    private static void RunPasswordScreenshot(string path)
    {
        var demo = Config.Demo();
        SaveDialogShot(new PasswordForm(demo.Id, canWaitForAccept: true) { Password = demo.RuntimeOneTimePassword }, path);
    }

    /// <summary>Shows a dialog at the screen corner and saves what it draws (never the screen behind it).</summary>
    private static void SaveDialogShot(Form form, string path)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(0, 0);
        form.Show();
        for (int i = 0; i < 80; i++) { Application.DoEvents(); Thread.Sleep(8); }
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var bmp = new Bitmap(form.Width, form.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(form.Handle, hdc, 2 /*PW_RENDERFULLCONTENT*/); } finally { g.ReleaseHdc(hdc); }
            }
            bmp.Save(path, ImageFormat.Png);
        }
        catch { }
        try { form.Close(); } catch { }
        Application.Exit();
    }

    /// <summary>A made-up desktop for screenshots: wallpaper, taskbar and two app windows.</summary>
    private static Bitmap DemoDesktop(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        void Text(string t, Font f, RectangleF r, Color c, bool center = false)
        {
            using var br = new SolidBrush(c);
            using var sf = new StringFormat { Alignment = center ? StringAlignment.Center : StringAlignment.Near };
            g.DrawString(t, f, br, r, sf);
        }
        using (var wall = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, w, h),
            Color.FromArgb(24, 78, 119), Color.FromArgb(52, 160, 164), 35f))
            g.FillRectangle(wall, 0, 0, w, h);
        using (var tb = new SolidBrush(Color.FromArgb(230, 32, 32, 36))) g.FillRectangle(tb, 0, h - 48, w, 48);
        var appColors = new[] { Color.FromArgb(0, 113, 255), Color.FromArgb(16, 185, 129), Color.FromArgb(245, 158, 11), Color.FromArgb(220, 53, 69) };
        for (int i = 0; i < appColors.Length; i++)
            Theme.FillRound(g, new Rectangle(w / 2 - 110 + i * 56, h - 40, 32, 32), 7, appColors[i]);

        void Window(Rectangle r, string title, Action<Rectangle> body)
        {
            using (var sh = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillRectangle(sh, r.X + 6, r.Y + 8, r.Width, r.Height);
            Theme.FillRound(g, r, 8, Color.White);
            Theme.FillRound(g, new Rectangle(r.X, r.Y, r.Width, 40), 8, Color.FromArgb(243, 244, 246));
            g.FillRectangle(new SolidBrush(Color.FromArgb(243, 244, 246)), r.X, r.Y + 30, r.Width, 10);
            using (var f = new Font("Segoe UI", 12f)) Text(title, f, new RectangleF(r.X + 14, r.Y + 9, r.Width - 28, 24), Color.FromArgb(40, 44, 52));
            body(new Rectangle(r.X + 24, r.Y + 60, r.Width - 48, r.Height - 84));
        }

        Window(new Rectangle(120, 110, 620, 520), "Sales report", b =>
        {
            int[] vals = { 42, 58, 51, 73, 66, 88 };
            string[] mon = { "Oct", "Nov", "Dec", "Jan", "Feb", "Mar" };
            int bw = b.Width / vals.Length;
            using var f = new Font("Segoe UI", 10f);
            for (int i = 0; i < vals.Length; i++)
            {
                int bh = (b.Height - 40) * vals[i] / 100;
                Theme.FillRound(g, new Rectangle(b.X + i * bw + 14, b.Bottom - 30 - bh, bw - 28, bh), 5,
                    i == vals.Length - 1 ? Color.FromArgb(16, 185, 129) : Color.FromArgb(0, 113, 255));
                Text(mon[i], f, new RectangleF(b.X + i * bw, b.Bottom - 24, bw, 22), Color.FromArgb(110, 116, 128), center: true);
            }
        });
        Window(new Rectangle(800, 180, 520, 430), "Notes", b =>
        {
            using var f = new Font("Segoe UI", 12f);
            using var hf = new Font("Segoe UI", 14f, FontStyle.Bold);
            string[] lines = { "Weekly check-in", "", "• Update the sales report", "• Send the March figures", "• Book the team meeting", "• Back up the shared folder" };
            int y = b.Y;
            foreach (var l in lines)
            {
                Text(l, l == lines[0] ? hf : f, new RectangleF(b.X, y, b.Width, 30), Color.FromArgb(40, 44, 52));
                y += l == lines[0] ? 38 : 30;
            }
        });
        return bmp;
    }

    private static void RunScreenshot(Config cfg, string path, string page = null)
    {
        var form = new MainForm(cfg);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(0, 0);
        form.Show();
        Application.DoEvents();
        if (page != null) form.OpenPage(page);

        for (int i = 0; i < 120; i++)
        {
            Application.DoEvents();
            Thread.Sleep(8);
        }
        Application.DoEvents();

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var bmp = new Bitmap(form.Width, form.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(form.Handle, hdc, 2 /*PW_RENDERFULLCONTENT*/); } finally { g.ReleaseHdc(hdc); }
            }
            bmp.Save(path, ImageFormat.Png);
        }
        catch { }

        try { form.Close(); } catch { }
        Application.Exit();
    }
}
