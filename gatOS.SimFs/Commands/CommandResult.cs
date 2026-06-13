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
    public uint ToErrno() => Outcome switch
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
}
