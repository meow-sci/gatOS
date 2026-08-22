using gatOS.SimFs;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Paint;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using gatOS.Paint;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace gatOS.Mcp;

/// <summary>Immutable SDK primitive registry reused by each stateless HTTP request.</summary>
public sealed class McpRegistry
{
    private static readonly JsonSerializerOptions RegistryJson = new(SimJson.Options)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    private static readonly JsonElement EnvelopeOutputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "ok", "snapshot_sequence", "ut" },
        properties = new
        {
            ok = new { type = "boolean" }, data = new { }, snapshot_sequence = new { type = "integer" },
            ut = new { type = "number" }, outcome = new { type = new[] { "string", "null" } },
            errno = new { type = new[] { "string", "null" } }, message = new { type = new[] { "string", "null" } },
            retryable = new { type = new[] { "boolean", "null" } },
        },
    });
    public McpRegistry(SnapshotStore snapshots, ICommandSink? commands = null, Func<string>? transports = null,
        AudioStore? audio = null, CameraStore? camera = null, ScheduleStore? schedules = null,
        PaintStore? paint = null, TextureStore? textures = null)
    {
        Presenters = new(snapshots, commands, transports, audio, camera, schedules, paint, textures);
        var h = new McpToolHandlers(Presenters, audio, camera, schedules, textures);
        Tools = new McpServerPrimitiveCollection<McpServerTool>();
        AddRead("gatos.get_world", h.GetWorld, "Read simulation time, status, indexes, or the complete current world.");
        AddRead("gatos.list_celestials", h.ListCelestials, "List celestial summaries with deterministic cursor pagination.");
        AddRead("gatos.get_celestial", h.GetCelestial, "Read one complete celestial body by raw id.");
        AddRead("gatos.list_vessels", h.ListVessels, "List and filter vessel summaries.");
        AddRead("gatos.get_vessel", h.GetVessel, "Read selected complete logical sections for one vessel or active.");
        AddRead("gatos.list_kittens", h.ListKittens, "List EVA kitten vessels.");
        AddRead("gatos.get_kitten", h.GetKitten, "Read a kitten through the shared vessel telemetry model.");
        AddRead("gatos.get_runtime_state", h.GetRuntimeState, "Read complete state for one gatOS runtime feature.");
        AddRead("gatos.get_capabilities", h.GetCapabilities, "Discover actions, limits, gates, phases, and safety notes.");
        AddRead("gatos.wait", h.Wait, "Wait for a newer snapshot, event, or UT condition.");

        AddWrite("gatos.ignite_engines", h.Ignite, "Ignite all ignitable vessel engines.", destructive: true);
        AddWrite("gatos.shutdown_engines", h.Shutdown, "Shut down all vessel engines.", destructive: true);
        AddWrite("gatos.activate_stage", h.Stage, "Activate the next vessel stage.", destructive: true);
        AddWrite("gatos.vessel_control", h.VesselControl, "Vessel control family. operation selects the payload shape: flags/fractions use value; translate/rotate/attitude_target/burn use values; modes/frames use token. Includes irreversible ignite, stage, and take_control operations; inspect state first.", destructive: true);
        AddWrite("gatos.module_control", h.ModuleControl, "Indexed module control family. operation selects engine/RCS/light/animation/docking/decoupler semantics; ordinal is always the zero-based module index. Includes irreversible undock and fire_decoupler operations; inspect module state first.", destructive: true);
        AddWrite("gatos.camera_control", h.CameraControl, "Programmable camera family. operation selects ownership, target/frame token, scalar lens/orbit value, vector/quaternion placement, seven-slot aim, or playback key/value payload. Read camera runtime state before composing complex calls.");
        AddWrite("gatos.camera_track", h.CameraTrack, "List/read/upload/delete structured camera tracks, or play/update/stop the camera player. Uploads accept UTF-8 JSON chunks by decoded byte offset.");
        AddWrite("gatos.audio_control", h.AudioControl, "Audio playback family. play uses token=clip, aux=channel, values=[start_ms,end_ms,volume,loop,pan,pitch,group]; update uses flat numeric key/value pairs; stop uses token=all/channel/clip.");
        AddWrite("gatos.audio_clip", h.AudioClip, "List, retrieve as MCP audio/binary content, upload in base64 chunks, or delete audio clips.", outputSchema: EnvelopeOutputSchema);
        AddWrite("gatos.schedule_control", h.ScheduleControl, "Inspect or control schedule/camera players. list has no id; get/stop/remove use id; pause/loop use value 0|1; scrub uses milliseconds; rate uses 0..100; clear has no id.");
        AddWrite("gatos.debug_control", h.DebugControl, "Explicit debug/cheat family for warp, teleport/impulse, refills, welds, cosmetics, IVA physics, and face FX. Each operation has a distinct payload; consult gatos.get_capabilities and the MCP reference before mutation.", destructive: true);
        AddWrite("gatos.render_fx_control", h.RenderFxControl, "Runtime FX editor. family is engine_plume/plume_trail/clouds/terrain; operation is set/reset or plume_trail clear; entity scope and field path vary by family.");
        AddWrite("gatos.paint_control", h.PaintControl, "Opt-in paint family. operation selects master/global/template/vessel/part/EVA rule; flags use value, colors use normalized color=[r,g,b], and target names a template, part instance, blend, or EVA material.");
        AddWrite("gatos.paint_texture", h.PaintTexture, "Custom ground-clutter texture store. operation selects list/catalog/bindings/retrieve/upload/delete; upload takes base64 bytes chunkable by decoded byte offset. Bind an uploaded image over a stock texture with gatos.paint_control(operation:\"texture_bind\").", outputSchema: EnvelopeOutputSchema);
        AddWrite("gatos.command", h.Command, "Advanced canonical action envelope covering every catalog action.", destructive: true);
        AddWrite("gatos.execute_batch", h.ExecuteBatch, "Validate and submit an ordered same-phase same-tick command batch.", destructive: true);
        AddWrite("gatos.schedule_batch", h.ScheduleBatch, "Create a typed render, wall, or UT timed command sequence.", destructive: true);

        Resources = new McpServerResourceCollection();
        AddResource("gatos.world", "gatos://world", (Func<string>)(() => Presenters.ResourceJson("gatos://world")), "Current simulation summary.");
        AddResource("gatos.celestials", "gatos://celestials", (Func<string>)(() => Presenters.ResourceJson("gatos://celestials")), "Celestial summaries.");
        AddResource("gatos.celestial", "gatos://celestials/{id}", (Func<string, string>)((id) => Presenters.ResourceJson("gatos://celestials/" + Uri.EscapeDataString(id))), "One complete celestial.");
        AddResource("gatos.vessels", "gatos://vessels", (Func<string>)(() => Presenters.ResourceJson("gatos://vessels")), "Vessel summaries.");
        AddResource("gatos.vessel", "gatos://vessels/{id}", (Func<string, string>)((id) => Presenters.ResourceJson("gatos://vessels/" + Uri.EscapeDataString(id))), "One complete vessel.");
        AddResource("gatos.kittens", "gatos://kittens", (Func<string>)(() => Presenters.ResourceJson("gatos://kittens")), "EVA kitten summaries.");
        AddResource("gatos.kitten", "gatos://kittens/{id}", (Func<string, string>)((id) => Presenters.ResourceJson("gatos://kittens/" + Uri.EscapeDataString(id))), "One complete kitten vessel.");
        AddResource("gatos.runtime", "gatos://runtime/{feature}", (Func<string, string>)((feature) => Presenters.ResourceJson("gatos://runtime/" + Uri.EscapeDataString(feature))), "One complete runtime feature state.");
        AddResource("gatos.capabilities", "gatos://capabilities", (Func<string>)(() => Presenters.ResourceJson("gatos://capabilities")), "Current MCP capabilities and gates.");
    }

    public McpPresenters Presenters { get; }
    public McpServerPrimitiveCollection<McpServerTool> Tools { get; }
    public McpServerResourceCollection Resources { get; }

    public McpServerOptions CreateOptions(string version) => new()
    {
        ServerInfo = new Implementation { Name = "gatOS", Version = version },
        ServerInstructions = "Operating loop: call gatos.get_capabilities, discover raw ids, read the target and its controllable/gate state, call the narrowest tool, then gatos.wait(after_sequence=previous) and re-read before planning again. Multipurpose control tools are discriminated by operation: read each parameter description and the matching capability action before filling value/values/token/aux; omit irrelevant slots. KSA vectors use documented CCI/body frames. execute_batch is same-tick and single-phase; schedule_batch uses absolute at_ms offsets and may mix phases. Never blindly retry one-shot triggers such as stage, ignite, undock, decoupler fire, release, clear, or remove. Expected failures return isError plus structured errno/retryable fields. /sim/display is intentionally absent because it is a TTY-only visual stream.",
        ScopeRequests = false,
        ToolCollection = Tools,
        ResourceCollection = Resources,
        Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability(),
            Resources = new ResourcesCapability(),
        },
    };

    private void AddRead(string name, Delegate method, string description) => Tools.Add(new ErrorMarkingTool(McpServerTool.Create(method, new()
    {
        Name = name, Description = description, ReadOnly = true, Idempotent = true, Destructive = false,
        OpenWorld = false, UseStructuredContent = true, SerializerOptions = RegistryJson,
    })));

    private void AddWrite(string name, Delegate method, string description, bool destructive = false, JsonElement? outputSchema = null) => Tools.Add(new ErrorMarkingTool(McpServerTool.Create(method, new()
    {
        Name = name, Description = description, ReadOnly = false, Idempotent = false, Destructive = destructive,
        OpenWorld = false, UseStructuredContent = true, OutputSchema = outputSchema, SerializerOptions = RegistryJson,
    })));

    private void AddResource(string name, string uri, Delegate method, string description) => Resources.Add(McpServerResource.Create(method, new()
    {
        Name = name, UriTemplate = uri, Description = description, MimeType = "application/json", SerializerOptions = RegistryJson,
    }));

    private sealed class ErrorMarkingTool(McpServerTool inner) : DelegatingMcpServerTool(inner)
    {
        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var result = await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.StructuredContent is JsonElement structured
                && structured.ValueKind == JsonValueKind.Object
                && structured.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.False)
                result.IsError = true;
            return result;
        }
    }
}
