namespace gatOS.NineP.Protocol;

/// <summary>
///     The Linux errno values this server sends in <c>Rlerror</c> (OS_PLAN.md T7.2).
///     9P2000.L carries numeric Linux errnos directly — no string errors.
/// </summary>
public static class LinuxErrno
{
    /// <summary>No such file or directory.</summary>
    public const uint ENOENT = 2;

    /// <summary>I/O error (also the fallback for unexpected VFS exceptions).</summary>
    public const uint EIO = 5;

    /// <summary>Bad file descriptor (fid unknown or not open as required).</summary>
    public const uint EBADF = 9;

    /// <summary>Permission denied (write attempts on the read-only tree).</summary>
    public const uint EACCES = 13;

    /// <summary>
    ///     Resource busy: a control action cannot fire right now (e.g. a one-shot trigger that
    ///     has already fired). Part of the control-file errno vocabulary (KSA_GAME_INTEGRATION_PLAN
    ///     Part 2).
    /// </summary>
    public const uint EBUSY = 16;

    /// <summary>File exists (creating an entry whose name is already taken — host mounts).</summary>
    public const uint EEXIST = 17;

    /// <summary>Cross-device link (renaming across two different host mounts).</summary>
    public const uint EXDEV = 18;

    /// <summary>Not a directory (walking through a file).</summary>
    public const uint ENOTDIR = 20;

    /// <summary>Is a directory (Tread on a directory fid).</summary>
    public const uint EISDIR = 21;

    /// <summary>Invalid argument (malformed but parseable requests).</summary>
    public const uint EINVAL = 22;

    /// <summary>No space left on the host device (a write to a full host mount).</summary>
    public const uint ENOSPC = 28;

    /// <summary>Read-only file system (a write to a read-only host mount).</summary>
    public const uint EROFS = 30;

    /// <summary>Directory not empty (removing a non-empty directory on a host mount).</summary>
    public const uint ENOTEMPTY = 39;

    /// <summary>Operation not supported (unimplemented message types, xattrs, auth).</summary>
    public const uint EOPNOTSUPP = 95;

    /// <summary>
    ///     Operation timed out: the game thread did not drain a queued command in time (the sim
    ///     is paused or on a load screen). Part of the control-file errno vocabulary.
    /// </summary>
    public const uint ETIMEDOUT = 110;

    /// <summary>The conventional symbolic name for an errno (e.g. <c>2 → "ENOENT"</c>); EIO for unknown.</summary>
    public static string Name(uint errno) => errno switch
    {
        ENOENT => nameof(ENOENT),
        EIO => nameof(EIO),
        EBADF => nameof(EBADF),
        EACCES => nameof(EACCES),
        EBUSY => nameof(EBUSY),
        EEXIST => nameof(EEXIST),
        EXDEV => nameof(EXDEV),
        ENOTDIR => nameof(ENOTDIR),
        EISDIR => nameof(EISDIR),
        EINVAL => nameof(EINVAL),
        ENOSPC => nameof(ENOSPC),
        EROFS => nameof(EROFS),
        ENOTEMPTY => nameof(ENOTEMPTY),
        EOPNOTSUPP => nameof(EOPNOTSUPP),
        ETIMEDOUT => nameof(ETIMEDOUT),
        _ => nameof(EIO),
    };
}
