using System.Globalization;

namespace gatOS.SimFs.Commands;

/// <summary>
///     The <b>STATE</b> control archetype (KSA_GAME_INTEGRATION_PLAN Part 2): a read shows the
///     live setting (e.g. an engine's <c>active</c> flag or a panel's deploy fraction), a write
///     sets a new one. The Linux analogue is a gpio <c>value</c> or led <c>brightness</c> file —
///     idempotent, the same write twice is harmless. Values are either a <c>0</c>/<c>1</c> flag or
///     a <c>0..1</c> fraction; out-of-range or unparseable input fails the write with EINVAL.
/// </summary>
public sealed class ControlFile : CommandFile
{
    private readonly Kind _kind;
    private readonly Func<double, SimCommand> _build;

    private ControlFile(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Kind kind, Func<double, SimCommand> build)
        : base(name, qidPath, sink, read)
    {
        _kind = kind;
        _build = build;
    }

    private enum Kind
    {
        Flag,
        Fraction,
        Number,
    }

    /// <summary>A boolean setpoint: accepts exactly <c>0</c> or <c>1</c>.</summary>
    public static ControlFile Flag(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Func<double, SimCommand> build)
        => new(name, qidPath, sink, read, Kind.Flag, build);

    /// <summary>A continuous setpoint: accepts a real number in <c>[0, 1]</c>.</summary>
    public static ControlFile Fraction(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Func<double, SimCommand> build)
        => new(name, qidPath, sink, read, Kind.Fraction, build);

    /// <summary>An unbounded numeric setpoint: accepts any finite real (e.g. light intensity).</summary>
    public static ControlFile Number(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Func<double, SimCommand> build)
        => new(name, qidPath, sink, read, Kind.Number, build);

    /// <inheritdoc />
    protected override SimCommand? Parse(string token)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
            return null;

        switch (_kind)
        {
            case Kind.Flag when value is 0 or 1:
                return _build(value);
            case Kind.Fraction when value is >= 0 and <= 1:
                return _build(value);
            case Kind.Number:
                return _build(value);
            default:
                return null;
        }
    }
}
