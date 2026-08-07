using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The schedule player (plans/CAMERA_CONTROLS_PLAN.md §3.2): deadline ordering with stability at
///     equal offsets, the archetype-derived catch-up policy (every trigger fires, state controls
///     coalesce per path, cross-path order preserved), loop/scrub cursor handling, and the zero-alloc
///     idle tick.
/// </summary>
[TestFixture]
public sealed class SchedulerTests
{
    /// <summary>Discards every result; the coalescing policy is what is under test here.</summary>
    private sealed class NullObserver : IPostObserver
    {
        public void OnCommandResult(int token, CommandResult result)
        {
        }
    }

    private static ScheduleEntry State(double ms, string path, double value)
        => new(ms, path, new SimCommand("", "engine.active", 0, value), IsTrigger: false);

    private static ScheduleEntry Trigger(double ms, string path)
        => new(ms, path, new SimCommand("", "vessel.stage", SimCommand.NoOrdinal, 1), IsTrigger: true);

    private static (Scheduler Player, PlaybackClock Clock) Build(params ScheduleEntry[] entries)
    {
        var schedule = new Schedule("s", "", ClockBase.Render, 1, loop: false, entries);
        var clock = new PlaybackClock(ClockBase.Render) { DurationMs = schedule.DurationMs };
        clock.Start();
        return (new Scheduler(schedule, clock), clock);
    }

    private static List<DueCommand> Tick(Scheduler player, out int dropped)
    {
        var due = new List<DueCommand>();
        dropped = player.Tick(due, new NullObserver());
        return due;
    }

    // ---- ordering ---------------------------------------------------------------------------

    [Test]
    public void Commit_SortsByDeadline_StablyWithinEqualOffsets()
    {
        var schedule = new Schedule("s", "", ClockBase.Render, 1, false,
        [
            State(100, "b", 1),
            State(0, "first", 1),
            State(0, "second", 1),
            State(0, "third", 1),
        ]);

        Assert.That(schedule.Entries.Select(e => e.Path),
            Is.EqualTo(new[] { "first", "second", "third", "b" }),
            "authored order must survive within one deadline — that is what makes a 0-offset group a batch");
        Assert.That(schedule.DurationMs, Is.EqualTo(100));
    }

    [Test]
    public void EqualDeadlines_FireInAuthoredOrder()
    {
        var (player, clock) = Build(Trigger(0, "a"), Trigger(0, "b"), Trigger(0, "c"));
        clock.Advance(1, 0, 0);

        var due = Tick(player, out _);
        Assert.That(due.Select(d => d.Token), Is.EqualTo(new[] { 0, 1, 2 }));
    }

    // ---- catch-up / coalescing --------------------------------------------------------------

    [Test]
    public void Hitch_FiresEveryTrigger_ButOnlyTheLastStateWritePerPath()
    {
        var (player, clock) = Build(
            State(0, "fov", 10),
            Trigger(1, "stage"),
            State(2, "fov", 20),
            State(3, "throttle", 0.5),
            Trigger(4, "ignite"),
            State(5, "fov", 30));

        clock.Advance(500, 0, 0); // a 500 ms hitch: everything comes due at once

        var due = Tick(player, out var dropped);
        Assert.Multiple(() =>
        {
            // Cross-path order is preserved; superseded fov writes (indices 0 and 2) are skipped.
            Assert.That(due.Select(d => d.Token), Is.EqualTo(new[] { 1, 3, 4, 5 }));
            Assert.That(dropped, Is.EqualTo(2));
            Assert.That(due.Count, Is.LessThan(6), "coalescing bounds the burst by distinct leaves");
        });
    }

    [Test]
    public void Hitch_BoundsTheEmitCountByDistinctLeaves_NotEntries()
    {
        // 3 600 authored frames of a 60 Hz light show, all due at once after a stall.
        var entries = new List<ScheduleEntry>();
        for (var i = 0; i < 1200; i++)
        {
            entries.Add(State(i, "lights/0/brightness", i / 1200.0));
            entries.Add(State(i, "lights/1/brightness", i / 1200.0));
            entries.Add(State(i, "lights/2/brightness", i / 1200.0));
        }

        var (player, clock) = Build([.. entries]);
        clock.Advance(5000, 0, 0);

        var due = Tick(player, out var dropped);
        Assert.Multiple(() =>
        {
            Assert.That(due, Has.Count.EqualTo(3), "one surviving write per distinct leaf");
            Assert.That(dropped, Is.EqualTo(3597));
            Assert.That(player.Pending, Is.EqualTo(0));
        });
    }

    [Test]
    public void SingleDueEntry_TakesTheFastPath()
    {
        var (player, clock) = Build(State(0, "a", 1), State(100, "a", 2));
        clock.Advance(10, 0, 0);

        var due = Tick(player, out var dropped);
        Assert.Multiple(() =>
        {
            Assert.That(due, Has.Count.EqualTo(1));
            Assert.That(dropped, Is.EqualTo(0));
            Assert.That(player.Pending, Is.EqualTo(1));
        });
    }

    // ---- loop / scrub -----------------------------------------------------------------------

    [Test]
    public void Loop_DrainsTheTailThenRestartsTheCursor()
    {
        var (player, clock) = Build(Trigger(0, "a"), Trigger(50, "b"), Trigger(100, "c"));
        clock.Loop = true;

        clock.Advance(10, 0, 0);
        Assert.That(Tick(player, out _).Select(d => d.Token), Is.EqualTo(new[] { 0 }));

        // Wrap past the end: the old cycle's tail (b, c) fires before the new cycle's head (a).
        clock.Advance(100, 0, 0); // 110 -> wraps to 10
        var due = Tick(player, out _);
        Assert.Multiple(() =>
        {
            Assert.That(clock.LoopCount, Is.EqualTo(1));
            Assert.That(due.Select(d => d.Token), Is.EqualTo(new[] { 1, 2, 0 }));
            Assert.That(player.Pending, Is.EqualTo(2), "the cursor restarted just past entry 0");
        });
    }

    [Test]
    public void Scrub_ReSeatsTheCursor_AndFiresNothing()
    {
        var (player, clock) = Build(Trigger(0, "a"), Trigger(50, "b"), Trigger(100, "c"));

        clock.Scrub(75);
        var due = Tick(player, out var dropped);
        Assert.Multiple(() =>
        {
            Assert.That(due, Is.Empty, "a seek is navigation, not playback");
            Assert.That(dropped, Is.EqualTo(0));
            Assert.That(player.Pending, Is.EqualTo(1), "only entry c is still ahead of 75 ms");
        });

        clock.Advance(30, 0, 0); // 105 ms
        Assert.That(Tick(player, out _).Select(d => d.Token), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void ScrubBackwards_ReplaysFromThere()
    {
        var (player, clock) = Build(Trigger(0, "a"), Trigger(50, "b"));
        clock.Advance(100, 0, 0);
        Tick(player, out _);
        Assert.That(player.Pending, Is.EqualTo(0));

        clock.Scrub(0);
        Tick(player, out _);
        Assert.That(player.Pending, Is.EqualTo(1), "entry a is at exactly 0, so it is behind the cursor");
    }

    // ---- completion -------------------------------------------------------------------------

    [Test]
    public void IsComplete_OnlyOnceEverythingFiredAndTheClockReachedTheEnd()
    {
        var (player, clock) = Build(Trigger(0, "a"), Trigger(100, "b"));
        Assert.That(player.IsComplete, Is.False);

        clock.Advance(100, 0, 0);
        Tick(player, out _);
        Assert.That(player.IsComplete, Is.True);
    }

    [Test]
    public void LoopingPlayer_IsNeverComplete()
    {
        var (player, clock) = Build(Trigger(0, "a"));
        clock.Loop = true;
        clock.Advance(100, 0, 0);
        Tick(player, out _);
        Assert.That(player.IsComplete, Is.False);
    }

    [Test]
    public void EmptySchedule_TicksToNothing()
    {
        var (player, clock) = Build();
        clock.Advance(100, 0, 0);
        Assert.Multiple(() =>
        {
            Assert.That(Tick(player, out var dropped), Is.Empty);
            Assert.That(dropped, Is.EqualTo(0));
        });
    }

    // ---- the steady state must not allocate -------------------------------------------------

    [Test]
    public void IdleTick_AllocatesNothing()
    {
        var (player, clock) = Build(Trigger(1_000_000, "a"));
        var observer = new NullObserver();
        var due = new List<DueCommand>(16);

        // Warm up: JIT the whole path (including List<T>'s first growth) before measuring.
        for (var i = 0; i < 64; i++)
            player.Tick(due, observer);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            clock.Advance(1, 0, 0);
            player.Tick(due, observer);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Multiple(() =>
        {
            Assert.That(due, Is.Empty);
            // The scheduler's idle path is branch-only. The tiny budget absorbs any test-host
            // instrumentation noise; a real regression (a dictionary or list per tick) is orders of
            // magnitude larger. Same discipline as the PerfStat "Sample alloc" tripwire.
            Assert.That(allocated, Is.LessThan(64), $"idle ticks allocated {allocated} bytes");
        });
    }
}
