namespace gatOS.SimFs.Commands;

/// <summary>
///     The <b>TRIGGER</b> control archetype (KSA_GAME_INTEGRATION_PLAN Part 2): writing the exact
///     fire token (conventionally <c>1</c>) performs a one-shot action; any other token fails with
///     EINVAL. The Linux analogue is a sysfs one-shot like <c>reset</c> or sysrq-trigger. Reads
///     return a status line (default <c>0</c>) so the file is still <c>cat</c>-able. Non-idempotent
///     actions that can no longer fire surface as EBUSY from the executor.
/// </summary>
public sealed class TriggerFile : CommandFile
{
    private readonly string _token;
    private readonly SimCommand _command;

    /// <param name="name">The entry name.</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="sink">Where the fire command is submitted.</param>
    /// <param name="command">The command emitted when the fire token is written.</param>
    /// <param name="read">Optional status provider (defaults to <c>"0"</c>).</param>
    /// <param name="token">The exact token that fires the action (defaults to <c>"1"</c>).</param>
    public TriggerFile(string name, ulong qidPath, ICommandSink sink, SimCommand command,
        Func<string>? read = null, string token = "1")
        : base(name, qidPath, sink, read ?? (() => "0"))
    {
        _command = command;
        _token = token;
    }

    /// <inheritdoc />
    protected override SimCommand? Parse(string token) => token == _token ? _command : null;
}
