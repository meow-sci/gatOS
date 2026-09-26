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
    ///     convention) to an above-surface altitude; non-finite radii sanitize to 0. An escape
    ///     trajectory's apoapsis is <b>not</b> reliably non-finite — KSA's state-vector orbit build
    ///     stores <c>a·(1+e)</c>, a finite large negative radius when <c>a &lt; 0</c> — so apoapsis
    ///     callers go through <see cref="ApoapsisToAltitude"/> instead.
    /// </summary>
    public static double RadiusToAltitude(double radiusMeters, double meanRadiusMeters)
        => double.IsFinite(radiusMeters) && double.IsFinite(meanRadiusMeters)
            ? radiusMeters - meanRadiusMeters
            : 0;

    /// <summary>
    ///     The apoapsis altitude, or 0 when the orbit has no apoapsis (<paramref name="isBound"/>
    ///     false: hyperbolic/parabolic). Mirrors the gate KSA's own Universe Manifest adopted at
    ///     rev 5439 (<c>Orbit.IsBound()</c>) and the <c>time_to_ap</c> "0 when none" contract.
    /// </summary>
    public static double ApoapsisToAltitude(double apoapsisRadiusMeters, double meanRadiusMeters, bool isBound)
        => isBound ? RadiusToAltitude(apoapsisRadiusMeters, meanRadiusMeters) : 0;
}
