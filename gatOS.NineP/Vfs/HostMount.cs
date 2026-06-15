using System.Collections.Concurrent;
using gatOS.NineP.Protocol;

namespace gatOS.NineP.Vfs;

/// <summary>
///     One host folder shared into the guest: it appears at <c>/mnt/&lt;Name&gt;</c> and maps to
///     <see cref="HostPath"/> on the host. <see cref="Writable"/> mounts accept create/write/delete/
///     rename; read-only mounts reject every mutation with <c>EROFS</c>.
/// </summary>
/// <param name="Name">The single-component mount name (the guest directory under <c>/mnt</c>).</param>
/// <param name="HostPath">The absolute host directory to expose.</param>
/// <param name="Writable">Whether the guest may modify files through this mount.</param>
public readonly record struct HostMountSpec(string Name, string HostPath, bool Writable);

/// <summary>
///     Interns host paths to stable, mount-tree-unique qid path numbers. The kernel's dcache keys on
///     the qid, so the same on-disk path must always map to the same number across re-listings;
///     newly created files get a fresh number on first sight. Path 0 is reserved for the mount root,
///     so interning starts at 1. Keys are case-folded on Windows so a path reached two ways shares a qid.
/// </summary>
public sealed class HostMountQids
{
    private readonly ConcurrentDictionary<string, ulong> _byPath =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private long _next;

    /// <summary>The stable qid path number for <paramref name="fullPath"/> (assigned on first sight).</summary>
    public ulong Intern(string fullPath)
    {
        var key = Normalize(fullPath);
        return _byPath.GetOrAdd(key, _ => (ulong)Interlocked.Increment(ref _next));
    }

    private static string Normalize(string fullPath)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        }
        catch
        {
            return fullPath;
        }
    }
}

/// <summary>
///     Builds the root of the host-mounts 9p tree: one directory whose children are the configured
///     mounts (each a <see cref="HostDirectory"/> named by its mount name). The guest mounts this
///     root once at <c>/mnt</c>, so <c>/mnt/&lt;name&gt;</c> is the host folder.
/// </summary>
public static class HostMountTree
{
    /// <summary>Assembles the mount-root directory from <paramref name="mounts"/> (may be empty).</summary>
    public static VfsDirectory Build(IReadOnlyList<HostMountSpec> mounts)
    {
        var qids = new HostMountQids();
        var children = new VfsNode[mounts.Count];
        for (var i = 0; i < mounts.Count; i++)
        {
            var m = mounts[i];
            children[i] = new HostDirectory(m.Name, Path.GetFullPath(m.HostPath), m.Writable, qids);
        }

        return DelegateDirectory.Fixed("/", 0, children);
    }
}

/// <summary>Maps host <see cref="System.IO"/> exceptions to the closest Linux errno for an Rlerror.</summary>
internal static class HostIo
{
    public static VfsErrorException ToVfsError(Exception ex, string what) => ex switch
    {
        VfsErrorException v => v,
        UnauthorizedAccessException => new VfsErrorException(LinuxErrno.EACCES, $"{what}: access denied"),
        FileNotFoundException or DirectoryNotFoundException => new VfsErrorException(LinuxErrno.ENOENT,
            $"{what}: not found"),
        PathTooLongException => new VfsErrorException(LinuxErrno.EINVAL, $"{what}: path too long"),
        IOException io when IsDiskFull(io) => new VfsErrorException(LinuxErrno.ENOSPC, $"{what}: no space"),
        _ => new VfsErrorException(LinuxErrno.EIO, $"{what}: {ex.Message}"),
    };

    // Windows: ERROR_DISK_FULL (112) / ERROR_HANDLE_DISK_FULL (39) in the low word of the HResult.
    private static bool IsDiskFull(IOException io) => (io.HResult & 0xFFFF) is 112 or 39;
}
