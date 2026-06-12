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
///     only). <c>-cpu host</c> requires hardware accel, so a ladder that starts at tcg uses
///     <c>-cpu max</c>. <c>pic=off</c> from the analysis sketch is deliberately not included
///     until validated on WHPX (T6.7) — maximally compatible first.
/// </remarks>
public sealed class QemuCommandBuilder(OperatingSystemFacts facts)
{
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
        // "host" passes the host CPU through and exists only under hardware accel.
        args.AddRange(["-cpu", ladder[0] == "tcg" ? "max" : "host"]);
        args.AddRange(["-m", spec.MemoryMb.ToString()]);
        args.AddRange(["-smp", spec.Cpus.ToString()]);

        args.AddRange(["-kernel", spec.KernelPath]);
        args.AddRange(["-initrd", spec.InitrdPath]);
        args.AddRange(["-append", $"{spec.KernelCmdlineBase} gatos.simport={spec.SimPort ?? 0}"]);

        args.AddRange(["-drive", $"file={spec.OverlayPath},if=virtio,format=qcow2"]);

        var restrict = spec.RestrictNetwork ? ",restrict=on" : "";
        args.AddRange(["-netdev", $"user,id=n0,hostfwd=tcp:127.0.0.1:{spec.SshHostPort}-:22{restrict}"]);
        args.AddRange(["-device", "virtio-net-pci,netdev=n0"]);

        args.AddRange(["-device", "virtio-serial-pci"]);
        args.AddRange(["-chardev", $"socket,id=qga0,host=127.0.0.1,port={spec.QgaPort},server=on,wait=off"]);
        args.AddRange(["-device", "virtserialport,chardev=qga0,name=org.qemu.guest_agent.0"]);

        args.AddRange(["-qmp", $"tcp:127.0.0.1:{spec.QmpPort},server=on,wait=off"]);

        args.AddRange(["-display", "none"]);
        args.AddRange(["-serial", $"file:{spec.SerialLogPath}"]);
        args.AddRange(["-monitor", "none"]);
        // Guest poweroff/reboot exits the process — the clean lifecycle signal for VmHost.
        args.Add("-no-reboot");

        return args;
    }
}
