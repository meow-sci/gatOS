using Brutal.Numerics;
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
///     <see cref="Sample"/> builds the M9 core first, then runs <see cref="Enrich"/> (the G3
///     read-surface extensions) inside a guard: if any extension API has drifted, the vessel keeps
///     its core telemetry and the extension dirs simply don't appear — graceful degradation rather
///     than a blanked vessel. The fault is logged once.
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
        var core = SampleCore(vehicle, activeVesselId);
        // detail off (config telemetry_vessel_detail): skip the whole G3 enrich pass. It is the
        // most expensive per-vessel work (navball, environment + every per-module StateList read)
        // and the second VesselSnapshot allocation the `with` clone makes — so a player who only
        // needs core flight telemetry pays neither.
        if (!detail)
            return core;
        try
        {
            return Enrich(core, vehicle, utSeconds);
        }
        catch (Exception ex)
        {
            if (!_enrichErrorLogged)
            {
                _enrichErrorLogged = true;
                ModLog.Log.Warn($"telemetry: vessel read extensions degraded (logged once): {ex.Message}");
            }

            return core;
        }
    }

    private static VesselSnapshot SampleCore(Vehicle vehicle, string? activeVesselId)
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

        OrbitSnapshot? orbit = null;
        if (vehicle.Orbit is { } o && parent is not null)
            orbit = new OrbitSnapshot(
                // KSA apsides are radii from the body center; the /sim contract is altitudes.
                Sanitize.RadiusToAltitude(o.Apoapsis, parent.MeanRadius),
                Sanitize.RadiusToAltitude(o.Periapsis, parent.MeanRadius),
                Sanitize.Finite(o.Eccentricity),
                Sanitize.Finite(o.Inclination * RadToDeg), // stored in radians
                Sanitize.Finite(o.SemiMajorAxis),
                Sanitize.Finite(o.Period));

        var attitude = vehicle.GetBody2Cci();
        var rates = vehicle.BodyRates;
        var (batteryFraction, _) = SampleBattery(vehicle);

        return new VesselSnapshot(
            Id: vehicle.Id,
            Name: vehicle.Id, // KSA has no separate display name: Vehicle.SetName assigns Id
            Situation: vehicle.Situation.ToString(),
            PositionCci: Vec(positionCci),
            LatitudeDeg: latitudeDeg,
            LongitudeDeg: longitudeDeg,
            OrbitalSpeed: Sanitize.Finite(vehicle.OrbitalSpeed),
            SurfaceSpeed: Sanitize.Finite(vehicle.GetSurfaceSpeed()),
            InertialSpeed: Sanitize.Finite(vehicle.GetInertialSpeed()),
            AttitudeBody2Cci: new QuatSnap(
                Sanitize.Finite(attitude.X), Sanitize.Finite(attitude.Y),
                Sanitize.Finite(attitude.Z), Sanitize.Finite(attitude.W)),
            BodyRatesRadS: Vec(rates),
            BarometricAltitude: Sanitize.Finite(vehicle.GetBarometricAltitude()),
            RadarAltitude: Sanitize.Finite(vehicle.GetRadarAltitude()),
            MassTotal: Sanitize.Finite(vehicle.TotalMass),
            MassDry: Sanitize.Finite(vehicle.InertMass),
            MassPropellant: Sanitize.Finite(vehicle.PropellantMass),
            Orbit: orbit,
            Engines: SampleEngines(vehicle),
            Tanks: SampleTanks(vehicle),
            BatteryChargeFraction: batteryFraction,
            ParentBodyName: parent?.Id,
            LightsMasterOn: vehicle.LightsOn,
            Animations: SampleAnimations(vehicle))
        {
            Controlled = activeVesselId is not null && vehicle.Id == activeVesselId,
            Controllable = ReadControllable(vehicle),
            EngineOn = ReadEngineOn(vehicle),
        };
    }

    [KsaAnchor("Vehicle.IsSet(VehicleEngine.MainIgnite, false) => _manualControlInputs.EngineOn",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-14", Risk = ChurnRisk.Medium,
        Notes = "The live ignition master ctl/ignite (MainIgnite) and ctl/shutdown (MainShutdown) toggle; "
            + "the same state the game's ignite button reads. NOT per-engine IsActive (allowed-to-fire).")]
    private static bool ReadEngineOn(Vehicle vehicle) => vehicle.IsSet(VehicleEngine.MainIgnite, false);

    [KsaAnchor("Vehicle.IsControllable => _overrideIsControllable || Parts.Controls.NumModules > 0",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "4750/rev 4699: a vessel with no Control Module (Parts.Controls.NumModules==0) cannot be "
            + "controlled by the player or the flight computer (control + FC paths gate via ControlsLockout). "
            + "Reported as vessels/<id>/controllable so guests/autopilots can pre-check; gatOS itself does not "
            + "gate (relies on KSA's lockout). The player-controlled vessel always has a Control Module.")]
    private static bool ReadControllable(Vehicle vehicle) => vehicle.IsControllable;

    // ---- G3 read-surface extensions (KSA_GAME_INTEGRATION_PLAN §4.5/§4.6) -----------------

    private static VesselSnapshot Enrich(VesselSnapshot core, Vehicle vehicle, double utSeconds)
    {
        var (_, batteryCapacity) = SampleBattery(vehicle);
        var rcs = SampleRcs(vehicle);
        // The vessel-level RCS master flag is "any thruster controller active" (RcsActuator.SetMaster
        // toggles them all); derive it from the per-controller states we just sampled.
        var rcsOn = false;
        foreach (var r in rcs)
            if (r.Active)
            {
                rcsOn = true;
                break;
            }

        var (attitudeMode, attitudeFrame) = SampleFlightComputer(vehicle);
        return core with
        {
            PositionEcl = Vec(vehicle.GetPositionEcl()),
            VelocityCci = Vec(vehicle.GetVelocityCci()),
            CenterOfMass = Vec(vehicle.CenterOfMassAsmb),
            Navball = SampleNavball(vehicle),
            Environment = SampleEnvironment(vehicle),
            Orbit = EnrichOrbit(core.Orbit, vehicle, utSeconds),
            Engines = EnrichEngines(core.Engines, vehicle),
            Tanks = EnrichTanks(core.Tanks, vehicle),
            BatteryCapacityJoules = batteryCapacity,
            PowerProducedW = SamplePowerProduced(vehicle),
            PowerConsumedW = SamplePowerConsumed(vehicle),
            // The writable-setpoint read-backs the control files surface (ctl/throttle, ctl/rcs,
            // ctl/attitude_mode, ctl/attitude_frame). Without these the snapshot reported the
            // record defaults (0 / false / ""), so every transport showed throttle 0% and a blank
            // attitude mode regardless of the real state.
            ThrottleCmd = Sanitize.Finite(vehicle.GetManualThrottle()),
            RcsOn = rcsOn,
            AttitudeMode = attitudeMode,
            AttitudeFrame = attitudeFrame,
            Rcs = rcs,
            Solar = SampleSolar(vehicle),
            Generators = SampleGenerators(vehicle),
            Lights = SampleLights(vehicle),
            Docking = SampleDocking(vehicle),
            Decouplers = SampleDecouplers(vehicle),
            Encounters = SampleEncounters(vehicle),
        };
    }

    [KsaAnchor("Vehicle.GetManualThrottle(); FlightComputer.{AttitudeMode,AttitudeTrackTarget,AttitudeFrame}",
        SourceFile = "KSA/Vehicle.cs / KSA/FlightComputer.cs", Verified = "2026-06-13", Risk = ChurnRisk.Medium,
        Notes = "Read-back of the writable setpoints. Manual attitude reports \"manual\"; auto reports the "
                + "track-target name (Prograde/…). Frame is the VehicleReferenceFrame name.")]
    private static (string Mode, string Frame) SampleFlightComputer(Vehicle vehicle)
    {
        var fc = vehicle.FlightComputer;
        var mode = fc.AttitudeMode == FlightComputerAttitudeMode.Manual
            ? "manual"
            : fc.AttitudeTrackTarget.ToString();
        return (mode, fc.AttitudeFrame.ToString());
    }

    [KsaAnchor("Orbit.StateVectors.TrueAnomaly.Degrees; LongitudeOfAscendingNode/ArgumentOfPeriapsis (rad); "
               + "Vehicle.NextApoapsisTime/NextPeriapsisTime/NextPatchEventTime",
        SourceFile = "KSA/Orbit.cs / KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low)]
    private static OrbitSnapshot? EnrichOrbit(OrbitSnapshot? orbit, Vehicle vehicle, double utSeconds)
    {
        if (orbit is null || vehicle.Orbit is not { } o)
            return orbit;
        return orbit with
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
            nb.Frame.ToString(), Sanitize.Finite(nb.Speed));
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

    // ---- engines (M9 core + G3 throttle/propellant/min) -----------------------------------

    [KsaAnchor("vehicle.Parts.Modules.Get<EngineController>(); .IsActive, .VacuumData{ThrustMax,MassFlowRateMax}",
        SourceFile = "KSA/EngineController.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "Isp computed thrust/(massflow·g0). Index is the vessel-level ordinal the control addresses.")]
    private static List<EngineSnapshot> SampleEngines(Vehicle vehicle)
    {
        var engineModules = vehicle.Parts.Modules.Get<EngineController>();
        var engines = new List<EngineSnapshot>(engineModules.Length);
        for (var i = 0; i < engineModules.Length; i++)
        {
            var engine = engineModules[i];
            double vacThrust = engine.VacuumData.ThrustMax.Length();
            double massFlow = engine.VacuumData.MassFlowRateMax;
            var isp = massFlow > 0 ? vacThrust / (massFlow * StandardGravity) : 0;
            engines.Add(new EngineSnapshot(i, engine.IsActive,
                Sanitize.Finite(vacThrust), Sanitize.Finite(isp))
            {
                MinThrottle = Sanitize.Finite(engine.MinimumThrottle),
            });
        }

        return engines;
    }

    [KsaAnchor("EngineControllerState{CommandThrottle,IsPropellantAvailable} via ModuleStateful.TryGetFrom",
        SourceFile = "KSA/EngineControllerState.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium)]
    private static IReadOnlyList<EngineSnapshot> EnrichEngines(IReadOnlyList<EngineSnapshot> core, Vehicle vehicle)
    {
        var modules = vehicle.Parts.Modules.Get<EngineController>();
        if (modules.Length == 0
            || !TryStates<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>(
                vehicle, out var states))
            return core;

        var result = new List<EngineSnapshot>(core.Count);
        foreach (var e in core)
        {
            if (e.Index >= 0 && e.Index < modules.Length)
            {
                ref readonly var st = ref states.GetState(modules[e.Index]);
                result.Add(e with
                {
                    ThrottleCmd = Sanitize.Finite(st.CommandThrottle),
                    PropellantAvailable = st.IsPropellantAvailable,
                });
            }
            else
            {
                result.Add(e);
            }
        }

        return result;
    }

    // ---- tanks (M9 core + G3 fraction) ----------------------------------------------------

    [KsaAnchor("vehicle.Parts.Modules.Get<Tank>().Moles; vehicle.Parts.Moles.GetState(mole).Mass; Mole.FilledFraction",
        SourceFile = "KSA/Tank.cs / KSA/Mole.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "A Tank holds one Mole per substance; amounts live in the SoA Moles state list.")]
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

    private static IReadOnlyList<TankSnapshot> EnrichTanks(IReadOnlyList<TankSnapshot> core, Vehicle vehicle)
        => core; // SampleTanks already fills Fraction; kept as a seam for symmetry.

    // ---- battery / power ------------------------------------------------------------------

    [KsaAnchor("vehicle.Parts.Batteries.GetState(b).Charge, b.MaximumCapacity",
        SourceFile = "KSA/Battery.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Low,
        Notes = "Charge fraction + capacity (Joules) summed across all batteries; null when none. 4750/rev 4681: Charge/MaximumCapacity are now the Joules struct (.Value() float, magnitude unchanged).")]
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

    [KsaAnchor("SolarPanelState.Produced + GeneratorState.Produced (Watts; .Value() float)",
        SourceFile = "KSA/SolarPanelState.cs / KSA/GeneratorState.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750",
        Risk = ChurnRisk.Medium, Notes = "Instantaneous watts (W); summed over panels + generators. 4750/rev 4681: Joules→Watts (was per-sample energy proxy).")]
    private static double SamplePowerProduced(Vehicle vehicle)
    {
        double total = 0;
        if (TryStates<SolarPanel, SolarPanelState, EmptyStruct, EmptyStruct>(vehicle, out var solar))
            foreach (var panel in solar.Modules)
                total += solar.GetState(panel).Produced.Value();
        if (TryStates<Generator, GeneratorState, EmptyStruct, EmptyStruct>(vehicle, out var gens))
            foreach (var gen in gens.Modules)
                total += gens.GetState(gen).Produced.Value();
        return Sanitize.Finite(total);
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

    // ---- RCS / solar / generators / lights / docking / decouplers -------------------------

    [KsaAnchor("vehicle.Parts.Modules.Get<ThrusterController>(); .IsActive; ThrusterControllerState{ControlMap,IsPropellantAvailable}",
        SourceFile = "KSA/ThrusterController.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium)]
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
                map = st.ControlMap.ToString();
                propellant = st.IsPropellantAvailable;
            }

            result.Add(new RcsSnapshot(i, thruster.IsActive, propellant, map));
        }

        return result;
    }

    [KsaAnchor("vehicle.Parts.Modules.Get<SolarPanel>(); SolarPanelState{Produced,IsOccluded,SunAoA,SunEfficiency}; "
               + "SolarTrackerState.CurrentAngle; SolarPanel.KeyframeAnimationModule",
        SourceFile = "KSA/SolarPanel.cs / KSA/SolarTracker.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "Produced is instantaneous watts (W) (4750/rev 4681 Joules→Watts). AnimationIndex links the "
                + "panel's deploy animation to the vessel-level animation ordinal; tracker 1:1 by index when counts match.")]
    private static IReadOnlyList<SolarSnapshot> SampleSolar(Vehicle vehicle)
    {
        var panels = vehicle.Parts.Modules.Get<SolarPanel>();
        if (panels.Length == 0)
            return [];

        var hasStates = TryStates<SolarPanel, SolarPanelState, EmptyStruct, EmptyStruct>(vehicle, out var states);
        var animations = vehicle.Parts.Modules.Get<KeyframeAnimationModule>();

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

            result.Add(new SolarSnapshot(i, Sanitize.Finite(produced), occluded,
                Sanitize.Finite(sunAoa), Sanitize.Finite(efficiency),
                trackerAngles is not null, Sanitize.Finite(trackerAngles?[i] ?? 0),
                AnimationIndex: AnimationIndexOf(animations, panel.KeyframeAnimationModule)));
        }

        return result;
    }

    private static int AnimationIndexOf(System.Span<KeyframeAnimationModule> animations, KeyframeAnimationModule? target)
    {
        if (target is null)
            return SolarSnapshot.NoAnimation;
        for (var i = 0; i < animations.Length; i++)
            if (ReferenceEquals(animations[i], target))
                return i;
        return SolarSnapshot.NoAnimation;
    }

    [KsaAnchor("vehicle.Parts.Modules.Get<Generator>(); GeneratorState{Active,Produced (Watts)}",
        SourceFile = "KSA/Generator.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium, Notes = "Produced is instantaneous watts (W); 4750/rev 4681 Joules→Watts.")]
    private static IReadOnlyList<GeneratorSnapshot> SampleGenerators(Vehicle vehicle)
    {
        var modules = vehicle.Parts.Modules.Get<Generator>();
        if (modules.Length == 0
            || !TryStates<Generator, GeneratorState, EmptyStruct, EmptyStruct>(vehicle, out var states))
            return [];
        var result = new List<GeneratorSnapshot>(modules.Length);
        for (var i = 0; i < modules.Length; i++)
        {
            ref readonly var st = ref states.GetState(modules[i]);
            result.Add(new GeneratorSnapshot(i, st.Active, Sanitize.Finite(st.Produced.Value())));
        }

        return result;
    }

    [KsaAnchor("vehicle.Parts.Modules.Get<LightModule>(); .Template.Intensity.Value/.ColorRgb/.OuterAngle.Value/.InnerAngle.Value; "
               + "Parent.FullPart.LightSwitch.LightIsActive; Parent.FullPart.SubtreeModules.Get<KeyframeAnimationModule>()",
        SourceFile = "KSA/LightModule.cs", Verified = "2026-06-23", Risk = ChurnRisk.High,
        Notes = "Template internals are High-churn; on-state reads the part's LightSwitch PowerConsumer. "
                + "OuterAngle/InnerAngle are the spotlight cone half-angles (radians); exposed as "
                + "outer_angle/inner_angle (degrees). AnimationIndex links a light part's actuate animation "
                + "to the vessel-level animation ordinal (the same subtree scan SolarPanel.OnPartCreated uses), "
                + "so lights/<n>/goal co-locates the deploy control alongside on/brightness/color/inner_angle/outer_angle.")]
    private static IReadOnlyList<LightSnapshot> SampleLights(Vehicle vehicle)
    {
        var modules = vehicle.Parts.Modules.Get<LightModule>();
        if (modules.Length == 0)
            return [];
        var animations = vehicle.Parts.Modules.Get<KeyframeAnimationModule>();
        var result = new List<LightSnapshot>(modules.Length);
        for (var i = 0; i < modules.Length; i++)
        {
            var light = modules[i];
            var on = light.Parent.FullPart.LightSwitch?.LightIsActive ?? false;
            double intensity = light.Template.Intensity.Value;
            var rgb = light.Template.ColorRgb;
            result.Add(new LightSnapshot(i, on, Sanitize.Finite(intensity),
                new double3Snap(Sanitize.Finite(rgb.R), Sanitize.Finite(rgb.G), Sanitize.Finite(rgb.B)),
                AnimationIndex: AnimationIndexOf(animations, LightAnimation(light)))
            {
                OuterAngleDeg = Sanitize.Finite(light.Template.OuterAngle.Value * RadToDeg),
                InnerAngleDeg = Sanitize.Finite(light.Template.InnerAngle.Value * RadToDeg),
            });
        }

        return result;
    }

    /// <summary>The light part's actuate/deploy animation, or null when it has none (same subtree
    /// scan <c>SolarPanel.OnPartCreated</c> uses to bind a panel's deploy animation).</summary>
    private static KeyframeAnimationModule? LightAnimation(LightModule light)
    {
        var span = light.Parent.FullPart.SubtreeModules.Get<KeyframeAnimationModule>();
        return span.Length > 0 ? span[0] : null;
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
        SourceFile = "KSA/Decoupler.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium)]
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

    // ---- animations (M9) ------------------------------------------------------------------

    [KsaAnchor("vehicle.Parts.Modules.Get<KeyframeAnimationModule>(); .TimeGoal, .Shared.Duration, State.{TimeCurrent,DeploymentState}",
        SourceFile = "KSA/KeyframeAnimationModule.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "Deploy fraction = time/Duration. Solar flagged via Parent.SubtreeModules.HasAny<SolarPanel>(). "
                + "State read via ModuleStateful.TryGetFrom(vehicle.Parts.States, …); falls back to goal-derived.")]
    private static List<AnimationSnapshot> SampleAnimations(Vehicle vehicle)
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
                deploymentState = state.DeploymentState.ToString();
            }
            else
            {
                deploymentState = KeyframeAnimationModule
                    .DeriveDeploymentState(module.TimeGoal, module.TimeGoal).ToString();
            }

            var isSolar = module.Parent.SubtreeModules.HasAny<SolarPanel>();
            result.Add(new AnimationSnapshot(i, Sanitize.Finite(goalFraction),
                Sanitize.Finite(currentFraction), deploymentState, isSolar));
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
