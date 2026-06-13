namespace gatOS.Vm.Tests;

/// <summary>Golden-args coverage of <see cref="QemuCommandBuilder"/> per OS branch (T3.3).</summary>
[TestFixture]
public sealed class QemuCommandBuilderTests
{
    private static readonly OperatingSystemFacts WindowsX64 = new(IsWindows: true, IsLinux: false, IsMacOs: false, IsX64: true);
    private static readonly OperatingSystemFacts LinuxX64 = new(IsWindows: false, IsLinux: true, IsMacOs: false, IsX64: true);
    private static readonly OperatingSystemFacts MacX64 = new(IsWindows: false, IsLinux: false, IsMacOs: true, IsX64: true);
    private static readonly OperatingSystemFacts MacArm64 = new(IsWindows: false, IsLinux: false, IsMacOs: true, IsX64: false);

    private static VmLaunchSpec Spec(int? simPort = null, bool restrict = false, string accelOverride = "",
        int? httpPort = null, int? serialPort = null)
        => new(
            OverlayPath: "/data/disks/default.qcow2",
            KernelPath: "/data/disks/guest-v1/vmlinuz-virt",
            InitrdPath: "/data/disks/guest-v1/initramfs-virt",
            KernelCmdlineBase: "console=ttyS0 root=/dev/vda rw quiet",
            MemoryMb: 256, Cpus: 2,
            SshHostPort: 50022, QgaPort: 50023, QmpPort: 50024,
            SimPort: simPort, RestrictNetwork: restrict,
            SerialLogPath: "/data/logs/serial.log",
            AccelOverride: accelOverride,
            HttpPort: httpPort,
            SerialPort: serialPort);

    [Test]
    public void LinuxX64_GoldenArgs()
    {
        var args = new QemuCommandBuilder(LinuxX64).Build(Spec());
        Assert.That(args, Is.EqualTo(new[]
        {
            "-accel", "kvm", "-accel", "tcg",
            "-M", "q35",
            "-cpu", "host",
            "-m", "256",
            "-smp", "2",
            "-kernel", "/data/disks/guest-v1/vmlinuz-virt",
            "-initrd", "/data/disks/guest-v1/initramfs-virt",
            "-append", "console=ttyS0 root=/dev/vda rw quiet gatos.simport=0 gatos.httpport=0 gatos.mqttport=0",
            "-drive", "file=/data/disks/default.qcow2,if=virtio,format=qcow2",
            "-netdev", "user,id=n0,hostfwd=tcp:127.0.0.1:50022-:22",
            "-device", "virtio-net-pci,netdev=n0",
            "-device", "virtio-serial-pci",
            "-chardev", "socket,id=qga0,host=127.0.0.1,port=50023,server=on,wait=off",
            "-device", "virtserialport,chardev=qga0,name=org.qemu.guest_agent.0",
            "-qmp", "tcp:127.0.0.1:50024,server=on,wait=off",
            "-display", "none",
            "-serial", "file:/data/logs/serial.log",
            "-monitor", "none",
            "-no-reboot",
        }));
    }

    [Test]
    public void AccelLadders_ArePerOs_AndNeverContainForeignAccels()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new QemuCommandBuilder(WindowsX64).ResolveAccelLadder(Spec()),
                Is.EqualTo(new[] { "whpx", "tcg" }));
            Assert.That(new QemuCommandBuilder(LinuxX64).ResolveAccelLadder(Spec()),
                Is.EqualTo(new[] { "kvm", "tcg" }));
            Assert.That(new QemuCommandBuilder(MacX64).ResolveAccelLadder(Spec()),
                Is.EqualTo(new[] { "hvf", "tcg" }));
        });
    }

    [Test]
    public void NonX64Host_CollapsesTheLadderToTcg_WithCpuMax()
    {
        var builder = new QemuCommandBuilder(MacArm64);
        Assert.That(builder.ResolveAccelLadder(Spec()), Is.EqualTo(new[] { "tcg" }));

        var args = builder.Build(Spec());
        var cpu = args[args.ToList().IndexOf("-cpu") + 1];
        Assert.That(cpu, Is.EqualTo("max"), "-cpu host requires hardware acceleration");
    }

    [Test]
    public void AccelOverride_ForcesASingleAccel()
    {
        var args = new QemuCommandBuilder(LinuxX64).Build(Spec(accelOverride: "tcg"));
        Assert.Multiple(() =>
        {
            Assert.That(args.Count(a => a == "-accel"), Is.EqualTo(1));
            Assert.That(args[args.ToList().IndexOf("-accel") + 1], Is.EqualTo("tcg"));
            Assert.That(args[args.ToList().IndexOf("-cpu") + 1], Is.EqualTo("max"));
        });
    }

    [Test]
    public void SimPort_IsInjectedIntoTheKernelCmdline()
    {
        var args = new QemuCommandBuilder(LinuxX64).Build(Spec(simPort: 5640));
        var append = args[args.ToList().IndexOf("-append") + 1];
        Assert.That(append, Is.EqualTo("console=ttyS0 root=/dev/vda rw quiet gatos.simport=5640 gatos.httpport=0 gatos.mqttport=0"));
    }

    [Test]
    public void HttpPort_IsInjectedIntoTheKernelCmdline()
    {
        var args = new QemuCommandBuilder(LinuxX64).Build(Spec(simPort: 5640, httpPort: 4242));
        var append = args[args.ToList().IndexOf("-append") + 1];
        Assert.That(append, Is.EqualTo("console=ttyS0 root=/dev/vda rw quiet gatos.simport=5640 gatos.httpport=4242 gatos.mqttport=0"));
    }

    [Test]
    public void RestrictNetwork_AppendsRestrictOnToTheNetdev()
    {
        var args = new QemuCommandBuilder(LinuxX64).Build(Spec(restrict: true));
        var netdev = args[args.ToList().IndexOf("-netdev") + 1];
        Assert.That(netdev, Does.EndWith(",restrict=on"));
    }

    [Test]
    public void SerialPort_WiresTheGatosSerialChardev_WhenSet()
    {
        var args = new QemuCommandBuilder(LinuxX64).Build(Spec(serialPort: 50025)).ToList();
        Assert.That(args, Does.Contain("socket,id=gatosserial,host=127.0.0.1,port=50025,server=on,wait=off"));
        var chardevIdx = args.IndexOf("socket,id=gatosserial,host=127.0.0.1,port=50025,server=on,wait=off");
        Assert.That(args[chardevIdx - 1], Is.EqualTo("-chardev"));
        Assert.That(args[chardevIdx + 1], Is.EqualTo("-device"));
        Assert.That(args[chardevIdx + 2], Is.EqualTo("virtserialport,chardev=gatosserial,name=gatos.serial"));
    }

    [Test]
    public void SerialPort_IsAbsent_WhenNull()
    {
        var args = new QemuCommandBuilder(LinuxX64).Build(Spec());
        Assert.That(args, Has.None.Contains("gatosserial"));
        Assert.That(args, Has.None.Contains("name=gatos.serial"));
    }
}
