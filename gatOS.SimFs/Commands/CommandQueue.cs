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
    public async Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
    {
        if (!ControlEnabled)
            return new CommandResult(CommandOutcome.Denied, "control is disabled in gatos.toml");

        var pending = new Pending(command);
        (command.Phase == CommandPhase.Solver ? _solver : _frame).Enqueue(pending);

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
    ///     awaiting submit. Abandoned (timed-out) commands are dropped without executing. Returns
    ///     the number executed.
    /// </summary>
    public int Drain(CommandPhase phase, ICommandExecutor executor, int maxCommands)
    {
        var queue = phase == CommandPhase.Solver ? _solver : _frame;
        var executed = 0;
        for (var i = 0; i < maxCommands && queue.TryDequeue(out var pending); i++)
        {
            if (pending.Abandoned)
                continue;

            CommandResult result;
            try
            {
                result = executor.Execute(pending.Command);
            }
            catch (Exception ex)
            {
                // The executor contracts not to throw; treat a leak as an integration fault.
                ModLog.Log.Debug($"command executor threw for '{pending.Command.Action}': {ex.Message}");
                result = new CommandResult(CommandOutcome.Fault, ex.Message);
            }

            pending.Completion.TrySetResult(result);
            executed++;
        }

        return executed;
    }

    private sealed class Pending(SimCommand command)
    {
        // RunContinuationsAsynchronously: the awaiting transport thread must never resume inline
        // on the game thread inside Drain (threading rule 5 — nothing blocks the game thread).
        internal TaskCompletionSource<CommandResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal SimCommand Command { get; } = command;
        internal volatile bool Abandoned;
    }
}
