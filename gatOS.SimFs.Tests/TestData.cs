using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests;

/// <summary>Fixture snapshots for the M8 suites.</summary>
internal static class TestData
{
    internal static VesselSnapshot Vessel(string id = "test-1", string situation = "Freefall",
        double radarAltitude = 70950.5, OrbitSnapshot? orbit = null, bool withOrbit = true,
        double? battery = 0.87)
        => new(
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
            ParentBodyName: "Kerth");

    internal static SimSnapshot Snapshot(long sequence, params VesselSnapshot[] vessels)
        => new(sequence, sequence * 0.1, 1, vessels.Length > 0 ? vessels[0].Id : null, vessels, []);

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
