using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The write pipe (KSA_GAME_INTEGRATION_PLAN §3.1): enqueue on a transport thread, drain on
///     the game thread, with timeout/denied/abandon/fault semantics.
/// </summary>
[TestFixture]
public sealed class CommandQueueTests
{
    // Phase is derived from the action (SolverActions), so pick an action that maps to the wanted lane:
    // a frame action (engine.active) or a solver action (debug.refill_fuel).
    private static SimCommand Cmd(CommandPhase phase = CommandPhase.Frame)
        => phase == CommandPhase.Solver
            ? new SimCommand("v1", "debug.refill_fuel", SimCommand.NoOrdinal, 1)
            : new SimCommand("v1", "engine.active", 0, 1);

    [Test]
    public async Task Submit_ThenDrain_ExecutesAndReturnsResult()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var submit = queue.SubmitAsync(Cmd(), CancellationToken.None);

        var drained = queue.Drain(CommandPhase.Frame, executor, 64);
        var result = await submit;

        Assert.Multiple(() =>
        {
            Assert.That(drained, Is.EqualTo(1));
            Assert.That(executor.Count, Is.EqualTo(1));
            Assert.That(result.IsSuccess, Is.True);
        });
    }

    [Test]
    public async Task ControlDisabled_DeniesWithoutQueuing()
    {
        var queue = new CommandQueue(controlEnabled: false, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var result = await queue.SubmitAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Denied));
            Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task NotDrained_TimesOut()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromMilliseconds(50));
        var result = await queue.SubmitAsync(Cmd(), CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.TimedOut));
    }

    [Test]
    public async Task TimedOutCommand_IsSkippedByDrain()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromMilliseconds(50));
        var executor = new FakeCommandExecutor();
        var result = await queue.SubmitAsync(Cmd(), CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.TimedOut));

        // A late drain must not execute a command the writer already gave up on.
        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
        Assert.That(executor.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task ExecutorThrow_MapsToFault()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor { Throw = true };
        var submit = queue.SubmitAsync(Cmd(), CancellationToken.None);
        queue.Drain(CommandPhase.Frame, executor, 64);
        var result = await submit;
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Fault));
    }

    [Test]
    public async Task FramePhase_DoesNotDrainSolverCommands()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var submit = queue.SubmitAsync(Cmd(CommandPhase.Solver), CancellationToken.None);

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0), "frame drain skips solver commands");
        Assert.That(queue.Drain(CommandPhase.Solver, executor, 64), Is.EqualTo(1), "solver drain takes them");
        await submit;
    }

    [Test]
    public async Task Drain_RespectsMaxCommands()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var submits = Enumerable.Range(0, 5).Select(_ => queue.SubmitAsync(Cmd(), CancellationToken.None)).ToArray();

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 2), Is.EqualTo(2), "bounded per drain");
        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(3), "remainder next drain");
        await Task.WhenAll(submits);
    }

    // ---- batch submits (/sim/ctl/batch — same-tick atomic groups) --------------------------

    private static SimCommand[] Batch(int count)
        => [.. Enumerable.Range(0, count).Select(i => new SimCommand("v1", "engine.active", i, 1))];

    [Test]
    public async Task SubmitBatch_ExecutesWholeGroupInOneDrain_InOrder()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var submit = queue.SubmitBatchAsync(Batch(3), CancellationToken.None);

        var drained = queue.Drain(CommandPhase.Frame, executor, 64);
        var result = await submit;

        Assert.Multiple(() =>
        {
            Assert.That(drained, Is.EqualTo(3), "the whole group in ONE drain — the same-tick guarantee");
            Assert.That(executor.All.Select(c => c.Ordinal), Is.EqualTo(new[] { 0, 1, 2 }), "in order");
            Assert.That(result.IsSuccess, Is.True);
        });
    }

    [Test]
    public async Task SubmitBatch_NeverSplitsAcrossDrains()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var submit = queue.SubmitBatchAsync(Batch(3), CancellationToken.None);

        // A group executes in full once dequeued, so a tight budget overshoots rather than split.
        Assert.That(queue.Drain(CommandPhase.Frame, executor, 1), Is.EqualTo(3), "atomic beats the bound");
        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0), "nothing left behind");
        await submit;
    }

    [Test]
    public async Task SinglesAndBatch_DrainFifo_InOneDrain()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var before = queue.SubmitAsync(new SimCommand("v1", "engine.active", 100, 1), CancellationToken.None);
        var batch = queue.SubmitBatchAsync(Batch(2), CancellationToken.None);
        var after = queue.SubmitAsync(new SimCommand("v1", "engine.active", 200, 1), CancellationToken.None);

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(4));
        Assert.That(executor.All.Select(c => c.Ordinal), Is.EqualTo(new[] { 100, 0, 1, 200 }), "FIFO preserved");
        await Task.WhenAll(before, batch, after);
    }

    [Test]
    public async Task SubmitBatch_ReportsFirstFailure_AndStillExecutesTheRest()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor
        {
            OnExecute = c => c.Ordinal == 1
                ? new CommandResult(CommandOutcome.Busy, "no can do")
                : CommandResult.Ok,
        };
        var submit = queue.SubmitBatchAsync(Batch(3), CancellationToken.None);
        queue.Drain(CommandPhase.Frame, executor, 64);
        var result = await submit;

        Assert.Multiple(() =>
        {
            Assert.That(executor.Count, Is.EqualTo(3), "batch commands are independent — all execute");
            Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Busy));
            Assert.That(result.Message, Does.Contain("command 2/3"), "the failure names its line");
        });
    }

    [Test]
    public async Task SubmitBatch_MixedPhases_IsInvalid_WithoutQueuing()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var result = await queue.SubmitBatchAsync([Cmd(), Cmd(CommandPhase.Solver)], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Invalid));
            Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
            Assert.That(queue.Drain(CommandPhase.Solver, executor, 64), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task SubmitBatch_Empty_IsInvalid()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var result = await queue.SubmitBatchAsync([], CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Invalid));
    }

    [Test]
    public async Task SubmitBatch_ControlDisabled_Denies()
    {
        var queue = new CommandQueue(controlEnabled: false, debugEnabled: false, TimeSpan.FromSeconds(5));
        var result = await queue.SubmitBatchAsync(Batch(2), CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Denied));
    }

    [Test]
    public async Task SubmitBatch_SolverPhase_DrainsInSolverLane()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        SimCommand[] refills =
        [
            new("v1", "debug.refill_fuel", SimCommand.NoOrdinal, 1),
            new("v2", "debug.refill_fuel", SimCommand.NoOrdinal, 1),
        ];
        var submit = queue.SubmitBatchAsync(refills, CancellationToken.None);

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0), "not in the frame lane");
        Assert.That(queue.Drain(CommandPhase.Solver, executor, 64), Is.EqualTo(2), "the solver lane takes it");
        await submit;
    }

    [Test]
    public async Task SubmitBatch_NotDrained_TimesOut_AndIsSkipped()
    {
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromMilliseconds(50));
        var executor = new FakeCommandExecutor();
        var result = await queue.SubmitBatchAsync(Batch(2), CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.TimedOut));

        // A late drain must not execute a batch the writer already gave up on.
        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
        Assert.That(executor.Count, Is.EqualTo(0));
    }
}
