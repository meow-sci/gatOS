using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests;

/// <summary>Fixture snapshots for the M8 suites.</summary>
internal static class TestData
{
    internal static VesselSnapshot Vessel(string id = "test-1", string situation = "Freefall",
        double radarAltitude = 70950.5, OrbitSnapshot? orbit = null, bool withOrbit = true,
        double? battery = 0.87, bool lightsOn = false, bool engineOn = false,
        IReadOnlyList<AnimationSnapshot>? animations = null,
        IReadOnlyList<SolarSnapshot>? solar = null, IReadOnlyList<DecouplerSnapshot>? decouplers = null,
        IReadOnlyList<RcsSnapshot>? rcs = null, IReadOnlyList<LightSnapshot>? lights = null,
        IReadOnlyList<DockingSnapshot>? docking = null)
        => new VesselSnapshot(
            Id: id,
            Name: $"Vessel {id}",
            Situation: situation,
            PositionCci: new double3Snap(1234.5, -6789.25, 42),
            LatitudeDeg: 45.5,
            LongitudeDeg: -122.25,
            OrbitalSpeed: 7670.5,
            SurfaceSpeed: 7400.25,
            InertialSpeed: 7672,
            AttitudeBody2Cci: new QuatSnap(0, 0.7071, 0, 0.7071),
            BodyRatesRadS: new double3Snap(0.1, -0.2, 0.3),
            BarometricAltitude: 71000,
            RadarAltitude: radarAltitude,
            MassTotal: 12000,
            MassDry: 4000,
            MassPropellant: 8000,
            Orbit: withOrbit ? orbit ?? new OrbitSnapshot(250000, 240000, 0.0012, 51.6, 6620000, 5400) : null,
            Engines: [new EngineSnapshot(0, true, 250000, 312), new EngineSnapshot(1, false, 50000, 340)],
            Tanks: [new TankSnapshot("methalox", 7800, 9000), new TankSnapshot("monoprop", 200, 250)],
            BatteryChargeFraction: battery,
            ParentBodyName: "Kerth",
            LightsMasterOn: lightsOn,
            Animations: animations ?? [])
        {
            Solar = solar ?? [],
            Decouplers = decouplers ?? [],
            Rcs = rcs ?? [],
            Lights = lights ?? [],
            Docking = docking ?? [],
            EngineOn = engineOn,
        };

    /// <summary>
    ///     A vessel with every optional module collection populated — solar (with a deploy
    ///     animation + tracker), rcs, lights, generators, docking, decouplers, a non-solar
    ///     animation, navball and environment — so a tree crawl exercises every per-module read
    ///     dir (KSA_GAME_INTEGRATION_PLAN §4.5/§4.6) instead of the feature-empty default.
    /// </summary>
    internal static VesselSnapshot FullVessel(string id = "test-1")
        => Vessel(id,
                animations:
                [
                    new AnimationSnapshot(0, 0.5, 0.4, "Deploying", IsSolar: true),
                    new AnimationSnapshot(1, 0, 0, "Retracted", IsSolar: false),
                ],
                solar: [new SolarSnapshot(0, 120, false, 12, 0.95, HasTracker: true, 30, AnimationIndex: 0)],
                decouplers: [new DecouplerSnapshot(0, false)],
                rcs: [new RcsSnapshot(0, true, true, "Pitch|Yaw")],
                // The light carries an actuate animation linked to vessel-level ordinal 1 (the non-solar
                // animation above), so its co-located goal/current/state control surfaces.
                lights: [new LightSnapshot(0, true, 1.0, new double3Snap(1, 1, 1), AnimationIndex: 1) { SpreadDeg = 45 }])
            with
            {
                Navball = new NavballSnapshot(10, 20, 30, 1.5, 3400, "Lvlh", 7670),
                Environment = new EnvironmentSnapshot(101325, 1.2, 500, 0, 6_371_000,
                    new double3Snap(0, 0, 9.8), new double3Snap(0, 0, 0), 1.0),
                Generators = [new GeneratorSnapshot(0, true, 50)],
                Docking = [new DockingSnapshot(0, true, "part-7") { PushoffForceN = 7000 }],
                Encounters = [new EncounterSnapshot("Mun", 5000, 120000)],
                BatteryCapacityJoules = 9000,
            };

    /// <summary>A body catalog + system summary, for crawling <c>/sim/bodies</c> and <c>/sim/system</c>.</summary>
    internal static SimSnapshot WithCelestials(this SimSnapshot snapshot)
        => snapshot with
        {
            System = new SystemSnapshot("Kerbol", "Kerth", "Kerbol"),
            Bodies =
            [
                new BodySnapshot("Kerth", "Planet", "Kerbol", ["Mun"], 5.29e22, 600000, 3.5e12, 8.4e7,
                    7.3e-5, new double3Snap(0, 0, 0), new double3Snap(0, 0, 0),
                    new OrbitSnapshot(13_600_000, 13_600_000, 0, 0, 13_600_000, 9_200_000),
                    new AtmosphereSnapshot(70000, 5600, 101325, 1.2),
                    new OceanSnapshot(1000)),
            ],
        };

    internal static SimSnapshot Snapshot(long sequence, params VesselSnapshot[] vessels)
        => new(sequence, sequence * 0.1, 1, vessels.Length > 0 ? vessels[0].Id : null, vessels, [],
            "test-version", 10, []);

    internal static SimSnapshot WithEvents(this SimSnapshot snapshot, params SimEvent[] events)
        => snapshot with { NewEvents = events };

    internal static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting: {what}");
            await Task.Delay(10);
        }
    }
}
