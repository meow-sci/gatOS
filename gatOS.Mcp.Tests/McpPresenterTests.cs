using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using gatOS.Paint;
using NUnit.Framework;

namespace gatOS.Mcp.Tests;

[TestFixture]
public sealed class McpPresenterTests
{
    [Test]
    public void Registry_AdvertisesExactSurface_AndNeverDisplay()
    {
        var registry = new McpRegistry(new SnapshotStore());
        var names = registry.Tools.Select(t => t.ProtocolTool.Name).Order().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("gatos.get_world"));
            Assert.That(names, Does.Contain("gatos.execute_batch"));
            Assert.That(names, Does.Contain("gatos.schedule_batch"));
            Assert.That(names, Has.Length.EqualTo(26));
            Assert.That(names, Does.Contain("gatos.paint_control"));
            Assert.That(names, Has.None.Contains("display"));
            Assert.That(registry.Resources.Select(r => r.ProtocolResource?.Name ?? ""), Has.None.Contains("display"));
        });
    }

    [Test]
    public void MultipurposeTools_AdvertiseOperationAwareSchemasAndRisk()
    {
        var registry = new McpRegistry(new SnapshotStore());
        var vessel = registry.Tools.Single(t => t.ProtocolTool.Name == "gatos.vessel_control").ProtocolTool;
        var module = registry.Tools.Single(t => t.ProtocolTool.Name == "gatos.module_control").ProtocolTool;
        var camera = registry.Tools.Single(t => t.ProtocolTool.Name == "gatos.camera_control").ProtocolTool;

        var vesselProperties = vessel.InputSchema.GetProperty("properties");
        var cameraProperties = camera.InputSchema.GetProperty("properties");
        Assert.Multiple(() =>
        {
            Assert.That(vessel.Description, Does.Contain("translate/rotate/attitude_target/burn"));
            Assert.That(vesselProperties.GetProperty("operation").GetProperty("description").GetString(),
                Does.Contain("attitude_target"));
            Assert.That(vesselProperties.GetProperty("values").GetProperty("description").GetString(),
                Does.Contain("[ut,dvx,dvy,dvz]"));
            Assert.That(cameraProperties.GetProperty("operation").GetProperty("description").GetString(),
                Does.Contain("geodetic"));
            Assert.That(cameraProperties.GetProperty("values").GetProperty("description").GetString(),
                Does.Contain("seven slots"));
            Assert.That(vessel.Annotations?.DestructiveHint, Is.True);
            Assert.That(module.Annotations?.DestructiveHint, Is.True);
        });
    }

    [Test]
    public void PaintRuntime_IsFirstClassAndCapabilitiesMapItsActions()
    {
        var paint = new PaintStore();
        paint.SetGlobalPart(true, new PaintColor(0.1, 0.2, 0.3));
        var presenters = new McpPresenters(new SnapshotStore(), paint: paint);
        var runtime = presenters.GetRuntimeState("paint");
        Assert.That(runtime.Ok, Is.True);
        Assert.That(runtime.Data, Is.EqualTo(paint.Current));

        var capabilities = presenters.GetCapabilities();
        var json = gatOS.SimFs.SimJson.Serialize(capabilities.Data);
        Assert.That(json, Does.Contain("gatos.paint_control"));
        Assert.That(json, Does.Contain("paint.parts_enabled"));
    }

    [Test]
    public async Task PaintControl_MapsLogicalInputToCanonicalPaintCommand()
    {
        var sink = new RecordingSink();
        var presenters = new McpPresenters(new SnapshotStore(), sink, paint: new PaintStore());
        var handlers = new McpToolHandlers(presenters, null, null, null);

        var result = await handlers.PaintControl("kitten_material_color", "Valentina", color: [.1, .2, .3], target: "visor");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(sink.Commands, Has.Count.EqualTo(1));
            Assert.That(sink.Commands[0].Action, Is.EqualTo(SimActions.PaintKittenMaterialColor));
            Assert.That(sink.Commands[0].VesselId, Is.EqualTo("Valentina"));
            Assert.That(sink.Commands[0].Values, Is.EqualTo(new[] { .1, .2, .3 }));
            Assert.That(sink.Commands[0].Token, Is.EqualTo("visor"));
        });
    }

    [Test]
    public void Lists_DefaultTo50_CapAt1000_AndCursorSurvivesNewSnapshot()
    {
        var store = new SnapshotStore();
        store.Publish(Snapshot(1, 75));
        var presenters = new McpPresenters(store);
        var first = presenters.ListVessels();
        var page = (McpListPage<object>)first.Data!;
        Assert.That(page.Items, Has.Count.EqualTo(50));
        Assert.That(page.NextCursor, Is.Not.Null);

        store.Publish(Snapshot(2, 80));
        var second = presenters.ListVessels(cursor: page.NextCursor);
        var secondPage = (McpListPage<object>)second.Data!;
        Assert.Multiple(() =>
        {
            Assert.That(second.Ok, Is.True);
            Assert.That(second.SnapshotSequence, Is.EqualTo(2));
            Assert.That(secondPage.Items, Has.Count.EqualTo(30));
            Assert.That(presenters.ListVessels(limit: 1000).Ok, Is.True);
            Assert.That(presenters.ListVessels(limit: 1001).Errno, Is.EqualTo("EINVAL"));
        });
    }

    [Test]
    public async Task ExecuteBatch_SubmitsOnce_AndRejectsMixedPhases()
    {
        var sink = new RecordingSink();
        var presenters = new McpPresenters(new SnapshotStore(), sink);
        var ok = await presenters.SubmitBatchAsync([
            new(SimActions.VesselThrottle, "v", Value: .5),
            new(SimActions.VesselLights, "v", Value: 1),
        ], CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(ok.Ok, Is.True);
            Assert.That(sink.BatchCalls, Is.EqualTo(1));
            Assert.That(sink.Commands, Has.Count.EqualTo(2));
        });

        var mixed = await presenters.SubmitBatchAsync([
            new(SimActions.VesselThrottle, "v", Value: .5),
            new(SimActions.VesselAttitudeMode, "v", Token: "prograde"),
        ], CancellationToken.None);
        Assert.That(mixed.Errno, Is.EqualTo("EINVAL"));
        Assert.That(sink.BatchCalls, Is.EqualTo(1));
    }

    private static SimSnapshot Snapshot(long sequence, int count) => new(
        sequence, sequence, 1, "v000", Enumerable.Range(0, count).Select(i => Vessel($"v{i:000}")).ToArray(),
        [], "test", 20, []);

    private static VesselSnapshot Vessel(string id) => new(
        id, id, "Freefall", default, 0, 0, 0, 0, 0, default, default, 0, 0, 1, 1, 0,
        null, [], [], null, "Kerth", false, []);

    private sealed class RecordingSink : ICommandSink
    {
        internal List<SimCommand> Commands { get; } = [];
        internal int BatchCalls { get; private set; }
        public bool ControlEnabled => true;
        public bool DebugEnabled => true;
        public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(CommandResult.Ok);
        }
        public Task<CommandResult> SubmitBatchAsync(IReadOnlyList<SimCommand> commands, CancellationToken ct)
        {
            BatchCalls++;
            Commands.AddRange(commands);
            return Task.FromResult(CommandResult.Ok);
        }
    }
}
