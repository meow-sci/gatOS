using gatOS.Logging;

namespace gatOS.Vm.Tests.Integration;

/// <summary>
///     The M3 exit criterion: a full real boot → Running → StopAsync → Stopped against the
///     fetched guest artifacts (T3.6/T3.7). Gated by <c>GATOS_IT=1</c>.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class VmHostIntegrationTests
{
    private string _tempRoot = null!;
    private CapturingLogger _log = null!;

    [SetUp]
    public void SetUp()
    {
        TestEnv.RequireIntegration();
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-it-" + Guid.NewGuid().ToString("N"));
        GatOsPaths.OverrideDataDirForTests(_tempRoot);
        _log = new CapturingLogger();
        ModLog.SetLogger(_log);
    }

    [TearDown]
    public void TearDown()
    {
        ModLog.ResetToDefault();
        GatOsPaths.OverrideDataDirForTests(null);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public async Task FullLifecycle_Boot_Running_CleanStop()
    {
        var assets = TestEnv.RequireGuestAssetsDir();
        await using var host = new VmHost(new VmHostOptions
        {
            Profile = "it-vmhost",
            GuestAssetsDir = assets,
        });

        var statusLog = new List<VmStatus>();
        host.StatusChanged += (_, s) =>
        {
            lock (statusLog)
            {
                statusLog.Add(s);
            }
        };

        // Boot.
        var endpoints = await host.EnsureStartedAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(host.Status.State, Is.EqualTo(VmState.Running));
            Assert.That(endpoints.SshUser, Is.EqualTo("root"));
            Assert.That(File.Exists(endpoints.PrivateKeyPath), Is.True);
            Assert.That(endpoints.HostKeySha256, Has.Length.EqualTo(64));
            Assert.That(host.Status.EffectiveAccel, Is.Not.Null);
        });
        TestContext.Out.WriteLine($"booted with accel {host.Status.EffectiveAccel} on port {endpoints.SshPort}");

        // A second caller gets the cached endpoints without a second boot.
        Assert.That(await host.EnsureStartedAsync(CancellationToken.None), Is.EqualTo(endpoints));

        // Clean stop: the guest runs qemu-ga, so rung 1 (guest-shutdown) must do it within grace.
        var grace = TimeSpan.FromSeconds(30);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await host.StopAsync(grace);
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(host.Status.State, Is.EqualTo(VmState.Stopped));
            Assert.That(sw.Elapsed, Is.LessThan(grace + TimeSpan.FromSeconds(10)));
            Assert.That(_log.Lines, Has.Some.Contains("VM stopped via QGA guest-shutdown"),
                "the guest agent rung should have fired");
        });
        lock (statusLog)
        {
            Assert.That(statusLog.Select(s => s.State),
                Does.Contain(VmState.Starting).And.Contain(VmState.Running)
                    .And.Contain(VmState.Stopping).And.Contain(VmState.Stopped));
        }

        // The disk lock is gone: a fresh host can lock the same profile immediately.
        var disks = new DiskManager(assets);
        using (disks.AcquireOverlayLock("it-vmhost"))
        {
        }
    }

    private sealed class CapturingLogger : IModLogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Debug(string message) => Add(message);
        public void Info(string message) => Add(message);
        public void Warn(string message) => Add(message);
        public void Error(string message, Exception? ex = null) => Add(message);

        private void Add(string message)
        {
            lock (_lines)
            {
                _lines.Add(message);
            }
        }
    }
}
