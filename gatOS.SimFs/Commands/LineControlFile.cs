namespace gatOS.SimFs.Commands;

/// <summary>
///     A <b>STATE</b> control whose written value is a whole structured line, parsed by a
///     caller-supplied delegate — the escape hatch for controls whose argument shape mixes a string
///     with numbers and so fits none of the fixed archetypes (Flag/Fraction/Number/Vector/Enum/Token).
///     The welds controls use it: <c>weld</c> writes
///     <c>"&lt;target&gt; &lt;part_iid&gt; &lt;x y z&gt; &lt;pitch yaw roll&gt; &lt;lock&gt;"</c> — a vessel-id
///     token plus eight numbers — into one <see cref="SimCommand"/> (<c>Token</c> = target,
///     <c>Values</c> = the numbers). The parser returns <c>null</c> to fail the write with EINVAL,
///     exactly like every other archetype.
/// </summary>
public sealed class LineControlFile : CommandFile
{
    private readonly Func<string, SimCommand?> _parse;

    private LineControlFile(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Func<string, SimCommand?> parse)
        : base(name, qidPath, sink, read)
        => _parse = parse;

    /// <summary>Creates a line control whose trimmed line is parsed by <paramref name="parse"/>.</summary>
    public static LineControlFile Create(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Func<string, SimCommand?> parse)
        => new(name, qidPath, sink, read, parse);

    /// <inheritdoc />
    protected override SimCommand? Parse(string token) => _parse(token);
}
