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
    public McpEnvelope ListVessels(int limit = 50, string? cursor = null, bool? controlled = null, bool? controllable = null,
        bool? is_kitten = null, string? parent_body = null, string? situation = null)
        => _presenters.ListVessels(limit, cursor, controlled, controllable, is_kitten, parent_body, situation);
    public McpEnvelope GetVessel(string id, IReadOnlyList<string>? include = null) => _presenters.GetVessel(id, include);
    public McpEnvelope ListKittens(int limit = 50, string? cursor = null) => _presenters.ListKittens(limit, cursor);
    public McpEnvelope GetKitten(string id, IReadOnlyList<string>? include = null) => _presenters.GetVessel(id, include, true);
    public McpEnvelope GetRuntimeState(string feature) => _presenters.GetRuntimeState(feature);
    public McpEnvelope GetCapabilities() => _presenters.GetCapabilities();
    public Task<McpEnvelope> Wait(long? after_sequence = null, string? event_type = null, string? vessel_id = null,
        double? until_ut = null, int timeout_ms = 30_000, CancellationToken cancellationToken = default)
        => _presenters.WaitAsync(after_sequence, event_type, vessel_id, until_ut, timeout_ms, cancellationToken);

    public Task<McpEnvelope> Ignite(string vessel_id, CancellationToken cancellationToken = default)
        => _presenters.SubmitAsync(new(SimActions.VesselIgnite, vessel_id, Value: 1), cancellationToken);
    public Task<McpEnvelope> Shutdown(string vessel_id, CancellationToken cancellationToken = default)
        => _presenters.SubmitAsync(new(SimActions.VesselShutdown, vessel_id, Value: 1), cancellationToken);
    public Task<McpEnvelope> Stage(string vessel_id, CancellationToken cancellationToken = default)
        => _presenters.SubmitAsync(new(SimActions.VesselStage, vessel_id, Value: 1), cancellationToken);

    public Task<McpEnvelope> VesselControl(string operation, string vessel_id, int ordinal = -1, double value = 0,
        IReadOnlyList<double>? values = null, string? token = null, string? aux = null, CancellationToken cancellationToken = default)
        => Logical("vessel", operation, vessel_id, ordinal, value, values, token, aux, cancellationToken);
    public Task<McpEnvelope> ModuleControl(string operation, string vessel_id, int ordinal, double value = 0,
        IReadOnlyList<double>? values = null, string? token = null, string? aux = null, CancellationToken cancellationToken = default)
        => Logical("module", operation, vessel_id, ordinal, value, values, token, aux, cancellationToken);
    public Task<McpEnvelope> CameraControl(string operation, double value = 0, IReadOnlyList<double>? values = null,
        string? token = null, string? aux = null, CancellationToken cancellationToken = default)
        => Logical("camera", operation, "", -1, value, values, token, aux, cancellationToken);
    public Task<McpEnvelope> AudioControl(string operation, double value = 0, IReadOnlyList<double>? values = null,
        string? token = null, string? aux = null, CancellationToken cancellationToken = default)
        => Logical("audio", operation, "", -1, value, values, token, aux, cancellationToken);
    public Task<McpEnvelope> ScheduleControl(string operation, string? id = null, double value = 0, string? token = null,
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
    public Task<McpEnvelope> DebugControl(string operation, string vessel_id = "", int ordinal = -1, double value = 0,
        IReadOnlyList<double>? values = null, string? token = null, string? aux = null, CancellationToken cancellationToken = default)
        => Logical("debug", operation, vessel_id, ordinal, value, values, token, aux, cancellationToken);
    public Task<McpEnvelope> RenderFxControl(string family, string operation, string? entity = null, string? field = null,
        double value = 0, IReadOnlyList<double>? values = null, string? token = null, CancellationToken cancellationToken = default)
        => Logical("debug." + FxFamily(family), operation, "", -1, value, values, field ?? token, entity, cancellationToken);

    public Task<McpEnvelope> Command(string action, string vessel_id = "", int ordinal = -1, double value = 0,
        IReadOnlyList<double>? values = null, string? token = null, string? aux = null, CancellationToken cancellationToken = default)
        => _presenters.SubmitAsync(new(action, vessel_id, ordinal, value, values, token, aux), cancellationToken);

    public Task<McpEnvelope> ExecuteBatch(IReadOnlyList<McpCommandEnvelope> commands, CancellationToken cancellationToken = default)
        => _presenters.SubmitBatchAsync(commands, cancellationToken);

    public McpEnvelope ScheduleBatch(IReadOnlyList<McpScheduleEntry> entries, string? id = null, string? group = null,
        string clock = "wall", double rate = 1, bool loop = false)
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

    public async Task<McpEnvelope> CameraTrack(string operation, string? name = null, string? json = null,
        long offset = 0, bool complete = true, double value = 0, string? token = null,
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

    public CallToolResult AudioClip(string operation, string? name = null, long offset = 0, bool complete = true, string? data_base64 = null)
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
