using gatOS.SimFs;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Paint;
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
            Assert.That(names, Has.Length.EqualTo(27));
            Assert.That(names, Does.Contain("gatos.paint_control"));
            Assert.That(names, Does.Contain("gatos.paint_texture"));
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
    public async Task PaintControl_MapsTextureOperationsIncludingTheFileSlot()
    {
        var sink = new RecordingSink();
        var presenters = new McpPresenters(new SnapshotStore(), sink);
        var handlers = new McpToolHandlers(presenters, null, null, null, new TextureStore());

        await handlers.PaintControl("texture_bind", target: "Core/Rock.png", file: "rock.png");
        Assert.Multiple(() =>
        {
            Assert.That(sink.Commands[0].Action, Is.EqualTo(SimActions.PaintTextureBind));
            Assert.That(sink.Commands[0].Token, Is.EqualTo("Core/Rock.png"));
            Assert.That(sink.Commands[0].Aux, Is.EqualTo("rock.png"), "the file slot rides Aux");
        });

        await handlers.PaintControl("texture_clear", value: 1);
        Assert.That(sink.Commands[1].Action, Is.EqualTo(SimActions.PaintTextureClear));

        // The `all` spelling must survive the canonical envelope too — the bridge normalizes it,
        // so it cannot mean one thing over 9p and another over MCP.
        await handlers.PaintControl("texture_unbind", target: "all");
        Assert.That(sink.Commands[2].Token, Is.EqualTo("all"));
    }

    [Test]
    public void PaintTexture_UploadsAndReportsThroughTheSameStore()
    {
        var textures = new TextureStore();
        var presenters = new McpPresenters(new SnapshotStore(), new RecordingSink(), textures: textures);
        var handlers = new McpToolHandlers(presenters, null, null, null, textures);

        var png = Convert.ToBase64String(
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
        var upload = handlers.PaintTexture("upload", "rock.png", data_base64: png);
        Assert.That(upload.IsError, Is.Not.True, "upload should succeed");
        Assert.That(textures.TryGet("rock.png", out _), Is.EqualTo(TextureLookup.Ready));

        Assert.That(handlers.PaintTexture("list").IsError, Is.Not.True);
        Assert.That(handlers.PaintTexture("catalog").IsError, Is.Not.True);
        Assert.That(handlers.PaintTexture("bindings").IsError, Is.Not.True);

        var missing = handlers.PaintTexture("retrieve", "nope.png");
        Assert.Multiple(() =>
        {
            Assert.That(missing.IsError, Is.True);
            Assert.That(SimJson.Serialize(missing.StructuredContent), Does.Contain("ENOENT"));
        });
    }

    [Test]
    public void PaintTextures_IsAFirstClassRuntimeFeatureAndCapability()
    {
        var presenters = new McpPresenters(new SnapshotStore(), new RecordingSink(),
            textures: new TextureStore());

        Assert.That(presenters.GetRuntimeState("paint_textures").Ok, Is.True);

        var json = SimJson.Serialize(presenters.GetCapabilities().Data);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("paint_textures"));
            Assert.That(json, Does.Contain("paint.texture_bind"));
            Assert.That(json, Does.Contain("control_enabled + paint textures store"));
        });

        var disabled = new McpPresenters(new SnapshotStore(), new RecordingSink())
            .GetRuntimeState("paint_textures");
        Assert.That(disabled.Errno, Is.EqualTo("EOPNOTSUPP"), "an absent store reports unsupported");
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
