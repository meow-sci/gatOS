using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Paint;
using gatOS.SimFs.Paint.Stickers;
using gatOS.SimFs.Commands;
using ModelContextProtocol.Protocol;

namespace gatOS.Mcp;

internal sealed class McpToolHandlers
{
    private readonly McpPresenters _presenters;
    private readonly AudioStore? _audio;
    private readonly TextureStore? _textures;
    private readonly StickerStore? _stickers;
    private readonly CameraStore? _camera;
    private readonly ScheduleStore? _schedules;

    internal McpToolHandlers(McpPresenters presenters, AudioStore? audio, CameraStore? camera, ScheduleStore? schedules, TextureStore? textures = null, StickerStore? stickers = null)
    {
        _presenters = presenters;
        _audio = audio;
        _camera = camera;
        _schedules = schedules;
        _textures = textures;
        _stickers = stickers;
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
    public McpEnvelope GetRuntimeState([Description("camera, schedules, audio, paint, paint_textures, paint_stickers, welds, thug_life, face_fx, iva, engine_plume, plume_trail, clouds, or terrain")] string feature) => _presenters.GetRuntimeState(feature);
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
        [Description("parts_enabled, blend, parts_clear, global_*, template_*, vessel_*, part_*, kittens_enabled, kittens_clear, kitten_shared_*, kitten_shared_material_*, kitten_*, kitten_material_*, texture_bind, texture_unbind, or texture_clear")] string operation,
        [Description("Raw vessel/EVA id for vessel, part, kitten, and kitten_material operations; empty for global/shared/texture operations")] string vessel_id = "",
        [Description("0|1 for enabled flags and clear triggers; for texture_bind, 0 = faithful (render the image as authored, the default) and 1 = raw (interpret it as a stock clutter texture would be)")] double value = 0,
        [Description("Normalized sRGB [r,g,b], each 0..1, for *_color operations")] IReadOnlyList<double>? color = null,
        [Description("Template id, part instance_id, semantic EVA material name, or — for texture_bind/texture_unbind — the stock clutter texture id from gatos.paint_texture(operation:\"catalog\")")] string? target = null,
        [Description("Uploaded image name for texture_bind, as uploaded through gatos.paint_texture")] string? file = null,
        CancellationToken cancellationToken = default)
        => Logical("paint", operation, vessel_id, -1, value, color, target, file, cancellationToken);

    // texture_bind carries its render mode in `value`: 0 = faithful (render the image as authored,
    // the default), 1 = raw (interpret it exactly as one of KSA's own clutter textures).

    public CallToolResult PaintTexture(
        [Description("list, catalog, bindings, retrieve, upload, or delete")] string operation,
        [Description("Uploaded image name; required except for list, catalog, and bindings")] string? name = null,
        [Description("Decoded byte offset for an upload chunk")] long offset = 0,
        [Description("Finalize the upload after this chunk")] bool complete = true,
        [Description("Base64-encoded image bytes; required for upload")] string? data_base64 = null)
    {
        var s = _presenters.Current;
        if (_textures is null)
            return ToolResult(Unsupported("custom clutter textures", s.Sequence, s.UtSeconds));
        try
        {
            if (operation.Equals("retrieve", StringComparison.OrdinalIgnoreCase) && name is not null)
            {
                if (!_textures.Exists(name))
                    throw new VfsErrorException(LinuxErrno.ENOENT, $"paint/textures: no file '{name}'");
                var bytes = _textures.SnapshotBytes(name);
                var envelope = McpEnvelope.Success(
                    new { name, bytes = bytes.Length }, s.Sequence, s.UtSeconds);
                ContentBlock media = new EmbeddedResourceBlock
                {
                    Resource = BlobResourceContents.FromBytes(bytes,
                        "gatos://paint/textures/" + Uri.EscapeDataString(name), null),
                };
                return ToolResult(envelope, media);
            }

            object? data = operation.ToLowerInvariant() switch
            {
                "list" => _textures.List(),
                "catalog" => _textures.Catalog,
                "bindings" => new { desired = _textures.Bindings, applied = _textures.Applied },
                "upload" when name is not null && data_base64 is not null
                    => UploadTexture(name, offset, complete, data_base64),
                "delete" when name is not null => DeleteTexture(name),
                _ => throw new ArgumentException(
                    "operation must be list, catalog, bindings, retrieve, upload, or delete with required fields"),
            };
            return ToolResult(McpEnvelope.Success(data, s.Sequence, s.UtSeconds));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or VfsErrorException)
        {
            return ToolResult(StoreFailure(ex, s.Sequence, s.UtSeconds));
        }
    }

    /// <summary>
    ///     <c>/sim/paint/stickers</c> as one operation-shaped tool. Every mutating operation authors
    ///     the same canonical <c>paint.sticker_*</c> command the 9p line grammars build, and every
    ///     argument is checked against <see cref="StickerRules"/> first so a bad call fails here with
    ///     an <c>EINVAL</c> envelope instead of reaching the game thread.
    /// </summary>
    public Task<McpEnvelope> PaintSticker(
        [Description("place, spray, set, remove, clear, list, or debug")] string operation,
        [Description("Uploaded image name from gatos.paint_texture; required by place and spray, and by set when re-pointing a sticker at another image")] string? image = null,
        [Description("place anchor frame: vessel (part-local metres) or body (geodetic degrees); inferred from vessel_id/body when omitted")] string? anchor = null,
        [Description("Raw vessel id for a vessel anchor")] string? vessel_id = null,
        [Description("Anchor part or sub-part instance_id, from gatos.get_vessel(include:[\"parts\"])")] long part_iid = 0,
        [Description("Vessel anchor position [x,y,z] in part-local metres")] IReadOnlyList<double>? position = null,
        [Description("Vessel anchor outward surface normal [x,y,z]; finite and non-zero")] IReadOnlyList<double>? normal = null,
        [Description("Raw celestial body id for a body anchor")] string? body = null,
        [Description("Body anchor geodetic latitude in degrees, -90..90")] double? lat = null,
        [Description("Body anchor geodetic longitude in degrees, -360..360")] double? lon = null,
        [Description("Body anchor compass heading in degrees; also the rotation payload for set")] double? heading = null,
        [Description("Vessel anchor roll about the normal in degrees; on spray it adds to the upright default; also the rotation payload for set")] double? roll = null,
        [Description("Decal width in metres, 0 < width <= 1000; defaults to 1")] double? width = null,
        [Description("Decal height in metres, 0 < height <= 1000; defaults to 1")] double? height = null,
        [Description("Projection box depth in metres, 0 < depth <= 100; defaults to 0.3 on a vessel anchor and 1 on a body anchor")] double? depth = null,
        [Description("Opacity 0..1; defaults to 1")] double? alpha = null,
        [Description("Exposure multiplier, 0 < brightness <= 8; defaults to 1")] double? brightness = null,
        [Description("spray aim: camera (the main camera's forward axis, headless-friendly) or cursor")] string aim = "camera",
        [Description("spray ray length in metres, 0 < range <= 1e6; defaults to 2000")] double? range = null,
        [Description("Sticker id from gatos.paint_sticker(operation:\"list\"); required by set and remove")] int id = -1,
        [Description("Flag 0|1: sticker visibility for set, the projection-box checker for debug")] double? value = null,
        CancellationToken cancellationToken = default)
    {
        var s = _presenters.Current;
        if (_stickers is null) return Task.FromResult(Unsupported("stickers", s.Sequence, s.UtSeconds));
        switch (operation.ToLowerInvariant())
        {
            case "list":
                return Task.FromResult(McpEnvelope.Success(
                    new { stickers = _stickers.Stickers, runtime = _stickers.Runtime, last = _stickers.Last },
                    s.Sequence, s.UtSeconds));
            case "clear":
                return _presenters.SubmitAsync(new(SimActions.PaintStickerClear, Value: 1), cancellationToken);
            case "debug":
                return _presenters.SubmitAsync(new(SimActions.PaintStickerDebug, Value: value ?? 1), cancellationToken);
            case "remove":
                return id < 0
                    ? StickerInvalid("remove needs the sticker id")
                    : _presenters.SubmitAsync(new(SimActions.PaintStickerRemove, Ordinal: id, Value: 1), cancellationToken);
            case "place":
                return PlaceSticker(image, anchor, vessel_id, part_iid, position, normal, body, lat, lon,
                    heading, roll, width, height, depth, alpha, brightness, cancellationToken);
            case "spray":
                return SpraySticker(image, aim, range, roll, width, height, depth, alpha, brightness,
                    cancellationToken);
            case "set":
                return SetSticker(id, image, heading, roll, width, height, depth, alpha, brightness, value,
                    cancellationToken);
            default:
                return StickerInvalid("operation must be place, spray, set, remove, clear, list, or debug");
        }
    }

    /// <summary>
    ///     Builds the 12-slot <c>paint.sticker_place</c> payload exactly as
    ///     <see cref="StickerCommands.ParsePlace"/> does: <c>Token</c> = image,
    ///     <c>Aux</c> = the anchor descriptor, <c>Values</c> = <c>[x y z, nx ny nz, rotation, w, h, d,
    ///     alpha, brightness]</c> (a body anchor puts <c>lat, lon, 0</c> in the position slots, leaves
    ///     the normal zero and reads the rotation slot as a heading).
    /// </summary>
    private Task<McpEnvelope> PlaceSticker(string? image, string? anchor, string? vesselId, long partIid,
        IReadOnlyList<double>? position, IReadOnlyList<double>? normal, string? body, double? lat, double? lon,
        double? heading, double? roll, double? width, double? height, double? depth, double? alpha,
        double? brightness, CancellationToken ct)
    {
        if (image is null || !StickerRules.IsValidImage(image))
            return StickerInvalid("place needs the name of an image uploaded through gatos.paint_texture");
        var kind = (anchor ?? (body is null ? StickerCommands.VesselAnchor : StickerCommands.BodyAnchor))
            .ToLowerInvariant();
        var values = new double[12];
        if (kind == StickerCommands.BodyAnchor)
        {
            if (body is null || !StickerRules.IsValidTarget(body))
                return StickerInvalid("a body anchor needs body=<celestial id>");
            if (lat is not { } latitude || !StickerRules.IsValidLatitude(latitude))
                return StickerInvalid("lat must be in [-90, 90] degrees");
            if (lon is not { } longitude || !StickerRules.IsValidLongitude(longitude))
                return StickerInvalid("lon must be in [-360, 360] degrees");
            values[0] = latitude;
            values[1] = longitude;
            if (!TryStickerTail(heading, width, height, depth, alpha, brightness,
                    StickerRules.DefaultDepthBody, values, 6, out var bodyError))
                return StickerInvalid(bodyError);
            return _presenters.SubmitAsync(
                new(SimActions.PaintStickerPlace, Values: values, Token: image,
                    Aux: $"{StickerCommands.BodyAnchor} {body}"), ct);
        }

        if (kind != StickerCommands.VesselAnchor)
            return StickerInvalid($"anchor must be '{StickerCommands.VesselAnchor}' or '{StickerCommands.BodyAnchor}'");
        if (vesselId is null || !StickerRules.IsValidTarget(vesselId))
            return StickerInvalid("a vessel anchor needs vessel_id=<vessel id>");
        if (partIid is < 0 or > uint.MaxValue)
            return StickerInvalid("part_iid must be a part or sub-part instance_id");
        if (position is not { Count: 3 } p || !StickerRules.IsValidPosition(p[0])
            || !StickerRules.IsValidPosition(p[1]) || !StickerRules.IsValidPosition(p[2]))
            return StickerInvalid("position must be three finite part-local metres [x,y,z]");
        if (normal is not { Count: 3 } n || !StickerRules.IsValidNormal(n[0], n[1], n[2]))
            return StickerInvalid("normal must be a finite, non-zero [x,y,z]");
        for (var i = 0; i < 3; i++)
        {
            values[i] = p[i];
            values[3 + i] = n[i];
        }

        if (!TryStickerTail(roll, width, height, depth, alpha, brightness,
                StickerRules.DefaultDepthVessel, values, 6, out var error))
            return StickerInvalid(error);
        return _presenters.SubmitAsync(
            new(SimActions.PaintStickerPlace, Values: values, Token: image,
                Aux: $"{StickerCommands.VesselAnchor} {vesselId} {partIid.ToString(CultureInfo.InvariantCulture)}"), ct);
    }

    /// <summary>
    ///     Builds the 7-slot <c>paint.sticker_spray</c> payload exactly as
    ///     <see cref="StickerCommands.ParseSpray"/> does: <c>Values</c> = <c>[range, roll, w, h, d,
    ///     alpha, brightness]</c> with <c>d</c> left at the <c>-1</c> sentinel when the caller passed
    ///     no depth, so the game side substitutes the anchor kind's default once the ray reports what
    ///     it hit.
    /// </summary>
    private Task<McpEnvelope> SpraySticker(string? image, string aim, double? range, double? roll, double? width,
        double? height, double? depth, double? alpha, double? brightness, CancellationToken ct)
    {
        if (image is null || !StickerRules.IsValidImage(image))
            return StickerInvalid("spray needs the name of an image uploaded through gatos.paint_texture");
        if (!StickerRules.TryParseAim(aim.ToLowerInvariant(), out var cursor))
            return StickerInvalid($"aim must be '{StickerRules.AimCamera}' or '{StickerRules.AimCursor}'");
        var rayLength = range ?? StickerRules.DefaultRange;
        if (!StickerRules.IsValidRange(rayLength))
            return StickerInvalid("range must be in (0, 1e6] metres");
        var values = new double[7];
        values[0] = rayLength;
        if (!TryStickerTail(roll, width, height, depth, alpha, brightness,
                StickerCommands.DepthUnset, values, 1, out var error))
            return StickerInvalid(error);
        return _presenters.SubmitAsync(
            new(SimActions.PaintStickerSpray, Values: values, Token: image,
                Aux: StickerRules.FormatAim(cursor)), ct);
    }

    /// <summary>
    ///     One sticker knob per call — the same one-file-one-action shape
    ///     <c>/sim/paint/stickers/&lt;id&gt;/</c> has, so exactly one <c>paint.sticker_*</c> action is
    ///     emitted and a caller can never smuggle two edits into one ambiguous envelope.
    /// </summary>
    private Task<McpEnvelope> SetSticker(int id, string? image, double? heading, double? roll, double? width,
        double? height, double? depth, double? alpha, double? brightness, double? value, CancellationToken ct)
    {
        if (id < 0)
            return StickerInvalid("set needs the sticker id from gatos.paint_sticker(operation:\"list\")");
        // roll and heading are two spellings of the same knob but two distinct parameters, so they
        // are counted separately — folding them first would let set(roll:…, heading:…) through and
        // silently drop one of them.
        var chosen = (width is not null || height is not null ? 1 : 0) + (depth is not null ? 1 : 0)
            + (roll is not null ? 1 : 0) + (heading is not null ? 1 : 0)
            + (alpha is not null ? 1 : 0) + (brightness is not null ? 1 : 0)
            + (image is not null ? 1 : 0) + (value is not null ? 1 : 0);
        var rotation = roll ?? heading;
        if (chosen != 1)
            return StickerInvalid("set takes the sticker id and exactly one of width+height, depth, "
                + "roll/heading, alpha, brightness, image, or value (0|1 visibility)");

        if (width is not null || height is not null)
            return width is not { } w || height is not { } h
                   || !StickerRules.IsValidWidth(w) || !StickerRules.IsValidHeight(h)
                ? StickerInvalid("size needs both width and height, each in (0, 1000] metres")
                : _presenters.SubmitAsync(
                    new(SimActions.PaintStickerSize, Ordinal: id, Values: new[] { w, h }), ct);
        if (depth is { } d)
            return StickerRules.IsValidDepth(d)
                ? _presenters.SubmitAsync(new(SimActions.PaintStickerDepth, Ordinal: id, Value: d), ct)
                : StickerInvalid("depth must be in (0, 100] metres");
        if (rotation is { } rot)
            return StickerRules.IsValidRotation(rot)
                ? _presenters.SubmitAsync(new(SimActions.PaintStickerRotation, Ordinal: id, Value: rot), ct)
                : StickerInvalid("rotation must be a finite number of degrees");
        if (alpha is { } a)
            return StickerRules.IsValidAlpha(a)
                ? _presenters.SubmitAsync(new(SimActions.PaintStickerAlpha, Ordinal: id, Value: a), ct)
                : StickerInvalid("alpha must be in [0, 1]");
        if (brightness is { } b)
            return StickerRules.IsValidBrightness(b)
                ? _presenters.SubmitAsync(new(SimActions.PaintStickerBrightness, Ordinal: id, Value: b), ct)
                : StickerInvalid("brightness must be in (0, 8]");
        if (image is { } name)
            return StickerRules.IsValidImage(name)
                ? _presenters.SubmitAsync(new(SimActions.PaintStickerImage, Ordinal: id, Token: name), ct)
                : StickerInvalid("image must name a file uploaded through gatos.paint_texture");
        return _presenters.SubmitAsync(
            new(SimActions.PaintStickerVisible, Ordinal: id, Value: value!.Value), ct);
    }

    /// <summary>Applies and validates the shared numeric tail: rotation, w, h, d, alpha, brightness.</summary>
    private static bool TryStickerTail(double? rotation, double? width, double? height, double? depth,
        double? alpha, double? brightness, double defaultDepth, double[] values, int start, out string error)
    {
        var rot = rotation ?? StickerRules.DefaultRotation;
        var w = width ?? StickerRules.DefaultWidth;
        var h = height ?? StickerRules.DefaultHeight;
        var d = depth ?? defaultDepth;
        var a = alpha ?? StickerRules.DefaultAlpha;
        var b = brightness ?? StickerRules.DefaultBrightness;
        error = "";
        if (!StickerRules.IsValidRotation(rot))
            error = "rotation must be a finite number of degrees";
        else if (!StickerRules.IsValidWidth(w) || !StickerRules.IsValidHeight(h))
            error = "width and height must each be in (0, 1000] metres";
        else if (!StickerRules.IsValidDepth(d) && d != StickerCommands.DepthUnset)
            error = "depth must be in (0, 100] metres";
        else if (!StickerRules.IsValidAlpha(a))
            error = "alpha must be in [0, 1]";
        else if (!StickerRules.IsValidBrightness(b))
            error = "brightness must be in (0, 8]";
        if (error.Length != 0)
            return false;
        values[start] = rot;
        values[start + 1] = w;
        values[start + 2] = h;
        values[start + 3] = d;
        values[start + 4] = a;
        values[start + 5] = b;
        return true;
    }

    private Task<McpEnvelope> StickerInvalid(string message)
    {
        var s = _presenters.Current;
        return Task.FromResult(new McpEnvelope(false, null, s.Sequence, s.UtSeconds, "invalid", "EINVAL",
            message, false));
    }

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
    private object UploadTexture(string name, long offset, bool complete, string base64) { var bytes = Convert.FromBase64String(base64); _textures!.HttpUpload(name, offset, bytes, complete); return new { name, bytes = bytes.Length, complete }; }

    private object DeleteTexture(string name) { _textures!.Delete(name); return new { name, deleted = true }; }

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
    private static McpEnvelope StoreFailure(Exception ex, long seq, double ut) => new(false, null, seq, ut, "invalid", ex is VfsErrorException v ? LinuxErrno.Name(v.Errno) : "EINVAL", ex.Message, false);
}
