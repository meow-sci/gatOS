using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The IVA cabin-physics surface (<c>/sim/debug/iva</c>) walked over a live
///     <see cref="NinePServer"/>: the master <c>enabled</c> switch, the <c>adopt</c>/<c>adopt_all</c>
///     grammar, the registry reads, and the per-object
///     <c>&lt;id&gt;/{position,velocity,mass,shape,asleep,nudge,release,spec}</c> surface. A
///     <see cref="FakeCommandSink"/> stands in for the game thread, so these assert the command built
///     and the values read back — never game effects (the physics itself is
///     <see cref="Iva.CabinPhysicsTests"/>).
/// </summary>
[TestFixture]
public sealed class IvaPhysicsTreeTests
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

    private static IvaObjectSnapshot Object(int id) => new(
        id, "Gemini7", 4242, "Sardine Tin", "CoreIVAPropA_Subpart_DeanSardineA",
        new double3Snap(0.1, 0.2, 0.3), new double3Snap(0.01, 0, -0.02), new double3Snap(0, 0.5, 0),
        0.12, "box", new double3Snap(0.08, 0.05, 0.02), false);

    private static SimSnapshot WithIva(params IvaObjectSnapshot[] objects)
        => TestData.Snapshot(1, TestData.Vessel()) with
        {
            Iva = new IvaSnapshot(true, false, objects,
                [new IvaInteriorSnapshot("Gemini7", 5120, 42,
                    new double3Snap(-1, -0.9, -1.2), new double3Snap(1, 0.9, 0.4), false)],
                new IvaStatsSnapshot(1, objects.Length, 0, 4, 0.35, 1.2, false, "")),
        };

    // ---- the master switch: off by default, and it is what starts and ends everything ---------

    [Test]
    public async Task Enabled_DefaultsOff_AndIsReadableWhileDark()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel())); // no Iva block published at all
        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAsync("debug", "iva", "enabled"), Is.EqualTo("0\n"),
                "the feature ships off");
            Assert.That(await ReadAsync("debug", "iva", "count"), Is.EqualTo("0\n"));
            Assert.That(await ReadAsync("debug", "iva", "interior"), Is.Empty,
                "no interior geometry exists while the sim is off");
        });
    }

    [Test]
    public async Task Enabled_WritesTheMasterSwitch()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        await WriteAsync("1\n", "debug", "iva", "enabled");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "debug.iva_physics", SimCommand.NoOrdinal, 1)));

        await WriteAsync("0\n", "debug", "iva", "enabled");
        var off = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(off, Is.EqualTo(new SimCommand("", "debug.iva_physics", SimCommand.NoOrdinal, 0)));
            Assert.That(off.Phase, Is.EqualTo(CommandPhase.Frame),
                "IVA physics mutates part transforms and our own registry, never the flight computer");
        });
    }

    [Test]
    public async Task RunOutsideIva_WritesItsFlag()
    {
        _store.Publish(WithIva());
        await WriteAsync("1\n", "debug", "iva", "run_outside_iva");
        Assert.That(_sink.Last,
            Is.EqualTo(new SimCommand("", "debug.iva_run_outside_iva", SimCommand.NoOrdinal, 1)));
    }

    // ---- adopt ------------------------------------------------------------------------------

    [Test]
    public async Task Adopt_TwoTokens_DefaultsToRest()
    {
        _store.Publish(WithIva());
        await WriteAsync("Gemini7 4242\n", "debug", "iva", "adopt");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("debug.iva_adopt"));
            Assert.That(c.Token, Is.EqualTo("Gemini7"));
            Assert.That(c.Values, Is.EqualTo(new[] { 4242d, 0, 0, 0 }));
        });
    }

    [Test]
    public async Task Adopt_FiveTokens_CarriesAStartingVelocity()
    {
        _store.Publish(WithIva());
        await WriteAsync("Gemini7 4242 0.3 -0.1 0\n", "debug", "iva", "adopt");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("debug.iva_adopt"));
            Assert.That(c.Values, Is.EqualTo(new[] { 4242d, 0.3, -0.1, 0 }));
        });
    }

    [TestCase("")]
    [TestCase("Gemini7")]
    [TestCase("Gemini7 notanumber")]
    [TestCase("Gemini7 0", Description = "instance id 0 means 'the vehicle frame' — never a SubPart")]
    [TestCase("Gemini7 4242 0.1")]
    [TestCase("Gemini7 4242 0.1 0.2 nan")]
    public void Adopt_RejectsMalformedLines(string line)
    {
        _store.Publish(WithIva());
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(line + "\n", "debug", "iva", "adopt"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public async Task AdoptAll_AcceptsVesselCountAndTemplateFilter()
    {
        _store.Publish(WithIva());
        await WriteAsync("Gemini7\n", "debug", "iva", "adopt_all");
        var bare = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(bare.Action, Is.EqualTo("debug.iva_adopt_all"));
            Assert.That(bare.Token, Is.EqualTo("Gemini7"));
            Assert.That(bare.Value, Is.EqualTo(0), "0 = up to the configured per-vessel cap");
            Assert.That(bare.Aux, Is.Null);
        });

        await WriteAsync("Gemini7 4 Sardine\n", "debug", "iva", "adopt_all");
        var filtered = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(filtered.Value, Is.EqualTo(4));
            Assert.That(filtered.Aux, Is.EqualTo("Sardine"));
        });
    }

    [TestCase("")]
    [TestCase("Gemini7 -1")]
    [TestCase("Gemini7 x")]
    [TestCase("Gemini7 4 Sardine extra")]
    public void AdoptAll_RejectsMalformedLines(string line)
    {
        _store.Publish(WithIva());
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(line + "\n", "debug", "iva", "adopt_all"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    // ---- registry reads ----------------------------------------------------------------------

    [Test]
    public async Task Object_ReadsBackItsState()
    {
        _store.Publish(WithIva(Object(3)));
        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAsync("debug", "iva", "count"), Is.EqualTo("1\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "vessel"), Is.EqualTo("Gemini7\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "part"), Is.EqualTo("4242\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "name"), Is.EqualTo("Sardine Tin\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "template"),
                Is.EqualTo("CoreIVAPropA_Subpart_DeanSardineA\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "position"), Is.EqualTo("0.1 0.2 0.3\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "velocity"), Is.EqualTo("0.01 0 -0.02\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "angular_velocity"), Is.EqualTo("0 0.5 0\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "mass"), Is.EqualTo("0.12\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "shape"), Is.EqualTo("box\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "size"), Is.EqualTo("0.08 0.05 0.02\n"));
            Assert.That(await ReadAsync("debug", "iva", "3", "asleep"), Is.EqualTo("0\n"));
            // The spec is the adopt-compatible 2-token form (echo it back to re-adopt).
            Assert.That(await ReadAsync("debug", "iva", "3", "spec"), Is.EqualTo("Gemini7 4242\n"));
        });
    }

    [Test]
    public async Task StatsAndInterior_ReadAsStableColumns()
    {
        _store.Publish(WithIva(Object(0)));
        Assert.Multiple(async () =>
        {
            // vessels objects sleeping substeps avg_ms max_ms parked reason
            Assert.That(await ReadAsync("debug", "iva", "stats"), Is.EqualTo("1 1 0 4 0.35 1.2 0 -\n"));
            // vessel triangles source_parts aabb_min aabb_max fallback
            Assert.That(await ReadAsync("debug", "iva", "interior"),
                Is.EqualTo("Gemini7 5120 42 -1 -0.9 -1.2 1 0.9 0.4 0\n"));
        });
    }

    [Test]
    public async Task Help_DocumentsTheMasterSwitch()
    {
        var help = await ReadAsync("debug", "iva", "help");
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Contain("MASTER SWITCH"));
            Assert.That(help, Does.Contain("enabled"));
            Assert.That(help, Does.Contain("adopt_all"));
        });
    }

    [Test]
    public async Task MissingObject_IsEnoent()
    {
        _store.Publish(WithIva()); // enabled, but nothing adopted
        var dirFid = await WalkAsync("debug", "iva");
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WalkAsync(dirFid, _nextFid++, "7"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    // ---- per-object controls (keyed by id in the command ordinal) -----------------------------

    [Test]
    public async Task Nudge_WritesVectorKeyedById()
    {
        _store.Publish(WithIva(Object(5)));
        await WriteAsync("0.5 -0.25 1\n", "debug", "iva", "5", "nudge");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("debug.iva_nudge"));
            Assert.That(c.Ordinal, Is.EqualTo(5), "the object id rides in the ordinal");
            Assert.That(c.Values, Is.EqualTo(new[] { 0.5, -0.25, 1d }));
        });
    }

    [Test]
    public async Task Release_And_Clear_FireTriggers()
    {
        _store.Publish(WithIva(Object(4)));
        await WriteAsync("1\n", "debug", "iva", "4", "release");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "debug.iva_release", 4, 1)));

        await WriteAsync("1\n", "debug", "iva", "clear");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "debug.iva_clear", SimCommand.NoOrdinal, 1)));
    }

    // ---- helpers (mirror ThugLifeTreeTests) --------------------------------------------------

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
