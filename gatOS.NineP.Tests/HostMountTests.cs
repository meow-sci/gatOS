using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.NineP.Vfs;

namespace gatOS.NineP.Tests;

/// <summary>
///     Exercises the host-filesystem passthrough VFS (the <c>/mnt/&lt;name&gt;</c> folder-mounts
///     feature) end to end: the managed 9p client drives a live <see cref="NinePServer"/> whose root
///     is a <see cref="HostMountTree"/> over real temp directories — one read-write, one read-only.
///     Proves reads/stat, the full write+create+remove+rename surface on a writable mount, truncate,
///     and that a read-only mount and out-of-bounds names are rejected with the right errno.
/// </summary>
[TestFixture]
public sealed class HostMountTests
{
    private const uint OWronly = 1;
    private const uint ORdwr = 2;
    private const uint OTrunc = 0x200;
    private const uint AtRemovedir = 0x200;

    private string _root = null!;
    private string _rwDir = null!;
    private string _roDir = null!;
    private NinePServer _server = null!;
    private NinePTestClient _client = null!;
    private uint _fidSeq;

    [SetUp]
    public async Task SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "gatos-mnt-" + Guid.NewGuid().ToString("N"));
        _rwDir = Path.Combine(_root, "rw");
        _roDir = Path.Combine(_root, "ro");
        Directory.CreateDirectory(_rwDir);
        Directory.CreateDirectory(_roDir);
        File.WriteAllText(Path.Combine(_rwDir, "hello.txt"), "hello rw");
        File.WriteAllText(Path.Combine(_roDir, "notes.txt"), "read me");
        Directory.CreateDirectory(Path.Combine(_rwDir, "sub"));

        var tree = HostMountTree.Build([
            new HostMountSpec("work", _rwDir, Writable: true),
            new HostMountSpec("docs", _roDir, Writable: false),
        ]);
        _server = new NinePServer(tree);
        await _server.StartAsync();
        _client = await NinePTestClient.ConnectAsync(_server.Port);
        await _client.VersionAsync();
        await _client.AttachAsync(0);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private uint NextFid() => Interlocked.Increment(ref _fidSeq);

    /// <summary>Walks from the root (fid 0) to a fresh fid through the given names.</summary>
    private async Task<uint> WalkAsync(params string[] names)
    {
        var fid = NextFid();
        var qids = await _client.WalkAsync(0, fid, names);
        Assert.That(qids, Has.Length.EqualTo(names.Length), "walk resolved every component");
        return fid;
    }

    private async Task<string> ReadAllAsync(string mount, params string[] rest)
    {
        var path = new[] { mount }.Concat(rest).ToArray();
        var fid = await WalkAsync(path);
        await _client.LopenAsync(fid, 0);
        var bytes = await _client.ReadToEndAsync(fid);
        await _client.ClunkAsync(fid);
        return Encoding.UTF8.GetString(bytes);
    }

    // ---- reads / stat ----------------------------------------------------------------------

    [Test]
    public async Task Root_ListsMountNames()
    {
        var fid = await WalkAsync(); // clone the root fid (readdir needs an opened fid)
        await _client.LopenAsync(fid, 0);
        var entries = await _client.ReaddirAllAsync(fid);
        var names = entries.Select(e => e.Name).ToArray();
        Assert.That(names, Does.Contain("work"));
        Assert.That(names, Does.Contain("docs"));
    }

    [Test]
    public async Task ReadFile_ReturnsHostContent()
        => Assert.That(await ReadAllAsync("work", "hello.txt"), Is.EqualTo("hello rw"));

    [Test]
    public async Task Readdir_ListsRealEntries()
    {
        var fid = await WalkAsync("work");
        await _client.LopenAsync(fid, 0);
        var names = (await _client.ReaddirAllAsync(fid)).Select(e => e.Name).ToArray();
        Assert.That(names, Does.Contain("hello.txt"));
        Assert.That(names, Does.Contain("sub"));
    }

    [Test]
    public async Task Getattr_ReportsTruthfulSizeMtimeAndMode()
    {
        var hostPath = Path.Combine(_rwDir, "hello.txt");
        var fid = await WalkAsync("work", "hello.txt");
        var attrs = await _client.GetattrAsync(fid);
        var expectedMtime = (ulong)new DateTimeOffset(File.GetLastWriteTimeUtc(hostPath)).ToUnixTimeSeconds();
        Assert.Multiple(() =>
        {
            Assert.That(attrs.Size, Is.EqualTo((ulong)new FileInfo(hostPath).Length));
            Assert.That(attrs.Mode & 0x1FF, Is.EqualTo(0x1A4u), "writable mount → 0644");
            Assert.That(attrs.MtimeSec, Is.EqualTo(expectedMtime));
        });
    }

    [Test]
    public async Task Getattr_ReadOnlyMountFile_IsMode0444()
    {
        var fid = await WalkAsync("docs", "notes.txt");
        var attrs = await _client.GetattrAsync(fid);
        Assert.That(attrs.Mode & 0x1FF, Is.EqualTo(0x124u), "read-only mount → 0444");
    }

    // ---- writes / create / remove / rename (writable mount) --------------------------------

    [Test]
    public async Task Write_OverwritesExistingFile()
    {
        var fid = await WalkAsync("work", "hello.txt");
        await _client.LopenAsync(fid, OWronly | OTrunc);
        await _client.WriteAsync(fid, 0, "changed"u8.ToArray());
        await _client.ClunkAsync(fid);
        Assert.That(File.ReadAllText(Path.Combine(_rwDir, "hello.txt")), Is.EqualTo("changed"));
    }

    [Test]
    public async Task Lcreate_CreatesAndWritesNewFile()
    {
        var dir = await WalkAsync("work");
        await _client.LcreateAsync(dir, "new.txt", OWronly);
        await _client.WriteAsync(dir, 0, "fresh"u8.ToArray());
        await _client.ClunkAsync(dir);
        Assert.That(File.ReadAllText(Path.Combine(_rwDir, "new.txt")), Is.EqualTo("fresh"));
    }

    [Test]
    public async Task Lcreate_ExistingName_IsEexist()
    {
        var dir = await WalkAsync("work");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.LcreateAsync(dir, "hello.txt", OWronly));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EEXIST));
    }

    [Test]
    public async Task Mkdir_CreatesDirectory()
    {
        var dir = await WalkAsync("work");
        await _client.MkdirAsync(dir, "newdir");
        Assert.That(Directory.Exists(Path.Combine(_rwDir, "newdir")), Is.True);
    }

    [Test]
    public async Task Unlinkat_RemovesFile()
    {
        File.WriteAllText(Path.Combine(_rwDir, "tmp.txt"), "x");
        var dir = await WalkAsync("work");
        await _client.UnlinkatAsync(dir, "tmp.txt");
        Assert.That(File.Exists(Path.Combine(_rwDir, "tmp.txt")), Is.False);
    }

    [Test]
    public async Task Unlinkat_NonEmptyDir_IsEnotempty()
    {
        Directory.CreateDirectory(Path.Combine(_rwDir, "full"));
        File.WriteAllText(Path.Combine(_rwDir, "full", "a"), "a");
        var dir = await WalkAsync("work");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.UnlinkatAsync(dir, "full", AtRemovedir));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOTEMPTY));
    }

    [Test]
    public async Task Unlinkat_EmptyDir_Removes()
    {
        Directory.CreateDirectory(Path.Combine(_rwDir, "empty"));
        var dir = await WalkAsync("work");
        await _client.UnlinkatAsync(dir, "empty", AtRemovedir);
        Assert.That(Directory.Exists(Path.Combine(_rwDir, "empty")), Is.False);
    }

    [Test]
    public async Task Renameat_MovesFile()
    {
        File.WriteAllText(Path.Combine(_rwDir, "from.txt"), "move me");
        var dir = await WalkAsync("work");
        var dir2 = await WalkAsync("work");
        await _client.RenameatAsync(dir, "from.txt", dir2, "to.txt");
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(_rwDir, "from.txt")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(_rwDir, "to.txt")), Is.EqualTo("move me"));
        });
    }

    [Test]
    public async Task SetattrSize_TruncatesFile()
    {
        File.WriteAllText(Path.Combine(_rwDir, "big.txt"), "0123456789");
        var fid = await WalkAsync("work", "big.txt");
        await _client.LopenAsync(fid, ORdwr);
        await _client.SetattrSizeAsync(fid, 4);
        await _client.ClunkAsync(fid);
        Assert.That(File.ReadAllText(Path.Combine(_rwDir, "big.txt")), Is.EqualTo("0123"));
    }

    // ---- read-only mount + safety ----------------------------------------------------------

    [Test]
    public async Task ReadOnlyMount_Lcreate_IsErofs()
    {
        var dir = await WalkAsync("docs");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.LcreateAsync(dir, "nope.txt", OWronly));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EROFS));
    }

    [Test]
    public async Task ReadOnlyMount_OpenForWrite_IsEacces()
    {
        var fid = await WalkAsync("docs", "notes.txt");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.LopenAsync(fid, OWronly));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EACCES));
    }

    [Test]
    public async Task ReadOnlyMount_StillReadable()
        => Assert.That(await ReadAllAsync("docs", "notes.txt"), Is.EqualTo("read me"));

    [Test]
    public async Task Lcreate_InvalidName_IsEinval()
    {
        var dir = await WalkAsync("work");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.LcreateAsync(dir, "..", OWronly));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }
}
