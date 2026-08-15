using System.Collections.Concurrent;
using System.Globalization;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Display;
using gatOS.SimFs.Fx;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs;

/// <summary>
///     Builds the <c>/sim</c> VFS over a <see cref="SnapshotStore"/> (OS_PLAN.md T8.2):
///     <code>
///     /time/{ut,warp}
///     /vessels/active/…                  (dynamic alias of the active vessel's dir)
///     /vessels/by-id/&lt;id&gt;/
///         id name situation parent
///         position/{cci,lat,lon}  velocity/{orbital,surface,inertial}
///         attitude/{quat,rates}   altitude/{barometric,radar}
///         mass/{total,dry,propellant}
///         orbit/{apoapsis,periapsis,ecc,inc,sma,period}     (only while in orbit)
///         battery/charge                                    (only with a battery)
///         engines/&lt;n&gt;/{active,vac_thrust,isp}
///         tanks/&lt;resource&gt;/{amount,capacity}
///         stream
///     /events
///     /ctl/batch                         (atomic same-tick command groups; with a sink)
///     </code>
///     Every scalar file snapshots its value at open (<c>cache=none</c> makes consecutive
///     opens live); a vessel that vanished between walk and open answers ENOENT. Dynamic
///     nodes are transient objects, but their qids are interned by relative path so the same
///     logical file keeps a stable identity across snapshots.
/// </summary>
public static class SimFsTree
{
    /// <summary>
    ///     Accepted <c>ctl/attitude_mode</c> tokens: <c>manual</c> drops the flight computer to
    ///     manual attitude; any other token names a <c>FlightComputerAttitudeTrackTarget</c> and
    ///     puts it in auto-track. Canonical (enum) casing; matched case-insensitively.
    /// </summary>
    private static readonly string[] AttitudeModeTokens =
    [
        "manual", "Prograde", "Retrograde", "Normal", "AntiNormal", "RadialOut", "RadialIn",
        "Toward", "Away", "Antivel", "Align", "Forward", "Backward", "Up", "Down", "Ahead",
        "Behind", "Outward", "Inward", "PositiveDv", "NegativeDv", "Custom", "None",
    ];

    /// <summary>Accepted <c>ctl/attitude_frame</c> tokens (the <c>VehicleReferenceFrame</c> names).</summary>
    private static readonly string[] AttitudeFrameTokens =
        ["EclBody", "EnuBody", "Lvlh", "VlfBody", "BurnBody", "Dock"];

    /// <summary>
    ///     Accepted <c>ctl/rcs_mode</c> tokens (the <c>FlightComputerRCSMode</c> names) — the file twin
    ///     of the in-game <b>R</b> keybind. <c>Disabled</c> is a hard master cut-off: KSA zeroes the
    ///     manual thruster command flags outright, so <c>ctl/translate</c> and <c>ctl/rotate</c> do
    ///     nothing at all, and auto attitude holds lose RCS torque authority (only gimballed TVC
    ///     survives, and only while burning).
    /// </summary>
    private static readonly string[] RcsModeTokens = ["Enabled", "Disabled"];

    /// <summary>Builds the read-only <c>/sim</c> tree (no control surface, no status).</summary>
    public static VfsDirectory Build(SnapshotStore store) => Build(store, null, null);

    /// <summary>
    ///     Builds the <c>/sim</c> tree. When <paramref name="commands"/> is supplied the tree
    ///     gains its writable control surface (<c>ctl/</c>, writable <c>engines/&lt;n&gt;/active</c>,
    ///     <c>animations/</c>, <c>solar/</c>) and the <c>/sim/status/</c> integration-health tree
    ///     (KSA_GAME_INTEGRATION_PLAN Parts 4–5). With no sink the tree is purely read-only.
    /// </summary>
    /// <param name="store">The published-snapshot exchange the tree reads from.</param>
    /// <param name="commands">The command sink control files submit to; null = read-only tree.</param>
    /// <param name="transports">
    ///     Optional provider for <c>/sim/status/transports</c> (bound ports etc.); supplied by the
    ///     game mod, which alone knows the transport bindings.
    /// </param>
    /// <param name="display">
    ///     Optional screen-stream hub (STREAM_PLAN.md): when supplied the tree gains the
    ///     <c>/sim/display/</c> surface (the <c>enabled</c>/<c>fps</c>/<c>width</c>/<c>height</c>/
    ///     <c>encoding</c> control files and the binary <c>stream</c> feed). Supplied by the game mod,
    ///     which owns the render-thread capture.
    /// </param>
    /// <param name="audio">
    ///     Optional audio clip store (GATOS_CUSTOM_AUDIO_PLAN): when supplied the tree gains the
    ///     <c>/sim/audio/</c> surface — the writable <c>file/</c> clip directory, the
    ///     <c>play</c>/<c>set</c>/<c>stop</c> controls (with a command sink), and the
    ///     <c>status</c>/<c>info</c> reads. Null (audio disabled in config) removes the surface
    ///     entirely so the SPEC stays truthful.
    /// </param>
    /// <param name="schedules">
    ///     Optional live-player registry (plans/CAMERA_CONTROLS_PLAN.md §3): when supplied — and a
    ///     command sink is wired — <c>/sim/ctl</c> gains <c>timed_batch</c> and the
    ///     <c>schedules/</c> registry. Null (schedules disabled in config) removes both entirely so
    ///     the SPEC stays truthful.
    /// </param>
    /// <param name="camera">
    ///     Optional camera track store + live-state exchange (plans/CAMERA_CONTROLS_PLAN.md §4): when
    ///     supplied the tree gains the <c>/sim/camera/</c> surface — the writable <c>track/</c>
    ///     directory, the <c>status</c>/<c>info</c>/<c>playback</c> reads, and (with a command sink) the
    ///     whole ownership + pose + playback control surface. Null (camera disabled in config) removes
    ///     it entirely so the SPEC stays truthful.
    /// </param>
    public static VfsDirectory Build(SnapshotStore store, ICommandSink? commands, Func<string>? transports,
        DisplaySurface? display = null, AudioStore? audio = null, ScheduleStore? schedules = null,
        CameraStore? camera = null)
        => new Builder(store, commands, transports, display, audio, schedules, camera).BuildRoot();

    private sealed class Builder
    {
        private readonly SnapshotStore _store;
        private readonly ICommandSink? _commands;
        private readonly Func<string>? _transports;
        private readonly DisplaySurface? _display;
        private readonly AudioStore? _audio;
        private readonly ScheduleStore? _schedules;
        private readonly CameraStore? _camera;
        private readonly ConcurrentDictionary<string, ulong> _qids = new();
        private long _nextQid;

        // The finished root, captured so ctl/batch can resolve its lines' paths against the same
        // tree; assigned at the end of BuildRoot, read only lazily (at batch-write time).
        private VfsDirectory? _root;

        // Cached per-entity subtrees (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP1): the node
        // objects are snapshot-agnostic (their delegates read _store.Current at access time), so a
        // vessel/body dir — and everything under it — is built ONCE instead of materialized on
        // every walk. Presence (in listings and lookups) still tracks the live snapshot. Keyed by
        // (sanitized name, id) so a collision-suffix rename simply creates a sibling entry; stale
        // entries are swept when the cache outgrows the roster (SweepVesselDirs).
        private readonly ConcurrentDictionary<NodeKey, VfsDirectory> _vesselDirs = new();
        private readonly ConcurrentDictionary<NodeKey, VfsDirectory> _debugVesselDirs = new();
        private readonly ConcurrentDictionary<NodeKey, VfsDirectory> _bodyDirs = new();

        // Per-FX-entity subtrees (same GP1 discipline): an entity's field set is fixed by the
        // catalog plus the entity's layer/cloud-type shape, so its nodes are materialized once and
        // reused; a shape change moves the field count and rebuilds under a new key.
        private readonly ConcurrentDictionary<FxKey, VfsNode[]> _fxNodes = new();

        // Per-roster memo (GP1): the sanitized-name list + name/id indexes for the current
        // snapshot's vessel (and body) rosters, rebuilt only when the underlying list reference
        // changes — the old code re-sanitized every vessel id on every walk step through by-id.
        private volatile Roster<VesselSnapshot>? _vesselRoster;
        private volatile Roster<BodySnapshot>? _bodyRoster;

        private readonly record struct NodeKey(string Name, string Id);

        private readonly record struct FxKey(string Path, int FieldCount);

        private sealed class Roster<T>
            where T : class
        {
            public required IReadOnlyList<T> Source { get; init; }
            public required List<(string Name, T Item)> Sanitized { get; init; }
            public required Dictionary<string, T> ByName { get; init; }
            public required Dictionary<string, (string Name, T Item)> ById { get; init; }
        }

        internal Builder(SnapshotStore store, ICommandSink? commands, Func<string>? transports,
            DisplaySurface? display, AudioStore? audio, ScheduleStore? schedules, CameraStore? camera)
        {
            _store = store;
            _commands = commands;
            _transports = transports;
            _display = display;
            _audio = audio;
            _schedules = schedules;
            _camera = camera;
        }

        internal VfsDirectory BuildRoot()
        {
            // The active/by-id containers are singletons (only their delegates are dynamic).
            var activeDir = ActiveDir();
            var byIdDir = ByIdDir();
            var vesselsChildren = new VfsNode[] { activeDir, byIdDir };
            var children = new List<VfsNode>
            {
                TimeDir(),
                new DelegateDirectory("vessels", Qid("vessels"),
                    () => vesselsChildren,
                    name => name switch
                    {
                        "active" => activeDir,
                        "by-id" => byIdDir,
                        _ => null,
                    }),
                SystemDir(),
                BodiesDir(),
                new EventsFile("events", Qid("events"), _store),
            };

            // The integration-health tree rides with the control surface (G2): present whenever a
            // command sink is wired, regardless of whether writes are currently enabled.
            if (_commands is not null)
                children.Add(StatusDir());

            // The global control surface (/sim/ctl): controls that are not per-vessel — the atomic
            // batch, and (when a schedule store is wired) the timed batch + its player registry.
            // Their lines resolve against this very root, so they reach exactly the control files
            // (incl. debug/, when enabled) a direct write would.
            if (_commands is { } sink)
                children.Add(GlobalCtlDir(sink));

            // The /sim/debug cheat namespace (G-D2): only when a sink is wired and debug is enabled.
            if (_commands is { DebugEnabled: true })
                children.Add(DebugDir());

            // The screen stream (STREAM_PLAN.md): present whenever a display surface is wired.
            if (_display is not null)
                children.Add(DisplayDir());

            // Userland audio playback (GATOS_CUSTOM_AUDIO_PLAN): present whenever a store is wired.
            if (_audio is not null)
                children.Add(AudioDir());

            // The programmable camera (plans/CAMERA_CONTROLS_PLAN.md §4): present whenever a store is
            // wired ([camera] camera_enabled).
            if (_camera is not null)
                children.Add(CameraDir());

            var fixedChildren = children.ToArray();
            _root = new DelegateDirectory("/", Qid("/"), () => fixedChildren);
            return _root;
        }

        /// <summary>
        ///     <c>/sim/ctl</c>: the global (not per-vessel) control surface. <c>batch</c> is always
        ///     present; <c>timed_batch</c> + <c>schedules/</c> only when a schedule store is wired,
        ///     so a disabled scheduler leaves no trace of itself in the tree.
        /// </summary>
        private VfsDirectory GlobalCtlDir(ICommandSink sink)
        {
            var children = new List<VfsNode> { new BatchFile("batch", Qid("ctl/batch"), sink, () => _root!) };
            if (_schedules is { } schedules)
                children.AddRange(ScheduleTree.Nodes(sink, schedules, () => _root!, Qid));
            return DelegateDirectory.Fixed("ctl", Qid("ctl"), children.ToArray());
        }

        // ---- time (KSA_GAME_INTEGRATION_PLAN §4.2) ----------------------------------------

        private VfsDirectory TimeDir()
            => DelegateDirectory.Fixed("time", Qid("time"),
                Line("time/ut", "ut", () => Formats.Scalar(_store.Current.UtSeconds)),
                Line("time/warp", "warp", () => Formats.Scalar(_store.Current.WarpFactor)),
                Line("time/sim_dt", "sim_dt", () => Formats.Scalar(_store.Current.SimDtSeconds)),
                Line("time/warp_speeds", "warp_speeds",
                    () => string.Join(' ', _store.Current.WarpSpeeds.Select(Formats.Scalar))),
                Line("time/auto_warp", "auto_warp", () =>
                {
                    var s = _store.Current;
                    return s.AutoWarpActive ? $"1 {Formats.Scalar(s.AutoWarpTargetUt)}" : "0";
                }),
                new AlarmFile("alarm", Qid("time/alarm"), _store));

        // ---- system & bodies (KSA_GAME_INTEGRATION_PLAN §4.3) -----------------------------

        private VfsDirectory SystemDir()
            => DelegateDirectory.Fixed("system", Qid("system"),
                Line("system/name", "name", () => _store.Current.System?.Name ?? ""),
                Line("system/home", "home", () => _store.Current.System?.HomeBodyId ?? ""),
                Line("system/sun", "sun", () => _store.Current.System?.SunId ?? ""));

        private VfsDirectory BodiesDir()
            => new DelegateDirectory("bodies", Qid("bodies"),
                () =>
                {
                    var roster = Bodies(_store.Current);
                    var children = new VfsNode[roster.Sanitized.Count];
                    for (var i = 0; i < children.Length; i++)
                    {
                        var (name, body) = roster.Sanitized[i];
                        children[i] = BodyDir(name, body.Id);
                    }

                    return children;
                },
                name => Bodies(_store.Current).ByName.TryGetValue(name, out var body)
                    ? BodyDir(name, body.Id)
                    : null);

        private VfsDirectory BodyDir(string sanitized, string bodyId)
            => _bodyDirs.GetOrAdd(new NodeKey(sanitized, bodyId),
                static (key, self) => self.CreateBodyDir(key.Name, key.Id), this);

        /// <summary>
        ///     Builds a body's subtree ONCE (GP1): every child node reads the live snapshot at
        ///     access time, so only presence (orbit/atmosphere/ocean — constants per body in
        ///     practice) is re-checked per list/lookup.
        /// </summary>
        private VfsDirectory CreateBodyDir(string sanitized, string bodyId)
        {
            var p = $"bodies/{sanitized}";
            var always = new VfsNode[]
            {
                Line($"{p}/id", "id", () => Body(bodyId).Id),
                Line($"{p}/class", "class", () => Body(bodyId).Class),
                Line($"{p}/parent", "parent", () => Body(bodyId).ParentId ?? ""),
                Line($"{p}/children", "children", () => string.Join('\n', Body(bodyId).ChildIds)),
                Line($"{p}/mass", "mass", () => Formats.Scalar(Body(bodyId).Mass)),
                Line($"{p}/radius", "radius", () => Formats.Scalar(Body(bodyId).MeanRadius)),
                Line($"{p}/mu", "mu", () => Formats.Scalar(Body(bodyId).Mu)),
                Line($"{p}/soi", "soi", () => Formats.Scalar(Body(bodyId).SoiMeters)),
                Line($"{p}/rotation_rate", "rotation_rate",
                    () => Formats.Scalar(Body(bodyId).RotationRateRadS)),
                DelegateDirectory.Fixed("position", Qid($"{p}/position"),
                    Line($"{p}/position/ecl", "ecl", () => Formats.Vector(Body(bodyId).PositionEcl))),
                DelegateDirectory.Fixed("velocity", Qid($"{p}/velocity"),
                    Line($"{p}/velocity/ecl", "ecl", () => Formats.Vector(Body(bodyId).VelocityEcl))),
            };
            var orbit = BodyOrbitDir(p, bodyId);
            var atmosphere = AtmosphereDir(p, bodyId);
            var ocean = DelegateDirectory.Fixed("ocean", Qid($"{p}/ocean"),
                Line($"{p}/ocean/present", "present", () => "1"),
                Line($"{p}/ocean/density", "density",
                    () => Formats.Scalar(Body(bodyId).Ocean!.DensityKgM3)));
            // Move the main camera to this celestial (write 1), same action vessels' ctl/focus uses.
            var focus = _commands is { } sink
                ? new TriggerFile("focus", Qid($"{p}/focus"), sink,
                    new SimCommand(bodyId, "camera.focus", SimCommand.NoOrdinal, 1))
                : null;

            return new DelegateDirectory(sanitized, Qid(p),
                () =>
                {
                    var body = Body(bodyId);
                    var children = new List<VfsNode>(always.Length + 4);
                    children.AddRange(always);
                    if (body.Orbit is not null)
                        children.Add(orbit);
                    if (body.Atmosphere is not null)
                        children.Add(atmosphere);
                    if (body.Ocean is not null)
                        children.Add(ocean);
                    if (focus is not null)
                        children.Add(focus);
                    return children;
                },
                name =>
                {
                    var body = Body(bodyId); // ENOENT when the body vanished, like the old list scan
                    return name switch
                    {
                        "orbit" => body.Orbit is not null ? orbit : null,
                        "atmosphere" => body.Atmosphere is not null ? atmosphere : null,
                        "ocean" => body.Ocean is not null ? ocean : null,
                        "focus" => focus,
                        _ => FindByName(always, name),
                    };
                });
        }

        /// <summary>Linear scan over a small fixed child set (no dictionary needed at ≤ ~20 entries).</summary>
        private static VfsNode? FindByName(VfsNode[] nodes, string name)
        {
            foreach (var node in nodes)
                if (node.Name == name)
                    return node;
            return null;
        }

        private VfsDirectory BodyOrbitDir(string p, string bodyId)
            => DelegateDirectory.Fixed("orbit", Qid($"{p}/orbit"),
                Line($"{p}/orbit/apoapsis", "apoapsis", () => Formats.Scalar(BodyOrbit(bodyId).ApoapsisAltitude)),
                Line($"{p}/orbit/periapsis", "periapsis", () => Formats.Scalar(BodyOrbit(bodyId).PeriapsisAltitude)),
                Line($"{p}/orbit/ecc", "ecc", () => Formats.Scalar(BodyOrbit(bodyId).Eccentricity)),
                Line($"{p}/orbit/inc", "inc", () => Formats.Scalar(BodyOrbit(bodyId).InclinationDeg)),
                Line($"{p}/orbit/lan", "lan", () => Formats.Scalar(BodyOrbit(bodyId).LanDeg)),
                Line($"{p}/orbit/argpe", "argpe", () => Formats.Scalar(BodyOrbit(bodyId).ArgPeDeg)),
                Line($"{p}/orbit/sma", "sma", () => Formats.Scalar(BodyOrbit(bodyId).SmaMeters)),
                Line($"{p}/orbit/period", "period", () => Formats.Scalar(BodyOrbit(bodyId).PeriodSeconds)));

        private VfsDirectory AtmosphereDir(string p, string bodyId)
            => DelegateDirectory.Fixed("atmosphere", Qid($"{p}/atmosphere"),
                Line($"{p}/atmosphere/present", "present", () => "1"),
                Line($"{p}/atmosphere/height", "height",
                    () => Formats.Scalar(Body(bodyId).Atmosphere!.HeightM)),
                Line($"{p}/atmosphere/scale_height", "scale_height",
                    () => Formats.Scalar(Body(bodyId).Atmosphere!.ScaleHeightM)),
                Line($"{p}/atmosphere/sea_level_pressure", "sea_level_pressure",
                    () => Formats.Scalar(Body(bodyId).Atmosphere!.SeaLevelPressurePa)),
                Line($"{p}/atmosphere/sea_level_density", "sea_level_density",
                    () => Formats.Scalar(Body(bodyId).Atmosphere!.SeaLevelDensityKgM3)));

        private BodySnapshot Body(string bodyId)
            => Bodies(_store.Current).ById.TryGetValue(bodyId, out var entry)
                ? entry.Item
                : throw new VfsErrorException(LinuxErrno.ENOENT, $"body '{bodyId}' is gone");

        private OrbitSnapshot BodyOrbit(string bodyId)
            => Body(bodyId).Orbit
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"body '{bodyId}' has no orbit");

        // ---- status (integration health) -------------------------------------------------

        private VfsDirectory StatusDir()
            => DelegateDirectory.Fixed("status", Qid("status"),
                Line("status/game_version", "game_version",
                    () => _store.Current.GameVersion is { Length: > 0 } v ? v : "unknown"),
                Line("status/sampler", "sampler", () =>
                {
                    var rate = _store.Current.SampleRateHz;
                    return rate > 0 ? $"ok {Formats.Scalar(rate)}" : "idle";
                }),
                new SnapshotTextFile("accessors", Qid("status/accessors"), _store,
                    () => string.Concat(_store.Current.Accessors.Select(a => Formats.AccessorLine(a) + "\n"))),
                // LIVE (not snapshot-memoized): ports bind/unbind without a publish.
                LiveLine("status/transports", "transports", () => _transports?.Invoke() ?? "unknown"));

        // ---- display (the screen stream — STREAM_PLAN.md) --------------------------------

        private VfsDirectory DisplayDir()
        {
            var settings = _display!.Settings;
            return DelegateDirectory.Fixed("display", Qid("display"),
                DisplaySettingFile.Create("enabled", Qid("display/enabled"),
                    () => settings.Enabled ? "1" : "0",
                    tok => ApplyFlag(tok, v => settings.Enabled = v)),
                DisplaySettingFile.Create("fps", Qid("display/fps"),
                    () => settings.Fps.ToString(),
                    tok => ApplyInt(tok, v => settings.Fps = v)),
                DisplaySettingFile.Create("width", Qid("display/width"),
                    () => settings.Width.ToString(),
                    tok => ApplyInt(tok, v => settings.Width = v)),
                DisplaySettingFile.Create("height", Qid("display/height"),
                    () => settings.Height.ToString(),
                    tok => ApplyInt(tok, v => settings.Height = v)),
                DisplaySettingFile.Create("encoding", Qid("display/encoding"),
                    () => settings.Encoding.Token(),
                    tok =>
                    {
                        if (DisplayEncodings.Parse(tok) is not { } encoding)
                            return false;
                        settings.Encoding = encoding;
                        return true;
                    }),
                // LIVE (not snapshot-memoized): the settings mutate without a publish.
                LiveLine("display/format", "format",
                    () => $"{settings.Width}x{settings.Height}@{settings.Fps} {settings.Encoding.Token()}"),
                new DisplayStreamFile("stream", Qid("display/stream"), _display!));
        }

        // ---- audio (userland playback through the game's FMOD — GATOS_CUSTOM_AUDIO_PLAN) ----

        /// <summary>
        ///     The <c>/sim/audio</c> surface: the writable <c>file/</c> clip directory (upload with
        ///     <c>cat clip.mp3 &gt; file/&lt;name&gt;</c>, evict with <c>rm</c>), the
        ///     <c>play</c>/<c>set</c>/<c>stop</c> line controls (present only when a command sink is
        ///     wired — they actuate FMOD on the game thread), and the <c>status</c>/<c>info</c>
        ///     reads off the actuator-published snapshot (never game state).
        /// </summary>
        private VfsDirectory AudioDir()
        {
            var store = _audio!;
            var children = new List<VfsNode>
            {
                new AudioDirectory("file", Qid("audio/file"), store, Qid),
                new StaticTextFile("status", Qid("audio/status"),
                    () => string.Concat(store.Channels.Select(c => Formats.AudioChannelLine(c) + "\n"))),
                // LiveLine: clip usage changes on uploads/evictions, without a snapshot publish.
                LiveLine("audio/info", "info", () => AudioInfoLine(store)),
            };
            if (_commands is { } sink)
            {
                children.Add(LineControlFile.Create("play", Qid("audio/play"), sink,
                    () => "", AudioCommands.ParsePlay));
                children.Add(LineControlFile.Create("set", Qid("audio/set"), sink,
                    () => "", AudioCommands.ParseSet));
                children.Add(LineControlFile.Create("stop", Qid("audio/stop"), sink,
                    () => "", AudioCommands.ParseStop));
            }

            return DelegateDirectory.Fixed("audio", Qid("audio"), children.ToArray());
        }

        /// <summary>The <c>/sim/audio/info</c> summary line (store usage + caps + live channel count).</summary>
        private static string AudioInfoLine(AudioStore store)
        {
            var (clips, bytes) = store.Usage();
            return $"enabled=1 clips={clips} clips_max={store.MaxClips} "
                   + $"bytes={bytes.ToString(CultureInfo.InvariantCulture)} "
                   + $"bytes_max={store.MaxTotalBytes.ToString(CultureInfo.InvariantCulture)} "
                   + $"clip_bytes_max={store.MaxClipBytes.ToString(CultureInfo.InvariantCulture)} "
                   + $"channels={store.Channels.Count} channels_max={store.MaxChannels}";
        }

        // ---- camera (the programmable cinematic camera — plans/CAMERA_CONTROLS_PLAN.md §4) ----

        /// <summary>
        ///     The <c>/sim/camera</c> surface: ownership (<c>enabled</c>/<c>release</c>), the follow
        ///     controls, the fully-decomposed <c>pose/</c> channels (one leaf per knob, so every channel
        ///     the JSON track can animate is reachable from <c>ctl/batch</c>, <c>ctl/timed_batch</c>,
        ///     HTTP and MQTT too), the writable <c>track/</c> upload directory, and the three-verb
        ///     playback grammar mirroring <c>/sim/audio</c>.
        /// </summary>
        /// <remarks>
        ///     Every read here is <b>live</b> (never snapshot-memoized): the camera moves every rendered
        ///     frame, far faster than the telemetry publish cadence, and a stale pose read would make a
        ///     scrub-based preview loop unusable. Every read-back is the <i>composed effective</i> value
        ///     off the director-published <c>CameraStatus</c>, not the last thing written, so a client
        ///     that reconnects can resync (AGENTS.md §7).
        /// </remarks>
        private VfsDirectory CameraDir()
        {
            var store = _camera!;
            CameraStatus Status() => store.Status;

            var children = new List<VfsNode>
            {
                new StaticTextFile("status", Qid("camera/status"), () => CameraFormat.Status(Status())),
                LiveLine("camera/info", "info", () => CameraFormat.Info(store)),
                LiveLine("camera/target", "target", () => CameraFormat.FollowId(Status())),
                LiveLine("camera/playback", "playback", () => CameraFormat.Playback(Status())),
                // The upload/play diagnosis. A 9p clunk cannot carry an errno, so without this a guest
                // that `cp`s a malformed track has no way to read WHY it was rejected — the message
                // would exist only in the host log and in a later camera/play EINVAL.
                LiveLine("camera/last_error", "last_error", () => store.LastError),
                new CameraDirectory("track", Qid("camera/track"), store, Qid),
                FlagControl("camera/enabled", "enabled", "", CameraCommands.EnabledAction,
                    SimCommand.NoOrdinal, () => Formats.Flag(Status().Owned)),
                EnumControl("camera/mode", "mode", "", CameraCommands.ModeAction,
                    CameraRules.ModeTokens, () => CameraFormat.Mode(Status().Mode)),
                TokenControl("camera/follow", "follow", "", CameraCommands.FollowAction,
                    () => Status().Follow.ToString()),
                FlagControl("camera/tidal", "tidal", "", CameraCommands.TidalAction,
                    SimCommand.NoOrdinal, () => Formats.Flag(Status().Tidal)),
                // The map view's own knob. It sits beside mode/follow/tidal — the controls that drive
                // the GAME's camera — rather than under pose/, because it is not a composable channel:
                // it is a field of the game's map controller and only bites while the viewport is in
                // map mode. Its own directory so a second map knob does not need a new top-level name.
                DelegateDirectory.Fixed("map", Qid("camera/map"),
                    RangedControl("camera/map/scope", "scope", "", CameraCommands.MapScopeAction,
                        0, double.MaxValue, () => Formats.Scalar(Status().MapScope))),
                CameraPoseDir(store),
                LineControl("camera/play", "play", () => CameraFormat.Play(Status()),
                    CameraCommands.ParsePlay),
                LineControl("camera/set", "set", () => CameraFormat.Set(Status()),
                    CameraCommands.ParseSet),
            };

            // Triggers exist only with a sink: unlike a state control they have no value to read, so a
            // read-only twin would be a file that does nothing and says nothing.
            if (_commands is { } sink)
            {
                children.Add(new TriggerFile("release", Qid("camera/release"), sink,
                    new SimCommand("", CameraCommands.ReleaseAction, SimCommand.NoOrdinal, 1)));
                children.Add(new TriggerFile("stop", Qid("camera/stop"), sink,
                    new SimCommand("", CameraCommands.StopAction, SimCommand.NoOrdinal, 1)));
            }

            return DelegateDirectory.Fixed("camera", Qid("camera"), children.ToArray());
        }

        /// <summary>
        ///     <c>/sim/camera/pose</c>: one writable leaf per animatable channel, plus the two composite
        ///     conveniences (<c>aim</c>, <c>geo</c>) that set several at once. The granular leaves are the
        ///     point — they are what make the camera scriptable at frame rates and mirrored per-field
        ///     over HTTP/MQTT for free (AGENTS.md §7).
        /// </summary>
        private VfsDirectory CameraPoseDir(CameraStore store)
        {
            CameraPose Pose() => store.Status.Pose;
            var limits = store.Limits;

            var children = new List<VfsNode>
            {
                LineControl("camera/pose/position", "position", () => CameraFormat.Position(Pose()),
                    CameraCommands.ParsePosition),
                EnumControl("camera/pose/frame", "frame", "", CameraCommands.FrameAction,
                    CameraRules.FrameTokens, () => CameraFormat.Frame(Pose().Frame)),
                TokenControl("camera/pose/anchor", "anchor", "", CameraCommands.AnchorAction,
                    () => Pose().Anchor.ToString()),
                LineControl("camera/pose/geo", "geo", () => CameraFormat.Geo(Pose()),
                    CameraCommands.ParseGeo),
                DelegateDirectory.Fixed("orbit", Qid("camera/pose/orbit"),
                    RangedControl("camera/pose/orbit/radius", "radius", "",
                        CameraCommands.OrbitRadiusAction, 0, double.MaxValue,
                        () => Formats.Scalar(Pose().OrbitRadius)),
                    NumberControl("camera/pose/orbit/azimuth", "azimuth", "",
                        CameraCommands.OrbitAzimuthAction, SimCommand.NoOrdinal,
                        () => Formats.Scalar(Pose().OrbitAzimuth)),
                    RangedControl("camera/pose/orbit/elevation", "elevation", "",
                        CameraCommands.OrbitElevationAction, -90, 90,
                        () => Formats.Scalar(Pose().OrbitElevation))),
                // Wire-identical to a 4-arity vector control; its own parser exists so a degenerate
                // (zero-norm) quaternion fails the write instead of silently becoming identity.
                LineControl("camera/pose/rotation", "rotation",
                    () => Formats.Quat(Pose().Rotation.ToSnapshot()), CameraCommands.ParseRotation),
                LineControl("camera/pose/aim", "aim", () => CameraFormat.Aim(Pose()),
                    CameraCommands.ParseAim),
                TokenControl("camera/pose/aim_target", "aim_target", "", CameraCommands.AimTargetAction,
                    () => Pose().AimTarget.ToString()),
                VectorControl("camera/pose/aim_offset", "aim_offset", "", CameraCommands.AimOffsetAction,
                    SimCommand.NoOrdinal, 3, () => Formats.Vector(Pose().AimOffset.ToSnapshot())),
                EnumControl("camera/pose/aim_frame", "aim_frame", "", CameraCommands.AimFrameAction,
                    CameraRules.FrameTokens, () => CameraFormat.Frame(Pose().AimFrame)),
                EnumControl("camera/pose/aim_up", "aim_up", "", CameraCommands.AimUpAction,
                    CameraRules.AimUpTokens, () => CameraFormat.Up(Pose().AimUp)),
                NumberControl("camera/pose/roll", "roll", "", CameraCommands.RollAction,
                    SimCommand.NoOrdinal, () => Formats.Scalar(Pose().Roll)),
                // The FOV bounds come from [camera] and are deliberately wider than the game's own
                // 15–120: SetFieldOfView does not clamp, so fisheye and telephoto really are available.
                RangedControl("camera/pose/fov", "fov", "", CameraCommands.FovAction,
                    limits.FovMin, limits.FovMax, () => Formats.Scalar(Pose().Fov)),
                FlagControl("camera/pose/ortho", "ortho", "", CameraCommands.OrthoAction,
                    SimCommand.NoOrdinal, () => Formats.Flag(Pose().Ortho)),
                // Strictly positive: a zero-height orthographic frustum has no volume at all.
                RangedControl("camera/pose/ortho_height", "ortho_height", "",
                    CameraCommands.OrthoHeightAction, double.Epsilon, double.MaxValue,
                    () => Formats.Scalar(Pose().OrthoHeight)),
                RangedControl("camera/pose/smoothing", "smoothing", "", CameraCommands.SmoothingAction,
                    0, CameraRules.MaxSmoothingSeconds, () => Formats.Scalar(Pose().Smoothing)),
            };

            if (_commands is { } sink)
                children.Add(new TriggerFile("reset", Qid("camera/pose/reset"), sink,
                    new SimCommand("", CameraCommands.PoseResetAction, SimCommand.NoOrdinal, 1)));

            return DelegateDirectory.Fixed("pose", Qid("camera/pose"), children.ToArray());
        }

        /// <summary>Applies a <c>0</c>/<c>1</c> (or true/false/on/off) token; false = EINVAL.</summary>
        private static bool ApplyFlag(string token, Action<bool> set)
        {
            switch (token.ToLowerInvariant())
            {
                case "1" or "true" or "on":
                    set(true);
                    return true;
                case "0" or "false" or "off":
                    set(false);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Applies an integer token (the setter clamps the range); false = EINVAL.</summary>
        private static bool ApplyInt(string token, Action<int> set)
        {
            if (!int.TryParse(token, out var value))
                return false;
            set(value);
            return true;
        }

        // ---- vessels ---------------------------------------------------------------------

        private VfsDirectory ByIdDir()
            => new DelegateDirectory("by-id", Qid("vessels/by-id"),
                () =>
                {
                    var roster = Vessels(_store.Current);
                    SweepVesselDirs(roster);
                    var children = new VfsNode[roster.Sanitized.Count];
                    for (var i = 0; i < children.Length; i++)
                    {
                        var (name, vessel) = roster.Sanitized[i];
                        children[i] = VesselDir(name, vessel.Id);
                    }

                    return children;
                },
                name => Vessels(_store.Current).ByName.TryGetValue(name, out var vessel)
                    ? VesselDir(name, vessel.Id)
                    : null);

        /// <summary>
        ///     Evicts cached subtrees for vessels that no longer exist. Amortized: runs only when
        ///     the cache has outgrown the live roster (debris churn over a long session), from the
        ///     by-id listing path.
        /// </summary>
        private void SweepVesselDirs(Roster<VesselSnapshot> roster)
        {
            if (_vesselDirs.Count <= 2 * roster.Sanitized.Count + 16)
                return;
            foreach (var key in _vesselDirs.Keys)
                if (!roster.ById.ContainsKey(key.Id))
                    _vesselDirs.TryRemove(key, out _);
            foreach (var key in _debugVesselDirs.Keys)
                if (!roster.ById.ContainsKey(key.Id))
                    _debugVesselDirs.TryRemove(key, out _);
        }

        /// <summary>
        ///     The <c>active</c> alias: its own directory qid, but it lists/resolves the
        ///     active vessel's children directly, so <c>active/…</c> and <c>by-id/…</c> walk
        ///     to identical qids (the plan's "alias, NOT a symlink").
        /// </summary>
        private VfsDirectory ActiveDir()
            => new DelegateDirectory("active", Qid("vessels/active"),
                () => ResolveActive()?.List() ?? [],
                name => ResolveActive()?.Lookup(name));

        private VfsDirectory? ResolveActive()
        {
            var snapshot = _store.Current;
            if (snapshot.ActiveVesselId is not { } activeId)
                return null;
            return Vessels(snapshot).ById.TryGetValue(activeId, out var entry)
                ? VesselDir(entry.Name, activeId)
                : null;
        }

        private VfsDirectory VesselDir(string sanitized, string vesselId)
            => _vesselDirs.GetOrAdd(new NodeKey(sanitized, vesselId),
                static (key, self) => self.CreateVesselDir(key.Name, key.Id), this);

        /// <summary>
        ///     Builds a vessel's whole subtree ONCE (GP1): the pre-GP1 tree re-materialized all
        ///     ~60–100 child nodes on <b>every walk step</b> into the vessel. Node content always
        ///     reads the live snapshot; only presence (orbit/battery/module dirs coming and going)
        ///     is re-evaluated per list/lookup, in the exact pre-GP1 listing order.
        /// </summary>
        private VfsDirectory CreateVesselDir(string sanitized, string vesselId)
        {
            var p = $"vessels/by-id/{sanitized}";
            var prefix = new VfsNode[]
            {
                    Line($"{p}/id", "id", () => Vessel(vesselId).Id),
                    Line($"{p}/name", "name", () => Vessel(vesselId).Name),
                    Line($"{p}/situation", "situation", () => Vessel(vesselId).Situation),
                    Line($"{p}/parent", "parent", () => Vessel(vesselId).ParentBodyName ?? ""),
                    Line($"{p}/controlled", "controlled", () => Formats.Flag(Vessel(vesselId).Controlled)),
                    Line($"{p}/controllable", "controllable", () => Formats.Flag(Vessel(vesselId).Controllable)),
                    Line($"{p}/is_kitten", "is_kitten", () => Formats.Flag(Vessel(vesselId).IsKitten)),
                    Line($"{p}/com", "com", () => Formats.Vector(Vessel(vesselId).CenterOfMass)),
                    // Model scale factor. Intentionally a first-class vessel node (NOT under
                    // /sim/debug): the first per-vessel control deliberately moved out of the debug
                    // namespace. Read = current (best-effort); write any value > 0 to rescale the
                    // whole model (action vessel.scale, one-shot, exempt from the authority gate).
                    NumberControl($"{p}/scale", "scale", vesselId, "vessel.scale", SimCommand.NoOrdinal,
                        () => Formats.Scalar(Vessel(vesselId).Scale)),
                    // Render-distance override, likewise a first-class vessel node (NOT under
                    // /sim/debug). Read = current mark; write 1 to keep this vessel rendered at any
                    // distance (bypasses KSA's sub-pixel cull), 0 to restore the stock cull (action
                    // vessel.always_render, exempt from the authority gate).
                    FlagControl($"{p}/always_render", "always_render", vesselId, "vessel.always_render",
                        SimCommand.NoOrdinal, () => Formats.Flag(Vessel(vesselId).AlwaysRender)),
                    // Byte-provider form (GP1): the JSON doc is built as UTF-8 once per snapshot —
                    // the old string form built bytes, decoded, concatenated "\n", re-encoded.
                    new SnapshotTextFile("telemetry", Qid($"{p}/telemetry"), _store,
                        () => Formats.VesselTelemetryLine(_store.Current, Vessel(vesselId))),
                    DelegateDirectory.Fixed("position", Qid($"{p}/position"),
                        Line($"{p}/position/cci", "cci", () => Formats.Vector(Vessel(vesselId).PositionCci)),
                        Line($"{p}/position/ecl", "ecl", () => Formats.Vector(Vessel(vesselId).PositionEcl)),
                        Line($"{p}/position/lat", "lat", () => Formats.Scalar(Vessel(vesselId).LatitudeDeg)),
                        Line($"{p}/position/lon", "lon", () => Formats.Scalar(Vessel(vesselId).LongitudeDeg))),
                    DelegateDirectory.Fixed("velocity", Qid($"{p}/velocity"),
                        Line($"{p}/velocity/orbital", "orbital", () => Formats.Scalar(Vessel(vesselId).OrbitalSpeed)),
                        Line($"{p}/velocity/surface", "surface", () => Formats.Scalar(Vessel(vesselId).SurfaceSpeed)),
                        Line($"{p}/velocity/inertial", "inertial", () => Formats.Scalar(Vessel(vesselId).InertialSpeed)),
                        Line($"{p}/velocity/cci", "cci", () => Formats.Vector(Vessel(vesselId).VelocityCci))),
                    DelegateDirectory.Fixed("attitude", Qid($"{p}/attitude"),
                        Line($"{p}/attitude/quat", "quat", () => Formats.Quat(Vessel(vesselId).AttitudeBody2Cci)),
                        Line($"{p}/attitude/rates", "rates", () => Formats.Vector(Vessel(vesselId).BodyRatesRadS))),
                    DelegateDirectory.Fixed("altitude", Qid($"{p}/altitude"),
                        Line($"{p}/altitude/barometric", "barometric",
                            () => Formats.Scalar(Vessel(vesselId).BarometricAltitude)),
                        Line($"{p}/altitude/radar", "radar", () => Formats.Scalar(Vessel(vesselId).RadarAltitude))),
                    DelegateDirectory.Fixed("mass", Qid($"{p}/mass"),
                        Line($"{p}/mass/total", "total", () => Formats.Scalar(Vessel(vesselId).MassTotal)),
                        Line($"{p}/mass/dry", "dry", () => Formats.Scalar(Vessel(vesselId).MassDry)),
                        Line($"{p}/mass/propellant", "propellant",
                            () => Formats.Scalar(Vessel(vesselId).MassPropellant))),
            };

            // Conditional subtrees, built once; presence tracks the live snapshot per list/lookup.
            var orbit = OrbitDir(p, vesselId);
            var navball = NavballDir(p, vesselId);
            var environment = EnvironmentDir(p, vesselId);
            var battery = BatteryDir(p, vesselId);
            var power = PowerDir(p, vesselId);
            var engines = EnginesDir(p, vesselId);
            var tanks = TanksDir(p, vesselId);
            var rcs = RcsDir(p, vesselId);
            var solar = SolarDir(p, vesselId);
            var generators = GeneratorsDir(p, vesselId);
            var lights = LightsDir(p, vesselId);
            var docking = DockingDir(p, vesselId);
            var decouplers = DecouplersDir(p, vesselId);
            var srb = SrbDir(p, vesselId);
            var encounters = new SnapshotTextFile("encounters", Qid($"{p}/encounters"), _store,
                () => string.Concat(Vessel(vesselId).Encounters
                    .Select(e => Formats.EncounterLine(e) + "\n")));
            var animations = AnimationsDir(p, vesselId);
            // Parts + subparts (the welds anchor picker). Present only when the parts stream is on
            // (telemetry_vessel_parts gates the reader, so the list is empty when off → no dir).
            var parts = PartsDir(p, vesselId);
            var stream = new StreamFile("stream", Qid($"{p}/stream"), _store, vesselId);
            // The vessel control surface (G1/G4): only when a command sink is wired. Per-module
            // controls (engine active, rcs active, light state, decoupler fire, solar/animation
            // goal) live inside their own read dirs above and light up the same way.
            var ctl = _commands is not null ? CtlDir(p, vesselId) : null;

            return new DelegateDirectory(sanitized, Qid(p),
                () =>
                {
                    var vessel = Vessel(vesselId);
                    var children = new List<VfsNode>(prefix.Length + 19);
                    children.AddRange(prefix);
                    if (vessel.Orbit is not null)
                        children.Add(orbit);
                    if (vessel.Navball is not null)
                        children.Add(navball);
                    if (vessel.Environment is not null)
                        children.Add(environment);
                    if (vessel.BatteryChargeFraction is not null)
                        children.Add(battery);
                    children.Add(power);
                    children.Add(engines);
                    children.Add(tanks);
                    if (vessel.Rcs.Count > 0)
                        children.Add(rcs);
                    if (vessel.Solar.Count > 0)
                        children.Add(solar);
                    if (vessel.Generators.Count > 0)
                        children.Add(generators);
                    if (vessel.Lights.Count > 0)
                        children.Add(lights);
                    if (vessel.Docking.Count > 0)
                        children.Add(docking);
                    if (vessel.Decouplers.Count > 0)
                        children.Add(decouplers);
                    if (vessel.Srb.Count > 0)
                        children.Add(srb);
                    if (vessel.Encounters.Count > 0)
                        children.Add(encounters);
                    if (vessel.Animations.Count > 0)
                        children.Add(animations);
                    if (vessel.Parts.Count > 0)
                        children.Add(parts);
                    children.Add(stream);
                    if (ctl is not null)
                        children.Add(ctl);

                    return children;
                },
                name =>
                {
                    var vessel = Vessel(vesselId); // ENOENT when the vessel is gone (as before)
                    return name switch
                    {
                        "orbit" => vessel.Orbit is not null ? orbit : null,
                        "navball" => vessel.Navball is not null ? navball : null,
                        "environment" => vessel.Environment is not null ? environment : null,
                        "battery" => vessel.BatteryChargeFraction is not null ? battery : null,
                        "power" => power,
                        "engines" => engines,
                        "tanks" => tanks,
                        "rcs" => vessel.Rcs.Count > 0 ? rcs : null,
                        "solar" => vessel.Solar.Count > 0 ? solar : null,
                        "generators" => vessel.Generators.Count > 0 ? generators : null,
                        "lights" => vessel.Lights.Count > 0 ? lights : null,
                        "docking" => vessel.Docking.Count > 0 ? docking : null,
                        "decouplers" => vessel.Decouplers.Count > 0 ? decouplers : null,
                        "srb" => vessel.Srb.Count > 0 ? srb : null,
                        "encounters" => vessel.Encounters.Count > 0 ? encounters : null,
                        "animations" => vessel.Animations.Count > 0 ? animations : null,
                        "parts" => vessel.Parts.Count > 0 ? parts : null,
                        "stream" => stream,
                        "ctl" => ctl,
                        _ => FindByName(prefix, name),
                    };
                });
        }

        private VfsDirectory OrbitDir(string p, string vesselId)
            => DelegateDirectory.Fixed("orbit", Qid($"{p}/orbit"),
                Line($"{p}/orbit/apoapsis", "apoapsis", () => Formats.Scalar(Orbit(vesselId).ApoapsisAltitude)),
                Line($"{p}/orbit/periapsis", "periapsis", () => Formats.Scalar(Orbit(vesselId).PeriapsisAltitude)),
                Line($"{p}/orbit/ecc", "ecc", () => Formats.Scalar(Orbit(vesselId).Eccentricity)),
                Line($"{p}/orbit/inc", "inc", () => Formats.Scalar(Orbit(vesselId).InclinationDeg)),
                Line($"{p}/orbit/lan", "lan", () => Formats.Scalar(Orbit(vesselId).LanDeg)),
                Line($"{p}/orbit/argpe", "argpe", () => Formats.Scalar(Orbit(vesselId).ArgPeDeg)),
                Line($"{p}/orbit/sma", "sma", () => Formats.Scalar(Orbit(vesselId).SmaMeters)),
                Line($"{p}/orbit/period", "period", () => Formats.Scalar(Orbit(vesselId).PeriodSeconds)),
                Line($"{p}/orbit/true_anomaly", "true_anomaly",
                    () => Formats.Scalar(Orbit(vesselId).TrueAnomalyDeg)),
                Line($"{p}/orbit/time_to_ap", "time_to_ap", () => Formats.Scalar(Orbit(vesselId).TimeToApoapsis)),
                Line($"{p}/orbit/time_to_pe", "time_to_pe", () => Formats.Scalar(Orbit(vesselId).TimeToPeriapsis)),
                Line($"{p}/orbit/next_patch", "next_patch", () => Formats.Scalar(Orbit(vesselId).NextPatchEventUt)));

        private VfsDirectory NavballDir(string p, string vesselId)
            => DelegateDirectory.Fixed("navball", Qid($"{p}/navball"),
                Line($"{p}/navball/pitch", "pitch", () => Navball(vesselId).PitchDeg.ToString()),
                Line($"{p}/navball/yaw", "yaw", () => Navball(vesselId).YawDeg.ToString()),
                Line($"{p}/navball/roll", "roll", () => Navball(vesselId).RollDeg.ToString()),
                Line($"{p}/navball/twr", "twr", () => Formats.Scalar(Navball(vesselId).ThrustWeightRatio)),
                Line($"{p}/navball/deltav", "deltav", () => Formats.Scalar(Navball(vesselId).DeltaVVacuumMs)),
                Line($"{p}/navball/frame", "frame", () => Navball(vesselId).Frame),
                Line($"{p}/navball/speed", "speed", () => Formats.Scalar(Navball(vesselId).SpeedMs)));

        private VfsDirectory EnvironmentDir(string p, string vesselId)
            => DelegateDirectory.Fixed("environment", Qid($"{p}/environment"),
                Line($"{p}/environment/pressure", "pressure", () => Formats.Scalar(Env(vesselId).PressurePa)),
                Line($"{p}/environment/density", "density", () => Formats.Scalar(Env(vesselId).DensityKgM3)),
                Line($"{p}/environment/dynamic_pressure", "dynamic_pressure",
                    () => Formats.Scalar(Env(vesselId).DynamicPressurePa)),
                Line($"{p}/environment/ocean_density", "ocean_density",
                    () => Formats.Scalar(Env(vesselId).OceanDensityKgM3)),
                Line($"{p}/environment/terrain_radius", "terrain_radius",
                    () => Formats.Scalar(Env(vesselId).TerrainRadiusM)),
                Line($"{p}/environment/accel", "accel", () => Formats.Vector(Env(vesselId).AccelBody)),
                Line($"{p}/environment/angular_accel", "angular_accel",
                    () => Formats.Vector(Env(vesselId).AngularAccelBody)),
                Line($"{p}/environment/g_force", "g_force", () => Formats.Scalar(Env(vesselId).GForce)));

        private VfsDirectory BatteryDir(string p, string vesselId)
            => DelegateDirectory.Fixed("battery", Qid($"{p}/battery"),
                Line($"{p}/battery/charge", "charge", () => Formats.Scalar(Battery(vesselId))),
                Line($"{p}/battery/fraction", "fraction", () => Formats.Scalar(Battery(vesselId))),
                Line($"{p}/battery/capacity", "capacity",
                    () => Formats.Scalar(Vessel(vesselId).BatteryCapacityJoules ?? 0)));

        private VfsDirectory PowerDir(string p, string vesselId)
            => DelegateDirectory.Fixed("power", Qid($"{p}/power"),
                Line($"{p}/power/produced", "produced", () => Formats.Scalar(Vessel(vesselId).PowerProducedW)),
                Line($"{p}/power/consumed", "consumed", () => Formats.Scalar(Vessel(vesselId).PowerConsumedW)));

        /// <summary>
        ///     An index-keyed collection directory (<c>engines/</c>, <c>rcs/</c>, <c>parts/</c>, …):
        ///     the child <c>&lt;n&gt;/</c> nodes are created once and cached (GP1 — they read the live
        ///     snapshot at access time); presence tracks the live count on every list/lookup. Module
        ///     indexes equal their list positions (the sampler invariant the event differ also leans
        ///     on), so the count check is the exact old <c>Any(x =&gt; x.Index == index)</c> test.
        ///     <paramref name="extra"/> nodes (non-numeric names, e.g. <c>parts/json</c>) are listed
        ///     after the indexed children and resolved by name.
        /// </summary>
        private VfsDirectory IndexedDir(string dirName, string qidPath, Func<int> count, Func<int, VfsNode> create,
            params VfsNode[] extra)
        {
            var cache = new ConcurrentDictionary<int, VfsNode>();
            return new DelegateDirectory(dirName, Qid(qidPath),
                () =>
                {
                    var n = count();
                    var children = new VfsNode[n + extra.Length];
                    for (var i = 0; i < n; i++)
                        children[i] = cache.GetOrAdd(i, create);
                    for (var i = 0; i < extra.Length; i++)
                        children[n + i] = extra[i];
                    return children;
                },
                name =>
                {
                    foreach (var node in extra)
                        if (node.Name == name)
                            return node;
                    return int.TryParse(name, out var index) && index >= 0 && index < count()
                        ? cache.GetOrAdd(index, create)
                        : null;
                });
        }

        private VfsDirectory EnginesDir(string p, string vesselId)
            => IndexedDir("engines", $"{p}/engines",
                () => Vessel(vesselId).Engines.Count,
                index => EngineDir(p, vesselId, index));

        // ---- solid rocket motors (SRBs) ---------------------------------------------------------
        // Read-only by construction: a lit solid cannot be throttled or shut down, so ignition stays
        // on the engine surface (ctl/ignite, engines/<n>/active, staging) — srb/<n>/engine names the
        // engines/ entry that lights this motor. Solid propellant is NOT a tank (KSA keeps it on a
        // separate grain-segment module), so this dir is the only place a booster's remaining
        // propellant and burn time are readable.

        private VfsDirectory SrbDir(string p, string vesselId)
            => IndexedDir("srb", $"{p}/srb",
                () => Vessel(vesselId).Srb.Count,
                index => SrbMotorDir(p, vesselId, index));

        private VfsDirectory SrbMotorDir(string p, string vesselId, int index)
        {
            var q = $"{p}/srb/{index}";
            return DelegateDirectory.Fixed($"{index}", Qid(q),
                Line($"{q}/engine", "engine",
                    () => Srb(vesselId, index).EngineIndex.ToString(CultureInfo.InvariantCulture)),
                Line($"{q}/part", "part", () => Formats.UInt(Srb(vesselId, index).PartInstanceId)),
                Line($"{q}/substance", "substance", () => Srb(vesselId, index).Substance),
                Line($"{q}/grain", "grain", () => Srb(vesselId, index).Grain),
                Line($"{q}/grain_shape", "grain_shape", () => Srb(vesselId, index).GrainShape),
                Line($"{q}/segment_count", "segment_count",
                    () => Srb(vesselId, index).Segments.Count.ToString(CultureInfo.InvariantCulture)),
                Line($"{q}/valid", "valid", () => Formats.Flag(Srb(vesselId, index).StackValid)),
                Line($"{q}/error", "error", () => Srb(vesselId, index).StackError),
                Line($"{q}/active", "active", () => Formats.Flag(Srb(vesselId, index).Active)),
                Line($"{q}/propellant", "propellant",
                    () => Formats.Flag(Srb(vesselId, index).PropellantAvailable)),
                Line($"{q}/mass", "mass", () => Formats.Scalar(Srb(vesselId, index).MassKg)),
                Line($"{q}/mass_initial", "mass_initial",
                    () => Formats.Scalar(Srb(vesselId, index).MassInitialKg)),
                Line($"{q}/mass_unburnable", "mass_unburnable",
                    () => Formats.Scalar(Srb(vesselId, index).MassUnburnableKg)),
                Line($"{q}/mass_burnable", "mass_burnable",
                    () => Formats.Scalar(Srb(vesselId, index).MassBurnableKg)),
                Line($"{q}/fraction", "fraction", () => Formats.Scalar(Srb(vesselId, index).Fraction)),
                Line($"{q}/mass_flow", "mass_flow", () => Formats.Scalar(Srb(vesselId, index).MassFlowKgS)),
                Line($"{q}/burn_time", "burn_time",
                    () => Formats.Scalar(Srb(vesselId, index).BurnTimeRemainingS)),
                Line($"{q}/burning_area", "burning_area",
                    () => Formats.Scalar(Srb(vesselId, index).BurningAreaM2)),
                Line($"{q}/chamber_pressure", "chamber_pressure",
                    () => Formats.Scalar(Srb(vesselId, index).ChamberPressurePa)),
                Line($"{q}/chamber_temp", "chamber_temp",
                    () => Formats.Scalar(Srb(vesselId, index).ChamberTemperatureK)),
                Line($"{q}/exit_pressure", "exit_pressure",
                    () => Formats.Scalar(Srb(vesselId, index).ExitPressurePa)),
                Line($"{q}/exit_temp", "exit_temp",
                    () => Formats.Scalar(Srb(vesselId, index).ExitTemperatureK)),
                Line($"{q}/area_ratio", "area_ratio", () => Formats.Scalar(Srb(vesselId, index).AreaRatio)),
                SrbSegmentsDir(p, vesselId, index));
        }

        private VfsDirectory SrbSegmentsDir(string p, string vesselId, int srbIndex)
            => IndexedDir("segments", $"{p}/srb/{srbIndex}/segments",
                () => Srb(vesselId, srbIndex).Segments.Count,
                index => SrbSegmentDir(p, vesselId, srbIndex, index));

        private VfsDirectory SrbSegmentDir(string p, string vesselId, int srbIndex, int index)
        {
            var q = $"{p}/srb/{srbIndex}/segments/{index}";
            return DelegateDirectory.Fixed($"{index}", Qid(q),
                Line($"{q}/part", "part",
                    () => Formats.UInt(SrbSegment(vesselId, srbIndex, index).PartInstanceId)),
                Line($"{q}/substance", "substance", () => SrbSegment(vesselId, srbIndex, index).Substance),
                Line($"{q}/grain", "grain", () => SrbSegment(vesselId, srbIndex, index).Grain),
                Line($"{q}/mass", "mass", () => Formats.Scalar(SrbSegment(vesselId, srbIndex, index).MassKg)),
                Line($"{q}/mass_initial", "mass_initial",
                    () => Formats.Scalar(SrbSegment(vesselId, srbIndex, index).MassInitialKg)),
                Line($"{q}/mass_unburnable", "mass_unburnable",
                    () => Formats.Scalar(SrbSegment(vesselId, srbIndex, index).MassUnburnableKg)),
                Line($"{q}/fraction", "fraction",
                    () => Formats.Scalar(SrbSegment(vesselId, srbIndex, index).Fraction)),
                Line($"{q}/radius", "radius", () => Formats.Scalar(SrbSegment(vesselId, srbIndex, index).RadiusM)),
                Line($"{q}/length", "length", () => Formats.Scalar(SrbSegment(vesselId, srbIndex, index).LengthM)),
                Line($"{q}/volume", "volume", () => Formats.Scalar(SrbSegment(vesselId, srbIndex, index).VolumeM3)),
                Line($"{q}/burn_depth", "burn_depth",
                    () => Formats.Scalar(SrbSegment(vesselId, srbIndex, index).BurnDepthM)));
        }

        private VfsDirectory EngineDir(string p, string vesselId, int index)
            => DelegateDirectory.Fixed($"{index}", Qid($"{p}/engines/{index}"),
                FlagControl($"{p}/engines/{index}/active", "active", vesselId, "engine.active", index,
                    () => Formats.Flag(Engine(vesselId, index).Active)),
                Line($"{p}/engines/{index}/vac_thrust", "vac_thrust",
                    () => Formats.Scalar(Engine(vesselId, index).VacThrustN)),
                Line($"{p}/engines/{index}/isp", "isp", () => Formats.Scalar(Engine(vesselId, index).IspS)),
                Line($"{p}/engines/{index}/throttle", "throttle",
                    () => Formats.Scalar(Engine(vesselId, index).ThrottleCmd)),
                Line($"{p}/engines/{index}/propellant", "propellant",
                    () => Formats.Flag(Engine(vesselId, index).PropellantAvailable)),
                FractionControl($"{p}/engines/{index}/min_throttle", "min_throttle", vesselId,
                    "engine.min_throttle", index, () => Formats.Scalar(Engine(vesselId, index).MinThrottle)));

        // ---- parts + subparts (the welds anchor picker; read-only, cached by the reader) ----------

        private VfsDirectory PartsDir(string p, string vesselId)
            => IndexedDir("parts", $"{p}/parts",
                () => Vessel(vesselId).Parts.Count,
                index => PartDir(p, vesselId, index),
                PartsJsonFile(p, vesselId));

        /// <summary>
        ///     <c>parts/json</c>: the whole part/subpart tree of the vessel as one JSON document (the
        ///     <see cref="SimJson"/> snake_case projection of the <see cref="PartSnapshot"/> list, nested
        ///     <c>subparts</c> included) — one <c>cat</c> + <c>jq</c> instead of a find-the-iid pipeline.
        ///     Serialization is memoized on the <b>list reference</b>: the sampler passes the reader's
        ///     cached list through unchanged, so the reference only swaps when the reader actually
        ///     rebuilds (part-count change or the 10 s backstop) — publishes in between reuse the string.
        /// </summary>
        private VfsFile PartsJsonFile(string p, string vesselId)
        {
            PartsJsonCache? cache = null;
            return Line($"{p}/parts/json", "json", () =>
            {
                var parts = Vessel(vesselId).Parts;
                var c = cache;
                if (c is null || !ReferenceEquals(c.List, parts))
                    cache = c = new PartsJsonCache(parts, SimJson.Serialize(parts));
                return c.Json;
            });
        }

        private sealed class PartsJsonCache(IReadOnlyList<PartSnapshot> list, string json)
        {
            public readonly IReadOnlyList<PartSnapshot> List = list;
            public readonly string Json = json;
        }

        private VfsDirectory PartDir(string p, string vesselId, int index)
            => DelegateDirectory.Fixed($"{index}", Qid($"{p}/parts/{index}"),
                // instance_id is the STABLE handle a weld anchors to (Part.Id can collide).
                Line($"{p}/parts/{index}/instance_id", "instance_id",
                    () => Formats.UInt(Part(vesselId, index).InstanceId)),
                Line($"{p}/parts/{index}/id", "id", () => Part(vesselId, index).Id),
                Line($"{p}/parts/{index}/display_name", "display_name",
                    () => Part(vesselId, index).DisplayName),
                Line($"{p}/parts/{index}/template", "template", () => Part(vesselId, index).Template),
                Line($"{p}/parts/{index}/is_root", "is_root",
                    () => Formats.Flag(Part(vesselId, index).IsRoot)),
                Line($"{p}/parts/{index}/subpart_count", "subpart_count",
                    () => Part(vesselId, index).SubpartCount.ToString(CultureInfo.InvariantCulture)),
                Line($"{p}/parts/{index}/position", "position",
                    () => Formats.Vector(Part(vesselId, index).PositionVehicleAsmb)),
                SubpartsDir(p, vesselId, index));

        private VfsDirectory SubpartsDir(string p, string vesselId, int partIndex)
            => IndexedDir("subparts", $"{p}/parts/{partIndex}/subparts",
                () => Part(vesselId, partIndex).Subparts.Count,
                index => SubpartDir(p, vesselId, partIndex, index));

        private VfsDirectory SubpartDir(string p, string vesselId, int partIndex, int index)
            => DelegateDirectory.Fixed($"{index}", Qid($"{p}/parts/{partIndex}/subparts/{index}"),
                // A subpart's instance_id is a valid weld anchor exactly like a top-level part's.
                Line($"{p}/parts/{partIndex}/subparts/{index}/instance_id", "instance_id",
                    () => Formats.UInt(Subpart(vesselId, partIndex, index).InstanceId)),
                Line($"{p}/parts/{partIndex}/subparts/{index}/id", "id",
                    () => Subpart(vesselId, partIndex, index).Id),
                Line($"{p}/parts/{partIndex}/subparts/{index}/display_name", "display_name",
                    () => Subpart(vesselId, partIndex, index).DisplayName),
                Line($"{p}/parts/{partIndex}/subparts/{index}/template", "template",
                    () => Subpart(vesselId, partIndex, index).Template),
                Line($"{p}/parts/{partIndex}/subparts/{index}/position", "position",
                    () => Formats.Vector(Subpart(vesselId, partIndex, index).PositionVehicleAsmb)));

        // ---- control surface (only when a command sink is wired — KSA_GAME_INTEGRATION_PLAN T1) ----

        /// <summary>A <c>0</c>/<c>1</c> STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile FlagControl(string qidPath, string name, string vesselId, string action, int ordinal,
            Func<string> read)
            => _commands is { } sink
                ? ControlFile.Flag(name, Qid(qidPath), sink, read,
                    v => new SimCommand(vesselId, action, ordinal, v))
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>A <c>0..1</c> STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile FractionControl(string qidPath, string name, string vesselId, string action, int ordinal,
            Func<string> read)
            => _commands is { } sink
                ? ControlFile.Fraction(name, Qid(qidPath), sink, read,
                    v => new SimCommand(vesselId, action, ordinal, v))
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>
        ///     A <c>0..1</c> STATE control whose ordinal is resolved at write time — for cached nodes
        ///     whose target ordinal can move (a solar panel's / light's linked animation, GP1).
        /// </summary>
        private VfsFile FractionControl(string qidPath, string name, string vesselId, string action,
            Func<int> ordinal, Func<string> read)
            => _commands is { } sink
                ? ControlFile.Fraction(name, Qid(qidPath), sink, read,
                    v => new SimCommand(vesselId, action, ordinal(), v))
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>An unbounded numeric STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile NumberControl(string qidPath, string name, string vesselId, string action, int ordinal,
            Func<string> read)
            => _commands is { } sink
                ? ControlFile.Number(name, Qid(qidPath), sink, read,
                    v => new SimCommand(vesselId, action, ordinal, v))
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>A fixed-arity vector STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile VectorControl(string qidPath, string name, string vesselId, string action, int ordinal,
            int arity, Func<string> read)
            => _commands is { } sink
                ? VectorControlFile.Create(name, Qid(qidPath), sink, read, arity,
                    v => new SimCommand(vesselId, action, ordinal, 0) { Values = v })
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>
        ///     A numeric STATE control constrained to an inclusive range, or its read-only twin when
        ///     control is unwired.
        /// </summary>
        private VfsFile RangedControl(string qidPath, string name, string vesselId, string action,
            double min, double max, Func<string> read)
            => _commands is { } sink
                ? ControlFile.Ranged(name, Qid(qidPath), sink, read, min, max,
                    v => new SimCommand(vesselId, action, SimCommand.NoOrdinal, v))
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>A free-form token STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile TokenControl(string qidPath, string name, string vesselId, string action,
            Func<string> read)
            => _commands is { } sink
                ? TokenControlFile.Create(name, Qid(qidPath), sink, read,
                    t => new SimCommand(vesselId, action, SimCommand.NoOrdinal, 0) { Token = t })
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>A hand-parsed line STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile LineControl(string qidPath, string name, Func<string> read,
            Func<string, SimCommand?> parse)
            => _commands is { } sink
                ? LineControlFile.Create(name, Qid(qidPath), sink, read, parse)
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>A symbolic-token STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile EnumControl(string qidPath, string name, string vesselId, string action,
            IReadOnlyList<string> allowed, Func<string> read)
            => _commands is { } sink
                ? EnumControlFile.Create(name, Qid(qidPath), sink, read, allowed,
                    t => new SimCommand(vesselId, action, SimCommand.NoOrdinal, 0) { Token = t })
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        private VfsDirectory CtlDir(string p, string vesselId)
        {
            var sink = _commands!; // CtlDir is only reached when _commands is non-null
            var q = $"{p}/ctl";
            return DelegateDirectory.Fixed("ctl", Qid(q),
                new TriggerFile("ignite", Qid($"{q}/ignite"), sink,
                    new SimCommand(vesselId, "vessel.ignite", SimCommand.NoOrdinal, 1)),
                new TriggerFile("shutdown", Qid($"{q}/shutdown"), sink,
                    new SimCommand(vesselId, "vessel.shutdown", SimCommand.NoOrdinal, 1)),
                // The ignite/shutdown master as one readable toggle: read = EngineOn (the live game
                // state ignite/shutdown set), write 1 = ignite / 0 = shutdown.
                FlagControl($"{q}/engine", "engine", vesselId, "vessel.engine", SimCommand.NoOrdinal,
                    () => Formats.Flag(Vessel(vesselId).EngineOn)),
                new TriggerFile("stage", Qid($"{q}/stage"), sink,
                    new SimCommand(vesselId, "vessel.stage", SimCommand.NoOrdinal, 1)),
                FractionControl($"{q}/throttle", "throttle", vesselId, "vessel.throttle", SimCommand.NoOrdinal,
                    () => Formats.Scalar(Vessel(vesselId).ThrottleCmd)),
                FlagControl($"{q}/lights", "lights", vesselId, "vessel.lights", SimCommand.NoOrdinal,
                    () => Formats.Flag(Vessel(vesselId).LightsMasterOn)),
                FlagControl($"{q}/rcs", "rcs", vesselId, "vessel.rcs", SimCommand.NoOrdinal,
                    () => Formats.Flag(Vessel(vesselId).RcsOn)),
                // Manual RCS translation — the file twin of the player's translate keys. "x y z":
                // the SIGNS command bang-bang thrust along the body axes (+x = forward/nose,
                // +y = right, +z = down; 0 = that axis off). LATCHES like a held key until
                // overwritten — write "0 0 0" to stop. Read = the latched command as signs.
                VectorControl($"{q}/translate", "translate", vesselId, "vessel.translate",
                    SimCommand.NoOrdinal, 3, () => Formats.Vector(Vessel(vesselId).TranslateCmd)),
                // Manual RCS rotation — the file twin of the player's rotation keys and the
                // symmetric sibling of translate. "x y z": the SIGNS command bang-bang torque
                // about the body axes (+x = roll right, +y = pitch up, +z = yaw right; 0 = that
                // axis off). LATCHES like a held key until overwritten — write "0 0 0" to stop.
                // Full authority only in manual attitude mode (an auto-attitude hold strips the
                // rotation bits — the inverse of translate's compose behavior). Read = the
                // latched command as signs.
                VectorControl($"{q}/rotate", "rotate", vesselId, "vessel.rotate",
                    SimCommand.NoOrdinal, 3, () => Formats.Vector(Vessel(vesselId).RotateCmd)),
                EnumControl($"{q}/attitude_mode", "attitude_mode", vesselId, "vessel.attitude_mode",
                    AttitudeModeTokens, () => Vessel(vesselId).AttitudeMode),
                EnumControl($"{q}/attitude_frame", "attitude_frame", vesselId, "vessel.attitude_frame",
                    AttitudeFrameTokens, () => Vessel(vesselId).AttitudeFrame),
                // The flight computer's RCS master switch (the in-game R key). Disabled zeroes the
                // manual thruster flags, so translate/rotate become no-ops — read this before
                // concluding an RCS command was ignored for some other reason.
                EnumControl($"{q}/rcs_mode", "rcs_mode", vesselId, "vessel.rcs_mode",
                    RcsModeTokens, () => Vessel(vesselId).RcsMode),
                VectorControl($"{q}/attitude_target", "attitude_target", vesselId, "vessel.attitude_target",
                    SimCommand.NoOrdinal, 4, () => Formats.Quat(Vessel(vesselId).AttitudeBody2Cci)),
                VectorControl($"{q}/burn", "burn", vesselId, "vessel.burn", SimCommand.NoOrdinal, 4,
                    () => "0 0 0 0"),
                // Move the main camera to this vessel (write 1). Pure view op — does not switch control.
                new TriggerFile("focus", Qid($"{q}/focus"), sink,
                    new SimCommand(vesselId, "camera.focus", SimCommand.NoOrdinal, 1)));
        }

        // ---- /sim/debug cheat namespace (G-D2; gated by [control] debug_namespace) ----------

        private VfsDirectory DebugDir()
        {
            var sink = _commands!;
            return DelegateDirectory.Fixed("debug", Qid("debug"),
                new DelegateDirectory("vessels", Qid("debug/vessels"),
                    () =>
                    {
                        var roster = Vessels(_store.Current);
                        var children = new VfsNode[roster.Sanitized.Count];
                        for (var i = 0; i < children.Length; i++)
                        {
                            var (name, vessel) = roster.Sanitized[i];
                            children[i] = DebugVesselDir(name, vessel.Id);
                        }

                        return children;
                    },
                    name => Vessels(_store.Current).ByName.TryGetValue(name, out var vessel)
                        ? DebugVesselDir(name, vessel.Id)
                        : null),
                DelegateDirectory.Fixed("time", Qid("debug/time"),
                    NumberControl("debug/time/warp", "warp", "", "debug.warp", SimCommand.NoOrdinal,
                        () => Formats.Scalar(_store.Current.WarpFactor))),
                // Move the camera to any astronomical by id — vehicle OR body — view-only (the same
                // camera.focus action the per-vessel ctl/focus and bodies/<id>/focus triggers use).
                TokenControlFile.Create("focus", Qid("debug/focus"), sink,
                    () => _store.Current.ActiveVesselId ?? "",
                    t => new SimCommand(t, "camera.focus", SimCommand.NoOrdinal, 0) { Token = t }),
                // Focus AND take control of a vehicle by id (cheat-tier — grants control authority).
                TokenControlFile.Create("control_vessel", Qid("debug/control_vessel"), sink,
                    () => _store.Current.ActiveVesselId ?? "",
                    t => new SimCommand(t, "debug.control_vessel", SimCommand.NoOrdinal, 0) { Token = t }),
                // Global render hack: force interior (IVA) meshes visible outside the IVA camera.
                FlagControl("debug/always_render_iva", "always_render_iva", "", "debug.always_render_iva",
                    SimCommand.NoOrdinal, () => Formats.Flag(_store.Current.AlwaysRenderIva)),
                // The welds registry view + global ops (per-source weld/unweld live under debug/vessels/<id>/).
                WeldsDir(),
                // The thug-life sunglasses registry: add/clear/count + one editable entry per quad.
                ThugLifeDir(),
                // Face-anchored particle effects: one-shot bursts at a kitten's face (or any vessel).
                FaceFxDir(),
                // The IVA free-floating-object simulation: the master on/off, the adopt/release
                // grammar, diagnostics, and one entry per floating object.
                IvaDir(),
                // The four FX editors (the game's built-in imgui render editors as filesystems).
                EnginePlumeDir(),
                PlumeTrailDir(),
                CloudsDir(),
                TerrainDir());
        }

        // ---- IVA free-floating objects (plans/IVA_MOVEMENTS.md; gated by debug_namespace) ---------

        /// <summary>
        ///     The IVA cabin-physics registry. <c>enabled</c> is the <b>master switch that starts and
        ///     ends the whole feature</b> — off by default, and writing <c>0</c> releases every object
        ///     (restoring exact rest poses) and disposes every simulation, so nothing runs at all.
        ///     <c>adopt</c>/<c>adopt_all</c> cut shipped IVA prop SubParts loose, <c>clear</c> puts them
        ///     all back, and each live object appears as an editable <c>&lt;id&gt;/</c> subdir.
        /// </summary>
        private VfsDirectory IvaDir()
        {
            var sink = _commands!;
            var help = new StaticTextFile("help", Qid("debug/iva/help"), () => IvaHelp);
            var enabled = FlagControl("debug/iva/enabled", "enabled", "", "debug.iva_physics",
                SimCommand.NoOrdinal, () => Formats.Flag(_store.Current.Iva.Enabled));
            var runOutside = FlagControl("debug/iva/run_outside_iva", "run_outside_iva", "",
                "debug.iva_run_outside_iva", SimCommand.NoOrdinal,
                () => Formats.Flag(_store.Current.Iva.RunOutsideIva));
            var adopt = LineControlFile.Create("adopt", Qid("debug/iva/adopt"), sink, () => "", ParseIvaAdopt);
            var adoptAll = LineControlFile.Create("adopt_all", Qid("debug/iva/adopt_all"), sink,
                () => "", ParseIvaAdoptAll);
            var clear = new TriggerFile("clear", Qid("debug/iva/clear"), sink,
                new SimCommand("", SimActions.DebugIvaClear, SimCommand.NoOrdinal, 1));
            var count = Line("debug/iva/count", "count",
                () => _store.Current.Iva.Objects.Count.ToString(CultureInfo.InvariantCulture));
            var stats = Line("debug/iva/stats", "stats", () => Formats.IvaStats(_store.Current.Iva.Stats));
            // A multi-row report (one line per vessel with a built interior), so it carries its own
            // line terminators rather than going through Line's single-value + LF convention.
            var interior = new SnapshotTextFile("interior", Qid("debug/iva/interior"), _store,
                () => string.Concat(_store.Current.Iva.Interiors.Select(i => Formats.IvaInterior(i) + "\n")));
            var fixedChildren = new VfsNode[]
                { help, enabled, runOutside, adopt, adoptAll, clear, count, stats, interior };

            return new DelegateDirectory("iva", Qid("debug/iva"),
                () =>
                {
                    var objects = _store.Current.Iva.Objects;
                    if (objects.Count == 0)
                        return fixedChildren;
                    var children = new List<VfsNode>(fixedChildren.Length + objects.Count);
                    children.AddRange(fixedChildren);
                    children.AddRange(objects.Select(o => (VfsNode)IvaObjectDir(o.Id)));
                    return children;
                },
                name => int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                        && _store.Current.Iva.Objects.Any(o => o.Id == id)
                    ? IvaObjectDir(id)
                    : FindByName(fixedChildren, name));
        }

        private VfsDirectory IvaObjectDir(int id)
        {
            var sink = _commands!;
            var key = id.ToString(CultureInfo.InvariantCulture);
            var q = $"debug/iva/{key}";
            return DelegateDirectory.Fixed(key, Qid(q),
                Line($"{q}/vessel", "vessel", () => IvaObject(id).VesselId),
                Line($"{q}/part", "part", () => Formats.UInt(IvaObject(id).PartInstanceId)),
                Line($"{q}/name", "name", () => IvaObject(id).Part),
                Line($"{q}/template", "template", () => IvaObject(id).Template),
                Line($"{q}/position", "position", () => Formats.Vector(IvaObject(id).Position)),
                Line($"{q}/velocity", "velocity", () => Formats.Vector(IvaObject(id).Velocity)),
                Line($"{q}/angular_velocity", "angular_velocity",
                    () => Formats.Vector(IvaObject(id).AngularVelocity)),
                Line($"{q}/mass", "mass", () => Formats.Scalar(IvaObject(id).MassKg)),
                Line($"{q}/shape", "shape", () => IvaObject(id).Shape),
                Line($"{q}/size", "size", () => Formats.Vector(IvaObject(id).Size)),
                Line($"{q}/asleep", "asleep", () => Formats.Flag(IvaObject(id).Asleep)),
                // A one-shot velocity kick in the vessel assembly frame, m/s — poke an object to see
                // it move (and to test collisions) without waiting for the vessel to manoeuvre.
                VectorControl($"{q}/nudge", "nudge", "", "debug.iva_nudge", id, 3, () => "0 0 0"),
                // Un-adopt: restore the SubPart's exact rest pose and drop the body.
                new TriggerFile("release", Qid($"{q}/release"), sink,
                    new SimCommand("", SimActions.DebugIvaRelease, id, 1)),
                // The adopt-compatible line (echo it to adopt to re-adopt this SubPart).
                Line($"{q}/spec", "spec", () => Formats.IvaObjectSpec(IvaObject(id))));
        }

        private IvaObjectSnapshot IvaObject(int id)
        {
            var objects = _store.Current.Iva.Objects;
            for (var i = 0; i < objects.Count; i++)
                if (objects[i].Id == id)
                    return objects[i];
            throw new VfsErrorException(LinuxErrno.ENOENT, $"iva object {id} is gone");
        }

        /// <summary>
        ///     Parses <c>adopt</c>: <c>"&lt;vessel&gt; &lt;subpart_iid&gt; [vx vy vz]"</c> — 2 or 5
        ///     tokens. Returns null (⇒ EINVAL) on any malformed token.
        /// </summary>
        private static SimCommand? ParseIvaAdopt(string line)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is not (2 or 5) || parts[0].Length == 0)
                return null;
            if (!uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iid)
                || iid == 0)
                return null;
            var values = new double[4]; // [iid, vx, vy, vz]
            values[0] = iid;
            if (parts.Length == 5)
                for (var i = 0; i < 3; i++)
                    if (!double.TryParse(parts[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture,
                            out values[i + 1]) || !double.IsFinite(values[i + 1]))
                        return null;
            return new SimCommand("", SimActions.DebugIvaAdopt, SimCommand.NoOrdinal, 0)
            {
                Token = parts[0],
                Values = values,
            };
        }

        /// <summary>
        ///     Parses <c>adopt_all</c>: <c>"&lt;vessel&gt; [max] [template_substring]"</c> — 1 to 3
        ///     tokens. Returns null (⇒ EINVAL) on a malformed count.
        /// </summary>
        private static SimCommand? ParseIvaAdoptAll(string line)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 3 || parts[0].Length == 0)
                return null;
            double max = 0; // 0 ⇒ up to the configured per-vessel cap
            if (parts.Length >= 2)
            {
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    || n < 0)
                    return null;
                max = n;
            }

            return new SimCommand("", SimActions.DebugIvaAdoptAll, SimCommand.NoOrdinal, max)
            {
                Token = parts[0],
                Aux = parts.Length == 3 ? parts[2] : null,
            };
        }

        /// <summary>
        ///     The console-friendly readme behind <c>/sim/debug/iva/help</c>: the master switch, how to
        ///     cut props loose, and worked examples on the stock Gemini interior.
        /// </summary>
        private const string IvaHelp =
            """
            iva — free-floating objects inside a vessel's cabin, with real inertial physics.

            Loose props stop being glued to the hull: weightless and drifting while you coast,
            slammed aft when the engines light, flung around by RCS rotation, and bouncing off the
            ACTUAL interior surfaces (the collision mesh is built from the IVA meshes themselves).
            Look out a window while it runs. All paths below are under /sim/debug/iva/.

            MASTER SWITCH — everything is OFF until you turn it on, and off means off:
            no physics engine, no interior mesh, no per-frame work at all.
              echo 1 > enabled     # start it
              echo 0 > enabled     # stop it: every object goes back to its exact rest pose and
                                   # every simulation is torn down
              cat     enabled

            CUT SOMETHING LOOSE  (only SubParts can float — a top-level part's transform is saved,
            a SubPart's is not, so nothing here can ever contaminate your save file)
              echo "<vessel> <subpart_iid>"                > adopt
              echo "<vessel> <subpart_iid> <vx> <vy> <vz>" > adopt      # ...with a starting velocity
              echo "<vessel> [max] [template_substring]"   > adopt_all  # the smallest loose props first

            FIND A PROP  (needs telemetry_vessel_parts on)
              v=/sim/vessels/by-id/Gemini7
              cat $v/parts/json | jq -r '.[].subparts[] | "\(.instance_id) \(.template)"' | grep -i sardine

            PER OBJECT  (<id> = 0, 1, 2, ... — the smallest free slot, reused after release)
              position velocity angular_velocity   3 numbers, vessel assembly frame (m, m/s, rad/s)
              mass shape size                      kg; collision proxy kind; proxy extents in m
              asleep                               1 = settled, costs nothing to simulate
              nudge     "vx vy vz"                 one-shot velocity kick, m/s, assembly frame
              release                              echo 1 — put this one back at its rest pose
              spec                                 the adopt-compatible line; echo it back to re-adopt

            EVERYTHING BACK
              echo 1 > clear       # release every object (the sim stays enabled)
              cat     count        # how many are floating
              cat     stats        # vessels objects sleeping substeps avg_ms max_ms parked reason
              cat     interior     # per vessel: triangles source_parts aabb_min aabb_max fallback

            EXAMPLES  (stock Gemini 7 — its cabin already ships sardine tins, bolts, screws,
            photos, notes, tape and a toothbrush)
              echo 1 > /sim/debug/iva/enabled

              # Set every sardine tin adrift:
              echo "Gemini7 4 Sardine" > /sim/debug/iva/adopt_all

              # Or pick one by hand and flick it across the cabin at 0.3 m/s:
              iid=$(cat /sim/vessels/by-id/Gemini7/parts/json \
                    | jq -r '.[].subparts[] | select(.template|test("SardineA")) | .instance_id' | head -1)
              echo "Gemini7 $iid 0.3 0 0" > /sim/debug/iva/adopt

              # Fill the cabin with the 12 smallest loose things, then watch one drift:
              echo "Gemini7 12" > /sim/debug/iva/adopt_all
              watch -n0.2 cat /sim/debug/iva/0/position

              # Clunk on impact (needs /sim/audio and a clip called clunk.wav):
              tail -f /sim/events | grep --line-buffered iva.impact \
                | while read -r _; do echo "clunk.wav" > /sim/audio/play; done

            Notes: objects are simulated in the vessel's assembly frame by a gatOS-owned physics
            world — they never touch the game's own solver, so they cannot perturb your trajectory
            or corrupt a save. They park (velocities zeroed, poses frozen) under time warp, in the
            vehicle editor, and — unless run_outside_iva is 1 — whenever no viewport is in the IVA
            camera. Nothing is persisted: everything is released at mod unload. The same actions
            work over HTTP /v1 and MQTT.

            """;

        // ---- welds cheat (G-D; gated by debug_namespace) ------------------------------------------

        /// <summary>
        ///     The welds registry view + global ops. <c>clear</c> drops every weld; <c>count</c> reports
        ///     how many are active; each active weld appears as a <c>&lt;source_id&gt;/</c> subdir. The
        ///     per-source create/remove controls live under <c>debug/vessels/&lt;id&gt;/</c> (so the source
        ///     is path-implied, like teleport).
        /// </summary>
        private VfsDirectory WeldsDir()
        {
            var sink = _commands!;
            var clear = new TriggerFile("clear", Qid("debug/welds/clear"), sink,
                new SimCommand("", SimActions.DebugWeldClear, SimCommand.NoOrdinal, 1));
            var count = Line("debug/welds/count", "count",
                () => _store.Current.Welds.Count.ToString(CultureInfo.InvariantCulture));
            return new DelegateDirectory("welds", Qid("debug/welds"),
                () =>
                {
                    var children = new List<VfsNode> { clear, count };
                    children.AddRange(SanitizedWelds(_store.Current)
                        .Select(w => (VfsNode)WeldDir(w.Name, w.Weld.SourceId)));
                    return children.ToArray();
                },
                name => name switch
                {
                    "clear" => clear,
                    "count" => count,
                    _ => SanitizedWelds(_store.Current)
                        .Where(w => w.Name == name)
                        .Select(w => (VfsNode?)WeldDir(w.Name, w.Weld.SourceId))
                        .FirstOrDefault(),
                });
        }

        private VfsDirectory WeldDir(string sanitized, string sourceId)
        {
            var sink = _commands!;
            var q = $"debug/welds/{sanitized}";
            return DelegateDirectory.Fixed(sanitized, Qid(q),
                Line($"{q}/target", "target", () => Weld(sourceId).TargetId),
                Line($"{q}/part", "part", () => Formats.UInt(Weld(sourceId).PartInstanceId)),
                Line($"{q}/offset", "offset", () => Formats.Vector(Weld(sourceId).Offset)),
                Line($"{q}/rotation", "rotation", () => Formats.Vector(Weld(sourceId).Rotation)),
                Line($"{q}/lock_rotation", "lock_rotation", () => Formats.Flag(Weld(sourceId).LockRotation)),
                // Suspend/resume this weld without removing it.
                FlagControl($"{q}/enabled", "enabled", sourceId, "debug.weld_enable", SimCommand.NoOrdinal,
                    () => Formats.Flag(Weld(sourceId).Enabled)));
        }

        // ---- thug-life cheat (G-D; gated by debug_namespace) --------------------------------------

        /// <summary>
        ///     The thug-life sunglasses registry: <c>add</c> creates a new anchored quad (returns nothing —
        ///     the new entry appears under its id), <c>clear</c> removes all, <c>count</c> reports how many
        ///     are active, and each active quad is an editable <c>&lt;id&gt;/</c> subdir. Entries are keyed by
        ///     an integer id — the smallest free slot at create, reused after remove/clear — carried in the
        ///     command <c>Ordinal</c>.
        /// </summary>
        private VfsDirectory ThugLifeDir()
        {
            var sink = _commands!;
            var add = LineControlFile.Create("add", Qid("debug/thug_life/add"), sink,
                () => "", ParseThugLifeAdd);
            var clear = new TriggerFile("clear", Qid("debug/thug_life/clear"), sink,
                new SimCommand("", SimActions.DebugThugLifeClear, SimCommand.NoOrdinal, 1));
            var count = Line("debug/thug_life/count", "count",
                () => _store.Current.ThugLife.Count.ToString(CultureInfo.InvariantCulture));
            var help = new StaticTextFile("help", Qid("debug/thug_life/help"), () => ThugLifeHelp);
            return new DelegateDirectory("thug_life", Qid("debug/thug_life"),
                () =>
                {
                    var children = new List<VfsNode> { help, add, clear, count };
                    children.AddRange(_store.Current.ThugLife
                        .Select(t => (VfsNode)ThugLifeEntryDir(t.Id)));
                    return children.ToArray();
                },
                name => name switch
                {
                    "help" => help,
                    "add" => add,
                    "clear" => clear,
                    "count" => count,
                    _ => int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                         && _store.Current.ThugLife.Any(t => t.Id == id)
                        ? ThugLifeEntryDir(id)
                        : null,
                });
        }

        /// <summary>
        ///     The console-friendly readme behind <c>/sim/debug/thug_life/help</c> (a static text leaf):
        ///     how to anchor/tune/remove the sunglasses, with worked examples on the EVA Kitten vehicles
        ///     (ids <c>Hunter</c>, <c>Polaris</c>, <c>Banjo</c>).
        /// </summary>
        private const string ThugLifeHelp =
            """
            thug_life — stick "thug life" sunglasses (a flat textured quad) onto a part of a
            vehicle; it tracks that part every frame. Pure cosmetic cheat. All paths below are
            under /sim/debug/thug_life/ (needs the debug namespace enabled).

            CREATE
              echo "<vessel> <part_iid>" > add
              echo "<vessel> <part_iid> <x> <y> <z> <pitch> <yaw> <roll> <w> <h>" > add
                part_iid       a part instance_id (see FIND A PART), or 0 = the vehicle frame
                x y z          offset in the part's local frame, metres   (default 0 0 0)
                pitch yaw roll rotation, degrees                          (default 0 0 0)
                w h            quad size, metres                          (default 0.975 0.1875)
              Each add takes the lowest free id (0, 1, 2, ...) under thug_life/<id>/;
              remove/clear free ids for reuse, so the numbering tracks what's live.

            FIND A PART
              ls  /sim/vessels/by-id/Hunter/parts/
              cat /sim/vessels/by-id/Hunter/parts/0/instance_id    # the stable handle to pass
              cat /sim/vessels/by-id/Hunter/parts/0/display_name

            TUNE / INSPECT  (per entry <id>; write a file to set it, read it to see the current value)
              position  "x y z"           3 numbers, metres, in the anchor part's local frame.
                                          Axes follow the part; "0 0 0" sits right on the anchor.
                  echo "0 0.25 0" > thug_life/0/position
              rotation  "pitch yaw roll"  3 numbers, degrees, applied in the part's local frame.
                  echo "0 0 15"   > thug_life/0/rotation
              size      "width height"    2 numbers, metres — the quad's size in the world.
                  echo "1.2 0.24" > thug_life/0/size
              visible   0 | 1             0 hides the quad (entry kept); 1 shows it.
                  echo 0          > thug_life/0/visible
              cameras   all | main crew other
                                          which render passes draw the quad: "main" is the
                                          player's view, "crew" the kitten face-cam portraits,
                                          "other" any extra camera window. One entry serves
                                          every pass (it is a filter, not extra quads).
                                          Default all. Tokens combine, space/comma separated.
                  echo crew       > thug_life/0/cameras     # portraits only
                  echo "main crew" > thug_life/0/cameras    # everywhere but extra windows
              spec      (read-only)       the full add-compatible line; echo it to add to clone.
                  cat thug_life/0/spec

            REMOVE
              echo 1 > thug_life/0/remove              # one entry
              echo 1 > thug_life/clear                 # every entry
              cat     thug_life/count                  # how many are active

            EXAMPLES  (EVA Kittens: Hunter, Polaris, Banjo)
              # Shades on Hunter's root part:
              iid=$(cat /sim/vessels/by-id/Hunter/parts/0/instance_id)
              echo "Hunter $iid" > /sim/debug/thug_life/add

              # Shades on Polaris at its body frame, nudged up 0.2 m and made bigger:
              echo "Polaris 0 0 0.2 0 0 0 0 1.5 0.29" > /sim/debug/thug_life/add

              # Give the whole squad shades (part_iid 0 = vehicle frame):
              for k in Hunter Polaris Banjo; do echo "$k 0" > /sim/debug/thug_life/add; done

              # Tilt the last one, then take everyone's shades off:
              echo "0 0 20" > /sim/debug/thug_life/2/rotation
              echo 1        > /sim/debug/thug_life/clear

            Notes: entries are runtime-only (cleared on mod unload); if the anchor part is staged
            away it falls back to the vehicle frame. The same actions work over HTTP /v1 and MQTT.

            """;

        private VfsDirectory ThugLifeEntryDir(int id)
        {
            var sink = _commands!;
            var key = id.ToString(CultureInfo.InvariantCulture);
            var q = $"debug/thug_life/{key}";
            return DelegateDirectory.Fixed(key, Qid(q),
                Line($"{q}/vessel", "vessel", () => ThugLife(id).VesselId),
                Line($"{q}/part", "part", () => Formats.UInt(ThugLife(id).PartInstanceId)),
                // Live-tunable transform in the part's local frame (registry-keyed: vesselId "" + id in ordinal).
                VectorControl($"{q}/position", "position", "", "debug.thug_life_position", id, 3,
                    () => Formats.Vector(ThugLife(id).Position)),
                VectorControl($"{q}/rotation", "rotation", "", "debug.thug_life_rotation", id, 3,
                    () => Formats.Vector(ThugLife(id).Rotation)),
                VectorControl($"{q}/size", "size", "", "debug.thug_life_size", id, 2,
                    () => $"{Formats.Scalar(ThugLife(id).Width)} {Formats.Scalar(ThugLife(id).Height)}"),
                FlagControl($"{q}/visible", "visible", "", "debug.thug_life_visible", id,
                    () => Formats.Flag(ThugLife(id).Visible)),
                // Which render passes the quad draws in (main view / crew portraits / other viewports).
                LineControlFile.Create("cameras", Qid($"{q}/cameras"), sink,
                    () => ThugLife(id).Cameras,
                    line => ThugLifeCameraMask.TryParse(line, out var mask)
                        ? new SimCommand("", SimActions.DebugThugLifeCameras, id, mask)
                        : null),
                new TriggerFile("remove", Qid($"{q}/remove"), sink,
                    new SimCommand("", SimActions.DebugThugLifeRemove, id, 1)),
                // The full write-compatible spec line (echo to add to recreate as a new id).
                Line($"{q}/spec", "spec", () => Formats.ThugLifeSpec(ThugLife(id))));
        }

        // ---- face FX (particle bursts at a kitten's face; gated by debug_namespace) ----------------

        /// <summary>
        ///     <c>/sim/debug/fx</c>: one-shot particle effects anchored to a vessel — by default at an
        ///     EVA kitten's face. Spawns are <c>Burst</c> emitters that self-retire, so there is no
        ///     per-entry registry to edit: the surface is spawn + count + clear.
        /// </summary>
        private VfsDirectory FaceFxDir()
        {
            const string q = "debug/fx";
            var sink = _commands!;
            var spawn = LineControlFile.Create("spawn", Qid($"{q}/spawn"), sink,
                () => "", FaceFxRules.ParseSpawn);
            var clear = new TriggerFile("clear", Qid($"{q}/clear"), sink,
                new SimCommand("", FaceFxRules.ClearAction, SimCommand.NoOrdinal, 1));
            return DelegateDirectory.Fixed("fx", Qid(q),
                new StaticTextFile("help", Qid($"{q}/help"), () => FaceFxHelp),
                Line($"{q}/profiles", "profiles", () => string.Join(",", FaceFxRules.Profiles)),
                Line($"{q}/count", "count",
                    () => _store.Current.FaceFxLive.ToString(CultureInfo.InvariantCulture)),
                spawn, clear);
        }

        /// <summary>The console-friendly readme behind <c>/sim/debug/fx/help</c>.</summary>
        private const string FaceFxHelp =
            """
            fx — one-shot particle effects on a vessel, anchored at an EVA kitten's face by
            default. Pure cosmetic cheat riding the game's own particle pool. All paths below
            are under /sim/debug/fx/ (needs the debug namespace enabled).

            SPAWN
              echo "<vessel> <profile>" > spawn
              echo "<vessel> <profile> [scale <s>] [offset <x> <y> <z>]" > spawn
                profile   party    confetti burst (celebrations)
                          sparkle  gold glitter (small wins)
                          danger   fire flash (trouble)
                          death    slow grey puff (the end)
                scale     size/velocity multiplier, > 0            (default 1)
                offset    metres, vessel assembly frame — overrides the face anchor
                          (kittens default to their face; other vessels to their origin)

            INSPECT / STOP
              cat profiles     # the profile tokens, comma-separated
              cat count        # live gatOS emitters (bursts self-retire in seconds)
              echo 1 > clear   # stop every gatOS effect now

            EXAMPLES  (EVA Kittens: Hunter, Polaris, Banjo)
              echo "Hunter party" > /sim/debug/fx/spawn
              echo "Polaris danger scale 1.5" > /sim/debug/fx/spawn
              for k in Hunter Polaris Banjo; do echo "$k sparkle" > /sim/debug/fx/spawn; done

            Notes: effects are Burst emitters from the game's shared pool (capped; EAGAIN-style
            'busy' when exhausted) and render only when the graphics Particles setting is on.
            The same actions work over HTTP /v1 and MQTT.

            """;

        /// <summary>
        ///     Parses a thug-life <c>add</c> line — either <c>"&lt;vessel&gt; &lt;part_iid&gt;"</c> (2 tokens,
        ///     transform defaulted) or the full 10-token
        ///     <c>"&lt;vessel&gt; &lt;part_iid&gt; x y z pitch yaw roll width height"</c> — into a
        ///     <c>debug.thug_life_add</c> command. Returns null (⇒ EINVAL) on any malformed token.
        /// </summary>
        private static SimCommand? ParseThugLifeAdd(string line)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is not (2 or 10) || parts[0].Length == 0)
                return null;
            // part_iid: a non-negative integer.
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var iid)
                || !double.IsFinite(iid) || iid < 0 || iid != Math.Floor(iid))
                return null;
            // Values: [iid, x, y, z, pitch, yaw, roll, width, height] — defaults for the 2-token form.
            var values = new double[9];
            values[0] = iid;
            values[7] = 0.975; // default width (unscience)
            values[8] = 0.1875; // default height (keeps the 26:5 texture aspect at a uniform block size)
            if (parts.Length == 10)
                for (var i = 0; i < 8; i++) // x y z pitch yaw roll width height
                    if (!double.TryParse(parts[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture,
                            out values[i + 1]) || !double.IsFinite(values[i + 1]))
                        return null;
            return new SimCommand("", SimActions.DebugThugLifeAdd, SimCommand.NoOrdinal, 0)
            {
                Token = parts[0],
                Values = values,
            };
        }

        // ---- FX editors (G-D; gated by debug_namespace — plans/FX_EDITORS_PLAN.md) -----------------

        /// <summary>The sampled FX-editor surface, or null when it has not been sampled.</summary>
        private FxEditorsSnapshot? Fx() => _store.Current.FxEditors;

        /// <summary>
        ///     <c>debug/engineplume</c>: the volumetric-exhaust templates. Scope is <b>per template</b> —
        ///     a template is shared by every nozzle that references it, so one edit repaints all of them.
        /// </summary>
        private VfsDirectory EnginePlumeDir()
        {
            const string q = "debug/engineplume";
            return DelegateDirectory.Fixed("engineplume", Qid(q),
                new StaticTextFile("help", Qid($"{q}/help"), () => EnginePlumeHelp),
                FxEntitiesDir("templates", $"{q}/templates", () => Fx()?.PlumeTemplates ?? [],
                    FxCatalog.EnginePlume, FxCatalog.EnginePlumeSet, FxCatalog.EnginePlumeReset));
        }

        /// <summary>
        ///     <c>debug/plumetrail</c>: the one global volumetric-trail renderer, so its fields sit
        ///     directly in the family dir (no entity roster) beside the <c>clear</c> one-shot.
        /// </summary>
        private VfsDirectory PlumeTrailDir()
        {
            const string q = "debug/plumetrail";
            var help = new StaticTextFile("help", Qid($"{q}/help"), () => PlumeTrailHelp);
            var clear = new TriggerFile("clear", Qid($"{q}/clear"), _commands!,
                new SimCommand("", FxCatalog.PlumeTrailClear, SimCommand.NoOrdinal, 1));
            VfsNode[] Entity() => FxEntityNodes(q, FxCatalog.PlumeTrail, FxCatalog.PlumeTrailSet,
                FxCatalog.PlumeTrailReset, null, () => Fx()?.Trail, withDocs: true);
            return new DelegateDirectory("plumetrail", Qid(q),
                () =>
                {
                    var entity = Entity();
                    var children = new VfsNode[entity.Length + 2];
                    children[0] = help;
                    children[1] = clear;
                    entity.CopyTo(children, 2);
                    return children;
                },
                name => name switch
                {
                    "help" => help,
                    "clear" => clear,
                    _ => FindByName(Entity(), name),
                });
        }

        /// <summary>
        ///     <c>debug/clouds</c>: per-body cloud layers (body → layer → cloud type). Only bodies
        ///     that carry a cloud definition appear.
        /// </summary>
        private VfsDirectory CloudsDir()
        {
            const string q = "debug/clouds";
            return DelegateDirectory.Fixed("clouds", Qid(q),
                new StaticTextFile("help", Qid($"{q}/help"), () => CloudsHelp),
                FxEntitiesDir("bodies", $"{q}/bodies", () => Fx()?.CloudBodies ?? [],
                    FxCatalog.Clouds, FxCatalog.CloudsSet, FxCatalog.CloudsReset));
        }

        /// <summary>
        ///     <c>debug/terrain</c>: per-body terrain, plus the family-global <c>wireframe</c> toggle.
        ///     The global fields are addressed with an empty entity token and carry no <c>json</c>/
        ///     <c>reset</c> of their own — those are per body.
        /// </summary>
        private VfsDirectory TerrainDir()
        {
            const string q = "debug/terrain";
            var help = new StaticTextFile("help", Qid($"{q}/help"), () => TerrainHelp);
            var bodies = FxEntitiesDir("bodies", $"{q}/bodies", () => Fx()?.TerrainBodies ?? [],
                FxCatalog.Terrain, FxCatalog.TerrainSet, FxCatalog.TerrainReset);
            VfsNode[] Global() => FxEntityNodes(q, FxCatalog.Terrain, FxCatalog.TerrainSet,
                FxCatalog.TerrainReset, "", () => Fx()?.TerrainGlobal, withDocs: false);
            return new DelegateDirectory("terrain", Qid(q),
                () =>
                {
                    var global = Global();
                    var children = new VfsNode[global.Length + 2];
                    children[0] = help;
                    global.CopyTo(children, 1);
                    children[^1] = bodies;
                    return children;
                },
                name => name switch
                {
                    "help" => help,
                    "bodies" => bodies,
                    _ => FindByName(Global(), name),
                });
        }

        /// <summary>
        ///     The <c>&lt;id&gt;/</c> roster of an FX family (plume templates, cloud/terrain bodies):
        ///     one subdir per sampled entity, named by the sanitized id — the command still carries the
        ///     raw id. Empty (and ENOENT on walk) while the family has not been sampled.
        /// </summary>
        private VfsDirectory FxEntitiesDir(string dirName, string qidPrefix,
            Func<IReadOnlyList<FxEntitySnapshot>> roster, IReadOnlyList<FxFieldSpec> specs,
            string setAction, string resetAction)
            => new DelegateDirectory(dirName, Qid(qidPrefix),
                () =>
                {
                    var entities = SanitizeNames(roster(), static e => e.Id);
                    var children = new VfsNode[entities.Count];
                    for (var i = 0; i < children.Length; i++)
                        children[i] = FxEntityDir(qidPrefix, entities[i].Name, entities[i].Item.Id,
                            specs, setAction, resetAction, roster);
                    return children;
                },
                name =>
                {
                    foreach (var (entityName, entity) in SanitizeNames(roster(), static e => e.Id))
                        if (entityName == name)
                            return FxEntityDir(qidPrefix, name, entity.Id, specs, setAction, resetAction, roster);
                    return null;
                });

        private VfsDirectory FxEntityDir(string qidPrefix, string dirName, string entityId,
            IReadOnlyList<FxFieldSpec> specs, string setAction, string resetAction,
            Func<IReadOnlyList<FxEntitySnapshot>> roster)
        {
            var q = $"{qidPrefix}/{dirName}";
            VfsNode[] Nodes() => FxEntityNodes(q, specs, setAction, resetAction, entityId,
                () => FxFind(roster(), entityId), withDocs: true);
            return new DelegateDirectory(dirName, Qid(q), Nodes, name => FindByName(Nodes(), name));
        }

        /// <summary>
        ///     Materializes one FX entity's subtree from the family table + the entity's live field
        ///     keys: the nested control leaves (one per concrete field, in catalog order), then the
        ///     <c>json</c> discovery document and the <c>reset</c> trigger. Cached by (path, field
        ///     count); the nodes themselves always read the live snapshot. Empty while the entity is
        ///     not sampled, so its directory simply lists nothing.
        /// </summary>
        private VfsNode[] FxEntityNodes(string qidPrefix, IReadOnlyList<FxFieldSpec> specs,
            string setAction, string resetAction, string? token, Func<FxEntitySnapshot?> find,
            bool withDocs)
        {
            var entity = find();
            if (entity is null)
                return [];
            var cacheKey = new FxKey(qidPrefix, entity.Fields.Count);
            if (_fxNodes.TryGetValue(cacheKey, out var cached))
                return cached;

            // Catalog order drives the listing; within one spec the concrete keys differ only by
            // their indices, which the key comparer orders numerically (layers/2 before layers/10).
            var keys = entity.Fields.Keys.ToArray();
            Array.Sort(keys, FxCatalog.KeyComparer);
            var root = new FxTreeNode();
            foreach (var spec in specs)
            {
                foreach (var field in keys)
                {
                    if (!FxCatalog.Matches(spec, field))
                        continue;
                    var node = root;
                    foreach (var segment in field.Split('/'))
                        node = node.Child(segment);
                    node.Leaf = spec;
                    node.Key = field;
                }
            }

            var fields = FxChildren(root, qidPrefix, setAction, token, find);
            var nodes = fields;
            if (withDocs)
            {
                nodes = new VfsNode[fields.Length + 2];
                fields.CopyTo(nodes, 0);
                nodes[^2] = FxJsonFile(qidPrefix, find);
                nodes[^1] = new TriggerFile("reset", Qid($"{qidPrefix}/reset"), _commands!,
                    new SimCommand("", resetAction, SimCommand.NoOrdinal, 1) { Token = token });
            }

            _fxNodes[cacheKey] = nodes;
            return nodes;
        }

        private VfsNode[] FxChildren(FxTreeNode node, string qidPrefix, string action, string? token,
            Func<FxEntitySnapshot?> find)
        {
            var children = new VfsNode[node.Children.Count];
            for (var i = 0; i < children.Length; i++)
            {
                var (name, child) = node.Children[i];
                var path = $"{qidPrefix}/{name}";
                children[i] = child.Leaf is { } spec
                    ? FxLeaf(path, name, spec, child.Key, action, token, find)
                    : DelegateDirectory.Fixed(name, Qid(path), FxChildren(child, path, action, token, find));
            }

            return children;
        }

        /// <summary>
        ///     One FX field leaf. The archetype follows the spec's kind and the accepted values follow
        ///     the spec's inclusive range, so a wrong-arity / non-finite / out-of-range write fails
        ///     EINVAL <b>before</b> the sink. The write emits the family's single <c>_set</c> action
        ///     with the entity in <see cref="SimCommand.Token"/> and the concrete field path in
        ///     <see cref="SimCommand.Aux"/>; the payload always rides <see cref="SimCommand.Values"/>
        ///     (a scalar additionally in <see cref="SimCommand.Value"/>, so a hand-written
        ///     <c>POST /v1/command</c> may use either).
        /// </summary>
        private VfsFile FxLeaf(string qidPath, string name, FxFieldSpec spec, string field, string action,
            string? token, Func<FxEntitySnapshot?> find)
        {
            var sink = _commands!;
            Func<string> read = () => FxRead(spec, field, find, qidPath);
            return spec.Kind switch
            {
                FxKind.Flag => ControlFile.Flag(name, Qid(qidPath), sink, read,
                    v => FxSet(action, token, field, [v])),
                FxKind.Number => ControlFile.Ranged(name, Qid(qidPath), sink, read, spec.Min, spec.Max,
                    v => FxSet(action, token, field, [v])),
                _ => VectorControlFile.Create(name, Qid(qidPath), sink, read, spec.Arity, spec.Min, spec.Max,
                    values => FxSet(action, token, field, values)),
            };
        }

        private static SimCommand FxSet(string action, string? token, string field, IReadOnlyList<double> values)
            => new("", action, SimCommand.NoOrdinal, values.Count == 1 ? values[0] : 0)
            {
                Token = token,
                Aux = field,
                Values = values,
            };

        /// <summary>
        ///     The <c>json</c> discovery document for one entity — every field of the entity in one
        ///     object, memoized on the field-dictionary reference (the sampler republishes the same
        ///     instance while nothing changed, the <c>parts/json</c> precedent).
        /// </summary>
        private VfsFile FxJsonFile(string qidPrefix, Func<FxEntitySnapshot?> find)
        {
            FxJsonCache? cache = null;
            return Line($"{qidPrefix}/json", "json", () =>
            {
                var fields = FxRequire(find, qidPrefix).Fields;
                var c = cache;
                if (c is null || !ReferenceEquals(c.Fields, fields))
                    cache = c = new FxJsonCache(fields, Formats.FxFields(fields));
                return c.Json;
            });
        }

        private sealed class FxJsonCache(IReadOnlyDictionary<string, double[]> fields, string json)
        {
            public readonly IReadOnlyDictionary<string, double[]> Fields = fields;
            public readonly string Json = json;
        }

        /// <summary>Formats one FX field's live value; ENOENT when the entity or the field is gone.</summary>
        private static string FxRead(FxFieldSpec spec, string field, Func<FxEntitySnapshot?> find, string qidPath)
        {
            var entity = FxRequire(find, qidPath);
            if (!entity.Fields.TryGetValue(field, out var values) || values.Length != spec.Arity)
                throw new VfsErrorException(LinuxErrno.ENOENT, $"fx field '{qidPath}' is gone");
            return spec.Kind switch
            {
                FxKind.Flag => Formats.Flag(values[0] != 0),
                FxKind.Number => Formats.Scalar(values[0]),
                _ => string.Join(' ', values.Select(Formats.Scalar)),
            };
        }

        private static FxEntitySnapshot FxRequire(Func<FxEntitySnapshot?> find, string what)
            => find() ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"fx entity '{what}' is gone");

        private static FxEntitySnapshot? FxFind(IReadOnlyList<FxEntitySnapshot> roster, string id)
        {
            for (var i = 0; i < roster.Count; i++)
                if (roster[i].Id == id)
                    return roster[i];
            return null;
        }

        /// <summary>
        ///     The mutable scaffold used only while materializing an FX entity's directory tree:
        ///     children in first-seen (catalog) order, plus the concrete field key on a leaf.
        /// </summary>
        private sealed class FxTreeNode
        {
            private readonly Dictionary<string, FxTreeNode> _index = new(StringComparer.Ordinal);

            public List<(string Name, FxTreeNode Node)> Children { get; } = [];

            public FxFieldSpec? Leaf { get; set; }

            public string Key { get; set; } = "";

            public FxTreeNode Child(string name)
            {
                if (_index.TryGetValue(name, out var existing))
                    return existing;
                var child = new FxTreeNode();
                _index[name] = child;
                Children.Add((name, child));
                return child;
            }
        }

        /// <summary>The console readme behind <c>/sim/debug/engineplume/help</c>.</summary>
        private const string EnginePlumeHelp =
            """
            engineplume — the game's "Volumetric Exhausts" editor as files. Live-tunes the
            volumetric rocket exhaust plumes. Paths are under /sim/debug/engineplume/
            (needs the debug namespace enabled).

            SCOPE — PER TEMPLATE, NOT PER ENGINE
              templates/<id>/ edits the shared VolumetricExhaustTemplate, so the change hits
              EVERY nozzle in the universe that references that template, immediately.

            USAGE
              ls  templates/                          # which templates are loaded
              cat templates/<id>/json                 # every field of one template, as JSON
              cat templates/<id>/emission/brightness  # read one knob
              echo 60 > templates/<id>/emission/brightness
              echo "1 0.35 0.05" > templates/<id>/emission/color0
              echo 1  > templates/<id>/reset          # restore the pristine values

            FIELDS  (one leaf per knob; a read shows the live value)
              core/*             plume-length model weights
              absorption/*       medium density, scattering, refraction   (+ fake_clean_burn 0|1)
              emission/*         brightness + the 4-stop colour gradient  (color0 = nozzle exit)
              mach_diamonds/*    shock-diamond placement along the plume
              noise/*            density / radial / shape noise strength, size, speed
              quality/*          sample counts and the vessel self-shadow toggle
            Colours are "r g b", each channel 0..1. Every field has a fixed inclusive range —
            a value outside it, the wrong component count, or a non-number fails with EINVAL and
            never reaches the game. See SPEC_9P_FILESYSTEM.md for the full table with ranges.

            ANIMATING
              Writes are cheap and fire per frame; write only the leaves that changed. Group
              simultaneous changes through /sim/ctl/batch so they land in one tick. reset
              restores whatever the values were before gatOS first wrote them, so an aborted
              light show never strands the game. The same fields work over HTTP /v1 and MQTT.

            """;

        /// <summary>The console readme behind <c>/sim/debug/plumetrail/help</c>.</summary>
        private const string PlumeTrailHelp =
            """
            plumetrail — the game's "Plume Trails" editor as files. Live-tunes the volumetric
            exhaust trails left behind vehicles. Paths are under /sim/debug/plumetrail/
            (needs the debug namespace enabled).

            SCOPE — GLOBAL
              There is one trail renderer for the whole game; these settings are not per vessel.
              The renderer re-reads them every frame, so a write takes effect immediately.

            USAGE
              cat json                             # every field at once, as JSON
              cat render/max_distance
              echo 200000 > render/max_distance    # metres
              echo "0.9 0.6 0.4 1" > render/trail_color   # r g b a, each 0..1
              echo 1 > clear                       # drop the trails currently in the world
              echo 1 > reset                       # restore the pristine render settings

            FIELDS  (all under render/)
              max_distance, voxel_first_slice, min_step_size, step_size_distance_scale
              expansion_time, erosion_max_depth, erosion_edge_sharpness, self_shadow_steps
              light_brightness, sky_ambient_brightness, trail_color
            Each has a fixed inclusive range; out-of-range or unparseable writes fail with EINVAL
            before reaching the game. See SPEC_9P_FILESYSTEM.md for the ranges and units.

            clear is a one-shot (it deletes existing trail geometry); reset only restores settings.
            The same fields work over HTTP /v1 and MQTT.

            """;

        /// <summary>The console readme behind <c>/sim/debug/clouds/help</c>.</summary>
        private const string CloudsHelp =
            """
            clouds — the game's "Clouds" editor as files. Live-tunes a body's cloud layers.
            Paths are under /sim/debug/clouds/ (needs the debug namespace enabled).

            SCOPE — PER BODY, THEN PER LAYER, THEN PER CLOUD TYPE
              bodies/<body>/                       only bodies that define clouds appear
                shared/                            the body-wide altitude blend + shadow limits
                layers/<n>/                        one cloud layer
                  two_d/                           the flat, distant representation
                  raymarch/                        the volumetric marching parameters
                  types/<m>/                       one cloud type inside that layer

            USAGE
              ls  bodies/                          # which bodies have clouds
              cat bodies/Kerth/json                # every field of that body, as JSON
              echo "0.9 0.9 1" > bodies/Kerth/layers/0/color
              echo 4 > bodies/Kerth/layers/1/types/0/density
              echo 1 > bodies/Kerth/reset          # restore that body's pristine clouds

            FIELDS
              shared/{transition_start_km,transition_end_km,max_shadows_altitude_km}
              layers/<n>/{rotation_speed,detail_tile_km,color,scroll_speed}
              layers/<n>/two_d/{lambertian,color}
              layers/<n>/raymarch/{step_size,step_scale,max_step,light_distance,light_samples}
              layers/<n>/types/<m>/{start_altitude,height,density,edge_sharpness,multi_scatter,
                                    interpolate}
            Colours are "r g b" (0..1 per channel); rotation_speed is a plain 3-vector "x y z".
            Every field has a fixed inclusive range — an out-of-range, wrong-arity or
            unparseable write fails with EINVAL and never reaches the game. See
            SPEC_9P_FILESYSTEM.md for the full table with ranges and units.

            The noise scale is deliberately NOT exposed: changing it would rebuild the layer's
            GPU pipelines. reset restores the values as they were before gatOS first wrote them.
            The same fields work over HTTP /v1 and MQTT.

            """;

        /// <summary>The console readme behind <c>/sim/debug/terrain/help</c>.</summary>
        private const string TerrainHelp =
            """
            terrain — the game's "Terrain Editor" as files (a deliberately small first slice).
            Paths are under /sim/debug/terrain/ (needs the debug namespace enabled).

            SCOPE
              wireframe                            GLOBAL toggle: draw all terrain as wireframe
              bodies/<body>/                       per body; only bodies with a live render slot

            USAGE
              echo 1 > wireframe
              ls  bodies/
              cat bodies/Kerth/json                # every field of that body, as JSON
              echo 9000  > bodies/Kerth/max_height          # metres
              echo 0.35  > bodies/Kerth/tessellation/factor
              echo 1     > bodies/Kerth/reset               # restore pristine terrain values

            FIELDS  (per body)
              min_height, max_height               height-field range, metres
              slope_roughness_deg                  micro-slope roughness of the surface BRDF
              hapke_albedo                         mean single-scattering albedo
              biomes/{blend_strength,detail_fade_start_km,detail_fade_end_km}
              tessellation/{edge_length_px,factor,range_m}
            Every field has a fixed inclusive range; out-of-range or unparseable writes fail with
            EINVAL before reaching the game. See SPEC_9P_FILESYSTEM.md for ranges and units.

            Per-biome materials, procedural modifiers and ground clutter are not exposed. reset
            restores the values as they were before gatOS first wrote them (per body; the global
            wireframe toggle is just written back). The same fields work over HTTP /v1 and MQTT.

            """;

        private VfsDirectory DebugVesselDir(string sanitized, string vesselId)
            => _debugVesselDirs.GetOrAdd(new NodeKey(sanitized, vesselId),
                static (key, self) => self.CreateDebugVesselDir(key.Name, key.Id), this);

        private VfsDirectory CreateDebugVesselDir(string sanitized, string vesselId)
        {
            var sink = _commands!;
            var q = $"debug/vessels/{sanitized}";
            var always = new VfsNode[]
            {
                VectorControl($"{q}/teleport", "teleport", vesselId, "debug.teleport", SimCommand.NoOrdinal, 6,
                    () => "0 0 0 0 0 0"),
                // One-shot impulsive kick. Write: "<x> <y> <z> [cci|body] [ns|dv]" — a 3-vector in
                // the parent-CCI frame (default) or the vessel body frame (+X = nose), in
                // newton-seconds (default; Δv = J/mass at application) or directly as Δv m/s.
                // Keywords may follow the numbers in any order. Read = "0 0 0" (no read-back).
                LineControlFile.Create("impulse", Qid($"{q}/impulse"), sink,
                    () => "0 0 0", line => ParseImpulse(vesselId, line)),
                new TriggerFile("refill_fuel", Qid($"{q}/refill_fuel"), sink,
                    new SimCommand(vesselId, "debug.refill_fuel", SimCommand.NoOrdinal, 1)),
                new TriggerFile("refill_battery", Qid($"{q}/refill_battery"), sink,
                    new SimCommand(vesselId, "debug.refill_battery", SimCommand.NoOrdinal, 1)),
                // Weld this vessel (source) to a target part with an explicit pose. Write:
                //   "<target_id> <part_iid> <x> <y> <z> <pitch> <yaw> <roll> <lock>"
                // (part_iid 0 = anchor to the target body frame; lock 0/1). Read = the current spec or "".
                LineControlFile.Create("weld", Qid($"{q}/weld"), sink,
                    () => WeldReadback(vesselId), line => ParseWeld(vesselId, line)),
                // Weld at the CURRENT relative pose (captured now). Write: "<target_id> <part_iid> [<lock>]".
                LineControlFile.Create("weld_here", Qid($"{q}/weld_here"), sink,
                    () => WeldReadback(vesselId), line => ParseWeldHere(vesselId, line)),
                // Remove this source's weld.
                new TriggerFile("unweld", Qid($"{q}/unweld"), sink,
                    new SimCommand(vesselId, "debug.weld_remove", SimCommand.NoOrdinal, 1)),
            };
            // Per-docking-port cheat knobs (only when the vessel carries docking ports). Presence is
            // re-checked per list/lookup via a non-throwing lookup — the vessel may be gone mid-walk.
            var docking = DebugDockingDir(q, vesselId);

            return new DelegateDirectory(sanitized, Qid(q),
                () =>
                {
                    var children = new List<VfsNode>(always.Length + 1);
                    children.AddRange(always);
                    if (SnapshotIndex.VesselById(_store.Current, vesselId)?.Docking.Count > 0)
                        children.Add(docking);
                    return children;
                },
                name => name == "docking"
                    ? SnapshotIndex.VesselById(_store.Current, vesselId)?.Docking.Count > 0 ? docking : null
                    : FindByName(always, name));
        }

        private VfsDirectory DebugDockingDir(string q, string vesselId)
            => IndexedDir("docking", $"{q}/docking",
                () => Vessel(vesselId).Docking.Count,
                index => DebugDockingPortDir(q, vesselId, index));

        private VfsDirectory DebugDockingPortDir(string q, string vesselId, int index)
            => DelegateDirectory.Fixed($"{index}", Qid($"{q}/docking/{index}"),
                // Read shows the live impulse value in N·s (stock 7000); write overwrites
                // DockingPort.PushoffImpulse, the separation impulse the regular docking/<n>/undock
                // trigger then applies.
                NumberControl($"{q}/docking/{index}/pushoff_impulse", "pushoff_impulse", vesselId,
                    "debug.docking_pushoff", index, () => Formats.Scalar(Docking(vesselId, index).PushoffImpulseNs)));

        private VfsDirectory AnimationsDir(string p, string vesselId)
        {
            var basePath = $"{p}/animations";
            return IndexedDir("animations", basePath,
                () => Vessel(vesselId).Animations.Count,
                index => AnimationDir(basePath, vesselId, index.ToString(), index));
        }

        /// <summary>
        ///     Solar panels by index (KSA_GAME_INTEGRATION_PLAN §4.6): electrical reads always, plus
        ///     the deploy <c>goal</c>/<c>current</c>/<c>state</c> control when the panel has a deploy
        ///     animation (<see cref="SolarSnapshot.AnimationIndex"/>) and a sink is wired.
        /// </summary>
        private VfsDirectory SolarDir(string p, string vesselId)
            => IndexedDir("solar", $"{p}/solar",
                () => Vessel(vesselId).Solar.Count,
                index => SolarPanelDir(p, vesselId, index));

        private VfsDirectory SolarPanelDir(string p, string vesselId, int index)
        {
            var q = $"{p}/solar/{index}";
            var produced = Line($"{q}/produced", "produced", () => Formats.Scalar(Solar(vesselId, index).ProducedW));
            var occluded = Line($"{q}/occluded", "occluded", () => Formats.Flag(Solar(vesselId, index).Occluded));
            var sunAoa = Line($"{q}/sun_aoa", "sun_aoa", () => Formats.Scalar(Solar(vesselId, index).SunAoaDeg));
            var efficiency = Line($"{q}/efficiency", "efficiency",
                () => Formats.Scalar(Solar(vesselId, index).Efficiency));
            var trackerAngle = Line($"{q}/tracker_angle", "tracker_angle",
                () => Formats.Scalar(Solar(vesselId, index).TrackerAngleDeg));
            // The panel's animation ordinal is resolved at ACCESS time (it can move on a vehicle
            // edit) — the cached node never bakes a stale ordinal into its reads or its command.
            var goal = FractionControl($"{q}/goal", "goal", vesselId, "animation.goal",
                () => Solar(vesselId, index).AnimationIndex,
                () => Formats.Scalar(Anim(vesselId, Solar(vesselId, index).AnimationIndex).GoalFraction));
            var current = Line($"{q}/current", "current",
                () => Formats.Scalar(Anim(vesselId, Solar(vesselId, index).AnimationIndex).CurrentFraction));
            var state = Line($"{q}/state", "state",
                () => Anim(vesselId, Solar(vesselId, index).AnimationIndex).DeploymentState);

            return new DelegateDirectory($"{index}", Qid(q),
                () =>
                {
                    var panel = Solar(vesselId, index);
                    var children = new List<VfsNode>(8) { produced, occluded, sunAoa, efficiency };
                    if (panel.HasTracker)
                        children.Add(trackerAngle);
                    if (panel.AnimationIndex >= 0)
                    {
                        children.Add(goal);
                        children.Add(current);
                        children.Add(state);
                    }

                    return children;
                },
                name =>
                {
                    var panel = Solar(vesselId, index);
                    return name switch
                    {
                        "produced" => produced,
                        "occluded" => occluded,
                        "sun_aoa" => sunAoa,
                        "efficiency" => efficiency,
                        "tracker_angle" => panel.HasTracker ? trackerAngle : null,
                        "goal" => panel.AnimationIndex >= 0 ? goal : null,
                        "current" => panel.AnimationIndex >= 0 ? current : null,
                        "state" => panel.AnimationIndex >= 0 ? state : null,
                        _ => null,
                    };
                });
        }

        private VfsDirectory RcsDir(string p, string vesselId)
            => IndexedDir("rcs", $"{p}/rcs",
                () => Vessel(vesselId).Rcs.Count,
                index => RcsThrusterDir(p, vesselId, index));

        private VfsDirectory RcsThrusterDir(string p, string vesselId, int index)
        {
            var q = $"{p}/rcs/{index}";
            return DelegateDirectory.Fixed($"{index}", Qid(q),
                FlagControl($"{q}/active", "active", vesselId, "rcs.active", index,
                    () => Formats.Flag(Rcs(vesselId, index).Active)),
                Line($"{q}/propellant", "propellant", () => Formats.Flag(Rcs(vesselId, index).PropellantAvailable)),
                Line($"{q}/map", "map", () => Rcs(vesselId, index).ControlMap));
        }

        private VfsDirectory GeneratorsDir(string p, string vesselId)
            => IndexedDir("generators", $"{p}/generators",
                () => Vessel(vesselId).Generators.Count,
                index => GeneratorDir(p, vesselId, index));

        private VfsDirectory GeneratorDir(string p, string vesselId, int index)
            => DelegateDirectory.Fixed($"{index}", Qid($"{p}/generators/{index}"),
                Line($"{p}/generators/{index}/active", "active", () => Formats.Flag(Generator(vesselId, index).Active)),
                Line($"{p}/generators/{index}/produced", "produced",
                    () => Formats.Scalar(Generator(vesselId, index).ProducedW)));

        private VfsDirectory LightsDir(string p, string vesselId)
            => IndexedDir("lights", $"{p}/lights",
                () => Vessel(vesselId).Lights.Count,
                index => LightDir(p, vesselId, index));

        /// <summary>
        ///     One light by index: <c>on</c>/<c>brightness</c>/<c>color</c>/<c>inner_angle</c>/<c>outer_angle</c>
        ///     always, plus the co-located actuate <c>goal</c>/<c>current</c>/<c>state</c> control when the
        ///     light part carries a deploy animation (<see cref="LightSnapshot.AnimationIndex"/>). The same
        ///     vessel-level animation is also reachable under <c>animations/&lt;n&gt;/</c>; both route
        ///     the one <c>animation.goal</c> action by its ordinal (mirrors <see cref="SolarPanelDir"/>).
        /// </summary>
        private VfsDirectory LightDir(string p, string vesselId, int index)
        {
            var q = $"{p}/lights/{index}";
            var always = new VfsNode[]
            {
                FlagControl($"{q}/on", "on", vesselId, "light.on", index, () => Formats.Flag(Light(vesselId, index).On)),
                NumberControl($"{q}/brightness", "brightness", vesselId, "light.brightness", index,
                    () => Formats.Scalar(Light(vesselId, index).Intensity)),
                VectorControl($"{q}/color", "color", vesselId, "light.color", index, 3,
                    () => Formats.Vector(Light(vesselId, index).Color)),
                NumberControl($"{q}/outer_angle", "outer_angle", vesselId, "light.outer_angle", index,
                    () => Formats.Scalar(Light(vesselId, index).OuterAngleDeg)),
                NumberControl($"{q}/inner_angle", "inner_angle", vesselId, "light.inner_angle", index,
                    () => Formats.Scalar(Light(vesselId, index).InnerAngleDeg)),
            };
            // Like SolarPanelDir: the light part's animation ordinal resolves at access time.
            var goal = FractionControl($"{q}/goal", "goal", vesselId, "animation.goal",
                () => Light(vesselId, index).AnimationIndex,
                () => Formats.Scalar(Anim(vesselId, Light(vesselId, index).AnimationIndex).GoalFraction));
            var current = Line($"{q}/current", "current",
                () => Formats.Scalar(Anim(vesselId, Light(vesselId, index).AnimationIndex).CurrentFraction));
            var state = Line($"{q}/state", "state",
                () => Anim(vesselId, Light(vesselId, index).AnimationIndex).DeploymentState);

            return new DelegateDirectory($"{index}", Qid(q),
                () =>
                {
                    var light = Light(vesselId, index);
                    var children = new List<VfsNode>(always.Length + 3);
                    children.AddRange(always);
                    if (light.AnimationIndex >= 0)
                    {
                        children.Add(goal);
                        children.Add(current);
                        children.Add(state);
                    }

                    return children;
                },
                name =>
                {
                    var light = Light(vesselId, index);
                    return name switch
                    {
                        "goal" => light.AnimationIndex >= 0 ? goal : null,
                        "current" => light.AnimationIndex >= 0 ? current : null,
                        "state" => light.AnimationIndex >= 0 ? state : null,
                        _ => FindByName(always, name),
                    };
                });
        }

        private VfsDirectory DockingDir(string p, string vesselId)
            => IndexedDir("docking", $"{p}/docking",
                () => Vessel(vesselId).Docking.Count,
                index => DockingPortDir(p, vesselId, index));

        private VfsDirectory DockingPortDir(string p, string vesselId, int index)
        {
            var q = $"{p}/docking/{index}";
            var children = new List<VfsNode>
            {
                Line($"{q}/docked", "docked", () => Formats.Flag(Docking(vesselId, index).Docked)),
                Line($"{q}/docked_to", "docked_to", () => Docking(vesselId, index).DockedToPart ?? ""),
                Line($"{q}/pushoff_impulse", "pushoff_impulse",
                    () => Formats.Scalar(Docking(vesselId, index).PushoffImpulseNs)),
            };
            // Undock (G4): a one-shot TRIGGER mirroring decoupler fire — write 1 to separate this
            // docked port. Only present when a command sink is wired.
            if (_commands is { } sink)
                children.Add(new TriggerFile("undock", Qid($"{q}/undock"), sink,
                    new SimCommand(vesselId, "docking.undock", index, 1)));
            return DelegateDirectory.Fixed($"{index}", Qid(q), children.ToArray());
        }

        private VfsDirectory DecouplersDir(string p, string vesselId)
            => IndexedDir("decouplers", $"{p}/decouplers",
                () => Vessel(vesselId).Decouplers.Count,
                index => DecouplerDir(p, vesselId, index));

        private VfsDirectory DecouplerDir(string p, string vesselId, int index)
        {
            var q = $"{p}/decouplers/{index}";
            var children = new List<VfsNode>
            {
                Line($"{q}/fired", "fired", () => Formats.Flag(Decoupler(vesselId, index).Fired)),
                // rev 5132: a player-disabled decoupler cannot fire — `fire` returns EOPNOTSUPP.
                Line($"{q}/enabled", "enabled", () => Formats.Flag(Decoupler(vesselId, index).Enabled)),
            };
            if (_commands is { } sink)
                children.Add(new TriggerFile("fire", Qid($"{q}/fire"), sink,
                    new SimCommand(vesselId, "decoupler.fire", index, 1)));
            return DelegateDirectory.Fixed($"{index}", Qid(q), children.ToArray());
        }

        private VfsDirectory AnimationDir(string basePath, string vesselId, string entryName, int animIndex)
        {
            var q = $"{basePath}/{entryName}";
            return DelegateDirectory.Fixed(entryName, Qid(q),
                FractionControl($"{q}/goal", "goal", vesselId, "animation.goal", animIndex,
                    () => Formats.Scalar(Anim(vesselId, animIndex).GoalFraction)),
                Line($"{q}/current", "current", () => Formats.Scalar(Anim(vesselId, animIndex).CurrentFraction)),
                Line($"{q}/state", "state", () => Anim(vesselId, animIndex).DeploymentState));
        }

        private VfsDirectory TanksDir(string p, string vesselId)
        {
            var cache = new ConcurrentDictionary<string, VfsNode>(StringComparer.Ordinal);
            Func<string, VfsNode> create = tankName => TankDir(p, vesselId, tankName);
            return new DelegateDirectory("tanks", Qid($"{p}/tanks"),
                () =>
                {
                    var tanks = SanitizedTanks(vesselId);
                    var children = new VfsNode[tanks.Count];
                    for (var i = 0; i < tanks.Count; i++)
                        children[i] = cache.GetOrAdd(tanks[i].Name, create);
                    return children;
                },
                name =>
                {
                    var tanks = SanitizedTanks(vesselId);
                    for (var i = 0; i < tanks.Count; i++)
                        if (tanks[i].Name == name)
                            return cache.GetOrAdd(name, create);
                    return null;
                });
        }

        private VfsDirectory TankDir(string p, string vesselId, string tankName)
            => DelegateDirectory.Fixed(tankName, Qid($"{p}/tanks/{tankName}"),
                Line($"{p}/tanks/{tankName}/amount", "amount",
                    () => Formats.Scalar(Tank(vesselId, tankName).Amount)),
                Line($"{p}/tanks/{tankName}/capacity", "capacity",
                    () => Formats.Scalar(Tank(vesselId, tankName).Capacity)),
                Line($"{p}/tanks/{tankName}/fraction", "fraction",
                    () => Formats.Scalar(Tank(vesselId, tankName).Fraction)));

        // ---- live accessors (ENOENT when the entity vanished — OS_PLAN.md T7.1/T8.2) -------
        // All closure-free (GP1): id lookups hit the per-roster dictionaries; per-module lookups
        // index directly (module Index == list position, the sampler invariant) with a linear
        // fallback rather than a per-call LINQ lambda.

        private VesselSnapshot Vessel(string vesselId)
            => Vessels(_store.Current).ById.TryGetValue(vesselId, out var entry)
                ? entry.Item
                : throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' is gone");

        private OrbitSnapshot Orbit(string vesselId)
            => Vessel(vesselId).Orbit
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' is not in orbit");

        private double Battery(string vesselId)
            => Vessel(vesselId).BatteryChargeFraction
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' has no battery");

        private static T ByIndex<T>(IReadOnlyList<T> items, int index, Func<T, int> indexOf, string what)
            where T : class
        {
            if ((uint)index < (uint)items.Count && indexOf(items[index]) == index)
                return items[index];
            for (var i = 0; i < items.Count; i++)
                if (indexOf(items[i]) == index)
                    return items[i];
            throw new VfsErrorException(LinuxErrno.ENOENT, $"{what} {index} is gone");
        }

        private EngineSnapshot Engine(string vesselId, int index)
            => ByIndex(Vessel(vesselId).Engines, index, static e => e.Index, "engine");

        private AnimationSnapshot Anim(string vesselId, int index)
            => ByIndex(Vessel(vesselId).Animations, index, static a => a.Index, "animation");

        private TankSnapshot Tank(string vesselId, string tankName)
        {
            var tanks = SanitizedTanks(vesselId);
            for (var i = 0; i < tanks.Count; i++)
                if (tanks[i].Name == tankName)
                    return tanks[i].Tank;
            throw new VfsErrorException(LinuxErrno.ENOENT, $"tank '{tankName}' is gone");
        }

        private NavballSnapshot Navball(string vesselId)
            => Vessel(vesselId).Navball
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' has no navball");

        private EnvironmentSnapshot Env(string vesselId)
            => Vessel(vesselId).Environment
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' has no environment");

        private RcsSnapshot Rcs(string vesselId, int index)
            => ByIndex(Vessel(vesselId).Rcs, index, static r => r.Index, "rcs");

        private SolarSnapshot Solar(string vesselId, int index)
            => ByIndex(Vessel(vesselId).Solar, index, static s => s.Index, "solar");

        private GeneratorSnapshot Generator(string vesselId, int index)
            => ByIndex(Vessel(vesselId).Generators, index, static g => g.Index, "generator");

        private LightSnapshot Light(string vesselId, int index)
            => ByIndex(Vessel(vesselId).Lights, index, static l => l.Index, "light");

        private DockingSnapshot Docking(string vesselId, int index)
            => ByIndex(Vessel(vesselId).Docking, index, static d => d.Index, "docking");

        private DecouplerSnapshot Decoupler(string vesselId, int index)
            => ByIndex(Vessel(vesselId).Decouplers, index, static d => d.Index, "decoupler");

        private SrbSnapshot Srb(string vesselId, int index)
            => ByIndex(Vessel(vesselId).Srb, index, static s => s.Index, "srb");

        private SrbSegmentSnapshot SrbSegment(string vesselId, int srbIndex, int index)
            => ByIndex(Srb(vesselId, srbIndex).Segments, index, static s => s.Index, "srb segment");

        private PartSnapshot Part(string vesselId, int index)
            => ByIndex(Vessel(vesselId).Parts, index, static pt => pt.Index, "part");

        private SubpartSnapshot Subpart(string vesselId, int partIndex, int index)
            => ByIndex(Part(vesselId, partIndex).Subparts, index, static sp => sp.Index, "subpart");

        private WeldSnapshot Weld(string sourceId)
            => FindWeld(sourceId)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"weld for '{sourceId}' is gone");

        private WeldSnapshot? FindWeld(string sourceId)
        {
            var welds = _store.Current.Welds;
            for (var i = 0; i < welds.Count; i++)
                if (welds[i].SourceId == sourceId)
                    return welds[i];
            return null;
        }

        private ThugLifeSnapshot ThugLife(int id)
        {
            var entries = _store.Current.ThugLife;
            for (var i = 0; i < entries.Count; i++)
                if (entries[i].Id == id)
                    return entries[i];
            throw new VfsErrorException(LinuxErrno.ENOENT, $"thug_life entry {id} is gone");
        }

        /// <summary>The current weld spec for a source (write-compatible), or "" when not welded.</summary>
        private string WeldReadback(string sourceId)
            => FindWeld(sourceId) is { } w ? Formats.WeldSpec(w) : "";

        /// <summary>
        ///     Parses an explicit weld line — <c>"&lt;target&gt; &lt;part_iid&gt; x y z pitch yaw roll lock"</c>
        ///     (9 tokens) — into a <c>debug.weld_create</c> command. Returns null (⇒ EINVAL) on any
        ///     malformed token: non-finite number, non-integer/negative <c>part_iid</c>, or <c>lock</c>∉{0,1}.
        /// </summary>
        private static SimCommand? ParseWeld(string sourceId, string line)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 9 || parts[0].Length == 0)
                return null;
            var values = new double[8]; // part_iid, x, y, z, pitch, yaw, roll, lock
            for (var i = 0; i < 8; i++)
                if (!double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                    || !double.IsFinite(values[i]))
                    return null;
            if (values[0] < 0 || values[0] != Math.Floor(values[0]) || values[7] is not (0 or 1))
                return null;
            return new SimCommand(sourceId, "debug.weld_create", SimCommand.NoOrdinal, 0)
            {
                Token = parts[0],
                Values = values,
            };
        }

        /// <summary>
        ///     Parses a capture-pose weld line — <c>"&lt;target&gt; &lt;part_iid&gt; [lock]"</c> (2–3 tokens) —
        ///     into a <c>debug.weld_here</c> command (the offset/rotation are captured on the game thread).
        /// </summary>
        private static SimCommand? ParseWeldHere(string sourceId, string line)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 2 or > 3 || parts[0].Length == 0)
                return null;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var part)
                || !double.IsFinite(part) || part < 0 || part != Math.Floor(part))
                return null;
            var lockRot = 1.0;
            if (parts.Length == 3
                && (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out lockRot)
                    || lockRot is not (0 or 1)))
                return null;
            return new SimCommand(sourceId, "debug.weld_here", SimCommand.NoOrdinal, 0)
            {
                Token = parts[0],
                Values = [part, lockRot],
            };
        }

        /// <summary>
        ///     Parses an impulse line — <c>"&lt;x&gt; &lt;y&gt; &lt;z&gt; [cci|body] [ns|dv]"</c> (3 finite
        ///     numbers, then at most one frame keyword and one unit keyword in any order) — into a
        ///     <c>debug.impulse</c> command: <c>Values</c> = the vector, <c>Token</c> = the frame
        ///     (null ⇒ cci), <c>Aux</c> = the unit (null ⇒ ns). Returns null (⇒ EINVAL) on any
        ///     malformed, unknown, or duplicated token.
        /// </summary>
        private static SimCommand? ParseImpulse(string vesselId, string line)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 3 or > 5)
                return null;
            var values = new double[3];
            for (var i = 0; i < 3; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                    || !double.IsFinite(values[i]))
                    return null;
            string? frame = null, unit = null;
            for (var i = 3; i < parts.Length; i++)
                switch (parts[i])
                {
                    case ImpulseRules.FrameCci or ImpulseRules.FrameBody when frame is null:
                        frame = parts[i];
                        break;
                    case ImpulseRules.UnitNs or ImpulseRules.UnitDv when unit is null:
                        unit = parts[i];
                        break;
                    default:
                        return null; // unknown or duplicated keyword
                }
            return new SimCommand(vesselId, "debug.impulse", SimCommand.NoOrdinal, 0)
            {
                Values = values,
                Token = frame,
                Aux = unit,
            };
        }

        private List<(string Name, TankSnapshot Tank)> SanitizedTanks(string vesselId)
            => SanitizeNames(Vessel(vesselId).Tanks, t => t.Resource);

        private static List<(string Name, WeldSnapshot Weld)> SanitizedWelds(SimSnapshot snapshot)
            => SanitizeNames(snapshot.Welds, w => w.SourceId);

        // ---- naming / qids -------------------------------------------------------------------

        /// <summary>
        ///     The memoized vessel roster for a snapshot (GP1): the sanitized-name list plus
        ///     name→vessel and id→(name, vessel) indexes, rebuilt only when the snapshot's vessel
        ///     list reference changes — every walk step used to re-sanitize the whole roster.
        /// </summary>
        private Roster<VesselSnapshot> Vessels(SimSnapshot snapshot)
        {
            var roster = _vesselRoster;
            if (roster is not null && ReferenceEquals(roster.Source, snapshot.Vessels))
                return roster;
            roster = BuildRoster(snapshot.Vessels, static v => v.Id);
            _vesselRoster = roster;
            return roster;
        }

        /// <summary>The memoized body roster (reference-keyed, so the GP3 bodies sub-cadence keeps one roster alive).</summary>
        private Roster<BodySnapshot> Bodies(SimSnapshot snapshot)
        {
            var roster = _bodyRoster;
            if (roster is not null && ReferenceEquals(roster.Source, snapshot.Bodies))
                return roster;
            roster = BuildRoster(snapshot.Bodies, static b => b.Id);
            _bodyRoster = roster;
            return roster;
        }

        private static Roster<T> BuildRoster<T>(IReadOnlyList<T> items, Func<T, string> key)
            where T : class
        {
            var sanitized = SanitizeNames(items, key);
            var byName = new Dictionary<string, T>(sanitized.Count, StringComparer.Ordinal);
            var byId = new Dictionary<string, (string Name, T Item)>(sanitized.Count, StringComparer.Ordinal);
            foreach (var (name, item) in sanitized)
            {
                byName.TryAdd(name, item);
                byId.TryAdd(key(item), (name, item));
            }

            return new Roster<T> { Source = items, Sanitized = sanitized, ByName = byName, ById = byId };
        }

        /// <summary>
        ///     Maps items to unique directory names: anything outside <c>[A-Za-z0-9._-]</c>
        ///     becomes <c>_</c>; duplicates get <c>~2</c>, <c>~3</c>… in listing order
        ///     (OS_PLAN.md T8.2).
        /// </summary>
        private static List<(string Name, T Item)> SanitizeNames<T>(IReadOnlyList<T> items, Func<T, string> key)
        {
            var result = new List<(string, T)>(items.Count);
            var used = new Dictionary<string, int>();
            foreach (var item in items)
            {
                var name = Sanitize(key(item));
                if (used.TryGetValue(name, out var count))
                {
                    used[name] = count + 1;
                    name = $"{name}~{count + 1}";
                }
                else
                {
                    used[name] = 1;
                }

                result.Add((name, item));
            }

            return result;
        }

        private static string Sanitize(string id)
        {
            Span<char> chars = id.Length <= 64 ? stackalloc char[id.Length] : new char[id.Length];
            for (var i = 0; i < id.Length; i++)
            {
                var c = id[i];
                chars[i] = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                    or '.' or '_' or '-'
                    ? c
                    : '_';
            }

            var sanitized = new string(chars);
            return sanitized switch
            {
                "" => "_",
                "." or ".." => "_" + sanitized,
                _ => sanitized,
            };
        }

        /// <summary>
        ///     A snapshot-derived scalar leaf, memoized per published snapshot (GP1): one format
        ///     serves the Tgetattr + open + every concurrent reader until the next publish. Use
        ///     <see cref="LiveLine"/> for values that can change without a publish.
        /// </summary>
        private SnapshotTextFile Line(string qidPath, string name, Func<string> value)
            => new(name, Qid(qidPath), _store, () => value() + "\n");

        /// <summary>
        ///     A leaf whose value can change <b>without</b> a snapshot publish (live transport
        ///     state, display settings) — formatted on every access, never memoized.
        /// </summary>
        private StaticTextFile LiveLine(string qidPath, string name, Func<string> value)
            => new(name, Qid(qidPath), () => value() + "\n");

        private ulong Qid(string path)
            => _qids.GetOrAdd(path, _ => (ulong)Interlocked.Increment(ref _nextQid));
    }
}
