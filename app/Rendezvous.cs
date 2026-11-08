using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Rplus;

/// <summary>
/// Talks to a self-hosted ID server (hbbs, default port 21116) and relay
/// server (hbbr, default port 21117), so two Rplus computers can find each other by ID and
/// connect through the relay even when they are on different networks.
///
/// Messages are RendezvousMessage protobufs (field numbers in MECHANICS.md). UDP carries one raw
/// message per datagram; TCP uses the variable-length "BytesCodec" frame header.
/// Only Rplus peers can be controlled this way: any other client answers with its own
/// session protocol, which Rplus detects and reports.
/// </summary>
internal static class Rv
{
    public const int IdPort = 21116;              // added when the ID server has no port
    public const int RelayPort = IdPort + 1;      // ID server port + 1; used for a relay entered without a port
    public const int RegisterIntervalMs = 12000;  // estimated (REG_INTERVAL not located in the mediator code)
    public const int RelayAnswerTimeoutMs = 10000;
    public const string Version = "rplus-1.0";

    // RendezvousMessage oneof field numbers
    public const int F_RegisterPeer = 6, F_RegisterPeerResponse = 7, F_PunchHole = 9,
        F_RegisterPk = 15, F_RegisterPkResponse = 16, F_RequestRelay = 18, F_RelayResponse = 19,
        F_KeyExchange = 25;

    public static (string host, int port) Split(string s, int defPort)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];
        s = s.TrimEnd('/');
        int c = s.LastIndexOf(':');
        if (c > 0 && s.IndexOf(':') == c && int.TryParse(s[(c + 1)..], out var p)) return (s[..c], p);
        return (s, defPort);
    }

    public static string IdServerOf(Config cfg)
    {
        var (h, p) = Split(cfg.IdServer, IdPort);
        return string.IsNullOrEmpty(h) ? "" : $"{h}:{p}";
    }

    /// <summary>Relay address:
    /// the "relay-server" setting, else the relay the other side/server suggested, else the ID
    /// server with its port + 1 (so 21116 → 21117).</summary>
    public static string PickRelay(Config cfg, string suggested)
    {
        if (!string.IsNullOrWhiteSpace(cfg.RelayServer))
        {
            var (h, p) = Split(cfg.RelayServer, RelayPort);
            return $"{h}:{p}";
        }
        if (!string.IsNullOrWhiteSpace(suggested))
        {
            var (h, p) = Split(suggested, RelayPort);
            return $"{h}:{p}";
        }
        var (ih, ip) = Split(cfg.IdServer, IdPort);
        return string.IsNullOrEmpty(ih) ? "" : $"{ih}:{ip + 1}";
    }

    public static string RelayOf(Config cfg) => PickRelay(cfg, null);

    // ------------------------------------------------------------------ protobuf (tiny)
    public sealed class Pb
    {
        private readonly MemoryStream _m = new();
        public byte[] Bytes => _m.ToArray();
        public Pb Var(ulong v) { while (v >= 0x80) { _m.WriteByte((byte)(v | 0x80)); v >>= 7; } _m.WriteByte((byte)v); return this; }
        private Pb Tag(int f, int wt) => Var((ulong)((f << 3) | wt));
        public Pb Str(int f, string s) { if (string.IsNullOrEmpty(s)) return this; return Blob(f, Encoding.UTF8.GetBytes(s)); }
        public Pb Blob(int f, byte[] b) { if (b == null || b.Length == 0) return this; Tag(f, 2); Var((ulong)b.Length); _m.Write(b); return this; }
        public Pb Int(int f, long v) { if (v == 0) return this; Tag(f, 0); return Var((ulong)v); }
        public Pb Bool(int f, bool v) => v ? Int(f, 1) : this;
        public Pb Msg(int f, Pb inner) { Tag(f, 2); var b = inner.Bytes; Var((ulong)b.Length); _m.Write(b); return this; }
        /// <summary>A RendezvousMessage carrying one union member.</summary>
        public static byte[] Wrap(int field, Pb inner) => new Pb().Msg(field, inner).Bytes;
    }

    /// <summary>Parsed protobuf message: field number -> values (varint as ulong, bytes as byte[]).</summary>
    public sealed class PbIn
    {
        public readonly Dictionary<int, object> F = new();
        public static PbIn Parse(ReadOnlySpan<byte> b)
        {
            var r = new PbIn();
            int i = 0;
            while (i < b.Length)
            {
                ulong key = ReadVar(b, ref i);
                int f = (int)(key >> 3), wt = (int)(key & 7);
                switch (wt)
                {
                    case 0: r.F[f] = ReadVar(b, ref i); break;
                    case 2:
                        int n = (int)ReadVar(b, ref i);
                        if (n < 0 || i + n > b.Length) return r;
                        r.F[f] = b.Slice(i, n).ToArray(); i += n; break;
                    case 1: i += 8; break;
                    case 5: i += 4; break;
                    default: return r;
                }
            }
            return r;
        }
        private static ulong ReadVar(ReadOnlySpan<byte> b, ref int i)
        {
            ulong v = 0; int s = 0;
            while (i < b.Length) { byte x = b[i++]; v |= (ulong)(x & 0x7F) << s; if ((x & 0x80) == 0) break; s += 7; }
            return v;
        }
        public byte[] Bytes(int f) => F.TryGetValue(f, out var o) && o is byte[] b ? b : Array.Empty<byte>();
        public string Str(int f) => Encoding.UTF8.GetString(Bytes(f));
        public long Int(int f) => F.TryGetValue(f, out var o) && o is ulong u ? (long)u : 0;
        /// <summary>The one union member of a RendezvousMessage: (field, body).</summary>
        public (int field, PbIn body) Union()
        {
            foreach (var kv in F) if (kv.Value is byte[] b) return (kv.Key, Parse(b));
            return (0, new PbIn());
        }
    }

    // ------------------------------------------------------------------ TCP framing
    public static void WriteFrame(Stream s, byte[] data)
    {
        int n = data.Length;
        byte[] h;
        if (n <= 0x3F) h = new[] { (byte)(n << 2) };
        else if (n <= 0x3FFF) { int v = (n << 2) | 1; h = new[] { (byte)v, (byte)(v >> 8) }; }
        else if (n <= 0x3FFFFF) { int v = (n << 2) | 2; h = new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16) }; }
        else { uint v = ((uint)n << 2) | 3; h = new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) }; }
        s.Write(h, 0, h.Length);
        s.Write(data, 0, data.Length);
        s.Flush();
    }

    public static byte[] ReadFrame(Stream s)
    {
        int b0 = s.ReadByte();
        if (b0 < 0) return null;
        int hl = (b0 & 3) + 1;
        uint v = (uint)b0;
        for (int i = 1; i < hl; i++) { int x = s.ReadByte(); if (x < 0) return null; v |= (uint)x << (8 * i); }
        int n = (int)(v >> 2);
        var buf = new byte[n];
        int off = 0;
        while (off < n) { int r = s.Read(buf, off, n - off); if (r <= 0) return null; off += r; }
        return buf;
    }

    /// <summary>Stream that sends each flushed write as one BytesCodec frame (works whether the relay
    /// pipes raw bytes or re-frames messages).</summary>
    public sealed class FramedStream : Stream
    {
        private readonly Stream _inner;
        private readonly TcpClient _tcp;
        private readonly MemoryStream _out = new();
        private byte[] _in = Array.Empty<byte>();
        private int _inPos;
        public FramedStream(TcpClient tcp) { _tcp = tcp; _inner = tcp.GetStream(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_inPos >= _in.Length)
            {
                var f = ReadFrame(_inner);
                if (f == null) return 0;
                _in = f; _inPos = 0;
            }
            int k = Math.Min(count, _in.Length - _inPos);
            Array.Copy(_in, _inPos, buffer, offset, k);
            _inPos += k;
            return k;
        }
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override void Flush()
        {
            if (_out.Length == 0) return;
            var data = _out.ToArray(); _out.SetLength(0);
            WriteFrame(_inner, data);
        }
        protected override void Dispose(bool disposing) { try { _tcp.Close(); } catch { } base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    public static TcpClient Tcp(string hostPort, int defPort, int timeoutMs = 6000)
    {
        var (h, p) = Split(hostPort, defPort);
        var tcp = new TcpClient();
        if (!tcp.ConnectAsync(h, p).Wait(timeoutMs) || !tcp.Connected)
        { tcp.Close(); throw new IOException($"Cannot reach {h}:{p}"); }
        tcp.NoDelay = true;
        return tcp;
    }

    /// <summary>Join the relay with a pairing uuid; the returned stream reaches the other side.</summary>
    public static Stream JoinRelay(string relay, string uuid, string key)
    {
        var tcp = Tcp(relay, RelayPort);
        var rr = new Pb().Str(2, uuid).Str(6, key);
        WriteFrame(tcp.GetStream(), Pb.Wrap(F_RequestRelay, rr));
        return new FramedStream(tcp);
    }

    /// <summary>Controller side: ask the ID server to bring <paramref name="peerId"/> onto the relay.</summary>
    public static Stream ConnectById(Config cfg, string peerId, Action<string> status)
    {
        string idSrv = IdServerOf(cfg), relay = RelayOf(cfg);
        if (idSrv == "") throw new IOException("No ID server is set in Settings.");
        string uuid = Guid.NewGuid().ToString();
        status?.Invoke($"Asking {idSrv} for {peerId}…");
        using var tcp = Tcp(idSrv, IdPort);
        var s = tcp.GetStream();
        var req = new Pb().Str(1, peerId).Str(2, uuid).Str(4, relay).Str(6, cfg.ServerKey ?? "");
        WriteFrame(s, Pb.Wrap(F_RequestRelay, req));

        tcp.ReceiveTimeout = RelayAnswerTimeoutMs;
        PbIn answer = null;
        var until = Environment.TickCount64 + RelayAnswerTimeoutMs;
        while (Environment.TickCount64 < until)
        {
            byte[] f;
            try { f = ReadFrame(s); } catch { break; }
            if (f == null) break;
            var (field, body) = PbIn.Parse(f).Union();
            if (field == F_RelayResponse) { answer = body; break; }
            // KeyExchange or anything else: keep waiting for the relay answer.
        }
        if (answer == null)
            throw new IOException($"{peerId} is not online on your ID server (it must be running Rplus with the same server).");
        string refuse = answer.Str(6);
        if (!string.IsNullOrEmpty(refuse)) throw new IOException("Refused: " + refuse);
        string ver = answer.Str(7);
        if (!ver.StartsWith("rplus", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{peerId} is not running Rplus{(ver == "" ? "" : " (" + ver + ")")}. Rplus can only control other computers running Rplus.");
        string useRelay = PickRelay(cfg, answer.Str(3));
        status?.Invoke($"Joining relay {useRelay}…");
        return JoinRelay(useRelay, uuid, cfg.ServerKey ?? "");
    }
}

/// <summary>
/// Host side: keeps this computer registered with the ID server under its ID and answers
/// relay requests by joining the relay and handing the stream to the session host.
/// </summary>
internal sealed class ServerLink
{
    private readonly Config _cfg;
    private readonly Action<Stream> _serve;
    private UdpClient _udp;
    private IPEndPoint _server;
    private volatile bool _run;
    private long _lastReply;

    public Action<string, bool?> OnState;   // text, ok(true)/bad(false)/pending(null)
    public Action OnIdChanged;

    public ServerLink(Config cfg, Action<Stream> serve) { _cfg = cfg; _serve = serve; }

    public void Start()
    {
        string idSrv = Rv.IdServerOf(_cfg);
        if (idSrv == "") { OnState?.Invoke("No ID server set — direct IP only", null); return; }
        _run = true;
        new Thread(() => Loop(idSrv)) { IsBackground = true, Name = "Rplus-rendezvous" }.Start();
    }

    public void Stop() { _run = false; try { _udp?.Close(); } catch { } }

    private void Loop(string idSrv)
    {
        OnState?.Invoke("Connecting to your ID server…", null);
        while (_run)
        {
            try
            {
                var (h, p) = Rv.Split(idSrv, Rv.IdPort);
                var addr = Dns.GetHostAddresses(h).First(a => a.AddressFamily == AddressFamily.InterNetwork);
                _server = new IPEndPoint(addr, p);
                _udp = new UdpClient(0);
                try { _udp.Client.IOControl(-1744830452 /*SIO_UDP_CONNRESET*/, new byte[] { 0 }, null); } catch { }
                new Thread(ReceiveLoop) { IsBackground = true, Name = "Rplus-rendezvous-rx" }.Start();
                long nextBad = Environment.TickCount64 + 6000;
                while (_run)
                {
                    SendRegisterPeer();
                    for (int i = 0; i < Rv.RegisterIntervalMs / 250 && _run; i++)
                    {
                        Thread.Sleep(250);
                        if (Environment.TickCount64 > nextBad && Environment.TickCount64 - Interlocked.Read(ref _lastReply) > Rv.RegisterIntervalMs * 2 + 6000)
                        {
                            OnState?.Invoke("Not ready. Please check your connection to the ID server", false);
                            nextBad = Environment.TickCount64 + Rv.RegisterIntervalMs;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                OnState?.Invoke("Not ready: " + e.Message, false);
                for (int i = 0; i < 20 && _run; i++) Thread.Sleep(250);
            }
            finally { try { _udp?.Close(); } catch { } }
        }
    }

    private void Send(byte[] b) { try { _udp?.Send(b, b.Length, _server); } catch { } }
    private void SendRegisterPeer() => Send(Rv.Pb.Wrap(Rv.F_RegisterPeer, new Rv.Pb().Str(1, _cfg.Id).Int(2, 0)));
    private void SendRegisterPk()
    {
        var pk = new Rv.Pb().Str(1, _cfg.Id).Blob(2, _cfg.MachineUuid()).Blob(3, _cfg.MachinePk());
        Send(Rv.Pb.Wrap(Rv.F_RegisterPk, pk));
    }

    private void ReceiveLoop()
    {
        var udp = _udp;
        while (_run && udp == _udp)
        {
            byte[] d; IPEndPoint from = null;
            try { d = udp.Receive(ref from); } catch { return; }
            var (field, body) = Rv.PbIn.Parse(d).Union();
            switch (field)
            {
                case Rv.F_RegisterPeerResponse:
                    Interlocked.Exchange(ref _lastReply, Environment.TickCount64);
                    if (body.Int(2) != 0) SendRegisterPk();
                    else OnState?.Invoke($"Ready — reachable by ID through {Rv.IdServerOf(_cfg)}", true);
                    break;
                case Rv.F_RegisterPkResponse:
                    Interlocked.Exchange(ref _lastReply, Environment.TickCount64);
                    long res = body.Int(1);
                    if (res == 0) { OnState?.Invoke($"Ready — reachable by ID through {Rv.IdServerOf(_cfg)}", true); }
                    else if (res == 2) // UUID_MISMATCH: this ID belongs to another machine on the server
                    {
                        _cfg.RegenerateId(); _cfg.Save(); OnIdChanged?.Invoke();
                        OnState?.Invoke("That ID was taken on the server — picked a new one", null);
                        SendRegisterPeer();
                    }
                    else if (res == 4) OnState?.Invoke("Server says: too frequent, retrying…", null);
                    else OnState?.Invoke($"Server refused registration (code {res})", false);
                    break;
                case Rv.F_RequestRelay:
                    var rr = body;
                    ThreadPool.QueueUserWorkItem(_ => AnswerRelay(rr));
                    break;
            }
        }
    }

    private void AnswerRelay(Rv.PbIn rr)
    {
        try
        {
            string uuid = rr.Str(2);
            string relay = Rv.PickRelay(_cfg, rr.Str(4));
            // Tell the ID server we're coming, so it can answer the controller.
            using (var tcp = Rv.Tcp(Rv.IdServerOf(_cfg), Rv.IdPort))
            {
                var resp = new Rv.Pb().Blob(1, rr.Bytes(3)).Str(2, uuid).Str(3, relay).Str(4, _cfg.Id).Str(7, Rv.Version);
                Rv.WriteFrame(tcp.GetStream(), Rv.Pb.Wrap(Rv.F_RelayResponse, resp));
                Thread.Sleep(300);
            }
            var stream = Rv.JoinRelay(relay, uuid, _cfg.ServerKey ?? "");
            _serve(stream);
        }
        catch (Exception e) { OnState?.Invoke("Relay failed: " + e.Message, false); }
    }
}
