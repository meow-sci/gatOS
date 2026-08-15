using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.Paint;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

[TestFixture]
public sealed class PaintTreeTests
{
    private SnapshotStore _snapshots = null!;
    private PaintStore _paint = null!;
    private FakeCommandSink _sink = null!;
    private VfsDirectory _root = null!;

    [SetUp]
    public void SetUp()
    {
        _snapshots = new SnapshotStore();
        _snapshots.Publish(TestData.Snapshot(1, TestData.Vessel()));
        _paint = new PaintStore();
        _sink = new FakeCommandSink();
        _root = SimFsTree.Build(_snapshots, _sink, () => "test", paint: _paint);
    }

    [Test]
    public async Task RootMasters_AreVisibleWhileDisabled_AndBuildCanonicalCommands()
    {
        var enabled = VfsScan.Resolve(_root, "paint/parts/enabled");
        Assert.That(await VfsScan.ReadTextAsync(enabled!), Is.EqualTo("0"));
        await VfsScan.WriteTextAsync(enabled!, "1");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", SimActions.PaintPartsEnabled, -1, 1)));

        var kittens = VfsScan.Resolve(_root, "paint/kittens/enabled");
        await VfsScan.WriteTextAsync(kittens!, "1");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", SimActions.PaintKittensEnabled, -1, 1)));
    }

    [Test]
    public async Task GlobalColor_ValidatesNormalizedRgbBeforeQueue()
    {
        var file = VfsScan.Resolve(_root, "paint/parts/global/color")!;
        await VfsScan.WriteTextAsync(file, "0.1 0.2 0.3");
        Assert.That(_sink.Last!.Action, Is.EqualTo(SimActions.PaintGlobalColor));
        Assert.That(_sink.Last.Values, Is.EqualTo(new[] { 0.1, 0.2, 0.3 }));

        var submits = _sink.Submits;
        var ex = Assert.ThrowsAsync<VfsErrorException>(async () => await VfsScan.WriteTextAsync(file, "2 0 0"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
        Assert.That(_sink.Submits, Is.EqualTo(submits));
    }

    [Test]
    public async Task Blend_UsesPublishedTokens()
    {
        var file = VfsScan.Resolve(_root, "paint/parts/blend")!;
        await VfsScan.WriteTextAsync(file, "replace");
        Assert.That(_sink.Last!.Action, Is.EqualTo(SimActions.PaintBlend));
        Assert.That(_sink.Last.Token, Is.EqualTo("replace"));
    }
}
