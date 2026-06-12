namespace gatOS.Vm.Tests;

/// <summary>Covers <see cref="GuestManifest"/> parsing and validation (T3.2).</summary>
[TestFixture]
public sealed class GuestManifestTests
{
    private const string ValidManifest = """
        schema = 1
        guest_version = 1
        alpine_version = "3.24.0"
        kernel = "vmlinuz-virt"
        initrd = "initramfs-virt"
        base_image = "base.qcow2"
        kernel_cmdline = "console=ttyS0 root=/dev/vda rw quiet rootfstype=ext4 modules=virtio,ext4"
        ssh_user = "root"
        ssh_key = "id_ed25519"
        host_key_sha256 = "5BE6C4D66E91CA68688910C1CEEFEFF0239DB8FBB84289DF831ED31FE5E382A0"
        built_utc = "2026-06-12T03:18:05Z"
        """;

    private string _path = null!;

    [SetUp]
    public void SetUp() => _path = Path.GetTempFileName();

    [TearDown]
    public void TearDown() => File.Delete(_path);

    [Test]
    public void Load_ParsesAllFields_AndNormalizesThePinToLowercase()
    {
        File.WriteAllText(_path, ValidManifest);
        var manifest = GuestManifest.Load(_path);
        Assert.Multiple(() =>
        {
            Assert.That(manifest.GuestVersion, Is.EqualTo(1));
            Assert.That(manifest.AlpineVersion, Is.EqualTo("3.24.0"));
            Assert.That(manifest.Kernel, Is.EqualTo("vmlinuz-virt"));
            Assert.That(manifest.Initrd, Is.EqualTo("initramfs-virt"));
            Assert.That(manifest.BaseImage, Is.EqualTo("base.qcow2"));
            Assert.That(manifest.KernelCmdline, Does.StartWith("console=ttyS0 root=/dev/vda"));
            Assert.That(manifest.SshUser, Is.EqualTo("root"));
            Assert.That(manifest.SshKey, Is.EqualTo("id_ed25519"));
            Assert.That(manifest.HostKeySha256,
                Is.EqualTo("5be6c4d66e91ca68688910c1ceefeff0239db8fbb84289df831ed31fe5e382a0"));
        });
    }

    [Test]
    public void Load_Rejects_UnsupportedSchema()
    {
        File.WriteAllText(_path, ValidManifest.Replace("schema = 1", "schema = 2"));
        var ex = Assert.Throws<InvalidDataException>(() => GuestManifest.Load(_path));
        Assert.That(ex.Message, Does.Contain("schema 2"));
    }

    [Test]
    public void Load_Rejects_MissingRequiredKey()
    {
        File.WriteAllText(_path, ValidManifest.Replace("ssh_user = \"root\"", ""));
        var ex = Assert.Throws<InvalidDataException>(() => GuestManifest.Load(_path));
        Assert.That(ex.Message, Does.Contain("ssh_user"));
    }

    [Test]
    public void Load_Rejects_InvalidToml()
    {
        File.WriteAllText(_path, "this is = = not toml [");
        Assert.Throws<InvalidDataException>(() => GuestManifest.Load(_path));
    }
}
