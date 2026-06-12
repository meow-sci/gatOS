namespace gatOS.GameMod.Configuration;

/// <summary>
///     User configuration for gatOS, persisted as <c>gatos.toml</c> in the data dir
///     (<see cref="gatOS.Vm.GatOsPaths.ConfigFile"/>). Tomlyn-backed load/save and the full field
///     set (memory, cpus, network <c>restrict</c>, timeouts) are fleshed out at milestone M6
///     (OS_PLAN.md T0.4). This M0 stub fixes the type's home and the defaults of record.
/// </summary>
public sealed class GatOsConfig
{
    /// <summary>Guest RAM in MiB (OS_ANALYSIS §3.3 default).</summary>
    public int MemoryMb { get; set; } = 256;

    /// <summary>Guest vCPU count.</summary>
    public int Cpus { get; set; } = 2;

    /// <summary>
    ///     When <c>true</c>, the guest is launched with <c>-netdev user,restrict=on</c> (no outbound
    ///     NAT; "offline ship computer"). Defaults to open NAT so real apk mirrors work (D3).
    ///     Wired to QEMU args at M12.
    /// </summary>
    public bool RestrictNetwork { get; set; }
}
