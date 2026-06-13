namespace gatOS.SimFs.Snapshots;

// The immutable telemetry model (OS_PLAN.md T8.1). Plain doubles/strings only — no game
// types, no NaN/Infinity (the M9 sampler sanitizes before publishing). These shapes are a
// user-facing API surface once formatted into /sim files; change them deliberately.

/// <summary>One published frame of simulation telemetry.</summary>
/// <param name="Sequence">Monotonically increasing publish counter (0 = the empty snapshot).</param>
/// <param name="UtSeconds">Universal sim time in seconds.</param>
/// <param name="WarpFactor">The current time-warp factor.</param>
/// <param name="ActiveVesselId">Id of the controlled vessel; null when none.</param>
/// <param name="Vessels">All vessels, stable order.</param>
/// <param name="NewEvents">Events that occurred since the previous snapshot (M9 diffs them).</param>
/// <param name="GameVersion">KSA version string at sample time (<c>/sim/status/game_version</c>); "" when unknown.</param>
/// <param name="SampleRateHz">The sampler's configured cadence, for <c>/sim/status/sampler</c>.</param>
/// <param name="Accessors">
///     Per-accessor health: one entry per integration accessor that is currently degraded
///     (faulted on the game side). Empty when every accessor is healthy
///     (<c>/sim/status/accessors</c>).
/// </param>
public sealed record SimSnapshot(
    long Sequence,
    double UtSeconds,
    double WarpFactor,
    string? ActiveVesselId,
    IReadOnlyList<VesselSnapshot> Vessels,
    IReadOnlyList<SimEvent> NewEvents,
    string GameVersion,
    double SampleRateHz,
    IReadOnlyList<AccessorHealthSnapshot> Accessors)
{
    /// <summary>The pre-first-publish snapshot: sequence 0, no vessels, no events.</summary>
    public static SimSnapshot Empty { get; } = new(0, 0, 1, null, [], [], "", 0, []);
}

/// <summary>One vessel's telemetry.</summary>
/// <param name="Id">The stable vehicle id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Situation">Situation string ("Freefall", "Landed", …).</param>
/// <param name="PositionCci">Position in the CCI frame, meters.</param>
/// <param name="LatitudeDeg">Geodetic latitude, degrees.</param>
/// <param name="LongitudeDeg">Geodetic longitude, degrees.</param>
/// <param name="OrbitalSpeed">Orbital speed, m/s.</param>
/// <param name="SurfaceSpeed">Surface-relative speed, m/s.</param>
/// <param name="InertialSpeed">Inertial speed, m/s.</param>
/// <param name="AttitudeBody2Cci">Body→CCI attitude quaternion.</param>
/// <param name="BodyRatesRadS">Body rotation rates, rad/s.</param>
/// <param name="BarometricAltitude">Barometric altitude, meters.</param>
/// <param name="RadarAltitude">Radar altitude, meters.</param>
/// <param name="MassTotal">Total mass, kg.</param>
/// <param name="MassDry">Dry mass, kg.</param>
/// <param name="MassPropellant">Propellant mass, kg.</param>
/// <param name="Orbit">Orbit elements; null when not meaningfully in orbit.</param>
/// <param name="Engines">Engines, by index.</param>
/// <param name="Tanks">Tanks, by resource.</param>
/// <param name="BatteryChargeFraction">Battery charge 0..1; null when no battery.</param>
/// <param name="ParentBodyName">Name of the parent body; null when unknown.</param>
/// <param name="LightsMasterOn">The vessel's master lights flag (<c>Vehicle.LightsOn</c>).</param>
/// <param name="Animations">
///     Keyframe animations (deploy/retract actuators), by vessel-level ordinal. Solar-panel
///     deploy animations are flagged so the tree can surface them under <c>solar/</c> too.
/// </param>
public sealed record VesselSnapshot(
    string Id,
    string Name,
    string Situation,
    double3Snap PositionCci,
    double LatitudeDeg,
    double LongitudeDeg,
    double OrbitalSpeed,
    double SurfaceSpeed,
    double InertialSpeed,
    QuatSnap AttitudeBody2Cci,
    double3Snap BodyRatesRadS,
    double BarometricAltitude,
    double RadarAltitude,
    double MassTotal,
    double MassDry,
    double MassPropellant,
    OrbitSnapshot? Orbit,
    IReadOnlyList<EngineSnapshot> Engines,
    IReadOnlyList<TankSnapshot> Tanks,
    double? BatteryChargeFraction,
    string? ParentBodyName,
    bool LightsMasterOn,
    IReadOnlyList<AnimationSnapshot> Animations);

/// <summary>Orbit elements (altitudes, not radii — the sampler converts).</summary>
/// <param name="ApoapsisAltitude">Apoapsis altitude above the parent body surface, meters.</param>
/// <param name="PeriapsisAltitude">Periapsis altitude above the parent body surface, meters.</param>
/// <param name="Eccentricity">Eccentricity.</param>
/// <param name="InclinationDeg">Inclination, degrees.</param>
/// <param name="SmaMeters">Semi-major axis, meters.</param>
/// <param name="PeriodSeconds">Orbital period, seconds.</param>
public sealed record OrbitSnapshot(
    double ApoapsisAltitude,
    double PeriapsisAltitude,
    double Eccentricity,
    double InclinationDeg,
    double SmaMeters,
    double PeriodSeconds);

/// <summary>One engine's state.</summary>
/// <param name="Index">Stable per-vessel engine index.</param>
/// <param name="Active">Whether the engine is active.</param>
/// <param name="VacThrustN">Vacuum thrust, newtons.</param>
/// <param name="IspS">Specific impulse, seconds.</param>
public sealed record EngineSnapshot(int Index, bool Active, double VacThrustN, double IspS);

/// <summary>One tank's state.</summary>
/// <param name="Resource">Resource name.</param>
/// <param name="Amount">Current amount.</param>
/// <param name="Capacity">Capacity.</param>
public sealed record TankSnapshot(string Resource, double Amount, double Capacity);

/// <summary>
///     One keyframe animation (deploy/retract actuator). The goal/current fractions are
///     0 (retracted) … 1 (deployed); <see cref="Index"/> is the stable vessel-level ordinal the
///     control command addresses.
/// </summary>
/// <param name="Index">Stable per-vessel animation ordinal (PartTree enumeration order).</param>
/// <param name="GoalFraction">Commanded deploy fraction 0..1 (the setpoint a STATE read returns).</param>
/// <param name="CurrentFraction">Actual deploy fraction 0..1 (animation position).</param>
/// <param name="DeploymentState">"Deployed", "Retracted", "Deploying", "Retracting", "Broken".</param>
/// <param name="IsSolar">Whether this animation deploys a solar panel (surfaced under solar/ too).</param>
public sealed record AnimationSnapshot(
    int Index, double GoalFraction, double CurrentFraction, string DeploymentState, bool IsSolar);

/// <summary>
///     Health of one KSA integration accessor (reader or actuator). Present only while the
///     accessor is degraded after a fault (<c>/sim/status/accessors</c>); the snapshot omits
///     healthy accessors entirely.
/// </summary>
/// <param name="Name">The accessor's stable name (e.g. "reader.vessel.orbit").</param>
/// <param name="SinceUtSeconds">Sim time the accessor first faulted.</param>
/// <param name="Error">The fault message (first occurrence).</param>
public sealed record AccessorHealthSnapshot(string Name, double SinceUtSeconds, string Error);

/// <summary>One discrete event (situation change, vessel appeared, …; types fixed in T8.3).</summary>
/// <param name="UtSeconds">Sim time of the snapshot that carried the event.</param>
/// <param name="Type">Event type string (e.g. "situation-change").</param>
/// <param name="VesselId">The vessel concerned; null for global events.</param>
/// <param name="Detail">Human-readable detail (e.g. "Landed→Freefall").</param>
public sealed record SimEvent(double UtSeconds, string Type, string? VesselId, string Detail);

/// <summary>A plain 3-vector (named per the Brutal double3 convention).</summary>
/// <param name="X">X component.</param>
/// <param name="Y">Y component.</param>
/// <param name="Z">Z component.</param>
public readonly record struct double3Snap(double X, double Y, double Z);

/// <summary>A plain quaternion.</summary>
/// <param name="X">X component.</param>
/// <param name="Y">Y component.</param>
/// <param name="Z">Z component.</param>
/// <param name="W">W component.</param>
public readonly record struct QuatSnap(double X, double Y, double Z, double W);
