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
}
