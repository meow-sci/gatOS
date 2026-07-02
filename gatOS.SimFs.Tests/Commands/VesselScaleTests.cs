using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The per-vessel model-scale node <c>vessels/by-id/&lt;id&gt;/scale</c> (SCALING_FEATURE_PLAN):
///     a first-class read/write vessel node deliberately placed outside <c>/sim/debug</c>. Reads
///     render the best-effort snapshot value; writes build the one-shot <c>vessel.scale</c> Frame
///     command. Positivity (<c>&gt; 0</c>) is enforced by <see cref="ScaleRules"/> in the actuator;
///     unparseable/non-finite input fails at the control-file parse with EINVAL.
/// </summary>
[TestFixture]
public sealed class VesselScaleTests
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
        _store.Publish(TestData.Snapshot(1, TestData.Vessel(scale: 2.5)));
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
    public async Task Scale_ReadsBackSnapshotValue()
    {
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "scale"), Is.EqualTo("2.5\n"),
            "the best-effort snapshot value renders G9 invariant");
    }

    [Test]
    public async Task Scale_WriteBuildsOneShotFrameCommand()
    {
        await WriteAsync("50000\n", "vessels", "by-id", "test-1", "scale");
        Assert.That(_sink.Last,
            Is.EqualTo(new SimCommand("test-1", "vessel.scale", SimCommand.NoOrdinal, 50000)));
        Assert.That(_sink.Last!.Phase, Is.EqualTo(CommandPhase.Frame),
            "Part.Scale is geometry, not solver state — vessel.scale drains in the Frame phase");
    }

    [Test]
    public async Task Scale_AcceptsTinyAndFractionalFactors()
    {
        await WriteAsync("0.001\n", "vessels", "by-id", "test-1", "scale");
        Assert.That(_sink.Last,
            Is.EqualTo(new SimCommand("test-1", "vessel.scale", SimCommand.NoOrdinal, 0.001)));
    }

    [TestCase("abc\n")]
    [TestCase("\n")]
    [TestCase("nan\n")]
    [TestCase("inf\n")]
    public async Task Scale_UnparseableOrNonFinite_FailsEinvalWithoutSubmitting(string input)
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(input, "vessels", "by-id", "test-1", "scale"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
        Assert.That(_sink.Submits, Is.Zero, "a parse failure never reaches the command sink");
        await Task.CompletedTask;
    }

    // Zero and negative factors parse fine at the control file and are rejected by ScaleRules in
    // the game-coupled actuator (CommandOutcome.Invalid → EINVAL on the write); the rule itself is
    // game-free so the EINVAL boundary is pinned here.
    [TestCase(0, ExpectedResult = false)]
    [TestCase(-5, ExpectedResult = false)]
    [TestCase(double.NaN, ExpectedResult = false)]
    [TestCase(double.PositiveInfinity, ExpectedResult = false)]
    [TestCase(double.Epsilon, ExpectedResult = true)]
    [TestCase(0.001, ExpectedResult = true)]
    [TestCase(1, ExpectedResult = true)]
    [TestCase(100000, ExpectedResult = true)]
    public bool ScaleRules_AllowsOnlyFinitePositiveFactors(double factor) => ScaleRules.IsValid(factor);

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
