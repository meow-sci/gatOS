using System.Text;
using System.Text.Json;
using gatOS.Logging;
using Tomlyn;

namespace gatOS.GameMod.Configuration;

/// <summary>
///     User configuration for gatOS, persisted as <c>gatos.toml</c> in the data dir
///     (<see cref="gatOS.Vm.GatOsPaths.ConfigFile"/>; OS_PLAN.md T6.3). A pre-generated, fully
///     commented copy also ships in the mod folder (<see cref="gatOS.Vm.GatOsPaths.BundledConfigFile"/>)
///     so the common knobs (memory/CPUs/disk) are visible and editable before the first launch; on
///     first run the data-dir file is seeded from that copy when present. Loading never throws:
///     a missing file is seeded or created with defaults, an unparseable one is logged and replaced by
///     in-memory defaults while the file on disk is left untouched for the user to fix, and
///     out-of-range values are clamped with a warning.
/// </summary>
public sealed class GatOsConfig
{
    /// <summary>The config schema version this build reads and writes.</summary>
    public const int CurrentSchema = 1;

    // One cached options instance: TomlSerializerOptions compiles mapping metadata on first use.
    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const string FileHeader =
        """
        # gatOS configuration.
        #
        # The mod ships a template, gatos.default.toml, in its folder — edit it to set the common
        # options (memory, CPUs, disk) before you ever launch the game. On first launch gatOS copies
        # it to gatos.toml and from then on reads and writes gatos.toml (your live config, which
        # survives mod updates); in-game changes are saved there too. Delete gatos.toml to restore
        # every default. Out-of-range values are clamped (and logged), not rejected.
        #
        # Common settings are grouped first; the advanced surface follows.

        """;

    // The file layout: each serialized key is emitted under a section header with a short comment.
    // Common knobs come first so a player never has to scroll past the advanced surface. Tomlyn
    // still renders every value (quoting/escaping/bools) — this table only controls grouping,
    // ordering, and the inline docs. A key the table forgets still ships (see Serialize's catch-all),
    // so adding a property can never silently drop it from the file.
    private static readonly (string Title, (string Key, string Comment)[] Keys)[] Sections =
    {
        ("COMMON — most players only need to touch these", new[]
        {
            ("memory_mb", "Guest RAM in MiB. Bump it if you install heavy software."),
            ("cpus", "Guest virtual CPU count."),
            ("disk_size_gb",
                "Guest disk size in GiB (clamped 1..128). Grow-only: raising it expands the active\n"
                + "save's disk on the next boot; lowering it is a no-op (use Reset Disk to reclaim space)."),
            ("restrict_network", "true = guest gets no internet, only the gatOS channels (off = apk works)."),
            ("accel_override", "Force one accelerator: \"whpx\" | \"kvm\" | \"hvf\" | \"tcg\" (\"\" = auto ladder)."),
            ("cpu_model", "Override the guest CPU model (\"\" = auto; WHPX needs a named model, not host)."),
            ("boot_timeout_seconds", "Overall boot timeout in seconds; 0 = automatic (60 s accelerated / 300 s TCG)."),
        }),
        ("TELEMETRY — the /sim data feed (all tunable live in-game)", new[]
        {
            ("sample_rate_hz", "Master /sim sampling rate in Hz (clamped 1..120; retune live in-game)."),
            ("telemetry_enabled", "Master gate for sampling (false = /sim freezes; VM and shells unaffected)."),
            ("telemetry_vessel_detail", "Sample per-vessel detail (navball/environment/per-module); off = core only."),
            ("telemetry_vessel_parts", "Sample the per-vessel top-level parts list (/sim/.../parts; the welds anchor picker)."),
            ("telemetry_bodies", "Sample the celestial-body catalog + system summary (/sim/bodies, /sim/system)."),
            ("telemetry_bodies_rate_hz",
                "Bodies resample cadence in Hz (0 = every sample tick). Below the master rate, the\n"
                + "ticks between re-publish the same bodies/system data — cheaper, slightly staler."),
            ("telemetry_events", "Diff snapshots into /sim/events entries (and the event topics/streams)."),
        }),
        ("CONTROL — the /sim write surface", new[]
        {
            ("control_enabled", "Master switch for all /sim writes (false = every control write EACCES)."),
            ("control_all_vessels", "true = command any vessel; false = only the active vessel."),
            ("debug_namespace", "Expose the /sim/debug cheat surface (teleport / refuel / warp / switch vessel)."),
            ("command_timeout_ms", "How long a control write waits for the game thread before ETIMEDOUT."),
            ("max_commands_per_frame", "Upper bound on control commands executed per game frame."),
        }),
        ("TRANSPORTS — HTTP, MQTT, serial & bus bridges", new[]
        {
            ("http_enabled", "Serve the magic HTTP API (guest reaches it at $GATOS_HTTP / 10.0.2.2)."),
            ("http_bind_host", "Host IP to bind the HTTP API to (127.0.0.1 = host-local; 0.0.0.0 = all IPv4 interfaces)."),
            ("http_preferred_port", "Preferred HTTP port (4242); 0 = ephemeral only; falls back on a clash."),
            ("http_field_endpoints", "Serve the per-field /v1/fs/<path> filesystem mirror (reads + SSE + writes)."),
            ("mcp_enabled", "Serve the host-side MCP API for AI agents."),
            ("mcp_bind_host", "Host IP to bind the MCP API to (127.0.0.1 = host-local; 0.0.0.0 = all IPv4 interfaces)."),
            ("mcp_preferred_port", "Preferred MCP port (4243); 0 = ephemeral only; falls back on a clash."),
            ("mqtt_enabled", "Run the embedded MQTT broker (guest reaches it at $GATOS_MQTT / 10.0.2.2)."),
            ("mqtt_bind_host", "Host IP to bind MQTT to (127.0.0.1 = host-local; 0.0.0.0 = all IPv4 interfaces)."),
            ("mqtt_preferred_port", "Preferred MQTT port (1883); 0 = ephemeral only; falls back on a clash."),
            ("mqtt_field_topics", "Publish the per-field gatos/sim/<path> filesystem mirror (one topic per leaf)."),
            ("field_feed_hz", "Cadence of the MQTT field mirror in Hz (default 4; clamped 1..30)."),
            ("mqtt_publish_hz",
                "Cap on the MQTT world-topic cadence in Hz (0 = every snapshot). Below the sample\n"
                + "rate the broker coalesces to the newest snapshot — cheaper at high sample rates."),
            ("serial_telemetry_port", "Stream telemetry out over the gatos.serial virtio-serial port."),
            ("serial_command_port", "Accept SCPI command lines in over the gatos.serial virtio-serial port."),
            ("serial_mode", "Serial telemetry wire format: ndjson | nmea | ccsds (default ndjson)."),
            ("serial_interval_ms", "Serial telemetry cadence in milliseconds (default 500; clamped 50..60000)."),
            ("bus_ccsds", "Expose a CCSDS space-packet TM/TC feed (reserved — not yet served)."),
            ("bus_1553", "Expose a MIL-STD-1553 BC/RT framing feed (reserved — not yet served)."),
        }),
        ("DISPLAY — the screen stream (/sim/display; all tunable live over /sim)", new[]
        {
            ("display_enabled", "Boot seed for /sim/display/enabled. OFF by default; turn it on live with\n"
                + "`echo 1 > /sim/display/enabled` (and `echo 0 >` to stop). Capture costs nothing while off."),
            ("display_fps", "Stream cadence in Hz (clamped 1..60), decoupled from the game frame rate."),
            ("display_width", "Downscale target width in pixels (clamped 16..1920)."),
            ("display_height", "Downscale target height in pixels (clamped 16..1920)."),
            ("display_encoding", "Frame wire encoding: rgba-zlib (default; 3-10x smaller on the wire) | rgba\n"
                + "(raw fallback). rgba-zlib needs purrTTY's 2026-07-02+ native — older pins crash\n"
                + "on compressible o=z payloads (purrtty gotcha 34, fixed)."),
        }),
        ("PAINT — opt-in vehicle shaders and EVA material clones (/sim/paint)", new[]
        {
            ("paint_parts_enabled", "Boot seed for /sim/paint/parts/enabled. false keeps every shader hook absent."),
            ("paint_kittens_enabled", "Boot seed for /sim/paint/kittens/enabled. false creates no material clones."),
            ("paint_max_material_clones", "Hard cap on gatOS-owned EVA material clones (clamped 1..256)."),
            ("paint_textures_enabled", "Whether /sim/paint/textures exists. false removes the subtree entirely."),
            ("paint_texture_max_bytes", "Per-upload byte cap (EFBIG past it; clamped 64 KiB..256 MiB)."),
            ("paint_texture_max_total_bytes", "Store-wide upload byte cap (ENOSPC; clamped >= per-file..1 GiB)."),
            ("paint_texture_max_files", "Uploaded image count cap (ENOSPC; clamped 1..256)."),
            ("paint_texture_max_bindings", "Simultaneous texture override cap (ENOSPC; clamped 1..256)."),
            ("paint_texture_max_dimension", "Longest GPU edge; larger uploads downscale (clamped 16..16384)."),
            ("paint_stickers_enabled", "Whether /sim/paint/stickers exists. false removes the subtree entirely."),
            ("paint_stickers_max_count", "Maximum simultaneous sticker decals (clamped 1..4096)."),
            ("paint_stickers_max_view_distance_m", "Metres past which a sticker is not drawn (clamped 10..1e6)."),
        }),
        ("AUDIO — userland playback through the game's speakers (/sim/audio)", new[]
        {
            ("audio_enabled", "Serve /sim/audio: upload clips (cat x.mp3 > /sim/audio/file/x.mp3) and play\n"
                + "them through the game's FMOD mixer. false removes the surface entirely."),
            ("audio_max_clip_bytes", "Per-clip size cap in bytes (a write past it fails EFBIG)."),
            ("audio_max_total_bytes", "Store-wide byte cap across all clips (uploads past it fail ENOSPC)."),
            ("audio_max_clips", "Maximum number of uploaded clips (ENOSPC past it)."),
            ("audio_max_channels", "Maximum concurrent playback channels (play past it fails EBUSY)."),
        }),
        ("CAMERA — programmable cinematic camera (/sim/camera)", new[]
        {
            ("camera_enabled", "Serve /sim/camera: take the camera, fly it, follow and frame things, then\n"
                + "hand it back. false removes the surface entirely."),
            ("camera_max_tracks", "Maximum uploaded camera tracks (ENOSPC past it)."),
            ("camera_max_track_bytes", "Per-track JSON size cap in bytes (an upload past it fails EFBIG)."),
            ("camera_max_total_bytes", "Store-wide byte cap across all tracks (uploads past it fail ENOSPC)."),
            ("camera_max_keys", "Maximum keyframes per animated channel (EINVAL past it)."),
            ("camera_fov_min", "Lower field-of-view bound in degrees — deliberately wider than the game's own\n"
                + "15, since the engine's SetFieldOfView is unclamped."),
            ("camera_fov_max", "Upper field-of-view bound in degrees — deliberately wider than the game's own\n"
                + "120, so telephoto shots are available."),
            ("camera_release_blend_s", "Default eased hand-back in seconds when the director releases the camera\n"
                + "(the game's own CameraJumpTime default is 0.6)."),
            ("camera_allow_time_channel", "Allow camera tracks to drive simulation speed; additionally requires\n"
                + "debug_namespace."),
        }),
        ("SCHEDULE — host-side timed command sequences (/sim/ctl/timed_batch)", new[]
        {
            ("schedule_enabled", "Serve /sim/ctl/timed_batch + /sim/ctl/schedules: any control leaf, any time\n"
                + "offset, replayed host-side. false removes both entirely."),
            ("schedule_max_live", "Maximum concurrent live schedules (EINVAL past it)."),
            ("schedule_max_entries", "Maximum timed entries per schedule (EINVAL past it)."),
            ("schedule_max_bytes", "Per-schedule buffered payload cap in bytes (EINVAL past it)."),
            ("schedule_default_clock", "Clock base for a schedule that does not declare @clock: render (frames) |\n"
                + "wall (host time) | ut (simulation time, so warp scales it)."),
        }),
        ("IVA — free-floating cabin objects (/sim/debug/iva; all tunable live over /sim)", new[]
        {
            ("iva_physics_enabled",
                "Boot seed for /sim/debug/iva/enabled — the master switch for the whole cabin-physics\n"
                + "feature. OFF by default, and off means off: no physics simulation, no interior\n"
                + "collision mesh, no per-frame work. Turn it on live with\n"
                + "`echo 1 > /sim/debug/iva/enabled` (and `echo 0 >` to release everything and stop)."),
            ("iva_run_outside_iva",
                "Keep simulating when no viewport is in the IVA camera. Off = leaving IVA parks the\n"
                + "objects (velocities zeroed, poses frozen) until you come back."),
            ("iva_max_objects", "Cap on floating objects per vessel (an adopt past it fails EBUSY)."),
            ("iva_max_object_size",
                "Largest bounding-box extent, metres, a SubPart may have and still be adoptable — the\n"
                + "guard that stops adopt_all (or a mistyped id) cutting a hull panel or a seat loose."),
            ("iva_density_kg_m3", "Density used to derive an object's mass from its collision-proxy volume."),
            ("iva_max_speed", "Hard velocity clamp, m/s — the anti-tunnelling guard for thin art meshes."),
            ("iva_friction", "Contact friction coefficient; higher makes a settled object stay put."),
            ("iva_restitution", "Bounciness, as the maximum contact recovery velocity in m/s."),
            ("iva_substep_hz", "Fixed integration rate, Hz. A variable-dt contact sim is not stable."),
            ("iva_max_substeps_per_frame", "Substep budget per frame — the post-hitch catch-up bound."),
            ("iva_double_sided_interior",
                "Emit every interior triangle in both windings, so an object cannot fall through a wall\n"
                + "whose art happens to wind outward. On by default; off halves the triangle count."),
            ("iva_impact_speed", "Speed change (m/s) in one substep that fires an iva.impact event."),
        }),
    };

    /// <summary>Schema version of the file (readers reject anything but <see cref="CurrentSchema"/>).</summary>
    public int Schema { get; set; } = CurrentSchema;

    // ---- COMMON: the knobs a player is most likely to change (no in-game UI; hand-edit + relaunch). ----

    /// <summary>Guest RAM in MiB (OS_ANALYSIS §3.3 default).</summary>
    public int MemoryMb { get; set; } = 256;

    /// <summary>Guest vCPU count.</summary>
    public int Cpus { get; set; } = 2;

    /// <summary>
    ///     Guest disk size in GiB (clamped 1..128). The base image ships small; before boot the host
    ///     grows the active save's overlay to this size (grow-only) and the guest expands its ext4 to
    ///     fill it. Raise it for heavy software (compilers, big package installs); lowering it is a
    ///     no-op (disks never shrink — use Reset Disk to reclaim space).
    /// </summary>
    public int DiskSizeGb { get; set; } = 8;

    /// <summary>
    ///     When <c>true</c>, the guest is launched with <c>-netdev user,restrict=on</c> (no outbound
    ///     NAT; "offline ship computer"). Defaults to open NAT so real apk mirrors work (D3).
    /// </summary>
    public bool RestrictNetwork { get; set; }

    /// <summary>Forces one accelerator (<c>""</c> = auto ladder; see <c>VmHostOptions.AccelOverride</c>).</summary>
    public string AccelOverride { get; set; } = "";

    /// <summary>
    ///     Overrides the guest CPU model (<c>""</c> = auto: <c>host</c> on KVM/HVF, a named model on
    ///     WHPX, <c>max</c> on TCG; see <c>VmHostOptions.CpuModel</c>). WHPX cannot run <c>host</c>.
    /// </summary>
    public string CpuModel { get; set; } = "";

    /// <summary>Overall boot timeout in seconds; 0 = automatic (60 s accelerated / 300 s TCG).</summary>
    public int BootTimeoutSeconds { get; set; }

    // ---- TELEMETRY: the /sim data feed (all live-tunable from the in-game menu/status window). ----

    /// <summary>Master telemetry sampling rate for the <c>/sim</c> tree, Hz (consumed by the M9 sampler).</summary>
    public int SampleRateHz { get; set; } = 10;

    /// <summary>Master gate for telemetry sampling; <c>false</c> freezes <c>/sim</c> at the last frame.</summary>
    public bool TelemetryEnabled { get; set; } = true;

    /// <summary>
    ///     Sample the per-vessel detail pass (G3: navball, environment, RCS/solar/generators/lights/
    ///     docking/decouplers/encounters, orbit extras, throttle/power read-backs). The heaviest
    ///     per-vessel work; <c>false</c> keeps only the core flight telemetry.
    /// </summary>
    public bool TelemetryVesselDetail { get; set; } = true;

    /// <summary>
    ///     Sample the per-vessel top-level parts list (<c>/sim/vessels/by-id/&lt;id&gt;/parts</c>) — the
    ///     anchor picker for the welds cheat. Cached per vehicle (rebuilt on part-count change or every
    ///     10 s); <c>false</c> skips the read and removes the subtree on every transport.
    /// </summary>
    public bool TelemetryVesselParts { get; set; } = true;

    /// <summary>Sample the celestial-body catalog and system summary (<c>/sim/bodies</c>, <c>/sim/system</c>).</summary>
    public bool TelemetryBodies { get; set; } = true;

    /// <summary>
    ///     Bodies resample cadence in Hz; <c>0</c> (the default) resamples on every master tick.
    ///     Below the master rate, the ticks between re-publish the same bodies/system data by
    ///     reference (cheaper, slightly staler — GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP3).
    /// </summary>
    public int TelemetryBodiesRateHz { get; set; }

    /// <summary>Diff consecutive snapshots into <c>/sim/events</c> entries (and the event streams/topics).</summary>
    public bool TelemetryEvents { get; set; } = true;

    // ---- CONTROL: the /sim write surface. ----

    /// <summary>Master switch for all <c>/sim</c> control writes (KSA_GAME_INTEGRATION_PLAN Part 7).</summary>
    public bool ControlEnabled { get; set; } = true;

    /// <summary>When false, only the active vessel is commandable (G-D1); default commands any vessel.</summary>
    public bool ControlAllVessels { get; set; } = true;

    /// <summary>Exposes the <c>/sim/debug</c> cheat surface (G-D2; reserved for the G4 control surface).</summary>
    public bool DebugNamespace { get; set; } = true;

    /// <summary>How long a control write waits for the game thread before returning ETIMEDOUT.</summary>
    public int CommandTimeoutMs { get; set; } = 2000;

    /// <summary>Upper bound on control commands executed per game frame.</summary>
    public int MaxCommandsPerFrame { get; set; } = 64;

    // ---- TRANSPORTS: the HTTP, MQTT, serial and bus bridges. ----

    /// <summary>Serve the magic HTTP API (KSA_GAME_INTEGRATION_PLAN Part 6 T2 / Part 7).</summary>
    public bool HttpEnabled { get; set; } = true;

    /// <summary>IP address the HTTP API binds; loopback by default.</summary>
    public string HttpBindHost { get; set; } = "127.0.0.1";

    /// <summary>Preferred HTTP port (4242); 0 = ephemeral only; falls back to ephemeral on a clash.</summary>
    public int HttpPreferredPort { get; set; } = 4242;

    /// <summary>Serve the per-field <c>/v1/fs/&lt;path&gt;</c> filesystem mirror (reads, SSE, writes).</summary>
    public bool HttpFieldEndpoints { get; set; } = true;

    /// <summary>Serve the first-class host-side MCP endpoint for AI agents.</summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>IP address the MCP endpoint binds; loopback by default.</summary>
    public string McpBindHost { get; set; } = "127.0.0.1";

    /// <summary>Preferred MCP port (4243); 0 = ephemeral only; falls back to ephemeral on a clash.</summary>
    public int McpPreferredPort { get; set; } = 4243;

    /// <summary>Run the embedded MQTT broker (an additional game-data bridge).</summary>
    public bool MqttEnabled { get; set; } = true;

    /// <summary>IP address the MQTT broker binds; loopback by default.</summary>
    public string MqttBindHost { get; set; } = "127.0.0.1";

    /// <summary>Preferred MQTT port (1883); 0 = ephemeral only; falls back to ephemeral on a clash.</summary>
    public int MqttPreferredPort { get; set; } = 1883;

    /// <summary>Publish the per-field <c>gatos/sim/&lt;path&gt;</c> filesystem mirror (one topic per leaf).</summary>
    public bool MqttFieldTopics { get; set; } = true;

    /// <summary>
    ///     MQTT world-topic publish cadence cap in Hz; <c>0</c> (the default) publishes on every
    ///     snapshot. Below the sample rate the broker coalesces to the newest snapshot — useful at
    ///     high sample rates so MQTT consumers stop paying ×N the serialization
    ///     (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP2).
    /// </summary>
    public int MqttPublishHz { get; set; }

    /// <summary>Cadence of the MQTT field mirror, Hz (clamped 1..30); throttled below the sample rate.</summary>
    public int FieldFeedHz { get; set; } = 4;

    /// <summary>Stream telemetry out over the <c>gatos.serial</c> virtio-serial port (G7).</summary>
    public bool SerialTelemetryPort { get; set; }

    /// <summary>Accept SCPI command lines in over the <c>gatos.serial</c> virtio-serial port (G7).</summary>
    public bool SerialCommandPort { get; set; }

    /// <summary>Serial telemetry wire format: <c>ndjson</c> | <c>nmea</c> | <c>ccsds</c> (G7).</summary>
    public string SerialMode { get; set; } = "ndjson";

    /// <summary>Serial telemetry cadence in milliseconds (clamped to 50..60000).</summary>
    public int SerialIntervalMs { get; set; } = 500;

    /// <summary>Expose a CCSDS space-packet TM/TC feed (G7; reserved — not yet served).</summary>
    public bool BusCcsds { get; set; }

    /// <summary>Expose a MIL-STD-1553 BC/RT framing feed (G7; reserved — not yet served).</summary>
    public bool Bus1553 { get; set; }

    // ---- DISPLAY: the screen stream (/sim/display; STREAM_PLAN.md). Runtime control is the /sim files. ----

    /// <summary>Boot seed for <c>/sim/display/enabled</c>; <c>false</c> (off) by default.</summary>
    public bool DisplayEnabled { get; set; }

    /// <summary>Boot seed for the stream cadence in Hz (clamped 1..60), decoupled from the game frame rate.</summary>
    public int DisplayFps { get; set; } = 15;

    /// <summary>Boot seed for the downscale target width in pixels (clamped 16..1920).</summary>
    public int DisplayWidth { get; set; } = 320;

    /// <summary>Boot seed for the downscale target height in pixels (clamped 16..1920).</summary>
    public int DisplayHeight { get; set; } = 180;

    /// <summary>
    ///     Boot seed for the frame wire encoding: <c>rgba</c> (default) | <c>rgba-zlib</c>.
    ///     Zlib is quarantined from the default: purrTTY's pinned libghostty-vt native
    ///     memory-corrupts on <c>o=z</c> payloads of compressible data (purrtty gotcha 34).
    /// </summary>
    public string DisplayEncoding { get; set; } = "rgba-zlib";

    // ---- PAINT: runtime opt-in vehicle shaders and EVA material clones (/sim/paint). ----

    /// <summary>Boot seed for the part shader master. Runtime opt-in/out remains writable.</summary>
    public bool PaintPartsEnabled { get; set; }

    /// <summary>Boot seed for the EVA material clone master. Runtime opt-in/out remains writable.</summary>
    public bool PaintKittensEnabled { get; set; }

    /// <summary>Hard bound below KSA's fixed 512-entry material buffer.</summary>
    public int PaintMaxMaterialClones { get; set; } = 64;

    /// <summary>Whether /sim/paint/textures exists at all. false removes the subtree entirely.</summary>
    public bool PaintTexturesEnabled { get; set; } = true;

    /// <summary>Per-upload byte cap (EFBIG past it).</summary>
    public int PaintTextureMaxBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Store-wide byte cap across committed and in-flight uploads (ENOSPC past it).</summary>
    public int PaintTextureMaxTotalBytes { get; set; } = 128 * 1024 * 1024;

    /// <summary>Maximum uploaded image count (ENOSPC past it).</summary>
    public int PaintTextureMaxFiles { get; set; } = 32;

    /// <summary>Maximum simultaneous texture overrides (ENOSPC past it).</summary>
    public int PaintTextureMaxBindings { get; set; } = 32;

    /// <summary>Longest GPU edge; larger uploads are downscaled at bind rather than rejected.</summary>
    public int PaintTextureMaxDimension { get; set; } = 4096;

    /// <summary>Whether /sim/paint/stickers exists at all. false removes the subtree entirely.</summary>
    public bool PaintStickersEnabled { get; set; } = true;

    /// <summary>Maximum simultaneous sticker decals (the registry refuses past it).</summary>
    public int PaintStickersMaxCount { get; set; } = 256;

    /// <summary>Metres past which a sticker is culled from the draw list entirely.</summary>
    public double PaintStickersMaxViewDistanceM { get; set; } = 5000.0;

    // ---- AUDIO: userland playback through the game's FMOD (/sim/audio; GATOS_CUSTOM_AUDIO_PLAN). ----

    /// <summary>Serve the <c>/sim/audio</c> surface; <c>false</c> removes it from every transport.</summary>
    public bool AudioEnabled { get; set; } = true;

    /// <summary>Per-clip byte cap for uploaded audio (EFBIG past it; clamped 4 KiB..256 MiB).</summary>
    public int AudioMaxClipBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Store-wide byte cap across all uploaded clips (ENOSPC past it; never below the clip cap).</summary>
    public int AudioMaxTotalBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Maximum number of uploaded clips (ENOSPC past it; clamped 1..1024).</summary>
    public int AudioMaxClips { get; set; } = 64;

    /// <summary>Maximum concurrent playback channels (EBUSY past it; clamped 1..64).</summary>
    public int AudioMaxChannels { get; set; } = 16;

    // ---- CAMERA: the programmable cinematic camera (/sim/camera; plans/CAMERA_CONTROLS_PLAN.md). ----

    /// <summary>Serve the <c>/sim/camera</c> surface; <c>false</c> removes it from every transport.</summary>
    public bool CameraEnabled { get; set; } = true;

    /// <summary>Maximum uploaded camera tracks (ENOSPC past it; clamped 1..512).</summary>
    public int CameraMaxTracks { get; set; } = 32;

    /// <summary>Per-track JSON byte cap (EFBIG past it; clamped 1 KiB..64 MiB).</summary>
    public int CameraMaxTrackBytes { get; set; } = 1024 * 1024;

    /// <summary>Store-wide byte cap across all tracks (ENOSPC past it; never below the per-track cap).</summary>
    public int CameraMaxTotalBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Maximum keyframes per animated channel (EINVAL past it; clamped 2..65536).</summary>
    public int CameraMaxKeys { get; set; } = 4096;

    /// <summary>
    ///     Lower field-of-view bound in degrees (clamped 0.1..179) — deliberately wider than the
    ///     game's own 15°, because <c>SetFieldOfView</c> is unclamped and a fisheye is a shot.
    /// </summary>
    public double CameraFovMin { get; set; } = 1;

    /// <summary>
    ///     Upper field-of-view bound in degrees (clamped up to 179 and never below
    ///     <see cref="CameraFovMin"/>) — deliberately wider than the game's own 120°.
    /// </summary>
    public double CameraFovMax { get; set; } = 179;

    /// <summary>
    ///     Default eased hand-back in seconds when the director releases the camera (clamped 0..10;
    ///     the game's own <c>CameraJumpTime</c> default is 0.6 s).
    /// </summary>
    public double CameraReleaseBlendS { get; set; } = 0.6;

    /// <summary>
    ///     Allow camera tracks to drive simulation speed; additionally requires
    ///     <see cref="DebugNamespace"/>.
    /// </summary>
    public bool CameraAllowTimeChannel { get; set; } = true;

    // ---- SCHEDULE: host-side timed command sequences (/sim/ctl/timed_batch; CAMERA_CONTROLS_PLAN §3). ----

    /// <summary>
    ///     Serve <c>/sim/ctl/timed_batch</c> + <c>/sim/ctl/schedules</c>; <c>false</c> removes both
    ///     from every transport.
    /// </summary>
    public bool ScheduleEnabled { get; set; } = true;

    /// <summary>Maximum concurrent live schedules (EINVAL past it; clamped 1..256).</summary>
    public int ScheduleMaxLive { get; set; } = 16;

    /// <summary>Maximum timed entries per schedule (EINVAL past it; clamped 1..262144).</summary>
    public int ScheduleMaxEntries { get; set; } = 8192;

    /// <summary>Per-schedule buffered payload byte cap (EINVAL past it; clamped 1 KiB..16 MiB).</summary>
    public int ScheduleMaxBytes { get; set; } = 1024 * 1024;

    /// <summary>
    ///     Default clock base for a schedule that does not declare <c>@clock</c>: <c>render</c> |
    ///     <c>wall</c> | <c>ut</c> (anything else falls back to <c>render</c> with a warning).
    /// </summary>
    public string ScheduleDefaultClock { get; set; } = "render";

    // ---- IVA: free-floating cabin objects (/sim/debug/iva; plans/IVA_MOVEMENTS.md). ----

    /// <summary>
    ///     Boot seed for <c>/sim/debug/iva/enabled</c> — the master switch that starts and ends the
    ///     whole cabin-physics feature. <b>Off by default and off means off:</b> no physics
    ///     simulation, no interior collision mesh, no per-frame work. Turn it on live with
    ///     <c>echo 1 &gt; /sim/debug/iva/enabled</c>.
    /// </summary>
    public bool IvaPhysicsEnabled { get; set; }

    /// <summary>
    ///     Keep stepping the cabin sim when no viewport is in the IVA camera. Off by default: leaving
    ///     IVA parks the objects (velocities zeroed, poses frozen) until you come back.
    /// </summary>
    public bool IvaRunOutsideIva { get; set; }

    /// <summary>Fixed integration rate for the cabin sim in Hz (clamped 30..480).</summary>
    public int IvaSubstepHz { get; set; } = 120;

    /// <summary>Maximum substeps per frame — the post-hitch catch-up bound (clamped 1..32).</summary>
    public int IvaMaxSubstepsPerFrame { get; set; } = 8;

    /// <summary>Cap on floating objects per vessel; an adopt past it fails EBUSY (clamped 1..64).</summary>
    public int IvaMaxObjects { get; set; } = 16;

    /// <summary>
    ///     Largest bounding-box extent in metres a SubPart may have and still be adoptable — the guard
    ///     that keeps a hull panel or a seat from being cut loose (clamped 0.01..5).
    /// </summary>
    public double IvaMaxObjectSize { get; set; } = 0.5;

    /// <summary>Default density in kg/m³ used to derive an object's mass from its proxy volume (clamped 1..20000).</summary>
    public double IvaDensityKgM3 { get; set; } = 300;

    /// <summary>Hard velocity clamp in m/s — the anti-tunnelling guard for thin art meshes (clamped 0.1..200).</summary>
    public double IvaMaxSpeed { get; set; } = 15;

    /// <summary>Contact friction coefficient (clamped 0..2); higher makes settled objects stay put.</summary>
    public double IvaFriction { get; set; } = 0.6;

    /// <summary>Bounciness as Bepu's maximum recovery velocity in m/s (clamped 0..20).</summary>
    public double IvaRestitution { get; set; } = 1.0;

    /// <summary>
    ///     Emit every interior triangle in both windings, so an object cannot fall through a wall whose
    ///     art happens to wind outward. On by default; turning it off halves the triangle count.
    /// </summary>
    public bool IvaDoubleSidedInterior { get; set; } = true;

    /// <summary>Speed change in m/s within one substep above which an <c>iva.impact</c> event fires (clamped 0.01..50).</summary>
    public double IvaImpactSpeed { get; set; } = 0.4;

    // ---- MOUNTS: host folders shared into the guest under /mnt/<name>. ----

    /// <summary>
    ///     Host folders to share into the guest, each appearing at <c>/mnt/&lt;name&gt;</c>
    ///     (TOML <c>[[mounts]]</c> array). Empty by default — no host folder is exposed unless the
    ///     player adds an entry. Each mount is read-only unless <see cref="MountSpec.ReadOnly"/> is
    ///     set to <c>false</c>. Served over the same 9p-over-slirp channel as <c>/sim</c>.
    /// </summary>
    public List<MountSpec> Mounts { get; set; } = [];

    /// <summary>
    ///     Loads the config from <paramref name="path"/>. On first run (the file is missing) it is
    ///     seeded from <paramref name="bundledDefaultPath"/> when that copy exists — so settings a
    ///     player edited in the install folder before launching take effect — otherwise it is created
    ///     with generated defaults. Never throws: parse failures and schema mismatches fall back to
    ///     defaults (logged), and the existing file is never overwritten by a fallback.
    /// </summary>
    public static GatOsConfig LoadOrCreate(string path, string? bundledDefaultPath = null)
    {
        if (!File.Exists(path))
        {
            // Prefer seeding from the bundled default so pre-launch edits in the install folder
            // carry over; the copied file is then read below like any existing config.
            if (bundledDefaultPath is not null && File.Exists(bundledDefaultPath))
            {
                try
                {
                    File.Copy(bundledDefaultPath, path);
                    ModLog.Log.Info($"Seeded the config at '{path}' from the bundled default '{bundledDefaultPath}'.");
                }
                catch (Exception ex)
                {
                    ModLog.Log.Warn(
                        $"Could not seed the config from '{bundledDefaultPath}' ({ex.Message}); writing defaults instead.");
                }
            }

            if (!File.Exists(path))
            {
                var fresh = new GatOsConfig();
                try
                {
                    fresh.Save(path);
                    ModLog.Log.Info($"Created the default config at '{path}'.");
                }
                catch (Exception ex)
                {
                    ModLog.Log.Warn($"Could not write the default config to '{path}': {ex.Message}");
                }

                return fresh;
            }
        }

        GatOsConfig config;
        try
        {
            // Missing keys keep their property defaults; unknown keys are ignored; malformed
            // TOML and wrong value types throw TomlException (verified against Tomlyn 2.6.0).
            config = TomlSerializer.Deserialize<GatOsConfig>(File.ReadAllText(path), TomlOptions)
                     ?? new GatOsConfig();
        }
        catch (Exception ex)
        {
            ModLog.Log.Warn(
                $"Config '{path}' could not be read ({ex.Message}); using defaults. Fix or delete the file.");
            return new GatOsConfig();
        }

        if (config.Schema != CurrentSchema)
        {
            ModLog.Log.Warn(
                $"Config '{path}' has schema {config.Schema}; this build understands schema {CurrentSchema}. "
                + "Using defaults (the file is left untouched).");
            return new GatOsConfig();
        }

        config.Normalize();
        return config;
    }

    /// <summary>Writes the config atomically (temp + rename) in the sectioned, commented layout.</summary>
    public void Save(string path) => AtomicFile.WriteAllText(path, Serialize());

    private const string MountsSectionTitle = "MOUNTS — host folders shared into the guest under /mnt/<name>";

    private const string MountsHelp =
        """
        # Off by default. Each [[mounts]] entry shares a host folder into the guest at /mnt/<name>,
        # over the same channel as /sim. read_only = true (the default) lets the guest read but not
        # change your files; read_only = false grants full read-write passthrough to the real folder
        # — the guest can create, edit, delete and rename host files, so enable it deliberately.
        # Hand-edit these before launch (no in-game UI); changes take effect on the next launch.
        #
        # [[mounts]]
        # name = "scripts"             # shows up as /mnt/scripts in the guest
        # path = "C:/Users/you/ksa"    # the host folder (forward slashes are fine on Windows)
        # read_only = true
        """;

    /// <summary>
    ///     Renders the config to the on-disk TOML: the <see cref="FileHeader"/> preamble, then the
    ///     <c>schema</c> line, then every scalar grouped under its <see cref="Sections"/> header with
    ///     an inline comment, then the host folder mounts as readable <c>[[mounts]]</c> blocks.
    ///     Tomlyn formats the scalar values; this regroups them and hand-renders the mounts (Tomlyn
    ///     would inline the whole list onto one unreadable line, so we emit the block form ourselves
    ///     — both forms deserialize identically).
    /// </summary>
    public string Serialize()
    {
        // Index the rendered scalar lines by key so we can re-emit them in section order. Tomlyn owns
        // the value formatting; we never reformat a scalar ourselves.
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in TomlSerializer.Serialize(this, TomlOptions)
                     .Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                byKey[line[..eq].Trim()] = line;
        }

        // Tomlyn renders the list as an inline `mounts = [...]` scalar; the MOUNTS section renders it
        // in block form below, so drop the inline line here.
        byKey.Remove("mounts");

        var sb = new StringBuilder();
        sb.Append(FileHeader);

        // schema sits above the sections: it is the file-format version, not a tunable.
        if (byKey.Remove("schema", out var schemaLine))
            sb.Append('\n').Append(schemaLine).Append('\n');

        foreach (var (title, keys) in Sections)
        {
            sb.Append("\n# ===== ").Append(title).Append(" =====\n");
            foreach (var (key, comment) in keys)
            {
                if (!byKey.Remove(key, out var valueLine))
                    continue; // property removed from the class since this table was written

                sb.Append('\n');
                foreach (var commentLine in comment.Split('\n'))
                    sb.Append("# ").Append(commentLine).Append('\n');
                sb.Append(valueLine).Append('\n');
            }
        }

        // Host folder mounts: always the help block, then one [[mounts]] table per entry.
        sb.Append("\n# ===== ").Append(MountsSectionTitle).Append(" =====\n\n");
        sb.Append(MountsHelp).Append('\n');
        foreach (var mount in Mounts)
        {
            sb.Append("\n[[mounts]]\n");
            sb.Append("name = ").Append(RenderTomlString(mount.Name)).Append('\n');
            sb.Append("path = ").Append(RenderTomlString(mount.Path)).Append('\n');
            sb.Append("read_only = ").Append(mount.ReadOnly ? "true" : "false").Append('\n');
        }

        // Catch-all: any key the section table did not place (e.g. a freshly added property) still
        // ships, so the generated file is always complete even if this table lags the class.
        if (byKey.Count > 0)
        {
            sb.Append("\n# ===== OTHER =====\n\n");
            foreach (var line in byKey.Values)
                sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Renders a string as a TOML value: a literal <c>'…'</c> string when it has no quote or
    ///     newline (so a Windows path's backslashes need no escaping), else a basic escaped string.
    /// </summary>
    private static string RenderTomlString(string value)
    {
        if (!value.Contains('\'') && !value.Contains('\n') && !value.Contains('\r'))
            return $"'{value}'";
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        return $"\"{escaped}\"";
    }

    /// <summary>Clamps out-of-range values into something bootable, logging each correction.</summary>
    internal void Normalize()
    {
        MemoryMb = Clamp(nameof(MemoryMb), MemoryMb, 128, 8192); // Alpine floor / sanity ceiling
        Cpus = Clamp(nameof(Cpus), Cpus, 1, 16);
        DiskSizeGb = Clamp(nameof(DiskSizeGb), DiskSizeGb, 1, 128);
        SampleRateHz = Clamp(nameof(SampleRateHz), SampleRateHz, 1, 120);
        BootTimeoutSeconds = Clamp(nameof(BootTimeoutSeconds), BootTimeoutSeconds, 0, 3600);
        CommandTimeoutMs = Clamp(nameof(CommandTimeoutMs), CommandTimeoutMs, 100, 30000);
        MaxCommandsPerFrame = Clamp(nameof(MaxCommandsPerFrame), MaxCommandsPerFrame, 1, 4096);
        HttpBindHost = NormalizeBindHost(nameof(HttpBindHost), HttpBindHost);
        McpBindHost = NormalizeBindHost(nameof(McpBindHost), McpBindHost);
        MqttBindHost = NormalizeBindHost(nameof(MqttBindHost), MqttBindHost);
        if (HttpPreferredPort != 0)
            HttpPreferredPort = Clamp(nameof(HttpPreferredPort), HttpPreferredPort, 1024, 65535);
        if (McpPreferredPort != 0)
            McpPreferredPort = Clamp(nameof(McpPreferredPort), McpPreferredPort, 1024, 65535);
        if (MqttPreferredPort != 0)
            MqttPreferredPort = Clamp(nameof(MqttPreferredPort), MqttPreferredPort, 1024, 65535);
        SerialIntervalMs = Clamp(nameof(SerialIntervalMs), SerialIntervalMs, 50, 60000);
        FieldFeedHz = Clamp(nameof(FieldFeedHz), FieldFeedHz, 1, 30);
        MqttPublishHz = Clamp(nameof(MqttPublishHz), MqttPublishHz, 0, 120);
        DisplayFps = Clamp(nameof(DisplayFps), DisplayFps, 1, 60);
        DisplayWidth = Clamp(nameof(DisplayWidth), DisplayWidth, 16, 1920);
        DisplayHeight = Clamp(nameof(DisplayHeight), DisplayHeight, 16, 1920);
        PaintMaxMaterialClones = Clamp(nameof(PaintMaxMaterialClones), PaintMaxMaterialClones, 1, 256);
        PaintTextureMaxBytes = Clamp(nameof(PaintTextureMaxBytes), PaintTextureMaxBytes,
            64 * 1024, 256 * 1024 * 1024);
        PaintTextureMaxTotalBytes = Clamp(nameof(PaintTextureMaxTotalBytes), PaintTextureMaxTotalBytes,
            PaintTextureMaxBytes, int.MaxValue);
        PaintTextureMaxFiles = Clamp(nameof(PaintTextureMaxFiles), PaintTextureMaxFiles, 1, 256);
        PaintTextureMaxBindings = Clamp(nameof(PaintTextureMaxBindings), PaintTextureMaxBindings, 1, 256);
        PaintTextureMaxDimension = Clamp(nameof(PaintTextureMaxDimension), PaintTextureMaxDimension,
            16, 16384);
        PaintStickersMaxCount = Clamp(nameof(PaintStickersMaxCount), PaintStickersMaxCount, 1, 4096);
        PaintStickersMaxViewDistanceM = Clamp(nameof(PaintStickersMaxViewDistanceM),
            PaintStickersMaxViewDistanceM, 10, 1e6);
        AudioMaxClipBytes = Clamp(nameof(AudioMaxClipBytes), AudioMaxClipBytes, 4096, 256 * 1024 * 1024);
        AudioMaxTotalBytes = Clamp(nameof(AudioMaxTotalBytes), AudioMaxTotalBytes,
            AudioMaxClipBytes, 1024 * 1024 * 1024);
        AudioMaxClips = Clamp(nameof(AudioMaxClips), AudioMaxClips, 1, 1024);
        AudioMaxChannels = Clamp(nameof(AudioMaxChannels), AudioMaxChannels, 1, 64);
        CameraMaxTracks = Clamp(nameof(CameraMaxTracks), CameraMaxTracks, 1, 512);
        CameraMaxTrackBytes = Clamp(nameof(CameraMaxTrackBytes), CameraMaxTrackBytes, 1024, 64 * 1024 * 1024);
        CameraMaxTotalBytes = Clamp(nameof(CameraMaxTotalBytes), CameraMaxTotalBytes,
            CameraMaxTrackBytes, 256 * 1024 * 1024);
        CameraMaxKeys = Clamp(nameof(CameraMaxKeys), CameraMaxKeys, 2, 65536);
        CameraFovMin = Clamp(nameof(CameraFovMin), CameraFovMin, 0.1, 179);
        CameraFovMax = Clamp(nameof(CameraFovMax), CameraFovMax, CameraFovMin, 179);
        CameraReleaseBlendS = Clamp(nameof(CameraReleaseBlendS), CameraReleaseBlendS, 0, 10);
        ScheduleMaxLive = Clamp(nameof(ScheduleMaxLive), ScheduleMaxLive, 1, 256);
        ScheduleMaxEntries = Clamp(nameof(ScheduleMaxEntries), ScheduleMaxEntries, 1, 262144);
        ScheduleMaxBytes = Clamp(nameof(ScheduleMaxBytes), ScheduleMaxBytes, 1024, 16 * 1024 * 1024);
        IvaSubstepHz = Clamp(nameof(IvaSubstepHz), IvaSubstepHz, 30, 480);
        IvaMaxSubstepsPerFrame = Clamp(nameof(IvaMaxSubstepsPerFrame), IvaMaxSubstepsPerFrame, 1, 32);
        IvaMaxObjects = Clamp(nameof(IvaMaxObjects), IvaMaxObjects, 1, 64);
        IvaMaxObjectSize = Clamp(nameof(IvaMaxObjectSize), IvaMaxObjectSize, 0.01, 5);
        IvaDensityKgM3 = Clamp(nameof(IvaDensityKgM3), IvaDensityKgM3, 1, 20000);
        IvaMaxSpeed = Clamp(nameof(IvaMaxSpeed), IvaMaxSpeed, 0.1, 200);
        IvaFriction = Clamp(nameof(IvaFriction), IvaFriction, 0, 2);
        IvaRestitution = Clamp(nameof(IvaRestitution), IvaRestitution, 0, 20);
        IvaImpactSpeed = Clamp(nameof(IvaImpactSpeed), IvaImpactSpeed, 0.01, 50);

        var displayEncoding = DisplayEncoding.Trim().ToLowerInvariant();
        if (displayEncoding is not ("rgba-zlib" or "rgba"))
        {
            ModLog.Log.Warn($"Config: display_encoding '{DisplayEncoding}' is not rgba/rgba-zlib; using rgba-zlib.");
            displayEncoding = "rgba-zlib";
        }

        DisplayEncoding = displayEncoding;

        var scheduleClock = ScheduleDefaultClock.Trim().ToLowerInvariant();
        if (scheduleClock is not ("render" or "wall" or "ut"))
        {
            ModLog.Log.Warn($"Config: schedule_default_clock '{ScheduleDefaultClock}' is not render/wall/ut; using render.");
            scheduleClock = "render";
        }

        ScheduleDefaultClock = scheduleClock;

        var serialMode = SerialMode.Trim().ToLowerInvariant();
        if (serialMode is not ("ndjson" or "nmea" or "ccsds"))
        {
            ModLog.Log.Warn($"Config: serial_mode '{SerialMode}' is not ndjson/nmea/ccsds; using ndjson.");
            serialMode = "ndjson";
        }

        SerialMode = serialMode;

        var accel = AccelOverride.Trim().ToLowerInvariant();
        if (accel is not ("" or "whpx" or "kvm" or "hvf" or "tcg"))
        {
            ModLog.Log.Warn(
                $"Config: accel_override '{AccelOverride}' is not one of whpx/kvm/hvf/tcg; using the auto ladder.");
            accel = "";
        }

        AccelOverride = accel;

        // CPU model names are QEMU-defined (e.g. "Haswell"); pass through verbatim, just trimmed.
        CpuModel = CpuModel.Trim();

        NormalizeMounts();
    }

    private static string NormalizeBindHost(string name, string? value)
    {
        var candidate = value?.Trim() ?? "";
        if (System.Net.IPAddress.TryParse(candidate, out var address))
            return address.ToString();

        ModLog.Log.Warn($"Config: {name} '{value}' is not an IP address; using 127.0.0.1.");
        return "127.0.0.1";
    }

    /// <summary>
    ///     Sanitizes the host-folder mounts: each name is reduced to a safe single path component,
    ///     blank/invalid names and blank paths are dropped, and duplicate names are removed (the
    ///     name is the guest directory under <c>/mnt</c>, so it must be unique). Logged, not rejected.
    /// </summary>
    private void NormalizeMounts()
    {
        if (Mounts.Count == 0)
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<MountSpec>(Mounts.Count);
        foreach (var mount in Mounts)
        {
            var name = SanitizeMountName(mount.Name ?? "");
            if (name.Length == 0)
            {
                ModLog.Log.Warn($"Config: mount with name '{mount.Name}' is not a usable folder name; skipping.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(mount.Path))
            {
                ModLog.Log.Warn($"Config: mount '{name}' has no path; skipping.");
                continue;
            }

            if (!seen.Add(name))
            {
                ModLog.Log.Warn($"Config: duplicate mount name '{name}'; keeping the first only.");
                continue;
            }

            cleaned.Add(new MountSpec { Name = name, Path = mount.Path.Trim(), ReadOnly = mount.ReadOnly });
        }

        Mounts = cleaned;
    }

    /// <summary>Reduces a mount name to <c>[A-Za-z0-9._-]</c> and rejects <c>.</c>/<c>..</c>.</summary>
    private static string SanitizeMountName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                sb.Append(c);
        var name = sb.ToString();
        return name is "." or ".." ? "" : name;
    }

    private static int Clamp(string name, int value, int min, int max)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value)
            ModLog.Log.Warn($"Config: {name} {value} is outside [{min}, {max}]; using {clamped}.");
        return clamped;
    }

    /// <summary>Clamps a real-valued knob, treating a non-finite value as out of range.</summary>
    private static double Clamp(string name, double value, double min, double max)
    {
        var clamped = double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
        if (clamped != value)
            ModLog.Log.Warn($"Config: {name} {value} is outside [{min}, {max}]; using {clamped}.");
        return clamped;
    }
}

/// <summary>
///     One <c>[[mounts]]</c> entry: a host folder shared into the guest at <c>/mnt/&lt;name&gt;</c>.
///     Public, mutable, parameterless — the shape Tomlyn deserializes an array-of-tables into.
/// </summary>
public sealed class MountSpec
{
    /// <summary>
    ///     The mount name — the guest directory under <c>/mnt</c>. Sanitized at load to a single safe
    ///     path component (<c>[A-Za-z0-9._-]</c>); entries that sanitize to empty are dropped.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>The absolute host folder to expose (e.g. <c>C:/Users/me/ksa-scripts</c>).</summary>
    public string Path { get; set; } = "";

    /// <summary>
    ///     When <c>true</c> (the default) the guest can read but not modify the folder; <c>false</c>
    ///     gives the guest full read-write passthrough (create/edit/delete/rename real host files).
    /// </summary>
    public bool ReadOnly { get; set; } = true;
}
