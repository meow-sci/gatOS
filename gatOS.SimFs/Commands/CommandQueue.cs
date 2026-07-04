using System.Collections.Concurrent;
using gatOS.Logging;

namespace gatOS.SimFs.Commands;

/// <summary>
///     The one-way write pipe between transport threads and the game thread
///     (KSA_GAME_INTEGRATION_PLAN §3.1). Transport threads call <see cref="SubmitAsync"/>, which
///     enqueues a command and awaits its result with a timeout; the game thread calls
///     <see cref="Drain"/> once per phase per frame to execute pending commands and complete the
///     awaiting tasks. <b>Game state is only ever touched inside the executor on the game thread</b>
///     — this class itself is game-free and lock-light (two concurrent queues, one TCS per command).
/// </summary>
public sealed class CommandQueue : ICommandSink
{
    private readonly ConcurrentQueue<Pending> _frame = new();
    private readonly ConcurrentQueue<Pending> _solver = new();
    private readonly TimeSpan _timeout;

    /// <param name="controlEnabled"><c>[control] enabled</c>: false denies every submit.</param>
    /// <param name="debugEnabled"><c>[control] debug_namespace</c>: gates the /sim/debug surface.</param>
    /// <param name="timeout"><c>[control] command_timeout_ms</c>: how long a submit waits for the game thread.</param>
    public CommandQueue(bool controlEnabled, bool debugEnabled, TimeSpan timeout)
    {
        ControlEnabled = controlEnabled;
        DebugEnabled = debugEnabled;
        _timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(2);
    }

    /// <inheritdoc />
    public bool ControlEnabled { get; }

    /// <inheritdoc />
    public bool DebugEnabled { get; }

    /// <inheritdoc />
    public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        => EnqueueAsync([command], command.Phase, ct);

    /// <summary>
    ///     The atomic batch submit (<c>/sim/ctl/batch</c>): the whole group rides one
    ///     <see cref="Pending"/>, so the game thread executes it in order inside a single
    ///     <see cref="Drain"/> — same tick, never split. All commands must share one phase
    ///     (the mixed case cannot mean "same tick": the two phases drain at different points).
    /// </summary>
    public Task<CommandResult> SubmitBatchAsync(IReadOnlyList<SimCommand> commands, CancellationToken ct)
    {
        if (commands.Count == 0)
            return Task.FromResult(new CommandResult(CommandOutcome.Invalid, "empty batch"));

        var group = commands.ToArray();
        var phase = group[0].Phase;
        foreach (var command in group)
        {
            if (command.Phase != phase)
                return Task.FromResult(new CommandResult(CommandOutcome.Invalid,
                    "batch commands must all share one phase (Frame or Solver)"));
        }

        return EnqueueAsync(group, phase, ct);
    }

    private async Task<CommandResult> EnqueueAsync(SimCommand[] group, CommandPhase phase, CancellationToken ct)
    {
        if (!ControlEnabled)
            return new CommandResult(CommandOutcome.Denied, "control is disabled in gatos.toml");

        var pending = new Pending(group);
        (phase == CommandPhase.Solver ? _solver : _frame).Enqueue(pending);

        try
        {
            return await pending.Completion.Task.WaitAsync(_timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Abandon it so the game thread skips executing a command the writer already gave up on.
            pending.Abandoned = true;
            return new CommandResult(CommandOutcome.TimedOut,
                "the game thread did not execute the command in time (paused or loading?)");
        }
        // OperationCanceledException (the write was flushed/clunked) propagates to the caller.
    }

    /// <summary>
    ///     Game-thread only: executes up to <paramref name="maxCommands"/> pending commands of the
    ///     given <paramref name="phase"/> through <paramref name="executor"/>, completing each
    ///     awaiting submit. Abandoned (timed-out) commands are dropped without executing. A batch
    ///     group always executes in full once dequeued — same-tick atomicity is its whole point —
    ///     so the bound can overshoot by the size of the final group. Returns the number executed.
    /// </summary>
    public int Drain(CommandPhase phase, ICommandExecutor executor, int maxCommands)
    {
        var queue = phase == CommandPhase.Solver ? _solver : _frame;
        var executed = 0;
        while (executed < maxCommands && queue.TryDequeue(out var pending))
        {
            if (pending.Abandoned)
                continue;

            var commands = pending.Commands;
            CommandResult? failure = null;
            var result = CommandResult.Ok;
            for (var i = 0; i < commands.Length; i++)
            {
                try
                {
                    result = executor.Execute(commands[i]);
                }
                catch (Exception ex)
                {
                    // The executor contracts not to throw; treat a leak as an integration fault.
                    ModLog.Log.Debug($"command executor threw for '{commands[i].Action}': {ex.Message}");
                    result = new CommandResult(CommandOutcome.Fault, ex.Message);
                }

                executed++;
                // Batch commands are independent: a failure does not stop the rest, and the
                // submitter gets the FIRST failure, annotated with which command it was.
                if (!result.IsSuccess && failure is null)
                    failure = commands.Length == 1
                        ? result
                        : new CommandResult(result.Outcome,
                            $"command {i + 1}/{commands.Length} ({commands[i].Action}): {result.Message ?? "no detail"}");
            }

            pending.Completion.TrySetResult(failure ?? (commands.Length == 1 ? result : CommandResult.Ok));
        }

        return executed;
    }

    private sealed class Pending(SimCommand[] commands)
    {
        // RunContinuationsAsynchronously: the awaiting transport thread must never resume inline
        // on the game thread inside Drain (threading rule 5 — nothing blocks the game thread).
        internal TaskCompletionSource<CommandResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal SimCommand[] Commands { get; } = commands;
        internal volatile bool Abandoned;
    }
}
