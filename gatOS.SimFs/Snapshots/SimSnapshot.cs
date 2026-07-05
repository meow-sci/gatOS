namespace gatOS.SimFs.Snapshots;

// The immutable telemetry model (OS_PLAN.md T8.1). Plain doubles/strings only — no game
// types, no NaN/Infinity (the M9 sampler sanitizes before publishing). These shapes are a
// user-facing API surface once formatted into /sim files; change them deliberately.
//
// The G1/G2 (status, animations) and G3 (bodies, system, navball, environment, orbit-extras,
// per-module reads) additions are carried as init-only properties on the existing positional
// records (KSA_GAME_INTEGRATION_PLAN Parts 4–5). Init-only keeps every existing construction
// site valid — the M9 core fields stay positional; the readers fill the rest with object
// initializers; tests that build a bare vessel keep working (the extras default empty).

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

    /// <summary>Sim seconds advanced by the last tick (<c>/sim/time/sim_dt</c>); <c>0</c> ⇒ effectively paused.</summary>
    public double SimDtSeconds { get; init; }

    /// <summary>The discrete time-warp factors the game offers (<c>/sim/time/warp_speeds</c>).</summary>
    public IReadOnlyList<double> WarpSpeeds { get; init; } = [];

    /// <summary>Whether an auto-warp-to-time is running (<c>/sim/time/auto_warp</c>).</summary>
    public bool AutoWarpActive { get; init; }

    /// <summary>The target sim time auto-warp is heading to, or <c>0</c> when idle.</summary>
    public double AutoWarpTargetUt { get; init; }

    /// <summary>The celestial bodies in the current system (<c>/sim/bodies</c>); empty until sampled.</summary>
    public IReadOnlyList<BodySnapshot> Bodies { get; init; } = [];

    /// <summary>The current star-system summary (<c>/sim/system</c>); null until sampled.</summary>
    public SystemSnapshot? System { get; init; }

    /// <summary>
    ///     Active welds (the <c>/sim/debug/welds</c> cheat): each is a source vessel rigidly tracking a
    ///     part on a target vessel. Empty when none; only ever populated when the debug namespace is on.
    /// </summary>
    public IReadOnlyList<WeldSnapshot> Welds { get; init; } = [];

    /// <summary>
    ///     Whether the global "always render IVA" cheat is on (<c>/sim/debug/always_render_iva</c>):
    ///     interior part meshes render outside the IVA camera. A render hack, off by default.
    /// </summary>
    public bool AlwaysRenderIva { get; init; }

    /// <summary>
    ///     Active thug-life sunglasses quads (the <c>/sim/debug/thug_life</c> cheat): each is a textured
    ///     meme quad anchored to a part on a vessel, drawn into the scene each frame. Empty when none;
    ///     only ever populated when the debug namespace is on. A pure cosmetic runtime cheat; never persisted.
    /// </summary>
    public IReadOnlyList<ThugLifeSnapshot> ThugLife { get; init; } = [];
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
    IReadOnlyList<AnimationSnapshot> Animations)
{
    // ---- G3 read extensions (KSA_GAME_INTEGRATION_PLAN §4.5/§4.6) -------------------------

    /// <summary>Position in the parent body's ecliptic frame, meters.</summary>
    public double3Snap PositionEcl { get; init; }

    /// <summary>Velocity in the CCI frame, m/s (the vector behind <see cref="OrbitalSpeed"/>).</summary>
    public double3Snap VelocityCci { get; init; }

    /// <summary>Center of mass in the assembly frame, meters.</summary>
    public double3Snap CenterOfMass { get; init; }

    /// <summary>Whether this vessel is the player-controlled one (<c>controlled</c> flag).</summary>
    public bool Controlled { get; init; }

    /// <summary>
    ///     Whether KSA will accept flight-control + flight-computer commands for this vessel
    ///     (<c>Vehicle.IsControllable</c>: has a Control Module, or the debug override). A vessel
    ///     reading <c>0</c> here silently ignores throttle/stage/attitude/burn/RCS/ignite — gatOS
    ///     does not gate; it relies on KSA's own lockout, so this is the pre-check flag for guests
    ///     and autopilots. The player-controlled vessel is always controllable. (KSA 2026.6.9.4750,
    ///     rev 4699.)
    /// </summary>
    public bool Controllable { get; init; }

    /// <summary>NavBall-derived attitude/performance; null when unavailable.</summary>
    public NavballSnapshot? Navball { get; init; }

    /// <summary>Vessel-level manual throttle setpoint 0..1 (<c>ctl/throttle</c> read).</summary>
    public double ThrottleCmd { get; init; }

    /// <summary>Uniform vessel model scale factor (<c>scale</c> read; best-effort). 1.0 = unscaled.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>
    ///     Whether this vessel is force-rendered at any distance (<c>always_render</c> read): the
    ///     render-distance override that bypasses KSA's sub-pixel cull. Off by default.
    /// </summary>
    public bool AlwaysRender { get; init; }

    /// <summary>The flight computer's attitude track-target name (<c>ctl/attitude_mode</c> read).</summary>
    public string AttitudeMode { get; init; } = "";

    /// <summary>The flight computer's attitude reference frame name (<c>ctl/attitude_frame</c> read).</summary>
    public string AttitudeFrame { get; init; } = "";

    /// <summary>Whether any RCS thruster controller is active (<c>ctl/rcs</c> read).</summary>
    public bool RcsOn { get; init; }

    /// <summary>
    ///     The latched manual RCS translation command as body-axis signs (<c>ctl/translate</c>
    ///     read): each component −1/0/+1 for commanded thrust along body X (+ = forward/nose),
    ///     Y (+ = right), Z (+ = down). <c>(0,0,0)</c> = no translation commanded.
    /// </summary>
    public double3Snap TranslateCmd { get; init; }

    /// <summary>
    ///     Whether the vessel's main engines are ignited — the master that <c>ctl/ignite</c> and
    ///     <c>ctl/shutdown</c> toggle (KSA <c>EngineOn</c>, read via <c>IsSet(VehicleEngine.MainIgnite)</c>).
    ///     The reactive <c>ctl/engine</c> read; not the per-engine "allowed to fire" <c>engines/n/active</c>.
    /// </summary>
    public bool EngineOn { get; init; }

    /// <summary>Local physics environment (pressure, density, accelerations); null when unavailable.</summary>
    public EnvironmentSnapshot? Environment { get; init; }

    /// <summary>Total instantaneous electrical power produced, watts.</summary>
    public double PowerProducedW { get; init; }

    /// <summary>Total instantaneous electrical power consumed, watts.</summary>
    public double PowerConsumedW { get; init; }

    /// <summary>Battery capacity in joules; null when no battery.</summary>
    public double? BatteryCapacityJoules { get; init; }

    /// <summary>RCS thruster controllers, by index.</summary>
    public IReadOnlyList<RcsSnapshot> Rcs { get; init; } = [];

    /// <summary>Solar panels, by index.</summary>
    public IReadOnlyList<SolarSnapshot> Solar { get; init; } = [];

    /// <summary>Power generators, by index.</summary>
    public IReadOnlyList<GeneratorSnapshot> Generators { get; init; } = [];

    /// <summary>Lights, by index (deterministic PartTree order).</summary>
    public IReadOnlyList<LightSnapshot> Lights { get; init; } = [];

    /// <summary>Docking ports, by index.</summary>
    public IReadOnlyList<DockingSnapshot> Docking { get; init; } = [];

    /// <summary>Decouplers, by index.</summary>
    public IReadOnlyList<DecouplerSnapshot> Decouplers { get; init; } = [];

    /// <summary>Upcoming encounters / closest approaches on the current patch.</summary>
    public IReadOnlyList<EncounterSnapshot> Encounters { get; init; } = [];

    /// <summary>
    ///     Top-level parts (<c>Vehicle.Parts.Parts</c>), surfaced under <c>parts/&lt;n&gt;/</c> so guests
    ///     can discover a part to anchor a weld to. Subparts are not exposed. Empty when the parts
    ///     stream is gated off (<c>telemetry_vessel_parts</c>); the reader caches it per vehicle and
    ///     rebuilds on part-count change (or every 10 s).
    /// </summary>
    public IReadOnlyList<PartSnapshot> Parts { get; init; } = [];
}

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
    double PeriodSeconds)
{
    /// <summary>Longitude of the ascending node, degrees.</summary>
    public double LanDeg { get; init; }

    /// <summary>Argument of periapsis, degrees.</summary>
    public double ArgPeDeg { get; init; }

    /// <summary>True anomaly, degrees.</summary>
    public double TrueAnomalyDeg { get; init; }

    /// <summary>Seconds until the next apoapsis (0 when unbound/unknown).</summary>
    public double TimeToApoapsis { get; init; }

    /// <summary>Seconds until the next periapsis (0 when unbound/unknown).</summary>
    public double TimeToPeriapsis { get; init; }

    /// <summary>Sim time of the next patch transition (SOI change/escape); 0 when none.</summary>
    public double NextPatchEventUt { get; init; }
}

/// <summary>One engine's state.</summary>
/// <param name="Index">Stable per-vessel engine index.</param>
/// <param name="Active">Whether the engine is active.</param>
/// <param name="VacThrustN">Vacuum thrust, newtons.</param>
/// <param name="IspS">Specific impulse, seconds.</param>
public sealed record EngineSnapshot(int Index, bool Active, double VacThrustN, double IspS)
{
    /// <summary>Commanded throttle 0..1 (<c>EngineControllerState.CommandThrottle</c>).</summary>
    public double ThrottleCmd { get; init; }

    /// <summary>Whether propellant is currently available to the engine.</summary>
    public bool PropellantAvailable { get; init; }

    /// <summary>Configured minimum throttle 0..1 (the deep-throttle floor; also writable).</summary>
    public double MinThrottle { get; init; }
}

/// <summary>One tank's state.</summary>
/// <param name="Resource">Resource name.</param>
/// <param name="Amount">Current amount.</param>
/// <param name="Capacity">Capacity.</param>
public sealed record TankSnapshot(string Resource, double Amount, double Capacity)
{
    /// <summary>Fill fraction 0..1 (<c>Mole.FilledFraction</c>).</summary>
    public double Fraction { get; init; }
}

/// <summary>One RCS thruster controller's state.</summary>
/// <param name="Index">Stable per-vessel RCS index.</param>
/// <param name="Active">Whether the controller is active.</param>
/// <param name="PropellantAvailable">Whether propellant is available.</param>
/// <param name="ControlMap">The active control-axis flags as text (e.g. "Pitch|Yaw").</param>
public sealed record RcsSnapshot(int Index, bool Active, bool PropellantAvailable, string ControlMap);

/// <summary>One solar panel's state.</summary>
/// <param name="Index">Stable per-vessel solar index.</param>
/// <param name="ProducedW">Instantaneous power produced, watts.</param>
/// <param name="Occluded">Whether the panel is occluded.</param>
/// <param name="SunAoaDeg">Sun angle-of-attack, degrees.</param>
/// <param name="Efficiency">Sun efficiency 0..1.</param>
/// <param name="HasTracker">Whether a sun tracker is fitted.</param>
/// <param name="TrackerAngleDeg">The tracker's current angle, degrees (0 when no tracker).</param>
/// <param name="AnimationIndex">
///     The vessel-level animation ordinal of this panel's deploy animation, or
///     <see cref="NoAnimation"/> for a fixed panel. The tree maps <c>solar/&lt;n&gt;/goal</c> to it.
/// </param>
public sealed record SolarSnapshot(
    int Index, double ProducedW, bool Occluded, double SunAoaDeg, double Efficiency,
    bool HasTracker, double TrackerAngleDeg, int AnimationIndex = SolarSnapshot.NoAnimation)
{
    /// <summary>Sentinel <see cref="AnimationIndex"/> for a fixed (non-deployable) panel.</summary>
    public const int NoAnimation = -1;
}

/// <summary>One generator's state.</summary>
/// <param name="Index">Stable per-vessel generator index.</param>
/// <param name="Active">Whether it is producing.</param>
/// <param name="ProducedW">Instantaneous power produced, watts.</param>
public sealed record GeneratorSnapshot(int Index, bool Active, double ProducedW);

/// <summary>One light's state.</summary>
/// <param name="Index">Stable per-vessel light index.</param>
/// <param name="On">Whether the light is on.</param>
/// <param name="Intensity">Light intensity (template units).</param>
/// <param name="Color">RGB color, each 0..1.</param>
/// <param name="AnimationIndex">
///     The vessel-level animation ordinal of this light part's actuate/deploy animation, or
///     <see cref="NoAnimation"/> when the light part has none. The tree co-locates the deploy
///     <c>goal</c>/<c>current</c>/<c>state</c> control onto <c>lights/&lt;n&gt;/</c> when set — the
///     same animation also reachable under <c>animations/&lt;n&gt;/</c> (mirrors <see cref="SolarSnapshot"/>).
/// </param>
public sealed record LightSnapshot(
    int Index, bool On, double Intensity, double3Snap Color,
    int AnimationIndex = LightSnapshot.NoAnimation)
{
    /// <summary>Sentinel <see cref="AnimationIndex"/> for a light with no actuate animation.</summary>
    public const int NoAnimation = -1;

    /// <summary>
    ///     The spotlight cone <b>outer</b> half-angle in degrees (<c>LightModule.Template.OuterAngle</c>,
    ///     stored in radians by KSA). The hard edge of the beam — beyond it the cone contributes
    ///     nothing. Larger ⇒ wider beam; the stock default is 45°. Writable via
    ///     <c>lights/&lt;n&gt;/outer_angle</c> (action <c>light.outer_angle</c>); KSA clamps the
    ///     effective value to ~0..89.94°. Only affects spotlights — point lights carry it but ignore
    ///     it when rendering.
    /// </summary>
    public double OuterAngleDeg { get; init; }

    /// <summary>
    ///     The spotlight cone <b>inner</b> half-angle in degrees (<c>LightModule.Template.InnerAngle</c>,
    ///     stored in radians by KSA). Inside it the beam is at full brightness; between inner and outer
    ///     it falls off. Smaller relative to <see cref="OuterAngleDeg"/> ⇒ softer edge; equal ⇒ a hard
    ///     edge. To make a narrow pinpoint/laser the inner angle must come down with the outer (KSA
    ///     swaps the two if inner &gt; outer). Writable via <c>lights/&lt;n&gt;/inner_angle</c> (action
    ///     <c>light.inner_angle</c>); KSA clamps it to <c>[0, outer]</c>.
    /// </summary>
    public double InnerAngleDeg { get; init; }
}

/// <summary>One docking port's state.</summary>
/// <param name="Index">Stable per-vessel docking-port index.</param>
/// <param name="Docked">Whether it is docked.</param>
/// <param name="DockedToPart">The part id it is docked to, or null.</param>
public sealed record DockingSnapshot(int Index, bool Docked, string? DockedToPart)
{
    /// <summary>
    ///     The separation impulse this port applies when it undocks, in newton-seconds (N·s) — the
    ///     value <c>DockingPort.Undock</c> hands to <c>Vehicle.Split(Connector, splitImpulse)</c>.
    ///     Seeded from the part's XML (<c>PushoffImpulse</c>, stock 7000 N·s) and overwritable live
    ///     via the debug control. (KSA 2026.6.9.4750 renamed <c>PushoffForce</c> → <c>PushoffImpulse</c>
    ///     and changed the quantity from force (N) to impulse (N·s).)
    /// </summary>
    public double PushoffImpulseNs { get; init; }
}

/// <summary>One decoupler's state.</summary>
/// <param name="Index">Stable per-vessel decoupler index.</param>
/// <param name="Fired">Whether the decoupler has fired (irreversible).</param>
public sealed record DecouplerSnapshot(int Index, bool Fired);

/// <summary>A predicted encounter / closest approach with another body on the current patch.</summary>
/// <param name="Body">The other body's id.</param>
/// <param name="Ut">Sim time of closest approach.</param>
/// <param name="DistanceMeters">Closest-approach distance, meters.</param>
public sealed record EncounterSnapshot(string Body, double Ut, double DistanceMeters);

/// <summary>
///     One top-level part (<c>Vehicle.Parts.Parts</c>) — the anchor picker for the welds feature
///     (<c>/sim/vessels/by-id/&lt;id&gt;/parts/&lt;n&gt;/</c>). Subparts are deliberately not surfaced.
/// </summary>
/// <param name="Index">
///     Per-vessel part index in PartTree enumeration order — the friendly directory name. <b>Not</b>
///     stable across vehicle edits; the stable handle is <see cref="InstanceId"/>.
/// </param>
/// <param name="InstanceId">
///     Runtime-unique part id (<c>Part.InstanceId</c>) — the stable handle a weld anchors to. Pass it
///     to <c>debug/vessels/&lt;id&gt;/weld</c>.
/// </param>
/// <param name="Id">The part id string (<c>Part.Id</c>); can collide across instances of one template.</param>
/// <param name="DisplayName">Human-readable name (<c>Part.DisplayName</c>).</param>
/// <param name="Template">The part template id (<c>Part.Template.Id</c>).</param>
/// <param name="IsRoot">Whether this is the root part (<c>Part.PartParent == null</c>).</param>
/// <param name="SubpartCount">Number of subparts (informational; subparts are not exposed as nodes).</param>
/// <param name="PositionVehicleAsmb">Part position in the vehicle assembly frame, meters.</param>
public sealed record PartSnapshot(
    int Index, uint InstanceId, string Id, string DisplayName, string Template,
    bool IsRoot, int SubpartCount, double3Snap PositionVehicleAsmb);

/// <summary>
///     One active weld (<c>/sim/debug/welds</c>): a source vessel rigidly tracking a part on a target
///     vessel. A pure runtime cheat; never persisted.
/// </summary>
/// <param name="SourceId">The welded (following) vessel id.</param>
/// <param name="TargetId">The anchor vessel id.</param>
/// <param name="PartInstanceId">
///     The anchor part's <c>InstanceId</c>, or <c>0</c> when anchored to the target's body/CoM frame.
/// </param>
/// <param name="Offset">Position offset expressed in the anchor frame, meters.</param>
/// <param name="Rotation">Orientation offset relative to the anchor, Euler pitch/yaw/roll degrees.</param>
/// <param name="LockRotation">true ⇒ orientation locked to the anchor; false ⇒ only position held.</param>
/// <param name="Enabled">false ⇒ suspended (kept in the registry, no physics applied).</param>
public sealed record WeldSnapshot(
    string SourceId, string TargetId, uint PartInstanceId, double3Snap Offset, double3Snap Rotation,
    bool LockRotation, bool Enabled);

/// <summary>
///     One active thug-life sunglasses quad (<c>/sim/debug/thug_life/&lt;id&gt;</c>): a textured meme
///     quad anchored to a part on a vessel, rebuilt and drawn each frame. A pure cosmetic cheat; never
///     persisted.
/// </summary>
/// <param name="Id">The integer handle (the smallest free slot at create; reused after remove/clear) — the directory name.</param>
/// <param name="VesselId">The anchor vessel id.</param>
/// <param name="PartInstanceId">
///     The anchor part's <c>InstanceId</c>, or <c>0</c> when anchored to the vessel's body/assembly frame.
/// </param>
/// <param name="Position">Position offset in the anchor part's local frame, meters.</param>
/// <param name="Rotation">Orientation offset in the part's local frame, Euler pitch/yaw/roll degrees.</param>
/// <param name="Width">Quad width, meters.</param>
/// <param name="Height">Quad height, meters.</param>
/// <param name="Visible">false ⇒ the entry is kept but skipped while drawing.</param>
public sealed record ThugLifeSnapshot(
    int Id, string VesselId, uint PartInstanceId, double3Snap Position, double3Snap Rotation,
    double Width, double Height, bool Visible);

/// <summary>NavBall-derived attitude and performance figures.</summary>
/// <param name="PitchDeg">Pitch, degrees.</param>
/// <param name="YawDeg">Yaw (heading), degrees.</param>
/// <param name="RollDeg">Roll, degrees.</param>
/// <param name="ThrustWeightRatio">Current thrust-to-weight ratio.</param>
/// <param name="DeltaVVacuumMs">Remaining vacuum delta-V, m/s.</param>
/// <param name="Frame">The navball reference frame ("EclBody", "Lvlh", …).</param>
/// <param name="SpeedMs">The navball speed readout, m/s.</param>
public sealed record NavballSnapshot(
    int PitchDeg, int YawDeg, int RollDeg, double ThrustWeightRatio, double DeltaVVacuumMs,
    string Frame, double SpeedMs);

/// <summary>The vessel's local physics environment.</summary>
/// <param name="PressurePa">Static atmospheric pressure, pascals.</param>
/// <param name="DensityKgM3">Atmospheric density, kg/m³.</param>
/// <param name="DynamicPressurePa">Dynamic pressure (q), pascals.</param>
/// <param name="OceanDensityKgM3">Ocean density, kg/m³ (0 outside an ocean).</param>
/// <param name="TerrainRadiusM">Terrain radius below the vessel, meters.</param>
/// <param name="AccelBody">Linear acceleration in the body frame, m/s².</param>
/// <param name="AngularAccelBody">Angular acceleration in the body frame, rad/s².</param>
/// <param name="GForce">Acceleration magnitude in g (|accel| / g₀).</param>
public sealed record EnvironmentSnapshot(
    double PressurePa, double DensityKgM3, double DynamicPressurePa, double OceanDensityKgM3,
    double TerrainRadiusM, double3Snap AccelBody, double3Snap AngularAccelBody, double GForce);

/// <summary>One celestial body's catalog entry (<c>/sim/bodies/&lt;id&gt;</c>).</summary>
/// <param name="Id">The body id.</param>
/// <param name="Class">"Planet", "Moon", "Star", ….</param>
/// <param name="ParentId">The parent body id, or null for the root star.</param>
/// <param name="ChildIds">Ids of orbiting bodies.</param>
/// <param name="Mass">Mass, kg.</param>
/// <param name="MeanRadius">Mean radius, meters.</param>
/// <param name="Mu">Standard gravitational parameter, m³/s².</param>
/// <param name="SoiMeters">Sphere of influence radius, meters.</param>
/// <param name="RotationRateRadS">Sidereal rotation rate, rad/s.</param>
/// <param name="PositionEcl">Position in the system ecliptic frame, meters.</param>
/// <param name="VelocityEcl">Velocity in the system ecliptic frame, m/s.</param>
/// <param name="Orbit">Orbit elements about the parent, or null for the root star.</param>
/// <param name="Atmosphere">Atmosphere reference, or null when airless.</param>
/// <param name="Ocean">Ocean reference, or null when dry.</param>
public sealed record BodySnapshot(
    string Id,
    string Class,
    string? ParentId,
    IReadOnlyList<string> ChildIds,
    double Mass,
    double MeanRadius,
    double Mu,
    double SoiMeters,
    double RotationRateRadS,
    double3Snap PositionEcl,
    double3Snap VelocityEcl,
    OrbitSnapshot? Orbit,
    AtmosphereSnapshot? Atmosphere,
    OceanSnapshot? Ocean);

/// <summary>A body's atmosphere reference.</summary>
/// <param name="HeightM">Atmosphere boundary height above the surface, meters.</param>
/// <param name="ScaleHeightM">Scale height, meters.</param>
/// <param name="SeaLevelPressurePa">Sea-level pressure, pascals.</param>
/// <param name="SeaLevelDensityKgM3">Sea-level density, kg/m³.</param>
public sealed record AtmosphereSnapshot(
    double HeightM, double ScaleHeightM, double SeaLevelPressurePa, double SeaLevelDensityKgM3);

/// <summary>A body's ocean reference.</summary>
/// <param name="DensityKgM3">Ocean density, kg/m³.</param>
public sealed record OceanSnapshot(double DensityKgM3);

/// <summary>The current star-system summary.</summary>
/// <param name="Name">The system name.</param>
/// <param name="HomeBodyId">The home body id, or null.</param>
/// <param name="SunId">The primary star id, or null.</param>
public sealed record SystemSnapshot(string Name, string? HomeBodyId, string? SunId);

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
