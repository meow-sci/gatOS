using System.ComponentModel;
using System.Text;
using System.Text.Json;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Commands;
using ModelContextProtocol.Protocol;

namespace gatOS.Mcp;

internal sealed class McpToolHandlers
{
    private readonly McpPresenters _presenters;
    private readonly AudioStore? _audio;
    private readonly CameraStore? _camera;
    private readonly ScheduleStore? _schedules;

    internal McpToolHandlers(McpPresenters presenters, AudioStore? audio, CameraStore? camera, ScheduleStore? schedules)
    {
        _presenters = presenters;
        _audio = audio;
        _camera = camera;
        _schedules = schedules;
    }

    public McpEnvelope GetWorld([Description("summary or full")] string detail = "summary") => _presenters.GetWorld(detail);
    public McpEnvelope ListCelestials([Description("1..1000; defaults to 50")] int limit = 50, string? cursor = null) => _presenters.ListCelestials(limit, cursor);
    public McpEnvelope GetCelestial([Description("Raw celestial body id")] string id) => _presenters.GetCelestial(id);
    public McpEnvelope ListVessels([Description("1..1000; defaults to 50")] int limit = 50,
        [Description("Opaque next_cursor from an earlier gatos.list_vessels result")] string? cursor = null,
        [Description("Optional exact controlled-state filter")] bool? controlled = null,
        [Description("Optional exact KSA controllable-state filter")] bool? controllable = null,
        [Description("Optional exact EVA-kitten filter")] bool? is_kitten = null,
        [Description("Optional raw parent celestial id filter")] string? parent_body = null,
        [Description("Optional exact published situation token filter")] string? situation = null)
        => _presenters.ListVessels(limit, cursor, controlled, controllable, is_kitten, parent_body, situation);
    public McpEnvelope GetVessel([Description("Raw vessel id, or active")] string id,
        [Description("Omit for all, or choose flight, orbit, environment, propulsion, resources, power, control, modules, encounters, parts, paint, all")] IReadOnlyList<string>? include = null) => _presenters.GetVessel(id, include);
    public McpEnvelope ListKittens([Description("1..1000; defaults to 50")] int limit = 50,
        [Description("Opaque next_cursor from an earlier gatos.list_kittens result")] string? cursor = null) => _presenters.ListKittens(limit, cursor);
    public McpEnvelope GetKitten([Description("Raw id of a vessel whose is_kitten is true")] string id,
        [Description("Omit for all, or choose flight, orbit, environment, propulsion, resources, power, control, modules, encounters, parts, paint, all")] IReadOnlyList<string>? include = null) => _presenters.GetVessel(id, include, true);
    public McpEnvelope GetRuntimeState([Description("camera, schedules, audio, paint, welds, thug_life, face_fx, iva, engine_plume, plume_trail, clouds, or terrain")] string feature) => _presenters.GetRuntimeState(feature);
    public McpEnvelope GetCapabilities() => _presenters.GetCapabilities();
    public Task<McpEnvelope> Wait([Description("Return after snapshot_sequence becomes greater than this value")] long? after_sequence = null,
        [Description("Return on the next event with this exact type")] string? event_type = null,
        [Description("Optional raw vessel id filter, used only with event_type")] string? vessel_id = null,
        [Description("Return when universal simulation time reaches this value in seconds")] double? until_ut = null,
        [Description("Wall-clock timeout in milliseconds, 1..120000; defaults to 30000")] int timeout_ms = 30_000, CancellationToken cancellationToken = default)
        => _presenters.WaitAsync(after_sequence, event_type, vessel_id, until_ut, timeout_ms, cancellationToken);

    public Task<McpEnvelope> Ignite(string vessel_id, CancellationToken cancellationToken = default)
        => _presenters.SubmitAsync(new(SimActions.VesselIgnite, vessel_id, Value: 1), cancellationToken);
    public Task<McpEnvelope> Shutdown(string vessel_id, CancellationToken cancellationToken = default)
        => _presenters.SubmitAsync(new(SimActions.VesselShutdown, vessel_id, Value: 1), cancellationToken);
    public Task<McpEnvelope> Stage(string vessel_id, CancellationToken cancellationToken = default)
        => _presenters.SubmitAsync(new(SimActions.VesselStage, vessel_id, Value: 1), cancellationToken);

    public Task<McpEnvelope> VesselControl(
        [Description("ignite, shutdown, engine_master, stage, throttle, lights, rcs, translate, rotate, attitude_mode, attitude_frame, attitude_target, burn, rcs_mode, scale, always_render, focus, take_control, or a dotted canonical action")] string operation,
        [Description("Raw target vessel id")] string vessel_id,
        [Description("Normally -1; only a dotted indexed canonical action uses this slot")] int ordinal = -1,
        [Description("Scalar payload: flags 0/1; throttle 0..1; scale >0")] double value = 0,
        [Description("Numeric payload: translate/rotate [x,y,z], attitude_target [x,y,z,w], burn [ut,dvx,dvy,dvz]")] IReadOnlyList<double>? values = null,
        [Description("Symbolic payload: attitude/rcs mode or frame token")] string? token = null,
        [Description("Secondary token; normally null for vessel operations")] string? aux = null, CancellationToken cancellationToken = default)
        => Logical("vessel", operation, vessel_id, ordinal, value, values, token, aux, cancellationToken);
    public Task<McpEnvelope> ModuleControl(
        [Description("engine_active, engine_minimum_throttle, rcs_active, light_on, light_brightness, light_color, light_outer_angle, light_inner_angle, animation_goal, solar_deployment, undock, fire_decoupler, pushoff, or a dotted canonical action")] string operation,
        [Description("Raw target vessel id")] string vessel_id,
        [Description("Zero-based module ordinal from gatos.get_vessel")] int ordinal,
        [Description("Scalar payload: flags, fractions, brightness, angles in degrees, or pushoff impulse in N*s")] double value = 0,
        [Description("Numeric payload; light_color uses normalized [r,g,b]")] IReadOnlyList<double>? values = null,
        [Description("Primary symbolic payload for dotted canonical actions")] string? token = null,
        [Description("Secondary symbolic payload for dotted canonical actions")] string? aux = null, CancellationToken cancellationToken = default)
        => Logical("module", operation, vessel_id, ordinal, value, values, token, aux, cancellationToken);
    public Task<McpEnvelope> CameraControl(
        [Description("ownership/take, release, mode, follow, tidal, map_scope, position, frame, anchor, geodetic, orbit_radius, orbit_azimuth, orbit_elevation, rotation, aim, aim_target, aim_offset, aim_frame, aim_up, roll, fov, ortho, ortho_height, smoothing, reset, play, set, or stop")] string operation,
        [Description("Scalar payload: flags, metres, degrees, seconds, or playback scalar as documented for the operation")] double value = 0,
        [Description("Numeric payload: position/offset [x,y,z], rotation [x,y,z,w], geo [lat,lon,alt], aim seven slots, player key/value pairs")] IReadOnlyList<double>? values = null,
        [Description("Target ref, frame/mode/up token, or track name, depending on operation")] string? token = null,
        [Description("Optional camera-track group; otherwise operation-specific secondary token")] string? aux = null, CancellationToken cancellationToken = default)
        => Logical("camera", operation, "", -1, value, values, token, aux, cancellationToken);
    public Task<McpEnvelope> AudioControl(
        [Description("play, update, pause, resume, seek, or stop")] string operation,
        [Description("Operation-specific scalar; prefer values for audio.play/audio.set structured slots")] double value = 0,
        [Description("play: [start_ms,end_ms,volume,loop,pan,pitch,group]; update: flat [key,value,...], keys 0=volume 1=pan 2=pitch 3=paused 4=seek_ms")] IReadOnlyList<double>? values = null,
        [Description("Clip name for play; channel id/clip name for update; all/channel id/clip name for stop")] string? token = null,
        [Description("Optional channel id for play")] string? aux = null, CancellationToken cancellationToken = default)
        => Logical("audio", operation, "", -1, value, values, token, aux, cancellationToken);
    public Task<McpEnvelope> ScheduleControl(
        [Description("list, get, pause, resume, scrub, rate, loop, stop, remove, or clear")] string operation,
        [Description("Player id; omit for list and clear")] string? id = null,
        [Description("pause/loop flag 0|1, scrub position ms, or rate 0..100; ignored by list/get/stop/remove/clear/resume")] double value = 0,
        [Description("Fallback player id when id is omitted")] string? token = null,
        CancellationToken cancellationToken = default)
    {
        if (operation.Equals("list", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(_presenters.GetRuntimeState("schedules"));
        if (operation.Equals("get", StringComparison.OrdinalIgnoreCase))
        {
            var s = _presenters.Current;
            var player = id is null ? null : _schedules?.Find(id);
            return Task.FromResult(player is null
                ? new McpEnvelope(false, null, s.Sequence, s.UtSeconds, "not_found", "ENOENT", $"no player '{id}'", false)
                : McpEnvelope.Success(new { player.Id, player.Kind, player.Group, clock = player.Clock.Base.ToString().ToLowerInvariant(), player.DurationMs, state = player.State.ToString().ToLowerInvariant(), player.PendingCount, player.Dropped, player.LastError }, s.Sequence, s.UtSeconds));
        }
        var mapped = operation.Equals("resume", StringComparison.OrdinalIgnoreCase) ? "pause" : operation;
        var mappedValue = operation.Equals("resume", StringComparison.OrdinalIgnoreCase) ? 0 : value;
        return Logical("schedule", mapped, "", -1, mappedValue, null, id ?? token, null, cancellationToken);
    }
    public Task<McpEnvelope> DebugControl(
        [Description("warp, control_vessel, always_render_iva, teleport, impulse, refill_fuel, refill_battery, docking_pushoff, weld_create, weld_here, weld_remove, weld_clear, weld_enable, thug_life_*, iva_*, fx_spawn, fx_clear, or a dotted canonical debug action")] string operation,
        [Description("Raw vessel id for vessel-scoped actions; empty for global actions")] string vessel_id = "",
        [Description("Indexed docking port, cosmetic entry, or IVA object id; -1 when not indexed")] int ordinal = -1,
        [Description("Flag, warp factor, max count, or impulse scalar as documented for the operation")] double value = 0,
        [Description("Operation-specific vector/pose payload; inspect gatos.get_capabilities and the MCP reference before use")] IReadOnlyList<double>? values = null,
        [Description("Target vessel/id/profile token as documented for the operation")] string? token = null,
        [Description("Secondary unit, template substring, or FX profile token as documented for the operation")] string? aux = null, CancellationToken cancellationToken = default)
        => Logical("debug", operation, vessel_id, ordinal, value, values, token, aux, cancellationToken);
    public Task<McpEnvelope> RenderFxControl(
        [Description("engine_plume, plume_trail, clouds, or terrain")] string family,
        [Description("set, reset, or clear; clear is valid only for plume_trail")] string operation,
        [Description("engine template id, body id, or null for global plume trail / terrain wireframe")] string? entity = null,
        [Description("FxCatalog field path for set, including layer/type indices where required")] string? field = null,
        [Description("Scalar field value; also mirrored into values by the filesystem contract when applicable")] double value = 0,
        [Description("Vector/color field payload using the field's declared arity")] IReadOnlyList<double>? values = null,
        [Description("Alternate field token when field is omitted")] string? token = null, CancellationToken cancellationToken = default)
        => Logical("debug." + FxFamily(family), operation, "", -1, value, values, field ?? token, entity, cancellationToken);
    public Task<McpEnvelope> PaintControl(
        [Description("parts_enabled, blend, parts_clear, global_*, template_*, vessel_*, part_*, kittens_enabled, kittens_clear, kitten_shared_*, kitten_shared_material_*, kitten_*, or kitten_material_*")] string operation,
        [Description("Raw vessel/EVA id for vessel, part, kitten, and kitten_material operations; empty for global/shared operations")] string vessel_id = "",
        [Description("0|1 for enabled flags and clear triggers")] double value = 0,
        [Description("Normalized sRGB [r,g,b], each 0..1, for *_color operations")] IReadOnlyList<double>? color = null,
        [Description("Template id, part instance_id, or semantic EVA material name")] string? target = null,
        CancellationToken cancellationToken = default)
        => Logical("paint", operation, vessel_id, -1, value, color, target, null, cancellationToken);

    public Task<McpEnvelope> Command(
        [Description("Canonical gatOS action key returned by gatos.get_capabilities.actions[].action")] string action,
        [Description("Raw vessel id for vessel actions; empty string for global actions")] string vessel_id = "",
        [Description("Indexed module/entity ordinal, or -1 when not addressed by ordinal")] int ordinal = -1,
        [Description("Action-specific finite scalar; consult the matching capability action entry")] double value = 0,
        [Description("Action-specific finite numeric payload; consult the matching capability action entry")] IReadOnlyList<double>? values = null,
        [Description("Action-specific primary symbolic payload")] string? token = null,
        [Description("Action-specific secondary symbolic payload")] string? aux = null, CancellationToken cancellationToken = default)
        => _presenters.SubmitAsync(new(action, vessel_id, ordinal, value, values, token, aux), cancellationToken);

    public Task<McpEnvelope> ExecuteBatch(
        [Description("1..64 canonical command envelopes; all actions must derive the same Frame or Solver phase")] IReadOnlyList<McpCommandEnvelope> commands,
        CancellationToken cancellationToken = default)
        => _presenters.SubmitBatchAsync(commands, cancellationToken);

    public McpEnvelope ScheduleBatch(
        [Description("Timed entries shaped as {at_ms,command}; at_ms is a non-negative absolute offset from schedule start")] IReadOnlyList<McpScheduleEntry> entries,
        [Description("Optional unique player id; generated when omitted")] string? id = null,
        [Description("Optional shared playback group")] string? group = null,
        [Description("render, wall, or ut; defaults to wall")] string clock = "wall",
        [Description("Initial playback rate; finite and 0..100")] double rate = 1,
        [Description("Whether to loop at the schedule duration")] bool loop = false)
    {
        var s = _presenters.Current;
        if (_schedules is null) return new(false, null, s.Sequence, s.UtSeconds, "unsupported", "EOPNOTSUPP", "schedules are disabled", false);
        if (!Enum.TryParse<ClockBase>(clock, true, out var parsedClock))
            return new(false, null, s.Sequence, s.UtSeconds, "invalid", "EINVAL", "clock must be render, wall, or ut", false);
        var scheduled = new List<ScheduledCommand>(entries.Count);
        foreach (var entry in entries)
        {
            var command = entry.Command.ToCommand();
            if (!CommandCatalog.TryGet(command.Action, out var descriptor))
                return new(false, null, s.Sequence, s.UtSeconds, "invalid", "EINVAL", $"unknown action '{command.Action}'", false);
            scheduled.Add(new(entry.AtMs, CoalescingKey(command), command, descriptor.IsTrigger));
        }
        // This is input-limit enforcement, not result-size inspection. Count decoded JSON bytes once.
        var inputBytes = JsonSerializer.SerializeToUtf8Bytes(entries).Length;
        var build = ScheduleBuilder.Build(_schedules, new(id, group, parsedClock, rate, loop, inputBytes, scheduled));
        if (!build.IsValid)
            return new(false, null, s.Sequence, s.UtSeconds, "invalid", "EINVAL", build.Validation.Error, false);
        var assigned = ScheduleBuilder.Submit(_schedules, build);
        return McpEnvelope.Success(new { id = assigned, entries = entries.Count, clock = clock.ToLowerInvariant(), rate, loop }, s.Sequence, s.UtSeconds);
    }

    public async Task<McpEnvelope> CameraTrack(
        [Description("list, read, upload, delete, play, update, or stop")] string operation,
        [Description("Track name; required except for list and stop")] string? name = null,
        [Description("UTF-8 camera-track JSON chunk; required for upload")] string? json = null,
        [Description("Decoded UTF-8 byte offset for an upload chunk")] long offset = 0,
        [Description("Finalize and validate the upload after this chunk")] bool complete = true,
        [Description("Playback scalar used by play/update when applicable")] double value = 0,
        [Description("Fallback track/player name")] string? token = null,
        CancellationToken cancellationToken = default)
    {
        var s = _presenters.Current;
        if (_camera is null) return Unsupported("camera", s.Sequence, s.UtSeconds);
        if (operation.Equals("play", StringComparison.OrdinalIgnoreCase))
            return await _presenters.SubmitAsync(new(SimActions.CameraPlay, Token: name ?? token, Value: value), cancellationToken).ConfigureAwait(false);
        if (operation.Equals("update", StringComparison.OrdinalIgnoreCase))
            return await _presenters.SubmitAsync(new(SimActions.CameraSet, Token: name ?? token, Value: value), cancellationToken).ConfigureAwait(false);
        if (operation.Equals("stop", StringComparison.OrdinalIgnoreCase))
            return await _presenters.SubmitAsync(new(SimActions.CameraStop, Token: name ?? token, Value: value), cancellationToken).ConfigureAwait(false);
        try
        {
            object? data = operation.ToLowerInvariant() switch
            {
                "list" => _camera.List(),
                "read" when name is not null => ReadCamera(name),
                "upload" when name is not null && json is not null => UploadCamera(name, json, offset, complete),
                "delete" when name is not null => DeleteCamera(name),
                _ => throw new ArgumentException("operation must be list, read, upload, or delete with required fields"),
            };
            return McpEnvelope.Success(data, s.Sequence, s.UtSeconds);
        }
        catch (Exception ex) when (ex is ArgumentException or VfsErrorException)
        {
            return StoreFailure(ex, s.Sequence, s.UtSeconds);
        }
    }

    public CallToolResult AudioClip(
        [Description("list, retrieve, upload, or delete")] string operation,
        [Description("Clip name; required except for list")] string? name = null,
        [Description("Decoded byte offset for an upload chunk")] long offset = 0,
        [Description("Finalize the upload after this chunk")] bool complete = true,
        [Description("Base64-encoded upload bytes; required for upload")] string? data_base64 = null)
    {
        var s = _presenters.Current;
        if (_audio is null) return ToolResult(Unsupported("audio", s.Sequence, s.UtSeconds));
        try
        {
            if (operation.Equals("retrieve", StringComparison.OrdinalIgnoreCase) && name is not null)
            {
                if (!_audio.Exists(name))
                    throw new VfsErrorException(LinuxErrno.ENOENT, $"audio: no clip '{name}'");
                var bytes = _audio.SnapshotBytes(name);
                var mime = AudioMimeType(name);
                var envelope = McpEnvelope.Success(new { name, bytes = bytes.Length, mime_type = mime }, s.Sequence, s.UtSeconds);
                ContentBlock media = mime is not null
                    ? AudioContentBlock.FromBytes(bytes, mime)
                    : new EmbeddedResourceBlock
                    {
                        Resource = BlobResourceContents.FromBytes(bytes,
                            "gatos://audio/" + Uri.EscapeDataString(name), null),
                    };
                return ToolResult(envelope, media);
            }
            object? data = operation.ToLowerInvariant() switch
            {
                "list" => _audio.List(),
                "upload" when name is not null && data_base64 is not null => UploadAudio(name, offset, complete, data_base64),
                "delete" when name is not null => DeleteAudio(name),
                _ => throw new ArgumentException("operation must be list, retrieve, upload, or delete with required fields"),
            };
            return ToolResult(McpEnvelope.Success(data, s.Sequence, s.UtSeconds));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or VfsErrorException)
        {
            return ToolResult(StoreFailure(ex, s.Sequence, s.UtSeconds));
        }
    }

    private Task<McpEnvelope> Logical(string prefix, string operation, string vesselId, int ordinal, double value,
        IReadOnlyList<double>? values, string? token, string? aux, CancellationToken ct)
    {
        var action = MapAction(prefix, operation);
        return _presenters.SubmitAsync(new(action, vesselId, ordinal, value, values, token, aux), ct);
    }

    private static string MapAction(string prefix, string operation)
    {
        if (operation.Contains('.', StringComparison.Ordinal)) return operation;
        var op = operation.ToLowerInvariant();
        return (prefix, op) switch
        {
            ("vessel", "ignite") => SimActions.VesselIgnite,
            ("vessel", "shutdown") => SimActions.VesselShutdown,
            ("vessel", "engine_master") => SimActions.VesselEngine,
            ("vessel", "focus") => SimActions.CameraFocus,
            ("vessel", "take_control") => SimActions.DebugControlVessel,
            ("module", "engine_active") => SimActions.EngineActive,
            ("module", "engine_minimum_throttle" or "engine_min_throttle") => SimActions.EngineMinThrottle,
            ("module", "rcs_active") => SimActions.RcsActive,
            ("module", "light_on") => SimActions.LightOn,
            ("module", "light_brightness") => SimActions.LightBrightness,
            ("module", "light_color") => SimActions.LightColor,
            ("module", "light_outer_angle") => SimActions.LightOuterAngle,
            ("module", "light_inner_angle") => SimActions.LightInnerAngle,
            ("module", "animation_goal" or "solar_deployment") => SimActions.AnimationGoal,
            ("module", "undock") => SimActions.DockingUndock,
            ("module", "fire_decoupler") => SimActions.DecouplerFire,
            ("module", "pushoff") => SimActions.DebugDockingPushoff,
            ("camera", "ownership" or "take") => SimActions.CameraEnabled,
            ("camera", "geodetic") => SimActions.CameraGeo,
            ("camera", "reset") => SimActions.CameraPoseReset,
            ("audio", "update" or "pause" or "resume" or "seek") => SimActions.AudioSet,
            ("paint", _) => "paint." + op,
            ("debug.engineplume", "set") => SimActions.DebugEnginePlumeSet,
            ("debug.engineplume", "reset") => SimActions.DebugEnginePlumeReset,
            ("debug.plumetrail", "set") => SimActions.DebugPlumeTrailSet,
            ("debug.plumetrail", "reset") => SimActions.DebugPlumeTrailReset,
            ("debug.plumetrail", "clear") => SimActions.DebugPlumeTrailClear,
            ("debug.clouds", "set") => SimActions.DebugCloudsSet,
            ("debug.clouds", "reset") => SimActions.DebugCloudsReset,
            ("debug.terrain", "set") => SimActions.DebugTerrainSet,
            ("debug.terrain", "reset") => SimActions.DebugTerrainReset,
            _ => prefix + "." + op,
        };
    }

    private static string FxFamily(string family) => family.ToLowerInvariant() switch
    {
        "engine_plume" => "engineplume", "plume_trail" => "plumetrail", "clouds" => "clouds", "terrain" => "terrain",
        _ => family.ToLowerInvariant(),
    };
    private static string CoalescingKey(SimCommand c) => $"{c.Action}\u001f{c.VesselId}\u001f{c.Ordinal}";
    private object UploadCamera(string name, string json, long offset, bool complete) { var bytes = Encoding.UTF8.GetBytes(json); _camera!.HttpUpload(name, offset, bytes, complete); return new { name, bytes = bytes.Length, complete }; }
    private object ReadCamera(string name) { if (!_camera!.Exists(name)) throw new VfsErrorException(LinuxErrno.ENOENT, $"camera: no track '{name}'"); return new { name, json = Encoding.UTF8.GetString(_camera.SnapshotBytes(name)) }; }
    private object DeleteCamera(string name) { _camera!.Delete(name); return new { name, deleted = true }; }
    private object UploadAudio(string name, long offset, bool complete, string base64) { var bytes = Convert.FromBase64String(base64); _audio!.HttpUpload(name, offset, bytes, complete); return new { name, bytes = bytes.Length, complete }; }
    private object DeleteAudio(string name) { _audio!.Delete(name); return new { name, deleted = true }; }
    private static string? AudioMimeType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".ogg" or ".oga" => "audio/ogg",
        ".flac" => "audio/flac", ".m4a" or ".mp4" => "audio/mp4", ".aac" => "audio/aac",
        _ => null,
    };
    private static CallToolResult ToolResult(McpEnvelope envelope, ContentBlock? media = null)
    {
        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = envelope.Ok ? "gatOS operation completed" : envelope.Message ?? envelope.Errno ?? "gatOS operation failed" },
        };
        if (media is not null) content.Add(media);
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(envelope, SimJson.Options),
            IsError = !envelope.Ok,
        };
    }
    private static McpEnvelope Unsupported(string feature, long seq, double ut) => new(false, null, seq, ut, "unsupported", "EOPNOTSUPP", $"{feature} is disabled", false);
    private static McpEnvelope StoreFailure(Exception ex, long seq, double ut) => new(false, null, seq, ut, "invalid", ex is VfsErrorException v ? v.Errno.ToString() : "EINVAL", ex.Message, false);
}
