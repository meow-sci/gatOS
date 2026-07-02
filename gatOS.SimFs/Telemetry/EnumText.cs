using System.Collections.Concurrent;

namespace gatOS.SimFs.Telemetry;

/// <summary>
///     Cached enum-name strings for the telemetry hot path
///     (plans/GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP3). The sampler stringifies the same
///     stable enum values (Situation, attitude mode/frame, RCS control maps, animation deployment
///     states) every tick; <c>Enum.ToString()</c> allocates a fresh string each call, which at a
///     few dozen per vessel per tick is the sampler's dominant hidden string cost. This memoizes
///     the name per distinct value (including <c>[Flags]</c> combinations like "Pitch|Yaw"), so the
///     steady state serves interned strings with zero allocation.
/// </summary>
public static class EnumText
{
    /// <summary>The cached name of <paramref name="value"/> (identical to <c>value.ToString()</c>).</summary>
    public static string Of<T>(T value) where T : struct, Enum => Cache<T>.Names.GetOrAdd(value, Stringify);

    private static string Stringify<T>(T value) where T : struct, Enum => value.ToString();

    private static class Cache<T> where T : struct, Enum
    {
        // Bounded by the distinct values (and flag combinations) actually observed — a handful per
        // enum type in practice. EqualityComparer<T>.Default on an enum key does not box.
        internal static readonly ConcurrentDictionary<T, string> Names = new();
    }
}
