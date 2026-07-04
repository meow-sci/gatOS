namespace gatOS.SimFs.Commands;

/// <summary>
///     The game-free validation rules for the <c>debug.impulse</c> action (the
///     <c>debug/vessels/&lt;id&gt;/impulse</c> node): a finite 3-vector plus two optional keywords —
///     the frame the vector is expressed in (<see cref="FrameCci"/>, the default, or
///     <see cref="FrameBody"/>) and its unit (<see cref="UnitNs"/> newton-seconds, the default, or
///     <see cref="UnitDv"/> Δv in m/s). Lives here rather than in the game-coupled actuator so the
///     EINVAL boundary is unit-testable without game assemblies — the HTTP/MQTT <c>command</c>
///     paths reach the actuator without the 9p control-file parse, so the actuator re-validates
///     through these same rules.
/// </summary>
public static class ImpulseRules
{
    /// <summary>Frame keyword: the vector is in the parent body's CCI frame (the default).</summary>
    public const string FrameCci = "cci";

    /// <summary>Frame keyword: the vector is in the vessel body frame (+X = nose/thrust axis).</summary>
    public const string FrameBody = "body";

    /// <summary>Unit keyword: the vector is an impulse in newton-seconds, Δv = J/mass (the default).</summary>
    public const string UnitNs = "ns";

    /// <summary>Unit keyword: the vector is a Δv in m/s, applied as-is.</summary>
    public const string UnitDv = "dv";

    /// <summary>
    ///     Validates a <c>debug.impulse</c> payload: exactly three finite components, and the
    ///     frame/unit keywords (null/empty = default) drawn from the known sets. Returns the
    ///     EINVAL detail message, or null when valid.
    /// </summary>
    public static string? Validate(IReadOnlyList<double> vector, string? frame, string? unit)
    {
        if (vector.Count != 3)
            return "impulse expects 'x y z [cci|body] [ns|dv]'";
        foreach (var component in vector)
            if (!double.IsFinite(component))
                return "impulse components must be finite";
        if (frame is not (null or "" or FrameCci or FrameBody))
            return $"unknown impulse frame '{frame}' (cci|body)";
        if (unit is not (null or "" or UnitNs or UnitDv))
            return $"unknown impulse unit '{unit}' (ns|dv)";
        return null;
    }

    /// <summary>Whether the vector is in the vessel body frame (vs the default CCI).</summary>
    public static bool IsBodyFrame(string? frame) => frame == FrameBody;

    /// <summary>Whether the vector is a direct Δv in m/s (vs the default newton-seconds).</summary>
    public static bool IsDeltaV(string? unit) => unit == UnitDv;
}
