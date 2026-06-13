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
internal static class VesselReader
{
    private const double StandardGravity = 9.80665;
    private const double RadToDeg = 180.0 / Math.PI;

    [KsaAnchor("Vehicle core state (Id/Situation/GetPositionCci/OrbitalSpeed/masses/attitude)",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "Verified at M9; Name = Id (KSA has no separate display name).")]
    internal static VesselSnapshot Sample(Vehicle vehicle)
    {
        var parent = vehicle.Parent; // IParentBody (=> Orbit.Parent)
        var positionCci = vehicle.GetPositionCci();

        // Lat/lon via the body's own frame math: CCI → CCF, then IParentBody.GetLlaFromCcf.
        double latitudeDeg = 0, longitudeDeg = 0;
        if (parent is not null)
        {
            // The static Transform avoids the extension-method overload set (which would drag
            // BepuUtilities into overload resolution — CS0012 without that reference).
            var positionCcf = Brutal.Numerics.double3.Transform(positionCci, parent.GetCci2Ccf());
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
            BatteryChargeFraction: SampleBattery(vehicle),
            ParentBodyName: parent?.Id,
            LightsMasterOn: vehicle.LightsOn,
            Animations: SampleAnimations(vehicle));
    }

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
                Sanitize.Finite(vacThrust), Sanitize.Finite(isp)));
        }

        return engines;
    }

    [KsaAnchor("vehicle.Parts.Modules.Get<Tank>().Moles; vehicle.Parts.Moles.GetState(mole).Mass",
        SourceFile = "KSA/Tank.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "A Tank holds one Mole per substance; amounts live in the SoA Moles state list.")]
    private static List<TankSnapshot> SampleTanks(Vehicle vehicle)
    {
        var tanks = new List<TankSnapshot>();
        var moleStates = vehicle.Parts.Moles;
        foreach (var tank in vehicle.Parts.Modules.Get<Tank>())
        {
            foreach (var mole in tank.Moles)
            {
                double amount = moleStates.GetState(mole).Mass;
                double capacity = mole.GetLiquidMass(mole.ContainerVolume);
                tanks.Add(new TankSnapshot(mole.SubstancePhase.Name,
                    Sanitize.Finite(amount), Sanitize.Finite(capacity)));
            }
        }

        return tanks;
    }

    [KsaAnchor("vehicle.Parts.Batteries.GetState(b).Charge, b.MaximumCapacity",
        SourceFile = "KSA/Battery.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "Charge fraction summed across all batteries; null when the vehicle has none.")]
    private static double? SampleBattery(Vehicle vehicle)
    {
        var batteries = vehicle.Parts.Batteries;
        double charge = 0, capacity = 0;
        foreach (var battery in batteries.Modules)
        {
            charge += batteries.GetState(battery).Charge.Value();
            capacity += battery.MaximumCapacity.Value();
        }

        return capacity > 0 ? Math.Clamp(Sanitize.Finite(charge / capacity), 0, 1) : null;
    }

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

        var haveStates = ModuleStateful<KeyframeAnimationModule, KeyframeAnimationModule.State, EmptyStruct, EmptyStruct>
            .TryGetFrom(vehicle.Parts.States, out var states);

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

    private static double3Snap Vec(Brutal.Numerics.double3 v)
        => new(Sanitize.Finite(v.X), Sanitize.Finite(v.Y), Sanitize.Finite(v.Z));
}
