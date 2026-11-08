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

        string shot = null;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--screenshot" && i + 1 < args.Length) shot = args[i + 1];

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

        var cfg = Config.Load();

        if (shot != null) { RunScreenshot(cfg, shot); return; }

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
    private static void RunScreenshot(Config cfg, string path)
    {
        var form = new MainForm(cfg);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(0, 0);
        form.Show();
        Application.DoEvents();

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
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(path, ImageFormat.Png);
        }
        catch { }

        try { form.Close(); } catch { }
        Application.Exit();
    }
}
