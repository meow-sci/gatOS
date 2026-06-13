using System.Net;
using System.Text;
using System.Text.Json;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.Http.Tests;

/// <summary>
///     The G5 HTTP transport (KSA_GAME_INTEGRATION_PLAN Part 6 T2) exercised over a real loopback
///     socket with <see cref="HttpClient"/>: read projections, the command endpoint with errno→status
///     mapping, debug gating, SSE, and the sim-time long-poll.
/// </summary>
[TestFixture]
public sealed class HttpServerTests
{
    private SnapshotStore _store = null!;
    private RecordingSink _sink = null!;
    private SimHttpServer _server = null!;
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        _store = new SnapshotStore();
        _sink = new RecordingSink();
        _server = new SimHttpServer(_store, _sink, () => "9p 1; http 2");
        await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
        _store.Publish(Snapshot(1, Vessel("v1")));
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Test]
    public async Task Snapshot_SerializesVesselsAndSnakeCase()
    {
        using var json = JsonDocument.Parse(await _client.GetStringAsync("v1/snapshot"));
        var root = json.RootElement;
        Assert.That(root.GetProperty("ut_seconds").GetDouble(), Is.EqualTo(0.1));
        Assert.That(root.GetProperty("vessels")[0].GetProperty("id").GetString(), Is.EqualTo("v1"));
    }

    [Test]
    public async Task Time_ReportsClockFields()
    {
        using var json = JsonDocument.Parse(await _client.GetStringAsync("v1/time"));
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("ut").GetDouble(), Is.EqualTo(0.1));
            Assert.That(json.RootElement.GetProperty("warp").GetDouble(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task VesselTelemetry_IsOneJsonObject_And404WhenGone()
    {
        using var ok = JsonDocument.Parse(await _client.GetStringAsync("v1/vessels/v1/telemetry"));
        Assert.That(ok.RootElement.GetProperty("id").GetString(), Is.EqualTo("v1"));

        var gone = await _client.GetAsync("v1/vessels/ghost/telemetry");
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task OpenApi_IsValidJson()
    {
        using var json = JsonDocument.Parse(await _client.GetStringAsync("v1/openapi.json"));
        Assert.That(json.RootElement.GetProperty("openapi").GetString(), Does.StartWith("3."));
    }

    [Test]
    public async Task Command_OkReachesTheSink()
    {
        var response = await PostCommandAsync("""{"vessel_id":"v1","action":"engine.active","ordinal":0,"value":1}""");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("v1", "engine.active", 0, 1)));
    }

    [Test]
    public async Task Command_VectorAndToken_AreCarried()
    {
        await PostCommandAsync("""{"vessel_id":"v1","action":"vessel.burn","values":[100,1,2,3]}""");
        Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 100d, 1d, 2d, 3d }));
        await PostCommandAsync("""{"vessel_id":"v1","action":"vessel.attitude_mode","token":"Prograde"}""");
        Assert.That(_sink.Last!.Token, Is.EqualTo("Prograde"));
    }

    [Test]
    public async Task Command_NonSuccessMapsToStatusAndErrno()
    {
        _sink.Result = new CommandResult(CommandOutcome.NotFound, "vessel gone");
        var response = await PostCommandAsync("""{"vessel_id":"v1","action":"engine.active","value":1}""");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(json.RootElement.GetProperty("errno").GetString(), Is.EqualTo("ENOENT"));
    }

    [Test]
    public async Task Command_DebugActionRoutesToSolverPhase()
    {
        await PostCommandAsync("""{"vessel_id":"v1","action":"debug.refill_battery"}""");
        Assert.That(_sink.Last!.Phase, Is.EqualTo(CommandPhase.Solver));
    }

    [Test]
    public async Task Command_BadJson_Is400()
    {
        var response = await PostCommandAsync("not json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task DebugDisabled_ForbidsDebugCommands()
    {
        var store = new SnapshotStore();
        var sink = new RecordingSink { DebugEnabled = false };
        await using var server = new SimHttpServer(store, sink);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        var response = await client.PostAsync("v1/command",
            new StringContent("""{"vessel_id":"v1","action":"debug.warp","value":10}""", Encoding.UTF8));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task NoControl_PostCommandIsForbidden()
    {
        var store = new SnapshotStore();
        await using var server = new SimHttpServer(store); // read-only
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        var response = await client.PostAsync("v1/command",
            new StringContent("""{"vessel_id":"v1","action":"engine.active","value":1}""", Encoding.UTF8));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task TimeWait_BlocksUntilTargetUt()
    {
        var waitTask = _client.GetStringAsync("v1/time/wait?until=5");
        await Task.Delay(100);
        Assert.That(waitTask.IsCompleted, Is.False);
        _store.Publish(Snapshot(60, Vessel("v1"))); // ut = 6
        using var json = JsonDocument.Parse(await waitTask);
        Assert.That(json.RootElement.GetProperty("reached_ut").GetDouble(), Is.GreaterThanOrEqualTo(5));
    }

    [Test]
    public async Task Events_StreamDeliversNewEvents()
    {
        using var stream = await _client.GetStreamAsync("v1/events");
        using var reader = new StreamReader(stream);
        _store.Publish(Snapshot(2, Vessel("v1")).WithEvent(
            new SimEvent(0.2, "situation-change", "v1", "Landed→Freefall")));

        string? line = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cts.IsCancellationRequested)
        {
            var read = await reader.ReadLineAsync(cts.Token);
            if (read is { Length: > 0 } && read.StartsWith("data:"))
            {
                line = read["data:".Length..].Trim();
                break;
            }
        }

        Assert.That(line, Is.Not.Null);
        using var json = JsonDocument.Parse(line!);
        Assert.That(json.RootElement.GetProperty("type").GetString(), Is.EqualTo("situation-change"));
    }

    private Task<HttpResponseMessage> PostCommandAsync(string body)
        => _client.PostAsync("v1/command", new StringContent(body, Encoding.UTF8, "application/json"));

    private static SimSnapshot Snapshot(long seq, params VesselSnapshot[] vessels)
        => new(seq, seq * 0.1, 1, vessels.Length > 0 ? vessels[0].Id : null, vessels, [], "test", 10, []);

    private static VesselSnapshot Vessel(string id) => new(
        id, id, "Freefall", new double3Snap(0, 0, 0), 0, 0, 0, 0, 0, new QuatSnap(0, 0, 0, 1),
        new double3Snap(0, 0, 0), 0, 0, 1, 1, 0, null, [], [], null, "Kerth", false, []);

    /// <summary>An <see cref="ICommandSink"/> double recording the last command and a configurable result.</summary>
    private sealed class RecordingSink : ICommandSink
    {
        public bool ControlEnabled { get; init; } = true;
        public bool DebugEnabled { get; init; } = true;
        public CommandResult Result { get; set; } = CommandResult.Ok;
        public SimCommand? Last { get; private set; }

        public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        {
            Last = command;
            return Task.FromResult(Result);
        }
    }
}

internal static class SnapshotTestExtensions
{
    internal static SimSnapshot WithEvent(this SimSnapshot snapshot, SimEvent simEvent)
        => snapshot with { NewEvents = [simEvent] };
}
