using System.Text.Json;
using gatOS.Logging;
using Tomlyn;

namespace gatOS.GameMod.Configuration;

/// <summary>
///     User configuration for gatOS, persisted as <c>gatos.toml</c> in the data dir
///     (<see cref="gatOS.Vm.GatOsPaths.ConfigFile"/>; OS_PLAN.md T6.3). Loading never throws:
///     a missing file is created with defaults, an unparseable one is logged and replaced by
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
        # gatOS configuration. Deleting this file restores all defaults.
        #
        # memory_mb             guest RAM in MiB
        # cpus                  guest vCPU count
        # restrict_network      true = guest gets no internet, only the gatOS channels (D3)
        # accel_override        force one accelerator: "whpx" | "kvm" | "hvf" | "tcg" ("" = auto)
        # cpu_model             override guest CPU model ("" = auto; WHPX needs a named model, not host)
        # sample_rate_hz        master /sim telemetry sampling rate in Hz (clamped 1..120; retune in-game)
        # telemetry_enabled     master gate for sampling (false = telemetry freezes; VM/shells unaffected)
        # telemetry_vessel_detail sample the per-vessel detail (navball/environment/per-module); off = core only
        # telemetry_bodies      sample the celestial-body catalog + system summary (/sim/bodies, /sim/system)
        # telemetry_events      diff snapshots into /sim/events entries (and the event topics/streams)
        # boot_timeout_seconds  0 = automatic (60 s accelerated, 300 s under TCG)
        # control_enabled       master switch for /sim writes (false = every control write EACCES)
        # control_all_vessels   true = command any vessel; false = only the active vessel (G-D1)
        # debug_namespace       expose the /sim/debug cheat surface (G-D2; reserved for G4+)
        # command_timeout_ms    how long a control write waits for the game thread before ETIMEDOUT
        # max_commands_per_frame upper bound on control commands executed per game frame
        # http_enabled          serve the magic HTTP API (guest reaches it at $GATOS_HTTP / 10.0.2.2)
        # http_preferred_port   preferred HTTP port (4242); 0 = ephemeral only; falls back on a clash
        # mqtt_enabled          run the embedded MQTT broker (guest reaches it at $GATOS_MQTT / 10.0.2.2)
        # mqtt_preferred_port   preferred MQTT port (1883); 0 = ephemeral only; falls back on a clash
        # http_field_endpoints  serve the per-field /v1/fs/<path> filesystem mirror (reads + SSE + writes)
        # mqtt_field_topics     publish the per-field gatos/sim/<path> filesystem mirror (one topic per leaf)
        # field_feed_hz         cadence of the MQTT field mirror in Hz (default 4; clamped 1..30)
        # serial_telemetry_port stream telemetry over the gatos.serial virtio-serial port (G7)
        # serial_command_port   accept SCPI commands over the gatos.serial virtio-serial port (G7)
        # serial_mode           serial telemetry wire format: ndjson | nmea | ccsds (default ndjson)
        # serial_interval_ms    serial telemetry cadence in milliseconds (default 500; clamped 50..60000)
        # bus_ccsds             expose a CCSDS space-packet TM/TC feed (G7; reserved)
        # bus_1553              expose a MIL-STD-1553 BC/RT framing feed (G7; reserved)

        """;

    /// <summary>Schema version of the file (readers reject anything but <see cref="CurrentSchema"/>).</summary>
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Guest RAM in MiB (OS_ANALYSIS §3.3 default).</summary>
    public int MemoryMb { get; set; } = 256;

    /// <summary>Guest vCPU count.</summary>
    public int Cpus { get; set; } = 2;

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

    /// <summary>Sample the celestial-body catalog and system summary (<c>/sim/bodies</c>, <c>/sim/system</c>).</summary>
    public bool TelemetryBodies { get; set; } = true;

    /// <summary>Diff consecutive snapshots into <c>/sim/events</c> entries (and the event streams/topics).</summary>
    public bool TelemetryEvents { get; set; } = true;

    /// <summary>Overall boot timeout in seconds; 0 = automatic (60 s accelerated / 300 s TCG).</summary>
    public int BootTimeoutSeconds { get; set; }

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

    /// <summary>Serve the magic HTTP API (KSA_GAME_INTEGRATION_PLAN Part 6 T2 / Part 7).</summary>
    public bool HttpEnabled { get; set; } = true;

    /// <summary>Preferred HTTP port (4242); 0 = ephemeral only; falls back to ephemeral on a clash.</summary>
    public int HttpPreferredPort { get; set; } = 4242;

    /// <summary>Run the embedded MQTT broker (an additional game-data bridge).</summary>
    public bool MqttEnabled { get; set; } = true;

    /// <summary>Preferred MQTT port (1883); 0 = ephemeral only; falls back to ephemeral on a clash.</summary>
    public int MqttPreferredPort { get; set; } = 1883;

    /// <summary>Serve the per-field <c>/v1/fs/&lt;path&gt;</c> filesystem mirror (reads, SSE, writes).</summary>
    public bool HttpFieldEndpoints { get; set; } = true;

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

    /// <summary>
    ///     Loads the config from <paramref name="path"/>, creating it with defaults on first run.
    ///     Never throws: parse failures and schema mismatches fall back to defaults (logged), and
    ///     the existing file is never overwritten by a fallback.
    /// </summary>
    public static GatOsConfig LoadOrCreate(string path)
    {
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

    /// <summary>Writes the config atomically (temp + rename) with a field-reference header.</summary>
    public void Save(string path)
        => AtomicFile.WriteAllText(path, FileHeader + TomlSerializer.Serialize(this, TomlOptions));

    /// <summary>Clamps out-of-range values into something bootable, logging each correction.</summary>
    internal void Normalize()
    {
        MemoryMb = Clamp(nameof(MemoryMb), MemoryMb, 128, 8192); // Alpine floor / sanity ceiling
        Cpus = Clamp(nameof(Cpus), Cpus, 1, 16);
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
    }

    private static int Clamp(string name, int value, int min, int max)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value)
            ModLog.Log.Warn($"Config: {name} {value} is outside [{min}, {max}]; using {clamped}.");
        return clamped;
    }
}
