using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The write pipe (KSA_GAME_INTEGRATION_PLAN §3.1): enqueue on a transport thread, drain on
///     the game thread, with timeout/denied/abandon/fault semantics.
/// </summary>
[TestFixture]
public sealed class CommandQueueTests
{
    private static SimCommand Cmd(CommandPhase phase = CommandPhase.Frame)
        => new("v1", "engine.active", 0, 1, phase);

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
}
