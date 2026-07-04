using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The BATCH control archetype (<c>/sim/ctl/batch</c>): line grammar, path resolution against
///     the tree, all-or-nothing validation, one-phase enforcement, commit/abort handle semantics,
///     and errno mapping — exercised directly against the write handle (no server).
/// </summary>
[TestFixture]
public sealed class BatchFileTests
{
    private FakeCommandSink _sink = null!;
    private BatchFile _batch = null!;

    [SetUp]
    public void SetUp()
    {
        _sink = new FakeCommandSink();

        // A miniature /sim: per-vessel teleports (Frame), an ignite trigger (Frame), a
        // refill trigger (Solver), a read-only sensor, and the batch file itself at ctl/batch.
        VfsDirectory? root = null;
        VfsFile Teleport(string id, ulong qid) => VectorControlFile.Create("teleport", qid, _sink,
            () => "0 0 0 0 0 0", 6,
            v => new SimCommand(id, "debug.teleport", SimCommand.NoOrdinal, 0) { Values = v });
        var hunter = DelegateDirectory.Fixed("hunter", 2, Teleport("hunter", 3));
        var polaris = DelegateDirectory.Fixed("polaris", 4, Teleport("polaris", 5));
        var debug = DelegateDirectory.Fixed("debug", 6,
            DelegateDirectory.Fixed("vessels", 7, hunter, polaris));
        var ignite = new TriggerFile("ignite", 8, _sink,
            new SimCommand("hunter", "vessel.ignite", SimCommand.NoOrdinal, 1));
        var refill = new TriggerFile("refill_fuel", 9, _sink,
            new SimCommand("hunter", "debug.refill_fuel", SimCommand.NoOrdinal, 1));
        var sensor = new StaticTextFile("radar", 10, () => "5\n");
        _batch = new BatchFile("batch", 11, _sink, () => root!);
        var ctl = DelegateDirectory.Fixed("ctl", 12, _batch);
        root = DelegateDirectory.Fixed("/", 1, debug, ignite, refill, sensor, ctl);
    }

    private static Task<uint> WriteAsync(IVfsWritableFileHandle handle, string text)
        => handle.WriteAsync(0, Encoding.UTF8.GetBytes(text), CancellationToken.None).AsTask();

    [Test]
    public async Task Commit_SubmitsOneGroup_InOrder()
    {
        using var handle = _batch.OpenWrite();
        await WriteAsync(handle,
            "debug/vessels/hunter/teleport 1 2 3 4 5 6\n"
            + "debug/vessels/polaris/teleport 7 8 9 10 11 12\n"
            + "commit\n");

        Assert.Multiple(() =>
        {
            Assert.That(_sink.Submits, Is.EqualTo(1), "ONE grouped submit, not one per line");
            Assert.That(_sink.LastBatch, Has.Count.EqualTo(2));
            Assert.That(_sink.LastBatch![0].VesselId, Is.EqualTo("hunter"));
            Assert.That(_sink.LastBatch![0].Values, Is.EqualTo(new[] { 1d, 2d, 3d, 4d, 5d, 6d }));
            Assert.That(_sink.LastBatch![1].VesselId, Is.EqualTo("polaris"));
            Assert.That(_sink.LastBatch![1].Values, Is.EqualTo(new[] { 7d, 8d, 9d, 10d, 11d, 12d }));
        });
    }

    [Test]
    public async Task SplitWrites_AccumulateUntilCommit()
    {
        using var handle = _batch.OpenWrite();
        await WriteAsync(handle, "debug/vessels/hunter/teleport 1 2 3 4 5 6\n");
        await WriteAsync(handle, "debug/vessels/polaris/teleport 7 8 9 10 11 12\n");
        Assert.That(_sink.Submits, Is.EqualTo(0), "nothing fires before commit");

        await WriteAsync(handle, "commit\n");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Submits, Is.EqualTo(1));
            Assert.That(_sink.LastBatch, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task CommentsAndBlankLines_AreSkipped()
    {
        using var handle = _batch.OpenWrite();
        await WriteAsync(handle, "# formation setup\n\nignite 1\n\ncommit\n");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.LastBatch, Has.Count.EqualTo(1));
            Assert.That(_sink.LastBatch![0].Action, Is.EqualTo("vessel.ignite"));
        });
    }

    [Test]
    public async Task PathSpellings_BareSlashAndSimRooted_AllResolve()
    {
        using var handle = _batch.OpenWrite();
        await WriteAsync(handle,
            "/sim/debug/vessels/hunter/teleport 1 2 3 4 5 6\n"
            + "/ignite 1\n"
            + "commit\n");
        Assert.That(_sink.LastBatch, Has.Count.EqualTo(2));
    }

    [Test]
    public void UnknownPath_IsEnoent_AndNothingSubmits()
    {
        using var handle = _batch.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(
            () => WriteAsync(handle, "debug/vessels/nope/teleport 1 2 3 4 5 6\ncommit\n"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public void NonControlTarget_IsEinval()
    {
        using var handle = _batch.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(
            () => WriteAsync(handle, "radar 5\ncommit\n"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public void BadPayload_FailsTheWholeBatch_BeforeAnySubmit()
    {
        using var handle = _batch.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(
            () => WriteAsync(handle, "ignite 1\ndebug/vessels/hunter/teleport 1 2\ncommit\n"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL), "wrong teleport arity");
            Assert.That(_sink.Submits, Is.EqualTo(0), "all-or-nothing: the valid ignite line did not fire");
        });
    }

    [Test]
    public void EmptyBatch_IsEinval()
    {
        using var handle = _batch.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteAsync(handle, "commit\n"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public void MixedPhases_AreEinval()
    {
        using var handle = _batch.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(
            () => WriteAsync(handle, "ignite 1\nrefill_fuel 1\ncommit\n"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL),
                "vessel.ignite is Frame-phase, debug.refill_fuel is Solver-phase");
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public void FailedResult_MapsOutcomeToErrno()
    {
        _sink.Result = new CommandResult(CommandOutcome.Busy, "already fired");
        using var handle = _batch.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteAsync(handle, "ignite 1\ncommit\n"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EBUSY));
    }

    [Test]
    public async Task CloseWithoutCommit_DiscardsTheBatch()
    {
        var handle = _batch.OpenWrite();
        await WriteAsync(handle, "ignite 1\ndebug/vessels/hunter/teleport 1 2 3 4 5 6\n");
        handle.Dispose();
        Assert.That(_sink.Submits, Is.EqualTo(0), "no commit = abort");
    }

    [Test]
    public async Task UnterminatedTrailingCommit_FiresBestEffortOnClose()
    {
        var handle = _batch.OpenWrite();
        await WriteAsync(handle, "ignite 1\ncommit"); // printf-style: no final newline
        handle.Dispose();
        Assert.That(() => _sink.Submits, Is.EqualTo(1).After(2000, 10));
        Assert.That(_sink.LastBatch, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task LinesPastTheCommit_AreIgnored()
    {
        using var handle = _batch.OpenWrite();
        await WriteAsync(handle, "ignite 1\ncommit\nignite 1\ncommit\n");
        await WriteAsync(handle, "ignite 1\ncommit\n");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Submits, Is.EqualTo(1), "one batch per open handle");
            Assert.That(_sink.LastBatch, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void OverMaxCommands_IsEinval()
    {
        var text = new StringBuilder();
        for (var i = 0; i <= BatchFile.MaxCommands; i++)
            text.Append("ignite 1\n");
        text.Append("commit\n");

        using var handle = _batch.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteAsync(handle, text.ToString()));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public void OversizeBuffer_IsEinval()
    {
        using var handle = _batch.OpenWrite();
        var oversize = new string('#', BatchFile.MaxBytes + 1);
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteAsync(handle, oversize));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public async Task Read_ReturnsUsageHint()
    {
        using var handle = _batch.Open();
        var bytes = await handle.ReadAsync(0, 4096, CancellationToken.None);
        var text = Encoding.UTF8.GetString(bytes.Span);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.StartWith("#").And.EndWith("\n"));
            Assert.That(_batch.Size, Is.EqualTo(bytes.Length), "Size is truthful (spike rule 1)");
        });
    }

    [Test]
    public async Task DefaultSinkFallback_SubmitsSequentially_AndReportsFirstFailure()
    {
        // A sink that does NOT override SubmitBatchAsync exercises the interface default:
        // sequential submits, every command still submitted after a failure, first failure wins.
        var sink = new SequentialOnlySink
        {
            OnSubmit = c => c.Ordinal == 0
                ? new CommandResult(CommandOutcome.Busy, "first fails")
                : CommandResult.Ok,
        };
        ICommandSink asInterface = sink;
        var result = await asInterface.SubmitBatchAsync(
        [
            new SimCommand("v1", "engine.active", 0, 1),
            new SimCommand("v1", "engine.active", 1, 1),
        ], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(sink.Submitted, Has.Count.EqualTo(2), "the failure does not stop the rest");
            Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Busy));
        });
    }

    private sealed class SequentialOnlySink : ICommandSink
    {
        public bool ControlEnabled => true;
        public bool DebugEnabled => false;
        public List<SimCommand> Submitted { get; } = [];
        public Func<SimCommand, CommandResult>? OnSubmit { get; init; }

        public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        {
            Submitted.Add(command);
            return Task.FromResult(OnSubmit?.Invoke(command) ?? CommandResult.Ok);
        }
    }
}
