using System.Text;

namespace gatOS.NineP.Vfs;

/// <summary>
///     Generic walk/read/write helpers over a <see cref="VfsDirectory"/> tree, used by the
///     field-level transport projections (the per-leaf MQTT <c>gatos/sim/…</c> topics and the HTTP
///     <c>/v1/fs/…</c> endpoints) to mirror the <c>/sim</c> filesystem leaf-by-leaf. The path layout
///     and per-leaf formatting live exactly once — in the tree the 9p server already serves — so the
///     other transports derive from it rather than re-implementing it (the transport-parity rule).
/// </summary>
/// <remarks>
///     Only scalar files are enumerated: <see cref="VfsFile.IsStreaming"/> files (growing-log /
///     blocking-event — <c>stream</c>, <c>events</c>, <c>alarm</c>) are skipped, since a blocking
///     read would park the walk. Those keep their dedicated transport mechanisms.
/// </remarks>
public static class VfsScan
{
    /// <summary>
    ///     Depth-first enumeration of every scalar leaf under <paramref name="root"/>, each paired
    ///     with its <c>/</c>-joined path relative to the root (no leading slash, e.g.
    ///     <c>vessels/by-id/x/altitude/radar</c>). Streaming files are skipped; a directory whose
    ///     relative path satisfies <paramref name="prune"/> is not descended into.
    /// </summary>
    public static IEnumerable<(string Path, VfsFile File)> Leaves(
        VfsDirectory root, Func<string, bool>? prune = null)
        => Walk(root, "", prune);

    private static IEnumerable<(string Path, VfsFile File)> Walk(
        VfsDirectory dir, string prefix, Func<string, bool>? prune)
    {
        foreach (var child in dir.List())
        {
            var path = prefix.Length == 0 ? child.Name : $"{prefix}/{child.Name}";
            switch (child)
            {
                case VfsDirectory subdir when prune is null || !prune(path):
                    foreach (var leaf in Walk(subdir, path, prune))
                        yield return leaf;
                    break;
                case VfsFile { IsStreaming: false } file:
                    yield return (path, file);
                    break;
            }
        }
    }

    /// <summary>
    ///     Resolves a <c>/</c>-separated path to its <see cref="VfsFile"/> by walking
    ///     <see cref="VfsDirectory.Lookup"/> segment by segment; null when any segment is missing,
    ///     a non-final segment is not a directory, or the final node is not a file. Resolves any
    ///     path the tree exposes (including the <c>vessels/active</c> alias) — on-demand resolution
    ///     has no churn cost, so it is not pruned the way the bulk walk is.
    /// </summary>
    public static VfsFile? Resolve(VfsDirectory root, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var dir = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (dir.Lookup(segments[i]) is VfsDirectory child)
                dir = child;
            else
                return null;
        }

        return dir.Lookup(segments[^1]) as VfsFile;
    }

    /// <summary>
    ///     Reads a scalar file's current value in full and returns it UTF-8-decoded with the
    ///     trailing newline (the file convention) trimmed. Opens a fresh handle (one per-open
    ///     snapshot) and disposes it. Do not call on <see cref="VfsFile.IsStreaming"/> files.
    /// </summary>
    public static async ValueTask<string> ReadTextAsync(VfsFile file, CancellationToken ct = default)
    {
        using var handle = file.Open();
        using var buffer = new MemoryStream();
        ulong offset = 0;
        while (true)
        {
            var chunk = await handle.ReadAsync(offset, 8192, ct).ConfigureAwait(false);
            if (chunk.IsEmpty)
                break;
            buffer.Write(chunk.Span);
            offset += (ulong)chunk.Length;
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimEnd('\n');
    }

    /// <summary>
    ///     Writes <paramref name="value"/> to a writable file exactly as a single newline-terminated
    ///     write (the <c>echo value &gt; file</c> shape), so a control file actuates on the newline
    ///     and a failure throws <see cref="VfsErrorException"/> carrying the errno. Throws EACCES via
    ///     <see cref="VfsFile.OpenWrite"/> when the file is read-only.
    /// </summary>
    public static async ValueTask WriteTextAsync(VfsFile file, string value, CancellationToken ct = default)
    {
        var handle = file.OpenWrite();
        try
        {
            var payload = Encoding.UTF8.GetBytes(value.TrimEnd('\n') + "\n");
            await handle.WriteAsync(0, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            handle.Dispose();
        }
    }
}
