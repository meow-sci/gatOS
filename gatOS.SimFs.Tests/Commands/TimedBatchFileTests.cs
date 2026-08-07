using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The TIMED BATCH archetype (<c>/sim/ctl/timed_batch</c>): the directive + entry grammar, path
///     resolution against the tree, up-front all-or-nothing validation, the caps, and the
///     commit/abort handle semantics — exercised directly against the write handle (no server).
/// </summary>
[TestFixture]
public sealed class TimedBatchFileTests
{
    private FakeCommandSink _sink = null!;
    private ScheduleStore _store = null!;
    private TimedBatchFile _timed = null!;

    [SetUp]
    public void SetUp()
    {
        _sink = new FakeCommandSink();
        _store = new ScheduleStore(new ScheduleLimits(MaxLive: 3, MaxEntries: 8, MaxBytes: 4096));

        // A miniature /sim: a Frame throttle (state), a Frame ignite (trigger), a Solver refill
        // (trigger), a read-only sensor, and the timed batch itself at ctl/timed_batch.
        VfsDirectory? root = null;
        var throttle = ControlFile.Fraction("throttle", 2, _sink, () => "0",
            v => new SimCommand("hunter", "vessel.throttle", SimCommand.NoOrdinal, v));
        var ignite = new TriggerFile("ignite", 3, _sink,
            new SimCommand("hunter", "vessel.ignite", SimCommand.NoOrdinal, 1));
        var refill = new TriggerFile("refill_fuel", 4, _sink,
            new SimCommand("hunter", "debug.refill_fuel", SimCommand.NoOrdinal, 1));
        var sensor = new StaticTextFile("radar", 5, () => "5\n");
        _timed = new TimedBatchFile("timed_batch", 6, _sink, () => root!, _store);
        var ctl = DelegateDirectory.Fixed("ctl", 7, _timed);
        root = DelegateDirectory.Fixed("/", 1, throttle, ignite, refill, sensor, ctl);
    }

    private static Task<uint> WriteAsync(IVfsWritableFileHandle handle, string text)
        => handle.WriteAsync(0, Encoding.UTF8.GetBytes(text), CancellationToken.None).AsTask();

    private void Commit(string body)
    {
        using var handle = _timed.OpenWrite();
        WriteAsync(handle, body).GetAwaiter().GetResult();
    }

    private VfsErrorException Reject(string body)
    {
        using var handle = _timed.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteAsync(handle, body));
        Assert.That(_store.Count, Is.EqualTo(0), "all-or-nothing: nothing registered");
        return ex!;
    }

    /// <summary>Activates the store so committed schedules become live players.</summary>
    private void Activate() => _store.Activate();

    // ---- the happy path ---------------------------------------------------------------------

    [Test]
    public void Commit_RegistersAScheduleAndReservesItsId()
    {
        Commit("@id launch\n0 throttle 1\n1200 ignite 1\ncommit\n");
        Assert.Multiple(() =>
        {
            Assert.That(_store.IsIdLive("launch"), Is.True, "the id is reserved at commit, not at activation");
            Assert.That(_store.Count, Is.EqualTo(0), "the player appears at the next game tick");
        });

        Activate();
        var player = _store.Find("launch");
        Assert.Multiple(() =>
        {
            Assert.That(player, Is.Not.Null);
            Assert.That(player!.Kind, Is.EqualTo("schedule"));
            Assert.That(player.DurationMs, Is.EqualTo(1200));
            Assert.That(player.PendingCount, Is.EqualTo(2));
            Assert.That(player.State, Is.EqualTo(PlaybackState.Running));
        });
    }

    [Test]
    public void NoId_AutoAssignsAHashName()
    {
        Commit("0 ignite 1\ncommit\n");
        Activate();
        Assert.That(_store.Players.Single().Id, Does.StartWith("#"));
    }

    [Test]
    public void ColumnAlignedLines_AndCommentsAndBlanks_Parse()
    {
        Commit("""
               # clean footage
               @id      seq
               @clock   wall
               @rate    2

               0        throttle                             1
               1200     ignite                               1
               commit

               """);
        Activate();
        var player = _store.Find("seq")!;
        Assert.Multiple(() =>
        {
            Assert.That(player.Clock.Base, Is.EqualTo(ClockBase.Wall));
            Assert.That(player.Clock.Rate, Is.EqualTo(2));
            Assert.That(player.PendingCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void PathSpellings_BareSlashAndSimRooted_AllResolve()
    {
        Commit("0 /sim/ignite 1\n10 /throttle 0.5\n20 ignite 1\ncommit\n");
        Activate();
        Assert.That(_store.Players.Single().PendingCount, Is.EqualTo(3));
    }

    [Test]
    public void FractionalOffsets_AreLegal_AndSortStably()
    {
        Commit("16.67 ignite 1\n0 throttle 1\n16.67 throttle 0.5\ncommit\n");
        Activate();
        Assert.That(_store.Players.Single().DurationMs, Is.EqualTo(16.67).Within(1e-9));
    }

    [Test]
    public void MixedFrameAndSolverPhases_AreAccepted()
    {
        // The deliberate relaxation of BatchFile's rule: a schedule spans many ticks, so each entry
        // routes to its own phase queue when due.
        Commit("0 ignite 1\n100 refill_fuel 1\ncommit\n");
        Activate();
        Assert.That(_store.Count, Is.EqualTo(1));
    }

    [Test]
    public void DefaultClock_ComesFromTheLimits()
    {
        var store = new ScheduleStore(new ScheduleLimits(DefaultClock: ClockBase.Ut));
        VfsDirectory? root = null;
        var ignite = new TriggerFile("ignite", 3, _sink,
            new SimCommand("hunter", "vessel.ignite", SimCommand.NoOrdinal, 1));
        var timed = new TimedBatchFile("timed_batch", 6, _sink, () => root!, store);
        root = DelegateDirectory.Fixed("/", 1, ignite, timed);

        using (var handle = timed.OpenWrite())
            WriteAsync(handle, "0 ignite 1\ncommit\n").GetAwaiter().GetResult();
        store.Activate();
        Assert.That(store.Players.Single().Clock.Base, Is.EqualTo(ClockBase.Ut));
    }

    // ---- directives -------------------------------------------------------------------------

    [Test]
    [TestCase("@clock render", ClockBase.Render)]
    [TestCase("@clock wall", ClockBase.Wall)]
    [TestCase("@clock UT", ClockBase.Ut)]
    public void ClockDirective_AcceptsTheThreeBases(string directive, ClockBase expected)
    {
        Commit($"{directive}\n0 ignite 1\ncommit\n");
        Activate();
        Assert.That(_store.Players.Single().Clock.Base, Is.EqualTo(expected));
    }

    [Test]
    public void LoopAndGroupDirectives_Apply()
    {
        Commit("@id a\n@group take\n@loop 1\n0 ignite 1\ncommit\n");
        Activate();
        var player = _store.Find("a")!;
        Assert.Multiple(() =>
        {
            Assert.That(player.Group, Is.EqualTo("take"));
            Assert.That(player.Clock.Loop, Is.True);
        });
    }

    [Test]
    public void SameGroup_SharesOneClockInstance()
    {
        Commit("@id a\n@group take\n0 ignite 1\ncommit\n");
        Commit("@id b\n@group take\n@clock wall\n500 ignite 1\ncommit\n");
        Activate();

        var a = _store.Find("a")!;
        var b = _store.Find("b")!;
        Assert.Multiple(() =>
        {
            Assert.That(b.Clock, Is.SameAs(a.Clock), "one clock is what makes a group one take");
            Assert.That(b.Clock.Base, Is.EqualTo(ClockBase.Render), "the first member's clock base wins");
            Assert.That(a.Clock.DurationMs, Is.EqualTo(500), "the group clock spans its longest member");
        });
    }

    [Test]
    [TestCase("@nope 1", "unknown directive")]
    [TestCase("@id", "needs a value")]
    [TestCase("@clock sundial", "render|wall|ut")]
    [TestCase("@rate abc", "expected")]
    [TestCase("@rate 1e9", "expected")]
    [TestCase("@loop 2", "expected 0|1")]
    [TestCase("@id has spaces and $ signs", "not a valid id")]
    [TestCase("@group !!", "not a valid group name")]
    public void BadDirective_IsEinval(string directive, string messageFragment)
    {
        var ex = Reject($"{directive}\n0 ignite 1\ncommit\n");
        Assert.Multiple(() =>
        {
            Assert.That(ex.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(ex.Message, Does.Contain(messageFragment));
        });
    }

    [Test]
    [TestCase("@id a\n@id b")]
    [TestCase("@clock wall\n@clock ut")]
    [TestCase("@rate 1\n@rate 2")]
    [TestCase("@loop 0\n@loop 1")]
    [TestCase("@group x\n@group y")]
    public void DuplicateDirective_IsEinval(string directives)
    {
        var ex = Reject($"{directives}\n0 ignite 1\ncommit\n");
        Assert.Multiple(() =>
        {
            Assert.That(ex.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(ex.Message, Does.Contain("duplicate"));
        });
    }

    [Test]
    public void DirectiveAfterAnEntry_IsEinval()
    {
        var ex = Reject("0 ignite 1\n@rate 2\ncommit\n");
        Assert.Multiple(() =>
        {
            Assert.That(ex.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(ex.Message, Does.Contain("must precede entries"));
        });
    }

    // ---- entries ----------------------------------------------------------------------------

    [Test]
    [TestCase("abc ignite 1")]
    [TestCase("-5 ignite 1")]
    [TestCase("NaN ignite 1")]
    [TestCase("Infinity ignite 1")]
    public void BadOffset_IsEinval(string line)
    {
        Assert.That(Reject($"{line}\ncommit\n").Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public void OffsetWithNoPath_IsEinval()
    {
        Assert.That(Reject("0\ncommit\n").Errno, Is.EqualTo(LinuxErrno.EINVAL));
        Assert.That(Reject("0   \ncommit\n").Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public void UnknownPath_IsEnoent()
    {
        Assert.That(Reject("0 nope/nothing 1\ncommit\n").Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public void NonControlTarget_IsEinval()
    {
        var ex = Reject("0 radar 5\ncommit\n");
        Assert.Multiple(() =>
        {
            Assert.That(ex.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(ex.Message, Does.Contain("not a control file"));
        });
    }

    [Test]
    public void UnparseablePayload_IsEinval_AndFailsTheWholeSchedule()
    {
        var ex = Reject("0 ignite 1\n100 throttle 7\ncommit\n");
        Assert.Multiple(() =>
        {
            Assert.That(ex.Errno, Is.EqualTo(LinuxErrno.EINVAL), "7 is outside a fraction's 0..1");
            Assert.That(ex.Message, Does.Contain("cannot parse"));
        });
    }

    [Test]
    public void EmptySchedule_IsEinval()
    {
        var ex = Reject("# nothing but comments\n\ncommit\n");
        Assert.Multiple(() =>
        {
            Assert.That(ex.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(ex.Message, Does.Contain("no entries"));
        });
    }

    // ---- caps -------------------------------------------------------------------------------

    [Test]
    public void OverMaxEntries_IsEinval()
    {
        var text = new StringBuilder();
        for (var i = 0; i <= _store.Limits.MaxEntries; i++)
            text.Append(i).Append(" ignite 1\n");
        text.Append("commit\n");
        Assert.That(Reject(text.ToString()).Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public void OverMaxBytes_IsEinval()
    {
        using var handle = _timed.OpenWrite();
        var oversize = new string('#', _store.Limits.MaxBytes + 1);
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteAsync(handle, oversize));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public void OverMaxLive_IsEinval()
    {
        for (var i = 0; i < _store.Limits.MaxLive; i++)
            Commit($"@id s{i}\n0 ignite 1\ncommit\n");

        using var handle = _timed.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(
            () => WriteAsync(handle, "@id extra\n0 ignite 1\ncommit\n"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(ex.Message, Does.Contain("live players"));
        });
    }

    [Test]
    public void DuplicateId_IsEinval_AndLeavesTheFirstAlone()
    {
        Commit("@id launch\n0 ignite 1\ncommit\n");

        using var handle = _timed.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(
            () => WriteAsync(handle, "@id launch\n0 ignite 1\ncommit\n"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(ex.Message, Does.Contain("already live"));
        });

        Activate();
        Assert.That(_store.Count, Is.EqualTo(1));
    }

    [Test]
    public void FailedValidation_DoesNotLeakAnId()
    {
        // The id is reserved LAST, so a bad entry never burns the name.
        Reject("@id launch\n0 radar 5\ncommit\n");
        Assert.That(_store.IsIdLive("launch"), Is.False);
    }

    // ---- handle semantics --------------------------------------------------------------------

    [Test]
    public async Task SplitWrites_AccumulateUntilCommit()
    {
        using var handle = _timed.OpenWrite();
        await WriteAsync(handle, "@id seq\n0 ignite 1\n");
        Assert.That(_store.IsIdLive("seq"), Is.False, "nothing registers before commit");

        await WriteAsync(handle, "500 throttle 1\ncommit\n");
        Activate();
        Assert.That(_store.Find("seq")!.PendingCount, Is.EqualTo(2));
    }

    [Test]
    public async Task CloseWithoutCommit_DiscardsSilently()
    {
        var handle = _timed.OpenWrite();
        await WriteAsync(handle, "@id seq\n0 ignite 1\n");
        handle.Dispose();
        Activate();
        Assert.Multiple(() =>
        {
            Assert.That(_store.Count, Is.EqualTo(0));
            Assert.That(_store.IsIdLive("seq"), Is.False);
        });
    }

    [Test]
    public async Task UnterminatedTrailingCommit_FiresBestEffortOnClose()
    {
        var handle = _timed.OpenWrite();
        await WriteAsync(handle, "@id seq\n0 ignite 1\ncommit"); // printf-style: no final newline
        handle.Dispose();
        Activate();
        Assert.That(_store.Find("seq"), Is.Not.Null);
    }

    [Test]
    public async Task LinesPastTheCommit_AreIgnored()
    {
        using var handle = _timed.OpenWrite();
        await WriteAsync(handle, "@id one\n0 ignite 1\ncommit\n@id two\n0 ignite 1\ncommit\n");
        await WriteAsync(handle, "@id three\n0 ignite 1\ncommit\n");
        Activate();
        Assert.Multiple(() =>
        {
            Assert.That(_store.Count, Is.EqualTo(1), "one schedule per open handle");
            Assert.That(_store.Find("one"), Is.Not.Null);
        });
    }

    [Test]
    public void ControlDisabled_IsEacces()
    {
        var sink = new FakeCommandSink { ControlEnabled = false };
        VfsDirectory? root = null;
        var ignite = new TriggerFile("ignite", 3, sink,
            new SimCommand("hunter", "vessel.ignite", SimCommand.NoOrdinal, 1));
        var timed = new TimedBatchFile("timed_batch", 6, sink, () => root!, _store);
        root = DelegateDirectory.Fixed("/", 1, ignite, timed);

        using var handle = timed.OpenWrite();
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteAsync(handle, "0 ignite 1\ncommit\n"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EACCES));
    }

    [Test]
    public async Task Read_ReturnsUsageHint()
    {
        using var handle = _timed.Open();
        var bytes = await handle.ReadAsync(0, 4096, CancellationToken.None);
        var text = Encoding.UTF8.GetString(bytes.Span);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.StartWith("#").And.EndWith("\n"));
            Assert.That(_timed.Size, Is.EqualTo(bytes.Length), "Size is truthful (spike rule 1)");
        });
    }
}
