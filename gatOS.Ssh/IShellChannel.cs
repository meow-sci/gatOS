using gatOS.Vm;

namespace gatOS.Ssh;

/// <summary>
///     What <c>SshShellSession</c> needs from the connection layer — the test seam that keeps the
///     session's state machine unit-testable without SSH.NET or a VM (mirrors the
///     <c>IQemuProcess</c>/<c>IDiskManager</c> seams of M3). The real implementation is
///     <see cref="VmConnectionBroker"/>.
/// </summary>
internal interface IShellBroker
{
    /// <summary>Forwards <see cref="VmHost.StatusChanged"/> (sessions watch for Faulted).</summary>
    event EventHandler<VmStatus>? VmStatusChanged;

    /// <summary>
    ///     Boots the VM if needed and opens one interactive shell channel (PTY of
    ///     <paramref name="columns"/>×<paramref name="rows"/>, TERM=<paramref name="terminal"/>).
    /// </summary>
    Task<IShellChannel> OpenShellAsync(string terminal, int columns, int rows, CancellationToken ct);
}

/// <summary>
///     One open interactive shell channel into the guest. Events fire on connection-layer
///     threads; <see cref="Write"/> may block and must only be called from a dedicated writer
///     thread (see <c>ShellInputQueue</c>).
/// </summary>
internal interface IShellChannel : IDisposable
{
    /// <summary>Raised per received output chunk. The array is owned by the receiver.</summary>
    event EventHandler<byte[]>? DataReceived;

    /// <summary>Raised when the connection dies (daemon/VM death, network error).</summary>
    event EventHandler<Exception>? ErrorOccurred;

    /// <summary>Raised when the channel closes cleanly (e.g. the user typed <c>exit</c>).</summary>
    event EventHandler? Closed;

    /// <summary>Writes one chunk to the shell's stdin and flushes. Blocking; writer thread only.</summary>
    void Write(byte[] chunk);

    /// <summary>Propagates a terminal resize to the guest PTY (SIGWINCH).</summary>
    void ChangeWindowSize(int columns, int rows);
}
