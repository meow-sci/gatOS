using System.Globalization;

namespace gatOS.SimFs.Commands;

/// <summary>
///     A <b>STATE</b> control whose value is a fixed-arity vector of space-separated reals
///     (KSA_GAME_INTEGRATION_PLAN §5.1): attitude target (<c>x y z w</c> quaternion), burn
///     (<c>ut dvx dvy dvz</c>), light color (<c>r g b</c>). A write with the wrong component count
///     or a non-finite component fails with EINVAL; a read shows the current vector.
/// </summary>
public sealed class VectorControlFile : CommandFile
{
    private readonly int _arity;
    private readonly Func<IReadOnlyList<double>, SimCommand> _build;

    private VectorControlFile(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        int arity, Func<IReadOnlyList<double>, SimCommand> build)
        : base(name, qidPath, sink, read)
    {
        _arity = arity;
        _build = build;
    }

    /// <summary>Creates a vector control expecting exactly <paramref name="arity"/> components.</summary>
    public static VectorControlFile Create(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        int arity, Func<IReadOnlyList<double>, SimCommand> build)
        => new(name, qidPath, sink, read, arity, build);

    /// <inheritdoc />
    protected override SimCommand? Parse(string token)
    {
        var parts = token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != _arity)
            return null;

        var values = new double[_arity];
        for (var i = 0; i < _arity; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || !double.IsFinite(values[i]))
                return null;
        }

        return _build(values);
    }
}
