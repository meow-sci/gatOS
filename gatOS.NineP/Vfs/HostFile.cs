using Microsoft.Win32.SafeHandles;
using gatOS.NineP.Protocol;

namespace gatOS.NineP.Vfs;

/// <summary>
///     A real file on the host filesystem, exposed read (and optionally write) through 9P for the
///     host-folder mounts feature (<c>/mnt/&lt;name&gt;</c>). Unlike the synthetic telemetry files
///     this is a passthrough: <see cref="Size"/> and <see cref="ModifiedUnixSeconds"/> stat the live
///     file, and reads/writes go straight to disk by position (no shared seek state), so concurrent
///     pipelined 9p requests on one fid are safe.
/// </summary>
public sealed class HostFile : VfsFile
{
    private readonly string _path;
    private readonly bool _writable;

    /// <param name="name">The entry name within its parent (the leaf, or the mount alias at the root).</param>
    /// <param name="fullPath">The absolute host path this node maps to.</param>
    /// <param name="writable">Whether the owning mount is read-write.</param>
    /// <param name="qids">The mount's qid allocator (interns this path to a stable qid).</param>
    public HostFile(string name, string fullPath, bool writable, HostMountQids qids)
        : base(name, qids.Intern(fullPath))
    {
        _path = fullPath;
        _writable = writable;
    }

    /// <inheritdoc />
    public override long Size
    {
        get
        {
            try
            {
                var info = new FileInfo(_path);
                return info.Exists ? info.Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public override bool IsWritable => _writable;

    /// <inheritdoc />
    public override long ModifiedUnixSeconds
    {
        get
        {
            try
            {
                return new DateTimeOffset(File.GetLastWriteTimeUtc(_path)).ToUnixTimeSeconds();
            }
            catch
            {
                return -1;
            }
        }
    }

    /// <inheritdoc />
    public override IVfsFileHandle Open()
    {
        try
        {
            return new HostReadHandle(File.OpenHandle(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete));
        }
        catch (Exception ex)
        {
            throw HostIo.ToVfsError(ex, $"open '{Name}'");
        }
    }

    /// <inheritdoc />
    public override IVfsWritableFileHandle OpenWrite()
    {
        if (!_writable)
            throw new VfsErrorException(LinuxErrno.EROFS, $"'{Name}' is on a read-only mount");
        try
        {
            return new HostWriteHandle(File.OpenHandle(_path, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete));
        }
        catch (Exception ex)
        {
            throw HostIo.ToVfsError(ex, $"open '{Name}' for writing");
        }
    }

    /// <inheritdoc />
    public override void SetLength(long length)
    {
        if (!_writable)
            throw new VfsErrorException(LinuxErrno.EROFS, $"'{Name}' is on a read-only mount");
        try
        {
            using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            RandomAccess.SetLength(handle, length);
        }
        catch (Exception ex)
        {
            throw HostIo.ToVfsError(ex, $"truncate '{Name}'");
        }
    }

    /// <summary>A positional read handle over a host file (thread-safe; no shared seek state).</summary>
    internal sealed class HostReadHandle(SafeFileHandle handle) : IVfsFileHandle
    {
        public long Size
        {
            get
            {
                try
                {
                    return RandomAccess.GetLength(handle);
                }
                catch
                {
                    return 0;
                }
            }
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            if (count == 0)
                return ReadOnlyMemory<byte>.Empty;
            var buffer = new byte[count];
            try
            {
                var read = await RandomAccess.ReadAsync(handle, buffer, (long)offset, ct).ConfigureAwait(false);
                return buffer.AsMemory(0, read);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw HostIo.ToVfsError(ex, "read");
            }
        }

        public void Dispose() => handle.Dispose();
    }

    /// <summary>A positional write handle over a host file (honours the 9p write offset).</summary>
    internal sealed class HostWriteHandle(SafeFileHandle handle) : IVfsWritableFileHandle
    {
        public async ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            try
            {
                await RandomAccess.WriteAsync(handle, data, (long)offset, ct).ConfigureAwait(false);
                return (uint)data.Length;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw HostIo.ToVfsError(ex, "write");
            }
        }

        public void SetLength(long length)
        {
            try
            {
                RandomAccess.SetLength(handle, length);
            }
            catch (Exception ex)
            {
                throw HostIo.ToVfsError(ex, "truncate");
            }
        }

        public void Dispose() => handle.Dispose();
    }
}
