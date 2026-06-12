namespace gatOS.Vm.Tests.Integration;

/// <summary>
///     Boots the real guest artifacts with a real QEMU (T3.4). Gated by <c>GATOS_IT=1</c>;
///     plain <c>dotnet test</c> skips.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class QemuProcessIntegrationTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        TestEnv.RequireIntegration();
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-it-" + Guid.NewGuid().ToString("N"));
        GatOsPaths.OverrideDataDirForTests(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        GatOsPaths.OverrideDataDirForTests(null);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public async Task Start_SuperviseAndKill_RaisesExited()
    {
        var assets = TestEnv.RequireGuestAssetsDir();
        var manager = new DiskManager(assets);
        var installed = manager.EnsureBaseInstalled();
        var overlay = manager.GetOrCreateOverlay("it-qemuprocess");
        var ports = PortAllocator.AllocatePorts(3);

        var spec = new VmLaunchSpec(
            OverlayPath: overlay,
            KernelPath: installed.KernelPath,
            InitrdPath: installed.InitrdPath,
            KernelCmdlineBase: installed.Manifest.KernelCmdline,
            MemoryMb: VmLaunchSpec.DefaultMemoryMb,
            Cpus: VmLaunchSpec.DefaultCpus,
            SshHostPort: ports[0], QgaPort: ports[1], QmpPort: ports[2],
            SimPort: null, RestrictNetwork: false,
            SerialLogPath: Path.Combine(GatOsPaths.LogsDir, "serial-it.log"));

        await using var process = new QemuProcess();
        var exited = new TaskCompletionSource<QemuProcessExitedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, e) => exited.TrySetResult(e);

        await process.StartAsync(spec, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(process.IsRunning, Is.True);
            Assert.That(process.EffectiveAccel, Is.Not.Null.And.Not.Empty);
            Assert.That(File.Exists(process.QemuLogPath), Is.True);
        });
        TestContext.Out.WriteLine($"effective accel: {process.EffectiveAccel}");

        process.Kill();
        var args = await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(args, Is.Not.Null);
        Assert.That(process.IsRunning, Is.False);
    }
}
