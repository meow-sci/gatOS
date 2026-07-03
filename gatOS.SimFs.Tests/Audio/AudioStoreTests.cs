using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Audio;

/// <summary>
///     The <see cref="AudioStore"/> semantics (GATOS_CUSTOM_AUDIO_PLAN): name rules, ready-on-commit
///     visibility, versioning, the three caps with their errnos, delete, the session-less HTTP
///     chunked upload, and the bounded <c>audio.finished</c> event queue. Game-free by construction.
/// </summary>
[TestFixture]
public sealed class AudioStoreTests
{
    private static byte[] Bytes(int count, byte fill = 0xAB)
    {
        var data = new byte[count];
        Array.Fill(data, fill);
        return data;
    }

    private static void Upload(AudioStore store, string name, byte[] bytes)
    {
        var upload = store.OpenUpload(name, mustCreate: false);
        upload.SetLength(0);
        upload.Write(0, bytes);
        upload.Commit();
    }

    // ---- name rules --------------------------------------------------------------------------

    [TestCase("alarm.mp3", true)]
    [TestCase("A-Z_0.9", true)]
    [TestCase("x", true)]
    [TestCase("", false)]
    [TestCase(".", false)]
    [TestCase("..", false)]
    [TestCase("a b", false)]
    [TestCase("a/b", false)]
    [TestCase("a#b", false)]
    [TestCase("naïve.ogg", false)]
    public void NameRules(string name, bool valid) => Assert.That(AudioStore.IsValidName(name), Is.EqualTo(valid));

    [Test]
    public void NameRules_LengthCap()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AudioStore.IsValidName(new string('a', 64)), Is.True);
            Assert.That(AudioStore.IsValidName(new string('a', 65)), Is.False);
        });
    }

    // ---- ready-on-commit + versioning ----------------------------------------------------------

    [Test]
    public void Clip_IsInvisibleToPlay_UntilCommit()
    {
        var store = new AudioStore();
        var upload = store.OpenUpload("clip.ogg", mustCreate: true);
        upload.Write(0, Bytes(10));

        Assert.Multiple(() =>
        {
            Assert.That(store.Exists("clip.ogg"), Is.True, "the name is taken immediately");
            Assert.That(store.TryGet("clip.ogg", out _), Is.EqualTo(AudioClipLookup.Uploading), "⇒ EBUSY");
            Assert.That(store.TryGet("other", out _), Is.EqualTo(AudioClipLookup.Missing), "⇒ ENOENT");
        });

        upload.Commit();
        Assert.Multiple(() =>
        {
            Assert.That(store.TryGet("clip.ogg", out var clip), Is.EqualTo(AudioClipLookup.Ready));
            Assert.That(clip!.Bytes, Has.Length.EqualTo(10));
            Assert.That(clip.Version, Is.EqualTo(1));
            Assert.That(store.CurrentVersion("clip.ogg"), Is.EqualTo(1));
        });
    }

    [Test]
    public void Reupload_BumpsVersion_AndNeverMutatesTheOldBytes()
    {
        var store = new AudioStore();
        Upload(store, "clip.ogg", Bytes(4, 0x11));
        store.TryGet("clip.ogg", out var v1);

        Upload(store, "clip.ogg", Bytes(8, 0x22));
        store.TryGet("clip.ogg", out var v2);

        Assert.Multiple(() =>
        {
            Assert.That(v2!.Version, Is.EqualTo(2));
            Assert.That(v2.Bytes, Has.Length.EqualTo(8));
            Assert.That(v1!.Bytes, Has.Length.EqualTo(4), "a play-time reference stays valid");
            Assert.That(v1.Bytes[0], Is.EqualTo(0x11), "the old array is never touched");
        });
    }

    [Test]
    public void Truncate_MakesTheClipUploading_UntilRecommit()
    {
        var store = new AudioStore();
        Upload(store, "clip.ogg", Bytes(4));

        var rewrite = store.OpenUpload("clip.ogg", mustCreate: false);
        rewrite.SetLength(0); // the O_TRUNC a plain `cat >` carries
        Assert.Multiple(() =>
        {
            Assert.That(store.TryGet("clip.ogg", out _), Is.EqualTo(AudioClipLookup.Uploading), "⇒ EBUSY");
            Assert.That(store.SizeOf("clip.ogg"), Is.EqualTo(0), "ls sees the truncation");
        });

        rewrite.Write(0, Bytes(2));
        rewrite.Commit();
        Assert.That(store.SizeOf("clip.ogg"), Is.EqualTo(2));
    }

    [Test]
    public void OpenWithoutTruncate_SeedsForAppend()
    {
        var store = new AudioStore();
        Upload(store, "clip.ogg", [1, 2, 3]);

        var append = store.OpenUpload("clip.ogg", mustCreate: false);
        append.Write(3, [4, 5]); // O_APPEND lands at the current end
        append.Commit();

        Assert.That(store.SnapshotBytes("clip.ogg"), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void SparseWrite_ZeroFillsTheGap()
    {
        var store = new AudioStore();
        var upload = store.OpenUpload("clip.ogg", mustCreate: true);
        upload.Write(4, [9]);
        upload.Commit();
        Assert.That(store.SnapshotBytes("clip.ogg"), Is.EqualTo(new byte[] { 0, 0, 0, 0, 9 }));
    }

    [Test]
    public void ChunkedOffsets_AccumulateInOrder()
    {
        var store = new AudioStore();
        var upload = store.OpenUpload("clip.ogg", mustCreate: true);
        var payload = Encoding.ASCII.GetBytes("the quick brown cat");
        for (var i = 0; i < payload.Length; i += 5)
            upload.Write((ulong)i, payload.AsSpan(i, Math.Min(5, payload.Length - i)));
        upload.Commit();
        Assert.That(store.SnapshotBytes("clip.ogg"), Is.EqualTo(payload));
    }

    // ---- caps ----------------------------------------------------------------------------------

    [Test]
    public void PerClipCap_IsEfbig_MidWrite()
    {
        var store = new AudioStore(maxClipBytes: 16, maxTotalBytes: 1024);
        var upload = store.OpenUpload("clip.ogg", mustCreate: true);
        upload.Write(0, Bytes(16));
        var ex = Assert.Throws<VfsErrorException>(() => upload.Write(16, Bytes(1)));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EFBIG));
    }

    [Test]
    public void TotalCap_IsEnospc_CountingCommittedAndPending()
    {
        var store = new AudioStore(maxClipBytes: 64, maxTotalBytes: 100);
        Upload(store, "a", Bytes(60));

        var upload = store.OpenUpload("b", mustCreate: true);
        upload.Write(0, Bytes(40)); // exactly at the cap
        var ex = Assert.Throws<VfsErrorException>(() => upload.Write(40, Bytes(1)));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOSPC));
    }

    [Test]
    public void ClipCountCap_IsEnospc_AtCreate()
    {
        var store = new AudioStore(maxClips: 2);
        Upload(store, "a", Bytes(1));
        Upload(store, "b", Bytes(1));
        var ex = Assert.Throws<VfsErrorException>(() => store.OpenUpload("c", mustCreate: true));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOSPC));
    }

    [Test]
    public void AbortedUpload_ReleasesItsPendingBytes()
    {
        var store = new AudioStore(maxClipBytes: 64, maxTotalBytes: 100);
        var doomed = store.OpenUpload("a", mustCreate: true);
        doomed.Write(0, Bytes(60));
        doomed.Abort();

        Upload(store, "b", Bytes(60)); // fits again — the aborted 60 no longer counts
        Assert.That(store.SizeOf("b"), Is.EqualTo(60));
    }

    [Test]
    public void MustCreate_OnExistingName_IsEexist()
    {
        var store = new AudioStore();
        Upload(store, "a", Bytes(1));
        var ex = Assert.Throws<VfsErrorException>(() => store.OpenUpload("a", mustCreate: true));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EEXIST));
    }

    [Test]
    public void InvalidName_IsEinval()
    {
        var store = new AudioStore();
        var ex = Assert.Throws<VfsErrorException>(() => store.OpenUpload("no spaces", mustCreate: true));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    // ---- delete --------------------------------------------------------------------------------

    [Test]
    public void Delete_FreesTheName_AndMissingIsEnoent()
    {
        var store = new AudioStore();
        Upload(store, "a", Bytes(1));
        store.Delete("a");
        Assert.Multiple(() =>
        {
            Assert.That(store.Exists("a"), Is.False);
            Assert.That(Assert.Throws<VfsErrorException>(() => store.Delete("a"))!.Errno,
                Is.EqualTo(LinuxErrno.ENOENT));
        });
    }

    [Test]
    public void DeleteDuringUpload_DiscardsTheCommit()
    {
        var store = new AudioStore();
        var upload = store.OpenUpload("a", mustCreate: true);
        upload.Write(0, Bytes(4));
        store.Delete("a");
        upload.Commit(); // commits into a detached entry — write-after-unlink semantics
        Assert.That(store.Exists("a"), Is.False);
    }

    [Test]
    public void Usage_CountsCommittedBytes()
    {
        var store = new AudioStore();
        Upload(store, "a", Bytes(10));
        Upload(store, "b", Bytes(5));
        Assert.That(store.Usage(), Is.EqualTo((2, 15L)));
    }

    [Test]
    public void List_IsNameSorted_WithReadyFlags()
    {
        var store = new AudioStore();
        Upload(store, "zz", Bytes(3));
        var pending = store.OpenUpload("aa", mustCreate: true);
        pending.Write(0, Bytes(1));

        var list = store.List();
        Assert.Multiple(() =>
        {
            Assert.That(list.Select(c => c.Name), Is.EqualTo(new[] { "aa", "zz" }));
            Assert.That(list[0].Ready, Is.False);
            Assert.That(list[1], Is.EqualTo(new AudioClipInfo("zz", 3, 1, true)));
        });
    }

    // ---- HTTP chunked upload -------------------------------------------------------------------

    [Test]
    public void HttpUpload_SingleShot_Commits()
    {
        var store = new AudioStore();
        store.HttpUpload("a.mp3", 0, Bytes(7), complete: true);
        Assert.Multiple(() =>
        {
            Assert.That(store.TryGet("a.mp3", out var clip), Is.EqualTo(AudioClipLookup.Ready));
            Assert.That(clip!.Bytes, Has.Length.EqualTo(7));
        });
    }

    [Test]
    public void HttpUpload_Chunked_AppendsByPosition()
    {
        var store = new AudioStore();
        store.HttpUpload("a.mp3", 0, [1, 2, 3], complete: false);
        store.HttpUpload("a.mp3", 3, [4, 5], complete: false);
        Assert.That(store.TryGet("a.mp3", out _), Is.EqualTo(AudioClipLookup.Uploading), "not ready yet");
        store.HttpUpload("a.mp3", 5, [6], complete: true);
        Assert.That(store.SnapshotBytes("a.mp3"), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
    }

    [Test]
    public void HttpUpload_WrongOffset_IsEinval()
    {
        var store = new AudioStore();
        store.HttpUpload("a.mp3", 0, [1, 2, 3], complete: false);
        Assert.Multiple(() =>
        {
            Assert.That(Assert.Throws<VfsErrorException>(
                    () => store.HttpUpload("a.mp3", 7, [9], complete: false))!.Errno,
                Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(Assert.Throws<VfsErrorException>(
                    () => store.HttpUpload("b.mp3", 3, [9], complete: false))!.Errno,
                Is.EqualTo(LinuxErrno.EINVAL), "chunks must start at offset=0");
        });
    }

    [Test]
    public void HttpUpload_RestartAtZero_ReplacesThePendingUpload()
    {
        var store = new AudioStore();
        store.HttpUpload("a.mp3", 0, [1, 2, 3], complete: false);
        store.HttpUpload("a.mp3", 0, [9], complete: true); // a retry starts over
        Assert.That(store.SnapshotBytes("a.mp3"), Is.EqualTo(new byte[] { 9 }));
    }

    [Test]
    public void HttpUpload_FailedChunk_VoidsTheUpload()
    {
        var store = new AudioStore(maxClipBytes: 4);
        store.HttpUpload("a.mp3", 0, [1, 2, 3], complete: false);
        Assert.That(Assert.Throws<VfsErrorException>(
                () => store.HttpUpload("a.mp3", 3, [4, 5], complete: false))!.Errno,
            Is.EqualTo(LinuxErrno.EFBIG));
        // The pending upload is gone: continuing is EINVAL, restarting at 0 works.
        Assert.That(Assert.Throws<VfsErrorException>(
                () => store.HttpUpload("a.mp3", 3, [4], complete: false))!.Errno,
            Is.EqualTo(LinuxErrno.EINVAL));
        store.HttpUpload("a.mp3", 0, [7], complete: true);
        Assert.That(store.SnapshotBytes("a.mp3"), Is.EqualTo(new byte[] { 7 }));
    }

    // ---- audio.finished events -----------------------------------------------------------------

    [Test]
    public void Events_DrainInOrder_AndEmptyAfter()
    {
        var store = new AudioStore();
        store.EmitEvent(new SimEvent(1, "audio.finished", null, "#1 a.mp3 ended"));
        store.EmitEvent(new SimEvent(2, "audio.finished", null, "bgm b.ogg stopped"));

        var drained = store.DrainEvents();
        Assert.Multiple(() =>
        {
            Assert.That(drained.Select(e => e.Detail),
                Is.EqualTo(new[] { "#1 a.mp3 ended", "bgm b.ogg stopped" }));
            Assert.That(store.DrainEvents(), Is.Empty);
        });
    }

    [Test]
    public void Events_AreBounded_DroppingTheOldest()
    {
        var store = new AudioStore();
        for (var i = 0; i < 70; i++)
            store.EmitEvent(new SimEvent(i, "audio.finished", null, $"#{i} clip ended"));
        var drained = store.DrainEvents();
        Assert.Multiple(() =>
        {
            Assert.That(drained, Has.Count.EqualTo(64));
            Assert.That(drained[0].Detail, Is.EqualTo("#6 clip ended"), "the oldest were dropped");
        });
    }

    // ---- channel-status snapshot ---------------------------------------------------------------

    [Test]
    public void ChannelSnapshot_IsVolatileSwapped()
    {
        var store = new AudioStore();
        Assert.That(store.Channels, Is.Empty);
        var rows = new[]
        {
            new AudioChannelStatus("bgm", "music.ogg", false, 34100, 180000, 0.4, true, "music"),
        };
        store.PublishChannels(rows);
        Assert.That(store.Channels, Is.SameAs(rows));
    }
}
