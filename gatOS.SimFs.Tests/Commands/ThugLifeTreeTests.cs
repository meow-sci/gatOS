using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The thug-life sunglasses cheat (<c>/sim/debug/thug_life</c>) walked over a live
///     <see cref="NinePServer"/>: the <c>add</c>/<c>clear</c>/<c>count</c> registry ops and the per-entry
///     <c>&lt;id&gt;/{vessel,part,position,rotation,size,visible,remove,spec}</c> controls. A
///     <see cref="FakeCommandSink"/> stands in for the game thread, so these assert the command built and
///     the values read back — never game effects.
/// </summary>
[TestFixture]
public sealed class ThugLifeTreeTests
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

    private static ThugLifeSnapshot Entry(int id) => new(
        id, "Polaris", 4242, new double3Snap(1, 2, 3), new double3Snap(10, 20, 30), 0.975, 0.1875, true);

    // ---- add (create) -----------------------------------------------------------------------

    [Test]
    public async Task Add_TwoTokens_DefaultsTransform()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        await WriteAsync("Polaris 4242\n", "debug", "thug_life", "add");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("debug.thug_life_add"));
            Assert.That(c.Token, Is.EqualTo("Polaris"));
            Assert.That(c.Values, Is.EqualTo(new[] { 4242d, 0, 0, 0, 0, 0, 0, 0.975, 0.1875 }));
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame), "thug_life mutates only the registry");
        });
    }

    [Test]
    public async Task Add_TenTokens_ExplicitTransform()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        await WriteAsync("Polaris 0 0.1 0.2 0.3 10 20 30 1.5 0.5\n", "debug", "thug_life", "add");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("debug.thug_life_add"));
            Assert.That(c.Token, Is.EqualTo("Polaris"));
            Assert.That(c.Values, Is.EqualTo(new[] { 0d, 0.1, 0.2, 0.3, 10, 20, 30, 1.5, 0.5 }),
                "part_iid 0 = body frame");
        });
    }

    [TestCase("Polaris\n")] // too few (1)
    [TestCase("Polaris 4242 1 2 3\n")] // 5 tokens — not 2 or 10
    [TestCase("Polaris -1\n")] // negative part_iid
    [TestCase("Polaris 4.5\n")] // fractional part_iid
    [TestCase("Polaris 0 a b c 0 0 0 1 1\n")] // non-numeric transform
    [TestCase("   \n")] // empty after trim
    public void Add_BadLine_IsEinval_AndDoesNotSubmit(string line)
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(line, "debug", "thug_life", "add"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Add_ReadsEmpty()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        Assert.That(await ReadAsync("debug", "thug_life", "add"), Is.EqualTo("\n"));
    }

    [Test]
    public async Task Help_ReadsConsoleReadme()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        var help = await ReadAsync("debug", "thug_life", "help");
        Assert.Multiple(() =>
        {
            Assert.That(help, Does.Contain("thug_life"));
            Assert.That(help, Does.Contain("> add"), "documents the create command");
            // Documents the position/rotation/size controls with their formats + units.
            Assert.That(help, Does.Contain("position"));
            Assert.That(help, Does.Contain("rotation"));
            Assert.That(help, Does.Contain("size"));
            Assert.That(help, Does.Contain("metres"), "states the position/size unit");
            Assert.That(help, Does.Contain("degrees"), "states the rotation unit");
            Assert.That(help, Does.Contain("Hunter"), "carries the EVA Kitten examples");
            Assert.That(help, Does.Contain("Polaris"));
            Assert.That(help, Does.Contain("Banjo"));
        });
    }

    // ---- registry view (count + per-entry leaves) -------------------------------------------

    [Test]
    public async Task Registry_ProjectsEntries()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()) with { ThugLife = [Entry(0), Entry(3)] });

        var count = await ReadAsync("debug", "thug_life", "count");
        var vessel = await ReadAsync("debug", "thug_life", "3", "vessel");
        var part = await ReadAsync("debug", "thug_life", "3", "part");
        var position = await ReadAsync("debug", "thug_life", "3", "position");
        var rotation = await ReadAsync("debug", "thug_life", "3", "rotation");
        var size = await ReadAsync("debug", "thug_life", "3", "size");
        var visible = await ReadAsync("debug", "thug_life", "3", "visible");
        var spec = await ReadAsync("debug", "thug_life", "3", "spec");
        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo("2\n"));
            Assert.That(vessel, Is.EqualTo("Polaris\n"));
            Assert.That(part, Is.EqualTo("4242\n"));
            Assert.That(position, Is.EqualTo("1 2 3\n"));
            Assert.That(rotation, Is.EqualTo("10 20 30\n"));
            Assert.That(size, Is.EqualTo("0.975 0.1875\n"));
            Assert.That(visible, Is.EqualTo("1\n"));
            // The spec is the write-compatible 10-token form (echo to add to recreate).
            Assert.That(spec, Is.EqualTo("Polaris 4242 1 2 3 10 20 30 0.975 0.1875\n"));
        });
    }

    [Test]
    public async Task MissingEntry_IsEnoent()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel())); // no entries
        var dirFid = await WalkAsync("debug", "thug_life");
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => _client.WalkAsync(dirFid, _nextFid++, "7"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    // ---- per-entry edit controls (keyed by id in the command ordinal) -----------------------

    [Test]
    public async Task Position_WritesVectorKeyedById()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()) with { ThugLife = [Entry(5)] });
        await WriteAsync("0.5 -0.25 1\n", "debug", "thug_life", "5", "position");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("debug.thug_life_position"));
            Assert.That(c.Ordinal, Is.EqualTo(5), "the entry id rides in the ordinal");
            Assert.That(c.Values, Is.EqualTo(new[] { 0.5, -0.25, 1d }));
        });
    }

    [Test]
    public async Task Size_WritesTwoVectorKeyedById()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()) with { ThugLife = [Entry(2)] });
        await WriteAsync("2 0.4\n", "debug", "thug_life", "2", "size");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("debug.thug_life_size"));
            Assert.That(c.Ordinal, Is.EqualTo(2));
            Assert.That(c.Values, Is.EqualTo(new[] { 2d, 0.4 }));
        });
    }

    [Test]
    public async Task Visible_WritesFlagKeyedById()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()) with { ThugLife = [Entry(1)] });
        await WriteAsync("0\n", "debug", "thug_life", "1", "visible");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "debug.thug_life_visible", 1, 0)));
    }

    [Test]
    public async Task Remove_And_Clear_FireTriggers()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()) with { ThugLife = [Entry(4)] });
        await WriteAsync("1\n", "debug", "thug_life", "4", "remove");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "debug.thug_life_remove", 4, 1)));

        await WriteAsync("1\n", "debug", "thug_life", "clear");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "debug.thug_life_clear", SimCommand.NoOrdinal, 1)));
    }

    // ---- helpers (mirror IvaWeldsPartsTreeTests) --------------------------------------------

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
