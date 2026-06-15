using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using gatOS.NineP.Protocol;

namespace gatOS.NineP.Tests.TestClient;

/// <summary>
///     A managed 9P2000.L client speaking the production codec (OS_PLAN.md T7.4): the
///     conformance suite's instrument, also reused by <c>gatOS.SimFs.Tests</c>. Supports
///     multiple outstanding requests (tag-correlated), which the Tflush tests require.
/// </summary>
public sealed class NinePTestClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Task _readLoop;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<Response>> _pending = new();
    private readonly ConcurrentQueue<(MessageType Type, ushort Tag)> _responseLog = new();
    private int _nextTag;

    private NinePTestClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>One parsed response frame.</summary>
    public readonly record struct Response(MessageType Type, byte[] Body);

    /// <summary>A parsed Rreaddir entry.</summary>
    public readonly record struct DirEntry(Qid Qid, ulong Cookie, byte Type, string Name);

    /// <summary>The fields of one Rgetattr.</summary>
    public sealed record Attrs(ulong Valid, Qid Qid, uint Mode, uint Uid, uint Gid, ulong Nlink,
        ulong Size, ulong Blksize, ulong Blocks, ulong AtimeSec, ulong MtimeSec, ulong CtimeSec);

    /// <summary>Every response received, in arrival order (for flush-ordering assertions).</summary>
    public IReadOnlyCollection<(MessageType Type, ushort Tag)> ResponseLog => _responseLog;

    /// <summary>Connects to a server on loopback.</summary>
    public static async Task<NinePTestClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        tcp.NoDelay = true;
        return new NinePTestClient(tcp);
    }

    /// <summary>Negotiates the protocol version.</summary>
    public async Task<(uint Msize, string Version)> VersionAsync(uint msize = 65536, string version = "9P2000.L")
    {
        var body = await RequestAsync(MessageType.Tversion, MessageType.Rversion,
            w => w.WriteUInt32(msize).WriteString(version));
        var reader = new NinePReader(body);
        return (reader.ReadUInt32(), reader.ReadString());
    }

    /// <summary>Attaches a fid to the root.</summary>
    public async Task<Qid> AttachAsync(uint fid)
    {
        var body = await RequestAsync(MessageType.Tattach, MessageType.Rattach,
            w => w.WriteUInt32(fid).WriteUInt32(uint.MaxValue).WriteString("root").WriteString("")
                .WriteUInt32(0));
        return new NinePReader(body).ReadQid();
    }

    /// <summary>Walks names from fid to newfid; returns the qids that resolved (may be partial).</summary>
    public async Task<Qid[]> WalkAsync(uint fid, uint newfid, params string[] names)
    {
        var body = await RequestAsync(MessageType.Twalk, MessageType.Rwalk, w =>
        {
            w.WriteUInt32(fid).WriteUInt32(newfid).WriteUInt16((ushort)names.Length);
            foreach (var name in names)
                w.WriteString(name);
        });
        var reader = new NinePReader(body);
        var count = reader.ReadUInt16();
        var qids = new Qid[count];
        for (var i = 0; i < count; i++)
            qids[i] = reader.ReadQid();
        return qids;
    }

    /// <summary>Opens a fid with Linux open flags.</summary>
    public async Task<Qid> LopenAsync(uint fid, uint flags = 0)
    {
        var body = await RequestAsync(MessageType.Tlopen, MessageType.Rlopen,
            w => w.WriteUInt32(fid).WriteUInt32(flags));
        return new NinePReader(body).ReadQid();
    }

    /// <summary>Reads the basic attributes of a fid.</summary>
    public async Task<Attrs> GetattrAsync(uint fid)
    {
        var body = await RequestAsync(MessageType.Tgetattr, MessageType.Rgetattr,
            w => w.WriteUInt32(fid).WriteUInt64(0x7FF));
        var r = new NinePReader(body);
        var valid = r.ReadUInt64();
        var qid = r.ReadQid();
        var mode = r.ReadUInt32();
        var uid = r.ReadUInt32();
        var gid = r.ReadUInt32();
        var nlink = r.ReadUInt64();
        _ = r.ReadUInt64(); // rdev
        var size = r.ReadUInt64();
        var blksize = r.ReadUInt64();
        var blocks = r.ReadUInt64();
        var atime = r.ReadUInt64();
        _ = r.ReadUInt64();
        var mtime = r.ReadUInt64();
        _ = r.ReadUInt64();
        var ctime = r.ReadUInt64();
        return new Attrs(valid, qid, mode, uid, gid, nlink, size, blksize, blocks, atime, mtime, ctime);
    }

    /// <summary>Reads file bytes.</summary>
    public async Task<byte[]> ReadAsync(uint fid, ulong offset, uint count)
    {
        var (_, task) = BeginRead(fid, offset, count);
        return await task;
    }

    /// <summary>
    ///     Starts a Tread without awaiting it, returning the wire tag (for <see cref="FlushAsync"/>)
    ///     and the eventual data task. The task faults with <see cref="NinePErrorException"/> on
    ///     Rlerror and stays pending forever if the server (correctly) suppresses a flushed reply.
    /// </summary>
    public (ushort Tag, Task<byte[]> Data) BeginRead(uint fid, ulong offset, uint count)
    {
        var (tag, response) = BeginRequest(MessageType.Tread,
            w => w.WriteUInt32(fid).WriteUInt64(offset).WriteUInt32(count));
        return (tag, Decode());

        async Task<byte[]> Decode()
        {
            var body = Expect(await response, MessageType.Rread);
            var reader = new NinePReader(body);
            var length = reader.ReadUInt32();
            return reader.ReadBytes((int)length).ToArray();
        }
    }

    /// <summary>Reads the full content of a fid by issuing sequential reads to EOF.</summary>
    public async Task<byte[]> ReadToEndAsync(uint fid, uint chunk = 8192)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            var data = await ReadAsync(fid, (ulong)buffer.Length, chunk);
            if (data.Length == 0)
                return buffer.ToArray();
            buffer.Write(data);
        }
    }

    /// <summary>Reads directory entries at the given cookie offset.</summary>
    public async Task<DirEntry[]> ReaddirAsync(uint fid, ulong offset, uint count = 8192)
    {
        var body = await RequestAsync(MessageType.Treaddir, MessageType.Rreaddir,
            w => w.WriteUInt32(fid).WriteUInt64(offset).WriteUInt32(count));
        var reader = new NinePReader(body);
        var payload = reader.ReadUInt32();
        var entries = new List<DirEntry>();
        var data = new NinePReader(reader.ReadBytes((int)payload));
        while (data.Remaining > 0)
            entries.Add(new DirEntry(data.ReadQid(), data.ReadUInt64(), data.ReadByte(), data.ReadString()));
        return [.. entries];
    }

    /// <summary>Lists a whole directory by paging from cookie 0.</summary>
    public async Task<DirEntry[]> ReaddirAllAsync(uint fid, uint count = 8192)
    {
        var all = new List<DirEntry>();
        ulong cookie = 0;
        while (true)
        {
            var page = await ReaddirAsync(fid, cookie, count);
            if (page.Length == 0)
                return [.. all];
            all.AddRange(page);
            cookie = page[^1].Cookie;
        }
    }

    /// <summary>Writes file bytes (expected to fail against the read-only tree).</summary>
    public async Task<uint> WriteAsync(uint fid, ulong offset, byte[] data)
    {
        var body = await RequestAsync(MessageType.Twrite, MessageType.Rwrite,
            w => w.WriteUInt32(fid).WriteUInt64(offset).WriteUInt32((uint)data.Length).WriteBytes(data));
        return new NinePReader(body).ReadUInt32();
    }

    /// <summary>Creates a file in a directory fid and rebinds that fid to the new open file (Tlcreate).</summary>
    public async Task<Qid> LcreateAsync(uint fid, string name, uint flags = 1, uint mode = 0x1A4, uint gid = 0)
    {
        var body = await RequestAsync(MessageType.Tlcreate, MessageType.Rlcreate,
            w => w.WriteUInt32(fid).WriteString(name).WriteUInt32(flags).WriteUInt32(mode).WriteUInt32(gid));
        return new NinePReader(body).ReadQid();
    }

    /// <summary>Creates a directory in a directory fid (Tmkdir).</summary>
    public async Task<Qid> MkdirAsync(uint dfid, string name, uint mode = 0x1ED, uint gid = 0)
    {
        var body = await RequestAsync(MessageType.Tmkdir, MessageType.Rmkdir,
            w => w.WriteUInt32(dfid).WriteString(name).WriteUInt32(mode).WriteUInt32(gid));
        return new NinePReader(body).ReadQid();
    }

    /// <summary>Removes a child of a directory fid (Tunlinkat); flags carries AT_REMOVEDIR (0x200).</summary>
    public Task UnlinkatAsync(uint dfid, string name, uint flags = 0)
        => RequestAsync(MessageType.Tunlinkat, MessageType.Runlinkat,
            w => w.WriteUInt32(dfid).WriteString(name).WriteUInt32(flags));

    /// <summary>Moves a child between two directory fids (Trenameat).</summary>
    public Task RenameatAsync(uint oldDirFid, string oldName, uint newDirFid, string newName)
        => RequestAsync(MessageType.Trenameat, MessageType.Rrenameat,
            w => w.WriteUInt32(oldDirFid).WriteString(oldName).WriteUInt32(newDirFid).WriteString(newName));

    /// <summary>Truncates/extends an open fid to <paramref name="size"/> bytes (Tsetattr ATTR_SIZE).</summary>
    public Task SetattrSizeAsync(uint fid, ulong size)
        => RequestAsync(MessageType.Tsetattr, MessageType.Rsetattr, w => w
            .WriteUInt32(fid)
            .WriteUInt32(0x8)            // valid = P9_SETATTR_SIZE
            .WriteUInt32(0).WriteUInt32(0).WriteUInt32(0) // mode, uid, gid
            .WriteUInt64(size)          // size
            .WriteUInt64(0).WriteUInt64(0) // atime sec/nsec
            .WriteUInt64(0).WriteUInt64(0)); // mtime sec/nsec

    /// <summary>Releases a fid.</summary>
    public Task ClunkAsync(uint fid)
        => RequestAsync(MessageType.Tclunk, MessageType.Rclunk, w => w.WriteUInt32(fid));

    /// <summary>Flushes an outstanding request by its wire tag.</summary>
    public Task FlushAsync(ushort oldtag)
        => RequestAsync(MessageType.Tflush, MessageType.Rflush, w => w.WriteUInt16(oldtag));

    /// <summary>Statfs over a fid.</summary>
    public async Task<uint> StatfsTypeAsync(uint fid)
    {
        var body = await RequestAsync(MessageType.Tstatfs, MessageType.Rstatfs, w => w.WriteUInt32(fid));
        return new NinePReader(body).ReadUInt32();
    }

    /// <summary>Sends an arbitrary T-message and returns the raw response.</summary>
    public async Task<Response> SendRawAsync(MessageType type, Action<NinePWriter>? build = null)
    {
        var (_, response) = BeginRequest(type, build ?? (_ => { }));
        return await response;
    }

    /// <summary>
    ///     Sends a T-message with a caller-chosen wire tag and awaits its reply. Lets a test
    ///     deliberately reuse a tag the instant the previous request with it completed — 9p
    ///     permits exactly that, and the server must free a tag before writing its reply so the
    ///     reuse never collides with the just-answered request (regression for the
    ///     tag-reused-while-in-flight teardown that undercounted <c>find /sim</c>).
    /// </summary>
    public async Task<Response> RequestWithTagAsync(ushort tag, MessageType type, Action<NinePWriter>? build = null)
    {
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[tag] = tcs;
        var writer = new NinePWriter().Begin(type, tag);
        build?.Invoke(writer);
        var frame = writer.Frame();
        await _writeLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(frame);
        }
        finally
        {
            _writeLock.Release();
        }

        return await tcs.Task;
    }

    /// <summary>Writes raw bytes straight onto the socket (malformed-frame tests).</summary>
    public async Task SendRawBytesAsync(byte[] bytes)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(bytes);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Completes when the server closes the connection.</summary>
    public Task ConnectionClosed => _readLoop;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _tcp.Close();
        try
        {
            await _readLoop;
        }
        catch
        {
            // Teardown noise is irrelevant.
        }
    }

    private async Task<byte[]> RequestAsync(MessageType type, MessageType expected, Action<NinePWriter> build)
    {
        var (_, response) = BeginRequest(type, build);
        return Expect(await response, expected);
    }

    private (ushort Tag, Task<Response> Response) BeginRequest(MessageType type, Action<NinePWriter> build)
    {
        var tag = type == MessageType.Tversion
            ? (ushort)0xFFFF // NOTAG
            : (ushort)(Interlocked.Increment(ref _nextTag) & 0xFFFF);
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[tag] = tcs;

        var writer = new NinePWriter().Begin(type, tag);
        build(writer);
        var frame = writer.Frame();
        _ = Task.Run(async () =>
        {
            await _writeLock.WaitAsync();
            try
            {
                await _stream.WriteAsync(frame);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                _writeLock.Release();
            }
        });
        return (tag, tcs.Task);
    }

    private static byte[] Expect(Response response, MessageType expected)
    {
        if (response.Type == MessageType.Rlerror)
            throw new NinePErrorException(new NinePReader(response.Body).ReadUInt32());
        if (response.Type != expected)
            throw new InvalidOperationException($"expected {expected}, got {response.Type}");
        return response.Body;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var header = new byte[4];
            while (true)
            {
                await _stream.ReadExactlyAsync(header);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
                var frame = new byte[size - 4];
                await _stream.ReadExactlyAsync(frame);
                var type = (MessageType)frame[0];
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1, 2));
                _responseLog.Enqueue((type, tag));
                if (_pending.TryRemove(tag, out var tcs))
                    tcs.TrySetResult(new Response(type, frame[3..]));
            }
        }
        catch (Exception ex)
        {
            foreach (var tag in _pending.Keys.ToArray())
                if (_pending.TryRemove(tag, out var tcs))
                    tcs.TrySetException(new IOException("connection closed", ex));
        }
    }
}

/// <summary>An <c>Rlerror</c> from the server, carrying the Linux errno.</summary>
public sealed class NinePErrorException : Exception
{
    /// <param name="errno">The Linux errno from the Rlerror.</param>
    public NinePErrorException(uint errno)
        : base($"Rlerror: errno {errno}")
    {
        Errno = errno;
    }

    /// <summary>The Linux errno.</summary>
    public uint Errno { get; }
}
