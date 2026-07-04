using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>Captures the last submitted command and returns a configurable result (no game thread).</summary>
internal sealed class FakeCommandSink : ICommandSink
{
    public bool ControlEnabled { get; init; } = true;
    public bool DebugEnabled { get; init; }
    public CommandResult Result { get; set; } = CommandResult.Ok;
    public SimCommand? Last { get; private set; }
    public int Submits { get; private set; }

    /// <summary>The last group given to <see cref="SubmitBatchAsync"/> (null = none yet).</summary>
    public IReadOnlyList<SimCommand>? LastBatch { get; private set; }

    public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
    {
        if (!ControlEnabled)
            return Task.FromResult(new CommandResult(CommandOutcome.Denied, "disabled"));
        Last = command;
        Submits++;
        return Task.FromResult(Result);
    }

    // Overrides the interface default so tests can assert a batch arrived as ONE group.
    public Task<CommandResult> SubmitBatchAsync(IReadOnlyList<SimCommand> commands, CancellationToken ct)
    {
        if (!ControlEnabled)
            return Task.FromResult(new CommandResult(CommandOutcome.Denied, "disabled"));
        LastBatch = commands;
        Last = commands[^1];
        Submits++;
        return Task.FromResult(Result);
    }
}

/// <summary>A drainable executor double: records calls and can be made to throw or return a result.</summary>
internal sealed class FakeCommandExecutor : ICommandExecutor
{
    public CommandResult Result { get; set; } = CommandResult.Ok;
    public bool Throw { get; set; }
    public SimCommand? Last { get; private set; }
    public int Count { get; private set; }

    /// <summary>Every executed command, in order (for batch ordering asserts).</summary>
    public List<SimCommand> All { get; } = [];

    /// <summary>Optional per-command result override; falls back to <see cref="Result"/>.</summary>
    public Func<SimCommand, CommandResult>? OnExecute { get; set; }

    public CommandResult Execute(SimCommand command)
    {
        Last = command;
        All.Add(command);
        Count++;
        if (Throw)
            throw new InvalidOperationException("boom");
        return OnExecute?.Invoke(command) ?? Result;
    }
}
