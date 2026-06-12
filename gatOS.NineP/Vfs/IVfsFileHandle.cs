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
