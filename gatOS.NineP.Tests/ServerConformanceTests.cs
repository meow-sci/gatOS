using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;

namespace gatOS.NineP.Tests;

/// <summary>
///     The M7 conformance suite (OS_PLAN.md T7.4) — the managed client against a live
///     <see cref="NinePServer"/> over loopback TCP. Wire semantics asserted here are the ones
///     the M1 spike proved against the real v9fs client (spike/NOTES.md T1.2).
/// </summary>
[TestFixture]
public sealed class ServerConformanceTests
{
    private NinePServer _server = null!;
    private TestTree.GateFile _gate = null!;
    private TestTree.CaptureControlFile _ctl = null!;
    private Func<int> _ticksRead = null!;
    private NinePTestClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        var (root, gate, ticksRead, ctl) = TestTree.Build();
        _gate = gate;
        _ctl = ctl;
        _ticksRead = ticksRead;
        _server = new NinePServer(root);
        await _server.StartAsync();
        _client = await NinePTestClient.ConnectAsync(_server.Port);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    private async Task<uint> AttachedFidAsync(uint fid = 0)
    {
        await _client.VersionAsync();
        await _client.AttachAsync(fid);
        return fid;
    }

    // ---- version ---------------------------------------------------------------------------

    [Test]
    public async Task Version_ClampsMsize_AndEchoesDialect()
    {
        var (msize, version) = await _client.VersionAsync(1 << 20);
        Assert.Multiple(() =>
        {
            Assert.That(msize, Is.EqualTo(131072), "server ceiling");
            Assert.That(version, Is.EqualTo("9P2000.L"));
        });

        (msize, version) = await _client.VersionAsync(65536);
        Assert.Multiple(() =>
        {
            Assert.That(msize, Is.EqualTo(65536), "client minimum wins");
            Assert.That(version, Is.EqualTo("9P2000.L"));
        });
    }

    [Test]
    public async Task Version_UnknownDialect_AnswersUnknown()
    {
        var (_, version) = await _client.VersionAsync(65536, "9P2000.u");
        Assert.That(version, Is.EqualTo("unknown"));
    }

    // ---- attach / walk ---------------------------------------------------------------------

    [Test]
    public async Task Attach_ReturnsRootDirectoryQid()
    {
        await _client.VersionAsync();
        var qid = await _client.AttachAsync(0);
        Assert.Multiple(() =>
        {
            Assert.That(qid.Type, Is.EqualTo(QidType.Directory));
            Assert.That(qid.Path, Is.EqualTo(1000UL), "fixture root qid");
        });
    }

    [Test]
    public async Task Walk_FullPath_ReturnsAllQids_AndBindsNewfid()
    {
        var root = await AttachedFidAsync();
        var qids = await _client.WalkAsync(root, 1, "sub", "a");
        Assert.Multiple(() =>
        {
            Assert.That(qids, Has.Length.EqualTo(2));
            Assert.That(qids[0].Type, Is.EqualTo(QidType.Directory));
            Assert.That(qids[1].Type, Is.EqualTo(QidType.File));
        });

        await _client.LopenAsync(1);
        var content = await _client.ReadToEndAsync(1);
        Assert.That(Encoding.UTF8.GetString(content), Is.EqualTo("alpha\n"));
    }

    [Test]
    public async Task Walk_ZeroNames_ClonesTheFid()
    {
        var root = await AttachedFidAsync();
        var qids = await _client.WalkAsync(root, 1);
        Assert.That(qids, Is.Empty);
        // The clone is bound and usable.
        await _client.LopenAsync(1);
        Assert.That(await _client.ReaddirAllAsync(1), Is.Not.Empty);
    }

    [Test]
    public async Task Walk_PartialSuccess_ReturnsResolvedQids_AndDoesNotBindNewfid()
    {
        var root = await AttachedFidAsync();
        var qids = await _client.WalkAsync(root, 1, "sub", "nope", "deeper");
        Assert.That(qids, Has.Length.EqualTo(1), "only 'sub' resolved");

        // newfid must not be bound after a partial walk.
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.GetattrAsync(1));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EBADF));
    }

    [Test]
    public async Task Walk_FirstNameMissing_IsENOENT()
    {
        var root = await AttachedFidAsync();
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.WalkAsync(root, 1, "nope"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public async Task Walk_ThroughFile_IsENOTDIR()
    {
        var root = await AttachedFidAsync();
        // Walking *to* the file is fine; walking *through* it is not.
        var qids = await _client.WalkAsync(root, 2, "hello");
        Assert.That(qids, Has.Length.EqualTo(1));
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.WalkAsync(2, 3, "x"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOTDIR));
    }

    [Test]
    public async Task Walk_DotDotAtRoot_StaysAtRoot()
    {
        var root = await AttachedFidAsync();
        var qids = await _client.WalkAsync(root, 1, "..", "..", "sub");
        Assert.Multiple(() =>
        {
            Assert.That(qids, Has.Length.EqualTo(3));
            Assert.That(qids[0].Path, Is.EqualTo(1000UL), ".. at root is root");
            Assert.That(qids[1].Path, Is.EqualTo(1000UL));
            Assert.That(qids[2].Type, Is.EqualTo(QidType.Directory));
        });
    }

    [Test]
    public async Task Walk_LookupThrowsMidPath_IsPartialSuccess()
    {
        var root = await AttachedFidAsync();
        var qids = await _client.WalkAsync(root, 1, "denied", "x");
        Assert.That(qids, Has.Length.EqualTo(1), "'denied' resolved; its throwing lookup stops the walk");
    }

    [Test]
    public async Task Walk_TooManyNames_IsError()
    {
        var root = await AttachedFidAsync();
        var names = Enumerable.Repeat("..", 17).ToArray();
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.WalkAsync(root, 1, names));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    // ---- open / read -----------------------------------------------------------------------

    [Test]
    public async Task Lopen_WriteFlags_AreEACCES()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "hello");
        foreach (var flags in new uint[] { 1, 2 }) // O_WRONLY, O_RDWR
        {
            var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.LopenAsync(1, flags));
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EACCES));
        }

        // Still openable read-only afterwards (failed opens must not poison the fid).
        await _client.LopenAsync(1);
    }

    [Test]
    public async Task Lopen_OpenThrowsVfsError_PropagatesErrno()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "vanishing");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.LopenAsync(1));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public async Task Read_ServesByOffset_AndZeroBytesAtEof()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "hello");
        await _client.LopenAsync(1);
        Assert.That(Encoding.UTF8.GetString(await _client.ReadAsync(1, 0, 5)), Is.EqualTo("hello"));
        Assert.That(Encoding.UTF8.GetString(await _client.ReadAsync(1, 6, 100)), Is.EqualTo("world\n"));
        Assert.That(await _client.ReadAsync(1, 12, 100), Is.Empty, "EOF is a 0-byte Rread");
        Assert.That(await _client.ReadAsync(1, 9999, 100), Is.Empty, "past EOF too");
    }

    [Test]
    public async Task Read_UnopenedFid_IsEBADF_AndDirectory_IsEISDIR()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "hello");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.ReadAsync(1, 0, 10));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EBADF));

        await _client.WalkAsync(root, 2, "sub");
        await _client.LopenAsync(2);
        ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.ReadAsync(2, 0, 10));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EISDIR));
    }

    [Test]
    public async Task Read_CountIsClampedToMsize()
    {
        await _client.VersionAsync(8192);
        await _client.AttachAsync(0);
        // 'huge' is 100000 bytes; with msize 8192 one Rread must serve at most msize - 11.
        await _client.WalkAsync(0, 1, "huge");
        await _client.LopenAsync(1);
        var data = await _client.ReadAsync(1, 0, 1 << 20);
        Assert.Multiple(() =>
        {
            Assert.That(data, Has.Length.LessThanOrEqualTo(8192 - 11));
            Assert.That(data, Is.Not.Empty);
        });
    }

    [Test]
    public async Task OpenSnapshot_IsStablePerOpen_AndLivePerOpen()
    {
        var root = await AttachedFidAsync();

        await _client.WalkAsync(root, 1, "ticks");
        await _client.LopenAsync(1);
        var first = Encoding.UTF8.GetString(await _client.ReadToEndAsync(1));

        await _client.WalkAsync(root, 2, "ticks");
        await _client.LopenAsync(2);
        var second = Encoding.UTF8.GetString(await _client.ReadToEndAsync(2));

        Assert.That(second, Is.Not.EqualTo(first), "each open snapshots a live value");
        // Re-reading fid 1 at offset 0 re-serves its snapshot, not a new value.
        Assert.That(Encoding.UTF8.GetString(await _client.ReadAsync(1, 0, 100)), Is.EqualTo(first));
        Assert.That(_ticksRead(), Is.EqualTo(2), "exactly one provider call per open");
    }

    // ---- getattr / readdir / statfs ----------------------------------------------------------

    [Test]
    public async Task Getattr_File_ReportsTruthfulSizeAndMode()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "hello");
        var attrs = await _client.GetattrAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(attrs.Valid, Is.EqualTo(0x7FFUL));
            Assert.That(attrs.Size, Is.EqualTo((ulong)"hello world\n".Length), "truthful i_size (spike rule 1)");
            Assert.That(attrs.Mode, Is.EqualTo(0x8000u | 0x124), "S_IFREG | 0444");
            Assert.That(attrs.Qid.Type, Is.EqualTo(QidType.File));
            Assert.That(attrs.Nlink, Is.EqualTo(1UL));
            Assert.That(attrs.Blksize, Is.EqualTo(4096UL));
        });

        await _client.WalkAsync(root, 2, "sub");
        var dirAttrs = await _client.GetattrAsync(2);
        Assert.Multiple(() =>
        {
            Assert.That(dirAttrs.Mode, Is.EqualTo(0x4000u | 0x1ED), "S_IFDIR | 0755");
            Assert.That(dirAttrs.Size, Is.Zero);
        });
    }

    [Test]
    public async Task Getattr_OpenFid_ReportsTheOpenSnapshotSize()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "ticks");
        await _client.LopenAsync(1);
        var content = await _client.ReadToEndAsync(1);
        var attrs = await _client.GetattrAsync(1);
        Assert.That(attrs.Size, Is.EqualTo((ulong)content.Length),
            "an opened fid stats its own snapshot, even if the live value changed");
    }

    [Test]
    public async Task Readdir_IncludesDotAndDotDot_WithOrdinalCookies()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "sub");
        await _client.LopenAsync(1);
        var entries = await _client.ReaddirAllAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { ".", "..", "a", "b" }),
                "spike T1.2: . and .. are included");
            Assert.That(entries.Select(e => e.Cookie), Is.EqualTo(new ulong[] { 1, 2, 3, 4 }),
                "cookie = ordinal of the next entry");
            Assert.That(entries[0].Type, Is.EqualTo((byte)4), "DT_DIR");
            Assert.That(entries[2].Type, Is.EqualTo((byte)8), "DT_REG");
            Assert.That(entries[1].Qid.Path, Is.EqualTo(1000UL), ".. of a root child is the root");
        });
    }

    [Test]
    public async Task Readdir_PagesLargeDirectories_WithResumeFromCookie()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "big");
        await _client.LopenAsync(1);

        // A small count budget forces many pages.
        var all = new List<NinePTestClient.DirEntry>();
        ulong cookie = 0;
        var pages = 0;
        while (true)
        {
            var page = await _client.ReaddirAsync(1, cookie, 512);
            if (page.Length == 0)
                break;
            pages++;
            all.AddRange(page);
            cookie = page[^1].Cookie;
        }

        Assert.Multiple(() =>
        {
            Assert.That(pages, Is.GreaterThan(1), "the budget must actually page");
            Assert.That(all, Has.Count.EqualTo(202), "200 files + . + ..");
            Assert.That(all.Select(e => e.Name).Skip(2),
                Is.EqualTo(Enumerable.Range(0, 200).Select(i => $"file-{i:D3}")), "stable order");
        });
    }

    [Test]
    public async Task Readdir_UnopenedFid_IsEBADF()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "sub");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.ReaddirAsync(1, 0));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EBADF));
    }

    [Test]
    public async Task Statfs_ReportsV9fsMagic()
    {
        var root = await AttachedFidAsync();
        Assert.That(await _client.StatfsTypeAsync(root), Is.EqualTo(0x01021997u));
    }

    // ---- write / unsupported ------------------------------------------------------------------

    [Test]
    public async Task Write_ToReadOnlyOpenedFid_IsEBADF()
    {
        // A read-only file can't be opened for writing (Lopen_WriteFlags_AreEACCES covers that),
        // so a Twrite only ever lands on a fid opened O_RDONLY — which has no write handle: EBADF.
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "hello");
        await _client.LopenAsync(1);
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WriteAsync(1, 0, [1, 2, 3]));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EBADF));
    }

    [Test]
    public async Task WritableFile_Getattr_Has0644Mode()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "ctl");
        await _client.WalkAsync(root, 2, "hello");
        var ctl = await _client.GetattrAsync(1);
        var hello = await _client.GetattrAsync(2);
        Assert.Multiple(() =>
        {
            Assert.That(ctl.Mode, Is.EqualTo(0x8000u | 0x1A4u), "writable control file is S_IFREG | 0644");
            Assert.That(hello.Mode, Is.EqualTo(0x8000u | 0x124u), "read-only file stays S_IFREG | 0444");
        });
    }

    [Test]
    public async Task WritableFile_Wronly_Open_AndWrite_CapturesValue()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "ctl");
        await _client.LopenAsync(1, 1); // O_WRONLY
        var written = await _client.WriteAsync(1, 0, "1\n"u8.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(2u), "Rwrite reports the full byte count");
            Assert.That(_ctl.LastWrite, Is.EqualTo("1"), "the line before the LF actuated");
        });
    }

    [Test]
    public async Task WritableFile_RejectedWrite_ReturnsChosenErrno()
    {
        _ctl.RejectErrno = LinuxErrno.EINVAL;
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "ctl");
        await _client.LopenAsync(1, 1); // O_WRONLY
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WriteAsync(1, 0, "bogus\n"u8.ToArray()));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public async Task WritableFile_SetattrTruncate_Succeeds()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "ctl");
        // O_TRUNC drives a size-only Tsetattr; the handler only needs the fid.
        var response = await _client.SendRawAsync(MessageType.Tsetattr, w => w.WriteUInt32(1));
        Assert.That(response.Type, Is.EqualTo(MessageType.Rsetattr));
    }

    [Test]
    public async Task ReadOnlyFile_SetattrTruncate_IsEOPNOTSUPP()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "hello");
        var response = await _client.SendRawAsync(MessageType.Tsetattr, w => w.WriteUInt32(1));
        Assert.That(response.Type, Is.EqualTo(MessageType.Rlerror));
        Assert.That(new NinePReader(response.Body).ReadUInt32(), Is.EqualTo(LinuxErrno.EOPNOTSUPP));
    }

    [Test]
    public async Task WritableFile_Fsync_Succeeds()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "ctl");
        await _client.LopenAsync(1, 1);
        var response = await _client.SendRawAsync(MessageType.Tfsync, w => w.WriteUInt32(1).WriteUInt32(0));
        Assert.That(response.Type, Is.EqualTo(MessageType.Rfsync));
    }

    [Test]
    public async Task UnsupportedMessages_AreEOPNOTSUPP()
    {
        var root = await AttachedFidAsync();
        // Txattrwalk: the kernel probes this; a clean error is the expected answer.
        var response = await _client.SendRawAsync(MessageType.Txattrwalk,
            w => w.WriteUInt32(root).WriteUInt32(99).WriteString("security.selinux"));
        Assert.That(response.Type, Is.EqualTo(MessageType.Rlerror));
        Assert.That(new NinePReader(response.Body).ReadUInt32(), Is.EqualTo(LinuxErrno.EOPNOTSUPP));

        // A T-type we never implement.
        response = await _client.SendRawAsync(MessageType.Tsymlink,
            w => w.WriteUInt32(root).WriteString("x").WriteString("y").WriteUInt32(0));
        Assert.That(response.Type, Is.EqualTo(MessageType.Rlerror));
        Assert.That(new NinePReader(response.Body).ReadUInt32(), Is.EqualTo(LinuxErrno.EOPNOTSUPP));
    }

    // ---- clunk ---------------------------------------------------------------------------------

    [Test]
    public async Task Clunk_ReleasesTheFid_AndAlwaysSucceeds()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "hello");
        await _client.LopenAsync(1);
        await _client.ClunkAsync(1);

        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.ReadAsync(1, 0, 10));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EBADF));

        // Clunking an unknown fid still answers Rclunk (T7.4 table).
        await _client.ClunkAsync(424242);
    }

    // ---- flush / blocking reads -----------------------------------------------------------------

    [Test]
    public async Task BlockedRead_FlushCancels_RflushSent_NoReplyToOldTag()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "gate");
        await _client.LopenAsync(1);

        var (tag, data) = _client.BeginRead(1, 0, 4096);
        await WaitUntilAsync(() => _gate.ParkedReads > 0, "read should park on the gate");

        await _client.FlushAsync(tag);

        Assert.Multiple(() =>
        {
            Assert.That(data.IsCompleted, Is.False, "the flushed tag must never be answered");
            Assert.That(_client.ResponseLog.Select(r => r.Type), Does.Not.Contain(MessageType.Rread),
                "no Rread may precede or follow the Rflush for a canceled read");
        });

        // The session is fully alive afterwards.
        await _client.WalkAsync(root, 2, "hello");
        await _client.LopenAsync(2);
        Assert.That(Encoding.UTF8.GetString(await _client.ReadToEndAsync(2)), Is.EqualTo("hello world\n"));
    }

    [Test]
    public async Task BlockedRead_CompletesWhenSignaled()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "gate");
        await _client.LopenAsync(1);

        var (_, data) = _client.BeginRead(1, 0, 4096);
        await WaitUntilAsync(() => _gate.ParkedReads > 0, "read should park on the gate");
        _gate.Signal("event!\n");
        Assert.That(Encoding.UTF8.GetString(await data.WaitAsync(TimeSpan.FromSeconds(10))),
            Is.EqualTo("event!\n"));
    }

    [Test]
    public async Task Flush_UnknownOldTag_AnswersRflushImmediately()
    {
        await _client.VersionAsync();
        await _client.FlushAsync(12345);
    }

    [Test]
    public async Task BlockedRead_DoesNotStallOtherFids()
    {
        var root = await AttachedFidAsync();
        await _client.WalkAsync(root, 1, "gate");
        await _client.LopenAsync(1);
        var (tag, _) = _client.BeginRead(1, 0, 4096);
        await WaitUntilAsync(() => _gate.ParkedReads > 0, "read should park on the gate");

        // Pipelining: a second fid is fully serviceable while the first read is parked.
        await _client.WalkAsync(root, 2, "sub", "b");
        await _client.LopenAsync(2);
        Assert.That(Encoding.UTF8.GetString(await _client.ReadToEndAsync(2)), Is.EqualTo("beta\n"));

        await _client.FlushAsync(tag);
    }

    // ---- robustness ---------------------------------------------------------------------------

    [Test]
    public async Task MalformedFrame_ClosesTheConnection_ServerSurvives()
    {
        await _client.VersionAsync();
        // size=3 is below the 7-byte header minimum.
        await _client.SendRawBytesAsync([0x03, 0x00, 0x00, 0x00]);
        await _client.ConnectionClosed.WaitAsync(TimeSpan.FromSeconds(10));

        // The listener is unharmed: a fresh connection works end to end.
        await using var fresh = await NinePTestClient.ConnectAsync(_server.Port);
        await fresh.VersionAsync();
        await fresh.AttachAsync(0);
        await fresh.WalkAsync(0, 1, "hello");
        await fresh.LopenAsync(1);
        Assert.That(Encoding.UTF8.GetString(await fresh.ReadToEndAsync(1)), Is.EqualTo("hello world\n"));
    }

    [Test]
    public async Task TruncatedBody_ClosesTheConnection()
    {
        await _client.VersionAsync();
        // A Tattach whose body is one lonely byte.
        await _client.SendRawBytesAsync([0x08, 0x00, 0x00, 0x00, 104, 0x01, 0x00, 0xAA]);
        await _client.ConnectionClosed.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task TwoClientConnections_AreIndependent()
    {
        await AttachedFidAsync();
        await using var other = await NinePTestClient.ConnectAsync(_server.Port);
        await other.VersionAsync();
        await other.AttachAsync(0); // same fid number, different connection — no clash
        await other.WalkAsync(0, 1, "sub", "a");
        await other.LopenAsync(1);
        Assert.That(Encoding.UTF8.GetString(await other.ReadToEndAsync(1)), Is.EqualTo("alpha\n"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting: {what}");
            await Task.Delay(10);
        }
    }
}
