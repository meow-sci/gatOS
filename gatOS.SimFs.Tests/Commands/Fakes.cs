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

    public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
    {
        if (!ControlEnabled)
            return Task.FromResult(new CommandResult(CommandOutcome.Denied, "disabled"));
        Last = command;
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

    public CommandResult Execute(SimCommand command)
    {
        Last = command;
        Count++;
        if (Throw)
            throw new InvalidOperationException("boom");
        return Result;
    }
}
