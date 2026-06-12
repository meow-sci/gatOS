namespace gatOS.Vm;

/// <summary>Configuration for one <see cref="VmHost"/> (OS_PLAN.md T3.6).</summary>
public sealed record VmHostOptions
{
    /// <summary>The disk profile (overlay) this host boots. MVP: one persistent "default".</summary>
    public string Profile { get; init; } = "default";

    /// <summary>Guest RAM in MiB.</summary>
    public int MemoryMb { get; init; } = VmLaunchSpec.DefaultMemoryMb;

    /// <summary>Guest vCPU count.</summary>
    public int Cpus { get; init; } = VmLaunchSpec.DefaultCpus;

    /// <summary>D3: restrict slirp to the explicit forwards (no guest internet).</summary>
    public bool RestrictNetwork { get; init; }

    /// <summary>Forces one accelerator (config <c>accel_override</c>); empty = auto ladder.</summary>
    public string AccelOverride { get; init; } = "";

    /// <summary>
    ///     Supplies the 9p sim port at boot time (M8 wires the real server); <c>null</c> result
    ///     or provider keeps the guest's sim-mount idle (<c>gatos.simport=0</c>).
    /// </summary>
    public Func<int?>? SimPortProvider { get; init; }

    /// <summary>
    ///     Overall boot timeout (spawn → SSH banner); <c>null</c> = auto: 60 s accelerated,
    ///     300 s under TCG.
    /// </summary>
    public TimeSpan? BootTimeout { get; init; }

    /// <summary>Bundled guest assets dir; <c>null</c> = <see cref="GatOsPaths.GuestAssetsDir"/>.</summary>
    public string? GuestAssetsDir { get; init; }
}
