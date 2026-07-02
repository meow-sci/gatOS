using gatOS.NineP.Vfs;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests;

/// <summary>
///     GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP1: the read surface memoizes per snapshot —
///     cached subtrees (a walk no longer materializes ~60–100 nodes), per-snapshot leaf formatting
///     (stat + open + N readers = one format), and shared stream lines across fids.
/// </summary>
[TestFixture]
public sealed class SnapshotMemoTests
{
    private static SnapshotStore StoreWith(params VesselSnapshot[] vessels)
    {
        var store = new SnapshotStore();
        store.Publish(TestData.Snapshot(1, vessels));
        return store;
    }

    private static VfsDirectory Dir(VfsDirectory parent, string name)
        => (VfsDirectory)(parent.Lookup(name) ?? throw new InvalidOperationException($"missing dir '{name}'"));

    [Test]
    public void VesselSubtree_IsBuiltOnce_AndReusedAcrossWalksAndSnapshots()
    {
        var store = StoreWith(TestData.FullVessel());
        var root = SimFsTree.Build(store);

        var byId = Dir(Dir(root, "vessels"), "by-id");
        var first = byId.Lookup("test-1");
        var second = byId.Lookup("test-1");
        Assert.That(second, Is.SameAs(first), "the vessel dir must be cached, not rebuilt per walk");

        var altitude1 = ((VfsDirectory)first!).Lookup("altitude");
        var altitude2 = ((VfsDirectory)first).Lookup("altitude");
        Assert.That(altitude2, Is.SameAs(altitude1), "child nodes must be cached too");

        // A new snapshot does not invalidate the node graph — content reads the live snapshot.
        store.Publish(TestData.Snapshot(2, TestData.FullVessel()));
        Assert.That(byId.Lookup("test-1"), Is.SameAs(first));
    }

    [Test]
    public void ActiveAlias_ResolvesToTheSameCachedNodes()
    {
        var store = StoreWith(TestData.FullVessel());
        var root = SimFsTree.Build(store);
        var vessels = Dir(root, "vessels");
        var viaById = Dir(Dir(vessels, "by-id"), "test-1").Lookup("altitude");
        var viaActive = Dir(vessels, "active").Lookup("altitude");
        Assert.That(viaActive, Is.SameAs(viaById), "active/ is an alias onto the same cached subtree");
    }

    [Test]
    public void ConditionalChildren_TrackTheLiveSnapshot()
    {
        var store = StoreWith(TestData.Vessel(battery: 0.5));
        var root = SimFsTree.Build(store);
        var vessel = Dir(Dir(Dir(root, "vessels"), "by-id"), "test-1");
        Assert.That(vessel.Lookup("battery"), Is.Not.Null);

        store.Publish(TestData.Snapshot(2, TestData.Vessel(battery: null)));
        Assert.That(vessel.Lookup("battery"), Is.Null, "presence must follow the live snapshot");
        Assert.That(vessel.List().Select(n => n.Name), Does.Not.Contain("battery"));

        store.Publish(TestData.Snapshot(3, TestData.Vessel(battery: 0.9)));
        Assert.That(vessel.Lookup("battery"), Is.Not.Null);
    }

    [Test]
    public void GoneVessel_StillAnswersEnoent()
    {
        var store = StoreWith(TestData.Vessel());
        var root = SimFsTree.Build(store);
        var byId = Dir(Dir(root, "vessels"), "by-id");
        var vessel = (VfsDirectory)byId.Lookup("test-1")!;

        store.Publish(TestData.Snapshot(2, TestData.Vessel("other")));
        Assert.That(byId.Lookup("test-1"), Is.Null, "gone vessel no longer resolves from by-id");
        Assert.Throws<VfsErrorException>(() => vessel.List(), "a held dir for a gone vessel answers ENOENT");
    }

    [Test]
    public void SnapshotTextFile_FormatsOncePerSnapshot()
    {
        var store = StoreWith(TestData.Vessel());
        var calls = 0;
        var file = new SnapshotTextFile("x", 1, store, () =>
        {
            calls++;
            return "value\n";
        });

        _ = file.Size;            // stat
        using (file.Open())       // open
        {
        }

        using (file.Open())       // second reader
        {
        }

        Assert.That(calls, Is.EqualTo(1), "stat + two opens within one snapshot must format once");

        store.Publish(TestData.Snapshot(2, TestData.Vessel()));
        _ = file.Size;
        Assert.That(calls, Is.EqualTo(2), "a publish invalidates the memo");
    }

    [Test]
    public void StreamLine_IsSharedAcrossConsumersOfOneSnapshot()
    {
        var store = StoreWith(TestData.Vessel());
        var snapshot = store.Current;
        var vessel = snapshot.Vessels[0];
        var first = SnapshotIndex.StreamLine(snapshot, vessel);
        var second = SnapshotIndex.StreamLine(snapshot, vessel);
        Assert.That(second, Is.SameAs(first), "one snapshot's line is formatted once and shared");
        Assert.That(first, Is.EqualTo(Formats.StreamLine(snapshot, vessel)), "and byte-identical to a fresh format");
    }
}
