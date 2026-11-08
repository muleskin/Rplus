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

    /// <summary>Register a throwaway host ID on the server, then connect to it by ID through the
    /// relay and log every step. Proves the server path end to end without any window.</summary>
    private static string RelaySelfTest(string idServer, string relay, string key)
    {
        var log = new System.Text.StringBuilder();
        void L(string s) { lock (log) log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }

        var hostCfg = Config.Temporary(idServer, relay, key);
        var host = new Host(hostCfg) { OnStatus = s => L("host: " + s) };
        host.ApproveConnection = _ => true;
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
            OnNeedPassword = null,
        };
        client.OnNeedPassword = () => { L("client: host asked for password"); client.SendPassword(hostCfg.RuntimeOneTimePassword); };
        client.OnAuthResult = (ok, reason) => { L($"client: auth {ok} {reason}"); ok2 = ok; if (!ok) done.Set(); };
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
