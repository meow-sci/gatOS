using gatOS.NineP.Server;
using gatOS.NineP.Vfs;
using gatOS.Ssh;
using gatOS.Vm;
using Renci.SshNet;

namespace gatOS.SimFs.Tests.Integration;

/// <summary>
///     The host-folder mounts (<c>/mnt/&lt;name&gt;</c>) exit criterion against the real guest: the
///     VM boots with <c>gatos.mntport=&lt;port&gt;</c>, the guest's <c>mnt-mount</c> supervisor
///     mounts <c>/mnt</c> over slirp on its own, and a guest shell reads a read-only mount and fully
///     read-writes a writable one (create/edit/delete/rename real host files). Gated by
///     <c>GATOS_IT=1</c>; requires guest image v10+ (the version that ships <c>mnt-mount</c>).
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class HostMountIntegrationTests
{
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(60);

    private string? _dataRoot;
    private string? _shareRo;
    private string? _shareRw;

    [SetUp]
    public void SetUp()
    {
        TestEnv.RequireIntegration();
        _dataRoot = Path.Combine(Path.GetTempPath(), "gatos-it-mnt-" + Guid.NewGuid().ToString("N"));
        GatOsPaths.OverrideDataDirForTests(_dataRoot);
        _shareRo = Path.Combine(_dataRoot, "share-ro");
        _shareRw = Path.Combine(_dataRoot, "share-rw");
        Directory.CreateDirectory(_shareRo);
        Directory.CreateDirectory(_shareRw);
        File.WriteAllText(Path.Combine(_shareRo, "readme.txt"), "from host");
    }

    [TearDown]
    public void TearDown()
    {
        GatOsPaths.OverrideDataDirForTests(null);
        if (_dataRoot is not null && Directory.Exists(_dataRoot))
            Directory.Delete(_dataRoot, recursive: true);
    }

    [Test]
    public async Task GuestMountsHostFolders_ReadOnlyAndReadWrite()
    {
        var tree = HostMountTree.Build([
            new HostMountSpec("docs", _shareRo!, Writable: false),
            new HostMountSpec("work", _shareRw!, Writable: true),
        ]);
        await using var server = new NinePServer(tree);
        await server.StartAsync();

        var host = new VmHost(new VmHostOptions
        {
            Profile = "it-mnt",
            GuestAssetsDir = TestEnv.RequireGuestAssetsDir(),
            MntPortProvider = () => server.Port,
        });
        await using var broker = new VmConnectionBroker(host);
        using var ssh = await broker.ConnectAsync(CancellationToken.None);

        // The guest's mnt-mount supervisor mounts /mnt by itself (retry every 2 s).
        await WaitForMountAsync(ssh);

        // Read-only mount: the host file is readable from the guest.
        Assert.That(Run(ssh, "cat /mnt/docs/readme.txt").Trim(), Is.EqualTo("from host"));

        // Read-only mount: a write is rejected (nonzero exit), and the host file is untouched.
        Assert.That(Run(ssh, "echo x > /mnt/docs/readme.txt 2>/dev/null && echo OK || echo FAIL").Trim(),
            Is.EqualTo("FAIL"), "a read-only mount must reject writes");
        Assert.That(File.ReadAllText(Path.Combine(_shareRo!, "readme.txt")), Is.EqualTo("from host"));

        // Writable mount: create a file from the guest → it appears on the host.
        Assert.That(Run(ssh, "echo 'hello from guest' > /mnt/work/out.txt && echo OK || echo FAIL").Trim(),
            Is.EqualTo("OK"));
        Assert.That(File.ReadAllText(Path.Combine(_shareRw!, "out.txt")).Trim(), Is.EqualTo("hello from guest"));

        // Writable mount: mkdir, rename and delete all take effect on the host.
        Run(ssh, "mkdir /mnt/work/sub && mv /mnt/work/out.txt /mnt/work/sub/moved.txt");
        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(Path.Combine(_shareRw!, "sub")), Is.True);
            Assert.That(File.Exists(Path.Combine(_shareRw!, "sub", "moved.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(_shareRw!, "out.txt")), Is.False);
        });

        Run(ssh, "rm -rf /mnt/work/sub");
        Assert.That(Directory.Exists(Path.Combine(_shareRw!, "sub")), Is.False, "rm -rf removed it on the host");

        await broker.VmHost.StopAsync(TimeSpan.FromSeconds(30));
        Assert.That(broker.VmHost.Status.State, Is.EqualTo(VmState.Stopped));
    }

    private static string Run(SshClient ssh, string command)
    {
        using var cmd = ssh.RunCommand(command);
        return cmd.Result;
    }

    private static async Task WaitForMountAsync(SshClient ssh)
    {
        var deadline = DateTime.UtcNow + MountTimeout;
        while (true)
        {
            if (Run(ssh, "mountpoint -q /mnt && echo mounted || true").Contains("mounted"))
                return;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"/mnt did not mount within {MountTimeout.TotalSeconds:0} s "
                            + $"(cmdline: '{Run(ssh, "cat /proc/cmdline").Trim()}')");
            await Task.Delay(500);
        }
    }
}
