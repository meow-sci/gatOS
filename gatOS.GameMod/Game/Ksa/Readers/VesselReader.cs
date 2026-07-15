using Brutal.Numerics;
using gatOS.GameMod.Game.Ksa.Actuators;
using gatOS.GameMod.Game.Ksa.Render;
using gatOS.Logging;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Telemetry;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Readers;

/// <summary>
///     The vessel telemetry reader (KSA_GAME_INTEGRATION_PLAN §3.2): the only code that turns a
///     KSA <see cref="Vehicle"/> into an immutable <see cref="VesselSnapshot"/>. Every KSA member
///     it touches carries a <see cref="KsaAnchorAttribute"/> so a decomp drop that moves an API
///     surfaces as a build break here, not a mod crash (the churn playbook §3.4). Pure functions,
///     game-thread only; every double is scrubbed through <see cref="Sanitize"/>.
/// </summary>
/// <remarks>
///     <para><see cref="Sample"/> runs one of two single-pass builders: <see cref="BuildFull"/>
///     (core + the G3 read-surface extensions, constructing the snapshot <b>once</b>) when detail
///     is on, else <see cref="BuildCore"/>. The full build runs inside a guard: if any extension
///     API has drifted, the vessel falls back to its core telemetry and the extension dirs simply
///     don't appear — graceful degradation rather than a blanked vessel. The fault is logged once.</para>
///     <para><b>Single-pass discipline (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP3).</b> This
///     runs per vessel per sample tick on the game thread, so every module span and state list is
///     fetched once, the battery is walked once (fraction + capacity together), power production is
///     accumulated inside the solar/generator passes instead of a second state read, structural
///     animation links come from the per-vehicle <see cref="AnimationLinks"/> cache, and stable
///     enum values stringify through <see cref="EnumText"/> — the steady state allocates only the
///     snapshot records themselves.</para>
/// </remarks>
internal static class VesselReader
{
    private const double StandardGravity = 9.80665;
    private const double RadToDeg = 180.0 / Math.PI;
    private static bool _enrichErrorLogged;

    [KsaAnchor("Vehicle core state (Id/Situation/GetPositionCci/OrbitalSpeed/masses/attitude)",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "Verified at M9; Name = Id (KSA has no separate display name).")]
    internal static VesselSnapshot Sample(Vehicle vehicle, string? activeVesselId, double utSeconds, bool detail)
    {
        // detail off (config telemetry_vessel_detail): skip the whole G3 extension surface. It is
        // the most expensive per-vessel work, so a player who only needs core flight telemetry
        // pays none of it.
        if (detail)
            try
            {
                return BuildFull(vehicle, activeVesselId, utSeconds);
            }
            catch (Exception ex)
            {
                if (!_enrichErrorLogged)
                {
                    _enrichErrorLogged = true;
                    ModLog.Log.Warn($"telemetry: vessel read extensions degraded (logged once): {ex.Message}");
                }
            }

        return BuildCore(vehicle, activeVesselId, utSeconds);
    }

    /// <summary>The always-on core reads, gathered once and shared by both builders.</summary>
    private struct Basics
    {
        public IParentBody? Parent;
        public double3Snap PositionCci;
        public double LatitudeDeg, LongitudeDeg;
        public QuatSnap Attitude;
        public double3Snap BodyRates;
        public double OrbitalSpeed, SurfaceSpeed, InertialSpeed;
        public double BarometricAltitude, RadarAltitude;
        public double MassTotal, MassDry, MassPropellant;
        public string Situation;
        public double? BatteryFraction, BatteryCapacity;
        public bool Controlled, Controllable, EngineOn, LightsMasterOn;
        public double Scale;
        public bool AlwaysRender;
    }

    private static Basics ReadBasics(Vehicle vehicle, string? activeVesselId)
    {
        var parent = vehicle.Parent; // IParentBody (=> Orbit.Parent)
        var positionCci = vehicle.GetPositionCci();

        // Lat/lon via the body's own frame math: CCI → CCF, then IParentBody.GetLlaFromCcf.
        double latitudeDeg = 0, longitudeDeg = 0;
        if (parent is not null)
        {
            // The static Transform avoids the extension-method overload set (which would drag
            // BepuUtilities into overload resolution — CS0012 without that reference).
            var positionCcf = double3.Transform(positionCci, parent.GetCci2Ccf());
            var lla = parent.GetLlaFromCcf(positionCcf);
            latitudeDeg = Sanitize.Finite(lla.X);
            longitudeDeg = Sanitize.Finite(lla.Y);
        }

        var attitude = vehicle.GetBody2Cci();
        var (batteryFraction, batteryCapacity) = SampleBattery(vehicle);

        return new Basics
        {
            Parent = parent,
            PositionCci = Vec(positionCci),
            LatitudeDeg = latitudeDeg,
            LongitudeDeg = longitudeDeg,
            Attitude = new QuatSnap(
                Sanitize.Finite(attitude.X), Sanitize.Finite(attitude.Y),
                Sanitize.Finite(attitude.Z), Sanitize.Finite(attitude.W)),
            BodyRates = Vec(vehicle.BodyRates),
            OrbitalSpeed = Sanitize.Finite(vehicle.OrbitalSpeed),
            SurfaceSpeed = Sanitize.Finite(vehicle.GetSurfaceSpeed()),
            InertialSpeed = Sanitize.Finite(vehicle.GetInertialSpeed()),
            BarometricAltitude = Sanitize.Finite(vehicle.GetBarometricAltitude()),
            RadarAltitude = Sanitize.Finite(vehicle.GetRadarAltitude()),
            MassTotal = Sanitize.Finite(vehicle.TotalMass),
            MassDry = Sanitize.Finite(vehicle.InertMass),
            MassPropellant = Sanitize.Finite(vehicle.PropellantMass),
            Situation = EnumText.Of(vehicle.Situation),
            BatteryFraction = batteryFraction,
            BatteryCapacity = batteryCapacity,
            Controlled = activeVesselId is not null && vehicle.Id == activeVesselId,
            Controllable = ReadControllable(vehicle),
            EngineOn = ReadEngineOn(vehicle),
            LightsMasterOn = vehicle.LightsOn,
            // Rides the always-sampled core (not the gated detail pass): one cheap Part.Scale.X
            // read, and the scale node stays truthful with telemetry_vessel_detail off.
            Scale = ScaleActuator.Read(vehicle),
            // gatOS-owned registry lookup (no KSA read) — the always_render read-back.
            AlwaysRender = VesselForceRender.IsMarked(vehicle.Id),
        };
    }

    private static VesselSnapshot BuildCore(Vehicle vehicle, string? activeVesselId, double utSeconds)
    {
        var b = ReadBasics(vehicle, activeVesselId);
        var links = AnimationLinks.Get(vehicle, utSeconds);
        return new VesselSnapshot(
            Id: vehicle.Id,
            Name: vehicle.Id, // KSA has no separate display name: Vehicle.SetName assigns Id
            Situation: b.Situation,
            PositionCci: b.PositionCci,
            LatitudeDeg: b.LatitudeDeg,
            LongitudeDeg: b.LongitudeDeg,
            OrbitalSpeed: b.OrbitalSpeed,
            SurfaceSpeed: b.SurfaceSpeed,
            InertialSpeed: b.InertialSpeed,
            AttitudeBody2Cci: b.Attitude,
            BodyRatesRadS: b.BodyRates,
            BarometricAltitude: b.BarometricAltitude,
            RadarAltitude: b.RadarAltitude,
            MassTotal: b.MassTotal,
            MassDry: b.MassDry,
            MassPropellant: b.MassPropellant,
            Orbit: CoreOrbit(vehicle, b.Parent),
            Engines: SampleEngines(vehicle, detail: false),
            Tanks: SampleTanks(vehicle),
            BatteryChargeFraction: b.BatteryFraction,
            ParentBodyName: b.Parent?.Id,
            LightsMasterOn: b.LightsMasterOn,
            Animations: SampleAnimations(vehicle, links))
        {
            Controlled = b.Controlled,
            Controllable = b.Controllable,
            EngineOn = b.EngineOn,
            Scale = b.Scale,
            AlwaysRender = b.AlwaysRender,
        };
    }

    [KsaAnchor("Vehicle.{GetPositionEcl,GetVelocityCci,CenterOfMassAsmb,GetManualThrottle}",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "The G3 extension vectors + the writable-setpoint throttle read-back. Without the "
            + "read-backs the snapshot reported record defaults (0/false/\"\") for the ctl files.")]
    private static VesselSnapshot BuildFull(Vehicle vehicle, string? activeVesselId, double utSeconds)
    {
        var b = ReadBasics(vehicle, activeVesselId);
        var links = AnimationLinks.Get(vehicle, utSeconds);

        // Power production is accumulated inside the solar/generator passes — the states are read
        // once, not re-fetched by a separate power pass (GP3).
        var (solar, solarProducedW) = SampleSolar(vehicle, links);
        var (generators, generatorProducedW) = SampleGenerators(vehicle);

        var rcs = SampleRcs(vehicle);
        // The vessel-level RCS master flag is "any thruster controller active" (RcsActuator.SetMaster
        // toggles them all); derive it from the per-controller states we just sampled.
        var rcsOn = false;
        for (var i = 0; i < rcs.Count; i++)
            if (rcs[i].Active)
            {
                rcsOn = true;
                break;
            }

        var (attitudeMode, attitudeFrame) = SampleFlightComputer(vehicle);

        return new VesselSnapshot(
            Id: vehicle.Id,
            Name: vehicle.Id,
            Situation: b.Situation,
            PositionCci: b.PositionCci,
            LatitudeDeg: b.LatitudeDeg,
            LongitudeDeg: b.LongitudeDeg,
            OrbitalSpeed: b.OrbitalSpeed,
            SurfaceSpeed: b.SurfaceSpeed,
            InertialSpeed: b.InertialSpeed,
            AttitudeBody2Cci: b.Attitude,
            BodyRatesRadS: b.BodyRates,
            BarometricAltitude: b.BarometricAltitude,
            RadarAltitude: b.RadarAltitude,
            MassTotal: b.MassTotal,
            MassDry: b.MassDry,
            MassPropellant: b.MassPropellant,
            Orbit: FullOrbit(vehicle, b.Parent, utSeconds),
            Engines: SampleEngines(vehicle, detail: true),
            Tanks: SampleTanks(vehicle),
            BatteryChargeFraction: b.BatteryFraction,
            ParentBodyName: b.Parent?.Id,
            LightsMasterOn: b.LightsMasterOn,
            Animations: SampleAnimations(vehicle, links))
        {
            Controlled = b.Controlled,
            Controllable = b.Controllable,
            EngineOn = b.EngineOn,
            Scale = b.Scale,
            AlwaysRender = b.AlwaysRender,
            PositionEcl = Vec(vehicle.GetPositionEcl()),
            VelocityCci = Vec(vehicle.GetVelocityCci()),
            CenterOfMass = Vec(vehicle.CenterOfMassAsmb),
            Navball = SampleNavball(vehicle),
            Environment = SampleEnvironment(vehicle),
            BatteryCapacityJoules = b.BatteryCapacity,
            PowerProducedW = Sanitize.Finite(solarProducedW + generatorProducedW),
            PowerConsumedW = SamplePowerConsumed(vehicle),
            // The writable-setpoint read-backs the control files surface (ctl/throttle, ctl/rcs,
            // ctl/translate, ctl/attitude_mode, ctl/attitude_frame).
            ThrottleCmd = Sanitize.Finite(vehicle.GetManualThrottle()),
            TranslateCmd = SampleTranslate(vehicle),
            RcsOn = rcsOn,
            AttitudeMode = attitudeMode,
            AttitudeFrame = attitudeFrame,
            Rcs = rcs,
            Solar = solar,
            Generators = generators,
            Lights = SampleLights(vehicle, links),
            Docking = SampleDocking(vehicle),
            Decouplers = SampleDecouplers(vehicle),
            Encounters = SampleEncounters(vehicle),
        };
    }

    [KsaAnchor("Vehicle.IsSet(VehicleEngine.MainIgnite, false) => _manualControlInputs.EngineOn",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-07-03", GameVersion = "2026.7.3.4826", Risk = ChurnRisk.Medium,
        Notes = "The live ignition master ctl/ignite (MainIgnite) and ctl/shutdown (MainShutdown) toggle; "
            + "the same state the game's ignite button reads. NOT per-engine IsActive (allowed-to-fire). "
            + "4826: Vehicle.Split now copies _manualControlInputs to the separated vehicle, so a freshly "
            + "decoupled/undocked stage inherits the parent's engine-on state (was: reset to off).")]
    private static bool ReadEngineOn(Vehicle vehicle) => vehicle.IsSet(VehicleEngine.MainIgnite, false);

    [KsaAnchor("Vehicle.IsControllable => _overrideIsControllable || Parts.Controls.NumModules > 0",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "4750/rev 4699: a vessel with no Control Module (Parts.Controls.NumModules==0) cannot be "
            + "controlled by the player or the flight computer (control + FC paths gate via ControlsLockout). "
            + "Reported as vessels/<id>/controllable so guests/autopilots can pre-check; gatOS itself does not "
            + "gate (relies on KSA's lockout). The player-controlled vessel always has a Control Module.")]
    private static bool ReadControllable(Vehicle vehicle) => vehicle.IsControllable;

    [KsaAnchor("Vehicle.GetManualThrottle(); FlightComputer.{AttitudeMode,AttitudeTrackTarget,AttitudeFrame}",
        SourceFile = "KSA/Vehicle.cs / KSA/FlightComputer.cs", Verified = "2026-07-03", GameVersion = "2026.7.3.4826", Risk = ChurnRisk.Medium,
        Notes = "Read-back of the writable setpoints. Manual attitude reports \"manual\"; auto reports the "
                + "track-target name (Prograde/…). Frame is the VehicleReferenceFrame name. 4826: a freshly "
                + "split (decoupled/undocked) vehicle inherits the parent's manual throttle (Vehicle.Split "
                + "copies _manualControlInputs), so the post-split ctl/throttle read-back is no longer 0.")]
    private static (string Mode, string Frame) SampleFlightComputer(Vehicle vehicle)
    {
        var fc = vehicle.FlightComputer;
        var mode = fc.AttitudeMode == FlightComputerAttitudeMode.Manual
            ? "manual"
            : EnumText.Of(fc.AttitudeTrackTarget);
        return (mode, EnumText.Of(fc.AttitudeFrame));
    }

    /// <summary>The <c>ctl/translate</c> read-back: live flags decoded to body-axis signs.</summary>
    private static double3Snap SampleTranslate(Vehicle vehicle)
    {
        var (x, y, z) = TranslateActuator.Read(vehicle);
        return new double3Snap(x, y, z);
    }

    // ---- orbit ------------------------------------------------------------------------------

    private static OrbitSnapshot? CoreOrbit(Vehicle vehicle, IParentBody? parent)
    {
        if (vehicle.Orbit is not { } o || parent is null)
            return null;
        return new OrbitSnapshot(
            // KSA apsides are radii from the body center; the /sim contract is altitudes.
            Sanitize.RadiusToAltitude(o.Apoapsis, parent.MeanRadius),
            Sanitize.RadiusToAltitude(o.Periapsis, parent.MeanRadius),
            Sanitize.Finite(o.Eccentricity),
            Sanitize.Finite(o.Inclination * RadToDeg), // stored in radians
            Sanitize.Finite(o.SemiMajorAxis),
            Sanitize.Finite(o.Period));
    }

    [KsaAnchor("Orbit.StateVectors.TrueAnomaly.Degrees; LongitudeOfAscendingNode/ArgumentOfPeriapsis (rad); "
               + "Vehicle.NextApoapsisTime/NextPeriapsisTime/NextPatchEventTime",
        SourceFile = "KSA/Orbit.cs / KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low)]
    private static OrbitSnapshot? FullOrbit(Vehicle vehicle, IParentBody? parent, double utSeconds)
    {
        if (vehicle.Orbit is not { } o || parent is null)
            return null;
        return new OrbitSnapshot(
            Sanitize.RadiusToAltitude(o.Apoapsis, parent.MeanRadius),
            Sanitize.RadiusToAltitude(o.Periapsis, parent.MeanRadius),
            Sanitize.Finite(o.Eccentricity),
            Sanitize.Finite(o.Inclination * RadToDeg),
            Sanitize.Finite(o.SemiMajorAxis),
            Sanitize.Finite(o.Period))
        {
            LanDeg = Sanitize.Finite(o.LongitudeOfAscendingNode * RadToDeg),
            ArgPeDeg = Sanitize.Finite(o.ArgumentOfPeriapsis * RadToDeg),
            TrueAnomalyDeg = Sanitize.Finite(o.StateVectors.TrueAnomaly.Degrees),
            TimeToApoapsis = TimeUntil(vehicle.NextApoapsisTime, utSeconds),
            TimeToPeriapsis = TimeUntil(vehicle.NextPeriapsisTime, utSeconds),
            NextPatchEventUt = Sanitize.Finite(vehicle.NextPatchEventTime.Seconds()),
        };
    }

    private static double TimeUntil(SimTime target, double utSeconds)
    {
        var dt = Sanitize.Finite(target.Seconds()) - utSeconds;
        return dt > 0 ? dt : 0;
    }

    [KsaAnchor("Vehicle.NavBallData{AttitudeAngles(int3,deg),ThrustWeightRatio,DeltaVInVacuum,Frame,Speed}",
        SourceFile = "KSA/NavBallData.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "AttitudeAngles X/Y/Z = pitch/yaw/roll in whole degrees.")]
    private static NavballSnapshot SampleNavball(Vehicle vehicle)
    {
        ref readonly var nb = ref vehicle.NavBallData;
        return new NavballSnapshot(
            nb.AttitudeAngles.X, nb.AttitudeAngles.Y, nb.AttitudeAngles.Z,
            Sanitize.Finite(nb.ThrustWeightRatio), Sanitize.Finite(nb.DeltaVInVacuum),
            EnumText.Of(nb.Frame), Sanitize.Finite(nb.Speed));
    }

    [KsaAnchor("Vehicle.PhysicsEnvironment{AtmosphericPressure,AtmosphericDensity,OceanDensity,TerrainRadius}; "
               + "PhysicalAtmosphereReference.GetDynamicPressure(Vehicle); AccelerationBody/AngularAccelerationBody",
        SourceFile = "KSA/PhysicsEnvironment.cs / KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low)]
    private static EnvironmentSnapshot SampleEnvironment(Vehicle vehicle)
    {
        ref readonly var env = ref vehicle.PhysicsEnvironment;
        var accel = vehicle.AccelerationBody;
        var gForce = Sanitize.Finite(accel.Length() / StandardGravity);
        return new EnvironmentSnapshot(
            Sanitize.Finite(env.AtmosphericPressure),
            Sanitize.Finite(env.AtmosphericDensity),
            Sanitize.Finite(PhysicalAtmosphereReference.GetDynamicPressure(vehicle)),
            Sanitize.Finite(env.OceanDensity),
            Sanitize.Finite(env.TerrainRadius),
            Vec(accel),
            Vec(vehicle.AngularAccelerationBody),
            gForce);
    }

    // ---- engines (M9 core + G3 throttle/propellant, one pass) --------------------------------

    [KsaAnchor("vehicle.Parts.Modules.Get<EngineController>(); .IsActive, .VacuumData{ThrustMax,MassFlowRateMax}",
        SourceFile = "KSA/EngineController.cs", Verified = "2026-07-03", GameVersion = "2026.7.3.4826", Risk = ChurnRisk.Medium,
        Notes = "Isp computed thrust/(massflow·g0). Index is the vessel-level ordinal the control addresses. "
            + "4826: Decoupler.Decouple no longer force-deactivates the separated stage's IActivate modules, "
            + "so engines/<n>/active on a just-decoupled vehicle retains its pre-split state (was: false).")]
    [KsaAnchor("EngineControllerState{CommandThrottle,IsPropellantAvailable} via ModuleStateful.TryGetFrom",
        SourceFile = "KSA/EngineControllerState.cs", Verified = "2026-07-14", GameVersion = "2026.7.5.4892", Risk = ChurnRisk.Medium,
        Notes = "Read in the same pass as the module walk (GP3); detail-off skips the state fetch and "
            + "leaves ThrottleCmd/PropellantAvailable at the record defaults, exactly as before. "
            + "4892: FlightComputer.CommandEngineThrottles now zeroes CommandThrottle/CommandBurnTime "
            + "when no burn is commanded, so engines/<n>/throttle reads an honest 0 after burn end "
            + "instead of the last commanded value (members unchanged).")]
    private static List<EngineSnapshot> SampleEngines(Vehicle vehicle, bool detail)
    {
        var modules = vehicle.Parts.Modules.Get<EngineController>();
        var engines = new List<EngineSnapshot>(modules.Length);
        if (detail && modules.Length > 0
            && TryStates<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>(
                vehicle, out var states))
        {
            for (var i = 0; i < modules.Length; i++)
            {
                var engine = modules[i];
                ref readonly var st = ref states.GetState(engine);
                engines.Add(BuildEngine(engine, i,
                    Sanitize.Finite(st.CommandThrottle), st.IsPropellantAvailable));
            }
        }
        else
        {
            // detail off (or no state list): ThrottleCmd/PropellantAvailable keep the record
            // defaults, exactly like the pre-GP3 core pass.
            for (var i = 0; i < modules.Length; i++)
                engines.Add(BuildEngine(modules[i], i, throttleCmd: 0, propellantAvailable: false));
        }

        return engines;
    }

    private static EngineSnapshot BuildEngine(EngineController engine, int index,
        double throttleCmd, bool propellantAvailable)
    {
        double vacThrust = engine.VacuumData.ThrustMax.Length();
        double massFlow = engine.VacuumData.MassFlowRateMax;
        var isp = massFlow > 0 ? vacThrust / (massFlow * StandardGravity) : 0;
        return new EngineSnapshot(index, engine.IsActive,
            Sanitize.Finite(vacThrust), Sanitize.Finite(isp))
        {
            MinThrottle = Sanitize.Finite(engine.MinimumThrottle),
            ThrottleCmd = throttleCmd,
            PropellantAvailable = propellantAvailable,
        };
    }

    // ---- tanks -------------------------------------------------------------------------------

    [KsaAnchor("vehicle.Parts.Modules.Get<Tank>().Moles; vehicle.Parts.Moles.GetState(mole).Mass; Mole.FilledFraction",
        SourceFile = "KSA/Tank.cs / KSA/Mole.cs", Verified = "2026-07-14", GameVersion = "2026.7.5.4892", Risk = ChurnRisk.Low,
        Notes = "A Tank holds one Mole per substance; amounts live in the SoA Moles state list. "
            + "4892: the rev-4884 combustion->Reactions refactor is additive here (Tank gains "
            + "RoleAffinity/AssignedMix; Moles/MoleState/FilledFraction untouched) - tanks now "
            + "auto-assign a propellant mix by affinity and the substance catalog changed, so tank "
            + "resource names on new vehicles differ from the 4826 era.")]
    private static List<TankSnapshot> SampleTanks(Vehicle vehicle)
    {
        var tanks = new List<TankSnapshot>();
        var moleStates = vehicle.Parts.Moles;
        foreach (var tank in vehicle.Parts.Modules.Get<Tank>())
        {
            foreach (var mole in tank.Moles)
            {
                ref readonly var state = ref moleStates.GetState(mole);
                double amount = state.Mass;
                double capacity = mole.GetLiquidMass(mole.ContainerVolume);
                tanks.Add(new TankSnapshot(mole.SubstancePhase.Name,
                    Sanitize.Finite(amount), Sanitize.Finite(capacity))
                {
                    Fraction = Math.Clamp(Sanitize.Finite(mole.FilledFraction(in state)), 0, 1),
                });
            }
        }

        return tanks;
    }

    // ---- battery / power ----------------------------------------------------------------------

    [KsaAnchor("vehicle.Parts.Batteries.GetState(b).Charge, b.MaximumCapacity",
        SourceFile = "KSA/Battery.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Low,
        Notes = "Charge fraction + capacity (Joules) summed across all batteries; null when none. Walked "
            + "once per sample (GP3 — the fraction and capacity come from the same pass). 4750/rev 4681: "
            + "Charge/MaximumCapacity are now the Joules struct (.Value() float, magnitude unchanged).")]
    private static (double? Fraction, double? CapacityJoules) SampleBattery(Vehicle vehicle)
    {
        var batteries = vehicle.Parts.Batteries;
        double charge = 0, capacity = 0;
        foreach (var battery in batteries.Modules)
        {
            charge += batteries.GetState(battery).Charge.Value();
            capacity += battery.MaximumCapacity.Value();
        }

        return capacity > 0
            ? (Math.Clamp(Sanitize.Finite(charge / capacity), 0, 1), Sanitize.Finite(capacity))
            : (null, null);
    }

    [KsaAnchor("vehicle.Parts.PowerConsumers.GetState(c).Consumed (Watts; .Value() float)",
        SourceFile = "KSA/PowerConsumerState.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium, Notes = "Instantaneous watts (W); 4750/rev 4681 Joules→Watts.")]
    private static double SamplePowerConsumed(Vehicle vehicle)
    {
        double total = 0;
        var consumers = vehicle.Parts.PowerConsumers;
        foreach (var consumer in consumers.Modules)
            total += consumers.GetState(consumer).Consumed.Value();
        return Sanitize.Finite(total);
    }

    // ---- RCS / solar / generators / lights / docking / decouplers -----------------------------

    [KsaAnchor("vehicle.Parts.Modules.Get<ThrusterController>(); .IsActive; ThrusterControllerState{ControlMap,IsPropellantAvailable}",
        SourceFile = "KSA/ThrusterController.cs", Verified = "2026-07-03", GameVersion = "2026.7.3.4826", Risk = ChurnRisk.Medium,
        Notes = "4826: Decoupler.Decouple no longer force-deactivates the separated stage's IActivate modules, "
            + "so rcs/<n>/active on a just-decoupled vehicle retains its pre-split state (was: false).")]
    private static IReadOnlyList<RcsSnapshot> SampleRcs(Vehicle vehicle)
    {
        var modules = vehicle.Parts.Modules.Get<ThrusterController>();
        if (modules.Length == 0)
            return [];
        var hasStates =
            TryStates<ThrusterController, ThrusterControllerState, ThrusterControllerGlobalState, EmptyStruct>(
                vehicle, out var states);
        var result = new List<RcsSnapshot>(modules.Length);
        for (var i = 0; i < modules.Length; i++)
        {
            var thruster = modules[i];
            var map = "None";
            var propellant = false;
            if (hasStates)
            {
                ref readonly var st = ref states.GetState(thruster);
                map = EnumText.Of(st.ControlMap);
                propellant = st.IsPropellantAvailable;
            }

            result.Add(new RcsSnapshot(i, thruster.IsActive, propellant, map));
        }

        return result;
    }

    [KsaAnchor("vehicle.Parts.Modules.Get<SolarPanel>(); SolarPanelState{Produced,IsOccluded,SunAoA,SunEfficiency}; "
               + "SolarTrackerState.CurrentAngle",
        SourceFile = "KSA/SolarPanel.cs / KSA/SolarTracker.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "Produced is instantaneous watts (W) (4750/rev 4681 Joules→Watts), and the per-panel values "
                + "are accumulated into the vessel PowerProducedW sum in this same pass (GP3 — no separate "
                + "power-state read). AnimationIndex comes from the AnimationLinks cache; tracker 1:1 by "
                + "index when counts match.")]
    private static (IReadOnlyList<SolarSnapshot> Panels, double ProducedW) SampleSolar(
        Vehicle vehicle, AnimationLinks.Entry links)
    {
        var panels = vehicle.Parts.Modules.Get<SolarPanel>();
        if (panels.Length == 0)
            return ([], 0);

        var hasStates = TryStates<SolarPanel, SolarPanelState, EmptyStruct, EmptyStruct>(vehicle, out var states);

        // Trackers are separate modules; correlate 1:1 by index only when the counts match, and
        // extract their angles up front so the StateList ref struct stays inside its guard.
        var trackers = vehicle.Parts.Modules.Get<SolarTracker>();
        double[]? trackerAngles = null;
        if (trackers.Length == panels.Length
            && TryStates<SolarTracker, SolarTrackerState, EmptyStruct, EmptyStruct>(vehicle, out var trackerStates))
        {
            trackerAngles = new double[trackers.Length];
            for (var i = 0; i < trackers.Length; i++)
                trackerAngles[i] = trackerStates.GetState(trackers[i]).CurrentAngle * RadToDeg;
        }

        double producedTotal = 0;
        var result = new List<SolarSnapshot>(panels.Length);
        for (var i = 0; i < panels.Length; i++)
        {
            var panel = panels[i];
            double produced = 0, sunAoa = 0, efficiency = 0;
            var occluded = false;
            if (hasStates)
            {
                ref readonly var st = ref states.GetState(panel);
                produced = st.Produced.Value();
                occluded = st.IsOccluded;
                sunAoa = st.SunAoA * RadToDeg;
                efficiency = st.SunEfficiency;
            }

            producedTotal += produced;
            result.Add(new SolarSnapshot(i, Sanitize.Finite(produced), occluded,
                Sanitize.Finite(sunAoa), Sanitize.Finite(efficiency),
                trackerAngles is not null, Sanitize.Finite(trackerAngles?[i] ?? 0),
                AnimationIndex: links.SolarAnimationIndex[i]));
        }

        return (result, producedTotal);
    }

    [KsaAnchor("vehicle.Parts.Modules.Get<Generator>(); GeneratorState{Active,Produced (Watts)}",
        SourceFile = "KSA/Generator.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "Produced is instantaneous watts (W); 4750/rev 4681 Joules→Watts. Per-generator values are "
            + "accumulated into the vessel PowerProducedW sum in this same pass (GP3).")]
    private static (IReadOnlyList<GeneratorSnapshot> Generators, double ProducedW) SampleGenerators(Vehicle vehicle)
    {
        var modules = vehicle.Parts.Modules.Get<Generator>();
        if (modules.Length == 0
            || !TryStates<Generator, GeneratorState, EmptyStruct, EmptyStruct>(vehicle, out var states))
            return ([], 0);
        double producedTotal = 0;
        var result = new List<GeneratorSnapshot>(modules.Length);
        for (var i = 0; i < modules.Length; i++)
        {
            ref readonly var st = ref states.GetState(modules[i]);
            var produced = st.Produced.Value();
            producedTotal += produced;
            result.Add(new GeneratorSnapshot(i, st.Active, Sanitize.Finite(produced)));
        }

        return (result, producedTotal);
    }

    [KsaAnchor("vehicle.Parts.Modules.Get<LightModule>(); .Template.Intensity.Value/.ColorRgb/.OuterAngle.Value/.InnerAngle.Value; "
               + "Parent.FullPart.LightSwitch.LightIsActive",
        SourceFile = "KSA/LightModule.cs", Verified = "2026-06-23", Risk = ChurnRisk.High,
        Notes = "Template internals are High-churn; on-state reads the part's LightSwitch PowerConsumer. "
                + "OuterAngle/InnerAngle are the spotlight cone half-angles (radians); exposed as "
                + "outer_angle/inner_angle (degrees) — read per tick, NOT cached, because the light "
                + "controls write them live. AnimationIndex comes from the AnimationLinks cache (GP3), "
                + "so lights/<n>/goal co-locates the deploy control alongside on/brightness/color/angles.")]
    private static IReadOnlyList<LightSnapshot> SampleLights(Vehicle vehicle, AnimationLinks.Entry links)
    {
        var modules = vehicle.Parts.Modules.Get<LightModule>();
        if (modules.Length == 0)
            return [];
        var result = new List<LightSnapshot>(modules.Length);
        for (var i = 0; i < modules.Length; i++)
        {
            var light = modules[i];
            var on = light.Parent.FullPart.LightSwitch?.LightIsActive ?? false;
            double intensity = light.Template.Intensity.Value;
            var rgb = light.Template.ColorRgb;
            result.Add(new LightSnapshot(i, on, Sanitize.Finite(intensity),
                new double3Snap(Sanitize.Finite(rgb.R), Sanitize.Finite(rgb.G), Sanitize.Finite(rgb.B)),
                AnimationIndex: links.LightAnimationIndex[i])
            {
                OuterAngleDeg = Sanitize.Finite(light.Template.OuterAngle.Value * RadToDeg),
                InnerAngleDeg = Sanitize.Finite(light.Template.InnerAngle.Value * RadToDeg),
            });
        }

        return result;
    }

    [KsaAnchor("vehicle.Parts.Modules.Get<DockingPort>(); .Docked, .DockedToPart.Id, .PushoffImpulse",
        SourceFile = "KSA/DockingPort.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750",
        Risk = ChurnRisk.Medium,
        Notes = "PushoffImpulse is a public required float (newton-seconds, N·s) seeded from "
            + "DockingPortTemplate.PushoffImpulse (stock 7000 N·s); the undock separation impulse "
            + "Vehicle.Split(Connector, splitImpulse) applies. Read here so the debug control reads it "
            + "back. 4750 (rev 4683) renamed PushoffForce→PushoffImpulse, force (N)→impulse (N·s).")]
    private static IReadOnlyList<DockingSnapshot> SampleDocking(Vehicle vehicle)
    {
        var modules = vehicle.Parts.Modules.Get<DockingPort>();
        if (modules.Length == 0)
            return [];
        var result = new List<DockingSnapshot>(modules.Length);
        for (var i = 0; i < modules.Length; i++)
        {
            var port = modules[i];
            result.Add(new DockingSnapshot(i, port.Docked, port.DockedToPart?.Id)
            {
                PushoffImpulseNs = Sanitize.Finite(port.PushoffImpulse),
            });
        }

        return result;
    }

    [KsaAnchor("vehicle.Parts.Modules.Get<Decoupler>(); .IsActive (fired, irreversible)",
        SourceFile = "KSA/Decoupler.cs", Verified = "2026-07-03", GameVersion = "2026.7.3.4826", Risk = ChurnRisk.Medium,
        Notes = "4826: Decoupler.Decouple dropped its fire-time cascade that walked the separated vehicle "
            + "deactivating every IActivate module — module active/fired state on the separated stage now "
            + "persists as-is. IsActive itself (fired, irreversible) is unchanged.")]
    private static IReadOnlyList<DecouplerSnapshot> SampleDecouplers(Vehicle vehicle)
    {
        var modules = vehicle.Parts.Modules.Get<Decoupler>();
        if (modules.Length == 0)
            return [];
        var result = new List<DecouplerSnapshot>(modules.Length);
        for (var i = 0; i < modules.Length; i++)
            result.Add(new DecouplerSnapshot(i, modules[i].IsActive));
        return result;
    }

    [KsaAnchor("vehicle.Patch.Encounters; Encounter{Body.Id,GameTime,ClosestDistance}",
        SourceFile = "KSA/PatchedConic.cs / KSA/Encounter.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium)]
    private static IReadOnlyList<EncounterSnapshot> SampleEncounters(Vehicle vehicle)
    {
        var patch = vehicle.Patch;
        var encounters = patch.Encounters;
        if (encounters.Length == 0)
            return [];
        var result = new List<EncounterSnapshot>(encounters.Length);
        foreach (var e in encounters)
        {
            var body = (e.Body as Astronomical)?.Id ?? "?";
            result.Add(new EncounterSnapshot(body, Sanitize.Finite(e.GameTime.Seconds()),
                Sanitize.Finite(e.ClosestDistance)));
        }

        return result;
    }

    // ---- animations (M9) ----------------------------------------------------------------------

    [KsaAnchor("vehicle.Parts.Modules.Get<KeyframeAnimationModule>(); .TimeGoal, .Shared.Duration, State.{TimeCurrent,DeploymentState}",
        SourceFile = "KSA/KeyframeAnimationModule.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "Deploy fraction = time/Duration. The IsSolar flag comes from the AnimationLinks cache "
                + "(GP3 — the per-tick SubtreeModules.HasAny scan moved there). State read via "
                + "ModuleStateful.TryGetFrom(vehicle.Parts.States, …); falls back to goal-derived.")]
    private static List<AnimationSnapshot> SampleAnimations(Vehicle vehicle, AnimationLinks.Entry links)
    {
        var modules = vehicle.Parts.Modules.Get<KeyframeAnimationModule>();
        var result = new List<AnimationSnapshot>(modules.Length);
        if (modules.Length == 0)
            return result;

        var haveStates = TryStates<KeyframeAnimationModule, KeyframeAnimationModule.State, EmptyStruct, EmptyStruct>(
            vehicle, out var states);

        for (var i = 0; i < modules.Length; i++)
        {
            var module = modules[i];
            double duration = module.Shared.Duration;
            double goalFraction = duration > 0 ? Math.Clamp(module.TimeGoal / duration, 0, 1) : 0;
            double currentFraction = goalFraction;
            string deploymentState;
            if (haveStates)
            {
                ref readonly var state = ref states.GetState(module);
                currentFraction = duration > 0 ? Math.Clamp(state.TimeCurrent / duration, 0, 1) : 0;
                deploymentState = EnumText.Of(state.DeploymentState);
            }
            else
            {
                deploymentState = EnumText.Of(KeyframeAnimationModule
                    .DeriveDeploymentState(module.TimeGoal, module.TimeGoal));
            }

            result.Add(new AnimationSnapshot(i, Sanitize.Finite(goalFraction),
                Sanitize.Finite(currentFraction), deploymentState, links.AnimationIsSolar[i]));
        }

        return result;
    }

    /// <summary>The struct-of-arrays state list for a module type, or false when none on this vehicle.</summary>
    private static bool TryStates<TModule, TState, TGlobal, TFx>(
        Vehicle vehicle, out ModuleStateful<TModule, TState, TGlobal, TFx>.StateList states)
        where TModule : ModuleStateful<TModule, TState, TGlobal, TFx>, IDisposable
        where TState : unmanaged
        where TGlobal : unmanaged
        where TFx : struct
        => ModuleStateful<TModule, TState, TGlobal, TFx>.TryGetFrom(vehicle.Parts.States, out states);

    private static double3Snap Vec(double3 v)
        => new(Sanitize.Finite(v.X), Sanitize.Finite(v.Y), Sanitize.Finite(v.Z));
}
