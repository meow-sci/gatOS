namespace gatOS.Vm.Tests;

/// <summary>
///     State-machine unit tests for <see cref="VmHost"/> with fake process/disks/probe (T3.6).
/// </summary>
[TestFixture]
[NonParallelizable] // uses the GatOsPaths data-dir override for serial log paths
public sealed class VmHostTests
{
    private string _tempRoot = null!;
    private FakeDiskManager _disks = null!;
    private List<FakeQemuProcess> _processes = null!;
    private List<VmStatus> _statusLog = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-vmhost-" + Guid.NewGuid().ToString("N"));
        GatOsPaths.OverrideDataDirForTests(_tempRoot);
        _disks = new FakeDiskManager();
        _processes = [];
        _statusLog = [];
    }

    [TearDown]
    public void TearDown()
    {
        GatOsPaths.OverrideDataDirForTests(null);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private VmHost CreateHost(
        Func<int, TimeSpan, CancellationToken, Task>? probe = null,
        Func<int, IQgaClient>? qga = null,
        VmHostOptions? options = null)
    {
        var host = new VmHost(
            options ?? new VmHostOptions(),
            _disks,
            () =>
            {
                var p = new FakeQemuProcess();
                lock (_processes)
                {
                    _processes.Add(p);
                }

                return p;
            },
            probe ?? ((_, _, _) => Task.CompletedTask),
            qga ?? (_ => new FakeQgaClient(shutdownSucceeds: false)));
        host.StatusChanged += (_, s) =>
        {
            lock (_statusLog)
            {
                _statusLog.Add(s);
            }
        };
        return host;
    }

    [Test]
    public async Task EnsureStarted_BootsOnce_AndReturnsManifestShapedEndpoints()
    {
        await using var host = CreateHost();
        var endpoints = await host.EnsureStartedAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(endpoints.SshUser, Is.EqualTo("root"));
            Assert.That(endpoints.PrivateKeyPath, Is.EqualTo("/fake/key"));
            Assert.That(endpoints.HostKeySha256, Is.EqualTo("00ff"));
            Assert.That(endpoints.SshPort, Is.EqualTo(_processes[0].StartedSpec!.SshHostPort));
            Assert.That(host.Status.State, Is.EqualTo(VmState.Running));
            Assert.That(host.Status.EffectiveAccel, Is.EqualTo("fake"));
            Assert.That(_disks.LocksTaken, Is.EqualTo(1));
        });

        // Second call while Running: no second boot.
        var again = await host.EnsureStartedAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(again, Is.EqualTo(endpoints));
            Assert.That(_processes, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ConcurrentEnsureStarted_CoalescesIntoOneBoot()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = CreateHost(probe: (_, _, _) => release.Task);

        var callers = Enumerable.Range(0, 8)
            .Select(_ => host.EnsureStartedAsync(CancellationToken.None))
            .ToArray();
        await Task.Delay(100);
        Assert.That(host.Status.State, Is.EqualTo(VmState.Starting));
        release.SetResult();

        var endpoints = await Task.WhenAll(callers);
        Assert.Multiple(() =>
        {
            Assert.That(_processes, Has.Count.EqualTo(1), "all callers must share one boot");
            Assert.That(endpoints.Distinct().Count(), Is.EqualTo(1));
            Assert.That(host.Status.State, Is.EqualTo(VmState.Running));
        });
    }

    [Test]
    public async Task BootFailure_FaultsWithUserMessage_ReleasesLock_AndIsRetryable()
    {
        var attempts = 0;
        await using var host = CreateHost(probe: (_, _, _) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new TimeoutException("no banner"))
                : Task.CompletedTask);

        var ex = Assert.ThrowsAsync<VmStartException>(() => host.EnsureStartedAsync(CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(ex.UserMessage, Does.Contain("did not become reachable"));
            Assert.That(host.Status.State, Is.EqualTo(VmState.Faulted));
            Assert.That(host.Status.FaultReason, Is.EqualTo(ex.UserMessage));
            Assert.That(_disks.LocksReleased, Is.EqualTo(1), "boot failure must release the disk lock");
        });

        // Faulted is retryable: the next call boots fresh and succeeds.
        await host.EnsureStartedAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(host.Status.State, Is.EqualTo(VmState.Running));
            Assert.That(_processes, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task QemuMissing_SurfacesTheInstallHint()
    {
        _disks.FailWith = new DiskOperationException("Guest assets are missing — run guest/fetch-guest.sh.");
        await using var host = CreateHost();
        var ex = Assert.ThrowsAsync<VmStartException>(() => host.EnsureStartedAsync(CancellationToken.None));
        Assert.That(ex.UserMessage, Does.Contain("disk setup failed"));
    }

    [Test]
    public async Task UnexpectedExitWhileRunning_Faults_ReleasesLock_AndRaisesStatusChanged()
    {
        await using var host = CreateHost();
        await host.EnsureStartedAsync(CancellationToken.None);

        _processes[0].StderrTail = "guest panicked";
        _processes[0].TriggerExit(1);

        Assert.Multiple(() =>
        {
            Assert.That(host.Status.State, Is.EqualTo(VmState.Faulted));
            Assert.That(host.Status.FaultReason, Does.Contain("exited unexpectedly").And.Contain("guest panicked"));
            Assert.That(_disks.LocksReleased, Is.EqualTo(1));
        });
        lock (_statusLog)
        {
            Assert.That(_statusLog.Last().State, Is.EqualTo(VmState.Faulted));
        }

        // And the host recovers on the next request.
        await host.EnsureStartedAsync(CancellationToken.None);
        Assert.That(host.Status.State, Is.EqualTo(VmState.Running));
    }

    [Test]
    public async Task SimPortProvider_FlowsIntoTheLaunchSpec()
    {
        await using var host = CreateHost(options: new VmHostOptions { SimPortProvider = () => 5640 });
        await host.EnsureStartedAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(_processes[0].StartedSpec!.SimPort, Is.EqualTo(5640));
            Assert.That(host.Status.SimPort, Is.EqualTo(5640));
        });
    }

    [Test]
    public async Task DiskSizeBytes_GrowsTheOverlayBeforeBoot()
    {
        const long size = 8L * 1024 * 1024 * 1024;
        await using var host = CreateHost(options: new VmHostOptions { DiskSizeBytes = size });
        await host.EnsureStartedAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(_disks.ResizeCalls, Is.EqualTo(1));
            Assert.That(_disks.ResizedToBytes, Is.EqualTo(size));
        });
    }

    [Test]
    public async Task DiskSizeBytes_Zero_LeavesTheOverlayUntouched()
    {
        await using var host = CreateHost(); // default options ⇒ DiskSizeBytes 0
        await host.EnsureStartedAsync(CancellationToken.None);
        Assert.That(_disks.ResizeCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task PortClashOnStart_RetriesOnceWithFreshPorts()
    {
        await using var host = CreateHost();
        // First process fails like a taken hostfwd port; the host must retry with a new process.
        var first = true;
        var failing = new VmHost(
            new VmHostOptions(),
            _disks,
            () =>
            {
                var p = new FakeQemuProcess();
                if (first)
                {
                    first = false;
                    p.StartFailure = new VmStartException(
                        "The VM failed to start.",
                        "Could not set up host forwarding rule 'tcp:127.0.0.1:50022-:22'");
                }

                lock (_processes)
                {
                    _processes.Add(p);
                }

                return p;
            },
            (_, _, _) => Task.CompletedTask,
            _ => new FakeQgaClient(shutdownSucceeds: false));

        await failing.EnsureStartedAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(failing.Status.State, Is.EqualTo(VmState.Running));
            Assert.That(_processes, Has.Count.EqualTo(2));
        });
        await failing.DisposeAsync();
    }
}
