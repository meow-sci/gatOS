using gatOS.Vm;

namespace gatOS.Ssh.Tests.Integration;

/// <summary>
///     Guest boot contract (guest v17): <c>init-gatos</c> mounts the unified cgroup2 hierarchy
///     at <c>/sys/fs/cgroup</c> and delegates every available controller to child cgroups —
///     what OpenRC's cgroups service does on stock Alpine, and what container runtimes
///     (podman/crun) require to run at all. Gated by <c>GATOS_IT=1</c>.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class GuestCgroupIntegrationTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        TestEnv.RequireIntegration();
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-it-cgroup-" + Guid.NewGuid().ToString("N"));
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
    public async Task CgroupV2_IsMounted_WithControllersDelegated()
    {
        var host = new VmHost(new VmHostOptions
        {
            Profile = "it-cgroup",
            GuestAssetsDir = TestEnv.RequireGuestAssetsDir(),
        });
        await using var broker = new VmConnectionBroker(host);
        using var client = await broker.ConnectAsync(CancellationToken.None);

        using var fstype = client.RunCommand("awk '$2 == \"/sys/fs/cgroup\" {print $3}' /proc/mounts");
        Assert.That(fstype.Result.Trim(), Is.EqualTo("cgroup2"),
            "init-gatos must mount the unified cgroup2 hierarchy (container runtimes depend on it)");

        // The controllers podman/crun actually drive; the hierarchy exposing them at the root
        // is kernel config, the delegation to children is init-gatos's subtree_control writes.
        using var delegated = client.RunCommand("cat /sys/fs/cgroup/cgroup.subtree_control");
        var controllers = delegated.Result.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(controllers, Is.SupersetOf(new[] { "cpu", "memory", "pids" }),
            "cpu/memory/pids must be delegated to child cgroups or crun discards limits");
    }
}
