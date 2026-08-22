using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Paint;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Tests.Commands;

namespace gatOS.SimFs.Tests.Paint;

/// <summary>
///     The <c>/sim/paint/textures</c> surface walked over a live <see cref="NinePServer"/>
///     (GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN): the writable <c>file/</c> upload directory end-to-end
///     (create → chunked writes → clunk-commit → read-back → rm), the <c>bind</c>/<c>unbind</c>/
///     <c>clear</c> control files (a <see cref="FakeCommandSink"/> stands in for the game thread),
///     the <c>status</c>/<c>info</c>/<c>bindings</c>/<c>applied</c>/<c>clutter</c> reads, the errno
///     vocabulary on the wire, and the presence/absence gating.
/// </summary>
[TestFixture]
public sealed class TextureTreeTests
{
    private static readonly byte[] PngHeader = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private SnapshotStore _store = null!;
    private TextureStore _textures = null!;
    private FakeCommandSink _sink = null!;
    private NinePServer _server = null!;
    private NinePTestClient _client = null!;
    private uint _nextFid;

    private static byte[] Png(int extra = 8)
    {
        var bytes = new byte[PngHeader.Length + extra];
        PngHeader.CopyTo(bytes, 0);
        return bytes;
    }

    [SetUp]
    public async Task SetUp()
    {
        _store = new SnapshotStore();
        _textures = new TextureStore(maxFileBytes: 1024, maxTotalBytes: 4096, maxFiles: 4, maxBindings: 2);
        _sink = new FakeCommandSink();
        _server = new NinePServer(SimFsTree.Build(_store, _sink, () => "9p 4242", textures: _textures));
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

    // ---- upload / read-back / delete (the writable file/ dir) ----------------------------------

    [Test]
    public async Task Upload_ChunkedWrites_CommitOnClunk_AndReadBackMatches()
    {
        var bytes = Png(292);
        var dirFid = await WalkAsync("paint", "textures", "file");
        await _client.LcreateAsync(dirFid, "rock.png");
        await _client.WriteAsync(dirFid, 0, bytes.AsMemory(0, 100).ToArray());
        await _client.WriteAsync(dirFid, 100, bytes.AsMemory(100, 100).ToArray());
        await _client.WriteAsync(dirFid, 200, bytes.AsMemory(200).ToArray());

        Assert.That(_textures.TryGet("rock.png", out _), Is.EqualTo(TextureLookup.Uploading),
            "invisible to bind until the clunk");
        await _client.ClunkAsync(dirFid);

        Assert.Multiple(() =>
        {
            Assert.That(_textures.TryGet("rock.png", out var file), Is.EqualTo(TextureLookup.Ready));
            Assert.That(file!.Kind, Is.EqualTo(TextureImageKind.Png), "the container is sniffed at commit");
        });

        var readFid = await WalkAsync("paint", "textures", "file", "rock.png");
        var attr = await _client.GetattrAsync(readFid);
        Assert.That(attr.Size, Is.EqualTo((ulong)bytes.Length));
        await _client.LopenAsync(readFid);
        Assert.That(await _client.ReadToEndAsync(readFid), Is.EqualTo(bytes));
        await _client.ClunkAsync(readFid);
    }

    [Test]
    public async Task Listing_ShowsUploadsWithSizes()
    {
        _textures.HttpUpload("b.png", 0, Png(), complete: true);
        _textures.HttpUpload("a.png", 0, Png(16), complete: true);
        var dirFid = await WalkAsync("paint", "textures", "file");
        await _client.LopenAsync(dirFid);
        var names = (await _client.ReaddirAllAsync(dirFid))
            .Select(e => e.Name).Where(n => n is not ("." or "..")).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "a.png", "b.png" }), "name-sorted");
    }

    [Test]
    public async Task Rm_Evicts_AndUnbindsFirst()
    {
        _textures.HttpUpload("rock.png", 0, Png(), complete: true);
        _textures.Bind("Stock/A", "rock.png");

        var dirFid = await WalkAsync("paint", "textures", "file");
        await _client.UnlinkatAsync(dirFid, "rock.png");
        Assert.Multiple(() =>
        {
            Assert.That(_textures.Exists("rock.png"), Is.False);
            Assert.That(_textures.Bindings, Is.Empty, "rm can never orphan a live override");
        });

        var enoent = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.UnlinkatAsync(dirFid, "rock.png"));
        Assert.That(enoent!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public async Task Mkdir_IsEperm()
    {
        var dirFid = await WalkAsync("paint", "textures", "file");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.MkdirAsync(dirFid, "sub"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EPERM));
    }

    [Test]
    public async Task CapErrnos_ReachTheWire()
    {
        // Per-file cap (1024 here): the write past it fails EFBIG mid-stream, not at clunk.
        var dirFid = await WalkAsync("paint", "textures", "file");
        await _client.LcreateAsync(dirFid, "big.png");
        await _client.WriteAsync(dirFid, 0, new byte[1024]);
        var efbig = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WriteAsync(dirFid, 1024, new byte[1]));
        Assert.That(efbig!.Errno, Is.EqualTo(LinuxErrno.EFBIG));
        await _client.ClunkAsync(dirFid);

        // File-count cap (4): the fifth create fails ENOSPC.
        for (var i = 1; i < 4; i++)
            _textures.HttpUpload($"f{i}.png", 0, Png(), complete: true);
        var fullFid = await WalkAsync("paint", "textures", "file");
        var enospc = Assert.ThrowsAsync<NinePErrorException>(() => _client.LcreateAsync(fullFid, "five.png"));
        Assert.That(enospc!.Errno, Is.EqualTo(LinuxErrno.ENOSPC));
    }

    // ---- bind / unbind / clear ------------------------------------------------------------------

    [Test]
    public async Task Bind_SubmitsTheParsedCommand()
    {
        await WriteAsync("Core/Rock_diffuse.png rock.png", "paint", "textures", "bind");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo(SimActions.PaintTextureBind));
            Assert.That(_sink.Last!.Token, Is.EqualTo("Core/Rock_diffuse.png"));
            Assert.That(_sink.Last!.Aux, Is.EqualTo("rock.png"));
        });
    }

    [Test]
    public async Task Unbind_SubmitsTheParsedCommand()
    {
        await WriteAsync("Core/Rock_diffuse.png", "paint", "textures", "unbind");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo(SimActions.PaintTextureUnbind));
            Assert.That(_sink.Last!.Token, Is.EqualTo("Core/Rock_diffuse.png"));
        });
    }

    [Test]
    public async Task UnbindAll_NormalizesToTheSameTeardownActionAsClear()
    {
        await WriteAsync("all", "paint", "textures", "unbind");
        var viaUnbind = _sink.Last!;
        await WriteAsync("1", "paint", "textures", "clear");
        Assert.That(viaUnbind.Action, Is.EqualTo(SimActions.PaintTextureClear));
        Assert.That(_sink.Last!.Action, Is.EqualTo(SimActions.PaintTextureClear),
            "the two spellings of the global teardown cannot drift");
    }

    [TestCase("bind", "only-one-token")]
    [TestCase("bind", "target a b")]
    [TestCase("bind", "target bad name.png x")]
    [TestCase("unbind", "a b")]
    [TestCase("unbind", "")]
    public async Task BadControlLine_IsEinval_AndDoesNotSubmit(string control, string line)
    {
        var submits = _sink.Submits;
        var fid = await WalkAsync("paint", "textures", control);
        await _client.LopenAsync(fid, 1);
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WriteAsync(fid, 0, Encoding.UTF8.GetBytes(line + "\n")));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(submits));
        });
    }

    // ---- reads -----------------------------------------------------------------------------------

    [Test]
    public async Task Bindings_RowsAreValidBindLines()
    {
        _textures.HttpUpload("rock.png", 0, Png(), complete: true);
        _textures.Bind("Core/Rock_diffuse.png", "rock.png");

        var row = (await ReadAsync("paint", "textures", "bindings")).TrimEnd('\n');
        Assert.That(row, Is.EqualTo("Core/Rock_diffuse.png rock.png faithful"));

        // The symmetry contract: a bindings row parses as a bind line.
        var parsed = TextureCommands.ParseBind(row);
        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.Token, Is.EqualTo("Core/Rock_diffuse.png"));
    }

    [Test]
    public async Task Applied_RendersOneRowPerPublishedStatus()
    {
        _textures.PublishApplied([
            new TextureBindStatus("Core/Rock.png", "rock.png", TextureBindState.Applied, 512, 512, 10, 1024, ""),
            new TextureBindStatus("Core/Tree.png", "bad.png", TextureBindState.Failed, 0, 0, 0, 0, "decode failed"),
        ]);
        Assert.That(await ReadAsync("paint", "textures", "applied"), Is.EqualTo(
            "Core/Rock.png rock.png applied 512 512 10 1024 -\n"
            + "Core/Tree.png bad.png failed 0 0 0 0 decode_failed\n"));
    }

    [Test]
    public async Task Clutter_RendersThePublishedCatalog()
    {
        _textures.PublishCatalog([
            new ClutterTextureInfo("Core/Rock.png", "diffuse", 1024, 1024, 11, 2, ["Rocks", "Scree"]),
        ]);
        Assert.That(await ReadAsync("paint", "textures", "clutter"),
            Is.EqualTo("Core/Rock.png diffuse 1024 1024 11 2 Rocks,Scree\n"));
    }

    [Test]
    public async Task StatusAndInfo_RenderTheRuntimeAndCaps()
    {
        _textures.HttpUpload("rock.png", 0, Png(), complete: true);
        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAsync("paint", "textures", "status"), Does.StartWith(
                "available=0 bound=0 applied=0 catalog=0 retiring=0 vram_bytes=0 revision=0 error="));
            Assert.That(await ReadAsync("paint", "textures", "info"), Is.EqualTo(
                "files=1 files_max=4 bytes=16 bytes_max=4096 file_bytes_max=1024 "
                + "bindings_max=2 max_dimension=4096\n"));
        });
    }

    // ---- gating ------------------------------------------------------------------------------------

    [Test]
    public async Task NoStore_NoTexturesDir()
    {
        await using var server = new NinePServer(SimFsTree.Build(_store, _sink, () => "t"));
        await server.StartAsync();
        await using var client = await NinePTestClient.ConnectAsync(server.Port);
        await client.VersionAsync();
        await client.AttachAsync(0);
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => client.WalkAsync(0, 9, ["paint", "textures"]));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public async Task NoSink_KeepsFilesAndReads_DropsControls()
    {
        await using var server = new NinePServer(
            SimFsTree.Build(_store, null, () => "t", textures: _textures));
        await server.StartAsync();
        await using var client = await NinePTestClient.ConnectAsync(server.Port);
        await client.VersionAsync();
        await client.AttachAsync(0);

        Assert.That(await client.WalkAsync(0, 11, ["paint", "textures", "file"]), Has.Length.EqualTo(3));
        Assert.That(await client.WalkAsync(0, 12, ["paint", "textures", "status"]), Has.Length.EqualTo(3));
        // A walk that cannot reach the last element returns a short qid list rather than an error.
        Assert.That(await client.WalkAsync(0, 13, ["paint", "textures", "bind"]), Has.Length.EqualTo(2),
            "the control files are absent without a command sink");
    }

    [Test]
    public void ImageFiles_AreExcludedFromTheScalarFieldMirror()
    {
        _textures.HttpUpload("rock.png", 0, Png(), complete: true);
        var root = SimFsTree.Build(_store, _sink, () => "t", textures: _textures);
        var leaves = VfsScan.Leaves(root).Select(l => l.Path).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(leaves, Has.None.EqualTo("paint/textures/file/rock.png"),
                "binary images must never become MQTT topics or bulk-walk reads");
            Assert.That(leaves, Does.Contain("paint/textures/status"));
        });
    }

    // ---- helpers (mirror AudioTreeTests) --------------------------------------------------------

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
        await _client.WriteAsync(fid, 0, Encoding.UTF8.GetBytes(text + "\n"));
        await _client.ClunkAsync(fid);
    }
}
