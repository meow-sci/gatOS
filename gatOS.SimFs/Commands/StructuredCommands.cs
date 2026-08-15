namespace gatOS.SimFs.Commands;

/// <summary>The validated payload of an atomic same-tick command batch.</summary>
/// <param name="Commands">Commands to submit as one ordered group.</param>
/// <param name="Phase">The one game-thread phase shared by every command.</param>
public sealed record CommandBatch(IReadOnlyList<SimCommand> Commands, CommandPhase Phase);

/// <summary>The result of compiling a structured batch.</summary>
public sealed record CommandBatchBuildResult(CommandBatch? Batch, CommandValidationResult Validation)
{
    /// <summary>Whether a batch is available for submission.</summary>
    public bool IsValid => Batch is not null && Validation.IsValid;
}

/// <summary>Compiles transport-neutral command objects into an atomic same-tick batch.</summary>
public static class CommandBatchBuilder
{
    /// <summary>The published same-tick batch limit shared by 9P and MCP.</summary>
    public const int MaximumCommands = 64;

    /// <summary>
    ///     Validates every command and verifies the existing same-tick invariant: a batch may use
    ///     exactly one game-thread phase. The returned commands preserve caller order.
    /// </summary>
    public static CommandBatchBuildResult Build(IReadOnlyList<SimCommand>? commands)
    {
        if (commands is null || commands.Count == 0)
            return Invalid("batch must contain at least one command");
        if (commands.Count > MaximumCommands)
            return Invalid($"batch may contain at most {MaximumCommands} commands");

        var phase = commands[0].Phase;
        for (var i = 0; i < commands.Count; i++)
        {
            var validation = CommandCatalog.Validate(commands[i]);
            if (!validation.IsValid)
                return Invalid($"command {i + 1}: {validation.Error}");
            if (commands[i].Phase != phase)
                return Invalid("batch commands must all share one phase (Frame or Solver)");
        }

        return new CommandBatchBuildResult(new CommandBatch(commands.ToArray(), phase),
            CommandValidationResult.Valid());
    }

    private static CommandBatchBuildResult Invalid(string error)
        => new(null, CommandValidationResult.Invalid(error));
}

/// <summary>One typed timed-schedule entry, independent of a 9P filesystem path.</summary>
/// <param name="OffsetMs">Absolute non-negative offset from schedule start, in milliseconds.</param>
/// <param name="CoalescingKey">Stable logical state key used for catch-up coalescing.</param>
/// <param name="Command">The already-structured game command.</param>
/// <param name="IsTrigger">Whether catch-up must preserve this entry as an impulse.</param>
public sealed record ScheduledCommand(double OffsetMs, string CoalescingKey, SimCommand Command, bool IsTrigger);

/// <summary>Structured transport-neutral schedule options and entries.</summary>
/// <param name="Id">Requested schedule id, or null for an auto-generated id.</param>
/// <param name="Group">Optional shared-clock group.</param>
/// <param name="Clock">Requested clock base, or null for the store default.</param>
/// <param name="Rate">Requested playback rate, or null for one.</param>
/// <param name="Loop">Requested loop setting, or null for false.</param>
/// <param name="PayloadBytes">
///     Size of the decoded transport payload in bytes. The transport supplies its measured input
///     size so this builder can apply <see cref="ScheduleLimits.MaxBytes"/> without depending on a
///     particular JSON, HTTP, MQTT, or filesystem wire representation.
/// </param>
/// <param name="Entries">The typed timeline entries to compile.</param>
public sealed record ScheduleDefinition(
    string? Id,
    string? Group,
    ClockBase? Clock,
    double? Rate,
    bool? Loop,
    int PayloadBytes,
    IReadOnlyList<ScheduledCommand> Entries);

/// <summary>The result of compiling a schedule definition.</summary>
public sealed record ScheduleBuildResult(Schedule? Schedule, CommandValidationResult Validation)
{
    /// <summary>Whether a schedule is available for registration.</summary>
    public bool IsValid => Schedule is not null && Validation.IsValid;
}

/// <summary>
///     Builds a <see cref="Schedule"/> from typed transport input. This is the JSON-transport
///     analogue of <see cref="TimedBatchFile"/>: validation happens before reserving an id or
///     submitting work, while the existing <see cref="ScheduleStore"/> remains the owner of ids,
///     caps, activation, playback, and execution.
/// </summary>
public static class ScheduleBuilder
{
    /// <summary>Validates and constructs a schedule using the store's existing limits and defaults.</summary>
    public static ScheduleBuildResult Build(ScheduleStore store, ScheduleDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (definition is null)
            return Invalid("schedule definition is required");
        if (definition.Entries is null || definition.Entries.Count == 0)
            return Invalid("schedule must contain at least one entry");
        if (definition.PayloadBytes < 0 || definition.PayloadBytes > store.Limits.MaxBytes)
            return Invalid($"schedule payload must be between 0 and {store.Limits.MaxBytes} bytes");
        if (definition.Entries.Count > store.Limits.MaxEntries)
            return Invalid($"schedule has more than {store.Limits.MaxEntries} entries");
        if (definition.Id is not null && !ScheduleStore.IsValidId(definition.Id))
            return Invalid("schedule id must use [A-Za-z0-9_.-] and be at most 64 characters");
        if (!string.IsNullOrEmpty(definition.Group) && !ScheduleStore.IsValidId(definition.Group))
            return Invalid("schedule group must use [A-Za-z0-9_.-] and be at most 64 characters");

        var rate = definition.Rate ?? 1;
        if (!double.IsFinite(rate) || rate <= 0 || rate > 100)
            return Invalid("schedule rate must be finite and in (0, 100]");

        var entries = new ScheduleEntry[definition.Entries.Count];
        for (var i = 0; i < definition.Entries.Count; i++)
        {
            var entry = definition.Entries[i];
            if (!double.IsFinite(entry.OffsetMs) || entry.OffsetMs < 0)
                return Invalid($"entry {i + 1}: offset_ms must be finite and non-negative");
            if (string.IsNullOrWhiteSpace(entry.CoalescingKey))
                return Invalid($"entry {i + 1}: coalescing_key is required");
            var validation = CommandCatalog.Validate(entry.Command);
            if (!validation.IsValid)
                return Invalid($"entry {i + 1}: {validation.Error}");
            entries[i] = new ScheduleEntry(entry.OffsetMs, entry.CoalescingKey, entry.Command, entry.IsTrigger);
        }

        // Reserve only after every typed entry passed validation, exactly like TimedBatchFile.Commit.
        string id;
        try
        {
            id = store.ReserveId(definition.Id);
        }
        catch (Exception ex) when (ex is gatOS.NineP.Vfs.VfsErrorException)
        {
            return Invalid(ex.Message);
        }

        return new ScheduleBuildResult(new Schedule(id, definition.Group ?? "",
            definition.Clock ?? store.Limits.DefaultClock, rate, definition.Loop ?? false, entries),
            CommandValidationResult.Valid());
    }

    /// <summary>Submits a successfully built schedule without waiting for its playback lifetime.</summary>
    public static string Submit(ScheduleStore store, ScheduleBuildResult build)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(build);
        if (build.Schedule is not { } schedule || !build.IsValid)
            throw new InvalidOperationException(build.Validation.Error ?? "schedule build failed");
        return store.Submit(schedule);
    }

    private static ScheduleBuildResult Invalid(string error)
        => new(null, CommandValidationResult.Invalid(error));
}
