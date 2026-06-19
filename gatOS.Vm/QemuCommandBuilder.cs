namespace gatOS.Vm;

/// <summary>
///     Builds the QEMU argument list for a <see cref="VmLaunchSpec"/> (OS_PLAN.md T3.3).
///     The args come back as a list (one entry per argv element — callers feed them to
///     <c>ProcessStartInfo.ArgumentList</c>, never string-join).
/// </summary>
/// <remarks>
///     Accel ladders are per-OS because QEMU errors out on accelerator names that are not
///     compiled in (never pass foreign accel names): Windows <c>whpx,tcg</c>; Linux
///     <c>kvm,tcg</c>; macOS <c>hvf,tcg</c> — emitted as repeated <c>-accel</c> flags, first
///     available wins. Non-x64 hosts go straight to <c>tcg</c> (hardware accel is same-arch
///     only). The guest CPU model is chosen per accelerator (see <see cref="ResolveCpuModel"/>):
///     <c>host</c> passthrough under KVM/HVF, a named model under WHPX (which cannot run
///     <c>host</c>/<c>max</c>), and <c>max</c> under TCG. <c>pic=off</c> from the analysis sketch
///     is deliberately not included — maximally compatible first.
/// </remarks>
public sealed class QemuCommandBuilder(OperatingSystemFacts facts)
{
    /// <summary>
    ///     The named guest CPU model used under WHPX (Windows hardware accel); see
    ///     <see cref="ResolveCpuModel"/> for why <c>host</c>/<c>max</c> cannot be used there.
    ///     Haswell is the widest-supported model that still carries AES-NI (fast in-guest SSH crypto).
    /// </summary>
    public const string DefaultWhpxCpuModel = "Haswell";

    /// <summary>A builder for the current host.</summary>
    public static QemuCommandBuilder ForCurrentHost { get; } = new(OperatingSystemFacts.Current);

    /// <summary>The accelerators that will be attempted, in order, for <paramref name="spec"/>.</summary>
    public IReadOnlyList<string> ResolveAccelLadder(VmLaunchSpec spec)
    {
        if (spec.AccelOverride.Length > 0)
            return [spec.AccelOverride];
        if (!facts.IsX64)
            return ["tcg"];
        if (facts.IsWindows)
            return ["whpx", "tcg"];
        if (facts.IsLinux)
            return ["kvm", "tcg"];
        if (facts.IsMacOs)
            return ["hvf", "tcg"];
        return ["tcg"];
    }

    /// <summary>Computes the full QEMU argument list for <paramref name="spec"/>.</summary>
    public IReadOnlyList<string> Build(VmLaunchSpec spec)
    {
        var ladder = ResolveAccelLadder(spec);
        var args = new List<string>();

        foreach (var accel in ladder)
            args.AddRange(["-accel", accel]);

        args.AddRange(["-M", "q35"]);
        args.AddRange(["-cpu", ResolveCpuModel(spec, ladder[0])]);
        args.AddRange(["-m", spec.MemoryMb.ToString()]);
        args.AddRange(["-smp", spec.Cpus.ToString()]);

        args.AddRange(["-kernel", spec.KernelPath]);
        args.AddRange(["-initrd", spec.InitrdPath]);
        args.AddRange(["-append",
            $"{spec.KernelCmdlineBase} gatos.simport={spec.SimPort ?? 0} gatos.httpport={spec.HttpPort ?? 0} "
            + $"gatos.mqttport={spec.MqttPort ?? 0} gatos.mntport={spec.MntPort ?? 0}"]);

        args.AddRange(["-drive", $"file={spec.OverlayPath},if=virtio,format=qcow2"]);

        var restrict = spec.RestrictNetwork ? ",restrict=on" : "";
        args.AddRange(["-netdev", $"user,id=n0,hostfwd=tcp:0.0.0.0:{spec.SshHostPort}-:22{restrict}"]);
        args.AddRange(["-device", "virtio-net-pci,netdev=n0"]);

        args.AddRange(["-device", "virtio-serial-pci"]);
        args.AddRange(["-chardev", $"socket,id=qga0,host=127.0.0.1,port={spec.QgaPort},server=on,wait=off"]);
        args.AddRange(["-device", "virtserialport,chardev=qga0,name=org.qemu.guest_agent.0"]);

        // G7: the gatos.serial bus port. QEMU listens (server=on,wait=off) on a loopback socket
        // the host SerialBridge connects to; the guest sees /dev/virtio-ports/gatos.serial (the
        // init script symlinks every virtio-port by name). Shares the virtio-serial-pci bus above.
        if (spec.SerialPort is { } serialPort)
        {
            args.AddRange(["-chardev",
                $"socket,id=gatosserial,host=127.0.0.1,port={serialPort},server=on,wait=off"]);
            args.AddRange(["-device", "virtserialport,chardev=gatosserial,name=gatos.serial"]);
        }

        args.AddRange(["-qmp", $"tcp:127.0.0.1:{spec.QmpPort},server=on,wait=off"]);

        args.AddRange(["-display", "none"]);
        args.AddRange(["-serial", $"file:{spec.SerialLogPath}"]);
        args.AddRange(["-monitor", "none"]);
        // Guest poweroff/reboot exits the process — the clean lifecycle signal for VmHost.
        args.Add("-no-reboot");

        return args;
    }

    /// <summary>
    ///     The guest CPU model for the chosen accelerator. <c>-cpu host</c> passes the physical CPU
    ///     through and is ideal under KVM/HVF, but the <b>Windows Hypervisor Platform rejects it</b>
    ///     (and <c>-cpu max</c>) on real hardware — the guest triple-faults at boot with
    ///     "WHPX: Unexpected VP exit code 4" regardless of which individual features are masked off
    ///     (verified on a Raptor Lake i9-13900K: <c>host</c>, <c>host,-vmx</c>, <c>host,-apxf,-mpx</c>
    ///     and <c>max</c> all fault, while every named model boots cleanly). So WHPX uses a fixed
    ///     named model (<see cref="DefaultWhpxCpuModel"/>). TCG has no host CPU to pass through and
    ///     uses <c>max</c>. A non-empty <see cref="VmLaunchSpec.CpuModel"/> overrides all of this
    ///     (config <c>cpu_model</c>), e.g. to force <c>host</c> where a host's WHPX tolerates it.
    /// </summary>
    private static string ResolveCpuModel(VmLaunchSpec spec, string accel)
    {
        if (spec.CpuModel.Length > 0)
            return spec.CpuModel;
        return accel switch
        {
            "tcg" => "max",
            "whpx" => DefaultWhpxCpuModel,
            _ => "host", // kvm, hvf — passthrough is correct and fastest
        };
    }
}
