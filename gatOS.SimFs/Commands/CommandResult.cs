using gatOS.NineP.Protocol;

namespace gatOS.SimFs.Commands;

/// <summary>
///     The outcome of executing a <see cref="SimCommand"/>, mapped 1:1 to the frozen control-file
///     errno vocabulary (KSA_GAME_INTEGRATION_PLAN Part 2). The transport turns a non-success
///     outcome into the matching errno on the failed write.
/// </summary>
public enum CommandOutcome
{
    /// <summary>The mutation applied successfully.</summary>
    Ok,

    /// <summary>Unparseable or out-of-range argument (EINVAL).</summary>
    Invalid,

    /// <summary>The vessel/module vanished between walk and execution (ENOENT).</summary>
    NotFound,

    /// <summary>Control disabled by config — authority or debug gating (EACCES).</summary>
    Denied,

    /// <summary>The action cannot fire right now, e.g. an already-fired one-shot (EBUSY).</summary>
    Busy,

    /// <summary>A KSA API threw — integration-layer fault, flips the accessor health latch (EIO).</summary>
    Fault,

    /// <summary>The game thread did not drain the command in time — paused/loading (ETIMEDOUT).</summary>
    TimedOut,

    /// <summary>The accessor is disabled by its health latch after a prior fault (EOPNOTSUPP).</summary>
    Unsupported,
}

/// <summary>The result of submitting a command: an <see cref="CommandOutcome"/> and an optional message.</summary>
/// <param name="Outcome">The classified outcome.</param>
/// <param name="Message">A human-readable detail for logs (never shown to the guest as text).</param>
public sealed record CommandResult(CommandOutcome Outcome, string? Message = null)
{
    /// <summary>The shared success result.</summary>
    public static CommandResult Ok { get; } = new(CommandOutcome.Ok);

    /// <summary>Whether the command applied successfully.</summary>
    public bool IsSuccess => Outcome == CommandOutcome.Ok;

    /// <summary>The Linux errno a failed write should report (see <see cref="LinuxErrno"/>).</summary>
    public uint ToErrno() => Outcome.ToErrno();
}

/// <summary>
///     The single source of truth for turning a <see cref="CommandOutcome"/> into the frozen
///     errno vocabulary (KSA_GAME_INTEGRATION_PLAN Part 2) — as a Linux <c>errno</c> number for
///     the 9p write path and as the canonical errno name for the HTTP/MQTT/serial transports.
///     Every transport routes through here so the mapping can never drift between them.
/// </summary>
public static class CommandOutcomes
{
    /// <summary>The Linux errno number a failed outcome reports (<c>0</c> for success).</summary>
    public static uint ToErrno(this CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Ok => 0,
        CommandOutcome.Invalid => LinuxErrno.EINVAL,
        CommandOutcome.NotFound => LinuxErrno.ENOENT,
        CommandOutcome.Denied => LinuxErrno.EACCES,
        CommandOutcome.Busy => LinuxErrno.EBUSY,
        CommandOutcome.Fault => LinuxErrno.EIO,
        CommandOutcome.TimedOut => LinuxErrno.ETIMEDOUT,
        CommandOutcome.Unsupported => LinuxErrno.EOPNOTSUPP,
        _ => LinuxErrno.EIO,
    };

    /// <summary>The canonical errno name (<c>"EINVAL"</c>, …) for text/JSON transports.</summary>
    public static string ErrnoName(this CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Ok => "OK",
        CommandOutcome.Invalid => "EINVAL",
        CommandOutcome.NotFound => "ENOENT",
        CommandOutcome.Denied => "EACCES",
        CommandOutcome.Busy => "EBUSY",
        CommandOutcome.TimedOut => "ETIMEDOUT",
        CommandOutcome.Unsupported => "EOPNOTSUPP",
        _ => "EIO",
    };
}
