using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     <see cref="ScheduleStore"/>'s cap-pressure eviction: completed players persist for scripts to
///     read, but must never wedge <see cref="ScheduleLimits.MaxLive"/> — and the pass that reclaims
///     their slots must never truncate a live take.
/// </summary>
/// <remarks>
///     The regression these fixtures guard is a race, not a policy: the cap counts <i>reserved</i>
///     ids (claimed on a transport thread the instant a commit validates), while any eviction pass
///     can only see <i>activated</i> runners on the game thread. Reclaiming lazily — at the moment a
///     commit trips the cap — therefore always fails that first commit and only succeeds on a retry a
///     frame later. Eviction is consequently eager, in <see cref="ScheduleStore.Activate"/>, so the
///     registry is already under the cap before a commit arrives.
/// </remarks>
[TestFixture]
public sealed class ScheduleEvictionTests
{
    private const int MaxLive = 3;

    private ScheduleStore _store = null!;
    private List<DueCommand> _due = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ScheduleStore(new ScheduleLimits(MaxLive: MaxLive));
        _due = [];
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static ScheduleEntry Entry(double deadlineMs)
        => new(deadlineMs, "vessels/by-id/test-1/ctl/ignite",
            new SimCommand("test-1", "vessel.ignite", SimCommand.NoOrdinal, 1), IsTrigger: true);

    /// <summary>Reserves + submits a schedule (still invisible until the next <c>Activate</c>).</summary>
    private void Submit(string id, double[] deadlines, string group = "", bool loop = false)
        => _store.Submit(new Schedule(_store.ReserveId(id), group, ClockBase.Render, 1, loop,
            deadlines.Select(Entry).ToArray()));

    /// <summary>A one-entry schedule due at t=0 — one tick after activation it is <c>done</c>.</summary>
    private void SubmitOneShot(string id, string group = "") => Submit(id, [0], group);

    /// <summary>One game tick's worth of registry work, in the driver's order.</summary>
    private void GameTick(double renderMs = 0)
    {
        _store.Activate();
        _store.AdvanceAll(renderMs, renderMs, renderMs);
        _due.Clear();
        _store.Tick(_due);
    }

    /// <summary>Fills the registry to the cap with one-shot schedules that have all run to <c>done</c>.</summary>
    private void FillWithCompleted(params string[] ids)
    {
        foreach (var id in ids)
            SubmitOneShot(id);
        GameTick();
        foreach (var id in ids)
            Assert.That(_store.Find(id)!.State, Is.EqualTo(PlaybackState.Done), $"'{id}' should be done");
    }

    private string[] EvictedIds()
        => _store.DrainEvents().Where(e => e.Type == "schedule.evicted").Select(e => e.Detail).ToArray();

    // ---- the regression guard -----------------------------------------------------------------

    [Test]
    public void RegistryFullOfCompletedPlayers_TheNextCommitSucceedsFirstTry()
    {
        FillWithCompleted("s0", "s1", "s2");
        Assert.That(_store.Count, Is.EqualTo(MaxLive));

        // The game keeps ticking, so the pressure is relieved before any commit is attempted...
        _store.Activate();
        Assert.That(_store.Count, Is.EqualTo(MaxLive - 1), "one slot freed, and only one");

        // ...and this is the bug: it used to take a second attempt a frame later.
        Assert.DoesNotThrow(() => SubmitOneShot("extra"));
        _store.Activate();
        Assert.That(_store.Find("extra"), Is.Not.Null);
    }

    [Test]
    public void Eviction_FreesTheIdForReuse()
    {
        FillWithCompleted("s0", "s1", "s2");
        _store.Activate();

        Assert.Multiple(() =>
        {
            Assert.That(_store.IsIdLive("s0"), Is.False, "the reserved id is released, not just the runner");
            Assert.That(_store.Find("s0"), Is.Null);
        });

        // The name is genuinely reusable — a reserved-but-orphaned id would EINVAL here.
        Assert.DoesNotThrow(() => SubmitOneShot("s0"));
    }

    [Test]
    public void StillFullAfterReserving_IsStillEinval()
    {
        // Eviction relieves pressure; it does not raise the cap. Three LIVE players still means EINVAL.
        Submit("a", [1_000_000]);
        Submit("b", [1_000_000]);
        Submit("c", [1_000_000]);
        GameTick();

        var ex = Assert.Throws<VfsErrorException>(() => SubmitOneShot("d"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(ex.Message, Does.Contain("live players"));
        });
    }

    // ---- policy -------------------------------------------------------------------------------

    [Test]
    public void Eviction_IsOldestFirst_AndSparesTheNewest()
    {
        FillWithCompleted("oldest", "middle", "newest");
        _store.Activate();

        Assert.Multiple(() =>
        {
            Assert.That(_store.Find("oldest"), Is.Null, "activation order decides the victim");
            Assert.That(_store.Find("middle"), Is.Not.Null, "eviction stops the instant the cap clears");
            Assert.That(_store.Find("newest"), Is.Not.Null, "the reading a script just started is the last to go");
        });
    }

    [Test]
    public void BelowTheCap_NothingIsEvicted()
    {
        FillWithCompleted("a", "b");

        for (var i = 0; i < 16; i++)
            _store.Activate();

        Assert.Multiple(() =>
        {
            Assert.That(_store.Count, Is.EqualTo(2), "completed players stay visible for scripts to read");
            Assert.That(_store.Find("a")!.State, Is.EqualTo(PlaybackState.Done));
            Assert.That(_store.Find("b")!.State, Is.EqualTo(PlaybackState.Done));
            Assert.That(EvictedIds(), Is.Empty);
        });
    }

    [Test]
    public void FailedButStillRunning_IsNotEvicted()
    {
        // THE correctness assertion. `failed` is latched on the FIRST failing entry and the schedule
        // keeps running, so treating it as terminal would silently truncate a live take — and this
        // one is the oldest player, i.e. the first candidate the pass considers.
        Submit("wounded", [0, 1_000_000]);
        _store.Activate();
        _store.Tick(_due);
        _due[0].Observer!.OnCommandResult(_due[0].Token, new CommandResult(CommandOutcome.NotFound, "gone"));

        SubmitOneShot("g");
        SubmitOneShot("h");
        GameTick();

        var wounded = _store.Find("wounded")!;
        Assert.Multiple(() =>
        {
            Assert.That(wounded.State, Is.EqualTo(PlaybackState.Failed));
            Assert.That(wounded.PendingCount, Is.EqualTo(1), "it still has authored intent to fire");
        });

        _store.Activate();
        Assert.Multiple(() =>
        {
            Assert.That(_store.Find("wounded"), Is.Not.Null, "a failed-but-running take is not finished");
            Assert.That(_store.Find("g"), Is.Null, "the pass skipped past it to the oldest finished player");
            Assert.That(_store.Find("h"), Is.Not.Null);
        });
    }

    [Test]
    public void FailedAndExhausted_IsEvicted()
    {
        // The other half of the rule: once a failed player is out of entries, not looping and past
        // its own duration, it can never fire again and is a legitimate victim.
        Submit("spent", [0]);
        _store.Activate();
        _store.Tick(_due);
        _due[0].Observer!.OnCommandResult(_due[0].Token, new CommandResult(CommandOutcome.NotFound, "gone"));
        Assert.That(_store.Find("spent")!.State, Is.EqualTo(PlaybackState.Failed));

        // Reserving the last two ids is enough to put the registry at the cap — the eviction pass
        // runs before the drain, so "spent" is gone on the very tick that activates its successors.
        SubmitOneShot("g");
        SubmitOneShot("h");
        _store.Activate();
        Assert.That(_store.Find("spent"), Is.Null);
    }

    [Test]
    public void LoopingPlayer_IsNeverEvicted()
    {
        // A loop has no end, so no state it can reach makes it finished — not even `failed`, which it
        // wears here on top of an exhausted entry list and a clock past its duration.
        Submit("cue", [0], loop: true);
        _store.Activate();
        _store.Tick(_due);
        _due[0].Observer!.OnCommandResult(_due[0].Token, new CommandResult(CommandOutcome.NotFound, "gone"));

        SubmitOneShot("g");
        SubmitOneShot("h");
        GameTick(500);

        var cue = _store.Find("cue")!;
        Assert.Multiple(() =>
        {
            Assert.That(cue.State, Is.EqualTo(PlaybackState.Failed));
            Assert.That(cue.PendingCount, Is.EqualTo(0));
            Assert.That(cue.Clock.PositionMs, Is.GreaterThanOrEqualTo(cue.DurationMs));
        });

        _store.Activate();
        Assert.Multiple(() =>
        {
            Assert.That(_store.Find("cue"), Is.Not.Null, "looping is disqualifying on its own");
            Assert.That(_store.Find("g"), Is.Null);
        });
    }

    [Test]
    public void Eviction_DropsTheGroupClockWithItsLastMember()
    {
        SubmitOneShot("a", "take");
        SubmitOneShot("b");
        SubmitOneShot("c");
        GameTick();

        // The group clock keeps advancing while its (finished) member is listed.
        _store.AdvanceAll(500, 500, 500);
        _store.Activate();
        Assert.That(_store.Find("a"), Is.Null);

        // A re-created group must start at zero: a leaked clock would hand the joiner a stale position.
        SubmitOneShot("d", "take");
        _store.Activate();
        Assert.That(_store.Find("d")!.Clock.PositionMs, Is.EqualTo(0));
    }

    // ---- events -------------------------------------------------------------------------------

    [Test]
    public void EachEviction_EmitsExactlyOneEvent()
    {
        FillWithCompleted("s0", "s1", "s2");
        _store.DrainEvents(); // discard started/finished

        _store.Activate();
        Assert.That(EvictedIds(), Is.EqualTo(new[] { "s0 kind=schedule reason=max_live" }),
            "one event, naming the id, the kind and why — never a silent drop");

        // The pass took exactly the one slot it needed, so the next tick announces nothing.
        _store.Activate();
        Assert.That(EvictedIds(), Is.Empty);
    }

    [Test]
    public void EvictionEvents_CarryTheUtStamp()
    {
        FillWithCompleted("s0", "s1", "s2");
        _store.DrainEvents();

        _store.Activate(1234.5);
        var evicted = _store.DrainEvents().Single(e => e.Type == "schedule.evicted");
        Assert.Multiple(() =>
        {
            Assert.That(evicted.UtSeconds, Is.EqualTo(1234.5));
            Assert.That(evicted.VesselId, Is.Null, "a player has no vessel");
        });
    }

    // ---- the idle path must stay branch-only --------------------------------------------------

    [Test]
    public void IdleActivate_AllocatesNothing()
    {
        // Below the cap with a live player: both of the eviction guards are exercised, and the
        // pending queue is empty. Same discipline as SchedulerTests.IdleTick_AllocatesNothing.
        SubmitOneShot("a");
        GameTick();
        _store.DrainEvents();

        for (var i = 0; i < 64; i++)
            _store.Activate();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            _store.Activate();

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.LessThan(64), $"idle Activate allocated {allocated} bytes");
    }
}
