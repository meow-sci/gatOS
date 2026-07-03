using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The per-vessel render-distance override node <c>vessels/by-id/&lt;id&gt;/always_render</c>
///     (ported from the unscience <c>i-feel-seen</c> mod): a first-class read/write vessel node
///     deliberately placed outside <c>/sim/debug</c>, like <c>scale</c>. Reads render the snapshot
///     mark; writes build the <c>vessel.always_render</c> Frame command (a <c>0</c>/<c>1</c> flag —
///     anything else fails the write with EINVAL at the control-file parse).
/// </summary>
[TestFixture]
public sealed class VesselAlwaysRenderTests
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
        _server = new NinePServer(SimFsTree.Build(_store, _sink, () => "test"));
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
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
    public async Task AlwaysRender_ReadsBackSnapshotMark()
    {
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "always_render"), Is.EqualTo("0\n"),
            "unmarked by default");
        _store.Publish(TestData.Snapshot(2, TestData.Vessel(alwaysRender: true)));
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "always_render"), Is.EqualTo("1\n"),
            "a marked vessel reads back 1");
    }

    [Test]
    public async Task AlwaysRender_WritesBuildFrameFlagCommand()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "always_render");
        Assert.That(_sink.Last,
            Is.EqualTo(new SimCommand("test-1", "vessel.always_render", SimCommand.NoOrdinal, 1)));
        Assert.That(_sink.Last!.Phase, Is.EqualTo(CommandPhase.Frame),
            "a render-registry mutation is Frame-phase work, not solver state");

        await WriteAsync("0\n", "vessels", "by-id", "test-1", "always_render");
        Assert.That(_sink.Last,
            Is.EqualTo(new SimCommand("test-1", "vessel.always_render", SimCommand.NoOrdinal, 0)));
    }

    [TestCase("2\n")]
    [TestCase("-1\n")]
    [TestCase("abc\n")]
    [TestCase("\n")]
    public async Task AlwaysRender_NonFlagInput_FailsEinvalWithoutSubmitting(string input)
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(input, "vessels", "by-id", "test-1", "always_render"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
        Assert.That(_sink.Submits, Is.Zero, "a parse failure never reaches the command sink");
        await Task.CompletedTask;
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
