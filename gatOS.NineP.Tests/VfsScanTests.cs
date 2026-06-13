using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.NineP.Tests;

/// <summary>
///     <see cref="VfsScan"/> — the generic walk/read/write the field-level MQTT/HTTP projections use
///     to mirror the <c>/sim</c> filesystem leaf-by-leaf from the one tree the 9p server serves.
/// </summary>
[TestFixture]
public sealed class VfsScanTests
{
    private static (VfsDirectory Root, TestTree.CaptureControlFile Throttle) BuildTree()
    {
        var qid = 0UL;
        var throttle = new TestTree.CaptureControlFile("throttle", ++qid) { Value = "0.5" };
        var v1 = DelegateDirectory.Fixed("v1", ++qid,
            new StaticTextFile("radar", ++qid, () => "70950.5\n"),
            throttle);
        var byId = DelegateDirectory.Fixed("by-id", ++qid, v1);
        var active = DelegateDirectory.Fixed("active", ++qid,
            new StaticTextFile("radar", ++qid, () => "70950.5\n"));
        var vessels = DelegateDirectory.Fixed("vessels", ++qid, byId, active);
        var root = DelegateDirectory.Fixed("/", 1000,
            new StaticTextFile("ut", ++qid, () => "123\n"),
            vessels,
            new TestTree.GateFile("events", ++qid)); // IsStreaming — must be skipped
        return (root, throttle);
    }

    [Test]
    public void Leaves_EnumeratesScalarsWithPaths_AndSkipsStreamingFiles()
    {
        var (root, _) = BuildTree();
        var paths = VfsScan.Leaves(root).Select(l => l.Path).ToHashSet();
        Assert.Multiple(() =>
        {
            Assert.That(paths, Does.Contain("ut"));
            Assert.That(paths, Does.Contain("vessels/by-id/v1/radar"));
            Assert.That(paths, Does.Contain("vessels/by-id/v1/throttle"));
            Assert.That(paths, Does.Contain("vessels/active/radar"));
            Assert.That(paths, Does.Not.Contain("events"), "streaming files are skipped");
        });
    }

    [Test]
    public void Leaves_HonorsPrune()
    {
        var (root, _) = BuildTree();
        var paths = VfsScan.Leaves(root, p => p == "vessels/active").Select(l => l.Path).ToHashSet();
        Assert.Multiple(() =>
        {
            Assert.That(paths, Does.Contain("vessels/by-id/v1/radar"));
            Assert.That(paths.Where(p => p.StartsWith("vessels/active")), Is.Empty, "the alias subtree is pruned");
        });
    }

    [Test]
    public async Task Resolve_AndReadText_ReturnsTheLeafValue()
    {
        var (root, _) = BuildTree();
        var file = VfsScan.Resolve(root, "vessels/by-id/v1/radar");
        Assert.That(file, Is.Not.Null);
        Assert.That(await VfsScan.ReadTextAsync(file!), Is.EqualTo("70950.5"), "trailing newline trimmed");
    }

    [Test]
    public void Resolve_ReturnsNull_ForUnknownPathOrDirectory()
    {
        var (root, _) = BuildTree();
        Assert.Multiple(() =>
        {
            Assert.That(VfsScan.Resolve(root, "nope/x"), Is.Null);
            Assert.That(VfsScan.Resolve(root, "vessels/by-id"), Is.Null, "a directory is not a file");
            Assert.That(VfsScan.Resolve(root, ""), Is.Null);
        });
    }

    [Test]
    public async Task WriteText_ActuatesAWritableLeaf()
    {
        var (root, throttle) = BuildTree();
        var file = VfsScan.Resolve(root, "vessels/by-id/v1/throttle");
        await VfsScan.WriteTextAsync(file!, "0.8");
        Assert.That(throttle.LastWrite, Is.EqualTo("0.8"));
    }

    [Test]
    public void WriteText_OnReadOnlyLeaf_ThrowsEacces()
    {
        var (root, _) = BuildTree();
        var file = VfsScan.Resolve(root, "ut");
        var ex = Assert.ThrowsAsync<VfsErrorException>(async () => await VfsScan.WriteTextAsync(file!, "9"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EACCES));
    }
}
