using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Rplus;

/// <summary>
/// Rplus's own direct wire protocol (see MECHANICS.md).
/// Every message is one frame: [4-byte big-endian length][1-byte type][payload].
/// Control messages are JSON; the screen frame payload is a JPEG blob after a small header.
/// </summary>
internal enum MsgType : byte
{
    Hello = 1,          // client -> host : { id }
    AuthRequest = 2,    // host -> client : { needPassword, needClick }
    Auth = 3,           // client -> host : { password }
    AuthResult = 4,     // host -> client : { ok, reason }
    Frame = 5,          // host -> client : [int w][int h][jpeg bytes]
    Input = 6,          // client -> host : { kind, x, y, button, delta, key, down, text }
    Chat = 7,           // either        : { text }
    Perms = 8,          // host -> client : { keyboard, clipboard, file, audio }
    Quality = 9,        // client -> host : { quality }  (0..100)
    FileList = 10,      // either        : { path, remote }
    FileListReply = 11, // { path, entries:[{name,isDir,size,modified}] }
    FileGet = 12,       // client -> host : { path, id }  (host answers with a chunked transfer)
    FileData = 13,      // retired (whole-file download in one message)
    FilePut = 14,       // retired (whole-file upload in one message)
    Ping = 15,
    Bye = 16,
    XferStart = 17,     // sender -> receiver : { id, name, size, path }        (see FileTransfer.cs)
    XferChunk = 18,     // sender -> receiver : [4-byte id][data]
    XferEnd = 19,       // sender -> receiver : { id, ok, error }  all chunks sent, or stopped
    XferResult = 20,    // receiver -> sender : { id, ok, error }  file saved, or why not
    XferCancel = 21,    // either             : { id }
}

internal sealed class Msg
{
    public MsgType Type;
    public JsonElement Json;     // for control messages
    public byte[] Blob = Array.Empty<byte>();   // for frame / file payloads
    public int W, H;             // frame dims

    public static Msg Control(MsgType t, object body) =>
        new() { Type = t, Blob = JsonSerializer.SerializeToUtf8Bytes(body) };

    public T Body<T>() => JsonSerializer.Deserialize<T>(Blob);
}

/// <summary>Length-prefixed framing over a TCP stream, usable from any thread with a lock.</summary>
internal sealed class Transport : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly Stream _s;
    private readonly object _writeLock = new();
    private volatile bool _closed;

    public Transport(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _s = tcp.GetStream();
    }

    /// <summary>Over any stream, e.g. a relay connection.</summary>
    public Transport(Stream s) { _s = s; }

    public bool Connected => !_closed && (_tcp == null || _tcp.Connected);

    public void Send(Msg m)
    {
        if (_closed) return;
        // Frame layout: for Frame type we prepend 8 bytes (w,h) then blob.
        byte[] head = m.Type == MsgType.Frame ? new byte[8] : Array.Empty<byte>();
        if (m.Type == MsgType.Frame)
        {
            WriteI32(head, 0, m.W);
            WriteI32(head, 4, m.H);
        }
        int payloadLen = head.Length + m.Blob.Length;
        var prefix = new byte[5];
        WriteI32(prefix, 0, payloadLen + 1); // +1 for type byte
        prefix[4] = (byte)m.Type;
        lock (_writeLock)
        {
            try
            {
                _s.Write(prefix, 0, prefix.Length);
                if (head.Length > 0) _s.Write(head, 0, head.Length);
                if (m.Blob.Length > 0) _s.Write(m.Blob, 0, m.Blob.Length);
                _s.Flush();
            }
            catch { Close(); }
        }
    }

    public Msg Receive()
    {
        var prefix = ReadExact(5);
        if (prefix == null) return null;
        int total = ReadI32(prefix, 0);
        var type = (MsgType)prefix[4];
        int bodyLen = total - 1;
        if (bodyLen < 0 || bodyLen > 64 * 1024 * 1024) { Close(); return null; }
        var body = bodyLen > 0 ? ReadExact(bodyLen) : Array.Empty<byte>();
        if (body == null) return null;
        var m = new Msg { Type = type };
        if (type == MsgType.Frame && body.Length >= 8)
        {
            m.W = ReadI32(body, 0);
            m.H = ReadI32(body, 4);
            m.Blob = body.AsSpan(8).ToArray();
        }
        else m.Blob = body;
        return m;
    }

    private byte[] ReadExact(int n)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r;
            try { r = _s.Read(buf, off, n - off); }
            catch { Close(); return null; }
            if (r <= 0) { Close(); return null; }
            off += r;
        }
        return buf;
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _s.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
    }

    public void Dispose() => Close();

    private static void WriteI32(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
    private static int ReadI32(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}
