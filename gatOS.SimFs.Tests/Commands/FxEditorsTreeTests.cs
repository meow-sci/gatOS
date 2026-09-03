using System.Text;
using System.Text.Json;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Fx;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The four FX editors (<c>/sim/debug/{engineplume,plumetrail,clouds,terrain}</c>) walked over a
///     live <see cref="NinePServer"/>: catalog-driven leaves read the sampled value back, a write
///     builds the family's single <c>_set</c> command (entity in <c>Token</c>, concrete field path in
///     <c>Aux</c>), and every range/arity violation fails EINVAL before the sink. A
///     <see cref="FakeCommandSink"/> stands in for the game thread, so these assert the command built
///     and the values read back — never game effects.
/// </summary>
[TestFixture]
public sealed class FxEditorsTreeTests
{
    private SnapshotStore _store = null!;
    private FakeCommandSink _sink = null!;
    private NinePServer _server = null!;
    private NinePTestClient _client = null!;
    private uint _nextFid;

    [SetUp]
    public async Task SetUp()
    {
        _store = new SnapshotStore();
        _sink = new FakeCommandSink { DebugEnabled = true };
        _server = new NinePServer(SimFsTree.Build(_store, _sink, () => "9p 4242\ncontrol on"));
        await _server.StartAsync();
        _client = await NinePTestClient.ConnectAsync(_server.Port);
        await _client.VersionAsync();
        await _client.AttachAsync(0);
        _nextFid = 1;
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()).WithFxEditors());
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    // ---- engineplume (per-template entities) ------------------------------------------------

    [Test]
    public async Task EnginePlume_LeavesReadTheSampledValue()
    {
        var color = await ReadAsync("debug", "engineplume", "templates", "kerolox", "emission", "color0");
        var brightness = await ReadAsync("debug", "engineplume", "templates", "kerolox", "emission", "brightness");
        var flag = await ReadAsync("debug", "engineplume", "templates", "methalox", "absorption",
            "fake_clean_burn");
        Assert.Multiple(() =>
        {
            Assert.That(color, Is.EqualTo("0.5 0.75 1\n"), "a Color3 leaf is 'r g b'");
            Assert.That(brightness, Is.EqualTo("0.5\n"));
            Assert.That(flag, Is.EqualTo("1\n"), "a Flag leaf is 0/1");
        });
    }

    [Test]
    public async Task EnginePlume_Write_BuildsSetCommandWithTemplateAndField()
    {
        await WriteAsync("1 0.35 0.05\n", "debug", "engineplume", "templates", "methalox", "emission", "color0");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo(FxCatalog.EnginePlumeSet));
            Assert.That(c.VesselId, Is.EqualTo(""), "FX actions are vessel-agnostic");
            Assert.That(c.Ordinal, Is.EqualTo(SimCommand.NoOrdinal));
            Assert.That(c.Token, Is.EqualTo("methalox"), "the entity id rides in Token");
            Assert.That(c.Aux, Is.EqualTo("emission/color0"), "the concrete field path rides in Aux");
            Assert.That(c.Values, Is.EqualTo(new[] { 1d, 0.35, 0.05 }));
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame), "FX writes are render-side, never solver");
        });
    }

    [Test]
    public async Task EnginePlume_ScalarWrite_CarriesTheValueOnBothFields()
    {
        await WriteAsync("42\n", "debug", "engineplume", "templates", "kerolox", "emission", "brightness");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Value, Is.EqualTo(42));
            Assert.That(c.Values, Is.EqualTo(new[] { 42d }));
            Assert.That(c.Aux, Is.EqualTo("emission/brightness"));
        });
    }

    [TestCase("abc\n")] // unparseable
    [TestCase("1 0.5\n")] // wrong arity (Color3 wants 3)
    [TestCase("1 0.5 0.2 0.1\n")] // wrong arity
    [TestCase("1 1.5 0\n")] // out of range (channels are 0..1)
    [TestCase("-0.1 0 0\n")] // out of range low
    public void EnginePlume_BadColor_IsEinval_AndDoesNotSubmit(string line)
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(line, "debug", "engineplume", "templates", "kerolox", "emission", "color0"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    [TestCase("201\n")] // brightness range is 0..200
    [TestCase("-1\n")]
    [TestCase("nan\n")]
    public void EnginePlume_BadNumber_IsEinval_AndDoesNotSubmit(string line)
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(line, "debug", "engineplume", "templates", "kerolox", "emission", "brightness"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public void EnginePlume_HalfFlag_IsEinval()
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync("0.5\n", "debug", "engineplume", "templates", "kerolox", "absorption",
                "fake_clean_burn"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task EnginePlume_Reset_FiresTriggerForItsTemplate()
    {
        await WriteAsync("1\n", "debug", "engineplume", "templates", "kerolox", "reset");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo(FxCatalog.EnginePlumeReset));
            Assert.That(c.Token, Is.EqualTo("kerolox"));
            Assert.That(c.Value, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task EnginePlume_Json_IsOneObjectPerField()
    {
        using var doc = JsonDocument.Parse(
            await ReadAsync("debug", "engineplume", "templates", "kerolox", "json"));
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("emission/brightness").GetDouble(), Is.EqualTo(0.5));
            Assert.That(doc.RootElement.GetProperty("emission/color0").EnumerateArray()
                .Select(e => e.GetDouble()), Is.EqualTo(new[] { 0.5, 0.75, 1d }));
            Assert.That(doc.RootElement.EnumerateObject().Count(),
                Is.EqualTo(FxCatalog.EnginePlume.Count), "one member per catalog field");
        });
    }

    [Test]
    public async Task EnginePlume_Help_DocumentsScopeAndReset()
    {
        var help = await ReadAsync("debug", "engineplume", "help");
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Contain("PER TEMPLATE"), "states the sharing scope");
            Assert.That(help, Does.Contain("reset"));
            Assert.That(help, Does.Contain("emission/"));
            Assert.That(help, Does.Contain("SPEC_9P_FILESYSTEM.md"));
        });
    }

    [Test]
    public async Task EnginePlume_UnknownTemplate_IsEnoent()
    {
        var dirFid = await WalkAsync("debug", "engineplume", "templates");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.WalkAsync(dirFid, _nextFid++, "nope"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public async Task NoFxSample_LeavesTheFamilyDirsEmpty()
    {
        _store.Publish(TestData.Snapshot(2, TestData.Vessel())); // no FxEditors at all
        Assert.Multiple(async () =>
        {
            Assert.That(await NamesAsync("debug", "engineplume", "templates"), Is.Empty);
            Assert.That(await NamesAsync("debug", "clouds", "bodies"), Is.Empty);
            // The trail/terrain-global leaves are entity-backed too, so only help + the one-shots stay.
            Assert.That(await NamesAsync("debug", "plumetrail"), Is.EqualTo(new[] { "help", "clear" }));
            Assert.That(await NamesAsync("debug", "terrain"), Is.EqualTo(new[] { "help", "bodies" }));
        });
    }

    // ---- plumetrail (global singleton) ------------------------------------------------------

    [Test]
    public async Task PlumeTrail_ReadsAndWritesGlobalFields()
    {
        var distance = await ReadAsync("debug", "plumetrail", "render", "max_distance");
        await WriteAsync("200000\n", "debug", "plumetrail", "render", "max_distance");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(distance, Is.EqualTo("0.5\n"));
            Assert.That(c.Action, Is.EqualTo(FxCatalog.PlumeTrailSet));
            Assert.That(c.Token, Is.Null, "the trail is a singleton — no entity token");
            Assert.That(c.Aux, Is.EqualTo("render/max_distance"));
            Assert.That(c.Values, Is.EqualTo(new[] { 200000d }));
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [TestCase("0\n")] // below the 0.01 minimum
    [TestCase("1e8\n")] // above the 1e7 maximum
    [TestCase("\n")] // empty
    public void PlumeTrail_OutOfRange_IsEinval_AndDoesNotSubmit(string line)
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(line, "debug", "plumetrail", "render", "max_distance"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task PlumeTrail_ClearAndReset_FireTriggers()
    {
        await WriteAsync("1\n", "debug", "plumetrail", "clear");
        Assert.That(_sink.Last,
            Is.EqualTo(new SimCommand("", FxCatalog.PlumeTrailClear, SimCommand.NoOrdinal, 1)));

        await WriteAsync("1\n", "debug", "plumetrail", "reset");
        var reset = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(reset.Action, Is.EqualTo(FxCatalog.PlumeTrailReset));
            Assert.That(reset.Token, Is.Null);
        });
    }

    // ---- clouds (per body → layer → cloud type) ---------------------------------------------

    [Test]
    public async Task Clouds_IndexedLeaf_ReadsBackAndWritesItsFullPath()
    {
        var density = await ReadAsync("debug", "clouds", "bodies", "Kerth", "layers", "1", "types", "0", "density");
        await WriteAsync("4\n", "debug", "clouds", "bodies", "Kerth", "layers", "1", "types", "0", "density");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(density, Is.EqualTo("0.5\n"));
            Assert.That(c.Action, Is.EqualTo(FxCatalog.CloudsSet));
            Assert.That(c.Token, Is.EqualTo("Kerth"));
            Assert.That(c.Aux, Is.EqualTo("layers/1/types/0/density"), "indices ride in the field path");
            Assert.That(c.Values, Is.EqualTo(new[] { 4d }));
        });
    }

    [Test]
    public async Task Clouds_LayerSubtrees_AreDerivedFromTheSampledKeys()
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await NamesAsync("debug", "clouds", "bodies", "Kerth", "layers"),
                Is.EqualTo(new[] { "0", "1" }), "two sampled layers");
            Assert.That(await NamesAsync("debug", "clouds", "bodies", "Kerth", "layers", "0", "types"),
                Is.EqualTo(new[] { "0", "1" }), "two sampled cloud types");
            Assert.That(await NamesAsync("debug", "clouds", "bodies", "Kerth"),
                Does.Contain("shared").And.Contain("json").And.Contain("reset"));
        });
    }

    [Test]
    public async Task Clouds_UnboundedVectorLeaf_TakesAnyFiniteTriple()
    {
        await WriteAsync("-1000 0 2.5\n", "debug", "clouds", "bodies", "Kerth", "layers", "0", "rotation_speed");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Aux, Is.EqualTo("layers/0/rotation_speed"));
            Assert.That(c.Values, Is.EqualTo(new[] { -1000d, 0, 2.5 }));
        });
    }

    [TestCase("2 0 0\n")] // channel above 1
    [TestCase("0 0\n")] // wrong arity
    public void Clouds_BadColor_IsEinval_AndDoesNotSubmit(string line)
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(line, "debug", "clouds", "bodies", "Kerth", "layers", "0", "two_d", "color"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Clouds_ResetAndJsonAndHelp_AreServed()
    {
        await WriteAsync("1\n", "debug", "clouds", "bodies", "Kerth", "reset");
        var reset = _sink.Last!;
        using var doc = JsonDocument.Parse(await ReadAsync("debug", "clouds", "bodies", "Kerth", "json"));
        var help = await ReadAsync("debug", "clouds", "help");
        Assert.Multiple(() =>
        {
            Assert.That(reset.Action, Is.EqualTo(FxCatalog.CloudsReset));
            Assert.That(reset.Token, Is.EqualTo("Kerth"));
            Assert.That(doc.RootElement.GetProperty("layers/1/types/0/density").GetDouble(), Is.EqualTo(0.5));
            Assert.That(doc.RootElement.GetProperty("layers/0/color").EnumerateArray().Count(), Is.EqualTo(3));
            Assert.That(help, Does.Contain("PER BODY"));
            Assert.That(help, Does.Contain("layers/"));
        });
    }

    // ---- terrain (global toggle + per body) -------------------------------------------------

    [Test]
    public async Task Terrain_GlobalWireframe_UsesTheEmptyEntityToken()
    {
        var before = await ReadAsync("debug", "terrain", "wireframe");
        await WriteAsync("0\n", "debug", "terrain", "wireframe");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo("1\n"));
            Assert.That(c.Action, Is.EqualTo(FxCatalog.TerrainSet));
            Assert.That(c.Token, Is.EqualTo(""), "the global fields use an empty entity token");
            Assert.That(c.Aux, Is.EqualTo("wireframe"));
            Assert.That(c.Values, Is.EqualTo(new[] { 0d }));
        });
    }

    [Test]
    public void Terrain_WireframeNonFlag_IsEinval_AndDoesNotSubmit()
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync("2\n", "debug", "terrain", "wireframe"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Terrain_PerBodyLeaf_ReadsBackAndWrites()
    {
        var minHeight = await ReadAsync("debug", "terrain", "bodies", "Kerth", "min_height");
        var factor = await ReadAsync("debug", "terrain", "bodies", "Kerth", "tessellation", "factor");
        await WriteAsync("9000\n", "debug", "terrain", "bodies", "Kerth", "max_height");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(minHeight, Is.EqualTo("0\n"), "clamped into the [-20000, 0] fixture range");
            Assert.That(factor, Is.EqualTo("0.5\n"));
            Assert.That(c.Action, Is.EqualTo(FxCatalog.TerrainSet));
            Assert.That(c.Token, Is.EqualTo("Kerth"));
            Assert.That(c.Aux, Is.EqualTo("max_height"));
            Assert.That(c.Value, Is.EqualTo(9000));
        });
    }

    [Test]
    public async Task Terrain_PerBodyDir_CarriesNoGlobalField()
    {
        var names = await NamesAsync("debug", "terrain", "bodies", "Kerth");
        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Not.Contain("wireframe"), "wireframe is family-global");
            Assert.That(names, Does.Contain("biomes").And.Contain("tessellation")
                .And.Contain("json").And.Contain("reset"));
        });
    }

    [Test]
    public async Task Terrain_Reset_FiresPerBody()
    {
        await WriteAsync("1\n", "debug", "terrain", "bodies", "Kerth", "reset");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo(FxCatalog.TerrainReset));
            Assert.That(c.Token, Is.EqualTo("Kerth"));
        });
    }

    [Test]
    public async Task Terrain_Help_DocumentsTheGlobalAndPerBodySplit()
    {
        var help = await ReadAsync("debug", "terrain", "help");
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Contain("GLOBAL"));
            Assert.That(help, Does.Contain("per body"));
            Assert.That(help, Does.Contain("wireframe"));
        });
    }

    // ---- helpers (mirror ThugLifeTreeTests) -------------------------------------------------

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

    private async Task<string[]> NamesAsync(params string[] names)
    {
        var fid = await WalkAsync(names);
        await _client.LopenAsync(fid);
        var entries = await _client.ReaddirAllAsync(fid);
        await _client.ClunkAsync(fid);
        return entries.Select(e => e.Name).Where(n => n is not ("." or "..")).ToArray();
    }
}
