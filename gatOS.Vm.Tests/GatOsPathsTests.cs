using gatOS.Vm;

namespace gatOS.Vm.Tests;

/// <summary>Covers <see cref="GatOsPaths"/> directory creation, the test override and mod-dir guards (T0.4).</summary>
[TestFixture]
public sealed class GatOsPathsTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-tests-" + Guid.NewGuid().ToString("N"));
        GatOsPaths.OverrideDataDirForTests(_tempRoot);
        GatOsPaths.ModDir = null;
    }

    [TearDown]
    public void TearDown()
    {
        GatOsPaths.OverrideDataDirForTests(null);
        GatOsPaths.ModDir = null;
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public void DataDir_IsCreatedOnAccess_AtTheOverriddenLocation()
    {
        var dir = GatOsPaths.DataDir;
        Assert.That(dir, Is.EqualTo(_tempRoot));
        Assert.That(Directory.Exists(dir), Is.True);
    }

    [Test]
    public void DisksAndLogsDirs_AreCreatedUnderDataDir()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GatOsPaths.DisksDir, Is.EqualTo(Path.Combine(_tempRoot, "disks")));
            Assert.That(GatOsPaths.LogsDir, Is.EqualTo(Path.Combine(_tempRoot, "logs")));
            Assert.That(GatOsPaths.ConfigFile, Is.EqualTo(Path.Combine(_tempRoot, "gatos.toml")));
        });
        Assert.That(Directory.Exists(GatOsPaths.DisksDir), Is.True);
        Assert.That(Directory.Exists(GatOsPaths.LogsDir), Is.True);
    }

    [Test]
    public void GuestAndQemuDirs_ThrowUntilModDirIsSet()
    {
        Assert.Throws<InvalidOperationException>(() => _ = GatOsPaths.GuestAssetsDir);
        Assert.Throws<InvalidOperationException>(() => _ = GatOsPaths.BundledQemuDir);
        Assert.Throws<InvalidOperationException>(() => _ = GatOsPaths.BundledConfigFile);

        GatOsPaths.ModDir = "/opt/ksa/mods/gatOS";
        Assert.Multiple(() =>
        {
            Assert.That(GatOsPaths.GuestAssetsDir, Is.EqualTo(Path.Combine("/opt/ksa/mods/gatOS", "guest")));
            Assert.That(GatOsPaths.BundledQemuDir, Is.EqualTo(Path.Combine("/opt/ksa/mods/gatOS", "qemu")));
            Assert.That(GatOsPaths.BundledConfigFile, Is.EqualTo(Path.Combine("/opt/ksa/mods/gatOS", "gatos.default.toml")));
        });
    }
}
