namespace gatOS.Vm.Tests;

/// <summary>Covers <see cref="DiskLock"/> exclusive acquire, release and stale reclaim (T3.2).</summary>
[TestFixture]
public sealed class DiskLockTests
{
    private string _lockPath = null!;

    [SetUp]
    public void SetUp()
        => _lockPath = Path.Combine(Path.GetTempPath(), "gatos-lock-" + Guid.NewGuid().ToString("N") + ".lock");

    [TearDown]
    public void TearDown() => File.Delete(_lockPath);

    [Test]
    public void Acquire_CreatesTheLockFile_WithOurPid()
    {
        using var hold = DiskLock.Acquire(_lockPath);
        Assert.That(File.ReadAllText(_lockPath), Is.EqualTo(Environment.ProcessId.ToString()));
    }

    [Test]
    public void Acquire_Throws_WhileHeldByALiveProcess()
    {
        using var hold = DiskLock.Acquire(_lockPath); // our own (live) pid
        var ex = Assert.Throws<DiskOperationException>(() => DiskLock.Acquire(_lockPath));
        Assert.That(ex.Message, Does.Contain("in use"));
    }

    [Test]
    public void Dispose_ReleasesTheLock_SoItCanBeReacquired()
    {
        DiskLock.Acquire(_lockPath).Dispose();
        Assert.That(File.Exists(_lockPath), Is.False);
        using var again = DiskLock.Acquire(_lockPath);
    }

    [Test]
    public void Acquire_ReclaimsAStaleLock_WhenTheOwnerPidIsDead()
    {
        // Max pid is far below int.MaxValue on every supported OS, so this pid cannot be alive.
        File.WriteAllText(_lockPath, (int.MaxValue - 1).ToString());
        using var hold = DiskLock.Acquire(_lockPath);
        Assert.That(File.ReadAllText(_lockPath), Is.EqualTo(Environment.ProcessId.ToString()));
    }

    [Test]
    public void Acquire_ReclaimsAnUnreadableLockFile()
    {
        File.WriteAllText(_lockPath, "not-a-pid");
        using var hold = DiskLock.Acquire(_lockPath);
        Assert.That(File.ReadAllText(_lockPath), Is.EqualTo(Environment.ProcessId.ToString()));
    }
}
