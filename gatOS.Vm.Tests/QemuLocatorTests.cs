namespace gatOS.Vm.Tests;

/// <summary>Covers <see cref="QemuLocator"/> override resolution and version parsing (T3.1).</summary>
[TestFixture]
[NonParallelizable] // QemuLocator.OverridePath is process-global state.
public sealed class QemuLocatorTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gatos-qemu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        QemuLocator.OverridePath = null;
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void Find_HonorsOverridePath_WhenBothBinariesExist()
    {
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        File.WriteAllText(Path.Combine(_tempDir, "qemu-system-x86_64" + ext), "");
        File.WriteAllText(Path.Combine(_tempDir, "qemu-img" + ext), "");
        QemuLocator.OverridePath = _tempDir;

        var binaries = QemuLocator.Find();
        Assert.Multiple(() =>
        {
            Assert.That(binaries.SystemEmulator, Does.StartWith(_tempDir));
            Assert.That(binaries.QemuImg, Does.StartWith(_tempDir));
        });
    }

    [Test]
    public void Find_Throws_WhenOverrideDirectoryIsIncomplete()
    {
        QemuLocator.OverridePath = _tempDir; // empty dir: neither binary present
        Assert.Throws<QemuNotFoundException>(() => QemuLocator.Find());
    }

    [TestCase("QEMU emulator version 11.0.1 (v11.0.1)", "11.0.1")]
    [TestCase("QEMU emulator version 8.2.2 (Debian 1:8.2.2+ds-0ubuntu1)", "8.2.2")]
    [TestCase("QEMU emulator version 11.0", "11.0")]
    public void ParseVersion_ExtractsTheVersion(string banner, string expected)
        => Assert.That(QemuLocator.ParseVersion(banner), Is.EqualTo(Version.Parse(expected)));

    [Test]
    public void ParseVersion_ReturnsNull_OnGarbage()
        => Assert.That(QemuLocator.ParseVersion("not a version banner"), Is.Null);
}
