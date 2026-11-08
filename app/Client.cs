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
    public Action<bool, string> OnAuthResult;    // ok, reason
    public Action OnNeedPassword;                // host wants a password
    public Action<bool, bool, bool, bool> OnPerms; // keyboard, clipboard, file, audio
    public Action<string, List<Host.FileEntry>> OnFileList;
    public Action<string, byte[]> OnFileData;    // name, bytes
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
    public void RequestFile(string path) => _t?.Send(Msg.Control(MsgType.FileGet, new { path }));
    public void PutFile(string remotePath, byte[] data)
    {
        var head = JsonSerializer.SerializeToUtf8Bytes(new { path = remotePath });
        var blob = new byte[4 + head.Length + data.Length];
        blob[0] = (byte)(head.Length >> 24); blob[1] = (byte)(head.Length >> 16);
        blob[2] = (byte)(head.Length >> 8); blob[3] = (byte)head.Length;
        Array.Copy(head, 0, blob, 4, head.Length);
        Array.Copy(data, 0, blob, 4 + head.Length, data.Length);
        _t?.Send(new Msg { Type = MsgType.FilePut, Blob = blob });
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
                        if (a is { needPassword: true }) OnNeedPassword?.Invoke();
                    }
                    catch { }
                    break;
                case MsgType.AuthResult:
                    try { var r = m.Body<AuthRes>(); OnAuthResult?.Invoke(r.ok, r.reason); } catch { }
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
                case MsgType.FileData:
                    try
                    {
                        int hl = (m.Blob[0] << 24) | (m.Blob[1] << 16) | (m.Blob[2] << 8) | m.Blob[3];
                        var h = JsonSerializer.Deserialize<FileDataHead>(m.Blob.AsSpan(4, hl));
                        var data = m.Blob.AsSpan(4 + hl).ToArray();
                        OnFileData?.Invoke(h.name, data);
                    }
                    catch { }
                    break;
            }
        }
        bool wasRunning = _running;
        _running = false;
        if (wasRunning && !_gotAnswer)
            OnStatus?.Invoke("The other side closed the connection without answering as Rplus.");
        else OnClosed?.Invoke();
    }

    private sealed class AuthReq { public bool needPassword { get; set; } public bool needClick { get; set; } }
    private sealed class AuthRes { public bool ok { get; set; } public string reason { get; set; } }
    private sealed class PermsBody { public bool keyboard { get; set; } public bool clipboard { get; set; } public bool file { get; set; } public bool audio { get; set; } }
    private sealed class FileListReply { public string path { get; set; } public List<Host.FileEntry> entries { get; set; } = new(); }
    private sealed class FileDataHead { public string name { get; set; } public int size { get; set; } }
}
