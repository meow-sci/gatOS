namespace gatOS.NineP.Vfs;

/// <summary>
///     One open fid's view of a <see cref="VfsFile"/> (OS_PLAN.md T7.1). Created by
///     <see cref="VfsFile.Open"/> on <c>Tlopen</c>, disposed on <c>Tclunk</c> or connection
///     teardown.
/// </summary>
/// <remarks>
///     The two synthetic-file models the spike proved against the real v9fs client
///     (spike/NOTES.md T1.2, rule 3) are both expressed through this one interface:
///     <list type="bullet">
///         <item><b>growing-log</b> (<c>tail -f</c>): never block; serve what exists, return
///             empty at the frontier; <see cref="Size"/> = bytes produced so far.</item>
///         <item><b>blocking-event</b> (<c>cat</c> waits): a fresh read awaits the next event,
///             delivers it, then answers the kernel's continuation reads with empty results so
///             the syscall completes and the line reaches userspace immediately.</item>
///     </list>
/// </remarks>
public interface IVfsFileHandle : IDisposable
{
    /// <summary>
    ///     The truthful current size for <c>Tgetattr</c> on this open fid (never larger than
    ///     what reads will deliver — see <see cref="VfsFile.Size"/> remarks).
    /// </summary>
    long Size { get; }

    /// <summary>
    ///     Reads up to <paramref name="count"/> bytes at <paramref name="offset"/>. An empty
    ///     result is sent as a 0-byte <c>Rread</c> (EOF for this offset). The token fires on
    ///     <c>Tflush</c>, <c>Tclunk</c> and connection teardown — blocking implementations must
    ///     honor it promptly (that is how Ctrl-C on a blocked <c>cat</c> works).
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct);
}

/// <summary>
///     One open fid's write view of a writable <see cref="VfsFile"/> (control files;
///     KSA_GAME_INTEGRATION_PLAN Part 6 T1). Created by <see cref="VfsFile.OpenWrite"/> on a
///     <c>Tlopen</c> carrying <c>O_WRONLY</c>/<c>O_RDWR</c>, disposed on <c>Tclunk</c> or teardown.
/// </summary>
/// <remarks>
///     Control-file writes are <b>synchronous actuation with real feedback</b>: a failed write
///     throws <see cref="VfsErrorException"/> carrying the errno the kernel hands back to the
///     <c>write(2)</c> caller (so <c>echo 1 &gt; ignite</c> can exit non-zero with EINVAL). The
///     handle is line-buffered by contract — it executes the command on the first newline it
///     sees, and any unterminated trailing bytes on <see cref="IDisposable.Dispose"/> (clunk);
///     a clunk-time failure cannot reach the writer and is best-effort only.
/// </remarks>
public interface IVfsWritableFileHandle : IDisposable
{
    /// <summary>
    ///     Accepts <paramref name="data"/> written at <paramref name="offset"/>, returning the
    ///     number of bytes consumed (normally the full length). Throws
    ///     <see cref="VfsErrorException"/> to fail the underlying <c>write(2)</c> with a specific
    ///     errno. The token fires on <c>Tflush</c>/<c>Tclunk</c>/teardown.
    /// </summary>
    ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>
    ///     Truncates (or extends) the open file to <paramref name="length"/> bytes — the
    ///     <c>O_TRUNC</c> on open and the <c>ftruncate(2)</c> (<c>Tsetattr</c> size on this fid).
    ///     The default is a no-op so control files (length-free) ignore it; the host-file write
    ///     handle overrides it with a real truncate.
    /// </summary>
    void SetLength(long length)
    {
    }
}
