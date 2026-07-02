using System.Text.Json;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests;

/// <summary>
///     The shared JSON projection layer (<see cref="SimJson"/>) — the single source of truth the
///     HTTP and MQTT transports both serve, so that reads stay at parity across transports. These
///     pin the composite projections (time/status/event) and confirm the full record dumps carry
///     the granular vessel/world data (the "full" shape that complements the compact telemetry doc).
/// </summary>
[TestFixture]
public sealed class SimJsonTests
{
    [Test]
    public void Time_CarriesWarpSpeedsAndAutoWarp()
    {
        var snapshot = TestData.Snapshot(7, TestData.Vessel()) with
        {
            SimDtSeconds = 0.02, WarpSpeeds = [1, 10, 100], AutoWarpActive = true, AutoWarpTargetUt = 999,
        };
        using var json = JsonDocument.Parse(SimJson.Time(snapshot));
        var root = json.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("ut").GetDouble(), Is.EqualTo(0.7).Within(1e-12));
            Assert.That(root.GetProperty("warp").GetDouble(), Is.EqualTo(1));
            Assert.That(root.GetProperty("sim_dt").GetDouble(), Is.EqualTo(0.02));
            Assert.That(root.GetProperty("warp_speeds").GetArrayLength(), Is.EqualTo(3));
            Assert.That(root.GetProperty("auto_warp_active").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("auto_warp_target_ut").GetDouble(), Is.EqualTo(999));
        });
    }

    [Test]
    public void Status_CarriesAccessorsControlDebugTransports()
    {
        var snapshot = TestData.Snapshot(1) with
        {
            Accessors = [new AccessorHealthSnapshot("reader.vessel.orbit", 5, "boom")],
        };
        using var json = JsonDocument.Parse(SimJson.Status(snapshot, control: true, debug: false, transports: "9p 1"));
        var root = json.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("game_version").GetString(), Is.EqualTo("test-version"));
            Assert.That(root.GetProperty("sample_rate_hz").GetDouble(), Is.EqualTo(10));
            Assert.That(root.GetProperty("control").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("debug").GetBoolean(), Is.False);
            Assert.That(root.GetProperty("transports").GetString(), Is.EqualTo("9p 1"));
            Assert.That(root.GetProperty("accessors")[0].GetProperty("name").GetString(),
                Is.EqualTo("reader.vessel.orbit"));
        });
    }

    [Test]
    public void Event_CarriesVesselWhenPresent_AndOmitsWhenGlobal()
    {
        using var withVessel = JsonDocument.Parse(
            SimJson.Event(new SimEvent(12.5, "situation-change", "v1", "Landed→Freefall")));
        Assert.Multiple(() =>
        {
            Assert.That(withVessel.RootElement.GetProperty("type").GetString(), Is.EqualTo("situation-change"));
            Assert.That(withVessel.RootElement.GetProperty("vessel").GetString(), Is.EqualTo("v1"));
            Assert.That(withVessel.RootElement.GetProperty("detail").GetString(), Is.EqualTo("Landed→Freefall"),
                "relaxed escaping keeps the arrow literal");
        });

        using var global = JsonDocument.Parse(SimJson.Event(new SimEvent(1, "warp-changed", null, "1→100")));
        Assert.That(global.RootElement.TryGetProperty("vessel", out _), Is.False);
    }

    [Test]
    public void Vessel_FullDump_CarriesGranularModules()
    {
        // The "full" shape: raw-record snake_case names + every per-module collection, the data the
        // compact telemetry doc deliberately omits. This is what gives MQTT/HTTP parity with the
        // individual /sim scalar files.
        using var json = JsonDocument.Parse(SimJson.Serialize(TestData.FullVessel() with { Scale = 2.5 }));
        var root = json.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("id").GetString(), Is.EqualTo("test-1"));
            Assert.That(root.GetProperty("barometric_altitude").GetDouble(), Is.EqualTo(71000));
            Assert.That(root.GetProperty("scale").GetDouble(), Is.EqualTo(2.5),
                "the vessel model scale factor rides the full record dump");
            Assert.That(root.GetProperty("engines").GetArrayLength(), Is.GreaterThan(0));
            Assert.That(root.GetProperty("rcs").GetArrayLength(), Is.GreaterThan(0));
            Assert.That(root.GetProperty("navball").GetProperty("frame").GetString(), Is.EqualTo("Lvlh"));
            Assert.That(root.GetProperty("environment").GetProperty("pressure_pa").GetDouble(),
                Is.EqualTo(101325));
        });
    }

    [Test]
    public void Snapshot_FullDump_CarriesVesselsBodiesAndSystem()
    {
        var snapshot = TestData.Snapshot(3, TestData.FullVessel()).WithCelestials();
        using var json = JsonDocument.Parse(SimJson.Serialize(snapshot));
        var root = json.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("ut_seconds").GetDouble(), Is.EqualTo(0.3).Within(1e-12));
            Assert.That(root.GetProperty("vessels")[0].GetProperty("id").GetString(), Is.EqualTo("test-1"));
            Assert.That(root.GetProperty("bodies")[0].GetProperty("id").GetString(), Is.EqualTo("Kerth"));
            Assert.That(root.GetProperty("system").GetProperty("name").GetString(), Is.EqualTo("Kerbol"));
        });
    }

    [Test]
    public void Bodies_AndSystem_SerializeStandalone()
    {
        var snapshot = TestData.Snapshot(1).WithCelestials();
        using var bodies = JsonDocument.Parse(SimJson.Serialize(snapshot.Bodies));
        Assert.That(bodies.RootElement[0].GetProperty("id").GetString(), Is.EqualTo("Kerth"));

        using var system = JsonDocument.Parse(SimJson.Serialize(snapshot.System));
        Assert.That(system.RootElement.GetProperty("home_body_id").GetString(), Is.EqualTo("Kerth"));
    }
}
