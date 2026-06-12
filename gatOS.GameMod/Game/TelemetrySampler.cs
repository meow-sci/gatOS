using gatOS.Logging;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Telemetry;
using KSA;

namespace gatOS.GameMod.Game;

/// <summary>
///     The game-thread telemetry sampler (OS_PLAN.md T9.1): rate-limited by frame dt, reads
///     the verified KSA accessors (every one cross-checked against the decompiled sources —
///     see the T9.1 as-built note), builds an immutable <see cref="SimSnapshot"/> and
///     publishes it with one volatile swap (threading rule 1). 9p server threads only ever
///     see published snapshots (rule 2).
/// </summary>
/// <remarks>
///     Every vehicle is sampled inside its own try/catch (a mid-teardown vehicle throwing
///     must not kill the loop — ksa skill gotcha), and every double is scrubbed through
///     <see cref="Sanitize"/> (orbital fields are NaN/Inf as a matter of course). This file
///     compiles only when the KSA reference assemblies are present (csproj Game/** gate).
/// </remarks>
internal sealed class TelemetrySampler
{
    private const double StandardGravity = 9.80665;
    private const double RadToDeg = 180.0 / Math.PI;

    private readonly SnapshotStore _store;
    private readonly SampleClock _clock;
    private SimSnapshot? _previous;
    private long _sequence;
    private bool _vehicleErrorLogged;

    /// <param name="store">The exchange the 9p tree reads from.</param>
    /// <param name="rateHz">Config <c>sample_rate_hz</c> (already clamped to 1–120).</param>
    internal TelemetrySampler(SnapshotStore store, double rateHz)
    {
        _store = store;
        _clock = new SampleClock(rateHz);
    }

    /// <summary>
    ///     Per-frame tick, game thread only. <paramref name="active"/> gates the work: while
    ///     the VM is down and no 9p session exists there is nobody to read `/sim`, so the
    ///     sampler idles for free (T9.1).
    /// </summary>
    internal void Tick(double dt, bool active)
    {
        if (!active)
        {
            _clock.Reset();
            return;
        }

        if (_clock.Tick(dt))
            Sample();
    }

    private void Sample()
    {
        var ut = Sanitize.Finite(Universe.GetElapsedSimTime().Seconds());
        var warp = Sanitize.Finite(Universe.SimulationSpeed);
        var activeId = Program.ControlledVehicle?.Id;

        var vessels = new List<VesselSnapshot>();
        if (Universe.CurrentSystem is { } system)
        {
            foreach (var astronomical in system.All.UnsafeAsList())
            {
                if (astronomical is not Vehicle vehicle)
                    continue;
                try
                {
                    vessels.Add(SampleVehicle(vehicle));
                }
                catch (Exception ex)
                {
                    // One vehicle mid-teardown must not kill the snapshot; log the first only.
                    if (!_vehicleErrorLogged)
                    {
                        _vehicleErrorLogged = true;
                        ModLog.Log.Debug($"telemetry: a vehicle sample failed (logged once): {ex.Message}");
                    }
                }
            }
        }

        var events = EventDiffer.Diff(_previous, ut, warp, activeId, vessels);
        var snapshot = new SimSnapshot(++_sequence, ut, warp, activeId, vessels, events);
        _previous = snapshot;
        _store.Publish(snapshot);
    }

    private static VesselSnapshot SampleVehicle(Vehicle vehicle)
    {
        var parent = vehicle.Parent; // IParentBody (=> Orbit.Parent)
        var positionCci = vehicle.GetPositionCci();

        // Lat/lon via the body's own frame math: CCI → CCF, then IParentBody.GetLlaFromCcf
        // (returns lat°, lon°, altitude — the exact inverse of GetDirCcfFromLatLon).
        double latitudeDeg = 0, longitudeDeg = 0;
        if (parent is not null)
        {
            // The static Transform avoids the extension-method overload set, which would pull
            // BepuUtilities types into overload resolution (CS0012 without that reference).
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
            ParentBodyName: parent?.Id);
    }

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

    private static List<TankSnapshot> SampleTanks(Vehicle vehicle)
    {
        // A Tank holds one Mole per stored substance; amounts live in the SoA state list.
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

    private static double3Snap Vec(Brutal.Numerics.double3 v)
        => new(Sanitize.Finite(v.X), Sanitize.Finite(v.Y), Sanitize.Finite(v.Z));
}
