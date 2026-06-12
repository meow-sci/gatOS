using System.Diagnostics;
using gatOS.Logging;

namespace gatOS.Vm;

/// <summary>
///     A cross-process single-writer lock for one overlay disk. QEMU's own image locking is not
///     implemented on Windows (OS_ANALYSIS.md §3.7), so gatOS enforces one-VM-per-overlay itself:
///     a <c>&lt;profile&gt;.lock</c> file containing the owner PID, created exclusively at VM
///     start and removed at clean stop. A stale lock (owner PID no longer alive) is reclaimed
///     with a log line (OS_PLAN.md T3.2).
/// </summary>
public sealed class DiskLock : IDisposable
{
    private readonly string _path;
    private bool _disposed;

    private DiskLock(string path) => _path = path;

    /// <summary>
    ///     Acquires the lock file exclusively, reclaiming it first if the recorded owner process
    ///     is dead.
    /// </summary>
    /// <exception cref="DiskOperationException">The lock is held by a live process.</exception>
    public static DiskLock Acquire(string lockFilePath)
    {
        // Two attempts: the first may fail against a stale file, which we reclaim and retry.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(lockFilePath, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.Write(Environment.ProcessId);
                return new DiskLock(lockFilePath);
            }
            catch (IOException) when (File.Exists(lockFilePath) && attempt == 0)
            {
                var owner = ReadOwnerPid(lockFilePath);
                if (owner is { } pid && IsProcessAlive(pid))
                    throw new DiskOperationException(
                        $"Disk is in use by another process (pid {pid}, lock file '{lockFilePath}'). "
                        + "Only one VM may use an overlay at a time.");

                ModLog.Log.Warn($"Reclaiming stale disk lock '{lockFilePath}' "
                                + (owner is { } p ? $"(owner pid {p} is dead)." : "(unreadable owner pid)."));
                File.Delete(lockFilePath);
            }
        }
    }

    /// <summary>Releases the lock by deleting the lock file (best effort, idempotent).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            File.Delete(_path);
        }
        catch (IOException ex)
        {
            ModLog.Log.Warn($"Could not delete disk lock '{_path}': {ex.Message}");
        }
    }

    private static int? ReadOwnerPid(string path)
    {
        try
        {
            return int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
