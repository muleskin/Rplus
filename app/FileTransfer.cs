using System.Text.Json;

namespace Rplus;

/// <summary>
/// Chunked file transfer, shared by both sides.
///
/// The side that has the file sends XferStart { id, name, size, path }, then XferChunk blobs
/// ([4-byte id][up to 256 KB of data]), then XferEnd { id, ok, error }. The receiving side writes
/// to "&lt;destination&gt;.part" and renames it only when every byte has arrived, then answers with
/// XferResult { id, ok, error }. Either side can send XferCancel { id }.
///
/// Uploads: the controller sends, the host receives (path = destination on the host).
/// Downloads: the controller asks with FileGet { path, id }; the host sends, the controller receives.
/// The controller numbers every transfer, so ids never clash.
/// </summary>
internal static class FileXfer
{
    public const int ChunkSize = 256 * 1024;

    public sealed class StartBody { public int id { get; set; } public string name { get; set; } = ""; public long size { get; set; } public string path { get; set; } = ""; }
    public sealed class EndBody { public int id { get; set; } public bool ok { get; set; } public string error { get; set; } = ""; }
    public sealed class IdBody { public int id { get; set; } }

    /// <summary>Sends one file in chunks. Returns "" when every chunk was sent, else the reason it stopped.
    /// Runs on the caller's thread; the transport's write lock lets screen frames go out in between.</summary>
    public static string Send(Transport t, int id, string srcPath, string destPath, CancellationToken cancel,
                              Action<long, long> progress)
    {
        FileStream fs;
        long size;
        try { fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize); size = fs.Length; }
        catch (Exception e)
        {
            string why = "Can't open " + Path.GetFileName(srcPath) + ": " + e.Message;
            t.Send(Msg.Control(MsgType.XferEnd, new EndBody { id = id, ok = false, error = why }));
            return why;
        }
        using (fs)
        {
            t.Send(Msg.Control(MsgType.XferStart, new StartBody { id = id, name = Path.GetFileName(srcPath), size = size, path = destPath ?? "" }));
            var buf = new byte[4 + ChunkSize];
            buf[0] = (byte)(id >> 24); buf[1] = (byte)(id >> 16); buf[2] = (byte)(id >> 8); buf[3] = (byte)id;
            long sent = 0;
            progress?.Invoke(0, size);
            while (sent < size)
            {
                if (cancel.IsCancellationRequested)
                {
                    t.Send(Msg.Control(MsgType.XferEnd, new EndBody { id = id, ok = false, error = "Cancelled" }));
                    return "Cancelled";
                }
                if (!t.Connected) return "Connection lost";
                int n;
                try { n = fs.Read(buf, 4, ChunkSize); }
                catch (Exception e)
                {
                    string why = "Read failed: " + e.Message;
                    t.Send(Msg.Control(MsgType.XferEnd, new EndBody { id = id, ok = false, error = why }));
                    return why;
                }
                if (n <= 0) break;
                var blob = n == ChunkSize ? buf : buf.AsSpan(0, 4 + n).ToArray();
                t.Send(new Msg { Type = MsgType.XferChunk, Blob = blob });
                sent += n;
                progress?.Invoke(sent, size);
            }
            if (!t.Connected) return "Connection lost";
            t.Send(Msg.Control(MsgType.XferEnd, new EndBody { id = id, ok = true }));
            return "";
        }
    }

    public static int ChunkId(byte[] blob) =>
        blob.Length >= 4 ? (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3] : -1;
}

/// <summary>Files arriving in chunks: written to "&lt;dest&gt;.part", renamed when complete.</summary>
internal sealed class FileReceiver
{
    private sealed class Incoming
    {
        public string Dest = "", Part = "", Name = "";
        public FileStream Stream;
        public long Size, Got;
    }

    private readonly Dictionary<int, Incoming> _open = new();

    /// <summary>Opens the .part file. Returns "" or the reason it couldn't.</summary>
    public string Start(int id, string name, string destPath, long size)
    {
        Abort(id);
        try
        {
            var inc = new Incoming { Dest = destPath, Part = destPath + ".part", Name = name, Size = size };
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            inc.Stream = new FileStream(inc.Part, FileMode.Create, FileAccess.Write, FileShare.None, FileXfer.ChunkSize);
            lock (_open) _open[id] = inc;
            return "";
        }
        catch (Exception e) { return "Can't write " + name + ": " + e.Message; }
    }

    /// <summary>Writes one chunk. Returns (bytes so far, total) or null if the id is unknown or the write failed.</summary>
    public (long got, long size, string name)? Chunk(byte[] blob)
    {
        int id = FileXfer.ChunkId(blob);
        Incoming inc;
        lock (_open) _open.TryGetValue(id, out inc);
        if (inc == null) return null;
        try
        {
            inc.Stream.Write(blob, 4, blob.Length - 4);
            inc.Got += blob.Length - 4;
            return (inc.Got, inc.Size, inc.Name);
        }
        catch { Abort(id); return null; }
    }

    /// <summary>The sender is done. Returns "" when the file is complete and in place, else the reason.</summary>
    public string Finish(int id, bool senderOk, string senderError)
    {
        Incoming inc;
        lock (_open) { _open.TryGetValue(id, out inc); _open.Remove(id); }
        if (inc == null) return string.IsNullOrEmpty(senderError) ? "Transfer not found" : senderError;
        try { inc.Stream.Dispose(); } catch { }
        if (!senderOk) { TryDelete(inc.Part); return string.IsNullOrEmpty(senderError) ? "Stopped by the other side" : senderError; }
        if (inc.Got != inc.Size) { TryDelete(inc.Part); return $"Incomplete: got {inc.Got} of {inc.Size} bytes"; }
        try { File.Move(inc.Part, inc.Dest, overwrite: true); return ""; }
        catch (Exception e) { TryDelete(inc.Part); return "Can't save " + inc.Name + ": " + e.Message; }
    }

    /// <summary>Drops a transfer and deletes its .part file.</summary>
    public void Abort(int id)
    {
        Incoming inc;
        lock (_open) { _open.TryGetValue(id, out inc); _open.Remove(id); }
        if (inc == null) return;
        try { inc.Stream.Dispose(); } catch { }
        TryDelete(inc.Part);
    }

    public void AbortAll()
    {
        List<int> ids;
        lock (_open) ids = _open.Keys.ToList();
        foreach (var id in ids) Abort(id);
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
