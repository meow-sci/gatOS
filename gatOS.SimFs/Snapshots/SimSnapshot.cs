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

    /// <summary>
    ///     The IVA free-floating-object simulation (the <c>/sim/debug/iva</c> cheat): the master
    ///     on/off flag, the live objects, per-vessel interior-geometry diagnostics and step stats.
    ///     Never null — <see cref="IvaSnapshot.Off"/> is the default, so the master flag is readable
    ///     while the whole feature is dark. Runtime-only; never persisted.
    /// </summary>
    public IvaSnapshot Iva { get; init; } = IvaSnapshot.Off;

    /// <summary>
    ///     The live values behind the FX editors (<c>/sim/debug/{engineplume,plumetrail,clouds,terrain}</c>);
    ///     null when not sampled (debug namespace off, or before the first FX sample). Only ever
    ///     populated when the debug namespace is on; a pure runtime cheat surface, never persisted.
    /// </summary>
    public FxEditorsSnapshot? FxEditors { get; init; }
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
    ///     The latched manual RCS rotation command as body-axis torque signs (<c>ctl/rotate</c>
    ///     read): each component −1/0/+1 for commanded torque about body X (+ = roll right),
    ///     Y (+ = pitch up), Z (+ = yaw right). <c>(0,0,0)</c> = no rotation commanded.
    /// </summary>
    public double3Snap RotateCmd { get; init; }

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

    /// <summary>
    ///     Solid rocket motors (SRBs), by index — surfaced under <c>srb/&lt;n&gt;/</c>. Solid
    ///     propellant is <b>not</b> a <see cref="Tanks">tank</see>: KSA stores it on a separate
    ///     grain-segment module, so a booster contributes nothing to <see cref="Tanks"/> while it
    ///     <i>is</i> counted in <see cref="MassPropellant"/>. This list is what makes a booster's
    ///     remaining propellant and burn time readable. Empty on vessels with no SRB.
    /// </summary>
    public IReadOnlyList<SrbSnapshot> Srb { get; init; } = [];

    /// <summary>Upcoming encounters / closest approaches on the current patch.</summary>
    public IReadOnlyList<EncounterSnapshot> Encounters { get; init; } = [];

    /// <summary>
    ///     Top-level parts (<c>Vehicle.Parts.Parts</c>), surfaced under <c>parts/&lt;n&gt;/</c> so guests
    ///     can discover a part to anchor a weld to; each part carries its subparts under
    ///     <c>subparts/&lt;m&gt;/</c>. Empty when the parts stream is gated off
    ///     (<c>telemetry_vessel_parts</c>); the reader caches it per vehicle and rebuilds on
    ///     part-count change (or every 10 s).
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

/// <summary>
///     One solid rocket motor (SRB) — a KSA <c>SolidMotor</c> rocket core plus the stack of grain
///     segments that feeds it (<c>srb/&lt;n&gt;/</c>). A solid is <b>not throttleable and cannot be
///     shut down</b> once lit: it is ignited through the ordinary engine surface
///     (<c>ctl/ignite</c>, <c>engines/&lt;n&gt;/active</c>, staging) and then burns its grain to
///     depletion, so this record is read-only. <see cref="EngineIndex"/> points at the
///     <c>engines/&lt;n&gt;</c> entry that ignites it.
/// </summary>
/// <param name="Index">Stable per-vessel SRB index (position among the vessel's solid motors).</param>
/// <param name="Active">Whether the motor is currently burning (<c>RocketCoreState.Throttle &gt; 0</c>).</param>
/// <param name="MassKg">Current total grain mass across the stack, kg.</param>
/// <param name="MassInitialKg">Full-stack grain mass when new, kg.</param>
public sealed record SrbSnapshot(int Index, bool Active, double MassKg, double MassInitialKg)
{
    /// <summary>
    ///     The <c>engines/&lt;n&gt;</c> index of the engine controller that ignites this motor, or
    ///     <c>-1</c> when it could not be resolved.
    /// </summary>
    public int EngineIndex { get; init; } = -1;

    /// <summary>The <c>Part.InstanceId</c> of the motor's part — the handle <c>parts/</c> uses.</summary>
    public uint PartInstanceId { get; init; }

    /// <summary>The solid propellant substance name (<c>SolidMotor.Propellant.Name</c>).</summary>
    public string Substance { get; init; } = "";

    /// <summary>The grain geometry id driving the thrust profile (e.g. a star grain).</summary>
    public string Grain { get; init; } = "";

    /// <summary>The grain geometry's shape name.</summary>
    public string GrainShape { get; init; } = "";

    /// <summary>Whether the grain-segment stack resolved (<c>SolidMotorStack.IsValid</c>).</summary>
    public bool StackValid { get; init; }

    /// <summary>The stack's error message when <see cref="StackValid"/> is false; empty otherwise.</summary>
    public string StackError { get; init; } = "";

    /// <summary>Whether burnable grain remains and chamber pressure can be sustained.</summary>
    public bool PropellantAvailable { get; init; }

    /// <summary>
    ///     Grain mass that can never burn (the sliver/slag left when chamber pressure falls below
    ///     the quench threshold), kg. Subtract it to get usable propellant.
    /// </summary>
    public double MassUnburnableKg { get; init; }

    /// <summary>Usable grain remaining: <c>max(<see cref="MassKg"/> − <see cref="MassUnburnableKg"/>, 0)</c>, kg.</summary>
    public double MassBurnableKg { get; init; }

    /// <summary>Usable grain remaining as a fraction 0..1 of the usable grain when new.</summary>
    public double Fraction { get; init; }

    /// <summary>Instantaneous propellant mass flow, kg/s (0 when not burning).</summary>
    public double MassFlowKgS { get; init; }

    /// <summary>
    ///     Seconds of thrust left at the current mass flow (<c>RocketCoreState.ThrustTimeRemaining</c>).
    ///     <b>0 while the motor is not burning</b> — use <see cref="Fraction"/> for a pre-ignition
    ///     estimate.
    /// </summary>
    public double BurnTimeRemainingS { get; init; }

    /// <summary>Chamber pressure, pascals (0 when not burning).</summary>
    public double ChamberPressurePa { get; init; }

    /// <summary>Chamber temperature, kelvin (0 when not burning).</summary>
    public double ChamberTemperatureK { get; init; }

    /// <summary>Nozzle exit pressure, pascals (0 when not burning).</summary>
    public double ExitPressurePa { get; init; }

    /// <summary>Nozzle exit temperature, kelvin (0 when not burning).</summary>
    public double ExitTemperatureK { get; init; }

    /// <summary>
    ///     Current total burning surface area across the stack, m² — the geometric driver of a
    ///     solid's thrust curve (it rises and falls as the grain regresses).
    /// </summary>
    public double BurningAreaM2 { get; init; }

    /// <summary>Nozzle expansion (exit/throat) area ratio, as sized for this stack.</summary>
    public double AreaRatio { get; init; }

    /// <summary>The grain segments feeding this motor, under <c>segments/&lt;m&gt;/</c>.</summary>
    public IReadOnlyList<SrbSegmentSnapshot> Segments { get; init; } = [];
}

/// <summary>
///     One solid grain segment in an SRB's stack (<c>srb/&lt;n&gt;/segments/&lt;m&gt;/</c>) — a KSA
///     <c>SolidGrainSegment</c> module, the physical casing section holding a cast propellant grain.
///     Stacking segments is how a booster's total impulse is sized in the editor.
/// </summary>
/// <param name="Index">Segment index within the owning motor's stack (0-based, nozzle-ward order).</param>
/// <param name="MassKg">Current grain mass in this segment, kg.</param>
/// <param name="MassInitialKg">This segment's grain mass when new, kg.</param>
public sealed record SrbSegmentSnapshot(int Index, double MassKg, double MassInitialKg)
{
    /// <summary>The <c>Part.InstanceId</c> of the segment's part — the handle <c>parts/</c> uses.</summary>
    public uint PartInstanceId { get; init; }

    /// <summary>The solid propellant substance name; empty when the segment is unconfigured.</summary>
    public string Substance { get; init; } = "";

    /// <summary>The grain geometry id cast into this segment.</summary>
    public string Grain { get; init; } = "";

    /// <summary>This segment's share of the unburnable sliver mass, kg.</summary>
    public double MassUnburnableKg { get; init; }

    /// <summary>Usable grain remaining in this segment as a fraction 0..1 of its usable grain when new.</summary>
    public double Fraction { get; init; }

    /// <summary>Casing inner radius, meters.</summary>
    public double RadiusM { get; init; }

    /// <summary>Segment length, meters.</summary>
    public double LengthM { get; init; }

    /// <summary>Grain volume when new, m³.</summary>
    public double VolumeM3 { get; init; }

    /// <summary>
    ///     How far the burning surface has regressed from the initial port wall, meters (0 for an
    ///     unlit grain, rising toward the casing wall as it burns).
    /// </summary>
    public double BurnDepthM { get; init; }
}

/// <summary>A predicted encounter / closest approach with another body on the current patch.</summary>
/// <param name="Body">The other body's id.</param>
/// <param name="Ut">Sim time of closest approach.</param>
/// <param name="DistanceMeters">Closest-approach distance, meters.</param>
public sealed record EncounterSnapshot(string Body, double Ut, double DistanceMeters);

/// <summary>
///     One top-level part (<c>Vehicle.Parts.Parts</c>) — the anchor picker for the welds feature
///     (<c>/sim/vessels/by-id/&lt;id&gt;/parts/&lt;n&gt;/</c>). Its subparts are surfaced under
///     <c>parts/&lt;n&gt;/subparts/&lt;m&gt;/</c> (see <see cref="Subparts"/>).
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
/// <param name="SubpartCount">Number of subparts (== <see cref="Subparts"/>.Count when sampled).</param>
/// <param name="PositionVehicleAsmb">Part position in the vehicle assembly frame, meters.</param>
public sealed record PartSnapshot(
    int Index, uint InstanceId, string Id, string DisplayName, string Template,
    bool IsRoot, int SubpartCount, double3Snap PositionVehicleAsmb)
{
    /// <summary>
    ///     The part's subparts (<c>Part.SubParts</c>), surfaced under <c>subparts/&lt;m&gt;/</c> so a
    ///     weld can anchor to a subpart (e.g. a robotics/animated segment) by its
    ///     <see cref="SubpartSnapshot.InstanceId"/>.
    /// </summary>
    public IReadOnlyList<SubpartSnapshot> Subparts { get; init; } = [];
}

/// <summary>
///     One subpart (<c>Part.SubParts</c> — itself a <c>Part</c> with its own runtime-unique
///     <c>InstanceId</c>), under <c>parts/&lt;n&gt;/subparts/&lt;m&gt;/</c>.
/// </summary>
/// <param name="Index">Subpart index within the owning part's <c>SubParts</c> span (0-based).</param>
/// <param name="InstanceId">
///     Runtime-unique id (<c>Part.InstanceId</c>) — valid as a weld <c>&lt;part_iid&gt;</c> anchor
///     exactly like a top-level part's.
/// </param>
/// <param name="Id">The subpart id string (<c>Part.Id</c>).</param>
/// <param name="DisplayName">Human-readable name (<c>Part.DisplayName</c>).</param>
/// <param name="Template">The subpart template id (<c>Part.Template.Id</c>).</param>
/// <param name="PositionVehicleAsmb">Subpart position in the vehicle assembly frame, meters.</param>
public sealed record SubpartSnapshot(
    int Index, uint InstanceId, string Id, string DisplayName, string Template,
    double3Snap PositionVehicleAsmb);

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

/// <summary>
///     The IVA free-floating-object simulation as a whole (<c>/sim/debug/iva</c>): a gatOS-owned
///     rigid-body sim, one per vessel that has objects, running in that vessel's assembly frame and
///     colliding against interior geometry derived from the shipped IVA meshes. A pure runtime cheat
///     — nothing here is ever persisted, and a displaced object's transform cannot reach a save file
///     (KSA serializes no SubPart transform).
/// </summary>
/// <param name="Enabled">
///     The master switch (<c>/sim/debug/iva/enabled</c>). <b>Off by default.</b> While it is off no
///     simulation object exists at all: no physics engine instance, no interior geometry, no
///     per-frame work. Turning it off releases every object (restoring exact rest poses) and disposes
///     every simulation.
/// </param>
/// <param name="RunOutsideIva">
///     Whether the sim keeps stepping when no viewport is in the IVA camera mode
///     (<c>/sim/debug/iva/run_outside_iva</c>). Off by default: leaving IVA parks the objects in place.
/// </param>
/// <param name="Objects">The live floating objects, by registry id. Empty when none are adopted.</param>
/// <param name="Interiors">One diagnostics row per vessel with a built interior mesh.</param>
/// <param name="Stats">Step/health counters for the whole feature.</param>
public sealed record IvaSnapshot(
    bool Enabled,
    bool RunOutsideIva,
    IReadOnlyList<IvaObjectSnapshot> Objects,
    IReadOnlyList<IvaInteriorSnapshot> Interiors,
    IvaStatsSnapshot Stats)
{
    /// <summary>The feature-is-dark snapshot: master off, nothing adopted, no simulation.</summary>
    public static IvaSnapshot Off { get; } = new(false, false, [], [], IvaStatsSnapshot.Zero);
}

/// <summary>
///     One free-floating cabin object (<c>/sim/debug/iva/&lt;id&gt;</c>): a gatOS-owned rigid body
///     paired with a real IVA prop SubPart whose transform is rewritten each frame from the body's
///     pose. Rendering, lighting and IVA visibility gating therefore come from the game for free.
/// </summary>
/// <param name="Id">The registry handle (smallest free slot at adopt; reused after release) — the directory name.</param>
/// <param name="VesselId">The vessel whose cabin this object floats in.</param>
/// <param name="PartInstanceId">The driven SubPart's <c>Part.InstanceId</c> — the stable handle <c>parts/</c> uses.</param>
/// <param name="Part">The driven SubPart's display name.</param>
/// <param name="Template">The driven SubPart's template id (e.g. <c>CoreIVAPropA_Subpart_DeanSardineA</c>).</param>
/// <param name="Position">Position in the vessel assembly frame, metres.</param>
/// <param name="Velocity">Velocity relative to the cabin, m/s.</param>
/// <param name="AngularVelocity">Angular velocity relative to the cabin, rad/s.</param>
/// <param name="MassKg">The body's mass, kg (density × collision-proxy volume unless overridden).</param>
/// <param name="Shape">The collision proxy kind; <c>box</c> in this build (see <paramref name="Size"/>).</param>
/// <param name="Size">The collision proxy's full extents, metres.</param>
/// <param name="Asleep">Whether the body is sleeping (settled — costs nothing to simulate).</param>
public sealed record IvaObjectSnapshot(
    int Id, string VesselId, uint PartInstanceId, string Part, string Template,
    double3Snap Position, double3Snap Velocity, double3Snap AngularVelocity,
    double MassKg, string Shape, double3Snap Size, bool Asleep);

/// <summary>
///     Interior collision-geometry diagnostics for one vessel (<c>/sim/debug/iva/interior</c>) — the
///     evidence a live pass needs to confirm the mesh built correctly without a debug renderer.
/// </summary>
/// <param name="VesselId">The vessel the geometry belongs to.</param>
/// <param name="Triangles">Triangle count in the built static mesh (doubled when double-sided).</param>
/// <param name="SourceParts">How many parts/subparts contributed interior meshes.</param>
/// <param name="AabbMin">Minimum corner of the geometry's bounding box, assembly frame, metres.</param>
/// <param name="AabbMax">Maximum corner of the geometry's bounding box, assembly frame, metres.</param>
/// <param name="Fallback">
///     True when no interior meshes were found and a bounding-box "room" was synthesised instead, so
///     objects rattle in a box rather than falling out of the universe.
/// </param>
public sealed record IvaInteriorSnapshot(
    string VesselId, int Triangles, int SourceParts, double3Snap AabbMin, double3Snap AabbMax,
    bool Fallback);

/// <summary>Step/health counters for the IVA physics feature (<c>/sim/debug/iva/stats</c>).</summary>
/// <param name="Vessels">Vessels with a live cabin simulation.</param>
/// <param name="Objects">Total floating objects across all vessels.</param>
/// <param name="Sleeping">How many of those are asleep (settled).</param>
/// <param name="Substeps">Fixed substeps executed on the last driven frame.</param>
/// <param name="StepAvgMs">Mean game-thread cost of one driver pass, milliseconds.</param>
/// <param name="StepMaxMs">Worst game-thread cost of one driver pass, milliseconds.</param>
/// <param name="Parked">
///     Whether the sim is currently parked (velocities zeroed, poses frozen): under time warp, in the
///     vehicle editor, or outside the IVA camera when <c>run_outside_iva</c> is off.
/// </param>
/// <param name="ParkReason">Why it is parked ("warp", "editor", "not-iva"), or empty when running.</param>
public sealed record IvaStatsSnapshot(
    int Vessels, int Objects, int Sleeping, int Substeps, double StepAvgMs, double StepMaxMs,
    bool Parked, string ParkReason)
{
    /// <summary>All-zero counters — the stats of a feature that has never run.</summary>
    public static IvaStatsSnapshot Zero { get; } = new(0, 0, 0, 0, 0, 0, false, "");
}

/// <summary>
///     One addressable FX-editor entity — a volumetric-exhaust template, the (single) trail
///     renderer, or one body's clouds/terrain (plans/FX_EDITORS_PLAN.md §1). The field set an
///     entity publishes is what defines its <c>/sim</c> subtree: every key is a <b>concrete</b>
///     field path (<c>emission/color0</c>, <c>layers/1/types/0/density</c>) that resolves against
///     the family's <c>FxCatalog</c> table, and its value array is exactly the spec's arity long
///     (flags are <c>0</c>/<c>1</c>).
/// </summary>
/// <param name="Id">The entity id — the directory name source; <c>""</c> for a singleton entity.</param>
/// <param name="Fields">Live values, keyed by concrete field path.</param>
public sealed record FxEntitySnapshot(string Id, IReadOnlyDictionary<string, double[]> Fields);

/// <summary>
///     The sampled FX-editor surface: one entity roster per family. Rebuilt only when an FX write
///     happened or the resample interval elapsed (in-game imgui edits), else republished by
///     reference — so the whole subtree is allocation-free while idle.
/// </summary>
public sealed record FxEditorsSnapshot
{
    /// <summary>Volumetric-exhaust templates, keyed by template id. Shared: an edit hits every nozzle using it.</summary>
    public IReadOnlyList<FxEntitySnapshot> PlumeTemplates { get; init; } = [];

    /// <summary>The global volumetric-trail renderer (<c>Id</c> <c>""</c>); null when unavailable.</summary>
    public FxEntitySnapshot? Trail { get; init; }

    /// <summary>Bodies that carry a cloud definition, keyed by body id.</summary>
    public IReadOnlyList<FxEntitySnapshot> CloudBodies { get; init; } = [];

    /// <summary>Bodies that currently hold a terrain render slot, keyed by body id.</summary>
    public IReadOnlyList<FxEntitySnapshot> TerrainBodies { get; init; } = [];

    /// <summary>
    ///     The terrain family's <b>global</b> fields (<c>wireframe</c>) as a singleton entity
    ///     (<c>Id</c> <c>""</c>); null when unavailable. Addressed with an empty entity token.
    /// </summary>
    public FxEntitySnapshot? TerrainGlobal { get; init; }
}

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
