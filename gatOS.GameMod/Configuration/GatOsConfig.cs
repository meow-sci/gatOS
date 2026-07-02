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
            ("http_preferred_port", "Preferred HTTP port (4242); 0 = ephemeral only; falls back on a clash."),
            ("http_field_endpoints", "Serve the per-field /v1/fs/<path> filesystem mirror (reads + SSE + writes)."),
            ("mqtt_enabled", "Run the embedded MQTT broker (guest reaches it at $GATOS_MQTT / 10.0.2.2)."),
            ("mqtt_preferred_port", "Preferred MQTT port (1883); 0 = ephemeral only; falls back on a clash."),
            ("mqtt_field_topics", "Publish the per-field gatos/sim/<path> filesystem mirror (one topic per leaf)."),
            ("field_feed_hz", "Cadence of the MQTT field mirror in Hz (default 4; clamped 1..30)."),
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

    /// <summary>Preferred HTTP port (4242); 0 = ephemeral only; falls back to ephemeral on a clash.</summary>
    public int HttpPreferredPort { get; set; } = 4242;

    /// <summary>Serve the per-field <c>/v1/fs/&lt;path&gt;</c> filesystem mirror (reads, SSE, writes).</summary>
    public bool HttpFieldEndpoints { get; set; } = true;

    /// <summary>Run the embedded MQTT broker (an additional game-data bridge).</summary>
    public bool MqttEnabled { get; set; } = true;

    /// <summary>Preferred MQTT port (1883); 0 = ephemeral only; falls back to ephemeral on a clash.</summary>
    public int MqttPreferredPort { get; set; } = 1883;

    /// <summary>Publish the per-field <c>gatos/sim/&lt;path&gt;</c> filesystem mirror (one topic per leaf).</summary>
    public bool MqttFieldTopics { get; set; } = true;

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
        if (HttpPreferredPort != 0)
            HttpPreferredPort = Clamp(nameof(HttpPreferredPort), HttpPreferredPort, 1024, 65535);
        if (MqttPreferredPort != 0)
            MqttPreferredPort = Clamp(nameof(MqttPreferredPort), MqttPreferredPort, 1024, 65535);
        SerialIntervalMs = Clamp(nameof(SerialIntervalMs), SerialIntervalMs, 50, 60000);
        FieldFeedHz = Clamp(nameof(FieldFeedHz), FieldFeedHz, 1, 30);
        DisplayFps = Clamp(nameof(DisplayFps), DisplayFps, 1, 60);
        DisplayWidth = Clamp(nameof(DisplayWidth), DisplayWidth, 16, 1920);
        DisplayHeight = Clamp(nameof(DisplayHeight), DisplayHeight, 16, 1920);

        var displayEncoding = DisplayEncoding.Trim().ToLowerInvariant();
        if (displayEncoding is not ("rgba-zlib" or "rgba"))
        {
            ModLog.Log.Warn($"Config: display_encoding '{DisplayEncoding}' is not rgba/rgba-zlib; using rgba-zlib.");
            displayEncoding = "rgba-zlib";
        }

        DisplayEncoding = displayEncoding;

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
