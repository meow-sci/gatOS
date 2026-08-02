using System.Globalization;
using gatOS.SimFs.Fx;
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
        IReadOnlyList<DockingSnapshot>? docking = null, double scale = 1.0, bool alwaysRender = false)
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
            Scale = scale,
            AlwaysRender = alwaysRender,
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
                lights: [new LightSnapshot(0, true, 1.0, new double3Snap(1, 1, 1), AnimationIndex: 1) { OuterAngleDeg = 45, InnerAngleDeg = 22.5 }])
            with
            {
                Navball = new NavballSnapshot(10, 20, 30, 1.5, 3400, "Lvlh", 7670),
                Environment = new EnvironmentSnapshot(101325, 1.2, 500, 0, 6_371_000,
                    new double3Snap(0, 0, 9.8), new double3Snap(0, 0, 0), 1.0),
                Generators = [new GeneratorSnapshot(0, true, 50)],
                Docking = [new DockingSnapshot(0, true, "part-7") { PushoffImpulseNs = 7000 }],
                Encounters = [new EncounterSnapshot("Mun", 5000, 120000)],
                Srb = [Srb()],
                BatteryCapacityJoules = 9000,
            };

    /// <summary>
    ///     One solid rocket motor with a two-segment grain stack — half-burned, so the mass /
    ///     fraction / burn-time reads are all non-trivial and the unburnable sliver is visible.
    /// </summary>
    internal static SrbSnapshot Srb(int index = 0)
        => new(index, Active: true, MassKg: 21000, MassInitialKg: 40000)
        {
            EngineIndex = 1,
            PartInstanceId = 4242,
            Substance = "apcp",
            Grain = "star",
            GrainShape = "Star",
            StackValid = true,
            PropellantAvailable = true,
            MassUnburnableKg = 1000,
            MassBurnableKg = 20000,
            Fraction = 20000.0 / 39000.0,
            MassFlowKgS = 950,
            BurnTimeRemainingS = 21.05,
            ChamberPressurePa = 6.2e6,
            ChamberTemperatureK = 3100,
            ExitPressurePa = 101325,
            ExitTemperatureK = 1500,
            BurningAreaM2 = 18.4,
            AreaRatio = 7.5,
            Segments =
            [
                new SrbSegmentSnapshot(0, 11000, 20000)
                {
                    PartInstanceId = 4243, Substance = "apcp", Grain = "star",
                    MassUnburnableKg = 500, Fraction = 10500.0 / 19500.0,
                    RadiusM = 0.8, LengthM = 6, VolumeM3 = 11.3, BurnDepthM = 0.22,
                },
                new SrbSegmentSnapshot(1, 10000, 20000)
                {
                    PartInstanceId = 4244, Substance = "apcp", Grain = "star",
                    MassUnburnableKg = 500, Fraction = 9500.0 / 19500.0,
                    RadiusM = 0.8, LengthM = 6, VolumeM3 = 11.3, BurnDepthM = 0.24,
                },
            ],
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

    /// <summary>
    ///     A fully populated FX-editor surface for crawling <c>/sim/debug/{engineplume,plumetrail,
    ///     clouds,terrain}</c>: two plume templates, the trail singleton, one cloud body with
    ///     2 layers × 2 cloud types, one terrain body, and the terrain global toggle. Field values
    ///     are derived from the catalog (see <see cref="FxValue"/>) so a fixture never drifts from
    ///     the tables.
    /// </summary>
    internal static FxEditorsSnapshot FxEditors()
        => new()
        {
            PlumeTemplates =
            [
                FxEntity("kerolox", FxCatalog.EnginePlume),
                FxEntity("methalox", FxCatalog.EnginePlume),
            ],
            Trail = FxEntity("", FxCatalog.PlumeTrail),
            CloudBodies = [FxEntity("Kerth", FxCatalog.Clouds, 2, 2)],
            TerrainBodies = [FxEntity("Kerth", FxCatalog.Terrain.Where(s => s.Key != "wireframe"))],
            TerrainGlobal = FxEntity("", FxCatalog.Terrain.Where(s => s.Key == "wireframe")),
        };

    /// <summary>Attaches <see cref="FxEditors"/> to a snapshot.</summary>
    internal static SimSnapshot WithFxEditors(this SimSnapshot snapshot)
        => snapshot with { FxEditors = FxEditors() };

    /// <summary>
    ///     Builds one FX entity from a family table: every non-indexed key once, every <c>*</c>
    ///     segment expanded over <paramref name="dims"/> (outermost wildcard first).
    /// </summary>
    internal static FxEntitySnapshot FxEntity(string id, IEnumerable<FxFieldSpec> specs, params int[] dims)
    {
        var fields = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var spec in specs)
            foreach (var key in ExpandFxKey(spec.Key, dims))
                fields[key] = FxValue(spec);
        return new FxEntitySnapshot(id, fields);
    }

    /// <summary>
    ///     The fixture value for a field: a flag is <c>1</c>; anything else is
    ///     <c>0.5, 0.75, 1.0…</c> per component, clamped into the spec's range (so it is always
    ///     valid and always distinguishable per component).
    /// </summary>
    internal static double[] FxValue(FxFieldSpec spec)
    {
        var values = new double[spec.Arity];
        for (var i = 0; i < values.Length; i++)
            values[i] = spec.Kind == FxKind.Flag ? 1 : Math.Clamp(0.5 + (0.25 * i), spec.Min, spec.Max);
        return values;
    }

    private static List<string> ExpandFxKey(string key, int[] dims)
    {
        var current = new List<string> { key };
        for (var depth = 0; current.Count > 0 && current[0].Contains('*'); depth++)
        {
            var count = depth < dims.Length ? dims[depth] : 0;
            var next = new List<string>(current.Count * count);
            foreach (var pattern in current)
            {
                var star = pattern.IndexOf('*');
                for (var i = 0; i < count; i++)
                    next.Add(string.Concat(pattern.AsSpan(0, star),
                        i.ToString(CultureInfo.InvariantCulture), pattern.AsSpan(star + 1)));
            }

            current = next;
        }

        return current;
    }

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
