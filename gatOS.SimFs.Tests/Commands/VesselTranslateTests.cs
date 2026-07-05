using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The manual RCS translation control <c>ctl/translate</c>: writes parse a 3-vector whose
///     <b>signs</b> command bang-bang thrust along the body axes (+x = forward/nose, +y = right,
///     +z = down) into the <c>vessel.translate</c> Frame command; the command latches until
///     rewritten (<c>0 0 0</c> stops). Reads render the latched command from the snapshot
///     (<see cref="VesselSnapshot.TranslateCmd"/>). Arity/finiteness re-validation on the
///     JSON-command path is pinned game-free via <see cref="TranslateRules"/>.
/// </summary>
[TestFixture]
public sealed class VesselTranslateTests
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
        _store.Publish(TestData.Snapshot(1,
            TestData.Vessel() with { TranslateCmd = new double3Snap(1, 0, -1) }));
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
    public async Task Translate_ReadsBackTheLatchedCommand()
    {
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "ctl", "translate"),
            Is.EqualTo("1 0 -1\n"), "the snapshot's latched body-axis signs render as the read-back");
    }

    [Test]
    public async Task Translate_WriteBuildsFrameCommand()
    {
        await WriteAsync("1 0 -1\n", "vessels", "by-id", "test-1", "ctl", "translate");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.VesselId, Is.EqualTo("test-1"));
            Assert.That(_sink.Last!.Action, Is.EqualTo("vessel.translate"));
            Assert.That(_sink.Last!.Ordinal, Is.EqualTo(SimCommand.NoOrdinal));
            Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 1.0, 0.0, -1.0 }));
            Assert.That(_sink.Last!.Phase, Is.EqualTo(CommandPhase.Frame),
                "same _manualControlInputs field as ctl/throttle — Frame phase, not solver");
        });
    }

    [Test]
    public async Task Translate_MagnitudesAreCarriedVerbatim()
    {
        // Only the SIGNS drive the flags (bang-bang), but the command carries the numbers as
        // written — the actuator does the sign-quantizing, keeping the parse layer dumb.
        await WriteAsync("0.5 0 -2\n", "vessels", "by-id", "test-1", "ctl", "translate");
        Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 0.5, 0.0, -2.0 }));
    }

    [Test]
    public async Task Translate_AllStop_IsAValidCommand()
    {
        await WriteAsync("0 0 0\n", "vessels", "by-id", "test-1", "ctl", "translate");
        Assert.That(_sink.Last!.Action, Is.EqualTo("vessel.translate"));
        Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 0.0, 0.0, 0.0 }));
    }

    [TestCase("\n", Description = "empty")]
    [TestCase("1 0\n", Description = "too few components")]
    [TestCase("1 2 3 4\n", Description = "too many components")]
    [TestCase("a b c\n", Description = "unparseable")]
    [TestCase("1 nan 0\n", Description = "non-finite component")]
    [TestCase("1 0 inf\n", Description = "non-finite component")]
    public async Task Translate_MalformedLine_FailsEinvalWithoutSubmitting(string input)
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync(input, "vessels", "by-id", "test-1", "ctl", "translate"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
        Assert.That(_sink.Submits, Is.Zero, "a parse failure never reaches the command sink");
        await Task.CompletedTask;
    }

    // The actuator re-validates through the same game-free rule — the HTTP/MQTT command paths
    // reach it without the 9p parse — so the EINVAL boundary is pinned here without game assemblies.
    [Test]
    public void TranslateRules_ValidatesArityAndFiniteness()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TranslateRules.Validate([1, 0, -1]), Is.Null);
            Assert.That(TranslateRules.Validate([0, 0, 0]), Is.Null, "all-stop is valid");
            Assert.That(TranslateRules.Validate([0.5, 0, -2]), Is.Null, "magnitudes are allowed (signs used)");
            Assert.That(TranslateRules.Validate([1, 0]), Is.Not.Null, "wrong arity");
            Assert.That(TranslateRules.Validate([]), Is.Not.Null, "empty");
            Assert.That(TranslateRules.Validate([1, double.NaN, 0]), Is.Not.Null, "NaN");
            Assert.That(TranslateRules.Validate([1, 0, double.NegativeInfinity]), Is.Not.Null, "inf");
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
