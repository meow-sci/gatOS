using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Telemetry;

namespace gatOS.SimFs.Tests.Telemetry;

/// <summary>OS_PLAN.md T9.2: fixture-pair diffs for every event type.</summary>
[TestFixture]
public sealed class EventDifferTests
{
    private static SimSnapshot Previous(params VesselSnapshot[] vessels)
        => TestData.Snapshot(1, vessels);

    [Test]
    public void FirstSample_IsTheBaseline_NoEvents()
    {
        var events = EventDiffer.Diff(null, 1, 1, "test-1", [TestData.Vessel()]);
        Assert.That(events, Is.Empty);
    }

    [Test]
    public void NoChanges_NoEvents()
    {
        var vessel = TestData.Vessel();
        var previous = Previous(vessel);
        var events = EventDiffer.Diff(previous, 2, previous.WarpFactor, previous.ActiveVesselId, [vessel]);
        Assert.That(events, Is.Empty);
    }

    [Test]
    public void SituationChange_PerVessel()
    {
        var previous = Previous(TestData.Vessel(situation: "Landed"), TestData.Vessel("other"));
        var events = EventDiffer.Diff(previous, 2, 1, "test-1",
            [TestData.Vessel(situation: "Freefall"), TestData.Vessel("other")]);

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(events[0].Type, Is.EqualTo("situation-change"));
            Assert.That(events[0].VesselId, Is.EqualTo("test-1"));
            Assert.That(events[0].Detail, Is.EqualTo("Landed→Freefall"));
            Assert.That(events[0].UtSeconds, Is.EqualTo(2));
        });
    }

    [Test]
    public void VesselAppearedAndRemoved()
    {
        var previous = Previous(TestData.Vessel("old"));
        var events = EventDiffer.Diff(previous, 2, 1, "old", [TestData.Vessel("new")]);

        Assert.That(events, Has.Count.EqualTo(2));
        var appeared = events.Single(e => e.Type == "vessel-appeared");
        var removed = events.Single(e => e.Type == "vessel-removed");
        Assert.Multiple(() =>
        {
            Assert.That(appeared.VesselId, Is.EqualTo("new"));
            Assert.That(appeared.Detail, Is.EqualTo("Vessel new"));
            Assert.That(removed.VesselId, Is.EqualTo("old"));
            Assert.That(removed.Detail, Is.EqualTo("Vessel old"));
        });
    }

    [Test]
    public void ActiveChanged_IncludingToAndFromNone()
    {
        var vessel = TestData.Vessel();
        var previous = Previous(vessel); // active = "test-1"

        var toNone = EventDiffer.Diff(previous, 2, 1, null, [vessel]);
        Assert.That(toNone.Single().Detail, Is.EqualTo("test-1→none"));

        var fromNone = EventDiffer.Diff(previous with { ActiveVesselId = null }, 2, 1, "test-1", [vessel]);
        Assert.Multiple(() =>
        {
            Assert.That(fromNone.Single().Type, Is.EqualTo("active-changed"));
            Assert.That(fromNone.Single().Detail, Is.EqualTo("none→test-1"));
            Assert.That(fromNone.Single().VesselId, Is.EqualTo("test-1"));
        });
    }

    [Test]
    public void WarpChanged_FormatsBothValues()
    {
        var vessel = TestData.Vessel();
        var previous = Previous(vessel);
        var events = EventDiffer.Diff(previous, 2, 100, "test-1", [vessel]);
        Assert.Multiple(() =>
        {
            Assert.That(events.Single().Type, Is.EqualTo("warp-changed"));
            Assert.That(events.Single().VesselId, Is.Null, "warp is global");
            Assert.That(events.Single().Detail, Is.EqualTo("1→100"));
        });
    }

    [Test]
    public void SoiChanged_WhenParentBodyDiffers()
    {
        var previous = Previous(TestData.Vessel()); // parent "Kerth"
        var moved = TestData.Vessel() with { ParentBodyName = "Mun" };
        var events = EventDiffer.Diff(previous, 2, 1, "test-1", [moved]);
        Assert.Multiple(() =>
        {
            Assert.That(events.Single().Type, Is.EqualTo("soi-changed"));
            Assert.That(events.Single().Detail, Is.EqualTo("Kerth→Mun"));
        });
    }

    [Test]
    public void EngineStateAndFlameout_AreDetected()
    {
        var before = TestData.Vessel() with
        {
            Engines = [new EngineSnapshot(0, false, 1, 1) { PropellantAvailable = true }],
        };
        var fired = TestData.Vessel() with
        {
            Engines = [new EngineSnapshot(0, true, 1, 1) { PropellantAvailable = true }],
        };
        var starved = TestData.Vessel() with
        {
            Engines = [new EngineSnapshot(0, true, 1, 1) { PropellantAvailable = false }],
        };

        var onEvents = EventDiffer.Diff(Previous(before), 2, 1, "test-1", [fired]);
        Assert.That(onEvents.Single(e => e.Type == "engine-state").Detail, Is.EqualTo("engine 0 on"));

        var flameout = EventDiffer.Diff(Previous(fired), 3, 1, "test-1", [starved]);
        Assert.That(flameout.Any(e => e.Type == "flameout"), Is.True);
    }

    [Test]
    public void DockingDecouplerAndAnimation_Edges()
    {
        var before = TestData.Vessel() with
        {
            Docking = [new DockingSnapshot(0, false, null)],
            Decouplers = [new DecouplerSnapshot(0, false)],
            Animations = [new AnimationSnapshot(0, 1, 1, "Deploying", IsSolar: false)],
        };
        var after = TestData.Vessel() with
        {
            Docking = [new DockingSnapshot(0, true, "node-7")],
            Decouplers = [new DecouplerSnapshot(0, true)],
            Animations = [new AnimationSnapshot(0, 1, 1, "Deployed", IsSolar: false)],
        };

        var events = EventDiffer.Diff(Previous(before), 2, 1, "test-1", [after]);
        Assert.Multiple(() =>
        {
            Assert.That(events.Single(e => e.Type == "docked").Detail, Is.EqualTo("node-7"));
            Assert.That(events.Any(e => e.Type == "decoupled"), Is.True);
            Assert.That(events.Single(e => e.Type == "animation-complete").Detail,
                Is.EqualTo("animation 0 Deployed"));
        });
    }

    [Test]
    public void BatteryDepletionAndCharge_Edges()
    {
        var full = TestData.Vessel(battery: 1.0);
        var empty = TestData.Vessel(battery: 0.0);
        Assert.That(EventDiffer.Diff(Previous(full), 2, 1, "test-1", [empty])
            .Single(e => e.Type == "battery-depleted"), Is.Not.Null);
        Assert.That(EventDiffer.Diff(Previous(empty), 3, 1, "test-1", [full])
            .Single(e => e.Type == "battery-charged"), Is.Not.Null);
    }

    [Test]
    public void MultipleSimultaneousChanges_AllReported()
    {
        var previous = Previous(TestData.Vessel(situation: "Landed"));
        var events = EventDiffer.Diff(previous, 2, 50, null,
            [TestData.Vessel(situation: "Freefall"), TestData.Vessel("newcomer")]);

        Assert.That(events.Select(e => e.Type), Is.EquivalentTo(new[]
        {
            "warp-changed", "active-changed", "situation-change", "vessel-appeared",
        }));
    }
}
