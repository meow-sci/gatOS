using System.Diagnostics;
using System.Text.Json;

namespace gatOS.Vm.Tests;

/// <summary>
///     Covers <see cref="DiskManager"/> base install, overlay lifecycle and relative backing
///     refs (T3.2). Overlay tests run qemu-img and self-skip when QEMU is absent (unless
///     <c>GATOS_IT=1</c>, where it is required).
/// </summary>
[TestFixture]
[NonParallelizable] // uses the GatOsPaths data-dir override (process-global)
public sealed class DiskManagerTests
{
    private string _tempRoot = null!;
    private string _assetsDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-disks-" + Guid.NewGuid().ToString("N"));
        _assetsDir = Path.Combine(_tempRoot, "assets");
        Directory.CreateDirectory(_assetsDir);
        GatOsPaths.OverrideDataDirForTests(Path.Combine(_tempRoot, "data"));
    }

    [TearDown]
    public void TearDown()
    {
        GatOsPaths.OverrideDataDirForTests(null);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public void EnsureBaseInstalled_CopiesAllArtifacts_AndIsIdempotent()
    {
        WriteFakeAssets();
        var manager = new DiskManager(_assetsDir);

        var installed = manager.EnsureBaseInstalled();
        Assert.Multiple(() =>
        {
            Assert.That(installed.Manifest.GuestVersion, Is.EqualTo(1));
            Assert.That(installed.BaseImagePath,
                Is.EqualTo(Path.Combine(GatOsPaths.DisksDir, "base-v1.qcow2")));
            Assert.That(File.Exists(installed.BaseImagePath), Is.True);
            Assert.That(File.Exists(installed.KernelPath), Is.True);
            Assert.That(File.Exists(installed.InitrdPath), Is.True);
            Assert.That(File.Exists(installed.PrivateKeyPath), Is.True);
            Assert.That(File.Exists(Path.Combine(GatOsPaths.DisksDir, "guest-v1", "manifest.toml")), Is.True);
        });

        // Idempotent: a second call (fresh instance = no cache) must not re-copy.
        var stamp = File.GetLastWriteTimeUtc(installed.BaseImagePath);
        new DiskManager(_assetsDir).EnsureBaseInstalled();
        Assert.That(File.GetLastWriteTimeUtc(installed.BaseImagePath), Is.EqualTo(stamp));
    }

    [Test]
    public void EnsureBaseInstalled_FailsWithAReadableMessage_WhenAssetsAreMissing()
    {
        var manager = new DiskManager(Path.Combine(_tempRoot, "nope"));
        var ex = Assert.Throws<DiskOperationException>(() => manager.EnsureBaseInstalled());
        Assert.That(ex.Message, Does.Contain("fetch-guest"));
    }

    [Test]
    public void CreateOverlay_RecordsARelativeBackingRef_AndProfileMetadata()
    {
        var qemu = TestEnv.RequireQemu();
        WriteFakeAssets(realQcow2: qemu.QemuImg);
        var manager = new DiskManager(_assetsDir, () => qemu.QemuImg);

        var overlayPath = manager.GetOrCreateOverlay("default");
        Assert.That(File.Exists(overlayPath), Is.True);

        // The stored backing ref must be the bare relative filename (portable folder).
        using var info = JsonDocument.Parse(QemuImgInfo(qemu.QemuImg, overlayPath));
        Assert.Multiple(() =>
        {
            Assert.That(info.RootElement.GetProperty("backing-filename").GetString(),
                Is.EqualTo("base-v1.qcow2"));
            Assert.That(info.RootElement.GetProperty("backing-filename-format").GetString(),
                Is.EqualTo("qcow2"));
        });

        Assert.That(File.ReadAllText(Path.Combine(GatOsPaths.DisksDir, "default.toml")),
            Does.Contain("guest_version = 1"));
    }

    [Test]
    public void OverlayLifecycle_GetOrCreate_List_Delete()
    {
        var qemu = TestEnv.RequireQemu();
        WriteFakeAssets(realQcow2: qemu.QemuImg);
        var manager = new DiskManager(_assetsDir, () => qemu.QemuImg);

        var first = manager.GetOrCreateOverlay("save-alpha");
        var second = manager.GetOrCreateOverlay("save-alpha"); // idempotent
        Assert.That(second, Is.EqualTo(first));
        Assert.That(manager.ListOverlays(), Is.EqualTo(new[] { "save-alpha" }));

        manager.DeleteOverlay("save-alpha");
        Assert.Multiple(() =>
        {
            Assert.That(manager.ListOverlays(), Is.Empty);
            Assert.That(File.Exists(Path.Combine(GatOsPaths.DisksDir, "save-alpha.toml")), Is.False);
            // The shared base must survive overlay deletion.
            Assert.That(File.Exists(Path.Combine(GatOsPaths.DisksDir, "base-v1.qcow2")), Is.True);
        });
    }

    [Test]
    public void DeleteOverlay_Refuses_WhileTheOverlayIsLocked()
    {
        var qemu = TestEnv.RequireQemu();
        WriteFakeAssets(realQcow2: qemu.QemuImg);
        var manager = new DiskManager(_assetsDir, () => qemu.QemuImg);
        manager.GetOrCreateOverlay("busy");

        using var hold = manager.AcquireOverlayLock("busy");
        Assert.Throws<DiskOperationException>(() => manager.DeleteOverlay("busy"));
    }

    [TestCase("../escape")]
    [TestCase("a/b")]
    [TestCase("")]
    [TestCase("base-v1")]
    public void ProfileNames_ArePathSafe(string bad)
    {
        WriteFakeAssets();
        var manager = new DiskManager(_assetsDir);
        Assert.Throws<ArgumentException>(() => manager.GetOrCreateOverlay(bad));
    }

    /// <summary>
    ///     Lays down a guest-assets dir matching the manifest contract. When
    ///     <paramref name="realQcow2"/> is given (a qemu-img path), base.qcow2 is a real qcow2
    ///     (overlay creation opens the backing file); otherwise placeholder bytes suffice.
    /// </summary>
    private void WriteFakeAssets(string? realQcow2 = null)
    {
        File.WriteAllText(Path.Combine(_assetsDir, "manifest.toml"), """
            schema = 1
            guest_version = 1
            alpine_version = "3.24.0"
            kernel = "vmlinuz-virt"
            initrd = "initramfs-virt"
            base_image = "base.qcow2"
            kernel_cmdline = "console=ttyS0 root=/dev/vda rw quiet"
            ssh_user = "root"
            ssh_key = "id_ed25519"
            host_key_sha256 = "00ff"
            """);
        File.WriteAllText(Path.Combine(_assetsDir, "vmlinuz-virt"), "kernel");
        File.WriteAllText(Path.Combine(_assetsDir, "initramfs-virt"), "initrd");
        File.WriteAllText(Path.Combine(_assetsDir, "id_ed25519"), "key");

        var basePath = Path.Combine(_assetsDir, "base.qcow2");
        if (realQcow2 is null)
        {
            File.WriteAllText(basePath, "qcow2-placeholder");
        }
        else
        {
            using var process = Process.Start(new ProcessStartInfo(realQcow2)
            {
                ArgumentList = { "create", "-f", "qcow2", basePath, "16M" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.Zero, "fixture qemu-img create failed");
        }
    }

    private static string QemuImgInfo(string qemuImg, string imagePath)
    {
        using var process = Process.Start(new ProcessStartInfo(qemuImg)
        {
            ArgumentList = { "info", "--output=json", imagePath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.Zero, "qemu-img info failed");
        return stdout;
    }
}
