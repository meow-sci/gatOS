using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Camera;

/// <summary>
///     The writable <c>/sim/camera/track/</c> directory: each entry is one uploaded JSON camera track
///     held in-memory by the <see cref="CameraStore"/>. The <c>AudioDirectory</c> shape exactly —
///     <c>Tlcreate</c> + chunked <c>Twrite</c>s accumulate an upload that commits on clunk,
///     <c>Tunlinkat</c> evicts, and reads return the committed bytes (so <c>diff</c> both sides
///     matches). Flat: no subdirectories, no rename.
/// </summary>
/// <remarks>
///     The authoring loop is <c>cp /mnt/shots/flyby.json /sim/camera/track/flyby</c> — host-side
///     editing works today through the existing <c>/mnt</c> passthrough, with no watcher and no
///     persistence layer.
/// </remarks>
public sealed class CameraDirectory : VfsDirectory
{
    private readonly CameraStore _store;
    private readonly Func<string, ulong> _qid;

    /// <param name="name">The entry name (<c>track</c>).</param>
    /// <param name="qidPath">The directory's own stable qid.</param>
    /// <param name="store">The track store.</param>
    /// <param name="qid">
    ///     The tree's qid interner (path → stable qid), so a track keeps one identity across
    ///     re-listings, re-uploads and delete/re-create — exactly like the dynamic vessel dirs.
    /// </param>
    public CameraDirectory(string name, ulong qidPath, CameraStore store, Func<string, ulong> qid)
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
        var tracks = _store.List();
        var nodes = new VfsNode[tracks.Count];
        for (var i = 0; i < tracks.Count; i++)
            nodes[i] = Track(tracks[i].Name);
        return nodes;
    }

    /// <inheritdoc />
    public override VfsNode? Lookup(string name)
        => CameraStore.IsValidName(name) && _store.Exists(name) ? Track(name) : null;

    /// <inheritdoc />
    public override VfsCreatedFile CreateFile(string name, uint mode)
    {
        var upload = _store.OpenUpload(name, mustCreate: true);
        return new VfsCreatedFile(Track(name), new CameraTrackWriteHandle(upload));
    }

    /// <inheritdoc />
    public override VfsDirectory CreateDirectory(string name, uint mode)
        => throw new VfsErrorException(LinuxErrno.EPERM, "the camera track store holds flat track files only");

    /// <inheritdoc />
    public override void Unlink(string name, bool removeDir)
    {
        if (removeDir)
            throw new VfsErrorException(LinuxErrno.ENOTDIR, $"'{name}' is not a directory");
        _store.Delete(name);
    }

    /// <inheritdoc />
    public override void Rename(string oldName, VfsDirectory newParent, string newName)
        => throw new VfsErrorException(LinuxErrno.EPERM, "camera tracks cannot be renamed; re-upload instead");

    private CameraTrackFile Track(string name) => new(name, _qid($"camera/track/{name}"), _store);
}

/// <summary>
///     One uploaded track as a file node. Reads serve the committed bytes; writes open a fresh upload
///     seeded with them (so <c>O_APPEND</c> extends and the usual <c>O_TRUNC</c> restarts), which
///     commits — marks the track playable and bumps its version — on clunk.
/// </summary>
/// <remarks>
///     <b>Deliberately not <c>IsStreaming</c>.</b> <c>AudioClipFile</c> opts out of the scalar field
///     mirrors because a clip is multi-MiB binary and would be nonsense as an MQTT "point value". A
///     camera track is small JSON text, and having it appear at
///     <c>GET /v1/fs/sim/camera/track/&lt;name&gt;</c> and <c>gatos/sim/camera/track/&lt;name&gt;</c> is
///     genuinely useful — a shot is readable and re-uploadable over every transport. The
///     <c>camera_max_track_bytes</c> cap is what keeps that honest.
/// </remarks>
public sealed class CameraTrackFile : VfsFile
{
    private readonly CameraStore _store;

    /// <inheritdoc cref="VfsNode(string, ulong)"/>
    public CameraTrackFile(string name, ulong qidPath, CameraStore store)
        : base(name, qidPath)
        => _store = store;

    /// <inheritdoc />
    public override long Size => _store.SizeOf(Name);

    /// <inheritdoc />
    public override bool IsWritable => true;

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new ReadHandle(_store.SnapshotBytes(Name));

    /// <inheritdoc />
    public override IVfsWritableFileHandle OpenWrite()
        => new CameraTrackWriteHandle(_store.OpenUpload(Name, mustCreate: false));

    /// <inheritdoc />
    public override void SetLength(long length)
    {
        // A bare truncate(2) with no open write fid: run it as a tiny open→truncate→commit upload, so
        // `truncate -s 0 flyby` behaves and the version bumps like any other content change.
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
///     The per-fid upload handle: copies each ≤512 KiB <c>Twrite</c> span into the upload buffer by
///     offset (the span is pooled — valid only during the call), enforces the caps per-write so the
///     failing <c>write(2)</c> carries the real errno (EFBIG/ENOSPC — a clunk cannot), and commits the
///     track (playable, version bump) on Dispose (clunk).
/// </summary>
internal sealed class CameraTrackWriteHandle(CameraStore.CameraUpload upload) : IVfsWritableFileHandle
{
    public ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        upload.Write(offset, data.Span);
        return ValueTask.FromResult((uint)data.Length);
    }

    public void SetLength(long length) => upload.SetLength(length);

    public void Dispose() => upload.Commit();
}
