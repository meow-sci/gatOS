using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     The <see cref="CameraStore"/> in isolation: the track table and its caps, the commit/version
///     model, the HTTP chunked-upload mirror, the volatile status publish, the C3 commit seam, and the
///     bounded event queue.
/// </summary>
[TestFixture]
public sealed class CameraStoreTests
{
    private static CameraStore Small()
        => new(new CameraLimits(MaxTracks: 3, MaxTrackBytes: 64, MaxTotalBytes: 128, MaxKeys: 8));

    // ---- names ------------------------------------------------------------------------------------

    [TestCase("flyby", true)]
    [TestCase("fly-by.v2_1", true)]
    [TestCase("", false)]
    [TestCase(".", false)]
    [TestCase("..", false)]
    [TestCase("sub/dir", false)]
    [TestCase("has space", false)]
    public void IsValidName_IsTheSharedSimCharset(string name, bool valid)
        => Assert.That(CameraStore.IsValidName(name), Is.EqualTo(valid));

    // ---- commit model -----------------------------------------------------------------------------

    [Test]
    public void ATrack_IsInvisibleToPlayUntilItCommits()
    {
        var store = Small();
        var upload = store.OpenUpload("flyby", mustCreate: true);
        upload.Write(0, "{}"u8);

        Assert.Multiple(() =>
        {
            Assert.That(store.Exists("flyby"), Is.True, "the name is claimed immediately");
            Assert.That(store.TryGet("flyby", out var pending), Is.EqualTo(CameraTrackLookup.Uploading));
            Assert.That(pending, Is.Null);
            Assert.That(store.CurrentVersion("flyby"), Is.Null);
        });

        upload.Commit();
        Assert.Multiple(() =>
        {
            Assert.That(store.TryGet("flyby", out var ready), Is.EqualTo(CameraTrackLookup.Ready));
            Assert.That(ready!.Bytes, Is.EqualTo("{}"u8.ToArray()));
            Assert.That(ready.Version, Is.EqualTo(1));
            Assert.That(store.SizeOf("flyby"), Is.EqualTo(2));
        });
    }

    [Test]
    public void MissingTrack_IsMissing()
        => Assert.That(Small().TryGet("nope", out _), Is.EqualTo(CameraTrackLookup.Missing));

    [Test]
    public void Reupload_InstallsAFreshArray_SoAHeldReferenceNeverMutates()
    {
        var store = Small();
        store.HttpUpload("flyby", 0, "one"u8, complete: true);
        store.TryGet("flyby", out var first);

        store.HttpUpload("flyby", 0, "two"u8, complete: true);
        store.TryGet("flyby", out var second);

        Assert.Multiple(() =>
        {
            Assert.That(first!.Bytes, Is.EqualTo("one"u8.ToArray()), "the shot that started on v1 keeps v1");
            Assert.That(second!.Bytes, Is.EqualTo("two"u8.ToArray()));
            Assert.That(second.Version, Is.EqualTo(2));
        });
    }

    [Test]
    public void Truncate_MakesTheCommittedBytesUnreachableImmediately()
    {
        var store = Small();
        store.HttpUpload("flyby", 0, "hello"u8, complete: true);

        var upload = store.OpenUpload("flyby", mustCreate: false);
        upload.SetLength(0);
        Assert.That(store.TryGet("flyby", out _), Is.EqualTo(CameraTrackLookup.Uploading),
            "a truncate is visible before the commit, exactly like a real file");
        upload.Commit();
        Assert.That(store.SizeOf("flyby"), Is.EqualTo(0));
    }

    [Test]
    public void OpenWithoutTruncate_SeedsWithTheCommittedBytes()
    {
        var store = Small();
        store.HttpUpload("flyby", 0, "ab"u8, complete: true);
        var upload = store.OpenUpload("flyby", mustCreate: false);
        upload.Write(2, "cd"u8);
        upload.Commit();
        Assert.That(store.SnapshotBytes("flyby"), Is.EqualTo("abcd"u8.ToArray()));
    }

    [Test]
    public void SparseWrite_ZeroFillsTheGap()
    {
        var store = Small();
        var upload = store.OpenUpload("flyby", mustCreate: true);
        upload.Write(3, "x"u8);
        upload.Commit();
        Assert.That(store.SnapshotBytes("flyby"), Is.EqualTo(new byte[] { 0, 0, 0, (byte)'x' }));
    }

    [Test]
    public void Abort_LeavesNothingCommitted_AndReleasesTheByteAccounting()
    {
        var store = Small();
        var upload = store.OpenUpload("a", mustCreate: true);
        upload.Write(0, new byte[64]);
        upload.Abort();

        // The store cap is 128; had the aborted 64 bytes stayed accounted, this second track would
        // have to fail on the last byte.
        store.HttpUpload("b", 0, new byte[64], complete: true);
        store.HttpUpload("c", 0, new byte[64], complete: true);
        Assert.That(store.Usage().Bytes, Is.EqualTo(128));
    }

    // ---- caps -------------------------------------------------------------------------------------

    [Test]
    public void PerTrackCap_IsEfbig()
    {
        var store = Small();
        var upload = store.OpenUpload("big", mustCreate: true);
        upload.Write(0, new byte[64]);
        var ex = Assert.Throws<VfsErrorException>(() => upload.Write(64, new byte[1]));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EFBIG));
    }

    [Test]
    public void StoreCap_IsEnospc()
    {
        var store = Small();
        store.HttpUpload("a", 0, new byte[64], complete: true);
        store.HttpUpload("b", 0, new byte[64], complete: true);
        var upload = store.OpenUpload("c", mustCreate: true);
        var ex = Assert.Throws<VfsErrorException>(() => upload.Write(0, new byte[1]));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOSPC));
    }

    [Test]
    public void TrackCountCap_IsEnospc()
    {
        var store = Small();
        for (var i = 0; i < 3; i++)
            store.HttpUpload($"t{i}", 0, new byte[1], complete: true);
        var ex = Assert.Throws<VfsErrorException>(() => store.OpenUpload("t3", mustCreate: true));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOSPC));
    }

    [Test]
    public void BadName_IsEinval_AndDuplicateCreate_IsEexist()
    {
        var store = Small();
        Assert.Multiple(() =>
        {
            Assert.That(Assert.Throws<VfsErrorException>(() => store.OpenUpload("bad name", mustCreate: true))!
                .Errno, Is.EqualTo(LinuxErrno.EINVAL));
            store.HttpUpload("flyby", 0, "{}"u8, complete: true);
            Assert.That(Assert.Throws<VfsErrorException>(() => store.OpenUpload("flyby", mustCreate: true))!
                .Errno, Is.EqualTo(LinuxErrno.EEXIST));
        });
    }

    // ---- listing / delete / clear -------------------------------------------------------------------

    [Test]
    public void List_IsNameSorted_AndCarriesTheUploadState()
    {
        var store = Small();
        store.HttpUpload("b", 0, new byte[7], complete: true);
        store.OpenUpload("a", mustCreate: true); // opened, never committed

        var list = store.List();
        Assert.Multiple(() =>
        {
            Assert.That(list.Select(t => t.Name), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(list[0].Ready, Is.False);
            Assert.That(list[0].Bytes, Is.EqualTo(0));
            Assert.That(list[1].Ready, Is.True);
            Assert.That(list[1].Bytes, Is.EqualTo(7));
            Assert.That(list[1].Version, Is.EqualTo(1));
        });
    }

    [Test]
    public void Delete_EvictsAndThenAnswersEnoent()
    {
        var store = Small();
        store.HttpUpload("flyby", 0, "{}"u8, complete: true);
        store.Delete("flyby");
        Assert.Multiple(() =>
        {
            Assert.That(store.Exists("flyby"), Is.False);
            Assert.That(Assert.Throws<VfsErrorException>(() => store.Delete("flyby"))!.Errno,
                Is.EqualTo(LinuxErrno.ENOENT));
        });
    }

    [Test]
    public void Clear_EmptiesEverything()
    {
        var store = Small();
        store.HttpUpload("a", 0, new byte[4], complete: true);
        store.Clear();
        Assert.That(store.Usage(), Is.EqualTo((0, 0L)));
    }

    // ---- HTTP chunking ---------------------------------------------------------------------------------

    [Test]
    public void HttpUpload_AppendsByPositionAndCommitsOnComplete()
    {
        var store = Small();
        store.HttpUpload("flyby", 0, "abc"u8, complete: false);
        store.HttpUpload("flyby", 3, "def"u8, complete: true);
        Assert.That(store.SnapshotBytes("flyby"), Is.EqualTo("abcdef"u8.ToArray()));
    }

    [Test]
    public void HttpUpload_RejectsAnOutOfOrderChunk_AndVoidsTheUpload()
    {
        var store = Small();
        store.HttpUpload("flyby", 0, "abc"u8, complete: false);
        Assert.Multiple(() =>
        {
            Assert.That(Assert.Throws<VfsErrorException>(
                () => store.HttpUpload("flyby", 9, "x"u8, complete: false))!.Errno,
                Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(Assert.Throws<VfsErrorException>(
                () => store.HttpUpload("nope", 5, "x"u8, complete: false))!.Errno,
                Is.EqualTo(LinuxErrno.EINVAL), "chunks must start at offset=0");
        });
    }

    // ---- the C3 commit seam -----------------------------------------------------------------------------

    [Test]
    public void OnTrackCommitted_FiresOncePerCommit_WithTheCommittedBytes()
    {
        var store = Small();
        var seen = new List<CameraTrack>();
        store.OnTrackCommitted = seen.Add;

        store.HttpUpload("flyby", 0, "{}"u8, complete: true);
        store.HttpUpload("flyby", 0, "[]"u8, complete: true);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Has.Count.EqualTo(2));
            Assert.That(seen[0].Version, Is.EqualTo(1));
            Assert.That(seen[1].Version, Is.EqualTo(2));
            Assert.That(seen[1].Bytes, Is.EqualTo("[]"u8.ToArray()));
        });
    }

    [Test]
    public void OnTrackCommitted_DoesNotFireForAnAbortedUpload()
    {
        var store = Small();
        var fired = 0;
        store.OnTrackCommitted = _ => fired++;
        store.OpenUpload("flyby", mustCreate: true).Abort();
        Assert.That(fired, Is.Zero);
    }

    // ---- status + state ---------------------------------------------------------------------------------

    [Test]
    public void Status_StartsIdle_AndIsReplacedByOnePublish()
    {
        var store = Small();
        Assert.That(store.Status, Is.SameAs(CameraStatus.Idle));

        var published = CameraStatus.Idle with
        {
            Owned = true,
            Mode = CameraModeKind.Fixed,
            Pose = CameraPose.Default with { Fov = 24 },
        };
        store.PublishStatus(published);
        Assert.Multiple(() =>
        {
            Assert.That(store.Status, Is.SameAs(published), "one volatile reference swap, never a merge");
            Assert.That(store.Status.Pose.Fov, Is.EqualTo(24));
        });
    }

    [Test]
    public void State_IsTheCompositorTheDirectorDrives()
    {
        var store = Small();
        store.State.SetOverride(CameraChannel.Fov, 24);
        Assert.That(store.State.Compose(null, CameraChannelMask.None).Fov, Is.EqualTo(24));
    }

    // ---- events ------------------------------------------------------------------------------------------

    [Test]
    public void Events_DrainOnce_AndAreBoundedAt64()
    {
        var store = Small();
        Assert.That(store.DrainEvents(), Is.Empty);

        for (var i = 0; i < 70; i++)
            store.EmitEvent(new SimEvent(i, "camera.shot", null, $"shot-{i}"));

        var drained = store.DrainEvents();
        Assert.Multiple(() =>
        {
            Assert.That(drained, Has.Count.EqualTo(64), "bounded: a disabled sampler can never grow it");
            Assert.That(drained[0].Detail, Is.EqualTo("shot-6"), "the OLDEST is dropped");
            Assert.That(store.DrainEvents(), Is.Empty, "a drain takes them");
        });
    }
}
