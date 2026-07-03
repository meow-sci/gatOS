using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Tests.Commands;

namespace gatOS.SimFs.Tests.Audio;

/// <summary>
///     The <c>/sim/audio</c> surface walked over a live <see cref="NinePServer"/>
///     (GATOS_CUSTOM_AUDIO_PLAN): the writable <c>file/</c> clip directory end-to-end
///     (create → chunked writes → clunk-commit → read-back → rm), the
///     <c>play</c>/<c>set</c>/<c>stop</c> control files (a <see cref="FakeCommandSink"/> stands in
///     for the game thread), the <c>status</c>/<c>info</c> reads, the errno vocabulary on the
///     wire, and the presence/absence gating.
/// </summary>
[TestFixture]
public sealed class AudioTreeTests
{
    private SnapshotStore _store = null!;
    private AudioStore _audio = null!;
    private FakeCommandSink _sink = null!;
    private NinePServer _server = null!;
    private NinePTestClient _client = null!;
    private uint _nextFid;

    [SetUp]
    public async Task SetUp()
    {
        _store = new SnapshotStore();
        _audio = new AudioStore(maxClipBytes: 1024, maxTotalBytes: 4096, maxClips: 4, maxChannels: 2);
        _sink = new FakeCommandSink();
        _server = new NinePServer(SimFsTree.Build(_store, _sink, () => "9p 4242", audio: _audio));
        await _server.StartAsync();
        _client = await NinePTestClient.ConnectAsync(_server.Port);
        await _client.VersionAsync();
        await _client.AttachAsync(0);
        _nextFid = 1;
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    // ---- upload / read-back / delete (the writable file/ dir) --------------------------------

    [Test]
    public async Task Upload_ChunkedWrites_CommitOnClunk_AndReadBackMatches()
    {
        var payload = new byte[300];
        new Random(42).NextBytes(payload);

        var dirFid = await WalkAsync("audio", "file");
        await _client.LcreateAsync(dirFid, "alarm.mp3");
        // A `cat` arrives as many chunked writes at increasing offsets.
        await _client.WriteAsync(dirFid, 0, payload[..100]);
        await _client.WriteAsync(dirFid, 100, payload[100..250]);
        await _client.WriteAsync(dirFid, 250, payload[250..]);
        Assert.That(_audio.TryGet("alarm.mp3", out _), Is.EqualTo(AudioClipLookup.Uploading),
            "invisible to play until the clunk");
        await _client.ClunkAsync(dirFid);

        Assert.Multiple(() =>
        {
            Assert.That(_audio.TryGet("alarm.mp3", out var clip), Is.EqualTo(AudioClipLookup.Ready));
            Assert.That(clip!.Bytes, Is.EqualTo(payload));
        });

        // Read-back over 9p returns the exact stored bytes (md5sum both sides matches).
        var fid = await WalkAsync("audio", "file", "alarm.mp3");
        await _client.LopenAsync(fid);
        var size = (await _client.GetattrAsync(fid)).Size;
        var readBack = await _client.ReadToEndAsync(fid);
        await _client.ClunkAsync(fid);
        Assert.Multiple(() =>
        {
            Assert.That(size, Is.EqualTo(300));
            Assert.That(readBack, Is.EqualTo(payload));
        });
    }

    [Test]
    public async Task Listing_ShowsClipsWithSizes()
    {
        _audio.HttpUpload("b.ogg", 0, new byte[7], complete: true);
        _audio.HttpUpload("a.wav", 0, new byte[3], complete: true);

        var dirFid = await WalkAsync("audio", "file");
        await _client.LopenAsync(dirFid);
        var entries = await _client.ReaddirAllAsync(dirFid);
        Assert.That(entries.Select(e => e.Name).Where(n => n is not ("." or "..")),
            Is.EqualTo(new[] { "a.wav", "b.ogg" }), "name-sorted");
    }

    [Test]
    public async Task Reupload_WithTruncate_ReplacesBytes()
    {
        _audio.HttpUpload("clip.ogg", 0, "old-bytes"u8.ToArray(), complete: true);

        var fid = await WalkAsync("audio", "file", "clip.ogg");
        await _client.LopenAsync(fid, 0x201); // O_WRONLY | O_TRUNC — the plain `cat >` shape
        await _client.WriteAsync(fid, 0, "new"u8.ToArray());
        await _client.ClunkAsync(fid);

        Assert.Multiple(() =>
        {
            Assert.That(_audio.SnapshotBytes("clip.ogg"), Is.EqualTo("new"u8.ToArray()));
            _audio.TryGet("clip.ogg", out var clip);
            Assert.That(clip!.Version, Is.EqualTo(2), "the version bumps on every commit");
        });
    }

    [Test]
    public async Task Truncate2_OnUnopenedFid_Empties()
    {
        _audio.HttpUpload("clip.ogg", 0, new byte[9], complete: true);
        var fid = await WalkAsync("audio", "file", "clip.ogg");
        await _client.SetattrSizeAsync(fid, 0); // bare truncate(2), no open write handle
        Assert.That(_audio.SizeOf("clip.ogg"), Is.EqualTo(0));
    }

    [Test]
    public async Task Rm_Evicts_AndCreateExisting_IsEexist()
    {
        _audio.HttpUpload("clip.ogg", 0, new byte[1], complete: true);

        var dirFid = await WalkAsync("audio", "file");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.LcreateAsync(dirFid, "clip.ogg"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EEXIST));

        var rmFid = await WalkAsync("audio", "file");
        await _client.UnlinkatAsync(rmFid, "clip.ogg");
        Assert.That(_audio.Exists("clip.ogg"), Is.False);

        var missingFid = await WalkAsync("audio", "file");
        var enoent = Assert.ThrowsAsync<NinePErrorException>(() => _client.UnlinkatAsync(missingFid, "clip.ogg"));
        Assert.That(enoent!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public async Task Mkdir_IsEperm()
    {
        var dirFid = await WalkAsync("audio", "file");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.MkdirAsync(dirFid, "sub"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EPERM));
    }

    [Test]
    public async Task CapErrnos_ReachTheWire()
    {
        // Per-clip cap (1024 here): the write past it fails EFBIG mid-stream, not at clunk.
        var dirFid = await WalkAsync("audio", "file");
        await _client.LcreateAsync(dirFid, "big.mp3");
        await _client.WriteAsync(dirFid, 0, new byte[1024]);
        var efbig = Assert.ThrowsAsync<NinePErrorException>(() => _client.WriteAsync(dirFid, 1024, new byte[1]));
        Assert.That(efbig!.Errno, Is.EqualTo(LinuxErrno.EFBIG));
        await _client.ClunkAsync(dirFid);

        // Clip-count cap (4): the fifth create fails ENOSPC.
        for (var i = 1; i < 4; i++)
            _audio.HttpUpload($"clip{i}.ogg", 0, new byte[1], complete: true);
        var fullFid = await WalkAsync("audio", "file");
        var enospc = Assert.ThrowsAsync<NinePErrorException>(() => _client.LcreateAsync(fullFid, "five.ogg"));
        Assert.That(enospc!.Errno, Is.EqualTo(LinuxErrno.ENOSPC));
    }

    // ---- play / set / stop control files ------------------------------------------------------

    [Test]
    public async Task Play_SubmitsTheParsedCommand()
    {
        await WriteAsync("alarm.mp3 vol=0.8 end=1200\n", "audio", "play");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("audio.play"));
            Assert.That(c.Token, Is.EqualTo("alarm.mp3"));
            Assert.That(c.Values, Is.EqualTo(new[] { 0d, 1200, 0.8, 0, 0, 1, 0 }));
        });
    }

    [Test]
    public async Task Set_And_Stop_SubmitTheirCommands()
    {
        await WriteAsync("bgm vol=0.15\n", "audio", "set");
        Assert.That(_sink.Last!.Action, Is.EqualTo("audio.set"));

        await WriteAsync("all\n", "audio", "stop");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo("audio.stop"));
            Assert.That(_sink.Last!.Token, Is.EqualTo("all"));
        });
    }

    [TestCase("play")]
    [TestCase("set")]
    [TestCase("stop")]
    public void BadControlLine_IsEinval_AndDoesNotSubmit(string control)
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => WriteAsync("not a=valid line=\n", "audio", control));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    // ---- status / info -------------------------------------------------------------------------

    [Test]
    public async Task Status_RendersOneLinePerChannel()
    {
        _audio.PublishChannels(
        [
            new AudioChannelStatus("#3", "alarm.mp3", true, 200, 1200, 1, false, "sfx"),
            new AudioChannelStatus("bgm", "music.ogg", false, 34100, 180000, 0.4, true, "music"),
        ]);
        var status = await ReadAsync("audio", "status");
        Assert.That(status, Is.EqualTo("#3 alarm.mp3 paused 200 1200 1 0 sfx\n"
                                       + "bgm music.ogg playing 34100 180000 0.4 1 music\n"));
    }

    [Test]
    public async Task Info_RendersUsageAndCaps()
    {
        _audio.HttpUpload("a.ogg", 0, new byte[10], complete: true);
        _audio.PublishChannels([new AudioChannelStatus("#1", "a.ogg", false, 0, 500, 1, false, "sfx")]);
        var info = await ReadAsync("audio", "info");
        Assert.That(info, Is.EqualTo("enabled=1 clips=1 clips_max=4 bytes=10 bytes_max=4096 "
                                     + "clip_bytes_max=1024 channels=1 channels_max=2\n"));
    }

    // ---- gating + field-mirror exclusion -------------------------------------------------------

    [Test]
    public void NoStore_NoAudioDir()
    {
        var root = SimFsTree.Build(new SnapshotStore(), _sink, () => "");
        Assert.That(root.Lookup("audio"), Is.Null, "[audio] enabled=false removes the surface");
    }

    [Test]
    public void NoSink_KeepsFilesAndReads_DropsControls()
    {
        var root = SimFsTree.Build(new SnapshotStore(), null, null, audio: _audio);
        var audio = (VfsDirectory)root.Lookup("audio")!;
        Assert.Multiple(() =>
        {
            Assert.That(audio.Lookup("file"), Is.Not.Null);
            Assert.That(audio.Lookup("status"), Is.Not.Null);
            Assert.That(audio.Lookup("info"), Is.Not.Null);
            Assert.That(audio.Lookup("play"), Is.Null, "no sink ⇒ no way to actuate");
            Assert.That(audio.Lookup("stop"), Is.Null);
        });
    }

    [Test]
    public void ClipFiles_AreExcludedFromTheScalarFieldMirror()
    {
        _audio.HttpUpload("clip.ogg", 0, new byte[16], complete: true);
        var root = SimFsTree.Build(_store, _sink, () => "", audio: _audio);
        var leaves = VfsScan.Leaves(root).Select(l => l.Path).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(leaves, Does.Not.Contain("audio/file/clip.ogg"),
                "binary clips must never become MQTT topics / bulk-walk reads");
            Assert.That(leaves, Does.Contain("audio/play"));
            Assert.That(leaves, Does.Contain("audio/status"));
            Assert.That(leaves, Does.Contain("audio/info"));
        });
    }

    // ---- helpers (mirror ThugLifeTreeTests) ----------------------------------------------------

    private async Task<uint> WalkAsync(params string[] names)
    {
        var fid = _nextFid++;
        var qids = await _client.WalkAsync(0, fid, names);
        Assert.That(qids, Has.Length.EqualTo(names.Length), $"walk {string.Join('/', names)}");
        return fid;
    }

    private async Task<string> ReadAsync(params string[] names)
    {
        var fid = await WalkAsync(names);
        await _client.LopenAsync(fid);
        var content = Encoding.UTF8.GetString(await _client.ReadToEndAsync(fid));
        await _client.ClunkAsync(fid);
        return content;
    }

    private async Task WriteAsync(string text, params string[] names)
    {
        var fid = await WalkAsync(names);
        await _client.LopenAsync(fid, 1); // O_WRONLY
        await _client.WriteAsync(fid, 0, Encoding.UTF8.GetBytes(text));
        await _client.ClunkAsync(fid);
    }
}
