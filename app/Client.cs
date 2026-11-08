using System.Drawing;
using System.Net.Sockets;
using System.Text.Json;

namespace Rplus;

/// <summary>
/// The "controlling" side. Connects to a host, authenticates, receives screen frames
/// and sends input / chat / file-transfer requests.
/// </summary>
internal sealed class Client
{
    private Transport _t;
    private Thread _reader;
    private volatile bool _running;

    public Action<Bitmap, int, int> OnFrame;
    public Action<string> OnChat;
    public Action<string> OnStatus;
    public Action<bool, string, bool> OnAuthResult;   // ok, reason, may retry
    public Action<bool, bool> OnAuthRequest;          // password accepted?, accept window shown?
    public Action<bool, bool, bool, bool> OnPerms; // keyboard, clipboard, file, audio
    public Action<string, List<Host.FileEntry>> OnFileList;
    public Action<Transfer> OnTransfer;          // progress and the end of each file transfer (any thread)
    public Action OnClosed;

    public bool Connected => _running && _t is { Connected: true };

    public bool Connect(string host, int port, string myId)
    {
        try
        {
            var tcp = new TcpClient();
            var ok = tcp.ConnectAsync(host, port).Wait(5000);
            if (!ok || !tcp.Connected) { OnStatus?.Invoke("Could not reach " + host); return false; }
            Begin(new Transport(tcp), myId);
            return true;
        }
        catch (Exception e) { OnStatus?.Invoke("Connect failed: " + e.Message); return false; }
    }

    /// <summary>Use an already-open stream (the ID server's relay).</summary>
    public void ConnectStream(Stream s, string myId) => Begin(new Transport(s), myId);

    private volatile bool _gotAnswer;
    private void Begin(Transport t, string myId)
    {
        _t = t;
        _running = true;
        _t.Send(Msg.Control(MsgType.Hello, new { id = myId }));
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Rplus-client" };
        _reader.Start();
    }

    public void SendPassword(string pw) => _t?.Send(Msg.Control(MsgType.Auth, new { password = pw }));
    public void SendChat(string text) => _t?.Send(Msg.Control(MsgType.Chat, new { text }));
    public void SetQuality(int q, int fps) => _t?.Send(Msg.Control(MsgType.Quality, new { quality = q, fps }));

    public void MouseMove(double x, double y) => Input("move", x, y, 0, 0, 0, false, null);
    public void MouseDown(int b, double x, double y) => Input("down", x, y, b, 0, 0, false, null);
    public void MouseUp(int b, double x, double y) => Input("up", x, y, b, 0, 0, false, null);
    public void Wheel(int delta) => Input("wheel", 0, 0, 0, delta, 0, false, null);
    public void KeyEv(int vk, bool down) => Input("key", 0, 0, 0, 0, vk, down, null);
    public void TypeText(string s) => Input("text", 0, 0, 0, 0, 0, false, s);

    private void Input(string kind, double x, double y, int button, int delta, int key, bool down, string text)
        => _t?.Send(Msg.Control(MsgType.Input, new { kind, x, y, button, delta, key, down, text = text ?? "" }));

    public void RequestFileList(string path) => _t?.Send(Msg.Control(MsgType.FileList, new { path }));
    // ---------------- file transfer ----------------

    /// <summary>One transfer's state, as passed to OnTransfer.</summary>
    public sealed class Transfer
    {
        public int Id;
        public bool Upload;
        public string Name = "";
        public long Done, Total;
        public bool Finished, Ok;
        public string Error = "";
        public long LastReportMs;
    }

    private int _nextXfer;
    private readonly Dictionary<int, Transfer> _xfers = new();
    private readonly Dictionary<int, CancellationTokenSource> _uploadCancel = new();
    private readonly Dictionary<int, string> _downloadDir = new();
    private readonly FileReceiver _downloads = new();

    /// <summary>Sends a local file to remotePath on the other computer. Returns the transfer id.</summary>
    public int Upload(string localPath, string remotePath)
    {
        var x = NewTransfer(true, Path.GetFileName(localPath));
        var cts = new CancellationTokenSource();
        lock (_xfers) _uploadCancel[x.Id] = cts;
        var t = _t;
        Task.Run(() =>
        {
            string err = t == null ? "Not connected"
                : FileXfer.Send(t, x.Id, localPath, remotePath, cts.Token, (d, total) => Progress(x, d, total));
            if (err != "") Finish(x.Id, false, err);
            // otherwise the other side answers with XferResult once the file is saved
        });
        return x.Id;
    }

    /// <summary>Fetches remotePath from the other computer into localDir. Returns the transfer id.</summary>
    public int Download(string remotePath, string localDir)
    {
        var x = NewTransfer(false, Path.GetFileName(remotePath));
        lock (_xfers) _downloadDir[x.Id] = localDir;
        _t?.Send(Msg.Control(MsgType.FileGet, new { path = remotePath, id = x.Id }));
        return x.Id;
    }

    public void CancelTransfer(int id)
    {
        CancellationTokenSource cts;
        lock (_xfers) _uploadCancel.TryGetValue(id, out cts);
        if (cts != null) { try { cts.Cancel(); } catch { } }   // the send loop tells the other side
        else _t?.Send(Msg.Control(MsgType.XferCancel, new FileXfer.IdBody { id = id }));   // a download: ask them to stop
    }

    private Transfer NewTransfer(bool upload, string name)
    {
        var x = new Transfer { Id = Interlocked.Increment(ref _nextXfer), Upload = upload, Name = name };
        lock (_xfers) _xfers[x.Id] = x;
        OnTransfer?.Invoke(x);
        return x;
    }

    private void Progress(Transfer x, long done, long total)
    {
        x.Done = done; x.Total = total;
        long now = Environment.TickCount64;
        if (done < total && now - x.LastReportMs < 100) return;   // about ten updates a second
        x.LastReportMs = now;
        OnTransfer?.Invoke(x);
    }

    private void Finish(int id, bool ok, string error)
    {
        Transfer x;
        CancellationTokenSource cts;
        lock (_xfers)
        {
            if (!_xfers.TryGetValue(id, out x) || x.Finished) return;   // only the first answer counts
            x.Finished = true; x.Ok = ok; x.Error = error ?? "";
            _xfers.Remove(id);
            _uploadCancel.Remove(id, out cts);
            _downloadDir.Remove(id);
        }
        cts?.Dispose();
        OnTransfer?.Invoke(x);
    }

    private void OnXferStart(Msg m)
    {
        FileXfer.StartBody b;
        try { b = m.Body<FileXfer.StartBody>(); } catch { return; }
        if (b == null) return;
        Transfer x; string dir;
        lock (_xfers) { _xfers.TryGetValue(b.id, out x); _downloadDir.TryGetValue(b.id, out dir); }
        if (x == null || dir == null) return;
        x.Name = b.name; x.Total = b.size;
        string err = _downloads.Start(b.id, b.name, Path.Combine(dir, Path.GetFileName(b.name)), b.size);
        if (err != "")
        {
            _t?.Send(Msg.Control(MsgType.XferCancel, new FileXfer.IdBody { id = b.id }));
            Finish(b.id, false, err);
        }
        else Progress(x, 0, b.size);
    }

    private void OnXferChunk(Msg m)
    {
        var r = _downloads.Chunk(m.Blob);
        if (r == null) return;
        Transfer x;
        lock (_xfers) _xfers.TryGetValue(FileXfer.ChunkId(m.Blob), out x);
        if (x != null) Progress(x, r.Value.got, r.Value.size);
    }

    private void OnXferEnd(Msg m)
    {
        FileXfer.EndBody b;
        try { b = m.Body<FileXfer.EndBody>(); } catch { return; }
        if (b == null) return;
        string err = _downloads.Finish(b.id, b.ok, b.error);
        Finish(b.id, err == "", err);
    }

    private void OnXferResult(Msg m)
    {
        try { var b = m.Body<FileXfer.EndBody>(); if (b != null) Finish(b.id, b.ok, b.error); } catch { }
    }

    private void OnXferCancel(Msg m)
    {
        // The other side refused an upload: stop sending. Its XferResult says why.
        try
        {
            var b = m.Body<FileXfer.IdBody>();
            CancellationTokenSource cts = null;
            if (b != null) lock (_xfers) _uploadCancel.TryGetValue(b.id, out cts);
            cts?.Cancel();
        }
        catch { }
    }

    /// <summary>The connection ended: nothing in flight can finish.</summary>
    private void EndAllTransfers()
    {
        List<int> ids;
        lock (_xfers)
        {
            ids = _xfers.Keys.ToList();
            foreach (var c in _uploadCancel.Values) { try { c.Cancel(); } catch { } }
        }
        _downloads.AbortAll();
        foreach (var id in ids) Finish(id, false, "Connection lost");
    }

    public void Close()
    {
        _running = false;
        try { _t?.Send(new Msg { Type = MsgType.Bye }); } catch { }
        _t?.Close();
    }

    private void ReadLoop()
    {
        while (_running)
        {
            var m = _t.Receive();
            if (m == null) break;
            switch (m.Type)
            {
                case MsgType.AuthRequest:
                    _gotAnswer = true;
                    try
                    {
                        var a = m.Body<AuthReq>();
                        if (a != null) OnAuthRequest?.Invoke(a.needPassword, a.needClick);
                    }
                    catch { }
                    break;
                case MsgType.AuthResult:
                    try { var r = m.Body<AuthRes>(); OnAuthResult?.Invoke(r.ok, r.reason, r.retry); } catch { }
                    break;
                case MsgType.Perms:
                    try { var p = m.Body<PermsBody>(); OnPerms?.Invoke(p.keyboard, p.clipboard, p.file, p.audio); } catch { }
                    break;
                case MsgType.Frame:
                    try
                    {
                        using var ms = new MemoryStream(m.Blob);
                        var bmp = new Bitmap(ms);
                        OnFrame?.Invoke(bmp, m.W, m.H);
                    }
                    catch { }
                    break;
                case MsgType.Chat:
                    try { OnChat?.Invoke(m.Body<Host.ChatBody>()?.text ?? ""); } catch { }
                    break;
                case MsgType.FileListReply:
                    try { var fl = m.Body<FileListReply>(); OnFileList?.Invoke(fl.path, fl.entries); } catch { }
                    break;
                case MsgType.XferStart: OnXferStart(m); break;
                case MsgType.XferChunk: OnXferChunk(m); break;
                case MsgType.XferEnd: OnXferEnd(m); break;
                case MsgType.XferResult: OnXferResult(m); break;
                case MsgType.XferCancel: OnXferCancel(m); break;
            }
        }
        bool wasRunning = _running;
        _running = false;
        EndAllTransfers();
        if (wasRunning && !_gotAnswer)
            OnStatus?.Invoke("The other side closed the connection without answering as Rplus.");
        else OnClosed?.Invoke();
    }

    private sealed class AuthReq { public bool needPassword { get; set; } public bool needClick { get; set; } }
    private sealed class AuthRes { public bool ok { get; set; } public string reason { get; set; } public bool retry { get; set; } }
    private sealed class PermsBody { public bool keyboard { get; set; } public bool clipboard { get; set; } public bool file { get; set; } public bool audio { get; set; } }
    private sealed class FileListReply { public string path { get; set; } public List<Host.FileEntry> entries { get; set; } = new(); }
}
