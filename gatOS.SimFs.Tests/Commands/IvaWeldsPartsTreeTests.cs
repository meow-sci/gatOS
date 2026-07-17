using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The two cheat features added to <c>/sim</c> walked over a live <c>NinePServer</c>: the
///     per-vessel <c>parts/</c> anchor picker, the global <c>always_render_iva</c> toggle, and the
///     <c>welds/</c> registry + per-source <c>weld</c>/<c>weld_here</c>/<c>unweld</c> controls. A
///     <see cref="FakeCommandSink"/> stands in for the game thread, so these assert the command built
///     and the values read back — never game effects.
/// </summary>
[TestFixture]
public sealed class IvaWeldsPartsTreeTests
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
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    private static VesselSnapshot WithParts(params PartSnapshot[] parts)
        => TestData.Vessel() with { Parts = parts };

    // ---- parts/ (the anchor picker) ----------------------------------------------------------

    [Test]
    public async Task Parts_AreListedWithStableInstanceId()
    {
        _store.Publish(TestData.Snapshot(1, WithParts(
            new PartSnapshot(0, 4242, "rootpart", "Command Pod", "pod_mk1", true, 3, new double3Snap(0, 0, 0)),
            new PartSnapshot(1, 4243, "tankpart", "Fuel Tank", "tank_x", false, 0, new double3Snap(0, -1.5, 0)))));

        var iid0 = await ReadAsync("vessels", "by-id", "test-1", "parts", "0", "instance_id");
        var root0 = await ReadAsync("vessels", "by-id", "test-1", "parts", "0", "is_root");
        var sub0 = await ReadAsync("vessels", "by-id", "test-1", "parts", "0", "subpart_count");
        var iid1 = await ReadAsync("vessels", "by-id", "test-1", "parts", "1", "instance_id");
        var tmpl1 = await ReadAsync("vessels", "by-id", "test-1", "parts", "1", "template");
        var root1 = await ReadAsync("vessels", "by-id", "test-1", "parts", "1", "is_root");
        var pos1 = await ReadAsync("vessels", "by-id", "test-1", "parts", "1", "position");
        Assert.Multiple(() =>
        {
            Assert.That(iid0, Is.EqualTo("4242\n"));
            Assert.That(root0, Is.EqualTo("1\n"));
            Assert.That(sub0, Is.EqualTo("3\n"));
            Assert.That(iid1, Is.EqualTo("4243\n"));
            Assert.That(tmpl1, Is.EqualTo("tank_x\n"));
            Assert.That(root1, Is.EqualTo("0\n"));
            Assert.That(pos1, Is.EqualTo("0 -1.5 0\n"));
        });
    }

    [Test]
    public async Task Subparts_AreListedUnderTheirPart_WithOwnInstanceIds()
    {
        _store.Publish(TestData.Snapshot(1, WithParts(
            new PartSnapshot(0, 4242, "rootpart", "Command Pod", "pod_mk1", true, 2, new double3Snap(0, 0, 0))
            {
                Subparts =
                [
                    new SubpartSnapshot(0, 9001, "hatch", "Hatch", "hatch_a", new double3Snap(0, 1, 0)),
                    new SubpartSnapshot(1, 9002, "antenna", "Antenna", "ant_b", new double3Snap(0.5, 1, 0)),
                ],
            },
            new PartSnapshot(1, 4243, "tankpart", "Fuel Tank", "tank_x", false, 0, new double3Snap(0, -1.5, 0)))));

        var iid0 = await ReadAsync("vessels", "by-id", "test-1", "parts", "0", "subparts", "0", "instance_id");
        var name0 = await ReadAsync("vessels", "by-id", "test-1", "parts", "0", "subparts", "0", "display_name");
        var iid1 = await ReadAsync("vessels", "by-id", "test-1", "parts", "0", "subparts", "1", "instance_id");
        var tmpl1 = await ReadAsync("vessels", "by-id", "test-1", "parts", "0", "subparts", "1", "template");
        var pos1 = await ReadAsync("vessels", "by-id", "test-1", "parts", "0", "subparts", "1", "position");
        Assert.Multiple(() =>
        {
            Assert.That(iid0, Is.EqualTo("9001\n"));
            Assert.That(name0, Is.EqualTo("Hatch\n"));
            Assert.That(iid1, Is.EqualTo("9002\n"));
            Assert.That(tmpl1, Is.EqualTo("ant_b\n"));
            Assert.That(pos1, Is.EqualTo("0.5 1 0\n"));
        });

        // A part without subparts keeps the (empty) subparts/ dir; a child walk is ENOENT.
        var emptyDir = await WalkAsync("vessels", "by-id", "test-1", "parts", "1", "subparts");
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WalkAsync(emptyDir, _nextFid++, "0"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public async Task PartsJson_ServesTheWholeTree_AsOneSnakeCaseDocument()
    {
        _store.Publish(TestData.Snapshot(1, WithParts(
            new PartSnapshot(0, 4242, "rootpart", "Command Pod", "pod_mk1", true, 1, new double3Snap(0, 0, 0))
            {
                Subparts = [new SubpartSnapshot(0, 9001, "hatch", "Hatch", "hatch_a", new double3Snap(0, 1, 0))],
            },
            new PartSnapshot(1, 4243, "tankpart", "Fuel Tank", "tank_x", false, 0, new double3Snap(0, -1.5, 0)))));

        var text = await ReadAsync("vessels", "by-id", "test-1", "parts", "json");
        Assert.That(text, Does.EndWith("\n"));

        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var parts = doc.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(parts.GetArrayLength(), Is.EqualTo(2));
            Assert.That(parts[0].GetProperty("instance_id").GetUInt32(), Is.EqualTo(4242));
            Assert.That(parts[0].GetProperty("display_name").GetString(), Is.EqualTo("Command Pod"));
            Assert.That(parts[0].GetProperty("is_root").GetBoolean(), Is.True);
            Assert.That(parts[0].GetProperty("subparts")[0].GetProperty("instance_id").GetUInt32(),
                Is.EqualTo(9001));
            Assert.That(parts[0].GetProperty("subparts")[0].GetProperty("template").GetString(),
                Is.EqualTo("hatch_a"));
            Assert.That(parts[1].GetProperty("id").GetString(), Is.EqualTo("tankpart"));
            Assert.That(parts[1].GetProperty("subparts").GetArrayLength(), Is.EqualTo(0));
        });

        // The json file coexists with the indexed children (parts/0, parts/1 still resolve).
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "parts", "0", "instance_id"),
            Is.EqualTo("4242\n"));
    }

    [Test]
    public async Task Parts_AreAbsentWhenVesselHasNone()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel())); // default fixture carries no parts
        var vesselFid = await WalkAsync("vessels", "by-id", "test-1");
        // A single-element walk to a missing child errors (a multi-element walk would return partial qids).
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WalkAsync(vesselFid, _nextFid++, "parts"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    // ---- always_render_iva (global render cheat) ---------------------------------------------

    [Test]
    public async Task AlwaysRenderIva_ReadsSnapshotFlag_AndWritesToggle()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()) with { AlwaysRenderIva = true });
        Assert.That(await ReadAsync("debug", "always_render_iva"), Is.EqualTo("1\n"));

        await WriteAsync("0\n", "debug", "always_render_iva");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "debug.always_render_iva", SimCommand.NoOrdinal, 0)));
        await WriteAsync("1\n", "debug", "always_render_iva");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "debug.always_render_iva", SimCommand.NoOrdinal, 1)));
    }

    // ---- weld / weld_here / unweld (per-source write controls) -------------------------------

    [Test]
    public async Task Weld_ExplicitPose_BuildsCreateCommand()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        await WriteAsync("Polaris 4242 1 2 3 10 20 30 1\n", "debug", "vessels", "test-1", "weld");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("debug.weld_create"));
            Assert.That(c.VesselId, Is.EqualTo("test-1"));
            Assert.That(c.Token, Is.EqualTo("Polaris"));
            Assert.That(c.Values, Is.EqualTo(new[] { 4242d, 1, 2, 3, 10, 20, 30, 1 }));
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame), "weld create mutates only the registry");
        });
    }

    [Test]
    public async Task WeldHere_DefaultsLockOn_WhenOmitted()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        await WriteAsync("Polaris 4242\n", "debug", "vessels", "test-1", "weld_here");
        Assert.That(_sink.Last!.Action, Is.EqualTo("debug.weld_here"));
        Assert.That(_sink.Last!.Token, Is.EqualTo("Polaris"));
        Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 4242d, 1d }), "lock defaults to 1");

        await WriteAsync("Polaris 0 0\n", "debug", "vessels", "test-1", "weld_here");
        Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 0d, 0d }), "explicit lock honored; part 0 = body frame");
    }

    [Test]
    public async Task Unweld_FiresRemoveTrigger()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        await WriteAsync("1\n", "debug", "vessels", "test-1", "unweld");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "debug.weld_remove", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public void Weld_BadArity_IsEinval_AndDoesNotSubmit()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync("Polaris 4242 1 2 3\n", "debug", "vessels", "test-1", "weld"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    // ---- welds/ (the registry view + global ops) --------------------------------------------

    [Test]
    public async Task WeldsRegistry_ProjectsActiveWelds()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()) with
        {
            Welds =
            [
                new WeldSnapshot("test-1", "Polaris", 4242,
                    new double3Snap(1, 2, 3), new double3Snap(10, 20, 30), LockRotation: true, Enabled: true),
            ],
        });

        var count = await ReadAsync("debug", "welds", "count");
        var target = await ReadAsync("debug", "welds", "test-1", "target");
        var part = await ReadAsync("debug", "welds", "test-1", "part");
        var offset = await ReadAsync("debug", "welds", "test-1", "offset");
        var rotation = await ReadAsync("debug", "welds", "test-1", "rotation");
        var lockRot = await ReadAsync("debug", "welds", "test-1", "lock_rotation");
        var enabled = await ReadAsync("debug", "welds", "test-1", "enabled");
        var spec = await ReadAsync("debug", "vessels", "test-1", "weld");
        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo("1\n"));
            Assert.That(target, Is.EqualTo("Polaris\n"));
            Assert.That(part, Is.EqualTo("4242\n"));
            Assert.That(offset, Is.EqualTo("1 2 3\n"));
            Assert.That(rotation, Is.EqualTo("10 20 30\n"));
            Assert.That(lockRot, Is.EqualTo("1\n"));
            Assert.That(enabled, Is.EqualTo("1\n"));
            // The per-source weld read-back is the write-compatible spec line.
            Assert.That(spec, Is.EqualTo("Polaris 4242 1 2 3 10 20 30 1\n"));
        });
    }

    [Test]
    public async Task WeldsRegistry_EnabledTogglesAndClearFire()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()) with
        {
            Welds =
            [
                new WeldSnapshot("test-1", "Polaris", 0,
                    new double3Snap(0, 0, 0), new double3Snap(0, 0, 0), LockRotation: false, Enabled: true),
            ],
        });

        await WriteAsync("0\n", "debug", "welds", "test-1", "enabled");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "debug.weld_enable", SimCommand.NoOrdinal, 0)));

        await WriteAsync("1\n", "debug", "welds", "clear");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "debug.weld_clear", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public async Task UnweldedSource_ReadsEmptyWeldSpec()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        Assert.That(await ReadAsync("debug", "vessels", "test-1", "weld"), Is.EqualTo("\n"));
    }

    // ---- helpers (mirror ControlSurfaceTests) -----------------------------------------------

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
