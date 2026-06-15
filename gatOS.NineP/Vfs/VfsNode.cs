namespace gatOS.NineP.Vfs;

/// <summary>
///     A node in the synthetic filesystem served over 9P2000.L (OS_PLAN.md T7.1). Nodes are
///     cheap, possibly transient objects; identity on the wire is the <see cref="QidPath"/>,
///     which the tree builder must keep stable for the same logical path (M8 interns them).
/// </summary>
public abstract class VfsNode
{
    /// <param name="name">The entry name within the parent directory.</param>
    /// <param name="qidPath">The stable, tree-unique qid path number for this logical node.</param>
    protected VfsNode(string name, ulong qidPath)
    {
        Name = name;
        QidPath = qidPath;
    }

    /// <summary>The entry name within the parent directory (root's name is by convention "/").</summary>
    public string Name { get; }

    /// <summary>Stable unique id for the qid <c>path</c> field, assigned by the tree.</summary>
    public ulong QidPath { get; }

    /// <summary>Whether this node is a directory.</summary>
    public abstract bool IsDirectory { get; }

    /// <summary>
    ///     The node's modification time in Unix seconds for <c>Tgetattr</c>, or <c>-1</c> to let
    ///     the server fall back to its fixed <c>AttrTime</c>. Synthetic telemetry nodes have no real
    ///     mtime (they return -1); host-filesystem passthrough nodes report the on-disk time so
    ///     <c>ls -l</c>, <c>make</c> and <c>rsync</c> behave.
    /// </summary>
    public virtual long ModifiedUnixSeconds => -1;
}

/// <summary>A directory node: a listable, walkable set of children.</summary>
public abstract class VfsDirectory : VfsNode
{
    /// <inheritdoc cref="VfsNode(string, ulong)"/>
    protected VfsDirectory(string name, ulong qidPath)
        : base(name, qidPath)
    {
    }

    /// <inheritdoc />
    public sealed override bool IsDirectory => true;

    /// <summary>
    ///     Lists the children in a stable order. Dynamic directories may produce a different
    ///     set on every call; the server snapshots one listing per readdir cycle so paging
    ///     stays consistent.
    /// </summary>
    public abstract IReadOnlyList<VfsNode> List();

    /// <summary>Resolves one child by name (may build nodes dynamically); null = ENOENT.</summary>
    public abstract VfsNode? Lookup(string name);

    /// <summary>
    ///     Whether entries can be created/removed/renamed in this directory (host read-write
    ///     mounts). The default synthetic tree is read-only; the write/create handlers
    ///     (<c>Tlcreate</c>/<c>Tmkdir</c>/<c>Tunlinkat</c>/<c>Trenameat</c>) reject mutations on
    ///     a directory that is not writable with <c>EROFS</c>.
    /// </summary>
    public virtual bool IsWritable => false;

    /// <summary>
    ///     Creates a child file (<c>Tlcreate</c>) and hands back an open write handle to it — the
    ///     two happen together so the just-created file is the one the fid is bound to. Throws
    ///     <see cref="VfsErrorException"/>: the default read-only tree → <c>EROFS</c>; a host mount
    ///     → <c>EEXIST</c> when the name is taken, <c>EACCES</c>/<c>ENOSPC</c>/etc. from the host FS.
    /// </summary>
    public virtual VfsCreatedFile CreateFile(string name, uint mode)
        => throw new VfsErrorException(Protocol.LinuxErrno.EROFS, $"'{Name}' is read-only");

    /// <summary>Creates a child directory (<c>Tmkdir</c>); read-only tree → <c>EROFS</c>.</summary>
    public virtual VfsDirectory CreateDirectory(string name, uint mode)
        => throw new VfsErrorException(Protocol.LinuxErrno.EROFS, $"'{Name}' is read-only");

    /// <summary>
    ///     Removes a child (<c>Tunlinkat</c>). <paramref name="removeDir"/> is the kernel's
    ///     <c>AT_REMOVEDIR</c> flag (rmdir vs unlink). Read-only tree → <c>EROFS</c>.
    /// </summary>
    public virtual void Unlink(string name, bool removeDir)
        => throw new VfsErrorException(Protocol.LinuxErrno.EROFS, $"'{Name}' is read-only");

    /// <summary>
    ///     Moves <paramref name="oldName"/> in this directory to <paramref name="newName"/> in
    ///     <paramref name="newParent"/> (<c>Trenameat</c>). Read-only tree → <c>EROFS</c>; across
    ///     two unrelated mounts → <c>EXDEV</c>.
    /// </summary>
    public virtual void Rename(string oldName, VfsDirectory newParent, string newName)
        => throw new VfsErrorException(Protocol.LinuxErrno.EROFS, $"'{Name}' is read-only");
}

/// <summary>
///     A regular (read-only) file node.
/// </summary>
/// <remarks>
///     <b>Size must be truthful</b> — at most the bytes a fresh open would actually deliver
///     right now. The analysis-era "fake 4096" advice is wrong on ≥6.11 guest kernels: netfslib
///     turns a 0-byte read inside the claimed i_size into userspace ENODATA, and a claimed size
///     of 0 means the kernel never issues a Tread at all (spike/NOTES.md T1.2, rule 1).
/// </remarks>
public abstract class VfsFile : VfsNode
{
    /// <inheritdoc cref="VfsNode(string, ulong)"/>
    protected VfsFile(string name, ulong qidPath)
        : base(name, qidPath)
    {
    }

    /// <inheritdoc />
    public sealed override bool IsDirectory => false;

    /// <summary>
    ///     The current size in bytes for <c>Tgetattr</c> on an unopened fid (<c>ls -la</c>).
    ///     Opened fids report <see cref="IVfsFileHandle.Size"/> instead, so per-open snapshots
    ///     stay self-consistent.
    /// </summary>
    public abstract long Size { get; }

    /// <summary>Opens a per-fid read handle (one per <c>Tlopen</c>).</summary>
    public abstract IVfsFileHandle Open();

    /// <summary>
    ///     Whether this file accepts writes (control files; KSA_GAME_INTEGRATION_PLAN Part 6 T1).
    ///     Writable files report writable mode bits so the kernel's permission pre-check lets an
    ///     <c>echo</c> through; the default is a read-only sensor file.
    /// </summary>
    public virtual bool IsWritable => false;

    /// <summary>
    ///     Whether this is a growing-log or blocking-event file (a "stream"), as opposed to a scalar
    ///     whose current value a single read returns at once. A bulk scalar walk (<see cref="VfsScan"/>,
    ///     used by the field-level MQTT/HTTP projections) <b>must not</b> read these: a blocking-event
    ///     file would park the walk, and a growing-log is a stream, not a point value. The default is
    ///     a scalar sensor/state file; <c>stream</c>/<c>events</c>/<c>alarm</c> override this to true.
    /// </summary>
    public virtual bool IsStreaming => false;

    /// <summary>
    ///     Opens a per-fid write handle (one per write-mode <c>Tlopen</c>). The default throws
    ///     <see cref="VfsErrorException"/> with EACCES — only <see cref="IsWritable"/> files
    ///     override it.
    /// </summary>
    public virtual IVfsWritableFileHandle OpenWrite()
        => throw new VfsErrorException(Protocol.LinuxErrno.EACCES, $"'{Name}' is not writable");

    /// <summary>
    ///     Truncates (or extends) the file to <paramref name="length"/> bytes for a
    ///     <c>Tsetattr</c> size change on a fid that has no open write handle (a bare
    ///     <c>truncate(2)</c>). The default is a no-op so control files (length-free) accept the
    ///     request silently; host files override it. Size changes on an open write fid are routed
    ///     through <see cref="IVfsWritableFileHandle.SetLength"/> instead.
    /// </summary>
    public virtual void SetLength(long length)
    {
    }
}

/// <summary>
///     The result of <see cref="VfsDirectory.CreateFile"/>: the new file node and an already-open
///     write handle to it, returned together so the <c>Tlcreate</c> handler binds the fid to the
///     exact file it just created.
/// </summary>
/// <param name="Node">The created file node (its qid is the <c>Rlcreate</c> qid).</param>
/// <param name="WriteHandle">An open write handle to the new file.</param>
public readonly record struct VfsCreatedFile(VfsFile Node, IVfsWritableFileHandle WriteHandle);
