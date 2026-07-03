using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Audio;

/// <summary>
///     The writable <c>/sim/audio/file/</c> directory: each entry is one uploaded clip held
///     in-memory by the <see cref="AudioStore"/>. The memory-backed analogue of
///     <see cref="HostDirectory"/> — <c>Tlcreate</c> + chunked <c>Twrite</c>s accumulate an upload
///     that commits (becomes playable) on clunk, <c>Tunlinkat</c> evicts, and reads return the
///     committed bytes (so <c>md5sum</c> both sides matches). Flat: no subdirectories, no rename.
/// </summary>
public sealed class AudioDirectory : VfsDirectory
{
    private readonly AudioStore _store;
    private readonly Func<string, ulong> _qid;

    /// <param name="name">The entry name (<c>file</c>).</param>
    /// <param name="qidPath">The directory's own stable qid.</param>
    /// <param name="store">The clip store.</param>
    /// <param name="qid">
    ///     The tree's qid interner (path → stable qid), so a clip keeps one identity across
    ///     re-listings, re-uploads and delete/re-create — exactly like the dynamic vessel dirs.
    /// </param>
    public AudioDirectory(string name, ulong qidPath, AudioStore store, Func<string, ulong> qid)
        : base(name, qidPath)
    {
        _store = store;
        _qid = qid;
    }

    /// <inheritdoc />
    public override bool IsWritable => true;

    /// <inheritdoc />
    public override IReadOnlyList<VfsNode> List()
    {
        var clips = _store.List();
        var nodes = new VfsNode[clips.Count];
        for (var i = 0; i < clips.Count; i++)
            nodes[i] = Clip(clips[i].Name);
        return nodes;
    }

    /// <inheritdoc />
    public override VfsNode? Lookup(string name)
        => AudioStore.IsValidName(name) && _store.Exists(name) ? Clip(name) : null;

    /// <inheritdoc />
    public override VfsCreatedFile CreateFile(string name, uint mode)
    {
        var upload = _store.OpenUpload(name, mustCreate: true);
        return new VfsCreatedFile(Clip(name), new AudioClipWriteHandle(upload));
    }

    /// <inheritdoc />
    public override VfsDirectory CreateDirectory(string name, uint mode)
        => throw new VfsErrorException(LinuxErrno.EPERM, "the audio store holds flat clip files only");

    /// <inheritdoc />
    public override void Unlink(string name, bool removeDir)
    {
        if (removeDir)
            throw new VfsErrorException(LinuxErrno.ENOTDIR, $"'{name}' is not a directory");
        _store.Delete(name);
    }

    /// <inheritdoc />
    public override void Rename(string oldName, VfsDirectory newParent, string newName)
        => throw new VfsErrorException(LinuxErrno.EPERM, "audio clips cannot be renamed; re-upload instead");

    private AudioClipFile Clip(string name) => new(name, _qid($"audio/file/{name}"), _store);
}

/// <summary>
///     One uploaded clip as a file node. Reads serve the committed bytes; writes open a fresh
///     upload seeded with them (so <c>O_APPEND</c> extends and the usual <c>O_TRUNC</c> restarts),
///     which commits — marks the clip playable and bumps its version — on clunk.
/// </summary>
public sealed class AudioClipFile : VfsFile
{
    private readonly AudioStore _store;

    /// <inheritdoc cref="VfsNode(string, ulong)"/>
    public AudioClipFile(string name, ulong qidPath, AudioStore store)
        : base(name, qidPath)
        => _store = store;

    /// <inheritdoc />
    public override long Size => _store.SizeOf(Name);

    /// <inheritdoc />
    public override bool IsWritable => true;

    /// <summary>
    ///     Not a stream — a binary blob. Marked streaming to opt out of the scalar field mirrors
    ///     (the MQTT per-leaf topics and bulk walks), which must never read multi-MiB clip bytes
    ///     as a "point value"; the same exclusion the display stream uses.
    /// </summary>
    public override bool IsStreaming => true;

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new ReadHandle(_store.SnapshotBytes(Name));

    /// <inheritdoc />
    public override IVfsWritableFileHandle OpenWrite()
        => new AudioClipWriteHandle(_store.OpenUpload(Name, mustCreate: false));

    /// <inheritdoc />
    public override void SetLength(long length)
    {
        // A bare truncate(2) with no open write fid: run it as a tiny open→truncate→commit upload,
        // so `truncate -s 0 clip` behaves and the version bumps like any other content change.
        var upload = _store.OpenUpload(Name, mustCreate: false);
        try
        {
            upload.SetLength(length);
            upload.Commit();
        }
        catch
        {
            upload.Abort();
            throw;
        }
    }

    private sealed class ReadHandle(byte[] content) : IVfsFileHandle
    {
        public long Size => content.Length;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            if (offset >= (ulong)content.Length)
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            var start = (int)offset;
            var length = (int)Math.Min(count, (uint)(content.Length - start));
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(content.AsMemory(start, length));
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
///     The per-fid upload handle: copies each ≤512 KiB <c>Twrite</c> span into the upload buffer
///     by offset (the span is pooled — valid only during the call), enforces the caps per-write so
///     the failing <c>write(2)</c> carries the real errno (EFBIG/ENOSPC — a clunk cannot), and
///     commits the clip (playable, version bump) on Dispose (clunk).
/// </summary>
internal sealed class AudioClipWriteHandle(AudioStore.AudioUpload upload) : IVfsWritableFileHandle
{
    public ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        upload.Write(offset, data.Span);
        return ValueTask.FromResult((uint)data.Length);
    }

    public void SetLength(long length) => upload.SetLength(length);

    public void Dispose() => upload.Commit();
}
