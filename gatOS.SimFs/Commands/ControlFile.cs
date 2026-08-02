using System.Globalization;

namespace gatOS.SimFs.Commands;

/// <summary>
///     The <b>STATE</b> control archetype (KSA_GAME_INTEGRATION_PLAN Part 2): a read shows the
///     live setting (e.g. an engine's <c>active</c> flag or a panel's deploy fraction), a write
///     sets a new one. The Linux analogue is a gpio <c>value</c> or led <c>brightness</c> file —
///     idempotent, the same write twice is harmless. Values are a <c>0</c>/<c>1</c> flag, a
///     <c>0..1</c> fraction, any finite real, or a real inside a declared inclusive range;
///     out-of-range or unparseable input fails the write with EINVAL.
/// </summary>
public sealed class ControlFile : CommandFile
{
    private readonly Kind _kind;
    private readonly double _min;
    private readonly double _max;
    private readonly Func<double, SimCommand> _build;

    private ControlFile(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Kind kind, double min, double max, Func<double, SimCommand> build)
        : base(name, qidPath, sink, read)
    {
        _kind = kind;
        _min = min;
        _max = max;
        _build = build;
    }

    private enum Kind
    {
        Flag,
        Fraction,
        Number,
        Ranged,
    }

    /// <summary>A boolean setpoint: accepts exactly <c>0</c> or <c>1</c>.</summary>
    public static ControlFile Flag(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Func<double, SimCommand> build)
        => new(name, qidPath, sink, read, Kind.Flag, 0, 1, build);

    /// <summary>A continuous setpoint: accepts a real number in <c>[0, 1]</c>.</summary>
    public static ControlFile Fraction(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Func<double, SimCommand> build)
        => new(name, qidPath, sink, read, Kind.Fraction, 0, 1, build);

    /// <summary>An unbounded numeric setpoint: accepts any finite real (e.g. light intensity).</summary>
    public static ControlFile Number(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        Func<double, SimCommand> build)
        => new(name, qidPath, sink, read, Kind.Number, double.NegativeInfinity, double.PositiveInfinity, build);

    /// <summary>
    ///     A numeric setpoint constrained to an arbitrary <b>inclusive</b> range — the archetype
    ///     the catalog-driven FX-editor leaves use, where each field declares its own bounds
    ///     (infinite bounds are allowed and degrade to <see cref="Number"/>).
    /// </summary>
    public static ControlFile Ranged(string name, ulong qidPath, ICommandSink sink, Func<string> read,
        double min, double max, Func<double, SimCommand> build)
        => new(name, qidPath, sink, read, Kind.Ranged, min, max, build);

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
            case Kind.Ranged when value >= _min && value <= _max:
                return _build(value);
            default:
                return null;
        }
    }
}
