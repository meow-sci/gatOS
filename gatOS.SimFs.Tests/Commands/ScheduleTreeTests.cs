using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The scheduler surface (<c>/sim/ctl/timed_batch</c> + <c>/sim/ctl/schedules/</c>) walked over a
///     live <see cref="NinePServer"/>: every leaf's archetype and read-back, the exact
///     <see cref="SimCommand"/> each control builds (global addressing — empty vessel, id in
///     <c>Token</c>, Frame phase), the EINVAL boundaries, config-gated removal, and a
///     <c>timed_batch</c> commit reaching a real control leaf end to end.
/// </summary>
[TestFixture]
public sealed class ScheduleTreeTests
{
    private SnapshotStore _snapshots = null!;
    private FakeCommandSink _sink = null!;
    private ScheduleStore _schedules = null!;
    private NinePServer _server = null!;
    private NinePTestClient _client = null!;
    private uint _nextFid;

    [SetUp]
    public async Task SetUp()
    {
        _snapshots = new SnapshotStore();
        _sink = new FakeCommandSink { DebugEnabled = true };
        _schedules = new ScheduleStore(new ScheduleLimits(MaxLive: 4));
        _server = new NinePServer(SimFsTree.Build(_snapshots, _sink, () => "9p 4242", schedules: _schedules));
        await _server.StartAsync();
        _client = await NinePTestClient.ConnectAsync(_server.Port);
        await _client.VersionAsync();
        await _client.AttachAsync(0);
        _nextFid = 1;
        _snapshots.Publish(TestData.Snapshot(1, TestData.FullVessel()));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    /// <summary>Commits a schedule through the real <c>timed_batch</c> leaf and activates it.</summary>
    private async Task CommitAsync(string body)
    {
        await WriteAsync(body, "ctl", "timed_batch");
        _schedules.Activate();
    }

    // ---- presence & gating -------------------------------------------------------------------

    [Test]
    public async Task Ctl_ExposesBatchTimedBatchAndTheRegistry()
    {
        var names = await ListAsync("ctl");
        Assert.That(names, Is.EquivalentTo(new[] { "batch", "timed_batch", "schedules" }));

        var registry = await ListAsync("ctl", "schedules");
        Assert.That(registry, Is.EquivalentTo(new[] { "help", "clear", "count" }));
    }

    [Test]
    public async Task NoScheduleStore_RemovesTheWholeSurface_ButKeepsBatch()
    {
        // schedule_enabled=false wires no store, so the SPEC stays truthful: nothing to discover.
        await using var server = new NinePServer(SimFsTree.Build(new SnapshotStore(), _sink, () => "9p 1"));
        await server.StartAsync();
        await using var client = await NinePTestClient.ConnectAsync(server.Port);
        await client.VersionAsync();
        await client.AttachAsync(0);

        var fid = 1u;
        var qids = await client.WalkAsync(0, fid, ["ctl"]);
        Assert.That(qids, Has.Length.EqualTo(1));
        await client.LopenAsync(fid);
        var names = (await client.ReaddirAllAsync(fid)).Select(e => e.Name).ToArray();
        await client.ClunkAsync(fid);

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("batch"));
            Assert.That(names, Does.Not.Contain("timed_batch"));
            Assert.That(names, Does.Not.Contain("schedules"));
        });
    }

    [Test]
    public async Task Count_TracksTheRegistry()
    {
        Assert.That(await ReadAsync("ctl", "schedules", "count"), Is.EqualTo("0\n"));
        await CommitAsync("@id a\n0 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");
        Assert.That(await ReadAsync("ctl", "schedules", "count"), Is.EqualTo("1\n"));
    }

    [Test]
    public async Task Help_IsReadable()
    {
        Assert.That(await ReadAsync("ctl", "schedules", "help"), Does.Contain("timed_batch"));
    }

    [Test]
    public async Task TimedBatch_ReadsAUsageHint()
    {
        Assert.That(await ReadAsync("ctl", "timed_batch"), Does.StartWith("#").And.Contain("commit"));
    }

    // ---- per-player status leaves ------------------------------------------------------------

    [Test]
    public async Task EntryDir_RendersEveryStatusLeaf()
    {
        await CommitAsync(
            "@id launch\n@group take\n@clock wall\n@rate 2\n@loop 1\n"
            + "0 vessels/by-id/test-1/ctl/throttle 0.5\n"
            + "2500 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");

        var names = await ListAsync("ctl", "schedules", "launch");
        Assert.That(names, Is.EquivalentTo(new[]
        {
            "kind", "group", "state", "t", "duration", "pending", "dropped", "clock", "last_error",
            "pause", "scrub", "rate", "loop", "stop", "remove",
        }));

        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "kind"), Is.EqualTo("schedule\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "group"), Is.EqualTo("take\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "state"), Is.EqualTo("running\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "t"), Is.EqualTo("0.0\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "duration"), Is.EqualTo("2500.0\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "pending"), Is.EqualTo("2\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "dropped"), Is.EqualTo("0\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "clock"), Is.EqualTo("wall\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "last_error"), Is.EqualTo("-\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "rate"), Is.EqualTo("2\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "loop"), Is.EqualTo("1\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "launch", "pause"), Is.EqualTo("0\n"));
        });
    }

    [Test]
    public async Task Ungrouped_RendersGroupAsDash()
    {
        await CommitAsync("@id solo\n0 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");
        Assert.That(await ReadAsync("ctl", "schedules", "solo", "group"), Is.EqualTo("-\n"));
    }

    [Test]
    public async Task StatusLeavesAreLive_NotSnapshotMemoized()
    {
        await CommitAsync("@id seq\n1000 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");
        Assert.That(await ReadAsync("ctl", "schedules", "seq", "t"), Is.EqualTo("0.0\n"));

        // No snapshot is published: a memoized leaf would still read 0.0 here.
        _schedules.AdvanceAll(250, 0, 0);
        Assert.That(await ReadAsync("ctl", "schedules", "seq", "t"), Is.EqualTo("250.0\n"));
    }

    // ---- controls build the exact command -----------------------------------------------------

    [Test]
    public async Task Pause_BuildsTheGlobalCommandWithTheIdInToken()
    {
        await CommitAsync("@id seq\n0 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");
        await WriteAsync("1\n", "ctl", "schedules", "seq", "pause");

        var command = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(command.Action, Is.EqualTo("schedule.pause"));
            Assert.That(command.VesselId, Is.Empty, "global addressing: a player has no vessel");
            Assert.That(command.Ordinal, Is.EqualTo(SimCommand.NoOrdinal));
            Assert.That(command.Value, Is.EqualTo(1));
            Assert.That(command.Token, Is.EqualTo("seq"));
            Assert.That(command.Phase, Is.EqualTo(CommandPhase.Frame), "no schedule.* action is solver-visible");
        });
    }

    [Test]
    public async Task EveryControl_BuildsItsAction()
    {
        await CommitAsync("@id seq\n0 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");

        await WriteAsync("500\n", "ctl", "schedules", "seq", "scrub");
        Assert.That(_sink.Last, Is.EqualTo(
            new SimCommand("", "schedule.scrub", SimCommand.NoOrdinal, 500) { Token = "seq" }));

        await WriteAsync("2.5\n", "ctl", "schedules", "seq", "rate");
        Assert.That(_sink.Last, Is.EqualTo(
            new SimCommand("", "schedule.rate", SimCommand.NoOrdinal, 2.5) { Token = "seq" }));

        await WriteAsync("1\n", "ctl", "schedules", "seq", "loop");
        Assert.That(_sink.Last, Is.EqualTo(
            new SimCommand("", "schedule.loop", SimCommand.NoOrdinal, 1) { Token = "seq" }));

        await WriteAsync("1\n", "ctl", "schedules", "seq", "stop");
        Assert.That(_sink.Last, Is.EqualTo(
            new SimCommand("", "schedule.stop", SimCommand.NoOrdinal, 1) { Token = "seq" }));

        await WriteAsync("1\n", "ctl", "schedules", "seq", "remove");
        Assert.That(_sink.Last, Is.EqualTo(
            new SimCommand("", "schedule.remove", SimCommand.NoOrdinal, 1) { Token = "seq" }));

        await WriteAsync("1\n", "ctl", "schedules", "clear");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "schedule.clear", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public async Task BadControlValues_AreEinval_AndNeverReachTheSink()
    {
        await CommitAsync("@id seq\n0 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");
        var before = _sink.Submits;

        foreach (var (leaf, value) in new[] { ("pause", "2"), ("loop", "yes"), ("rate", "abc"), ("stop", "0") })
        {
            var ex = Assert.ThrowsAsync<NinePErrorException>(
                async () => await WriteAsync($"{value}\n", "ctl", "schedules", "seq", leaf));
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL), $"{leaf} <- {value}");
        }

        Assert.That(_sink.Submits, Is.EqualTo(before));
    }

    [Test]
    public async Task UnknownPlayer_DoesNotResolve()
    {
        // Standard 9p: a walk that cannot complete returns the qids it managed (a partial walk),
        // which the guest kernel turns into ENOENT.
        var qids = await _client.WalkAsync(0, _nextFid++, ["ctl", "schedules", "nope"]);
        Assert.That(qids, Has.Length.EqualTo(2), "ctl and schedules resolve; 'nope' does not");
    }

    // ---- end to end ---------------------------------------------------------------------------

    [Test]
    public async Task TimedBatch_ReachesAControlLeaf_AndTickYieldsTheExactCommands()
    {
        await CommitAsync("""
                          @id launch
                          0    vessels/by-id/test-1/ctl/throttle 1
                          1000 vessels/by-id/test-1/ctl/ignite   1
                          commit

                          """);

        var due = new List<DueCommand>();
        _schedules.AdvanceAll(16, 16, 16);
        _schedules.Tick(due);
        Assert.Multiple(() =>
        {
            Assert.That(due, Has.Count.EqualTo(1));
            Assert.That(due[0].Command.Action, Is.EqualTo("vessel.throttle"));
            Assert.That(due[0].Command.VesselId, Is.EqualTo("test-1"));
            Assert.That(due[0].Command.Value, Is.EqualTo(1));
            Assert.That(due[0].Token, Is.EqualTo(0), "the entry index, for last_error");
        });

        due.Clear();
        _schedules.AdvanceAll(2000, 2000, 2000);
        _schedules.Tick(due);
        Assert.Multiple(() =>
        {
            Assert.That(due, Has.Count.EqualTo(1));
            Assert.That(due[0].Command.Action, Is.EqualTo("vessel.ignite"));
        });

        Assert.That(await ReadAsync("ctl", "schedules", "launch", "state"), Is.EqualTo("done\n"));
    }

    [Test]
    public async Task StoreExecute_DrivesTheRegistry()
    {
        await CommitAsync("@id seq\n1000 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");

        Assert.Multiple(() =>
        {
            Assert.That(_schedules.Execute(Ctl("schedule.pause", 1, "seq")).IsSuccess, Is.True);
            Assert.That(_schedules.Execute(Ctl("schedule.pause", 2, "seq")).Outcome,
                Is.EqualTo(CommandOutcome.Invalid));
            Assert.That(_schedules.Execute(Ctl("schedule.rate", 1000, "seq")).Outcome,
                Is.EqualTo(CommandOutcome.Invalid));
            Assert.That(_schedules.Execute(Ctl("schedule.scrub", -1, "seq")).Outcome,
                Is.EqualTo(CommandOutcome.Invalid));
            Assert.That(_schedules.Execute(Ctl("schedule.pause", 1, "nope")).Outcome,
                Is.EqualTo(CommandOutcome.NotFound));
        });

        Assert.That(await ReadAsync("ctl", "schedules", "seq", "state"), Is.EqualTo("paused\n"));

        _schedules.Execute(Ctl("schedule.scrub", 400, "seq"));
        Assert.That(await ReadAsync("ctl", "schedules", "seq", "t"), Is.EqualTo("400.0\n"));

        _schedules.Execute(Ctl("schedule.remove", 1, "seq"));
        Assert.Multiple(() =>
        {
            Assert.That(_schedules.Count, Is.EqualTo(0));
            Assert.That(_schedules.IsIdLive("seq"), Is.False, "remove frees the id for reuse");
        });
    }

    [Test]
    public async Task Clear_StopsAndRemovesEverything()
    {
        await CommitAsync("@id a\n0 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");
        await CommitAsync("@id b\n0 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");
        Assert.That(_schedules.Count, Is.EqualTo(2));

        _schedules.Execute(new SimCommand("", "schedule.clear", SimCommand.NoOrdinal, 1));
        Assert.Multiple(() =>
        {
            Assert.That(_schedules.Count, Is.EqualTo(0));
            Assert.That(_schedules.IsIdLive("a"), Is.False);
        });
        Assert.That(await ListAsync("ctl", "schedules"), Is.EquivalentTo(new[] { "help", "clear", "count" }));
    }

    [Test]
    public async Task GroupMembers_MoveTogether()
    {
        await CommitAsync("@id a\n@group take\n1000 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");
        await CommitAsync("@id b\n@group take\n2000 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");

        // One clock per group: AdvanceAll must move it ONCE, not once per member.
        _schedules.AdvanceAll(500, 0, 0);
        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAsync("ctl", "schedules", "a", "t"), Is.EqualTo("500.0\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "b", "t"), Is.EqualTo("500.0\n"));
        });

        // ...and pausing either member holds them both.
        _schedules.Execute(Ctl("schedule.pause", 1, "b"));
        Assert.That(await ReadAsync("ctl", "schedules", "a", "state"), Is.EqualTo("paused\n"));
    }

    [Test]
    public async Task FailedEntry_RecordsTheFirstErrorAndKeepsRunning()
    {
        await CommitAsync(
            "@id seq\n0 vessels/by-id/test-1/ctl/ignite 1\n"
            + "100 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");

        var due = new List<DueCommand>();
        _schedules.AdvanceAll(1, 0, 0);
        _schedules.Tick(due);
        due[0].Observer!.OnCommandResult(due[0].Token, new CommandResult(CommandOutcome.NotFound, "gone"));
        // A second failure must not overwrite the first — the cause is more useful than the symptom.
        due[0].Observer!.OnCommandResult(7, new CommandResult(CommandOutcome.Busy, "later"));

        Assert.Multiple(async () =>
        {
            Assert.That(await ReadAsync("ctl", "schedules", "seq", "state"), Is.EqualTo("failed\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "seq", "last_error"),
                Is.EqualTo("entry 0: ENOENT (gone)\n"));
            Assert.That(await ReadAsync("ctl", "schedules", "seq", "pending"), Is.EqualTo("1\n"),
                "a failed schedule keeps running");
        });
    }

    [Test]
    public async Task Events_ReportTheLifecycle()
    {
        await CommitAsync("@id seq\n0 vessels/by-id/test-1/ctl/ignite 1\ncommit\n");
        var started = _schedules.DrainEvents();
        Assert.Multiple(() =>
        {
            Assert.That(started.Select(e => e.Type), Is.EqualTo(new[] { "schedule.started" }));
            Assert.That(started[0].Detail, Does.Contain("seq").And.Contain("entries=1"));
        });

        var due = new List<DueCommand>();
        _schedules.AdvanceAll(1, 0, 0);
        _schedules.Tick(due);
        Assert.That(_schedules.DrainEvents().Select(e => e.Type), Is.EqualTo(new[] { "schedule.finished" }));
    }

    // ---- helpers (mirror ThugLifeTreeTests) ---------------------------------------------------

    private static SimCommand Ctl(string action, double value, string id)
        => new("", action, SimCommand.NoOrdinal, value) { Token = id };

    private async Task<uint> WalkAsync(params string[] names)
    {
        var fid = _nextFid++;
        var qids = await _client.WalkAsync(0, fid, names);
        Assert.That(qids, Has.Length.EqualTo(names.Length), $"walk {string.Join('/', names)}");
        return fid;
    }

    /// <summary>Directory entry names, without the <c>.</c>/<c>..</c> the 9p server synthesizes.</summary>
    private async Task<string[]> ListAsync(params string[] names)
    {
        var fid = await WalkAsync(names);
        await _client.LopenAsync(fid);
        var entries = (await _client.ReaddirAllAsync(fid))
            .Select(e => e.Name)
            .Where(n => n is not ("." or ".."))
            .ToArray();
        await _client.ClunkAsync(fid);
        return entries;
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
