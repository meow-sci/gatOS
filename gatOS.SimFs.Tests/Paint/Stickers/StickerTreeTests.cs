using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Paint;
using gatOS.SimFs.Paint.Stickers;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Tests.Commands;

namespace gatOS.SimFs.Tests.Paint.Stickers;

/// <summary>
///     The <c>/sim/paint/stickers</c> surface walked over a live <see cref="NinePServer"/>
///     (STICKERS_PLAN §3.7): the <c>place</c>/<c>spray</c> grammars and the per-sticker controls
///     (a <see cref="FakeCommandSink"/> stands in for the game thread), the
///     <c>info</c>/<c>status</c>/<c>count</c>/<c>last</c> reads off a published store, the
///     <c>spec</c> round trip, the ENOENT/EINVAL errno vocabulary on the wire, and the
///     presence/absence gating.
/// </summary>
[TestFixture]
public sealed class StickerTreeTests
{
    private SnapshotStore _store = null!;
    private TextureStore _textures = null!;
    private StickerStore _stickers = null!;
    private FakeCommandSink _sink = null!;
    private NinePServer _server = null!;
    private NinePTestClient _client = null!;
    private uint _nextFid;

    [SetUp]
    public async Task SetUp()
    {
        _store = new SnapshotStore();
        _textures = new TextureStore();
        _stickers = new StickerStore(maxCount: 64, maxViewDistanceMetres: 1500);
        _sink = new FakeCommandSink { DebugEnabled = true };
        _server = new NinePServer(SimFsTree.Build(_store, _sink, () => "9p 4242",
            textures: _textures, stickers: _stickers));
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

    private static StickerSnapshot Vessel(int id) => new(
        id, "meow.png", StickerAnchorKind.Vessel, "Kitten-1", 1187,
        new double3Snap(0, 0.5, -1.4), new double3Snap(0, 1, 0),
        15, 0.6, 0.3, 0.05, 0.5, 2, true, true, StickerTextureState.Ready);

    private static StickerSnapshot Body(int id) => new(
        id, "logo.png", StickerAnchorKind.Body, "Mun", 0,
        new double3Snap(12.03, -41.88, 0), default,
        90, 5, 5, 1, 1, 1, false, false, StickerTextureState.Missing);

    private void Publish(params StickerSnapshot[] stickers)
        => _stickers.Publish(stickers,
            new StickerRuntime(true, stickers.Length, 1, 1, 4096, true, "active", ""));

    // ---- gating ------------------------------------------------------------------------------

    [Test]
    public async Task NoStickerStore_NoStickersDir()
    {
        await using var server = new NinePServer(
            SimFsTree.Build(_store, _sink, () => "t", textures: _textures));
        await server.StartAsync();
        await using var client = await NinePTestClient.ConnectAsync(server.Port);
        await client.VersionAsync();
        await client.AttachAsync(0);
        // paint/ still exists (the texture store is wired), so the walk stops short of stickers
        // rather than erroring — the 9p spelling of "that name is not there".
        Assert.That(await client.WalkAsync(0, 9, ["paint", "stickers"]), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task NoTextureStore_NoStickersDir()
    {
        // Stickers draw uploaded images: without the image store the surface would be a dead end.
        await using var server = new NinePServer(
            SimFsTree.Build(_store, _sink, () => "t", stickers: _stickers));
        await server.StartAsync();
        await using var client = await NinePTestClient.ConnectAsync(server.Port);
        await client.VersionAsync();
        await client.AttachAsync(0);
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => client.WalkAsync(0, 9, ["paint", "stickers"]));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public async Task NoSink_KeepsReads_DropsControls()
    {
        Publish(Vessel(0));
        await using var server = new NinePServer(SimFsTree.Build(_store, null, () => "t",
            textures: _textures, stickers: _stickers));
        await server.StartAsync();
        await using var client = await NinePTestClient.ConnectAsync(server.Port);
        await client.VersionAsync();
        await client.AttachAsync(0);

        Assert.Multiple(async () =>
        {
            Assert.That(await client.WalkAsync(0, 11, ["paint", "stickers", "status"]), Has.Length.EqualTo(3));
            Assert.That(await client.WalkAsync(0, 12, ["paint", "stickers", "0", "spec"]), Has.Length.EqualTo(4));
            Assert.That(await client.WalkAsync(0, 13, ["paint", "stickers", "place"]), Has.Length.EqualTo(2),
                "the control files are absent without a command sink");
            Assert.That(await client.WalkAsync(0, 14, ["paint", "stickers", "0", "remove"]), Has.Length.EqualTo(3),
                "so is the per-sticker remove trigger");
        });
    }

    // ---- reads -------------------------------------------------------------------------------

    [Test]
    public async Task Help_ReadsConsoleReadme()
    {
        var help = await ReadAsync("paint", "stickers", "help");
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Contain("> spray"), "documents the aimed create command");
            Assert.That(help, Does.Contain("> place"), "documents the exact create command");
            Assert.That(help, Does.Contain("aim=camera"), "documents how the spray is aimed");
            Assert.That(help, Does.Contain("aim=cursor"));
            Assert.That(help, Does.Contain("/sim/camera"), "points at the feature that can aim it");
            Assert.That(help, Does.Contain("/sim/paint/textures/file/"), "points at the image store");
            Assert.That(help, Does.Contain("brightness"), "documents every tunable");
            Assert.That(help, Does.Contain("metres"), "states the size/depth unit");
            Assert.That(help, Does.Contain("degrees"), "states the rotation unit");
            Assert.That(help, Does.Contain("spec"), "documents the round-trippable read-back");
            Assert.That(help, Does.Contain("clutter"), "explains that clutter is hit by the projection");
            Assert.That(help, Does.Contain("main viewport"), "states the renderer's one seam");
        });
    }

    [Test]
    public async Task InfoStatusCountAndLast_RenderThePublishedStore()
    {
        Publish(Vessel(0), Body(3));
        _stickers.PublishLast("3 body Mun hit 41.5m");

        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAsync("paint", "stickers", "info"), Is.EqualTo(
                "enabled=1 stickers=2 stickers_max=64 live=1 images=1 vram_bytes=4096 "
                + "patch=1 renderer=active max_view_distance_m=1500\n"));
            Assert.That(await ReadAsync("paint", "stickers", "status"), Is.EqualTo(
                "0 meow.png vessel Kitten-1 live=1 texture=ready\n"
                + "3 logo.png body Mun live=0 texture=missing\n"));
            Assert.That(await ReadAsync("paint", "stickers", "count"), Is.EqualTo("2\n"));
            Assert.That(await ReadAsync("paint", "stickers", "last"), Is.EqualTo("3 body Mun hit 41.5m\n"));
            Assert.That(await ReadAsync("paint", "stickers", "last_error"), Is.EqualTo("\n"));
        });
    }

    [Test]
    public async Task LastError_RendersTheRuntimeFault()
    {
        _stickers.Publish([], new StickerRuntime(false, 0, 0, 0, 0, false, "degraded", "pipeline\nfailed"));
        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAsync("paint", "stickers", "last_error"), Is.EqualTo("pipeline failed\n"));
            Assert.That(await ReadAsync("paint", "stickers", "status"), Is.EqualTo("\n"),
                "no stickers, no rows");
        });
    }

    [Test]
    public async Task PerSticker_LeavesProjectTheEntry()
    {
        Publish(Vessel(2), Body(7));
        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAsync("paint", "stickers", "2", "image"), Is.EqualTo("meow.png\n"));
            Assert.That(await ReadAsync("paint", "stickers", "2", "anchor"),
                Is.EqualTo("vessel Kitten-1 1187 0 0.5 -1.4 0 1 0\n"));
            Assert.That(await ReadAsync("paint", "stickers", "2", "live"), Is.EqualTo("1\n"));
            Assert.That(await ReadAsync("paint", "stickers", "2", "visible"), Is.EqualTo("1\n"));
            Assert.That(await ReadAsync("paint", "stickers", "2", "size"), Is.EqualTo("0.6 0.3\n"));
            Assert.That(await ReadAsync("paint", "stickers", "2", "depth"), Is.EqualTo("0.05\n"));
            Assert.That(await ReadAsync("paint", "stickers", "2", "rotation"), Is.EqualTo("15\n"));
            Assert.That(await ReadAsync("paint", "stickers", "2", "alpha"), Is.EqualTo("0.5\n"));
            Assert.That(await ReadAsync("paint", "stickers", "2", "brightness"), Is.EqualTo("2\n"));
            Assert.That(await ReadAsync("paint", "stickers", "7", "anchor"),
                Is.EqualTo("body Mun 12.03 -41.88\n"));
            Assert.That(await ReadAsync("paint", "stickers", "7", "live"), Is.EqualTo("0\n"));
        });
    }

    [Test]
    public async Task Spec_ReadsBackAsAValidPlaceLine()
    {
        Publish(Vessel(1));
        var spec = (await ReadAsync("paint", "stickers", "1", "spec")).TrimEnd('\n');
        Assert.That(spec, Is.EqualTo(
            "meow.png vessel Kitten-1 1187 0 0.5 -1.4 0 1 0 roll=15 w=0.6 h=0.3 d=0.05 alpha=0.5 brightness=2"));

        // The symmetry contract: echo a spec straight back into place.
        await WriteAsync(spec, "paint", "stickers", "place");
        var command = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(command.Action, Is.EqualTo(SimActions.PaintStickerPlace));
            Assert.That(command.Aux, Is.EqualTo("vessel Kitten-1 1187"));
            Assert.That(command.Values, Is.EqualTo(new[]
            {
                0d, 0.5, -1.4, 0d, 1d, 0d, 15d, 0.6, 0.3, 0.05, 0.5, 2d,
            }));
        });
    }

    [Test]
    public async Task Registry_ListsOneDirPerSticker()
    {
        Publish(Vessel(0), Body(3));
        var dirFid = await WalkAsync("paint", "stickers");
        await _client.LopenAsync(dirFid);
        var names = (await _client.ReaddirAllAsync(dirFid))
            .Select(e => e.Name).Where(n => n is not ("." or "..")).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("0"));
            Assert.That(names, Does.Contain("3"));
            Assert.That(names, Does.Contain("place"));
            Assert.That(names, Does.Contain("spray"));
            Assert.That(names, Does.Contain("clear"));
        });
    }

    [Test]
    public async Task MissingSticker_IsEnoent()
    {
        Publish(Vessel(0));
        var dirFid = await WalkAsync("paint", "stickers");
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WalkAsync(dirFid, _nextFid++, "7"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    // ---- writes ------------------------------------------------------------------------------

    [Test]
    public async Task Place_SubmitsTheParsedCommand()
    {
        await WriteAsync("meow.png body Mun 12.03 -41.88 heading=90 w=5 h=5", "paint", "stickers", "place");
        var command = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(command.Action, Is.EqualTo(SimActions.PaintStickerPlace));
            Assert.That(command.Token, Is.EqualTo("meow.png"));
            Assert.That(command.Aux, Is.EqualTo("body Mun"));
            Assert.That(command.Values, Is.EqualTo(new[]
            {
                12.03, -41.88, 0d, 0d, 0d, 0d, 90d, 5d, 5d, 1d, 1d, 1d,
            }));
            Assert.That(command.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [Test]
    public async Task Spray_SubmitsTheParsedCommand()
    {
        await WriteAsync("meow.png aim=cursor w=2 h=2", "paint", "stickers", "spray");
        var command = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(command.Action, Is.EqualTo(SimActions.PaintStickerSpray));
            Assert.That(command.Token, Is.EqualTo("meow.png"));
            Assert.That(command.Aux, Is.EqualTo("cursor"));
            Assert.That(command.Values, Is.EqualTo(new[] { 2000d, 0d, 2d, 2d, -1d, 1d, 1d }));
        });
    }

    [Test]
    public async Task PlaceAndSpray_ReadEmpty()
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAsync("paint", "stickers", "place"), Is.EqualTo("\n"));
            Assert.That(await ReadAsync("paint", "stickers", "spray"), Is.EqualTo("\n"));
        });
    }

    [Test]
    public async Task PerSticker_ControlsCarryTheIdInTheOrdinal()
    {
        Publish(Vessel(5));
        await WriteAsync("0", "paint", "stickers", "5", "visible");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", SimActions.PaintStickerVisible, 5, 0)));

        await WriteAsync("1.5 0.75", "paint", "stickers", "5", "size");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo(SimActions.PaintStickerSize));
            Assert.That(_sink.Last!.Ordinal, Is.EqualTo(5));
            Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 1.5, 0.75 }));
        });

        await WriteAsync("0.25", "paint", "stickers", "5", "depth");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", SimActions.PaintStickerDepth, 5, 0.25)));

        await WriteAsync("-30", "paint", "stickers", "5", "rotation");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", SimActions.PaintStickerRotation, 5, -30)));

        await WriteAsync("0.4", "paint", "stickers", "5", "alpha");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", SimActions.PaintStickerAlpha, 5, 0.4)));

        await WriteAsync("3", "paint", "stickers", "5", "brightness");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", SimActions.PaintStickerBrightness, 5, 3)));

        await WriteAsync("other.png", "paint", "stickers", "5", "image");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", SimActions.PaintStickerImage, 5, 0)
            { Token = "other.png" }));
    }

    [Test]
    public async Task Debug_IsAGlobalFlagControl()
    {
        Assert.That(await ReadAsync("paint", "stickers", "debug"), Is.EqualTo("0\n"));

        await WriteAsync("1", "paint", "stickers", "debug");
        Assert.That(_sink.Last,
            Is.EqualTo(new SimCommand("", SimActions.PaintStickerDebug, SimCommand.NoOrdinal, 1)));

        await WriteAsync("0", "paint", "stickers", "debug");
        Assert.That(_sink.Last,
            Is.EqualTo(new SimCommand("", SimActions.PaintStickerDebug, SimCommand.NoOrdinal, 0)));
    }

    [Test]
    public async Task Debug_ReadsWhatTheManagerPublished()
    {
        _stickers.PublishDebug(true);
        Assert.That(await ReadAsync("paint", "stickers", "debug"), Is.EqualTo("1\n"));
        _stickers.PublishDebug(false);
        Assert.That(await ReadAsync("paint", "stickers", "debug"), Is.EqualTo("0\n"));
    }

    [Test]
    public async Task BadDebugWrite_IsEinval_AndDoesNotSubmit()
    {
        var submits = _sink.Submits;
        var fid = await WalkAsync("paint", "stickers", "debug");
        await _client.LopenAsync(fid, 1);
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WriteAsync(fid, 0, "maybe\n"u8.ToArray()));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(submits));
        });
    }

    [Test]
    public async Task RemoveAndClear_FireTriggers()
    {
        Publish(Vessel(4));
        await WriteAsync("1", "paint", "stickers", "4", "remove");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", SimActions.PaintStickerRemove, 4, 1)));

        await WriteAsync("1", "paint", "stickers", "clear");
        Assert.That(_sink.Last,
            Is.EqualTo(new SimCommand("", SimActions.PaintStickerClear, SimCommand.NoOrdinal, 1)));
    }

    [TestCase("place", "meow.png")]
    [TestCase("place", "meow.png vessel Kitten-1 1187 0 0 0 0 0 0")]
    [TestCase("place", "meow.png body Mun 91 0")]
    [TestCase("place", "meow.png body Mun 0 0 w=1 w=2")]
    [TestCase("spray", "meow.png aim=nose")]
    [TestCase("spray", "bad name.png")]
    [TestCase("spray", "meow.png brightness=99")]
    public async Task BadControlLine_IsEinval_AndDoesNotSubmit(string control, string line)
    {
        var submits = _sink.Submits;
        var fid = await WalkAsync("paint", "stickers", control);
        await _client.LopenAsync(fid, 1);
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WriteAsync(fid, 0, Encoding.UTF8.GetBytes(line + "\n")));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(submits));
        });
    }

    [TestCase("image", "bad name.png")]
    [TestCase("size", "1")]
    [TestCase("size", "1 2 3")]
    // size carries the same (0, 1000] bounds as the scalar knobs on BOTH components — the fixed
    // arity of the vector archetype only ever checked the count, so these used to write "successfully"
    // and then be dropped game-side with nothing on the wire to say so.
    [TestCase("size", "0 1")]
    [TestCase("size", "1 0")]
    [TestCase("size", "-2 1")]
    [TestCase("size", "1001 1")]
    [TestCase("size", "1 1001")]
    [TestCase("size", "nan 1")]
    [TestCase("depth", "0")]
    [TestCase("alpha", "2")]
    [TestCase("brightness", "9")]
    public async Task BadPerStickerWrite_IsEinval_AndDoesNotSubmit(string leaf, string line)
    {
        Publish(Vessel(1));
        var submits = _sink.Submits;
        var fid = await WalkAsync("paint", "stickers", "1", leaf);
        await _client.LopenAsync(fid, 1);
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WriteAsync(fid, 0, Encoding.UTF8.GetBytes(line + "\n")));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(submits));
        });
    }

    // ---- helpers (mirror TextureTreeTests) ---------------------------------------------------

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
