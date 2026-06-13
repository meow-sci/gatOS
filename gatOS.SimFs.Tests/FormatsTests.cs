using System.Text;
using System.Text.Json;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests;

/// <summary>The documented `/sim` value formats (OS_PLAN.md T8.2/T8.3) — a frozen API surface.</summary>
[TestFixture]
public sealed class FormatsTests
{
    [Test]
    public void Scalar_IsG9Invariant()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Formats.Scalar(0), Is.EqualTo("0"));
            Assert.That(Formats.Scalar(-122.25), Is.EqualTo("-122.25"));
            Assert.That(Formats.Scalar(1234567.891), Is.EqualTo("1234567.89"), "9 significant digits");
            Assert.That(Formats.Scalar(1e21), Is.EqualTo("1E+21"));
            Assert.That(Formats.Scalar(0.5), Is.EqualTo("0.5"), "invariant decimal separator");
        });
    }

    [Test]
    public void EncounterLine_IsCompactJson()
    {
        var line = Formats.EncounterLine(new EncounterSnapshot("Mun", 1234.5, 67000));
        using var json = JsonDocument.Parse(line);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("body").GetString(), Is.EqualTo("Mun"));
            Assert.That(json.RootElement.GetProperty("ut").GetDouble(), Is.EqualTo(1234.5));
            Assert.That(json.RootElement.GetProperty("distance").GetDouble(), Is.EqualTo(67000));
            Assert.That(line, Does.Not.EndWith("\n"), "the file joins lines with its own LF");
        });
    }

    [Test]
    public void VesselTelemetry_IsOneAtomicJsonObject()
    {
        var vessel = TestData.Vessel() with { Controlled = true };
        var snapshot = TestData.Snapshot(7, vessel);
        var doc = Formats.VesselTelemetry(snapshot, vessel);
        using var json = JsonDocument.Parse(doc);
        var root = json.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("seq").GetInt64(), Is.EqualTo(7));
            Assert.That(root.GetProperty("id").GetString(), Is.EqualTo("test-1"));
            Assert.That(root.GetProperty("controlled").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("mass").GetProperty("t").GetDouble(), Is.EqualTo(12000));
            Assert.That(root.GetProperty("orbit").GetProperty("ap").GetDouble(), Is.EqualTo(250000));
            Assert.That(root.TryGetProperty("vel", out _), Is.True);
        });
    }

    [Test]
    public void Flag_IsZeroOrOne()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Formats.Flag(true), Is.EqualTo("1"));
            Assert.That(Formats.Flag(false), Is.EqualTo("0"));
        });
    }

    [Test]
    public void VectorAndQuat_AreSpaceSeparatedComponents()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Formats.Vector(new double3Snap(1, -2.5, 3)), Is.EqualTo("1 -2.5 3"));
            Assert.That(Formats.Quat(new QuatSnap(0, 0.25, 0, 1)), Is.EqualTo("0 0.25 0 1"));
        });
    }

    [Test]
    public void StreamLine_IsSingleLineJson_WithThePlannedShape()
    {
        var vessel = TestData.Vessel();
        var snapshot = TestData.Snapshot(7, vessel);
        var line = Encoding.UTF8.GetString(Formats.StreamLine(snapshot, vessel));

        Assert.That(line, Does.EndWith("\n"));
        Assert.That(line.TrimEnd('\n'), Does.Not.Contain('\n'), "exactly one line");

        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("seq").GetInt64(), Is.EqualTo(7));
            Assert.That(root.GetProperty("ut").GetDouble(), Is.EqualTo(0.7).Within(1e-12));
            Assert.That(root.GetProperty("sit").GetString(), Is.EqualTo("Freefall"));
            Assert.That(root.GetProperty("alt").GetProperty("radar").GetDouble(), Is.EqualTo(70950.5));
            Assert.That(root.GetProperty("alt").GetProperty("baro").GetDouble(), Is.EqualTo(71000));
            Assert.That(root.GetProperty("vel").GetProperty("surf").GetDouble(), Is.EqualTo(7400.25));
            Assert.That(root.GetProperty("att").GetProperty("q").GetArrayLength(), Is.EqualTo(4));
            Assert.That(root.GetProperty("att").GetProperty("rates").GetArrayLength(), Is.EqualTo(3));
            Assert.That(root.GetProperty("mass").GetProperty("p").GetDouble(), Is.EqualTo(8000));
        });
    }

    [Test]
    public void EventLine_CarriesVesselWhenPresent_AndRelaxedEscaping()
    {
        var line = Encoding.UTF8.GetString(
            Formats.EventLine(new SimEvent(12.5, "situation-change", "test-1", "Landed→Freefall")));
        Assert.That(line, Does.Contain("Landed→Freefall"), "relaxed escaping keeps the arrow literal");

        using var json = JsonDocument.Parse(line);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("type").GetString(), Is.EqualTo("situation-change"));
            Assert.That(json.RootElement.GetProperty("vessel").GetString(), Is.EqualTo("test-1"));
            Assert.That(json.RootElement.GetProperty("detail").GetString(), Is.EqualTo("Landed→Freefall"));
        });
    }

    [Test]
    public void EventLine_OmitsVesselWhenGlobal()
    {
        var line = Encoding.UTF8.GetString(
            Formats.EventLine(new SimEvent(1, "warp-changed", null, "1→100")));
        using var json = JsonDocument.Parse(line);
        Assert.That(json.RootElement.TryGetProperty("vessel", out _), Is.False);
    }
}
