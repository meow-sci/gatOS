using System.Collections.Concurrent;
using System.Globalization;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Display;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs;

/// <summary>
///     Builds the <c>/sim</c> VFS over a <see cref="SnapshotStore"/> (OS_PLAN.md T8.2):
///     <code>
///     /time/{ut,warp}
///     /vessels/active/…                  (dynamic alias of the active vessel's dir)
///     /vessels/by-id/&lt;id&gt;/
///         id name situation parent
///         position/{cci,lat,lon}  velocity/{orbital,surface,inertial}
///         attitude/{quat,rates}   altitude/{barometric,radar}
///         mass/{total,dry,propellant}
///         orbit/{apoapsis,periapsis,ecc,inc,sma,period}     (only while in orbit)
///         battery/charge                                    (only with a battery)
///         engines/&lt;n&gt;/{active,vac_thrust,isp}
///         tanks/&lt;resource&gt;/{amount,capacity}
///         stream
///     /events
///     </code>
///     Every scalar file snapshots its value at open (<c>cache=none</c> makes consecutive
///     opens live); a vessel that vanished between walk and open answers ENOENT. Dynamic
///     nodes are transient objects, but their qids are interned by relative path so the same
///     logical file keeps a stable identity across snapshots.
/// </summary>
public static class SimFsTree
{
    /// <summary>
    ///     Accepted <c>ctl/attitude_mode</c> tokens: <c>manual</c> drops the flight computer to
    ///     manual attitude; any other token names a <c>FlightComputerAttitudeTrackTarget</c> and
    ///     puts it in auto-track. Canonical (enum) casing; matched case-insensitively.
    /// </summary>
    private static readonly string[] AttitudeModeTokens =
    [
        "manual", "Prograde", "Retrograde", "Normal", "AntiNormal", "RadialOut", "RadialIn",
        "Toward", "Away", "Antivel", "Align", "Forward", "Backward", "Up", "Down", "Ahead",
        "Behind", "Outward", "Inward", "PositiveDv", "NegativeDv", "Custom", "None",
    ];

    /// <summary>Accepted <c>ctl/attitude_frame</c> tokens (the <c>VehicleReferenceFrame</c> names).</summary>
    private static readonly string[] AttitudeFrameTokens =
        ["EclBody", "EnuBody", "Lvlh", "VlfBody", "BurnBody", "Dock"];

    /// <summary>Builds the read-only <c>/sim</c> tree (no control surface, no status).</summary>
    public static VfsDirectory Build(SnapshotStore store) => Build(store, null, null);

    /// <summary>
    ///     Builds the <c>/sim</c> tree. When <paramref name="commands"/> is supplied the tree
    ///     gains its writable control surface (<c>ctl/</c>, writable <c>engines/&lt;n&gt;/active</c>,
    ///     <c>animations/</c>, <c>solar/</c>) and the <c>/sim/status/</c> integration-health tree
    ///     (KSA_GAME_INTEGRATION_PLAN Parts 4–5). With no sink the tree is purely read-only.
    /// </summary>
    /// <param name="store">The published-snapshot exchange the tree reads from.</param>
    /// <param name="commands">The command sink control files submit to; null = read-only tree.</param>
    /// <param name="transports">
    ///     Optional provider for <c>/sim/status/transports</c> (bound ports etc.); supplied by the
    ///     game mod, which alone knows the transport bindings.
    /// </param>
    /// <param name="display">
    ///     Optional screen-stream hub (STREAM_PLAN.md): when supplied the tree gains the
    ///     <c>/sim/display/</c> surface (the <c>enabled</c>/<c>fps</c>/<c>width</c>/<c>height</c>/
    ///     <c>encoding</c> control files and the binary <c>stream</c> feed). Supplied by the game mod,
    ///     which owns the render-thread capture.
    /// </param>
    public static VfsDirectory Build(SnapshotStore store, ICommandSink? commands, Func<string>? transports,
        DisplaySurface? display = null)
        => new Builder(store, commands, transports, display).BuildRoot();

    private sealed class Builder
    {
        private readonly SnapshotStore _store;
        private readonly ICommandSink? _commands;
        private readonly Func<string>? _transports;
        private readonly DisplaySurface? _display;
        private readonly ConcurrentDictionary<string, ulong> _qids = new();
        private long _nextQid;

        internal Builder(SnapshotStore store, ICommandSink? commands, Func<string>? transports,
            DisplaySurface? display)
        {
            _store = store;
            _commands = commands;
            _transports = transports;
            _display = display;
        }

        internal VfsDirectory BuildRoot()
        {
            var children = new List<VfsNode>
            {
                TimeDir(),
                new DelegateDirectory("vessels", Qid("vessels"),
                    () => [ActiveDir(), ByIdDir()],
                    name => name switch
                    {
                        "active" => ActiveDir(),
                        "by-id" => ByIdDir(),
                        _ => null,
                    }),
                SystemDir(),
                BodiesDir(),
                new EventsFile("events", Qid("events"), _store),
            };

            // The integration-health tree rides with the control surface (G2): present whenever a
            // command sink is wired, regardless of whether writes are currently enabled.
            if (_commands is not null)
                children.Add(StatusDir());

            // The /sim/debug cheat namespace (G-D2): only when a sink is wired and debug is enabled.
            if (_commands is { DebugEnabled: true })
                children.Add(DebugDir());

            // The screen stream (STREAM_PLAN.md): present whenever a display surface is wired.
            if (_display is not null)
                children.Add(DisplayDir());

            var fixedChildren = children.ToArray();
            return new DelegateDirectory("/", Qid("/"), () => fixedChildren);
        }

        // ---- time (KSA_GAME_INTEGRATION_PLAN §4.2) ----------------------------------------

        private VfsDirectory TimeDir()
            => DelegateDirectory.Fixed("time", Qid("time"),
                Line("time/ut", "ut", () => Formats.Scalar(_store.Current.UtSeconds)),
                Line("time/warp", "warp", () => Formats.Scalar(_store.Current.WarpFactor)),
                Line("time/sim_dt", "sim_dt", () => Formats.Scalar(_store.Current.SimDtSeconds)),
                Line("time/warp_speeds", "warp_speeds",
                    () => string.Join(' ', _store.Current.WarpSpeeds.Select(Formats.Scalar))),
                Line("time/auto_warp", "auto_warp", () =>
                {
                    var s = _store.Current;
                    return s.AutoWarpActive ? $"1 {Formats.Scalar(s.AutoWarpTargetUt)}" : "0";
                }),
                new AlarmFile("alarm", Qid("time/alarm"), _store));

        // ---- system & bodies (KSA_GAME_INTEGRATION_PLAN §4.3) -----------------------------

        private VfsDirectory SystemDir()
            => DelegateDirectory.Fixed("system", Qid("system"),
                Line("system/name", "name", () => _store.Current.System?.Name ?? ""),
                Line("system/home", "home", () => _store.Current.System?.HomeBodyId ?? ""),
                Line("system/sun", "sun", () => _store.Current.System?.SunId ?? ""));

        private VfsDirectory BodiesDir()
            => new DelegateDirectory("bodies", Qid("bodies"),
                () => SanitizedBodies(_store.Current)
                    .Select(b => (VfsNode)BodyDir(b.Name, b.Item.Id))
                    .ToArray(),
                name => SanitizedBodies(_store.Current)
                    .Where(b => b.Name == name)
                    .Select(b => (VfsNode?)BodyDir(b.Name, b.Item.Id))
                    .FirstOrDefault());

        private VfsDirectory BodyDir(string sanitized, string bodyId)
        {
            var p = $"bodies/{sanitized}";
            return new DelegateDirectory(sanitized, Qid(p), () =>
            {
                var body = Body(bodyId);
                var children = new List<VfsNode>
                {
                    Line($"{p}/id", "id", () => Body(bodyId).Id),
                    Line($"{p}/class", "class", () => Body(bodyId).Class),
                    Line($"{p}/parent", "parent", () => Body(bodyId).ParentId ?? ""),
                    Line($"{p}/children", "children", () => string.Join('\n', Body(bodyId).ChildIds)),
                    Line($"{p}/mass", "mass", () => Formats.Scalar(Body(bodyId).Mass)),
                    Line($"{p}/radius", "radius", () => Formats.Scalar(Body(bodyId).MeanRadius)),
                    Line($"{p}/mu", "mu", () => Formats.Scalar(Body(bodyId).Mu)),
                    Line($"{p}/soi", "soi", () => Formats.Scalar(Body(bodyId).SoiMeters)),
                    Line($"{p}/rotation_rate", "rotation_rate",
                        () => Formats.Scalar(Body(bodyId).RotationRateRadS)),
                    DelegateDirectory.Fixed("position", Qid($"{p}/position"),
                        Line($"{p}/position/ecl", "ecl", () => Formats.Vector(Body(bodyId).PositionEcl))),
                    DelegateDirectory.Fixed("velocity", Qid($"{p}/velocity"),
                        Line($"{p}/velocity/ecl", "ecl", () => Formats.Vector(Body(bodyId).VelocityEcl))),
                };
                if (body.Orbit is not null)
                    children.Add(BodyOrbitDir(p, bodyId));
                if (body.Atmosphere is not null)
                    children.Add(AtmosphereDir(p, bodyId));
                if (body.Ocean is not null)
                    children.Add(DelegateDirectory.Fixed("ocean", Qid($"{p}/ocean"),
                        Line($"{p}/ocean/present", "present", () => "1"),
                        Line($"{p}/ocean/density", "density",
                            () => Formats.Scalar(Body(bodyId).Ocean!.DensityKgM3))));
                // Move the main camera to this celestial (write 1), same action vessels' ctl/focus uses.
                if (_commands is { } sink)
                    children.Add(new TriggerFile("focus", Qid($"{p}/focus"), sink,
                        new SimCommand(bodyId, "camera.focus", SimCommand.NoOrdinal, 1)));

                return children;
            });
        }

        private VfsDirectory BodyOrbitDir(string p, string bodyId)
            => DelegateDirectory.Fixed("orbit", Qid($"{p}/orbit"),
                Line($"{p}/orbit/apoapsis", "apoapsis", () => Formats.Scalar(BodyOrbit(bodyId).ApoapsisAltitude)),
                Line($"{p}/orbit/periapsis", "periapsis", () => Formats.Scalar(BodyOrbit(bodyId).PeriapsisAltitude)),
                Line($"{p}/orbit/ecc", "ecc", () => Formats.Scalar(BodyOrbit(bodyId).Eccentricity)),
                Line($"{p}/orbit/inc", "inc", () => Formats.Scalar(BodyOrbit(bodyId).InclinationDeg)),
                Line($"{p}/orbit/lan", "lan", () => Formats.Scalar(BodyOrbit(bodyId).LanDeg)),
                Line($"{p}/orbit/argpe", "argpe", () => Formats.Scalar(BodyOrbit(bodyId).ArgPeDeg)),
                Line($"{p}/orbit/sma", "sma", () => Formats.Scalar(BodyOrbit(bodyId).SmaMeters)),
                Line($"{p}/orbit/period", "period", () => Formats.Scalar(BodyOrbit(bodyId).PeriodSeconds)));

        private VfsDirectory AtmosphereDir(string p, string bodyId)
            => DelegateDirectory.Fixed("atmosphere", Qid($"{p}/atmosphere"),
                Line($"{p}/atmosphere/present", "present", () => "1"),
                Line($"{p}/atmosphere/height", "height",
                    () => Formats.Scalar(Body(bodyId).Atmosphere!.HeightM)),
                Line($"{p}/atmosphere/scale_height", "scale_height",
                    () => Formats.Scalar(Body(bodyId).Atmosphere!.ScaleHeightM)),
                Line($"{p}/atmosphere/sea_level_pressure", "sea_level_pressure",
                    () => Formats.Scalar(Body(bodyId).Atmosphere!.SeaLevelPressurePa)),
                Line($"{p}/atmosphere/sea_level_density", "sea_level_density",
                    () => Formats.Scalar(Body(bodyId).Atmosphere!.SeaLevelDensityKgM3)));

        private BodySnapshot Body(string bodyId)
            => _store.Current.Bodies.FirstOrDefault(b => b.Id == bodyId)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"body '{bodyId}' is gone");

        private OrbitSnapshot BodyOrbit(string bodyId)
            => Body(bodyId).Orbit
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"body '{bodyId}' has no orbit");

        private static List<(string Name, BodySnapshot Item)> SanitizedBodies(SimSnapshot snapshot)
            => SanitizeNames(snapshot.Bodies, b => b.Id);

        // ---- status (integration health) -------------------------------------------------

        private VfsDirectory StatusDir()
            => DelegateDirectory.Fixed("status", Qid("status"),
                Line("status/game_version", "game_version",
                    () => _store.Current.GameVersion is { Length: > 0 } v ? v : "unknown"),
                Line("status/sampler", "sampler", () =>
                {
                    var rate = _store.Current.SampleRateHz;
                    return rate > 0 ? $"ok {Formats.Scalar(rate)}" : "idle";
                }),
                new StaticTextFile("accessors", Qid("status/accessors"),
                    () => string.Concat(_store.Current.Accessors.Select(a => Formats.AccessorLine(a) + "\n"))),
                Line("status/transports", "transports", () => _transports?.Invoke() ?? "unknown"));

        // ---- display (the screen stream — STREAM_PLAN.md) --------------------------------

        private VfsDirectory DisplayDir()
        {
            var settings = _display!.Settings;
            return DelegateDirectory.Fixed("display", Qid("display"),
                DisplaySettingFile.Create("enabled", Qid("display/enabled"),
                    () => settings.Enabled ? "1" : "0",
                    tok => ApplyFlag(tok, v => settings.Enabled = v)),
                DisplaySettingFile.Create("fps", Qid("display/fps"),
                    () => settings.Fps.ToString(),
                    tok => ApplyInt(tok, v => settings.Fps = v)),
                DisplaySettingFile.Create("width", Qid("display/width"),
                    () => settings.Width.ToString(),
                    tok => ApplyInt(tok, v => settings.Width = v)),
                DisplaySettingFile.Create("height", Qid("display/height"),
                    () => settings.Height.ToString(),
                    tok => ApplyInt(tok, v => settings.Height = v)),
                DisplaySettingFile.Create("encoding", Qid("display/encoding"),
                    () => settings.Encoding.Token(),
                    tok =>
                    {
                        if (DisplayEncodings.Parse(tok) is not { } encoding)
                            return false;
                        settings.Encoding = encoding;
                        return true;
                    }),
                Line("display/format", "format",
                    () => $"{settings.Width}x{settings.Height}@{settings.Fps} {settings.Encoding.Token()}"),
                new DisplayStreamFile("stream", Qid("display/stream"), _display!));
        }

        /// <summary>Applies a <c>0</c>/<c>1</c> (or true/false/on/off) token; false = EINVAL.</summary>
        private static bool ApplyFlag(string token, Action<bool> set)
        {
            switch (token.ToLowerInvariant())
            {
                case "1" or "true" or "on":
                    set(true);
                    return true;
                case "0" or "false" or "off":
                    set(false);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Applies an integer token (the setter clamps the range); false = EINVAL.</summary>
        private static bool ApplyInt(string token, Action<int> set)
        {
            if (!int.TryParse(token, out var value))
                return false;
            set(value);
            return true;
        }

        // ---- vessels ---------------------------------------------------------------------

        private VfsDirectory ByIdDir()
            => new DelegateDirectory("by-id", Qid("vessels/by-id"),
                () => SanitizedVessels(_store.Current)
                    .Select(v => (VfsNode)VesselDir(v.Name, v.Vessel.Id))
                    .ToArray(),
                name => SanitizedVessels(_store.Current)
                    .Where(v => v.Name == name)
                    .Select(v => (VfsNode?)VesselDir(v.Name, v.Vessel.Id))
                    .FirstOrDefault());

        /// <summary>
        ///     The <c>active</c> alias: its own directory qid, but it lists/resolves the
        ///     active vessel's children directly, so <c>active/…</c> and <c>by-id/…</c> walk
        ///     to identical qids (the plan's "alias, NOT a symlink").
        /// </summary>
        private VfsDirectory ActiveDir()
            => new DelegateDirectory("active", Qid("vessels/active"),
                () => ResolveActive()?.List() ?? [],
                name => ResolveActive()?.Lookup(name));

        private VfsDirectory? ResolveActive()
        {
            var snapshot = _store.Current;
            if (snapshot.ActiveVesselId is not { } activeId)
                return null;
            return SanitizedVessels(snapshot)
                .Where(v => v.Vessel.Id == activeId)
                .Select(v => (VfsDirectory?)VesselDir(v.Name, v.Vessel.Id))
                .FirstOrDefault();
        }

        private VfsDirectory VesselDir(string sanitized, string vesselId)
        {
            var p = $"vessels/by-id/{sanitized}";
            return new DelegateDirectory(sanitized, Qid(p), () =>
            {
                var vessel = Vessel(vesselId);
                var children = new List<VfsNode>
                {
                    Line($"{p}/id", "id", () => Vessel(vesselId).Id),
                    Line($"{p}/name", "name", () => Vessel(vesselId).Name),
                    Line($"{p}/situation", "situation", () => Vessel(vesselId).Situation),
                    Line($"{p}/parent", "parent", () => Vessel(vesselId).ParentBodyName ?? ""),
                    Line($"{p}/controlled", "controlled", () => Formats.Flag(Vessel(vesselId).Controlled)),
                    Line($"{p}/controllable", "controllable", () => Formats.Flag(Vessel(vesselId).Controllable)),
                    Line($"{p}/com", "com", () => Formats.Vector(Vessel(vesselId).CenterOfMass)),
                    new StaticTextFile("telemetry", Qid($"{p}/telemetry"),
                        () => Formats.VesselTelemetry(_store.Current, Vessel(vesselId)) + "\n"),
                    DelegateDirectory.Fixed("position", Qid($"{p}/position"),
                        Line($"{p}/position/cci", "cci", () => Formats.Vector(Vessel(vesselId).PositionCci)),
                        Line($"{p}/position/ecl", "ecl", () => Formats.Vector(Vessel(vesselId).PositionEcl)),
                        Line($"{p}/position/lat", "lat", () => Formats.Scalar(Vessel(vesselId).LatitudeDeg)),
                        Line($"{p}/position/lon", "lon", () => Formats.Scalar(Vessel(vesselId).LongitudeDeg))),
                    DelegateDirectory.Fixed("velocity", Qid($"{p}/velocity"),
                        Line($"{p}/velocity/orbital", "orbital", () => Formats.Scalar(Vessel(vesselId).OrbitalSpeed)),
                        Line($"{p}/velocity/surface", "surface", () => Formats.Scalar(Vessel(vesselId).SurfaceSpeed)),
                        Line($"{p}/velocity/inertial", "inertial", () => Formats.Scalar(Vessel(vesselId).InertialSpeed)),
                        Line($"{p}/velocity/cci", "cci", () => Formats.Vector(Vessel(vesselId).VelocityCci))),
                    DelegateDirectory.Fixed("attitude", Qid($"{p}/attitude"),
                        Line($"{p}/attitude/quat", "quat", () => Formats.Quat(Vessel(vesselId).AttitudeBody2Cci)),
                        Line($"{p}/attitude/rates", "rates", () => Formats.Vector(Vessel(vesselId).BodyRatesRadS))),
                    DelegateDirectory.Fixed("altitude", Qid($"{p}/altitude"),
                        Line($"{p}/altitude/barometric", "barometric",
                            () => Formats.Scalar(Vessel(vesselId).BarometricAltitude)),
                        Line($"{p}/altitude/radar", "radar", () => Formats.Scalar(Vessel(vesselId).RadarAltitude))),
                    DelegateDirectory.Fixed("mass", Qid($"{p}/mass"),
                        Line($"{p}/mass/total", "total", () => Formats.Scalar(Vessel(vesselId).MassTotal)),
                        Line($"{p}/mass/dry", "dry", () => Formats.Scalar(Vessel(vesselId).MassDry)),
                        Line($"{p}/mass/propellant", "propellant",
                            () => Formats.Scalar(Vessel(vesselId).MassPropellant))),
                };
                if (vessel.Orbit is not null)
                    children.Add(OrbitDir(p, vesselId));
                if (vessel.Navball is not null)
                    children.Add(NavballDir(p, vesselId));
                if (vessel.Environment is not null)
                    children.Add(EnvironmentDir(p, vesselId));
                if (vessel.BatteryChargeFraction is not null)
                    children.Add(BatteryDir(p, vesselId));
                children.Add(PowerDir(p, vesselId));
                children.Add(EnginesDir(p, vesselId));
                children.Add(TanksDir(p, vesselId));
                if (vessel.Rcs.Count > 0)
                    children.Add(RcsDir(p, vesselId));
                if (vessel.Solar.Count > 0)
                    children.Add(SolarDir(p, vesselId));
                if (vessel.Generators.Count > 0)
                    children.Add(GeneratorsDir(p, vesselId));
                if (vessel.Lights.Count > 0)
                    children.Add(LightsDir(p, vesselId));
                if (vessel.Docking.Count > 0)
                    children.Add(DockingDir(p, vesselId));
                if (vessel.Decouplers.Count > 0)
                    children.Add(DecouplersDir(p, vesselId));
                if (vessel.Encounters.Count > 0)
                    children.Add(new StaticTextFile("encounters", Qid($"{p}/encounters"),
                        () => string.Concat(Vessel(vesselId).Encounters
                            .Select(e => Formats.EncounterLine(e) + "\n"))));
                if (vessel.Animations.Count > 0)
                    children.Add(AnimationsDir(p, vesselId));
                // Top-level parts (the welds anchor picker). Present only when the parts stream is on
                // (telemetry_vessel_parts gates the reader, so the list is empty when off → no dir).
                if (vessel.Parts.Count > 0)
                    children.Add(PartsDir(p, vesselId));
                children.Add(new StreamFile("stream", Qid($"{p}/stream"), _store, vesselId));

                // The vessel control surface (G1/G4): only when a command sink is wired. Per-module
                // controls (engine active, rcs active, light state, decoupler fire, solar/animation
                // goal) live inside their own read dirs above and light up the same way.
                if (_commands is not null)
                    children.Add(CtlDir(p, vesselId));

                return children;
            });
        }

        private VfsDirectory OrbitDir(string p, string vesselId)
            => DelegateDirectory.Fixed("orbit", Qid($"{p}/orbit"),
                Line($"{p}/orbit/apoapsis", "apoapsis", () => Formats.Scalar(Orbit(vesselId).ApoapsisAltitude)),
                Line($"{p}/orbit/periapsis", "periapsis", () => Formats.Scalar(Orbit(vesselId).PeriapsisAltitude)),
                Line($"{p}/orbit/ecc", "ecc", () => Formats.Scalar(Orbit(vesselId).Eccentricity)),
                Line($"{p}/orbit/inc", "inc", () => Formats.Scalar(Orbit(vesselId).InclinationDeg)),
                Line($"{p}/orbit/lan", "lan", () => Formats.Scalar(Orbit(vesselId).LanDeg)),
                Line($"{p}/orbit/argpe", "argpe", () => Formats.Scalar(Orbit(vesselId).ArgPeDeg)),
                Line($"{p}/orbit/sma", "sma", () => Formats.Scalar(Orbit(vesselId).SmaMeters)),
                Line($"{p}/orbit/period", "period", () => Formats.Scalar(Orbit(vesselId).PeriodSeconds)),
                Line($"{p}/orbit/true_anomaly", "true_anomaly",
                    () => Formats.Scalar(Orbit(vesselId).TrueAnomalyDeg)),
                Line($"{p}/orbit/time_to_ap", "time_to_ap", () => Formats.Scalar(Orbit(vesselId).TimeToApoapsis)),
                Line($"{p}/orbit/time_to_pe", "time_to_pe", () => Formats.Scalar(Orbit(vesselId).TimeToPeriapsis)),
                Line($"{p}/orbit/next_patch", "next_patch", () => Formats.Scalar(Orbit(vesselId).NextPatchEventUt)));

        private VfsDirectory NavballDir(string p, string vesselId)
            => DelegateDirectory.Fixed("navball", Qid($"{p}/navball"),
                Line($"{p}/navball/pitch", "pitch", () => Navball(vesselId).PitchDeg.ToString()),
                Line($"{p}/navball/yaw", "yaw", () => Navball(vesselId).YawDeg.ToString()),
                Line($"{p}/navball/roll", "roll", () => Navball(vesselId).RollDeg.ToString()),
                Line($"{p}/navball/twr", "twr", () => Formats.Scalar(Navball(vesselId).ThrustWeightRatio)),
                Line($"{p}/navball/deltav", "deltav", () => Formats.Scalar(Navball(vesselId).DeltaVVacuumMs)),
                Line($"{p}/navball/frame", "frame", () => Navball(vesselId).Frame),
                Line($"{p}/navball/speed", "speed", () => Formats.Scalar(Navball(vesselId).SpeedMs)));

        private VfsDirectory EnvironmentDir(string p, string vesselId)
            => DelegateDirectory.Fixed("environment", Qid($"{p}/environment"),
                Line($"{p}/environment/pressure", "pressure", () => Formats.Scalar(Env(vesselId).PressurePa)),
                Line($"{p}/environment/density", "density", () => Formats.Scalar(Env(vesselId).DensityKgM3)),
                Line($"{p}/environment/dynamic_pressure", "dynamic_pressure",
                    () => Formats.Scalar(Env(vesselId).DynamicPressurePa)),
                Line($"{p}/environment/ocean_density", "ocean_density",
                    () => Formats.Scalar(Env(vesselId).OceanDensityKgM3)),
                Line($"{p}/environment/terrain_radius", "terrain_radius",
                    () => Formats.Scalar(Env(vesselId).TerrainRadiusM)),
                Line($"{p}/environment/accel", "accel", () => Formats.Vector(Env(vesselId).AccelBody)),
                Line($"{p}/environment/angular_accel", "angular_accel",
                    () => Formats.Vector(Env(vesselId).AngularAccelBody)),
                Line($"{p}/environment/g_force", "g_force", () => Formats.Scalar(Env(vesselId).GForce)));

        private VfsDirectory BatteryDir(string p, string vesselId)
            => DelegateDirectory.Fixed("battery", Qid($"{p}/battery"),
                Line($"{p}/battery/charge", "charge", () => Formats.Scalar(Battery(vesselId))),
                Line($"{p}/battery/fraction", "fraction", () => Formats.Scalar(Battery(vesselId))),
                Line($"{p}/battery/capacity", "capacity",
                    () => Formats.Scalar(Vessel(vesselId).BatteryCapacityJoules ?? 0)));

        private VfsDirectory PowerDir(string p, string vesselId)
            => DelegateDirectory.Fixed("power", Qid($"{p}/power"),
                Line($"{p}/power/produced", "produced", () => Formats.Scalar(Vessel(vesselId).PowerProducedW)),
                Line($"{p}/power/consumed", "consumed", () => Formats.Scalar(Vessel(vesselId).PowerConsumedW)));

        private VfsDirectory EnginesDir(string p, string vesselId)
            => new DelegateDirectory("engines", Qid($"{p}/engines"),
                () => Vessel(vesselId).Engines
                    .Select(e => (VfsNode)EngineDir(p, vesselId, e.Index))
                    .ToArray(),
                name => int.TryParse(name, out var index)
                        && Vessel(vesselId).Engines.Any(e => e.Index == index)
                    ? EngineDir(p, vesselId, index)
                    : null);

        private VfsDirectory EngineDir(string p, string vesselId, int index)
            => DelegateDirectory.Fixed($"{index}", Qid($"{p}/engines/{index}"),
                FlagControl($"{p}/engines/{index}/active", "active", vesselId, "engine.active", index,
                    () => Formats.Flag(Engine(vesselId, index).Active)),
                Line($"{p}/engines/{index}/vac_thrust", "vac_thrust",
                    () => Formats.Scalar(Engine(vesselId, index).VacThrustN)),
                Line($"{p}/engines/{index}/isp", "isp", () => Formats.Scalar(Engine(vesselId, index).IspS)),
                Line($"{p}/engines/{index}/throttle", "throttle",
                    () => Formats.Scalar(Engine(vesselId, index).ThrottleCmd)),
                Line($"{p}/engines/{index}/propellant", "propellant",
                    () => Formats.Flag(Engine(vesselId, index).PropellantAvailable)),
                FractionControl($"{p}/engines/{index}/min_throttle", "min_throttle", vesselId,
                    "engine.min_throttle", index, () => Formats.Scalar(Engine(vesselId, index).MinThrottle)));

        // ---- top-level parts (the welds anchor picker; read-only, cached by the reader) -----------

        private VfsDirectory PartsDir(string p, string vesselId)
            => new DelegateDirectory("parts", Qid($"{p}/parts"),
                () => Vessel(vesselId).Parts
                    .Select(pt => (VfsNode)PartDir(p, vesselId, pt.Index))
                    .ToArray(),
                name => int.TryParse(name, out var index)
                        && Vessel(vesselId).Parts.Any(pt => pt.Index == index)
                    ? PartDir(p, vesselId, index)
                    : null);

        private VfsDirectory PartDir(string p, string vesselId, int index)
            => DelegateDirectory.Fixed($"{index}", Qid($"{p}/parts/{index}"),
                // instance_id is the STABLE handle a weld anchors to (Part.Id can collide).
                Line($"{p}/parts/{index}/instance_id", "instance_id",
                    () => Formats.UInt(Part(vesselId, index).InstanceId)),
                Line($"{p}/parts/{index}/id", "id", () => Part(vesselId, index).Id),
                Line($"{p}/parts/{index}/display_name", "display_name",
                    () => Part(vesselId, index).DisplayName),
                Line($"{p}/parts/{index}/template", "template", () => Part(vesselId, index).Template),
                Line($"{p}/parts/{index}/is_root", "is_root",
                    () => Formats.Flag(Part(vesselId, index).IsRoot)),
                Line($"{p}/parts/{index}/subpart_count", "subpart_count",
                    () => Part(vesselId, index).SubpartCount.ToString(CultureInfo.InvariantCulture)),
                Line($"{p}/parts/{index}/position", "position",
                    () => Formats.Vector(Part(vesselId, index).PositionVehicleAsmb)));

        // ---- control surface (only when a command sink is wired — KSA_GAME_INTEGRATION_PLAN T1) ----

        /// <summary>A <c>0</c>/<c>1</c> STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile FlagControl(string qidPath, string name, string vesselId, string action, int ordinal,
            Func<string> read)
            => _commands is { } sink
                ? ControlFile.Flag(name, Qid(qidPath), sink, read,
                    v => new SimCommand(vesselId, action, ordinal, v))
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>A <c>0..1</c> STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile FractionControl(string qidPath, string name, string vesselId, string action, int ordinal,
            Func<string> read)
            => _commands is { } sink
                ? ControlFile.Fraction(name, Qid(qidPath), sink, read,
                    v => new SimCommand(vesselId, action, ordinal, v))
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>An unbounded numeric STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile NumberControl(string qidPath, string name, string vesselId, string action, int ordinal,
            Func<string> read)
            => _commands is { } sink
                ? ControlFile.Number(name, Qid(qidPath), sink, read,
                    v => new SimCommand(vesselId, action, ordinal, v))
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>A fixed-arity vector STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile VectorControl(string qidPath, string name, string vesselId, string action, int ordinal,
            int arity, Func<string> read)
            => _commands is { } sink
                ? VectorControlFile.Create(name, Qid(qidPath), sink, read, arity,
                    v => new SimCommand(vesselId, action, ordinal, 0) { Values = v })
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>A symbolic-token STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile EnumControl(string qidPath, string name, string vesselId, string action,
            IReadOnlyList<string> allowed, Func<string> read)
            => _commands is { } sink
                ? EnumControlFile.Create(name, Qid(qidPath), sink, read, allowed,
                    t => new SimCommand(vesselId, action, SimCommand.NoOrdinal, 0) { Token = t })
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        private VfsDirectory CtlDir(string p, string vesselId)
        {
            var sink = _commands!; // CtlDir is only reached when _commands is non-null
            var q = $"{p}/ctl";
            return DelegateDirectory.Fixed("ctl", Qid(q),
                new TriggerFile("ignite", Qid($"{q}/ignite"), sink,
                    new SimCommand(vesselId, "vessel.ignite", SimCommand.NoOrdinal, 1)),
                new TriggerFile("shutdown", Qid($"{q}/shutdown"), sink,
                    new SimCommand(vesselId, "vessel.shutdown", SimCommand.NoOrdinal, 1)),
                // The ignite/shutdown master as one readable toggle: read = EngineOn (the live game
                // state ignite/shutdown set), write 1 = ignite / 0 = shutdown.
                FlagControl($"{q}/engine", "engine", vesselId, "vessel.engine", SimCommand.NoOrdinal,
                    () => Formats.Flag(Vessel(vesselId).EngineOn)),
                new TriggerFile("stage", Qid($"{q}/stage"), sink,
                    new SimCommand(vesselId, "vessel.stage", SimCommand.NoOrdinal, 1)),
                FractionControl($"{q}/throttle", "throttle", vesselId, "vessel.throttle", SimCommand.NoOrdinal,
                    () => Formats.Scalar(Vessel(vesselId).ThrottleCmd)),
                FlagControl($"{q}/lights", "lights", vesselId, "vessel.lights", SimCommand.NoOrdinal,
                    () => Formats.Flag(Vessel(vesselId).LightsMasterOn)),
                FlagControl($"{q}/rcs", "rcs", vesselId, "vessel.rcs", SimCommand.NoOrdinal,
                    () => Formats.Flag(Vessel(vesselId).RcsOn)),
                EnumControl($"{q}/attitude_mode", "attitude_mode", vesselId, "vessel.attitude_mode",
                    AttitudeModeTokens, () => Vessel(vesselId).AttitudeMode),
                EnumControl($"{q}/attitude_frame", "attitude_frame", vesselId, "vessel.attitude_frame",
                    AttitudeFrameTokens, () => Vessel(vesselId).AttitudeFrame),
                VectorControl($"{q}/attitude_target", "attitude_target", vesselId, "vessel.attitude_target",
                    SimCommand.NoOrdinal, 4, () => Formats.Quat(Vessel(vesselId).AttitudeBody2Cci)),
                VectorControl($"{q}/burn", "burn", vesselId, "vessel.burn", SimCommand.NoOrdinal, 4,
                    () => "0 0 0 0"),
                // Move the main camera to this vessel (write 1). Pure view op — does not switch control.
                new TriggerFile("focus", Qid($"{q}/focus"), sink,
                    new SimCommand(vesselId, "camera.focus", SimCommand.NoOrdinal, 1)));
        }

        // ---- /sim/debug cheat namespace (G-D2; gated by [control] debug_namespace) ----------

        private VfsDirectory DebugDir()
        {
            var sink = _commands!;
            return DelegateDirectory.Fixed("debug", Qid("debug"),
                new DelegateDirectory("vessels", Qid("debug/vessels"),
                    () => SanitizedVessels(_store.Current)
                        .Select(v => (VfsNode)DebugVesselDir(v.Name, v.Vessel.Id))
                        .ToArray(),
                    name => SanitizedVessels(_store.Current)
                        .Where(v => v.Name == name)
                        .Select(v => (VfsNode?)DebugVesselDir(v.Name, v.Vessel.Id))
                        .FirstOrDefault()),
                DelegateDirectory.Fixed("time", Qid("debug/time"),
                    NumberControl("debug/time/warp", "warp", "", "debug.warp", SimCommand.NoOrdinal,
                        () => Formats.Scalar(_store.Current.WarpFactor))),
                // Move the camera to any astronomical by id — vehicle OR body — view-only (the same
                // camera.focus action the per-vessel ctl/focus and bodies/<id>/focus triggers use).
                TokenControlFile.Create("focus", Qid("debug/focus"), sink,
                    () => _store.Current.ActiveVesselId ?? "",
                    t => new SimCommand(t, "camera.focus", SimCommand.NoOrdinal, 0) { Token = t }),
                // Focus AND take control of a vehicle by id (cheat-tier — grants control authority).
                TokenControlFile.Create("control_vessel", Qid("debug/control_vessel"), sink,
                    () => _store.Current.ActiveVesselId ?? "",
                    t => new SimCommand(t, "debug.control_vessel", SimCommand.NoOrdinal, 0) { Token = t }),
                // Global render hack: force interior (IVA) meshes visible outside the IVA camera.
                FlagControl("debug/always_render_iva", "always_render_iva", "", "debug.always_render_iva",
                    SimCommand.NoOrdinal, () => Formats.Flag(_store.Current.AlwaysRenderIva)),
                // The welds registry view + global ops (per-source weld/unweld live under debug/vessels/<id>/).
                WeldsDir(),
                // The thug-life sunglasses registry: add/clear/count + one editable entry per quad.
                ThugLifeDir());
        }

        // ---- welds cheat (G-D; gated by debug_namespace) ------------------------------------------

        /// <summary>
        ///     The welds registry view + global ops. <c>clear</c> drops every weld; <c>count</c> reports
        ///     how many are active; each active weld appears as a <c>&lt;source_id&gt;/</c> subdir. The
        ///     per-source create/remove controls live under <c>debug/vessels/&lt;id&gt;/</c> (so the source
        ///     is path-implied, like teleport).
        /// </summary>
        private VfsDirectory WeldsDir()
        {
            var sink = _commands!;
            VfsNode Clear() => new TriggerFile("clear", Qid("debug/welds/clear"), sink,
                new SimCommand("", "debug.weld_clear", SimCommand.NoOrdinal, 1));
            VfsNode Count() => Line("debug/welds/count", "count",
                () => _store.Current.Welds.Count.ToString(CultureInfo.InvariantCulture));
            return new DelegateDirectory("welds", Qid("debug/welds"),
                () =>
                {
                    var children = new List<VfsNode> { Clear(), Count() };
                    children.AddRange(SanitizedWelds(_store.Current)
                        .Select(w => (VfsNode)WeldDir(w.Name, w.Weld.SourceId)));
                    return children.ToArray();
                },
                name => name switch
                {
                    "clear" => Clear(),
                    "count" => Count(),
                    _ => SanitizedWelds(_store.Current)
                        .Where(w => w.Name == name)
                        .Select(w => (VfsNode?)WeldDir(w.Name, w.Weld.SourceId))
                        .FirstOrDefault(),
                });
        }

        private VfsDirectory WeldDir(string sanitized, string sourceId)
        {
            var sink = _commands!;
            var q = $"debug/welds/{sanitized}";
            return DelegateDirectory.Fixed(sanitized, Qid(q),
                Line($"{q}/target", "target", () => Weld(sourceId).TargetId),
                Line($"{q}/part", "part", () => Formats.UInt(Weld(sourceId).PartInstanceId)),
                Line($"{q}/offset", "offset", () => Formats.Vector(Weld(sourceId).Offset)),
                Line($"{q}/rotation", "rotation", () => Formats.Vector(Weld(sourceId).Rotation)),
                Line($"{q}/lock_rotation", "lock_rotation", () => Formats.Flag(Weld(sourceId).LockRotation)),
                // Suspend/resume this weld without removing it.
                FlagControl($"{q}/enabled", "enabled", sourceId, "debug.weld_enable", SimCommand.NoOrdinal,
                    () => Formats.Flag(Weld(sourceId).Enabled)));
        }

        // ---- thug-life cheat (G-D; gated by debug_namespace) --------------------------------------

        /// <summary>
        ///     The thug-life sunglasses registry: <c>add</c> creates a new anchored quad (returns nothing —
        ///     the new entry appears under its id), <c>clear</c> removes all, <c>count</c> reports how many
        ///     are active, and each active quad is an editable <c>&lt;id&gt;/</c> subdir. Entries are keyed by
        ///     an integer id — the smallest free slot at create, reused after remove/clear — carried in the
        ///     command <c>Ordinal</c>.
        /// </summary>
        private VfsDirectory ThugLifeDir()
        {
            var sink = _commands!;
            VfsNode Add() => LineControlFile.Create("add", Qid("debug/thug_life/add"), sink,
                () => "", ParseThugLifeAdd);
            VfsNode Clear() => new TriggerFile("clear", Qid("debug/thug_life/clear"), sink,
                new SimCommand("", "debug.thug_life_clear", SimCommand.NoOrdinal, 1));
            VfsNode Count() => Line("debug/thug_life/count", "count",
                () => _store.Current.ThugLife.Count.ToString(CultureInfo.InvariantCulture));
            VfsNode Help() => new StaticTextFile("help", Qid("debug/thug_life/help"), () => ThugLifeHelp);
            return new DelegateDirectory("thug_life", Qid("debug/thug_life"),
                () =>
                {
                    var children = new List<VfsNode> { Help(), Add(), Clear(), Count() };
                    children.AddRange(_store.Current.ThugLife
                        .Select(t => (VfsNode)ThugLifeEntryDir(t.Id)));
                    return children.ToArray();
                },
                name => name switch
                {
                    "help" => Help(),
                    "add" => Add(),
                    "clear" => Clear(),
                    "count" => Count(),
                    _ => int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                         && _store.Current.ThugLife.Any(t => t.Id == id)
                        ? ThugLifeEntryDir(id)
                        : null,
                });
        }

        /// <summary>
        ///     The console-friendly readme behind <c>/sim/debug/thug_life/help</c> (a static text leaf):
        ///     how to anchor/tune/remove the sunglasses, with worked examples on the EVA Kitten vehicles
        ///     (ids <c>Hunter</c>, <c>Polaris</c>, <c>Banjo</c>).
        /// </summary>
        private const string ThugLifeHelp =
            """
            thug_life — stick "thug life" sunglasses (a flat textured quad) onto a part of a
            vehicle; it tracks that part every frame. Pure cosmetic cheat. All paths below are
            under /sim/debug/thug_life/ (needs the debug namespace enabled).

            CREATE
              echo "<vessel> <part_iid>" > add
              echo "<vessel> <part_iid> <x> <y> <z> <pitch> <yaw> <roll> <w> <h>" > add
                part_iid       a part instance_id (see FIND A PART), or 0 = the vehicle frame
                x y z          offset in the part's local frame, metres   (default 0 0 0)
                pitch yaw roll rotation, degrees                          (default 0 0 0)
                w h            quad size, metres                          (default 0.975 0.1875)
              Each add takes the lowest free id (0, 1, 2, ...) under thug_life/<id>/;
              remove/clear free ids for reuse, so the numbering tracks what's live.

            FIND A PART
              ls  /sim/vessels/by-id/Hunter/parts/
              cat /sim/vessels/by-id/Hunter/parts/0/instance_id    # the stable handle to pass
              cat /sim/vessels/by-id/Hunter/parts/0/display_name

            TUNE / INSPECT  (per entry <id>; write a file to set it, read it to see the current value)
              position  "x y z"           3 numbers, metres, in the anchor part's local frame.
                                          Axes follow the part; "0 0 0" sits right on the anchor.
                  echo "0 0.25 0" > thug_life/0/position
              rotation  "pitch yaw roll"  3 numbers, degrees, applied in the part's local frame.
                  echo "0 0 15"   > thug_life/0/rotation
              size      "width height"    2 numbers, metres — the quad's size in the world.
                  echo "1.2 0.24" > thug_life/0/size
              visible   0 | 1             0 hides the quad (entry kept); 1 shows it.
                  echo 0          > thug_life/0/visible
              spec      (read-only)       the full add-compatible line; echo it to add to clone.
                  cat thug_life/0/spec

            REMOVE
              echo 1 > thug_life/0/remove              # one entry
              echo 1 > thug_life/clear                 # every entry
              cat     thug_life/count                  # how many are active

            EXAMPLES  (EVA Kittens: Hunter, Polaris, Banjo)
              # Shades on Hunter's root part:
              iid=$(cat /sim/vessels/by-id/Hunter/parts/0/instance_id)
              echo "Hunter $iid" > /sim/debug/thug_life/add

              # Shades on Polaris at its body frame, nudged up 0.2 m and made bigger:
              echo "Polaris 0 0 0.2 0 0 0 0 1.5 0.29" > /sim/debug/thug_life/add

              # Give the whole squad shades (part_iid 0 = vehicle frame):
              for k in Hunter Polaris Banjo; do echo "$k 0" > /sim/debug/thug_life/add; done

              # Tilt the last one, then take everyone's shades off:
              echo "0 0 20" > /sim/debug/thug_life/2/rotation
              echo 1        > /sim/debug/thug_life/clear

            Notes: entries are runtime-only (cleared on mod unload); if the anchor part is staged
            away it falls back to the vehicle frame. The same actions work over HTTP /v1 and MQTT.

            """;

        private VfsDirectory ThugLifeEntryDir(int id)
        {
            var sink = _commands!;
            var key = id.ToString(CultureInfo.InvariantCulture);
            var q = $"debug/thug_life/{key}";
            return DelegateDirectory.Fixed(key, Qid(q),
                Line($"{q}/vessel", "vessel", () => ThugLife(id).VesselId),
                Line($"{q}/part", "part", () => Formats.UInt(ThugLife(id).PartInstanceId)),
                // Live-tunable transform in the part's local frame (registry-keyed: vesselId "" + id in ordinal).
                VectorControl($"{q}/position", "position", "", "debug.thug_life_position", id, 3,
                    () => Formats.Vector(ThugLife(id).Position)),
                VectorControl($"{q}/rotation", "rotation", "", "debug.thug_life_rotation", id, 3,
                    () => Formats.Vector(ThugLife(id).Rotation)),
                VectorControl($"{q}/size", "size", "", "debug.thug_life_size", id, 2,
                    () => $"{Formats.Scalar(ThugLife(id).Width)} {Formats.Scalar(ThugLife(id).Height)}"),
                FlagControl($"{q}/visible", "visible", "", "debug.thug_life_visible", id,
                    () => Formats.Flag(ThugLife(id).Visible)),
                new TriggerFile("remove", Qid($"{q}/remove"), sink,
                    new SimCommand("", "debug.thug_life_remove", id, 1)),
                // The full write-compatible spec line (echo to add to recreate as a new id).
                Line($"{q}/spec", "spec", () => Formats.ThugLifeSpec(ThugLife(id))));
        }

        /// <summary>
        ///     Parses a thug-life <c>add</c> line — either <c>"&lt;vessel&gt; &lt;part_iid&gt;"</c> (2 tokens,
        ///     transform defaulted) or the full 10-token
        ///     <c>"&lt;vessel&gt; &lt;part_iid&gt; x y z pitch yaw roll width height"</c> — into a
        ///     <c>debug.thug_life_add</c> command. Returns null (⇒ EINVAL) on any malformed token.
        /// </summary>
        private static SimCommand? ParseThugLifeAdd(string line)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is not (2 or 10) || parts[0].Length == 0)
                return null;
            // part_iid: a non-negative integer.
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var iid)
                || !double.IsFinite(iid) || iid < 0 || iid != Math.Floor(iid))
                return null;
            // Values: [iid, x, y, z, pitch, yaw, roll, width, height] — defaults for the 2-token form.
            var values = new double[9];
            values[0] = iid;
            values[7] = 0.975; // default width (unscience)
            values[8] = 0.1875; // default height (keeps the 26:5 texture aspect at a uniform block size)
            if (parts.Length == 10)
                for (var i = 0; i < 8; i++) // x y z pitch yaw roll width height
                    if (!double.TryParse(parts[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture,
                            out values[i + 1]) || !double.IsFinite(values[i + 1]))
                        return null;
            return new SimCommand("", "debug.thug_life_add", SimCommand.NoOrdinal, 0)
            {
                Token = parts[0],
                Values = values,
            };
        }

        private VfsDirectory DebugVesselDir(string sanitized, string vesselId)
        {
            var sink = _commands!;
            var q = $"debug/vessels/{sanitized}";
            var children = new List<VfsNode>
            {
                VectorControl($"{q}/teleport", "teleport", vesselId, "debug.teleport", SimCommand.NoOrdinal, 6,
                    () => "0 0 0 0 0 0"),
                new TriggerFile("refill_fuel", Qid($"{q}/refill_fuel"), sink,
                    new SimCommand(vesselId, "debug.refill_fuel", SimCommand.NoOrdinal, 1)),
                new TriggerFile("refill_battery", Qid($"{q}/refill_battery"), sink,
                    new SimCommand(vesselId, "debug.refill_battery", SimCommand.NoOrdinal, 1)),
                // Weld this vessel (source) to a target part with an explicit pose. Write:
                //   "<target_id> <part_iid> <x> <y> <z> <pitch> <yaw> <roll> <lock>"
                // (part_iid 0 = anchor to the target body frame; lock 0/1). Read = the current spec or "".
                LineControlFile.Create("weld", Qid($"{q}/weld"), sink,
                    () => WeldReadback(vesselId), line => ParseWeld(vesselId, line)),
                // Weld at the CURRENT relative pose (captured now). Write: "<target_id> <part_iid> [<lock>]".
                LineControlFile.Create("weld_here", Qid($"{q}/weld_here"), sink,
                    () => WeldReadback(vesselId), line => ParseWeldHere(vesselId, line)),
                // Remove this source's weld.
                new TriggerFile("unweld", Qid($"{q}/unweld"), sink,
                    new SimCommand(vesselId, "debug.weld_remove", SimCommand.NoOrdinal, 1)),
            };
            // Per-docking-port cheat knobs (only when the vessel carries docking ports). Non-throwing
            // presence check — Vessel() would throw if the snapshot lost the vessel mid-walk.
            if (_store.Current.Vessels.FirstOrDefault(v => v.Id == vesselId)?.Docking.Count > 0)
                children.Add(DebugDockingDir(q, vesselId));
            return DelegateDirectory.Fixed(sanitized, Qid(q), children.ToArray());
        }

        private VfsDirectory DebugDockingDir(string q, string vesselId)
            => new DelegateDirectory("docking", Qid($"{q}/docking"),
                () => Vessel(vesselId).Docking
                    .Select(d => (VfsNode)DebugDockingPortDir(q, vesselId, d.Index)).ToArray(),
                name => int.TryParse(name, out var idx) && Vessel(vesselId).Docking.Any(d => d.Index == idx)
                    ? DebugDockingPortDir(q, vesselId, idx)
                    : null);

        private VfsDirectory DebugDockingPortDir(string q, string vesselId, int index)
            => DelegateDirectory.Fixed($"{index}", Qid($"{q}/docking/{index}"),
                // Read shows the live impulse value in N·s (stock 7000); write overwrites
                // DockingPort.PushoffImpulse, the separation impulse the regular docking/<n>/undock
                // trigger then applies.
                NumberControl($"{q}/docking/{index}/pushoff_impulse", "pushoff_impulse", vesselId,
                    "debug.docking_pushoff", index, () => Formats.Scalar(Docking(vesselId, index).PushoffImpulseNs)));

        private VfsDirectory AnimationsDir(string p, string vesselId)
        {
            var basePath = $"{p}/animations";
            return new DelegateDirectory("animations", Qid(basePath),
                () => Vessel(vesselId).Animations
                    .Select(a => (VfsNode)AnimationDir(basePath, vesselId, a.Index.ToString(), a.Index))
                    .ToArray(),
                name => int.TryParse(name, out var idx) && Vessel(vesselId).Animations.Any(a => a.Index == idx)
                    ? AnimationDir(basePath, vesselId, idx.ToString(), idx)
                    : null);
        }

        /// <summary>
        ///     Solar panels by index (KSA_GAME_INTEGRATION_PLAN §4.6): electrical reads always, plus
        ///     the deploy <c>goal</c>/<c>current</c>/<c>state</c> control when the panel has a deploy
        ///     animation (<see cref="SolarSnapshot.AnimationIndex"/>) and a sink is wired.
        /// </summary>
        private VfsDirectory SolarDir(string p, string vesselId)
            => new DelegateDirectory("solar", Qid($"{p}/solar"),
                () => Vessel(vesselId).Solar.Select(s => (VfsNode)SolarPanelDir(p, vesselId, s.Index)).ToArray(),
                name => int.TryParse(name, out var idx) && Vessel(vesselId).Solar.Any(s => s.Index == idx)
                    ? SolarPanelDir(p, vesselId, idx)
                    : null);

        private VfsDirectory SolarPanelDir(string p, string vesselId, int index)
        {
            var q = $"{p}/solar/{index}";
            var panel = Solar(vesselId, index);
            var children = new List<VfsNode>
            {
                Line($"{q}/produced", "produced", () => Formats.Scalar(Solar(vesselId, index).ProducedW)),
                Line($"{q}/occluded", "occluded", () => Formats.Flag(Solar(vesselId, index).Occluded)),
                Line($"{q}/sun_aoa", "sun_aoa", () => Formats.Scalar(Solar(vesselId, index).SunAoaDeg)),
                Line($"{q}/efficiency", "efficiency", () => Formats.Scalar(Solar(vesselId, index).Efficiency)),
            };
            if (panel.HasTracker)
                children.Add(Line($"{q}/tracker_angle", "tracker_angle",
                    () => Formats.Scalar(Solar(vesselId, index).TrackerAngleDeg)));
            if (panel.AnimationIndex >= 0)
            {
                var animIndex = panel.AnimationIndex;
                children.Add(FractionControl($"{q}/goal", "goal", vesselId, "animation.goal", animIndex,
                    () => Formats.Scalar(Anim(vesselId, animIndex).GoalFraction)));
                children.Add(Line($"{q}/current", "current",
                    () => Formats.Scalar(Anim(vesselId, animIndex).CurrentFraction)));
                children.Add(Line($"{q}/state", "state", () => Anim(vesselId, animIndex).DeploymentState));
            }

            return DelegateDirectory.Fixed($"{index}", Qid(q), children.ToArray());
        }

        private VfsDirectory RcsDir(string p, string vesselId)
            => new DelegateDirectory("rcs", Qid($"{p}/rcs"),
                () => Vessel(vesselId).Rcs.Select(r => (VfsNode)RcsThrusterDir(p, vesselId, r.Index)).ToArray(),
                name => int.TryParse(name, out var idx) && Vessel(vesselId).Rcs.Any(r => r.Index == idx)
                    ? RcsThrusterDir(p, vesselId, idx)
                    : null);

        private VfsDirectory RcsThrusterDir(string p, string vesselId, int index)
        {
            var q = $"{p}/rcs/{index}";
            return DelegateDirectory.Fixed($"{index}", Qid(q),
                FlagControl($"{q}/active", "active", vesselId, "rcs.active", index,
                    () => Formats.Flag(Rcs(vesselId, index).Active)),
                Line($"{q}/propellant", "propellant", () => Formats.Flag(Rcs(vesselId, index).PropellantAvailable)),
                Line($"{q}/map", "map", () => Rcs(vesselId, index).ControlMap));
        }

        private VfsDirectory GeneratorsDir(string p, string vesselId)
            => new DelegateDirectory("generators", Qid($"{p}/generators"),
                () => Vessel(vesselId).Generators.Select(g => (VfsNode)GeneratorDir(p, vesselId, g.Index)).ToArray(),
                name => int.TryParse(name, out var idx) && Vessel(vesselId).Generators.Any(g => g.Index == idx)
                    ? GeneratorDir(p, vesselId, idx)
                    : null);

        private VfsDirectory GeneratorDir(string p, string vesselId, int index)
            => DelegateDirectory.Fixed($"{index}", Qid($"{p}/generators/{index}"),
                Line($"{p}/generators/{index}/active", "active", () => Formats.Flag(Generator(vesselId, index).Active)),
                Line($"{p}/generators/{index}/produced", "produced",
                    () => Formats.Scalar(Generator(vesselId, index).ProducedW)));

        private VfsDirectory LightsDir(string p, string vesselId)
            => new DelegateDirectory("lights", Qid($"{p}/lights"),
                () => Vessel(vesselId).Lights.Select(l => (VfsNode)LightDir(p, vesselId, l.Index)).ToArray(),
                name => int.TryParse(name, out var idx) && Vessel(vesselId).Lights.Any(l => l.Index == idx)
                    ? LightDir(p, vesselId, idx)
                    : null);

        /// <summary>
        ///     One light by index: <c>on</c>/<c>brightness</c>/<c>color</c>/<c>inner_angle</c>/<c>outer_angle</c>
        ///     always, plus the co-located actuate <c>goal</c>/<c>current</c>/<c>state</c> control when the
        ///     light part carries a deploy animation (<see cref="LightSnapshot.AnimationIndex"/>). The same
        ///     vessel-level animation is also reachable under <c>animations/&lt;n&gt;/</c>; both route
        ///     the one <c>animation.goal</c> action by its ordinal (mirrors <see cref="SolarPanelDir"/>).
        /// </summary>
        private VfsDirectory LightDir(string p, string vesselId, int index)
        {
            var q = $"{p}/lights/{index}";
            var light = Light(vesselId, index);
            var children = new List<VfsNode>
            {
                FlagControl($"{q}/on", "on", vesselId, "light.on", index, () => Formats.Flag(Light(vesselId, index).On)),
                NumberControl($"{q}/brightness", "brightness", vesselId, "light.brightness", index,
                    () => Formats.Scalar(Light(vesselId, index).Intensity)),
                VectorControl($"{q}/color", "color", vesselId, "light.color", index, 3,
                    () => Formats.Vector(Light(vesselId, index).Color)),
                NumberControl($"{q}/outer_angle", "outer_angle", vesselId, "light.outer_angle", index,
                    () => Formats.Scalar(Light(vesselId, index).OuterAngleDeg)),
                NumberControl($"{q}/inner_angle", "inner_angle", vesselId, "light.inner_angle", index,
                    () => Formats.Scalar(Light(vesselId, index).InnerAngleDeg)),
            };
            if (light.AnimationIndex >= 0)
            {
                var animIndex = light.AnimationIndex;
                children.Add(FractionControl($"{q}/goal", "goal", vesselId, "animation.goal", animIndex,
                    () => Formats.Scalar(Anim(vesselId, animIndex).GoalFraction)));
                children.Add(Line($"{q}/current", "current",
                    () => Formats.Scalar(Anim(vesselId, animIndex).CurrentFraction)));
                children.Add(Line($"{q}/state", "state", () => Anim(vesselId, animIndex).DeploymentState));
            }

            return DelegateDirectory.Fixed($"{index}", Qid(q), children.ToArray());
        }

        private VfsDirectory DockingDir(string p, string vesselId)
            => new DelegateDirectory("docking", Qid($"{p}/docking"),
                () => Vessel(vesselId).Docking.Select(d => (VfsNode)DockingPortDir(p, vesselId, d.Index)).ToArray(),
                name => int.TryParse(name, out var idx) && Vessel(vesselId).Docking.Any(d => d.Index == idx)
                    ? DockingPortDir(p, vesselId, idx)
                    : null);

        private VfsDirectory DockingPortDir(string p, string vesselId, int index)
        {
            var q = $"{p}/docking/{index}";
            var children = new List<VfsNode>
            {
                Line($"{q}/docked", "docked", () => Formats.Flag(Docking(vesselId, index).Docked)),
                Line($"{q}/docked_to", "docked_to", () => Docking(vesselId, index).DockedToPart ?? ""),
                Line($"{q}/pushoff_impulse", "pushoff_impulse",
                    () => Formats.Scalar(Docking(vesselId, index).PushoffImpulseNs)),
            };
            // Undock (G4): a one-shot TRIGGER mirroring decoupler fire — write 1 to separate this
            // docked port. Only present when a command sink is wired.
            if (_commands is { } sink)
                children.Add(new TriggerFile("undock", Qid($"{q}/undock"), sink,
                    new SimCommand(vesselId, "docking.undock", index, 1)));
            return DelegateDirectory.Fixed($"{index}", Qid(q), children.ToArray());
        }

        private VfsDirectory DecouplersDir(string p, string vesselId)
            => new DelegateDirectory("decouplers", Qid($"{p}/decouplers"),
                () => Vessel(vesselId).Decouplers.Select(d => (VfsNode)DecouplerDir(p, vesselId, d.Index)).ToArray(),
                name => int.TryParse(name, out var idx) && Vessel(vesselId).Decouplers.Any(d => d.Index == idx)
                    ? DecouplerDir(p, vesselId, idx)
                    : null);

        private VfsDirectory DecouplerDir(string p, string vesselId, int index)
        {
            var q = $"{p}/decouplers/{index}";
            var children = new List<VfsNode>
            {
                Line($"{q}/fired", "fired", () => Formats.Flag(Decoupler(vesselId, index).Fired)),
            };
            if (_commands is { } sink)
                children.Add(new TriggerFile("fire", Qid($"{q}/fire"), sink,
                    new SimCommand(vesselId, "decoupler.fire", index, 1)));
            return DelegateDirectory.Fixed($"{index}", Qid(q), children.ToArray());
        }

        private VfsDirectory AnimationDir(string basePath, string vesselId, string entryName, int animIndex)
        {
            var q = $"{basePath}/{entryName}";
            return DelegateDirectory.Fixed(entryName, Qid(q),
                FractionControl($"{q}/goal", "goal", vesselId, "animation.goal", animIndex,
                    () => Formats.Scalar(Anim(vesselId, animIndex).GoalFraction)),
                Line($"{q}/current", "current", () => Formats.Scalar(Anim(vesselId, animIndex).CurrentFraction)),
                Line($"{q}/state", "state", () => Anim(vesselId, animIndex).DeploymentState));
        }

        private VfsDirectory TanksDir(string p, string vesselId)
            => new DelegateDirectory("tanks", Qid($"{p}/tanks"),
                () => SanitizedTanks(vesselId)
                    .Select(t => (VfsNode)TankDir(p, vesselId, t.Name))
                    .ToArray(),
                name => SanitizedTanks(vesselId).Any(t => t.Name == name)
                    ? TankDir(p, vesselId, name)
                    : null);

        private VfsDirectory TankDir(string p, string vesselId, string tankName)
            => DelegateDirectory.Fixed(tankName, Qid($"{p}/tanks/{tankName}"),
                Line($"{p}/tanks/{tankName}/amount", "amount",
                    () => Formats.Scalar(Tank(vesselId, tankName).Amount)),
                Line($"{p}/tanks/{tankName}/capacity", "capacity",
                    () => Formats.Scalar(Tank(vesselId, tankName).Capacity)),
                Line($"{p}/tanks/{tankName}/fraction", "fraction",
                    () => Formats.Scalar(Tank(vesselId, tankName).Fraction)));

        // ---- live accessors (ENOENT when the entity vanished — OS_PLAN.md T7.1/T8.2) -------

        private VesselSnapshot Vessel(string vesselId)
            => _store.Current.Vessels.FirstOrDefault(v => v.Id == vesselId)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' is gone");

        private OrbitSnapshot Orbit(string vesselId)
            => Vessel(vesselId).Orbit
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' is not in orbit");

        private double Battery(string vesselId)
            => Vessel(vesselId).BatteryChargeFraction
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' has no battery");

        private EngineSnapshot Engine(string vesselId, int index)
            => Vessel(vesselId).Engines.FirstOrDefault(e => e.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"engine {index} is gone");

        private AnimationSnapshot Anim(string vesselId, int index)
            => Vessel(vesselId).Animations.FirstOrDefault(a => a.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"animation {index} is gone");

        private TankSnapshot Tank(string vesselId, string tankName)
            => SanitizedTanks(vesselId).Where(t => t.Name == tankName).Select(t => t.Tank).FirstOrDefault()
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"tank '{tankName}' is gone");

        private NavballSnapshot Navball(string vesselId)
            => Vessel(vesselId).Navball
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' has no navball");

        private EnvironmentSnapshot Env(string vesselId)
            => Vessel(vesselId).Environment
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' has no environment");

        private RcsSnapshot Rcs(string vesselId, int index)
            => Vessel(vesselId).Rcs.FirstOrDefault(r => r.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"rcs {index} is gone");

        private SolarSnapshot Solar(string vesselId, int index)
            => Vessel(vesselId).Solar.FirstOrDefault(s => s.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"solar {index} is gone");

        private GeneratorSnapshot Generator(string vesselId, int index)
            => Vessel(vesselId).Generators.FirstOrDefault(g => g.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"generator {index} is gone");

        private LightSnapshot Light(string vesselId, int index)
            => Vessel(vesselId).Lights.FirstOrDefault(l => l.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"light {index} is gone");

        private DockingSnapshot Docking(string vesselId, int index)
            => Vessel(vesselId).Docking.FirstOrDefault(d => d.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"docking {index} is gone");

        private DecouplerSnapshot Decoupler(string vesselId, int index)
            => Vessel(vesselId).Decouplers.FirstOrDefault(d => d.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"decoupler {index} is gone");

        private PartSnapshot Part(string vesselId, int index)
            => Vessel(vesselId).Parts.FirstOrDefault(pt => pt.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"part {index} is gone");

        private WeldSnapshot Weld(string sourceId)
            => _store.Current.Welds.FirstOrDefault(w => w.SourceId == sourceId)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"weld for '{sourceId}' is gone");

        private ThugLifeSnapshot ThugLife(int id)
            => _store.Current.ThugLife.FirstOrDefault(t => t.Id == id)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"thug_life entry {id} is gone");

        /// <summary>The current weld spec for a source (write-compatible), or "" when not welded.</summary>
        private string WeldReadback(string sourceId)
            => _store.Current.Welds.FirstOrDefault(w => w.SourceId == sourceId) is { } w
                ? Formats.WeldSpec(w)
                : "";

        /// <summary>
        ///     Parses an explicit weld line — <c>"&lt;target&gt; &lt;part_iid&gt; x y z pitch yaw roll lock"</c>
        ///     (9 tokens) — into a <c>debug.weld_create</c> command. Returns null (⇒ EINVAL) on any
        ///     malformed token: non-finite number, non-integer/negative <c>part_iid</c>, or <c>lock</c>∉{0,1}.
        /// </summary>
        private static SimCommand? ParseWeld(string sourceId, string line)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 9 || parts[0].Length == 0)
                return null;
            var values = new double[8]; // part_iid, x, y, z, pitch, yaw, roll, lock
            for (var i = 0; i < 8; i++)
                if (!double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                    || !double.IsFinite(values[i]))
                    return null;
            if (values[0] < 0 || values[0] != Math.Floor(values[0]) || values[7] is not (0 or 1))
                return null;
            return new SimCommand(sourceId, "debug.weld_create", SimCommand.NoOrdinal, 0)
            {
                Token = parts[0],
                Values = values,
            };
        }

        /// <summary>
        ///     Parses a capture-pose weld line — <c>"&lt;target&gt; &lt;part_iid&gt; [lock]"</c> (2–3 tokens) —
        ///     into a <c>debug.weld_here</c> command (the offset/rotation are captured on the game thread).
        /// </summary>
        private static SimCommand? ParseWeldHere(string sourceId, string line)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 2 or > 3 || parts[0].Length == 0)
                return null;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var part)
                || !double.IsFinite(part) || part < 0 || part != Math.Floor(part))
                return null;
            var lockRot = 1.0;
            if (parts.Length == 3
                && (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out lockRot)
                    || lockRot is not (0 or 1)))
                return null;
            return new SimCommand(sourceId, "debug.weld_here", SimCommand.NoOrdinal, 0)
            {
                Token = parts[0],
                Values = [part, lockRot],
            };
        }

        private List<(string Name, TankSnapshot Tank)> SanitizedTanks(string vesselId)
            => SanitizeNames(Vessel(vesselId).Tanks, t => t.Resource);

        private static List<(string Name, WeldSnapshot Weld)> SanitizedWelds(SimSnapshot snapshot)
            => SanitizeNames(snapshot.Welds, w => w.SourceId);

        // ---- naming / qids -------------------------------------------------------------------

        private static List<(string Name, VesselSnapshot Vessel)> SanitizedVessels(SimSnapshot snapshot)
            => SanitizeNames(snapshot.Vessels, v => v.Id);

        /// <summary>
        ///     Maps items to unique directory names: anything outside <c>[A-Za-z0-9._-]</c>
        ///     becomes <c>_</c>; duplicates get <c>~2</c>, <c>~3</c>… in listing order
        ///     (OS_PLAN.md T8.2).
        /// </summary>
        private static List<(string Name, T Item)> SanitizeNames<T>(IReadOnlyList<T> items, Func<T, string> key)
        {
            var result = new List<(string, T)>(items.Count);
            var used = new Dictionary<string, int>();
            foreach (var item in items)
            {
                var name = Sanitize(key(item));
                if (used.TryGetValue(name, out var count))
                {
                    used[name] = count + 1;
                    name = $"{name}~{count + 1}";
                }
                else
                {
                    used[name] = 1;
                }

                result.Add((name, item));
            }

            return result;
        }

        private static string Sanitize(string id)
        {
            Span<char> chars = id.Length <= 64 ? stackalloc char[id.Length] : new char[id.Length];
            for (var i = 0; i < id.Length; i++)
            {
                var c = id[i];
                chars[i] = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                    or '.' or '_' or '-'
                    ? c
                    : '_';
            }

            var sanitized = new string(chars);
            return sanitized switch
            {
                "" => "_",
                "." or ".." => "_" + sanitized,
                _ => sanitized,
            };
        }

        private StaticTextFile Line(string qidPath, string name, Func<string> value)
            => new(name, Qid(qidPath), () => value() + "\n");

        private ulong Qid(string path)
            => _qids.GetOrAdd(path, _ => (ulong)Interlocked.Increment(ref _nextQid));
    }
}
