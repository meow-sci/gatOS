using gatOS.SimFs.Commands;
using System.Reflection;

namespace gatOS.SimFs.Tests.Commands;

public sealed class StructuredCommandsTests
{
    [Test]
    public void Catalog_FindsAndValidatesTheSharedActionVocabulary()
    {
        Assert.That(CommandCatalog.TryGet(SimActions.VesselIgnite, out var descriptor), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Target, Is.EqualTo(CommandTargetKind.Vessel));
            Assert.That(descriptor.Phase, Is.EqualTo(CommandPhase.Frame));
            Assert.That(CommandCatalog.Validate(new SimCommand("hunter", SimActions.VesselIgnite,
                SimCommand.NoOrdinal, 1)).IsValid, Is.True);
            Assert.That(CommandCatalog.Validate(new SimCommand("hunter", "not.an.action",
                SimCommand.NoOrdinal, 1)).Error, Does.Contain("unknown action"));
            Assert.That(CommandCatalog.Validate(new SimCommand("hunter", SimActions.VesselThrottle,
                SimCommand.NoOrdinal, double.NaN)).Error, Does.Contain("finite"));
        });
    }

    [Test]
    public void Catalog_CoversEveryCanonicalActionWithAgentMetadata()
    {
        var constants = typeof(SimActions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var catalog = CommandCatalog.All.Select(entry => entry.Action).Order(StringComparer.Ordinal).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(catalog, Is.EqualTo(constants));
            Assert.That(CommandCatalog.All, Has.All.Property(nameof(CommandDescriptor.LogicalTool)).Not.Empty);
            Assert.That(CommandCatalog.All, Has.All.Property(nameof(CommandDescriptor.Gate)).Not.Empty);
            Assert.That(CommandCatalog.All, Has.All.Property(nameof(CommandDescriptor.ArgumentShape)).Not.Empty);
            Assert.That(CommandCatalog.All, Has.All.Property(nameof(CommandDescriptor.Units)).Not.Empty);
            Assert.That(CommandCatalog.All, Has.All.Property(nameof(CommandDescriptor.Safety)).Not.Empty);
        });
    }

    [Test]
    public void BatchBuilder_PreservesOrderAndRequiresOnePhase()
    {
        var ignite = new SimCommand("hunter", SimActions.VesselIgnite, SimCommand.NoOrdinal, 1);
        var throttle = new SimCommand("hunter", SimActions.VesselThrottle, SimCommand.NoOrdinal, 0.75);

        var built = CommandBatchBuilder.Build([ignite, throttle]);

        Assert.Multiple(() =>
        {
            Assert.That(built.IsValid, Is.True, built.Validation.Error);
            Assert.That(built.Batch!.Phase, Is.EqualTo(CommandPhase.Frame));
            Assert.That(built.Batch.Commands, Is.EqualTo(new[] { ignite, throttle }));
        });

        var mixed = CommandBatchBuilder.Build([ignite,
            new SimCommand("hunter", SimActions.DebugRefillFuel, SimCommand.NoOrdinal, 1)]);
        Assert.That(mixed.IsValid, Is.False);
        Assert.That(mixed.Validation.Error, Does.Contain("share one phase"));

        var tooMany = CommandBatchBuilder.Build(Enumerable.Repeat(throttle, 65).ToArray());
        Assert.That(tooMany.Validation.Error, Does.Contain("at most 64"));
    }

    [Test]
    public void ScheduleBuilder_UsesStoreLimitsDefaultsAndTypedCoalescingKeys()
    {
        var store = new ScheduleStore(new ScheduleLimits(MaxEntries: 2, DefaultClock: ClockBase.Ut));
        var definition = new ScheduleDefinition("launch", "take", null, null, true, 128,
        [
            new ScheduledCommand(1200, "hunter/throttle",
                new SimCommand("hunter", SimActions.VesselThrottle, SimCommand.NoOrdinal, 1), false),
            new ScheduledCommand(0, "hunter/ignite",
                new SimCommand("hunter", SimActions.VesselIgnite, SimCommand.NoOrdinal, 1), true),
        ]);

        var built = ScheduleBuilder.Build(store, definition);

        Assert.Multiple(() =>
        {
            Assert.That(built.IsValid, Is.True, built.Validation.Error);
            Assert.That(built.Schedule!.Id, Is.EqualTo("launch"));
            Assert.That(built.Schedule.Clock, Is.EqualTo(ClockBase.Ut));
            Assert.That(built.Schedule.Loop, Is.True);
            Assert.That(built.Schedule.Entries.Select(entry => entry.DeadlineMs), Is.EqualTo(new[] { 0d, 1200d }));
            Assert.That(built.Schedule.Entries[0].Path, Is.EqualTo("hunter/ignite"));
        });
        Assert.That(ScheduleBuilder.Submit(store, built), Is.EqualTo("launch"));
    }

    [Test]
    public void ScheduleBuilder_InvalidDefinitionDoesNotReserveItsId()
    {
        var store = new ScheduleStore();
        var invalid = new ScheduleDefinition("retry", null, null, null, null, 64,
        [
            new ScheduledCommand(-1, "throttle",
                new SimCommand("hunter", SimActions.VesselThrottle, SimCommand.NoOrdinal, 1), false),
        ]);

        Assert.That(ScheduleBuilder.Build(store, invalid).IsValid, Is.False);

        var valid = new ScheduleDefinition("retry", null, null, null, null, 64,
        [
            new ScheduledCommand(0, "throttle",
                new SimCommand("hunter", SimActions.VesselThrottle, SimCommand.NoOrdinal, 1), false),
        ]);
        Assert.That(ScheduleBuilder.Build(store, valid).IsValid, Is.True,
            "the invalid build must not claim its requested id");
    }

    [Test]
    public void ScheduleBuilder_EnforcesTheExistingPayloadLimit()
    {
        var store = new ScheduleStore(new ScheduleLimits(MaxBytes: 10));
        var definition = new ScheduleDefinition(null, "", null, null, null, 11,
        [
            new ScheduledCommand(0, "throttle",
                new SimCommand("hunter", SimActions.VesselThrottle, SimCommand.NoOrdinal, 1), false),
        ]);

        var built = ScheduleBuilder.Build(store, definition);

        Assert.That(built.IsValid, Is.False);
        Assert.That(built.Validation.Error, Does.Contain("10 bytes"));
    }
}
