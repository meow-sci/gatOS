using gatOS.NineP.Protocol;

namespace gatOS.NineP.Vfs;

/// <summary>
///     Thrown by VFS callbacks to surface a specific Linux errno to the guest as an
///     <c>Rlerror</c> (e.g. ENOENT when a dynamic node vanished between walk and open).
///     Any other exception from a VFS callback maps to EIO.
/// </summary>
public sealed class VfsErrorException : Exception
{
    /// <param name="errno">The Linux errno (see <see cref="LinuxErrno"/>).</param>
    /// <param name="message">A host-side log message (never sent to the guest).</param>
    public VfsErrorException(uint errno, string message)
        : base(message)
    {
        Errno = errno;
    }

    /// <summary>The Linux errno sent in the <c>Rlerror</c>.</summary>
    public uint Errno { get; }
}
