using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using gatOS.Logging;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.NineP.Server;

/// <summary>
///     One 9p connection: frames messages off the socket, dispatches each as its own task
///     (so a parked blocking read never stalls the loop — pipelining and <c>Tflush</c> keep
///     working), and serializes responses through a write lock (OS_PLAN.md T7.3/T7.4).
/// </summary>
/// <remarks>
///     Wire behaviors follow spike/NOTES.md T1.2 where it and the plan disagree: readdir
///     includes <c>.</c> and <c>..</c>, the dirent offset cookie is the ordinal of the
///     <i>next</i> entry, and a flushed request's tag is never answered after <c>Rflush</c>.
/// </remarks>
internal sealed class Session
{
    private const int MaxFrameBytes = 1 << 20;
    private const int HeaderBytes = 7;

    /// <summary>Rread header overhead: size[4] type[1] tag[2] count[4].</summary>
    private const uint ReadOverhead = 11;

    private const uint ModeDirectory = 0x4000 | 0x1ED;     // S_IFDIR | 0755
    private const uint ModeFile = 0x8000 | 0x124;          // S_IFREG | 0444 (read-only sensor)
    private const uint ModeFileWritable = 0x8000 | 0x1A4;  // S_IFREG | 0644 (control file)

    // Linux open-flag bits we care about (the access mode lives in the low two bits).
    private const uint OAccmode = 3;
    private const uint OWronly = 1;
    private const uint ORdwr = 2;
    private const uint OTrunc = 0x200;                  // O_TRUNC — truncate on open
    private const ulong GetattrBasic = 0x7FF;          // P9_GETATTR_BASIC
    private const uint SetattrSize = 0x8;              // P9_SETATTR_SIZE — the ftruncate path
    private const uint AtRemovedir = 0x200;           // AT_REMOVEDIR — Tunlinkat targets a directory
    private const byte DtDir = 4;
    private const byte DtReg = 8;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly VfsDirectory _root;
    private readonly NinePServerOptions _options;
    private readonly DateTimeOffset _attrTime;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, FidEntry> _fids = new();
    private readonly ConcurrentDictionary<ushort, InFlight> _inFlight = new();
    private readonly NinePServerStats _stats;
    private volatile uint _msize;

    internal Session(TcpClient client, VfsDirectory root, NinePServerOptions options,
        DateTimeOffset attrTime, NinePServerStats stats, CancellationToken serverToken)
    {
        _client = client;
        _stream = client.GetStream();
        _root = root;
        _options = options;
        _attrTime = attrTime;
        _stats = stats;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        _msize = options.MaxMsize;
    }

    /// <summary>Runs the read loop until the peer disconnects, a frame is malformed, or the server stops.</summary>
    internal async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            var header = new byte[4];
            while (!ct.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (size is < HeaderBytes or > MaxFrameBytes)
                    throw new ProtocolException($"frame size {size} outside [{HeaderBytes}, {MaxFrameBytes}]");

                // The frame body is rented (P4): a video-rate mount moves hundreds of messages a
                // second and a fresh array per message was steady garbage. Ownership passes to the
                // Dispatch task, whose finally returns it once the handler completes.
                var frameLength = (int)size - 4;
                var frame = ArrayPool<byte>.Shared.Rent(frameLength);
                try
                {
                    await _stream.ReadExactlyAsync(frame.AsMemory(0, frameLength), ct).ConfigureAwait(false);
                    Dispatch((MessageType)frame[0],
                        BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1, 2)), frame, frameLength);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(frame);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutdown.
        }
        catch (EndOfStreamException)
        {
            ModLog.Log.Debug("9p session: peer disconnected");
        }
        catch (ProtocolException ex)
        {
            ModLog.Log.Warn($"9p session: malformed frame, closing connection: {ex.Message}");
        }
        catch (IOException ex)
        {
            ModLog.Log.Debug($"9p session: connection error: {ex.Message}");
        }
        finally
        {
            Teardown();
        }
    }

    /// <summary>Cancels everything and closes the socket (idempotent).</summary>
    internal void Teardown()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (var fid in _fids.Keys.ToArray())
            if (_fids.TryRemove(fid, out var entry))
                entry.DisposeHandle();
        _client.Close();
    }

    private void Dispatch(MessageType type, ushort tag, byte[] frame, int frameLength)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var inFlight = new InFlight(cts, tag);
        if (!_inFlight.TryAdd(tag, inFlight))
        {
            // A tag may not be reused while outstanding; treat as a broken peer. The caller's
            // catch returns the rented frame.
            cts.Dispose();
            throw new ProtocolException($"tag {tag} reused while in flight");
        }

        inFlight.Task = Task.Run(async () =>
        {
            try
            {
                // The body view (after type[1] tag[2]) stays valid for the handler's whole run —
                // the rented frame is returned only in the finally below.
                await HandleAsync(type, tag, frame.AsMemory(3, frameLength - 3), inFlight, cts.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                // Backstop only: SendAsync frees the tag before the reply is written (see
                // there). This still covers the suppressed-reply and exception paths.
                _inFlight.TryRemove(tag, out _);
                cts.Dispose();
                ArrayPool<byte>.Shared.Return(frame);
            }
        });
    }

    private async Task HandleAsync(MessageType type, ushort tag, ReadOnlyMemory<byte> body, InFlight inFlight,
        CancellationToken ct)
    {
        try
        {
            var reply = type switch
            {
                MessageType.Tversion => HandleVersion(body, tag),
                MessageType.Tattach => HandleAttach(body, tag),
                MessageType.Twalk => HandleWalk(body, tag),
                MessageType.Tlopen => HandleLopen(body, tag),
                MessageType.Tgetattr => HandleGetattr(body, tag),
                MessageType.Treaddir => HandleReaddir(body, tag),
                MessageType.Tread => await HandleReadAsync(body, tag, ct).ConfigureAwait(false),
                MessageType.Twrite => await HandleWriteAsync(body, tag, ct).ConfigureAwait(false),
                MessageType.Tlcreate => HandleLcreate(body, tag),
                MessageType.Tmkdir => HandleMkdir(body, tag),
                MessageType.Tunlinkat => HandleUnlinkat(body, tag),
                MessageType.Trenameat => HandleRenameat(body, tag),
                MessageType.Tsetattr => HandleSetattr(body, tag),
                MessageType.Tfsync => HandleFsync(body, tag),
                MessageType.Tclunk => HandleClunk(body, tag),
                MessageType.Tstatfs => HandleStatfs(body, tag),
                MessageType.Tflush => await HandleFlushAsync(body, tag).ConfigureAwait(false),
                _ => Error(tag, LinuxErrno.EOPNOTSUPP),
            };
            await SendAsync(reply, inFlight).ConfigureAwait(false);
            reply.Return(); // reply bytes are on the wire (or suppressed) — recycle the buffer
        }
        catch (OperationCanceledException)
        {
            // Flushed (the Tflush handler owns the Rflush) or session teardown: no reply.
        }
        catch (VfsErrorException ex)
        {
            var error = Error(tag, ex.Errno);
            await SendAsync(error, inFlight).ConfigureAwait(false);
            error.Return();
        }
        catch (ProtocolException ex)
        {
            ModLog.Log.Warn($"9p session: malformed {type}, closing connection: {ex.Message}");
            Teardown();
        }
        catch (Exception ex) when (ex is not IOException and not ObjectDisposedException)
        {
            ModLog.Log.Error($"9p session: {type} handler failed", ex);
            var error = Error(tag, LinuxErrno.EIO);
            await SendAsync(error, inFlight).ConfigureAwait(false);
            error.Return();
        }
    }

    private async Task SendAsync(NinePWriter reply, InFlight inFlight)
    {
        // A flushed request must never be answered after its Rflush (flush(5); spike T1.2).
        // The Tflush handler sends its Rflush only after awaiting this task, so leaving the
        // tag for the Dispatch finally to reap is correct on this suppressed path.
        if (inFlight.Flushed)
            return;

        // Free the tag BEFORE the reply bytes can reach the client. 9p lets a client reuse a
        // tag the instant it receives the reply, so a tag still in _inFlight when the reply is
        // written races the next request's Dispatch — TryAdd fails, "tag reused while in
        // flight" tears the whole connection down (a load-dependent flake under e.g. `find`,
        // which recycles tags rapidly). This TryRemove is sequenced before WriteAsync, so the
        // tag is gone before any byte is sent and the reuse can never collide.
        _inFlight.TryRemove(inFlight.Tag, out _);

        await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _stream.WriteAsync(reply.Frame(), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The read loop notices the dead connection and tears down.
        }
        finally
        {
            _writeLock.Release();
            _stats.RecordSend(Stopwatch.GetTimestamp() - started);
        }
    }

    private static NinePWriter Error(ushort tag, uint errno)
        => new NinePWriter().Begin(MessageType.Rlerror, tag).WriteUInt32(errno);

    // ---- handlers ------------------------------------------------------------------------

    private NinePWriter HandleVersion(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var clientMsize = reader.ReadUInt32();
        var version = reader.ReadString();
        if (clientMsize < 4096)
            throw new ProtocolException($"client msize {clientMsize} is unusably small");

        // Tversion resets the session: abandon all fids and in-flight requests (T7.3).
        foreach (var fid in _fids.Keys.ToArray())
            if (_fids.TryRemove(fid, out var entry))
                entry.DisposeHandle();
        foreach (var (otherTag, other) in _inFlight)
        {
            if (otherTag == tag)
                continue;
            try
            {
                other.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _msize = Math.Min(clientMsize, _options.MaxMsize);
        var accepted = version == "9P2000.L";
        return new NinePWriter().Begin(MessageType.Rversion, tag)
            .WriteUInt32(_msize)
            .WriteString(accepted ? "9P2000.L" : "unknown");
    }

    private NinePWriter HandleAttach(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var fid = reader.ReadUInt32();
        _ = reader.ReadUInt32();  // afid — no auth
        _ = reader.ReadString();  // uname
        _ = reader.ReadString();  // aname
        var entry = new FidEntry([_root]);
        if (!_fids.TryAdd(fid, entry))
            throw new VfsErrorException(LinuxErrno.EINVAL, $"attach: fid {fid} already in use");
        return new NinePWriter().Begin(MessageType.Rattach, tag).WriteQid(Qid.ForNode(_root));
    }

    private NinePWriter HandleWalk(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var fid = reader.ReadUInt32();
        var newfid = reader.ReadUInt32();
        var count = reader.ReadUInt16();
        if (count > 16)
            throw new VfsErrorException(LinuxErrno.EINVAL, $"walk: {count} names exceeds the limit of 16");
        var names = new string[count];
        for (var i = 0; i < count; i++)
            names[i] = reader.ReadString();

        var entry = RequireFid(fid);
        var path = new List<VfsNode>(entry.Path);
        var qids = new List<Qid>(count);
        foreach (var name in names)
        {
            VfsNode? next;
            if (name == "..")
            {
                if (path.Count > 1)
                    path.RemoveAt(path.Count - 1);
                next = path[^1]; // ".." at root is root
            }
            else if (path[^1] is VfsDirectory dir)
            {
                try
                {
                    next = dir.Lookup(name);
                }
                catch (VfsErrorException) when (qids.Count > 0)
                {
                    break; // partial success
                }

                if (next is null)
                {
                    if (qids.Count == 0)
                        throw new VfsErrorException(LinuxErrno.ENOENT, $"walk: '{name}' not found");
                    break; // partial success
                }

                path.Add(next);
            }
            else
            {
                if (qids.Count == 0)
                    throw new VfsErrorException(LinuxErrno.ENOTDIR, $"walk: '{path[^1].Name}' is not a directory");
                break; // partial success
            }

            qids.Add(Qid.ForNode(next));
        }

        // newfid is bound only when every name resolved (T7.4 table).
        if (qids.Count == count)
        {
            var bound = new FidEntry(path);
            if (newfid == fid)
            {
                RequireFid(fid).DisposeHandle();
                _fids[fid] = bound;
            }
            else if (!_fids.TryAdd(newfid, bound))
            {
                throw new VfsErrorException(LinuxErrno.EINVAL, $"walk: newfid {newfid} already in use");
            }
        }

        var writer = new NinePWriter().Begin(MessageType.Rwalk, tag).WriteUInt16((ushort)qids.Count);
        foreach (var qid in qids)
            writer.WriteQid(qid);
        return writer;
    }

    private NinePWriter HandleLopen(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var fid = reader.ReadUInt32();
        var flags = reader.ReadUInt32();
        var entry = RequireFid(fid);
        lock (entry)
        {
            if (entry.Opened)
                throw new VfsErrorException(LinuxErrno.EINVAL, $"lopen: fid {fid} is already open");
            if (entry.Node is VfsFile file)
            {
                var access = flags & OAccmode;
                var wantsWrite = access is OWronly or ORdwr;
                var wantsRead = access != OWronly; // O_RDONLY or O_RDWR
                if (wantsWrite && !file.IsWritable)
                    throw new VfsErrorException(LinuxErrno.EACCES, $"lopen: '{file.Name}' is read-only");
                if (wantsRead)
                    entry.Handle = file.Open();
                if (wantsWrite)
                {
                    entry.WriteHandle = file.OpenWrite();
                    // O_TRUNC zeroes the file on open (host mounts); control files are length-free
                    // and their handle's SetLength is a no-op, so this is harmless for them.
                    if ((flags & OTrunc) != 0)
                        entry.WriteHandle.SetLength(0);
                }
            }

            entry.Opened = true;
        }

        return new NinePWriter().Begin(MessageType.Rlopen, tag)
            .WriteQid(Qid.ForNode(entry.Node))
            .WriteUInt32(0); // iounit: 0 = use msize-derived chunks
    }

    private NinePWriter HandleGetattr(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var fid = reader.ReadUInt32();
        _ = reader.ReadUInt64(); // request_mask — we always answer the basic set
        var entry = RequireFid(fid);
        var node = entry.Node;

        long size = 0;
        var mode = ModeDirectory;
        if (node is VfsFile file)
        {
            var handle = entry.Handle;
            size = handle?.Size ?? file.Size;
            // Writable control files must advertise 0644: the kernel pre-checks write permission
            // from getattr, so without the write bit `echo` fails before any Twrite reaches us.
            mode = file.IsWritable ? ModeFileWritable : ModeFile;
        }

        // Host-passthrough nodes report their real on-disk mtime; synthetic nodes return -1 and
        // fall back to the server's fixed AttrTime (keeps the golden frames deterministic).
        var seconds = node.ModifiedUnixSeconds is >= 0 and var nodeTime
            ? (ulong)nodeTime
            : (ulong)_attrTime.ToUnixTimeSeconds();
        return new NinePWriter().Begin(MessageType.Rgetattr, tag)
            .WriteUInt64(GetattrBasic)                                   // valid
            .WriteQid(Qid.ForNode(node))                                 // qid
            .WriteUInt32(mode)                                           // mode
            .WriteUInt32(0).WriteUInt32(0)                               // uid gid
            .WriteUInt64(1)                                              // nlink
            .WriteUInt64(0)                                              // rdev
            .WriteUInt64((ulong)size)                                    // size
            .WriteUInt64(4096)                                           // blksize
            .WriteUInt64((ulong)((size + 511) / 512))                    // blocks (512 B units)
            .WriteUInt64(seconds).WriteUInt64(0)                         // atime
            .WriteUInt64(seconds).WriteUInt64(0)                         // mtime
            .WriteUInt64(seconds).WriteUInt64(0)                         // ctime
            .WriteUInt64(0).WriteUInt64(0)                               // btime (not in valid mask)
            .WriteUInt64(0)                                              // gen
            .WriteUInt64(0);                                             // data_version
    }

    private NinePWriter HandleReaddir(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var fid = reader.ReadUInt32();
        var offset = reader.ReadUInt64();
        var count = reader.ReadUInt32();
        var entry = RequireFid(fid);
        if (entry.Node is not VfsDirectory dir)
            throw new VfsErrorException(LinuxErrno.ENOTDIR, $"readdir: fid {fid} is not a directory");

        IReadOnlyList<VfsNode> listing;
        lock (entry)
        {
            if (!entry.Opened)
                throw new VfsErrorException(LinuxErrno.EBADF, $"readdir: fid {fid} is not open");

            // Snapshot the listing when (re)starting so paging stays consistent for dynamic
            // directories; "." and ".." are included (spike T1.2 — the plan's "kernel
            // synthesizes them" note was wrong).
            if (offset == 0 || entry.DirListing is null)
            {
                var children = dir.List();
                var listed = new List<VfsNode>(children.Count + 2)
                {
                    new DotAlias(".", dir),
                    new DotAlias("..", entry.Path.Count > 1 ? entry.Path[^2] : dir),
                };
                listed.AddRange(children);
                entry.DirListing = listed;
            }

            listing = entry.DirListing;
        }

        var budget = Math.Min(count, _msize - ReadOverhead);
        var writer = new NinePWriter().Begin(MessageType.Rreaddir, tag);
        writer.WriteUInt32(0); // count, patched below
        var payloadStart = writer.Length;
        for (var i = (int)offset; i < listing.Count; i++)
        {
            var node = listing[i];
            var entryBytes = Qid.WireSize + 8 + 1 + 2 + System.Text.Encoding.UTF8.GetByteCount(node.Name);
            if (writer.Length - payloadStart + entryBytes > budget)
                break;
            writer.WriteQid(Qid.ForNode(node))
                .WriteUInt64((ulong)(i + 1))                          // cookie = ordinal of the NEXT entry
                .WriteByte(node.IsDirectory ? DtDir : DtReg)
                .WriteString(node.Name);
        }

        return writer.PatchUInt32(payloadStart - 4, (uint)(writer.Length - payloadStart));
    }

    private async Task<NinePWriter> HandleReadAsync(ReadOnlyMemory<byte> body, ushort tag, CancellationToken ct)
    {
        uint fid, count;
        ulong offset;
        {
            var reader = new NinePReader(body.Span);
            fid = reader.ReadUInt32();
            offset = reader.ReadUInt64();
            count = reader.ReadUInt32();
        }

        var entry = RequireFid(fid);
        if (entry.Node.IsDirectory)
            throw new VfsErrorException(LinuxErrno.EISDIR, $"read: fid {fid} is a directory");
        var handle = entry.Handle
                     ?? throw new VfsErrorException(LinuxErrno.EBADF, $"read: fid {fid} is not open");

        var clamped = Math.Min(count, _msize - ReadOverhead);
        var data = await handle.ReadAsync(offset, clamped, ct).ConfigureAwait(false);
        _stats.RecordTread(data.Length);
        return new NinePWriter().Begin(MessageType.Rread, tag)
            .WriteUInt32((uint)data.Length)
            .WriteBytes(data.Span);
    }

    private async Task<NinePWriter> HandleWriteAsync(ReadOnlyMemory<byte> body, ushort tag, CancellationToken ct)
    {
        uint fid;
        ulong offset;
        ReadOnlyMemory<byte> data;
        {
            var reader = new NinePReader(body.Span);
            fid = reader.ReadUInt32();
            offset = reader.ReadUInt64();
            var count = reader.ReadUInt32();
            _ = reader.ReadBytes((int)count); // bounds-check the declared count against the body
            // Slice the pooled body directly (fid[4] offset[8] count[4] precede the payload): it
            // stays valid for the handler's whole run — Dispatch returns the rented frame only
            // after this task completes — sparing a per-write payload copy.
            data = body.Slice(16, (int)count);
        }

        var entry = RequireFid(fid);
        if (entry.Node.IsDirectory)
            throw new VfsErrorException(LinuxErrno.EISDIR, $"write: fid {fid} is a directory");
        var handle = entry.WriteHandle
                     ?? throw new VfsErrorException(LinuxErrno.EBADF, $"write: fid {fid} is not open for writing");

        var written = await handle.WriteAsync(offset, data, ct).ConfigureAwait(false);
        return new NinePWriter().Begin(MessageType.Rwrite, tag).WriteUInt32(written);
    }

    /// <summary>
    ///     Applies a <c>Tsetattr</c> on a writable node. Only the size change (<c>ftruncate</c>,
    ///     and the host-mount <c>O_TRUNC</c>/<c>truncate(2)</c>) is acted on — through the open
    ///     write handle when there is one, else through the node; mode/uid/gid/time changes are
    ///     accepted as best-effort no-ops so <c>cp -p</c>/<c>chmod</c> don't fail on a host mount.
    ///     Read-only nodes get EOPNOTSUPP (control-file truncate stays a no-op success).
    /// </summary>
    private NinePWriter HandleSetattr(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var fid = reader.ReadUInt32();
        var valid = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // mode
        _ = reader.ReadUInt32(); // uid
        _ = reader.ReadUInt32(); // gid
        var size = reader.ReadUInt64();

        var entry = RequireFid(fid);
        var writableFile = entry.Node is VfsFile { IsWritable: true };
        if (!writableFile && entry.Node is not VfsDirectory { IsWritable: true })
            throw new VfsErrorException(LinuxErrno.EOPNOTSUPP, $"setattr: '{entry.Node.Name}' is read-only");

        if (writableFile && (valid & SetattrSize) != 0)
        {
            if (entry.WriteHandle is { } handle)
                handle.SetLength((long)size);
            else
                ((VfsFile)entry.Node).SetLength((long)size);
        }

        return new NinePWriter().Begin(MessageType.Rsetattr, tag);
    }

    /// <summary>
    ///     Creates a file in a directory fid and rebinds that fid to the new, write-open file
    ///     (<c>Tlcreate</c> — host read-write mounts). The synthetic tree's directories are not
    ///     writable, so this surfaces EROFS there.
    /// </summary>
    private NinePWriter HandleLcreate(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var fid = reader.ReadUInt32();
        var name = reader.ReadString();
        var flags = reader.ReadUInt32();
        var mode = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // gid

        var entry = RequireFid(fid);
        lock (entry)
        {
            if (entry.Opened)
                throw new VfsErrorException(LinuxErrno.EINVAL, $"lcreate: fid {fid} is already open");
            if (entry.Node is not VfsDirectory dir)
                throw new VfsErrorException(LinuxErrno.ENOTDIR, $"lcreate: fid {fid} is not a directory");

            var created = dir.CreateFile(name, mode);
            var newEntry = new FidEntry([.. entry.Path, created.Node])
            {
                Opened = true,
                WriteHandle = created.WriteHandle,
            };
            if ((flags & OAccmode) == ORdwr)
                newEntry.Handle = created.Node.Open();
            _fids[fid] = newEntry;

            return new NinePWriter().Begin(MessageType.Rlcreate, tag)
                .WriteQid(Qid.ForNode(created.Node))
                .WriteUInt32(0); // iounit
        }
    }

    /// <summary>Creates a directory in a directory fid (<c>Tmkdir</c>); read-only tree → EROFS.</summary>
    private NinePWriter HandleMkdir(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var fid = reader.ReadUInt32();
        var name = reader.ReadString();
        var mode = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // gid

        var entry = RequireFid(fid);
        if (entry.Node is not VfsDirectory dir)
            throw new VfsErrorException(LinuxErrno.ENOTDIR, $"mkdir: fid {fid} is not a directory");
        var created = dir.CreateDirectory(name, mode);
        return new NinePWriter().Begin(MessageType.Rmkdir, tag).WriteQid(Qid.ForNode(created));
    }

    /// <summary>Removes a child of a directory fid (<c>Tunlinkat</c>); read-only tree → EROFS.</summary>
    private NinePWriter HandleUnlinkat(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var fid = reader.ReadUInt32();
        var name = reader.ReadString();
        var flags = reader.ReadUInt32();

        var entry = RequireFid(fid);
        if (entry.Node is not VfsDirectory dir)
            throw new VfsErrorException(LinuxErrno.ENOTDIR, $"unlinkat: fid {fid} is not a directory");
        dir.Unlink(name, (flags & AtRemovedir) != 0);
        return new NinePWriter().Begin(MessageType.Runlinkat, tag);
    }

    /// <summary>Moves a child between two directory fids (<c>Trenameat</c>); read-only tree → EROFS.</summary>
    private NinePWriter HandleRenameat(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var oldFid = reader.ReadUInt32();
        var oldName = reader.ReadString();
        var newFid = reader.ReadUInt32();
        var newName = reader.ReadString();

        var oldEntry = RequireFid(oldFid);
        var newEntry = RequireFid(newFid);
        if (oldEntry.Node is not VfsDirectory oldDir)
            throw new VfsErrorException(LinuxErrno.ENOTDIR, $"renameat: fid {oldFid} is not a directory");
        if (newEntry.Node is not VfsDirectory newDir)
            throw new VfsErrorException(LinuxErrno.ENOTDIR, $"renameat: fid {newFid} is not a directory");
        oldDir.Rename(oldName, newDir, newName);
        return new NinePWriter().Begin(MessageType.Rrenameat, tag);
    }

    /// <summary>Trivially succeeds for any fid: synthetic files have nothing to flush to disk.</summary>
    private NinePWriter HandleFsync(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        _ = RequireFid(reader.ReadUInt32());
        return new NinePWriter().Begin(MessageType.Rfsync, tag);
    }

    private NinePWriter HandleClunk(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        var fid = reader.ReadUInt32();
        if (_fids.TryRemove(fid, out var entry))
            entry.DisposeHandle();
        return new NinePWriter().Begin(MessageType.Rclunk, tag); // always succeeds (T7.4 table)
    }

    private NinePWriter HandleStatfs(ReadOnlyMemory<byte> body, ushort tag)
    {
        var reader = new NinePReader(body.Span);
        _ = reader.ReadUInt32(); // fid (existence not enforced; static answer)
        return new NinePWriter().Begin(MessageType.Rstatfs, tag)
            .WriteUInt32(0x01021997) // V9FS_MAGIC
            .WriteUInt32(4096)       // bsize
            .WriteUInt64(1 << 20)    // blocks
            .WriteUInt64(1 << 20)    // bfree
            .WriteUInt64(1 << 20)    // bavail
            .WriteUInt64(1 << 10)    // files
            .WriteUInt64(1 << 10)    // ffree
            .WriteUInt64(0)          // fsid
            .WriteUInt32(255);       // namelen
    }

    private async Task<NinePWriter> HandleFlushAsync(ReadOnlyMemory<byte> body, ushort tag)
    {
        ushort oldtag;
        {
            var reader = new NinePReader(body.Span);
            oldtag = reader.ReadUInt16();
        }

        if (oldtag != tag && _inFlight.TryGetValue(oldtag, out var pending))
        {
            // flush(5): suppress any not-yet-sent reply to oldtag, cancel it, and only answer
            // Rflush once the old handler has finished (so no oldtag reply can follow Rflush).
            // Set Flushed + cancel unconditionally — Dispatch publishes the tag before it
            // assigns pending.Task, so a flush landing in that window still latches Flushed
            // (the handler then suppresses its own reply when it runs) and need only await the
            // task once it exists.
            pending.Flushed = true;
            try
            {
                pending.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (pending.Task is { } task)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                    // The flushed handler's outcome is irrelevant.
                }
            }
        }

        return new NinePWriter().Begin(MessageType.Rflush, tag);
    }

    private FidEntry RequireFid(uint fid)
        => _fids.TryGetValue(fid, out var entry)
            ? entry
            : throw new VfsErrorException(LinuxErrno.EBADF, $"unknown fid {fid}");

    /// <summary>Per-fid state (OS_PLAN.md T7.3): the walk path, open handle and readdir snapshot.</summary>
    private sealed class FidEntry
    {
        internal FidEntry(IReadOnlyList<VfsNode> path) => Path = path;

        /// <summary>The walk path from root ([0]) to the node ([^1]); ".." pops it.</summary>
        internal IReadOnlyList<VfsNode> Path { get; }

        internal VfsNode Node => Path[^1];
        internal bool Opened;
        internal IVfsFileHandle? Handle;
        internal IVfsWritableFileHandle? WriteHandle;
        internal IReadOnlyList<VfsNode>? DirListing;

        internal void DisposeHandle()
        {
            Dispose(Handle);
            Dispose(WriteHandle);
            Handle = null;
            WriteHandle = null;
        }

        private static void Dispose(IDisposable? handle)
        {
            try
            {
                handle?.Dispose();
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"9p session: handle dispose failed: {ex.Message}");
            }
        }
    }

    private sealed class InFlight
    {
        internal InFlight(CancellationTokenSource cts, ushort tag)
        {
            Cts = cts;
            Tag = tag;
        }

        internal CancellationTokenSource Cts { get; }
        internal ushort Tag { get; }
        internal volatile Task? Task;
        internal volatile bool Flushed;
    }

    /// <summary>Presents an existing node under the name "." or ".." in a readdir listing.</summary>
    private sealed class DotAlias : VfsNode
    {
        private readonly VfsNode _target;

        internal DotAlias(string name, VfsNode target)
            : base(name, target.QidPath)
            => _target = target;

        public override bool IsDirectory => _target.IsDirectory;
    }
}
