using System.Text;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The control surface walked over a live <c>NinePServer</c> (KSA_GAME_INTEGRATION_PLAN G1):
///     <c>ctl/</c>, writable <c>engines/&lt;n&gt;/active</c>, <c>animations/</c>, <c>solar/</c> and
///     <c>/sim/status/</c>, with a <see cref="FakeCommandSink"/> standing in for the game thread.
/// </summary>
[TestFixture]
public sealed class ControlSurfaceTests
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
        _sink = new FakeCommandSink();
        var root = SimFsTree.Build(_store, _sink, () => "9p 4242\ncontrol on");
        _store.Publish(TestData.Snapshot(1, TestData.Vessel(
            lightsOn: false,
            animations: [new AnimationSnapshot(0, 0, 0, "Retracted", IsSolar: true)])));
        _server = new NinePServer(root);
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

    [Test]
    public async Task ControlFiles_Advertise0644Mode()
    {
        var fid = await WalkAsync("vessels", "by-id", "test-1", "ctl", "ignite");
        var attrs = await _client.GetattrAsync(fid);
        Assert.That(attrs.Mode, Is.EqualTo(0x8000u | 0x1A4u), "writable control file is 0644");
    }

    [Test]
    public async Task CtlIgnite_FiresTriggerCommand()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "ctl", "ignite");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "vessel.ignite", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public async Task EngineActive_IsReadableAndWritable()
    {
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "engines", "0", "active"),
            Is.EqualTo("1\n"), "engine 0 is active in the fixture");
        await WriteAsync("0\n", "vessels", "by-id", "test-1", "engines", "0", "active");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "engine.active", 0, 0)));
    }

    [Test]
    public async Task CtlLights_ReadsMaster_AndWrites()
    {
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "ctl", "lights"), Is.EqualTo("0\n"));
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "ctl", "lights");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "vessel.lights", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public async Task AnimationGoal_WritesFraction()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "animations", "0", "goal");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "animation.goal", 0, 1)));
    }

    [Test]
    public async Task SolarGoal_MapsToUnderlyingAnimationOrdinal()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "solar", "0", "goal");
        // solar/0 is the IsSolar animation whose vessel-level ordinal is 0.
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "animation.goal", 0, 1)));
    }

    [Test]
    public async Task Status_ReportsVersionSamplerAndTransports()
    {
        Assert.That(await ReadAsync("status", "game_version"), Is.EqualTo("test-version\n"));
        Assert.That(await ReadAsync("status", "sampler"), Is.EqualTo("ok 10\n"));
        Assert.That(await ReadAsync("status", "transports"), Is.EqualTo("9p 4242\ncontrol on\n"));
        Assert.That(await ReadAsync("status", "accessors"), Is.EqualTo(""), "no degraded accessors");
    }

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
