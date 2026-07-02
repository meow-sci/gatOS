namespace gatOS.SimFs.Commands;

/// <summary>
///     The game-free validation rule for the <c>vessel.scale</c> action (the
///     <c>vessels/by-id/&lt;id&gt;/scale</c> node): a uniform model scale factor must be finite and
///     strictly positive — deliberately no upper clamp, arbitrarily large factors are allowed.
///     Lives here rather than in the game-coupled actuator so the EINVAL path is unit-testable
///     without game assemblies.
/// </summary>
public static class ScaleRules
{
    /// <summary>Whether <paramref name="factor"/> is a valid uniform model scale (finite, &gt; 0).</summary>
    public static bool IsValid(double factor) => double.IsFinite(factor) && factor > 0;
}
