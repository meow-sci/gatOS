namespace gatOS.Vm;

/// <summary>
///     Everything needed to compute one QEMU invocation (OS_PLAN.md T3.3). Pure data;
///     <see cref="QemuCommandBuilder.Build"/> turns it into an argument list.
/// </summary>
/// <param name="OverlayPath">The per-profile qcow2 overlay to boot.</param>
/// <param name="KernelPath">Installed kernel (direct kernel boot — no bootloader).</param>
/// <param name="InitrdPath">Installed initramfs.</param>
/// <param name="KernelCmdlineBase">
///     The manifest's base kernel command line; the builder appends <c>gatos.simport=…</c>.
/// </param>
/// <param name="MemoryMb">Guest RAM in MiB.</param>
/// <param name="Cpus">Guest vCPU count.</param>
/// <param name="SshHostPort">Loopback port forwarded to guest :22.</param>
/// <param name="QgaPort">Loopback port for the qemu-guest-agent chardev socket.</param>
/// <param name="QmpPort">Loopback port for the QMP control socket.</param>
/// <param name="SimPort">
///     The host 9p server port baked into the kernel cmdline; <c>null</c> (emitted as 0) keeps
///     the guest's sim-mount supervisor idle until M8 wires the real server.
/// </param>
/// <param name="RestrictNetwork">D3: adds <c>restrict=on</c> to the slirp netdev.</param>
/// <param name="SerialLogPath">File the guest serial console is written to.</param>
/// <param name="AccelOverride">
///     Forces a single accelerator (e.g. <c>"tcg"</c>); empty string selects the per-OS
///     auto ladder.
/// </param>
public sealed record VmLaunchSpec(
    string OverlayPath,
    string KernelPath,
    string InitrdPath,
    string KernelCmdlineBase,
    int MemoryMb,
    int Cpus,
    int SshHostPort,
    int QgaPort,
    int QmpPort,
    int? SimPort,
    bool RestrictNetwork,
    string SerialLogPath,
    string AccelOverride = "")
{
    /// <summary>Default guest RAM (OS_ANALYSIS.md §3.8: Alpine comfy at 192–256 MB).</summary>
    public const int DefaultMemoryMb = 256;

    /// <summary>Default guest vCPU count.</summary>
    public const int DefaultCpus = 2;
}
