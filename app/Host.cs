using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Rplus;

/// <summary>
/// The "being controlled" side. Listens for a peer, checks the password and (optionally)
/// an on-screen accept, then streams the screen as JPEG frames and applies remote input,
/// chat and file-transfer requests. One active session at a time (like the home screen).
/// </summary>
internal sealed class Host
{
    private readonly Config _cfg;
    private TcpListener _listener;
    private Thread _acceptThread;
    private volatile bool _running;

    // UI hooks (marshalled by the caller).
    public Func<string, bool> ApproveConnection;     // peerId -> accept?
    public Action<string> OnChat;                    // text from peer
    public Action<string> OnStatus;                  // human status line
    public Action<bool> OnSessionState;              // true=connected, false=ended

    public bool Sharing => _running;
    public int Quality = 57;                 // JPEG quality for "balanced" (Config.JpegQuality)
    public volatile int Fps = Config.FpsDefault;

    public Host(Config cfg) => _cfg = cfg;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _listener = new TcpListener(IPAddress.Any, _cfg.DirectPort);
        try { _listener.Start(); }
        catch (Exception e) { _listener = null; OnStatus?.Invoke($"Direct port {_cfg.DirectPort} unavailable ({e.Message}); ID server connections still work."); return; }
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Rplus-host" };
        _acceptThread.Start();
        OnStatus?.Invoke($"Ready on port {_cfg.DirectPort}. Share this ID and password.");
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { break; }
            ThreadPool.QueueUserWorkItem(_ => Serve(new Transport(client)));
        }
    }

    /// <summary>A session that arrived through the ID server's relay.</summary>
    public void ServeRelay(Stream s)
    {
        if (!_running) { try { s.Dispose(); } catch { } return; }
        Serve(new Transport(s));
    }

    private Transport _active;

    private void Serve(Transport t)
    {
        using var _ = t;
        try
        {
            var hello = t.Receive();
            if (hello == null || hello.Type != MsgType.Hello) return;
            string peerId = "";
            try { peerId = hello.Body<HelloBody>()?.id ?? ""; } catch { }

            t.Send(Msg.Control(MsgType.AuthRequest,
                new { needPassword = _cfg.RequiresPassword, needClick = _cfg.RequiresClickApproval }));

            if (_cfg.RequiresPassword)
            {
                var auth = t.Receive();
                if (auth == null || auth.Type != MsgType.Auth) return;
                string pw = "";
                try { pw = auth.Body<AuthBody>()?.password ?? ""; } catch { }
                if (!_cfg.PasswordAccepted(pw))
                {
                    t.Send(Msg.Control(MsgType.AuthResult, new { ok = false, reason = "Wrong password" }));
                    OnStatus?.Invoke($"Refused {peerId}: wrong password");
                    return;
                }
            }

            if (_cfg.RequiresClickApproval)
            {
                bool ok = ApproveConnection?.Invoke(peerId) ?? true;
                if (!ok)
                {
                    t.Send(Msg.Control(MsgType.AuthResult, new { ok = false, reason = "Refused" }));
                    OnStatus?.Invoke($"Refused {peerId}");
                    return;
                }
            }

            t.Send(Msg.Control(MsgType.AuthResult, new { ok = true, reason = "" }));
            t.Send(Msg.Control(MsgType.Perms, new
            {
                keyboard = _cfg.EnableKeyboard,
                clipboard = _cfg.EnableClipboard,
                file = _cfg.EnableFileTransfer,
                audio = _cfg.EnableAudio,
            }));

            _active = t;
            OnSessionState?.Invoke(true);
            OnStatus?.Invoke($"{peerId} connected");

            // reader for input/chat/file; streamer for frames
            var streamer = new Thread(() => StreamFrames(t)) { IsBackground = true };
            streamer.Start();
            ReadLoop(t);
            streamer.Join(500);
        }
        catch { }
        finally
        {
            _active = null;
            OnSessionState?.Invoke(false);
            OnStatus?.Invoke("Session ended");
        }
    }

    public void SendChat(string text) => _active?.Send(Msg.Control(MsgType.Chat, new { text }));

    private void StreamFrames(Transport t)
    {
        var rect = Native.VirtualScreen();
        // Cap the sent image so a 4K screen stays responsive.
        const int maxW = 1600;
        double scale = rect.Width > maxW ? (double)maxW / rect.Width : 1.0;
        int ow = (int)(rect.Width * scale), oh = (int)(rect.Height * scale);
        while (_running && t.Connected)
        {
            int periodMs = 1000 / Math.Clamp(Fps, Config.FpsMin, Config.FpsMax);
            long t0 = Environment.TickCount64;
            try
            {
                using var shot = Native.Capture(rect);
                using var small = scale < 1.0 ? new Bitmap(shot, ow, oh) : null;
                var src = small ?? shot;
                var jpg = Native.EncodeJpeg(src, Quality);
                t.Send(new Msg { Type = MsgType.Frame, W = src.Width, H = src.Height, Blob = jpg });
            }
            catch { }
            int spent = (int)(Environment.TickCount64 - t0);
            int wait = periodMs - spent;
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    private void ReadLoop(Transport t)
    {
        var rect = Native.VirtualScreen();
        while (_running && t.Connected)
        {
            var m = t.Receive();
            if (m == null) break;
            switch (m.Type)
            {
                case MsgType.Input:
                    if (_cfg.EnableKeyboard || true) ApplyInput(m, rect);
                    break;
                case MsgType.Chat:
                    try { OnChat?.Invoke(m.Body<ChatBody>()?.text ?? ""); } catch { }
                    break;
                case MsgType.Quality:
                    try
                    {
                        var qb = m.Body<QualityBody>();
                        if (qb != null)
                        {
                            Quality = Math.Clamp(qb.quality, 10, 95);
                            if (qb.fps > 0) Fps = Math.Clamp(qb.fps, Config.FpsMin, Config.FpsMax);
                        }
                    }
                    catch { }
                    break;
                case MsgType.FileList:
                    HandleFileList(t, m);
                    break;
                case MsgType.FileGet:
                    HandleFileGet(t, m);
                    break;
                case MsgType.FilePut:
                    HandleFilePut(m);
                    break;
                case MsgType.Bye:
                    return;
            }
        }
    }

    private void ApplyInput(Msg m, Rectangle rect)
    {
        InputBody b;
        try { b = m.Body<InputBody>(); } catch { return; }
        if (b == null) return;
        switch (b.kind)
        {
            case "move":
                if (_cfg.EnableKeyboard) Native.MouseMove(b.x, b.y);
                else Native.MouseMove(b.x, b.y); // pointer move allowed; buttons/keys gated below
                break;
            case "down": if (_cfg.EnableKeyboard) Native.MouseButton(b.button, true, b.x, b.y); break;
            case "up":   if (_cfg.EnableKeyboard) Native.MouseButton(b.button, false, b.x, b.y); break;
            case "wheel": if (_cfg.EnableKeyboard) Native.MouseWheel(b.delta); break;
            case "key":  if (_cfg.EnableKeyboard) Native.Key((ushort)b.key, b.down); break;
            case "text":
                if (_cfg.EnableKeyboard && !string.IsNullOrEmpty(b.text))
                    foreach (var ch in b.text) Native.Char(ch);
                break;
        }
    }

    // ---- file transfer (host side) ----
    private void HandleFileList(Transport t, Msg m)
    {
        if (!_cfg.EnableFileTransfer) return;
        string path = "";
        try { path = m.Body<FileListBody>()?.path ?? ""; } catch { }
        var entries = new List<FileEntry>();
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                foreach (var d in DriveInfo.GetDrives())
                    entries.Add(new FileEntry { name = d.Name, isDir = true, size = 0, modified = "" });
            }
            else
            {
                foreach (var d in Directory.GetDirectories(path))
                    entries.Add(new FileEntry { name = Path.GetFileName(d), isDir = true, size = 0,
                        modified = SafeTime(d) });
                foreach (var f in Directory.GetFiles(path))
                {
                    var fi = new FileInfo(f);
                    entries.Add(new FileEntry { name = fi.Name, isDir = false, size = fi.Length,
                        modified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") });
                }
            }
        }
        catch { }
        t.Send(Msg.Control(MsgType.FileListReply, new { path, entries }));
    }

    private static string SafeTime(string d)
    {
        try { return Directory.GetLastWriteTime(d).ToString("yyyy-MM-dd HH:mm"); } catch { return ""; }
    }

    private void HandleFileGet(Transport t, Msg m)
    {
        if (!_cfg.EnableFileTransfer) return;
        string path = "";
        try { path = m.Body<FileGetBody>()?.path ?? ""; } catch { }
        try
        {
            var bytes = File.ReadAllBytes(path);
            var name = Path.GetFileName(path);
            var head = JsonSerializer.SerializeToUtf8Bytes(new { name, size = bytes.Length });
            // FileData payload = [int headLen][head json][file bytes]
            var blob = new byte[4 + head.Length + bytes.Length];
            blob[0] = (byte)(head.Length >> 24); blob[1] = (byte)(head.Length >> 16);
            blob[2] = (byte)(head.Length >> 8); blob[3] = (byte)head.Length;
            Array.Copy(head, 0, blob, 4, head.Length);
            Array.Copy(bytes, 0, blob, 4 + head.Length, bytes.Length);
            t.Send(new Msg { Type = MsgType.FileData, Blob = blob });
        }
        catch { }
    }

    private void HandleFilePut(Msg m)
    {
        if (!_cfg.EnableFileTransfer) return;
        try
        {
            int headLen = (m.Blob[0] << 24) | (m.Blob[1] << 16) | (m.Blob[2] << 8) | m.Blob[3];
            var head = JsonSerializer.Deserialize<FilePutHead>(m.Blob.AsSpan(4, headLen));
            var data = m.Blob.AsSpan(4 + headLen).ToArray();
            File.WriteAllBytes(head.path, data);
        }
        catch { }
    }

    // ---- body DTOs ----
    public sealed class HelloBody { public string id { get; set; } }
    public sealed class AuthBody { public string password { get; set; } }
    public sealed class ChatBody { public string text { get; set; } }
    public sealed class QualityBody { public int quality { get; set; } public int fps { get; set; } }
    public sealed class InputBody
    {
        public string kind { get; set; } = "";
        public double x { get; set; }
        public double y { get; set; }
        public int button { get; set; }
        public int delta { get; set; }
        public int key { get; set; }
        public bool down { get; set; }
        public string text { get; set; } = "";
    }
    public sealed class FileListBody { public string path { get; set; } }
    public sealed class FileGetBody { public string path { get; set; } }
    public sealed class FilePutHead { public string path { get; set; } }
    public sealed class FileEntry
    {
        public string name { get; set; } = "";
        public bool isDir { get; set; }
        public long size { get; set; }
        public string modified { get; set; } = "";
    }
}
