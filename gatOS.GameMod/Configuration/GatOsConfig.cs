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
        # sample_rate_hz        /sim telemetry sampling rate (used once the 9p server lands, M9)
        # boot_timeout_seconds  0 = automatic (60 s accelerated, 300 s under TCG)
        # control_enabled       master switch for /sim writes (false = every control write EACCES)
        # control_all_vessels   true = command any vessel; false = only the active vessel (G-D1)
        # debug_namespace       expose the /sim/debug cheat surface (G-D2; reserved for G4+)
        # command_timeout_ms    how long a control write waits for the game thread before ETIMEDOUT
        # max_commands_per_frame upper bound on control commands executed per game frame

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

    /// <summary>Telemetry sampling rate for the <c>/sim</c> tree (consumed by the M9 sampler).</summary>
    public int SampleRateHz { get; set; } = 10;

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

        var accel = AccelOverride.Trim().ToLowerInvariant();
        if (accel is not ("" or "whpx" or "kvm" or "hvf" or "tcg"))
        {
            ModLog.Log.Warn(
                $"Config: accel_override '{AccelOverride}' is not one of whpx/kvm/hvf/tcg; using the auto ladder.");
            accel = "";
        }

        AccelOverride = accel;
    }

    private static int Clamp(string name, int value, int min, int max)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value)
            ModLog.Log.Warn($"Config: {name} {value} is outside [{min}, {max}]; using {clamped}.");
        return clamped;
    }
}
