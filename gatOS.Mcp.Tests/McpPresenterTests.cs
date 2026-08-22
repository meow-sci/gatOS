using gatOS.SimFs;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Paint;
using gatOS.SimFs.Paint.Stickers;
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
            Assert.That(names, Has.Length.EqualTo(28));
            Assert.That(names, Does.Contain("gatos.paint_control"));
            Assert.That(names, Does.Contain("gatos.paint_texture"));
            Assert.That(names, Does.Contain("gatos.paint_sticker"));
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

    [Test]
    public void PaintSticker_AdvertisesItsOperationsAndAnchorVocabulary()
    {
        var registry = new McpRegistry(new SnapshotStore());
        var sticker = registry.Tools.Single(t => t.ProtocolTool.Name == "gatos.paint_sticker").ProtocolTool;
        var properties = sticker.InputSchema.GetProperty("properties");
        Assert.Multiple(() =>
        {
            Assert.That(sticker.Description, Does.Contain("place/spray/set/remove/clear/list/debug"));
            Assert.That(sticker.Description, Does.Contain("gatos.paint_texture"),
                "images upload through the texture tool; there is no second upload surface");
            Assert.That(properties.GetProperty("operation").GetProperty("description").GetString(),
                Does.Contain("spray"));
            Assert.That(properties.GetProperty("anchor").GetProperty("description").GetString(),
                Does.Contain("vessel").And.Contain("body"));
            Assert.That(properties.GetProperty("aim").GetProperty("description").GetString(),
                Does.Contain("cursor"));
            Assert.That(properties.GetProperty("depth").GetProperty("description").GetString(),
                Does.Contain("0.3"), "the anchor-kind depth defaults are discoverable from the schema");
            Assert.That(sticker.Annotations?.ReadOnlyHint, Is.Not.True);
            Assert.That(sticker.OutputSchema?.GetProperty("required").EnumerateArray()
                .Select(x => x.GetString()), Does.Contain("ok"));
        });
    }

    [Test]
    public void PaintStickers_IsAFirstClassRuntimeFeatureAndCapability()
    {
        var stickers = new StickerStore(64, 1234);
        var presenters = new McpPresenters(new SnapshotStore(), new RecordingSink(), stickers: stickers);

        var runtime = presenters.GetRuntimeState("paint_stickers");
        Assert.That(runtime.Ok, Is.True);
        var runtimeJson = SimJson.Serialize(runtime.Data);
        Assert.Multiple(() =>
        {
            Assert.That(runtimeJson, Does.Contain("max_count"));
            Assert.That(runtimeJson, Does.Contain("max_view_distance_m"));
            Assert.That(runtimeJson, Does.Contain("\"debug\""));
            Assert.That(runtimeJson, Does.Contain("\"last\""));
            Assert.That(runtimeJson, Does.Contain("\"runtime\""));
        });

        var json = SimJson.Serialize(presenters.GetCapabilities().Data);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("paint_stickers"));
            Assert.That(json, Does.Contain("paint.sticker_spray"));
            Assert.That(json, Does.Contain("gatos.paint_sticker"));
            Assert.That(json, Does.Contain("control_enabled + paint stickers"));
        });

        var disabled = new McpPresenters(new SnapshotStore(), new RecordingSink())
            .GetRuntimeState("paint_stickers");
        Assert.That(disabled.Errno, Is.EqualTo("EOPNOTSUPP"), "an absent registry reports unsupported");
    }

    [Test]
    public async Task PaintSticker_PlaceAndSpray_MatchTheLineGrammarsExactly()
    {
        var (sink, handlers) = StickerHandlers();

        await handlers.PaintSticker("place", "meow.png", "vessel", "Kitten-1", 7,
            position: [0, 0.5, -1.4], normal: [0, 1, 0], roll: 15, width: 0.6, height: 0.3);
        AssertSameCommand(sink.Commands[0],
            StickerCommands.ParsePlace("meow.png vessel Kitten-1 7 0 0.5 -1.4 0 1 0 roll=15 w=0.6 h=0.3"));

        await handlers.PaintSticker("place", "meow.png", "body", body: "Mun", lat: 12.03, lon: -41.88,
            heading: 90, width: 5, height: 5);
        AssertSameCommand(sink.Commands[1],
            StickerCommands.ParsePlace("meow.png body Mun 12.03 -41.88 heading=90 w=5 h=5"));

        // The anchor keyword is inferred from which target slot is filled, so `body` alone is enough.
        await handlers.PaintSticker("place", "meow.png", body: "Mun", lat: 0, lon: 0);
        Assert.That(sink.Commands[2].Aux, Is.EqualTo("body Mun"));

        await handlers.PaintSticker("spray", "meow.png");
        AssertSameCommand(sink.Commands[3], StickerCommands.ParseSpray("meow.png"));
        Assert.That(sink.Commands[3].Values![4], Is.EqualTo(StickerCommands.DepthUnset),
            "an omitted depth stays the sentinel so the game picks the anchor kind's default");

        await handlers.PaintSticker("spray", "meow.png", aim: "cursor", range: 50, roll: 30, width: 2,
            height: 2, depth: 0.5, alpha: 0.4, brightness: 2);
        AssertSameCommand(sink.Commands[4],
            StickerCommands.ParseSpray("meow.png aim=cursor range=50 roll=30 w=2 h=2 d=0.5 alpha=0.4 brightness=2"));
    }

    [Test]
    public async Task PaintSticker_SetRemoveClearAndDebug_EmitOneCanonicalActionEach()
    {
        var (sink, handlers) = StickerHandlers();

        await handlers.PaintSticker("set", id: 3, width: 2, height: 1.5);
        await handlers.PaintSticker("set", id: 3, depth: 0.75);
        await handlers.PaintSticker("set", id: 3, roll: 45);
        await handlers.PaintSticker("set", id: 3, heading: 90);
        await handlers.PaintSticker("set", id: 3, alpha: 0.4);
        await handlers.PaintSticker("set", id: 3, brightness: 2);
        await handlers.PaintSticker("set", "other.png", id: 3);
        await handlers.PaintSticker("set", id: 3, value: 0);
        await handlers.PaintSticker("remove", id: 3);
        await handlers.PaintSticker("clear");
        await handlers.PaintSticker("debug", value: 1);

        Assert.Multiple(() =>
        {
            Assert.That(sink.Commands.Select(c => c.Action), Is.EqualTo(new[]
            {
                SimActions.PaintStickerSize, SimActions.PaintStickerDepth, SimActions.PaintStickerRotation,
                SimActions.PaintStickerRotation, SimActions.PaintStickerAlpha,
                SimActions.PaintStickerBrightness, SimActions.PaintStickerImage,
                SimActions.PaintStickerVisible, SimActions.PaintStickerRemove,
                SimActions.PaintStickerClear, SimActions.PaintStickerDebug,
            }));
            Assert.That(sink.Commands[0].Values, Is.EqualTo(new[] { 2d, 1.5 }));
            Assert.That(sink.Commands[0].Ordinal, Is.EqualTo(3));
            Assert.That(sink.Commands[3].Value, Is.EqualTo(90), "heading is the body spelling of rotation");
            Assert.That(sink.Commands[6].Token, Is.EqualTo("other.png"));
            Assert.That(sink.Commands[7].Value, Is.EqualTo(0), "value carries visibility for set");
            Assert.That(sink.Commands[9].Ordinal, Is.EqualTo(SimCommand.NoOrdinal), "clear is global");
            Assert.That(sink.Commands[10].Ordinal, Is.EqualTo(SimCommand.NoOrdinal), "debug is global");
        });
    }

    [Test]
    public async Task PaintSticker_ListReadsTheRegistry_AndBadInputNeverReachesTheSink()
    {
        var (sink, handlers) = StickerHandlers();

        var list = await handlers.PaintSticker("list");
        Assert.Multiple(() =>
        {
            Assert.That(list.Ok, Is.True);
            Assert.That(SimJson.Serialize(list.Data), Does.Contain("stickers").And.Contain("runtime"));
            Assert.That(sink.Commands, Is.Empty, "list is a read, not a command");
        });

        var bad = new[]
        {
            await handlers.PaintSticker("place", "meow.png", "vessel", "Kitten-1", 7,
                position: [0, 0, 0], normal: [0, 0, 0]),
            await handlers.PaintSticker("place", "meow.png", "body", body: "Mun", lat: 91, lon: 0),
            await handlers.PaintSticker("place", "meow.png", "hull", vessel_id: "Kitten-1"),
            await handlers.PaintSticker("spray", "meow.png", aim: "eyeballs"),
            await handlers.PaintSticker("spray", "meow.png", width: 0),
            await handlers.PaintSticker("spray", "meow.png", brightness: 9),
            await handlers.PaintSticker("set", id: 3, alpha: 0.5, depth: 1),
            // roll and heading are two spellings of ONE knob: asking for both is ambiguous, and
            // folding them before the count would have let this through and dropped heading.
            await handlers.PaintSticker("set", id: 3, heading: 90, roll: 45),
            await handlers.PaintSticker("set", alpha: 0.5),
            await handlers.PaintSticker("remove"),
            await handlers.PaintSticker("wat"),
        };
        Assert.Multiple(() =>
        {
            foreach (var envelope in bad)
                Assert.That(envelope.Errno, Is.EqualTo("EINVAL"), envelope.Message);
            Assert.That(sink.Commands, Is.Empty, "every rejection happened before the sink");
        });

        var off = new McpToolHandlers(new McpPresenters(new SnapshotStore(), sink), null, null, null);
        Assert.That((await off.PaintSticker("list")).Errno, Is.EqualTo("EOPNOTSUPP"));
    }

    private static (RecordingSink Sink, McpToolHandlers Handlers) StickerHandlers()
    {
        var sink = new RecordingSink();
        var stickers = new StickerStore();
        var presenters = new McpPresenters(new SnapshotStore(), sink, stickers: stickers);
        return (sink, new McpToolHandlers(presenters, null, null, null, new TextureStore(), stickers));
    }

    private static void AssertSameCommand(SimCommand actual, SimCommand? expected)
    {
        Assert.That(expected, Is.Not.Null, "the line grammar must accept the reference line");
        Assert.Multiple(() =>
        {
            Assert.That(actual.Action, Is.EqualTo(expected!.Action));
            Assert.That(actual.VesselId, Is.EqualTo(expected.VesselId));
            Assert.That(actual.Ordinal, Is.EqualTo(expected.Ordinal));
            Assert.That(actual.Token, Is.EqualTo(expected.Token));
            Assert.That(actual.Aux, Is.EqualTo(expected.Aux));
            Assert.That(actual.Values, Is.EqualTo(expected.Values));
        });
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
