using gatOS.NineP.Protocol;
using Microsoft.Win32.SafeHandles;

namespace gatOS.NineP.Vfs;

/// <summary>
///     A real directory on the host filesystem, exposed through 9P for the host-folder mounts
///     feature. <see cref="List"/>/<see cref="Lookup"/> enumerate the live directory; writable
///     mounts implement the full create/remove/rename surface, all confined to the mount subtree
///     (names are single components and the resolved path is verified to stay under the root, so a
///     guest cannot escape via <c>..</c> or an absolute name).
/// </summary>
public sealed class HostDirectory : VfsDirectory
{
    private readonly string _path;
    private readonly bool _writable;
    private readonly HostMountQids _qids;

    /// <param name="name">The entry name (the mount alias at the root, or the leaf below it).</param>
    /// <param name="fullPath">The absolute host directory this node maps to.</param>
    /// <param name="writable">Whether the owning mount is read-write.</param>
    /// <param name="qids">The mount's qid allocator.</param>
    public HostDirectory(string name, string fullPath, bool writable, HostMountQids qids)
        : base(name, qids.Intern(fullPath))
    {
        _path = fullPath;
        _writable = writable;
        _qids = qids;
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
                return new DateTimeOffset(Directory.GetLastWriteTimeUtc(_path)).ToUnixTimeSeconds();
            }
            catch
            {
                return -1;
            }
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<VfsNode> List()
    {
        if (!Directory.Exists(_path))
            return [];
        try
        {
            var nodes = new List<VfsNode>();
            foreach (var dir in Directory.EnumerateDirectories(_path))
                nodes.Add(new HostDirectory(Path.GetFileName(dir), dir, _writable, _qids));
            foreach (var file in Directory.EnumerateFiles(_path))
                nodes.Add(new HostFile(Path.GetFileName(file), file, _writable, _qids));
            return nodes;
        }
        catch (Exception ex)
        {
            throw HostIo.ToVfsError(ex, $"list '{Name}'");
        }
    }

    /// <inheritdoc />
    public override VfsNode? Lookup(string name)
    {
        if (!IsValidComponent(name))
            return null;
        var full = TryResolveChild(name);
        if (full is null)
            return null;
        if (Directory.Exists(full))
            return new HostDirectory(name, full, _writable, _qids);
        if (File.Exists(full))
            return new HostFile(name, full, _writable, _qids);
        return null;
    }

    /// <inheritdoc />
    public override VfsCreatedFile CreateFile(string name, uint mode)
    {
        var full = ResolveChildForWrite(name);
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(full, FileMode.CreateNew, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException) when (File.Exists(full) || Directory.Exists(full))
        {
            throw new VfsErrorException(LinuxErrno.EEXIST, $"'{name}' already exists");
        }
        catch (Exception ex)
        {
            throw HostIo.ToVfsError(ex, $"create '{name}'");
        }

        return new VfsCreatedFile(new HostFile(name, full, true, _qids),
            new HostFile.HostWriteHandle(handle));
    }

    /// <inheritdoc />
    public override VfsDirectory CreateDirectory(string name, uint mode)
    {
        var full = ResolveChildForWrite(name);
        if (Directory.Exists(full) || File.Exists(full))
            throw new VfsErrorException(LinuxErrno.EEXIST, $"'{name}' already exists");
        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex)
        {
            throw HostIo.ToVfsError(ex, $"mkdir '{name}'");
        }

        return new HostDirectory(name, full, true, _qids);
    }

    /// <inheritdoc />
    public override void Unlink(string name, bool removeDir)
    {
        var full = ResolveChildForWrite(name);
        try
        {
            if (removeDir)
            {
                if (File.Exists(full))
                    throw new VfsErrorException(LinuxErrno.ENOTDIR, $"'{name}' is not a directory");
                if (!Directory.Exists(full))
                    throw new VfsErrorException(LinuxErrno.ENOENT, $"'{name}' not found");
                if (Directory.EnumerateFileSystemEntries(full).Any())
                    throw new VfsErrorException(LinuxErrno.ENOTEMPTY, $"'{name}' is not empty");
                Directory.Delete(full, recursive: false);
            }
            else
            {
                if (Directory.Exists(full))
                    throw new VfsErrorException(LinuxErrno.EISDIR, $"'{name}' is a directory");
                if (!File.Exists(full))
                    throw new VfsErrorException(LinuxErrno.ENOENT, $"'{name}' not found");
                File.Delete(full);
            }
        }
        catch (Exception ex) when (ex is not VfsErrorException)
        {
            throw HostIo.ToVfsError(ex, $"remove '{name}'");
        }
    }

    /// <inheritdoc />
    public override void Rename(string oldName, VfsDirectory newParent, string newName)
    {
        if (newParent is not HostDirectory target)
            throw new VfsErrorException(LinuxErrno.EXDEV, "cannot rename across different mount types");
        var src = ResolveChildForWrite(oldName);
        var dst = target.ResolveChildForWrite(newName);
        try
        {
            if (Directory.Exists(src))
            {
                if (File.Exists(dst))
                    throw new VfsErrorException(LinuxErrno.ENOTDIR, $"'{newName}' is a file");
                if (Directory.Exists(dst))
                    throw new VfsErrorException(LinuxErrno.EEXIST, $"'{newName}' already exists");
                Directory.Move(src, dst);
            }
            else if (File.Exists(src))
            {
                if (Directory.Exists(dst))
                    throw new VfsErrorException(LinuxErrno.EISDIR, $"'{newName}' is a directory");
                File.Move(src, dst, overwrite: true);
            }
            else
            {
                throw new VfsErrorException(LinuxErrno.ENOENT, $"'{oldName}' not found");
            }
        }
        catch (Exception ex) when (ex is not VfsErrorException)
        {
            throw HostIo.ToVfsError(ex, $"rename '{oldName}'");
        }
    }

    /// <summary>Resolves a child name for a mutating op: enforces writability and a valid, in-bounds name.</summary>
    private string ResolveChildForWrite(string name)
    {
        if (!_writable)
            throw new VfsErrorException(LinuxErrno.EROFS, $"'{Name}' is a read-only mount");
        return TryResolveChild(name)
               ?? throw new VfsErrorException(LinuxErrno.EINVAL, $"invalid name '{name}'");
    }

    /// <summary>
    ///     Combines <paramref name="name"/> onto this directory and verifies the result stays inside
    ///     the mount root (defence in depth against traversal — the walk handler already rejects
    ///     <c>..</c>, but a crafted single component must not escape either). Null = invalid.
    /// </summary>
    private string? TryResolveChild(string name)
    {
        if (!IsValidComponent(name))
            return null;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_path, name));
        }
        catch
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(_path) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(root, comparison) ? full : null;
    }

    private static bool IsValidComponent(string name)
        => name.Length > 0 && name != "." && name != ".."
           && name.AsSpan().IndexOfAny('/', '\\') < 0 && !name.Contains('\0');
}
