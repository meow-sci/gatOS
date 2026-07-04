namespace gatOS.SimFs.Commands;

/// <summary>
///     The transport-thread seam control files submit to (KSA_GAME_INTEGRATION_PLAN Part 6 T1).
///     A control file builds a <see cref="SimCommand"/> and awaits <see cref="SubmitAsync"/>,
///     which enqueues it for the game thread and completes when the mutation has been applied (or
///     timed out). Implemented by <see cref="CommandQueue"/>; the tree depends only on this
///     interface so it stays game-free and unit-testable.
/// </summary>
public interface ICommandSink
{
    /// <summary>Master control switch (<c>[control] enabled</c>); when false every submit is denied.</summary>
    bool ControlEnabled { get; }

    /// <summary>Whether the <c>/sim/debug</c> cheat namespace is visible (<c>[control] debug_namespace</c>).</summary>
    bool DebugEnabled { get; }

    /// <summary>
    ///     Enqueues <paramref name="command"/> for game-thread execution and awaits its result.
    ///     Returns <see cref="CommandOutcome.Denied"/> immediately when control is disabled, and
    ///     <see cref="CommandOutcome.TimedOut"/> if the game thread does not drain it in time.
    ///     Honors <paramref name="ct"/> (the write being flushed/aborted).
    /// </summary>
    Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct);

    /// <summary>
    ///     Enqueues a group of commands meant to execute together in one game tick — the
    ///     <c>/sim/ctl/batch</c> surface (see <see cref="BatchFile"/>). All commands must share one
    ///     <see cref="SimCommand.Phase"/>. <see cref="CommandQueue"/> overrides this with the real
    ///     atomic guarantee (the group drains in order inside a single game-thread drain, never
    ///     split across ticks); this default is a sequential fallback for simple sinks and test
    ///     doubles, which submits one command at a time with <b>no</b> same-tick guarantee. Every
    ///     command executes even after a failure (they are independent); the returned result is
    ///     the first failure, or Ok.
    /// </summary>
    async Task<CommandResult> SubmitBatchAsync(IReadOnlyList<SimCommand> commands, CancellationToken ct)
    {
        CommandResult? failure = null;
        foreach (var command in commands)
        {
            var result = await SubmitAsync(command, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                failure ??= result;
        }

        return failure ?? CommandResult.Ok;
    }
}
