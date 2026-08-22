using System.Text;
using gatOS.SimFs;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Paint;
using gatOS.SimFs.Paint.Stickers;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using gatOS.Paint;

namespace gatOS.Mcp;

/// <summary>Agent-oriented JSON presenters over immutable snapshots and existing game-free stores.</summary>
public sealed class McpPresenters
{
    public const int DefaultListLimit = 50;
    public const int MaximumListLimit = 1000;

    private readonly SnapshotStore _snapshots;
    private readonly ICommandSink? _commands;
    private readonly Func<string>? _transports;
    private readonly AudioStore? _audio;
    private readonly CameraStore? _camera;
    private readonly ScheduleStore? _schedules;
    private readonly PaintStore? _paint;
    private readonly TextureStore? _textures;
    private readonly StickerStore? _stickers;

    public McpPresenters(SnapshotStore snapshots, ICommandSink? commands = null,
        Func<string>? transports = null, AudioStore? audio = null, CameraStore? camera = null,
        ScheduleStore? schedules = null, PaintStore? paint = null, TextureStore? textures = null,
        StickerStore? stickers = null)
    {
        _snapshots = snapshots;
        _commands = commands;
        _transports = transports;
        _audio = audio;
        _camera = camera;
        _schedules = schedules;
        _paint = paint;
        _textures = textures;
        _stickers = stickers;
    }

    public SimSnapshot Current => _snapshots.Current;

    public McpEnvelope GetWorld(string detail = "summary")
    {
        var s = Current;
        if (detail.Equals("full", StringComparison.OrdinalIgnoreCase))
            return Ok(s, s);
        if (!detail.Equals("summary", StringComparison.OrdinalIgnoreCase))
            return Invalid("detail must be 'summary' or 'full'", s);

        return Ok(new
        {
            time = new { ut = s.UtSeconds, warp = s.WarpFactor, sim_dt = s.SimDtSeconds },
            system = s.System,
            active_vessel = s.ActiveVesselId,
            status = new
            {
                game_version = s.GameVersion,
                sample_rate_hz = s.SampleRateHz,
                accessors = s.Accessors,
                control = _commands?.ControlEnabled ?? false,
                debug = _commands?.DebugEnabled ?? false,
                transports = _transports?.Invoke(),
            },
            vessels = s.Vessels.Select(VesselSummary).ToArray(),
            kittens = s.Vessels.Where(v => v.IsKitten).Select(VesselSummary).ToArray(),
            celestials = s.Bodies.Select(BodySummary).ToArray(),
        }, s);
    }

    public McpEnvelope ListCelestials(int limit = DefaultListLimit, string? cursor = null)
    {
        var s = Current;
        return Page("celestials", s.Bodies.Select(BodySummary).ToArray(), limit, cursor, s);
    }

    public McpEnvelope GetCelestial(string id)
    {
        var s = Current;
        var body = s.Bodies.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));
        return body is null ? Missing($"no celestial '{id}'", s) : Ok(body, s);
    }

    public McpEnvelope ListVessels(int limit = DefaultListLimit, string? cursor = null,
        bool? controlled = null, bool? controllable = null, bool? isKitten = null,
        string? parentBody = null, string? situation = null)
    {
        var s = Current;
        IEnumerable<VesselSnapshot> vessels = s.Vessels;
        if (controlled is not null) vessels = vessels.Where(v => v.Controlled == controlled);
        if (controllable is not null) vessels = vessels.Where(v => v.Controllable == controllable);
        if (isKitten is not null) vessels = vessels.Where(v => v.IsKitten == isKitten);
        if (!string.IsNullOrEmpty(parentBody)) vessels = vessels.Where(v => string.Equals(v.ParentBodyName, parentBody, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(situation)) vessels = vessels.Where(v => string.Equals(v.Situation, situation, StringComparison.OrdinalIgnoreCase));
        var ordered = vessels.OrderBy(v => v.Id, StringComparer.Ordinal).Select(VesselSummary).ToArray();
        return Page("vessels", ordered, limit, cursor, s);
    }

    public McpEnvelope ListKittens(int limit = DefaultListLimit, string? cursor = null)
    {
        var s = Current;
        var kittens = s.Vessels.Where(v => v.IsKitten).OrderBy(v => v.Id, StringComparer.Ordinal)
            .Select(VesselSummary).ToArray();
        return Page("kittens", kittens, limit, cursor, s);
    }

    public McpEnvelope GetVessel(string id, IReadOnlyList<string>? include = null, bool requireKitten = false)
    {
        var s = Current;
        var resolved = id.Equals("active", StringComparison.OrdinalIgnoreCase) ? s.ActiveVesselId : id;
        var vessel = s.Vessels.FirstOrDefault(v => string.Equals(v.Id, resolved, StringComparison.Ordinal));
        if (vessel is null) return Missing($"no vessel '{id}'", s);
        if (requireKitten && !vessel.IsKitten) return Invalid($"vessel '{id}' is not a kitten", s);
        if (include is null || include.Count == 0 || include.Any(x => x.Equals("all", StringComparison.OrdinalIgnoreCase)))
            return Ok(vessel, s);

        var allowed = new HashSet<string>(
            ["flight", "orbit", "environment", "propulsion", "resources", "power", "control", "modules", "encounters", "parts", "paint"],
            StringComparer.OrdinalIgnoreCase);
        if (include.Any(x => !allowed.Contains(x))) return Invalid("include contains an unknown section", s);

        var data = new Dictionary<string, object?>
        {
            ["id"] = vessel.Id, ["name"] = vessel.Name, ["situation"] = vessel.Situation,
            ["parent_body_name"] = vessel.ParentBodyName, ["controlled"] = vessel.Controlled,
            ["controllable"] = vessel.Controllable, ["is_kitten"] = vessel.IsKitten,
        };
        foreach (var section in include.Distinct(StringComparer.OrdinalIgnoreCase))
            data[section.ToLowerInvariant()] = Section(vessel, section);
        return Ok(data, s);
    }

    public McpEnvelope GetRuntimeState(string feature)
    {
        var s = Current;
        object? data = feature.ToLowerInvariant() switch
        {
            "camera" => _camera is null ? null : new { status = _camera.Status, tracks = _camera.List(), limits = _camera.Limits },
            "schedules" => _schedules is null ? null : new { players = _schedules.Players.Select(Player).ToArray(), limits = _schedules.Limits },
            "audio" => _audio is null ? null : new { clips = _audio.List(), channels = _audio.Channels, limits = new { _audio.MaxClipBytes, _audio.MaxTotalBytes, _audio.MaxClips, _audio.MaxChannels } },
            "welds" => s.Welds,
            "thug_life" => s.ThugLife,
            "face_fx" => new { live = s.FaceFxLive },
            "iva" => s.Iva,
            "engine_plume" => s.FxEditors?.PlumeTemplates,
            "plume_trail" => s.FxEditors?.Trail,
            "clouds" => s.FxEditors?.CloudBodies,
            "terrain" => s.FxEditors is null ? null : new { bodies = s.FxEditors.TerrainBodies, global = s.FxEditors.TerrainGlobal },
            "paint" => _paint?.Current,
            "paint_textures" => _textures is null ? null : new
            {
                runtime = _textures.Runtime,
                bindings = _textures.Bindings,
                applied = _textures.Applied,
                clutter = _textures.Catalog,
                files = _textures.List(),
                revision = _textures.Revision,
                limits = new
                {
                    _textures.MaxFileBytes, _textures.MaxTotalBytes, _textures.MaxFiles,
                    _textures.MaxBindings, _textures.MaxDimension,
                },
            },
            // Stickers publish a live registry rather than a store of bytes: the array, the
            // subsystem health line, the last place/spray result, the global box-checker flag, and
            // the two configured limits the parsers and the renderer honour.
            "paint_stickers" => _stickers is null ? null : new
            {
                runtime = _stickers.Runtime,
                stickers = _stickers.Stickers,
                last = _stickers.Last,
                debug = _stickers.Debug,
                limits = new
                {
                    max_count = _stickers.MaxCount,
                    max_view_distance_m = _stickers.MaxViewDistanceMetres,
                },
            },
            _ => MissingSentinel.Value,
        };
        if (ReferenceEquals(data, MissingSentinel.Value)) return Invalid($"unknown runtime feature '{feature}'", s);
        if (data is null) return new McpEnvelope(false, null, s.Sequence, s.UtSeconds, "unsupported", "EOPNOTSUPP", $"feature '{feature}' is disabled", false);
        return Ok(data, s);
    }

    public McpEnvelope GetCapabilities()
    {
        var s = Current;
        return Ok(new
        {
            protocol = "mcp",
            lists = new { default_limit = DefaultListLimit, maximum_limit = MaximumListLimit },
            json = new { size_limit = (int?)null, truncation = false },
            control_enabled = _commands?.ControlEnabled ?? false,
            debug_enabled = _commands?.DebugEnabled ?? false,
            features = new { audio = _audio is not null, camera = _camera is not null, schedules = _schedules is not null, paint = _paint is not null, paint_textures = _textures is not null, paint_stickers = _stickers is not null },
            actions = CommandCatalog.All.Select(d => new
            {
                action = d.Action,
                summary = d.Summary,
                target = d.Target.ToString().ToLowerInvariant(),
                phase = d.Phase.ToString().ToLowerInvariant(),
                logical_tool = d.LogicalTool,
                gate = d.Gate,
                available = GateAvailable(d.Gate),
                trigger = d.IsTrigger,
                idempotent = d.IsIdempotent,
                argument_shape = d.ArgumentShape,
                units = d.Units,
                safety = d.Safety,
                coalescing_key = d.CoalescingKey,
            }).ToArray(),
            display = new { exposed = false, reason = "TTY-only visual Kitty graphics stream" },
        }, s);
    }

    public async Task<McpEnvelope> WaitAsync(long? afterSequence, string? eventType, string? vesselId,
        double? untilUt, int timeoutMs, CancellationToken ct)
    {
        if (timeoutMs is < 1 or > 120_000) return Invalid("timeout_ms must be 1..120000", Current);
        if (afterSequence is null && eventType is null && untilUt is null) return Invalid("one wait condition is required", Current);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        var sequence = afterSequence ?? Current.Sequence;
        try
        {
            while (true)
            {
                var s = Current;
                var ev = eventType is null ? null : s.NewEvents.FirstOrDefault(e =>
                    string.Equals(e.Type, eventType, StringComparison.Ordinal) &&
                    (vesselId is null || string.Equals(e.VesselId, vesselId, StringComparison.Ordinal)));
                if ((afterSequence is not null && s.Sequence > afterSequence) || (untilUt is not null && s.UtSeconds >= untilUt) || ev is not null)
                    return Ok(new { matched = ev is not null ? "event" : untilUt is not null && s.UtSeconds >= untilUt ? "ut" : "snapshot", @event = ev }, s);
                s = await _snapshots.WaitForNextAsync(sequence, timeout.Token).ConfigureAwait(false);
                sequence = s.Sequence;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var s = Current;
            return new McpEnvelope(false, null, s.Sequence, s.UtSeconds, "timed_out", "ETIMEDOUT", "wait timed out", true);
        }
    }

    public async Task<McpEnvelope> SubmitAsync(McpCommandEnvelope envelope, CancellationToken ct)
    {
        var s = Current;
        if (_commands is null) return new(false, null, s.Sequence, s.UtSeconds, "denied", "EACCES", "MCP is read-only", false);
        var command = envelope.ToCommand();
        var validation = CommandCatalog.Validate(command);
        if (!validation.IsValid)
            return Invalid(validation.Error ?? "invalid command", s);
        var result = await _commands.SubmitAsync(command, ct).ConfigureAwait(false);
        return result.IsSuccess ? Ok(new { accepted = 1, action = command.Action, phase = command.Phase.ToString().ToLowerInvariant() }, Current) : McpEnvelope.Failure(result, Current.Sequence, Current.UtSeconds);
    }

    public async Task<McpEnvelope> SubmitBatchAsync(IReadOnlyList<McpCommandEnvelope> envelopes, CancellationToken ct)
    {
        var s = Current;
        if (_commands is null) return new(false, null, s.Sequence, s.UtSeconds, "denied", "EACCES", "MCP is read-only", false);
        if (envelopes.Count is < 1 or > 64) return Invalid("commands must contain 1..64 entries", s);
        var commands = envelopes.Select(x => x.ToCommand()).ToArray();
        var build = CommandBatchBuilder.Build(commands);
        if (!build.IsValid)
            return Invalid(build.Validation.Error ?? "invalid batch", s);
        var batch = build.Batch!;
        var phase = batch.Phase;
        var result = await _commands.SubmitBatchAsync(batch.Commands, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { accepted = commands.Length, phase = phase.ToString().ToLowerInvariant(), outcome = "ok" }, Current)
            : McpEnvelope.Failure(result, Current.Sequence, Current.UtSeconds);
    }

    public string ResourceJson(string uri)
    {
        McpEnvelope result = uri switch
        {
            "gatos://world" => GetWorld(),
            "gatos://celestials" => ListCelestials(MaximumListLimit),
            "gatos://vessels" => ListVessels(MaximumListLimit),
            "gatos://kittens" => ListKittens(MaximumListLimit),
            "gatos://capabilities" => GetCapabilities(),
            _ when uri.StartsWith("gatos://celestials/", StringComparison.Ordinal) => GetCelestial(Uri.UnescapeDataString(uri[19..])),
            _ when uri.StartsWith("gatos://vessels/", StringComparison.Ordinal) => GetVessel(Uri.UnescapeDataString(uri[16..])),
            _ when uri.StartsWith("gatos://kittens/", StringComparison.Ordinal) => GetVessel(Uri.UnescapeDataString(uri[16..]), requireKitten: true),
            _ when uri.StartsWith("gatos://runtime/", StringComparison.Ordinal) => GetRuntimeState(Uri.UnescapeDataString(uri[16..])),
            _ => new(false, null, Current.Sequence, Current.UtSeconds, "not_found", "ENOENT", "resource not found", false),
        };
        return SimJson.Serialize(result);
    }

    private McpEnvelope Page<T>(string collection, IReadOnlyList<T> values, int limit, string? cursor, SimSnapshot s)
    {
        if (limit is < 1 or > MaximumListLimit) return Invalid($"limit must be 1..{MaximumListLimit}", s);
        if (!TryDecodeCursor(collection, cursor, out var offset) || offset > values.Count) return Invalid("invalid cursor", s);
        var items = values.Skip(offset).Take(limit).ToArray();
        var next = offset + items.Length < values.Count ? EncodeCursor(collection, offset + items.Length) : null;
        return Ok(new McpListPage<T>(items, next, limit), s);
    }

    private static string EncodeCursor(string collection, int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"g1:{collection}:{offset}"));
    private static bool TryDecodeCursor(string collection, string? cursor, out int offset)
    {
        offset = 0;
        if (cursor is null) return true;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return value.StartsWith($"g1:{collection}:", StringComparison.Ordinal) && int.TryParse(value[(collection.Length + 4)..], out offset) && offset >= 0;
        }
        catch (FormatException) { return false; }
    }

    private static object VesselSummary(VesselSnapshot v) => new { v.Id, v.Name, v.Situation, v.ParentBodyName, v.Controlled, v.Controllable, v.IsKitten };
    private static object BodySummary(BodySnapshot b) => new { b.Id, b.Class, b.ParentId };
    private static object Player(IPlaybackPlayer p) => new { p.Id, p.Kind, p.Group, clock = p.Clock.Base.ToString().ToLowerInvariant(), p.DurationMs, state = p.State.ToString().ToLowerInvariant(), p.PendingCount, p.Dropped, p.LastError };

    private object? Section(VesselSnapshot v, string name) => name.ToLowerInvariant() switch
    {
        "flight" => new { v.PositionCci, v.PositionEcl, v.VelocityCci, v.LatitudeDeg, v.LongitudeDeg, v.BarometricAltitude, v.RadarAltitude, v.OrbitalSpeed, v.SurfaceSpeed, v.InertialSpeed, v.AttitudeBody2Cci, v.BodyRatesRadS, v.Navball },
        "orbit" => v.Orbit,
        "environment" => v.Environment,
        "propulsion" => new { v.Engines, v.Srb, v.Rcs },
        "resources" => new { v.Tanks, v.MassTotal, v.MassDry, v.MassPropellant },
        "power" => new { v.BatteryChargeFraction, v.BatteryCapacityJoules, v.PowerProducedW, v.PowerConsumedW, v.Solar, v.Generators },
        "control" => new { v.Controlled, v.Controllable, v.ThrottleCmd, v.AttitudeMode, v.AttitudeFrame, v.RcsMode, v.RcsOn, v.TranslateCmd, v.RotateCmd, v.LightsMasterOn },
        "modules" => new { v.Engines, v.Tanks, v.Rcs, v.Solar, v.Generators, v.Lights, v.Docking, v.Decouplers, v.Animations, v.Srb },
        "encounters" => v.Encounters,
        "parts" => v.Parts,
        "paint" => _paint is null ? null : new
        {
            parts = _paint.Current.Vessels.TryGetValue(v.Id, out var vesselRule) ? vesselRule : PaintRule.Default,
            part_overrides = _paint.Current.Parts.Where(x => x.Key.VesselId == v.Id)
                .ToDictionary(x => x.Key.InstanceId, x => x.Value),
            kitten = v.IsKitten && _paint.Current.Kittens.TryGetValue(v.Id, out var kittenRule)
                ? kittenRule : PaintRule.Default,
            kitten_materials = _paint.Current.KittenMaterials.Where(x => x.Key.VesselId == v.Id)
                .ToDictionary(x => x.Key.MaterialName, x => x.Value),
        },
        _ => null,
    };

    private bool GateAvailable(string gate) => gate switch
    {
        "audio_enabled" => _audio is not null,
        "camera_enabled" => _camera is not null,
        "schedule_enabled" => _schedules is not null,
        "control_enabled + paint runtime master" => _paint is not null && (_commands?.ControlEnabled ?? false),
        "control_enabled + paint textures store" => _textures is not null && (_commands?.ControlEnabled ?? false),
        "control_enabled + paint stickers" => _stickers is not null && (_commands?.ControlEnabled ?? false),
        "debug_namespace" => _commands?.DebugEnabled ?? false,
        _ => _commands?.ControlEnabled ?? false,
    };

    private static McpEnvelope Ok(object? data, SimSnapshot s) => McpEnvelope.Success(data, s.Sequence, s.UtSeconds);
    private static McpEnvelope Invalid(string message, SimSnapshot s) => new(false, null, s.Sequence, s.UtSeconds, "invalid", "EINVAL", message, false);
    private static McpEnvelope Missing(string message, SimSnapshot s) => new(false, null, s.Sequence, s.UtSeconds, "not_found", "ENOENT", message, false);
    private sealed class MissingSentinel { internal static readonly MissingSentinel Value = new(); }
}
