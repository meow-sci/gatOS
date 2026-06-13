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
    ///     Overrides the guest CPU model (config <c>cpu_model</c>); empty = auto per accelerator
    ///     (<c>host</c> on KVM/HVF, a named model on WHPX, <c>max</c> on TCG). Set e.g. to
    ///     <c>"host"</c> to force passthrough where a particular host's WHPX tolerates it.
    /// </summary>
    public string CpuModel { get; init; } = "";

    /// <summary>
    ///     Supplies the 9p sim port at boot time (M8 wires the real server); <c>null</c> result
    ///     or provider keeps the guest's sim-mount idle (<c>gatos.simport=0</c>).
    /// </summary>
    public Func<int?>? SimPortProvider { get; init; }

    /// <summary>
    ///     Supplies the magic HTTP port at boot time (G5); <c>null</c> result or provider emits
    ///     <c>gatos.httpport=0</c> so the guest's HTTP env stays unset. The guest reaches the host
    ///     server outbound at <c>10.0.2.2:&lt;port&gt;</c> (slirp), like the 9p server.
    /// </summary>
    public Func<int?>? HttpPortProvider { get; init; }

    /// <summary>
    ///     Supplies the embedded MQTT broker port at boot time; <c>null</c> emits
    ///     <c>gatos.mqttport=0</c> so the guest's MQTT env stays unset. Guest dials
    ///     <c>10.0.2.2:&lt;port&gt;</c> (slirp).
    /// </summary>
    public Func<int?>? MqttPortProvider { get; init; }

    /// <summary>
    ///     When true (G7) the boot allocates a loopback port and wires the <c>gatos.serial</c>
    ///     virtio-serial chardev; the port is published as <see cref="VmStatus.SerialPort"/> for
    ///     the host-side <c>SerialBridge</c> to connect to. The guest exposes
    ///     <c>/dev/virtio-ports/gatos.serial</c>. False omits the port.
    /// </summary>
    public bool SerialEnabled { get; init; }

    /// <summary>
    ///     Overall boot timeout (spawn → SSH banner); <c>null</c> = auto: 60 s accelerated,
    ///     300 s under TCG.
    /// </summary>
    public TimeSpan? BootTimeout { get; init; }

    /// <summary>Bundled guest assets dir; <c>null</c> = <see cref="GatOsPaths.GuestAssetsDir"/>.</summary>
    public string? GuestAssetsDir { get; init; }
}
