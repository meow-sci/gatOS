namespace gatOS.SimFs.Commands;

/// <summary>
///     A <b>STATE</b> control whose value is one of a fixed set of symbolic tokens
///     (KSA_GAME_INTEGRATION_PLAN §5.1): attitude track-target (<c>prograde</c>, <c>retrograde</c>,
///     …), attitude/burn reference frame (<c>lvlh</c>, <c>eclbody</c>, …). Matching is
///     case-insensitive; a token outside the allowed set fails the write with EINVAL. The emitted
///     <see cref="SimCommand"/> carries the canonical token in <see cref="SimCommand.Token"/>.
/// </summary>
public sealed class EnumControlFile : CommandFile
{
    private readonly IReadOnlyDictionary<string, string> _allowed; // lower-case → canonical
    private readonly Func<string, SimCommand> _build;

    private EnumControlFile(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        IReadOnlyList<string> allowed, Func<string, SimCommand> build)
        : base(name, qidPath, sink, read)
    {
        _allowed = allowed.ToDictionary(v => v.ToLowerInvariant(), v => v);
        _build = build;
    }

    /// <summary>Creates an enum control accepting the given (case-insensitive) <paramref name="allowed"/> tokens.</summary>
    public static EnumControlFile Create(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        IReadOnlyList<string> allowed, Func<string, SimCommand> build)
        => new(name, qidPath, sink, read, allowed, build);

    /// <inheritdoc />
    protected override SimCommand? Parse(string token)
        => _allowed.TryGetValue(token.ToLowerInvariant(), out var canonical) ? _build(canonical) : null;
}
