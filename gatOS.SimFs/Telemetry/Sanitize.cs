namespace gatOS.SimFs.Telemetry;

/// <summary>
///     Value scrubbing for the sampler (OS_PLAN.md T9.1, pure part). Snapshot records carry
///     plain finite doubles by contract (T8.1) — KSA telemetry reads can be NaN/Inf as a
///     matter of course (orbital fields on escape trajectories, mid-teardown vehicles).
/// </summary>
public static class Sanitize
{
    /// <summary>The value, or 0 when NaN/±Inf.</summary>
    public static double Finite(double value) => double.IsFinite(value) ? value : 0;

    /// <summary>
    ///     Converts a from-body-center radius (KSA's <c>Orbit.Apoapsis</c>/<c>Periapsis</c>
    ///     convention) to an above-surface altitude; non-finite radii (escape trajectories)
    ///     sanitize to 0.
    /// </summary>
    public static double RadiusToAltitude(double radiusMeters, double meanRadiusMeters)
        => double.IsFinite(radiusMeters) && double.IsFinite(meanRadiusMeters)
            ? radiusMeters - meanRadiusMeters
            : 0;
}
