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
    /// <summary>Shows the accept window: (peerId, cancel) → accepted? The token fires when the
    /// visitor's password was accepted first, so the window should close itself.</summary>
    public Func<string, CancellationToken, bool> ApproveConnection;
    public Action<string> OnChat;                    // text from peer
    public Action<string> OnStatus;                  // human status line
    public Action<bool> OnSessionState;              // true=connected, false=ended

    public bool Sharing => _running;
    public int Quality = 57;                 // JPEG quality for "balanced" (Config.JpegQuality)
    public volatile int Fps = Config.FpsDefault;

    public Host(Config cfg) => _cfg = cfg;

    /// <summary>Only take sessions through the ID server's relay (used by the self-test).</summary>
    public bool RelayOnly;

    public void Start()
    {
        if (_running) return;
        _running = true;
        if (RelayOnly) { OnStatus?.Invoke("Ready (relay only)."); return; }
        int port = _cfg.ListenPort();
        _listener = new TcpListener(IPAddress.Any, port);
        try { _listener.Start(); }
        catch (Exception e) { _listener = null; OnStatus?.Invoke($"Direct port {port} unavailable ({e.Message}); ID server connections still work."); return; }
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Rplus-host" };
        _acceptThread.Start();
        OnStatus?.Invoke($"Ready on port {port}. Share this ID and password.");
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

            // approve-mode: "password" = password only (no
            // accept window), "click" = accept window only (no password check), both = either one.
            bool byPassword = _cfg.RequiresPassword;
            bool byClick = _cfg.RequiresClickApproval;
            t.Send(Msg.Control(MsgType.AuthRequest, new { needPassword = byPassword, needClick = byClick }));

            using var approval = new CancellationTokenSource();
            void CancelApproval() { try { approval.Cancel(); } catch { } }
            Task<bool> clickTask = byClick ? Task.Run(() => ApproveConnection?.Invoke(peerId, approval.Token) ?? true) : null;
            Task<Msg> pending = null;     // a message read while waiting; the session handles it next
            bool authed = false;
            if (!byPassword)
            {
                authed = clickTask.Result;
            }
            else
            {
                while (true)
                {
                    pending ??= Task.Run(() => t.Receive());
                    int idx = clickTask != null ? Task.WaitAny(pending, clickTask) : Task.WaitAny(pending);
                    if (idx == 1) { authed = clickTask.Result; break; }       // the local user decided first
                    var m = pending.Result; pending = null;
                    if (m == null) { CancelApproval(); return; }            // they hung up
                    if (m.Type != MsgType.Auth) continue;
                    string pw = "";
                    try { pw = m.Body<AuthBody>()?.password ?? ""; } catch { }
                    if (_cfg.PasswordAccepted(pw)) { authed = true; CancelApproval(); break; }
                    // Wrong password: with the accept window also allowed, they may try again or wait.
                    bool retry = clickTask != null;
                    t.Send(Msg.Control(MsgType.AuthResult, new { ok = false, reason = "Wrong password", retry }));
                    OnStatus?.Invoke($"{peerId}: wrong password");
                    if (!retry) return;
                }
            }
            if (!authed)
            {
                t.Send(Msg.Control(MsgType.AuthResult, new { ok = false, reason = "Refused", retry = false }));
                OnStatus?.Invoke($"Refused {peerId}");
                return;
            }

            t.Send(Msg.Control(MsgType.AuthResult, new { ok = true, reason = "", retry = false }));
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
            ReadLoop(t, pending);
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

    private void ReadLoop(Transport t, Task<Msg> pending = null)
    {
        var sf = new SessionFiles();
        try { ReadMessages(t, pending, sf); }
        finally { sf.EndAll(); }
    }

    private void ReadMessages(Transport t, Task<Msg> pending, SessionFiles sf)
    {
        var rect = Native.VirtualScreen();
        while (_running && t.Connected)
        {
            Msg m;
            if (pending != null) { m = pending.Result; pending = null; }
            else m = t.Receive();
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
                    HandleFileGet(t, m, sf);
                    break;
                case MsgType.XferStart:
                    HandleUploadStart(t, m, sf);
                    break;
                case MsgType.XferChunk:
                    sf.Uploads.Chunk(m.Blob);
                    break;
                case MsgType.XferEnd:
                    HandleUploadEnd(t, m, sf);
                    break;
                case MsgType.XferCancel:
                    HandleCancel(m, sf);
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

    /// <summary>One connection's transfers: uploads arriving and downloads being sent, by transfer id.
    /// Per connection, because each controller numbers its own transfers from 1.</summary>
    private sealed class SessionFiles
    {
        public readonly FileReceiver Uploads = new();
        public readonly Dictionary<int, CancellationTokenSource> Downloads = new();
        public void EndAll()
        {
            lock (Downloads) foreach (var c in Downloads.Values) { try { c.Cancel(); } catch { } }
            Uploads.AbortAll();   // half-received files are deleted, never left under their real name
        }
    }

    private void HandleFileGet(Transport t, Msg m, SessionFiles sf)
    {
        FileGetBody b;
        try { b = m.Body<FileGetBody>(); } catch { return; }
        if (b == null || b.id <= 0) return;   // only the chunked form is answered
        if (!_cfg.EnableFileTransfer)
        {
            t.Send(Msg.Control(MsgType.XferEnd, new FileXfer.EndBody { id = b.id, ok = false, error = "File transfer is turned off on the other computer" }));
            return;
        }
        var cts = new CancellationTokenSource();
        lock (sf.Downloads) sf.Downloads[b.id] = cts;
        Task.Run(() =>
        {
            try { FileXfer.Send(t, b.id, b.path ?? "", "", cts.Token, null); }
            finally { lock (sf.Downloads) sf.Downloads.Remove(b.id); cts.Dispose(); }
        });
    }

    private void HandleUploadStart(Transport t, Msg m, SessionFiles sf)
    {
        FileXfer.StartBody b;
        try { b = m.Body<FileXfer.StartBody>(); } catch { return; }
        if (b == null) return;
        string err = !_cfg.EnableFileTransfer ? "File transfer is turned off on the other computer"
                   : string.IsNullOrEmpty(b.path) ? "No destination folder"
                   : sf.Uploads.Start(b.id, b.name, b.path, b.size);
        if (err != "")
        {
            // Tell the sender straight away so it stops; any chunks still on the way are ignored.
            t.Send(Msg.Control(MsgType.XferCancel, new FileXfer.IdBody { id = b.id }));
            t.Send(Msg.Control(MsgType.XferResult, new FileXfer.EndBody { id = b.id, ok = false, error = err }));
        }
    }

    private void HandleUploadEnd(Transport t, Msg m, SessionFiles sf)
    {
        FileXfer.EndBody b;
        try { b = m.Body<FileXfer.EndBody>(); } catch { return; }
        if (b == null) return;
        string err = sf.Uploads.Finish(b.id, b.ok, b.error);
        t.Send(Msg.Control(MsgType.XferResult, new FileXfer.EndBody { id = b.id, ok = err == "", error = err }));
    }

    private void HandleCancel(Msg m, SessionFiles sf)
    {
        FileXfer.IdBody b;
        try { b = m.Body<FileXfer.IdBody>(); } catch { return; }
        if (b == null) return;
        lock (sf.Downloads) if (sf.Downloads.TryGetValue(b.id, out var cts)) cts.Cancel();
        sf.Uploads.Abort(b.id);
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
    public sealed class FileGetBody { public string path { get; set; } public int id { get; set; } }
    public sealed class FileEntry
    {
        public string name { get; set; } = "";
        public bool isDir { get; set; }
        public long size { get; set; }
        public string modified { get; set; } = "";
    }
}
