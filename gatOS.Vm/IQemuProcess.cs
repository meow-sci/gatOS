namespace gatOS.Vm;

/// <summary>Raised when a started QEMU process exits, for whatever reason.</summary>
public sealed class QemuProcessExitedEventArgs(int exitCode, string stderrTail) : EventArgs
{
    /// <summary>The process exit code.</summary>
    public int ExitCode { get; } = exitCode;

    /// <summary>The last stderr lines, for fault surfacing.</summary>
    public string StderrTail { get; } = stderrTail;
}

/// <summary>
///     One QEMU subprocess — extracted as an interface so the <c>VmHost</c> state machine is
///     unit-testable without QEMU (OS_PLAN.md T3.6).
/// </summary>
public interface IQemuProcess : IAsyncDisposable
{
    /// <summary>True between a successful <see cref="StartAsync"/> and process exit.</summary>
    bool IsRunning { get; }

    /// <summary>
    ///     The accelerator this process was launched with, for diagnostics: the forced accel
    ///     after a C#-side retry, else the first ladder entry that was requested. (When QEMU's
    ///     own <c>-accel</c> list falls back internally this still names the first entry —
    ///     best-effort.)
    /// </summary>
    string? EffectiveAccel { get; }

    /// <summary>The last stderr lines (bounded ring), for fault surfacing.</summary>
    string StderrTail { get; }

    /// <summary>The qemu stdout/stderr log file of the current launch, if any.</summary>
    string? QemuLogPath { get; }

    /// <summary>Fires once when the started process exits (clean poweroff, crash, or kill).</summary>
    event EventHandler<QemuProcessExitedEventArgs>? Exited;

    /// <summary>
    ///     Spawns QEMU for <paramref name="spec"/> and supervises it. Retries once with TCG
    ///     forced when the process dies within the survival window with accel-looking stderr.
    /// </summary>
    /// <exception cref="VmStartException">QEMU is missing or exited during the survival window.</exception>
    Task StartAsync(VmLaunchSpec spec, CancellationToken ct);

    /// <summary>Waits up to <paramref name="timeout"/> for process exit; true when it exited.</summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout);

    /// <summary>
    ///     Asks QEMU to quit over QMP (handshake + <c>quit</c>) and waits up to
    ///     <paramref name="timeout"/> for exit. False on any failure (soft — callers escalate).
    /// </summary>
    Task<bool> TryQuitViaQmpAsync(TimeSpan timeout);

    /// <summary>Hard-kills the process tree (the last shutdown-ladder rung).</summary>
    void Kill();
}
