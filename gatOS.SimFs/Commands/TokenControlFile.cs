namespace gatOS.SimFs.Commands;

/// <summary>
///     A <b>STATE</b> control whose value is a free-form, non-empty token (no fixed allowed set):
///     <c>debug/focus</c> / <c>debug/control_vessel</c> write a target astronomical/vehicle id. Any
///     non-empty trimmed token is accepted and carried in <see cref="SimCommand.Token"/>; an empty
///     write fails with EINVAL.
/// </summary>
public sealed class TokenControlFile : CommandFile
{
    private readonly Func<string, SimCommand> _build;

    private TokenControlFile(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Func<string, SimCommand> build)
        : base(name, qidPath, sink, read)
    {
        _build = build;
    }

    /// <summary>Creates a free-form token control.</summary>
    public static TokenControlFile Create(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Func<string, SimCommand> build)
        => new(name, qidPath, sink, read, build);

    /// <inheritdoc />
    protected override SimCommand? Parse(string token) => token.Length == 0 ? null : _build(token);
}
