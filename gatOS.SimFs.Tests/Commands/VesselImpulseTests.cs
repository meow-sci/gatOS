using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The per-vessel one-shot impulse node <c>debug/vessels/&lt;id&gt;/impulse</c>: writes parse
///     <c>"x y z [cci|body] [ns|dv]"</c> (3 finite components, then at most one frame keyword and
///     one unit keyword in any order) into the <c>debug.impulse</c> Frame command — vector in
///     <c>Values</c>, frame in <c>Token</c> (null ⇒ cci), unit in <c>Aux</c> (null ⇒ ns). Reads
///     render the fixed <c>"0 0 0"</c> placeholder (like teleport, no meaningful read-back). The
///     actuator-side re-validation is pinned game-free via <see cref="ImpulseRules"/>.
/// </summary>
[TestFixture]
public sealed class VesselImpulseTests
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
        _sink = new FakeCommandSink { DebugEnabled = true }; // the /sim/debug subtree is debug-gated
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
    public async Task Impulse_ReadsPlaceholder()
    {
        Assert.That(await ReadAsync("debug", "vessels", "test-1", "impulse"), Is.EqualTo("0 0 0\n"),
            "impulse has no meaningful read-back — a fixed placeholder, like teleport");
    }

    [Test]
    public async Task Impulse_BareVector_DefaultsToCciNewtonSeconds()
    {
        await WriteAsync("100 -200 3.5\n", "debug", "vessels", "test-1", "impulse");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.VesselId, Is.EqualTo("test-1"));
            Assert.That(_sink.Last!.Action, Is.EqualTo("debug.impulse"));
            Assert.That(_sink.Last!.Ordinal, Is.EqualTo(SimCommand.NoOrdinal));
            Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 100.0, -200.0, 3.5 }));
            Assert.That(_sink.Last!.Token, Is.Null, "no frame keyword ⇒ the actuator defaults to cci");
            Assert.That(_sink.Last!.Aux, Is.Null, "no unit keyword ⇒ the actuator defaults to ns");
            Assert.That(_sink.Last!.Phase, Is.EqualTo(CommandPhase.Frame),
                "impulse uses the teleport pattern (orbit rebuild), not solver-visible state — Frame phase");
        });
    }

    [Test]
    public async Task Impulse_FrameAndUnitKeywords_RideTokenAndAux()
    {
        await WriteAsync("0 0 5000 body dv\n", "debug", "vessels", "test-1", "impulse");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo("debug.impulse"));
            Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 0.0, 0.0, 5000.0 }));
            Assert.That(_sink.Last!.Token, Is.EqualTo(ImpulseRules.FrameBody));
            Assert.That(_sink.Last!.Aux, Is.EqualTo(ImpulseRules.UnitDv));
        });
    }

    [Test]
    public async Task Impulse_KeywordsAcceptedInAnyOrder()
    {
        await WriteAsync("1 2 3 dv body\n", "debug", "vessels", "test-1", "impulse");
        Assert.That(_sink.Last!.Token, Is.EqualTo(ImpulseRules.FrameBody));
        Assert.That(_sink.Last!.Aux, Is.EqualTo(ImpulseRules.UnitDv));
    }

    [Test]
    public async Task Impulse_ExplicitDefaults_AreCarriedVerbatim()
    {
        await WriteAsync("1 2 3 cci ns\n", "debug", "vessels", "test-1", "impulse");
        Assert.That(_sink.Last!.Token, Is.EqualTo(ImpulseRules.FrameCci));
        Assert.That(_sink.Last!.Aux, Is.EqualTo(ImpulseRules.UnitNs));
    }

    [TestCase("\n", Description = "empty")]
    [TestCase("1 2\n", Description = "too few components")]
    [TestCase("1 2 3 4\n", Description = "a fourth number is not a keyword")]
    [TestCase("a b c\n", Description = "unparseable components")]
    [TestCase("1 nan 3\n", Description = "non-finite component")]
    [TestCase("1 2 inf\n", Description = "non-finite component")]
    [TestCase("1 2 3 warp\n", Description = "unknown keyword")]
    [TestCase("1 2 3 BODY\n", Description = "keywords are lowercase")]
    [TestCase("1 2 3 body cci\n", Description = "duplicate frame keyword")]
    [TestCase("1 2 3 ns dv\n", Description = "duplicate unit keyword")]
    [TestCase("1 2 3 body dv ns\n", Description = "too many tokens")]
    public async Task Impulse_MalformedLine_FailsEinvalWithoutSubmitting(string input)
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(input, "debug", "vessels", "test-1", "impulse"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
        Assert.That(_sink.Submits, Is.Zero, "a parse failure never reaches the command sink");
        await Task.CompletedTask;
    }

    // The actuator re-validates through the same game-free rules — the HTTP/MQTT command paths
    // reach it without the 9p parse — so the EINVAL boundary is pinned here without game assemblies.
    [Test]
    public void ImpulseRules_ValidatesArityFinitenessAndKeywords()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImpulseRules.Validate([1, 2, 3], null, null), Is.Null);
            Assert.That(ImpulseRules.Validate([1, 2, 3], "cci", "ns"), Is.Null);
            Assert.That(ImpulseRules.Validate([1, 2, 3], "body", "dv"), Is.Null);
            Assert.That(ImpulseRules.Validate([1, 2, 3], "", ""), Is.Null,
                "empty keywords mean default, like null (the JSON command path may send either)");
            Assert.That(ImpulseRules.Validate([1, 2], null, null), Is.Not.Null, "wrong arity");
            Assert.That(ImpulseRules.Validate([], null, null), Is.Not.Null, "empty vector");
            Assert.That(ImpulseRules.Validate([1, double.NaN, 3], null, null), Is.Not.Null, "NaN");
            Assert.That(ImpulseRules.Validate([1, 2, double.PositiveInfinity], null, null), Is.Not.Null, "inf");
            Assert.That(ImpulseRules.Validate([1, 2, 3], "enu", null), Is.Not.Null, "unknown frame");
            Assert.That(ImpulseRules.Validate([1, 2, 3], null, "kns"), Is.Not.Null, "unknown unit");
        });
    }

    [Test]
    public void ImpulseRules_KeywordSemantics()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImpulseRules.IsBodyFrame(null), Is.False);
            Assert.That(ImpulseRules.IsBodyFrame("cci"), Is.False);
            Assert.That(ImpulseRules.IsBodyFrame("body"), Is.True);
            Assert.That(ImpulseRules.IsDeltaV(null), Is.False);
            Assert.That(ImpulseRules.IsDeltaV("ns"), Is.False);
            Assert.That(ImpulseRules.IsDeltaV("dv"), Is.True);
        });
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
