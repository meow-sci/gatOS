namespace gatOS.Vm.Tests;

/// <summary>
///     Covers the <see cref="VmHost.StopAsync"/> shutdown ladder rung selection with fakes:
///     QGA <c>guest-shutdown</c> → QMP <c>quit</c> → kill (T3.7).
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ShutdownLadderTests
{
    private string _tempRoot = null!;
    private FakeDiskManager _disks = null!;
    private FakeQemuProcess _process = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-ladder-" + Guid.NewGuid().ToString("N"));
        GatOsPaths.OverrideDataDirForTests(_tempRoot);
        _disks = new FakeDiskManager();
        _process = new FakeQemuProcess();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _process.DisposeAsync();
        GatOsPaths.OverrideDataDirForTests(null);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private VmHost CreateRunningHostAsyncCore(FakeQgaClient qga)
        => new(
            new VmHostOptions(),
            _disks,
            () => _process,
            (_, _, _) => Task.CompletedTask,
            _ => qga);

    [Test]
    public async Task Rung1_QgaShutdown_StopsCleanly()
    {
        // A successful guest-shutdown is followed by the guest powering off (process exit).
        FakeQgaClient qga = null!;
        qga = new FakeQgaClient(shutdownSucceeds: true, onShutdown: () => _process.TriggerExit(0));
        var host = CreateRunningHostAsyncCore(qga);
        await host.EnsureStartedAsync(CancellationToken.None);

        await host.StopAsync(TimeSpan.FromSeconds(10));
        Assert.Multiple(() =>
        {
            Assert.That(qga.ShutdownCalled, Is.True);
            Assert.That(_process.QmpTried, Is.False, "rung 1 sufficed — no QMP");
            Assert.That(_process.KillCalled, Is.False, "rung 1 sufficed — no kill");
            Assert.That(host.Status.State, Is.EqualTo(VmState.Stopped));
            Assert.That(_disks.LocksReleased, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Rung2_QmpQuit_WhenQgaFails()
    {
        var qga = new FakeQgaClient(shutdownSucceeds: false);
        var host = CreateRunningHostAsyncCore(qga);
        await host.EnsureStartedAsync(CancellationToken.None);

        _process.QmpQuitSucceeds = true;
        await host.StopAsync(TimeSpan.FromSeconds(10));
        Assert.Multiple(() =>
        {
            Assert.That(_process.QmpTried, Is.True);
            Assert.That(_process.KillCalled, Is.False);
            Assert.That(host.Status.State, Is.EqualTo(VmState.Stopped));
            Assert.That(_disks.LocksReleased, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Rung3_Kill_WhenQgaAndQmpBothFail()
    {
        var qga = new FakeQgaClient(shutdownSucceeds: false);
        var host = CreateRunningHostAsyncCore(qga);
        await host.EnsureStartedAsync(CancellationToken.None);

        _process.QmpQuitSucceeds = false;
        await host.StopAsync(TimeSpan.FromSeconds(10));
        Assert.Multiple(() =>
        {
            Assert.That(_process.QmpTried, Is.True);
            Assert.That(_process.KillCalled, Is.True);
            Assert.That(host.Status.State, Is.EqualTo(VmState.Stopped));
            Assert.That(_disks.LocksReleased, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task StopAsync_IsIdempotent_AndDoesNotFaultOnExpectedExit()
    {
        var qga = new FakeQgaClient(shutdownSucceeds: true, onShutdown: () => _process.TriggerExit(0));
        var host = CreateRunningHostAsyncCore(qga);
        await host.EnsureStartedAsync(CancellationToken.None);

        await host.StopAsync(TimeSpan.FromSeconds(10));
        await host.StopAsync(TimeSpan.FromSeconds(10)); // second stop: no-op
        Assert.Multiple(() =>
        {
            Assert.That(host.Status.State, Is.EqualTo(VmState.Stopped));
            Assert.That(host.Status.FaultReason, Is.Null, "the ladder's exit is expected, not a fault");
            Assert.That(_disks.LocksReleased, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task StopAsync_FromStoppedHost_IsANoOp()
    {
        var host = CreateRunningHostAsyncCore(new FakeQgaClient(shutdownSucceeds: false));
        await host.StopAsync(TimeSpan.FromSeconds(1));
        Assert.That(host.Status.State, Is.EqualTo(VmState.Stopped));
    }
}
