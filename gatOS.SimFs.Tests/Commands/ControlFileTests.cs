using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The STATE/TRIGGER control-file archetypes (KSA_GAME_INTEGRATION_PLAN Part 2): value parsing,
///     command construction, line-buffered actuation, and errno mapping — exercised directly
///     against the write handle (no server).
/// </summary>
[TestFixture]
public sealed class ControlFileTests
{
    private static SimCommand FlagCommand(double v) => new("v1", "engine.active", 2, v);

    private static async Task<uint> WriteLineAsync(VfsFile file, string text)
    {
        using var handle = file.OpenWrite();
        return await handle.WriteAsync(0, Encoding.UTF8.GetBytes(text), CancellationToken.None);
    }

    [Test]
    public void WritableArchetypes_ReportWritable()
    {
        var sink = new FakeCommandSink();
        Assert.That(ControlFile.Flag("f", 1, sink, () => "0", FlagCommand).IsWritable, Is.True);
        Assert.That(new TriggerFile("t", 2, sink, FlagCommand(1)).IsWritable, Is.True);
    }

    [Test]
    public async Task Read_ReturnsLiveValuePlusNewline()
    {
        var sink = new FakeCommandSink();
        var value = "0";
        var file = ControlFile.Flag("active", 1, sink, () => value, FlagCommand);
        using var handle = file.Open();
        value = "1"; // snapshot-per-open: the open below sees the value at open time
        using var handle2 = file.Open();
        var bytes = await handle2.ReadAsync(0, 64, CancellationToken.None);
        Assert.That(Encoding.UTF8.GetString(bytes.Span), Is.EqualTo("1\n"));
    }

    [Test]
    public async Task Flag_WritesValue_BuildsAndSubmitsCommand()
    {
        var sink = new FakeCommandSink();
        var file = ControlFile.Flag("active", 1, sink, () => "0", FlagCommand);
        var written = await WriteLineAsync(file, "1\n");
        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(2u));
            Assert.That(sink.Last, Is.EqualTo(new SimCommand("v1", "engine.active", 2, 1)));
        });
    }

    [Test]
    public void Flag_OutOfRange_IsEinval_AndDoesNotSubmit()
    {
        var sink = new FakeCommandSink();
        var file = ControlFile.Flag("active", 1, sink, () => "0", FlagCommand);
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteLineAsync(file, "2\n"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Fraction_AcceptsInRange()
    {
        var sink = new FakeCommandSink();
        var file = ControlFile.Fraction("goal", 1, sink, () => "0", v => new SimCommand("v1", "animation.goal", 0, v));
        await WriteLineAsync(file, "0.5\n");
        Assert.That(sink.Last!.Value, Is.EqualTo(0.5));
    }

    [Test]
    public void Fraction_OutOfRange_IsEinval()
    {
        var sink = new FakeCommandSink();
        var file = ControlFile.Fraction("goal", 1, sink, () => "0", v => new SimCommand("v1", "animation.goal", 0, v));
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteLineAsync(file, "1.5\n"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public void Failure_MapsOutcomeToErrno()
    {
        var sink = new FakeCommandSink { Result = new CommandResult(CommandOutcome.Busy, "already fired") };
        var file = ControlFile.Flag("active", 1, sink, () => "0", FlagCommand);
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteLineAsync(file, "1\n"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EBUSY));
    }

    [Test]
    public async Task Trigger_FireToken_Submits()
    {
        var sink = new FakeCommandSink();
        var command = new SimCommand("v1", "vessel.ignite", SimCommand.NoOrdinal, 1);
        var file = new TriggerFile("ignite", 1, sink, command);
        await WriteLineAsync(file, "1\n");
        Assert.That(sink.Last, Is.EqualTo(command));
    }

    [Test]
    public void Trigger_WrongToken_IsEinval()
    {
        var sink = new FakeCommandSink();
        var file = new TriggerFile("ignite", 1, sink, new SimCommand("v1", "vessel.ignite", SimCommand.NoOrdinal, 1));
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteLineAsync(file, "0\n"));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(sink.Submits, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task ControlDisabled_SinkDenies_MapsToEacces()
    {
        var sink = new FakeCommandSink { ControlEnabled = false };
        var file = ControlFile.Flag("active", 1, sink, () => "0", FlagCommand);
        // The sink (CommandQueue in production) returns Denied; the file maps it to EACCES.
        try
        {
            await WriteLineAsync(file, "1\n");
            Assert.Fail("expected EACCES");
        }
        catch (VfsErrorException ex)
        {
            Assert.That(ex.Errno, Is.EqualTo(LinuxErrno.EACCES));
        }
    }

    // Every failed CommandOutcome must surface on the failed write(2) as its frozen errno
    // (KSA_GAME_INTEGRATION_PLAN Part 2). EINVAL/EBUSY/EACCES are covered above; pin the rest.
    [TestCase(CommandOutcome.NotFound, LinuxErrno.ENOENT)]
    [TestCase(CommandOutcome.Fault, LinuxErrno.EIO)]
    [TestCase(CommandOutcome.TimedOut, LinuxErrno.ETIMEDOUT)]
    [TestCase(CommandOutcome.Unsupported, LinuxErrno.EOPNOTSUPP)]
    public void FailedOutcome_MapsToFrozenErrno(CommandOutcome outcome, uint errno)
    {
        var sink = new FakeCommandSink { Result = new CommandResult(outcome, "x") };
        var file = ControlFile.Flag("active", 1, sink, () => "0", FlagCommand);
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteLineAsync(file, "1\n"));
        Assert.That(ex!.Errno, Is.EqualTo(errno));
    }

    [Test]
    public async Task UnterminatedWrite_ActuatesOnClunk()
    {
        // `printf '%s' 1 > file` (no newline): the value actuates best-effort when the handle is
        // clunked (KSA_GAME_INTEGRATION_PLAN Part 6 T1). No errno can flow back, so it can't fail
        // the write — but the command must still reach the sink.
        var sink = new FakeCommandSink();
        var file = ControlFile.Flag("active", 1, sink, () => "0", FlagCommand);
        var handle = file.OpenWrite();
        await handle.WriteAsync(0, "1"u8.ToArray(), CancellationToken.None); // no '\n'
        Assert.That(sink.Submits, Is.EqualTo(0), "nothing actuates before the clunk");

        handle.Dispose();
        Assert.That(await EventuallyAsync(() => sink.Submits == 1), Is.True, "clunk actuates the buffered value");
        Assert.That(sink.Last, Is.EqualTo(new SimCommand("v1", "engine.active", 2, 1)));
    }

    [Test]
    public async Task UnterminatedWrite_OnClunk_SwallowsFailure()
    {
        // A clunk-time actuation that the sink rejects is logged, never thrown (Dispose can't fail).
        var sink = new FakeCommandSink { Result = new CommandResult(CommandOutcome.Busy, "no") };
        var file = ControlFile.Flag("active", 1, sink, () => "0", FlagCommand);
        var handle = file.OpenWrite();
        await handle.WriteAsync(0, "1"u8.ToArray(), CancellationToken.None);
        Assert.DoesNotThrow(() => handle.Dispose());
        Assert.That(await EventuallyAsync(() => sink.Submits == 1), Is.True);
    }

    [Test]
    public async Task EmptyUnterminatedWrite_OnClunk_DoesNothing()
    {
        var sink = new FakeCommandSink();
        var file = ControlFile.Flag("active", 1, sink, () => "0", FlagCommand);
        using (var handle = file.OpenWrite())
            await handle.WriteAsync(0, "   "u8.ToArray(), CancellationToken.None); // whitespace only
        await Task.Delay(50);
        Assert.That(sink.Submits, Is.EqualTo(0), "a blank unterminated write actuates nothing");
    }

    [Test]
    public async Task Write_SplitAcrossCalls_ActuatesOnceTheNewlineArrives()
    {
        var sink = new FakeCommandSink();
        var file = ControlFile.Fraction("goal", 1, sink, () => "0",
            v => new SimCommand("v1", "animation.goal", 0, v));
        using var handle = file.OpenWrite();
        await handle.WriteAsync(0, "0."u8.ToArray(), CancellationToken.None);
        Assert.That(sink.Submits, Is.EqualTo(0), "no newline yet → no actuation");
        await handle.WriteAsync(2, "5\n"u8.ToArray(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(sink.Submits, Is.EqualTo(1));
            Assert.That(sink.Last!.Value, Is.EqualTo(0.5));
        });
    }

    [Test]
    public async Task Write_BytesAfterNewline_AreIgnored()
    {
        var sink = new FakeCommandSink();
        var file = ControlFile.Flag("active", 1, sink, () => "0", FlagCommand);
        using var handle = file.OpenWrite();
        var written = await handle.WriteAsync(0, "1\njunk"u8.ToArray(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(6u), "the whole write is consumed");
            Assert.That(sink.Submits, Is.EqualTo(1), "only the first line actuates");
            Assert.That(sink.Last!.Value, Is.EqualTo(1));
        });

        // A further write after the value was taken is a no-op that still consumes its bytes.
        var more = await handle.WriteAsync(6, "2\n"u8.ToArray(), CancellationToken.None);
        Assert.That(more, Is.EqualTo(2u));
        Assert.That(sink.Submits, Is.EqualTo(1), "control files take exactly one value");
    }

    [Test]
    public async Task Number_AcceptsAnyFinite_RejectsNonFinite()
    {
        var sink = new FakeCommandSink();
        var file = ControlFile.Number("brightness", 1, sink, () => "0",
            v => new SimCommand("v1", "light.brightness", 0, v));
        await WriteLineAsync(file, "2.5\n");
        Assert.That(sink.Last!.Value, Is.EqualTo(2.5), "an unbounded number is accepted");

        foreach (var bad in new[] { "nan\n", "inf\n", "-inf\n", "abc\n" })
        {
            var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteLineAsync(file, bad));
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL), $"'{bad.Trim()}' must be EINVAL");
        }
    }

    [Test]
    public async Task Vector_BuildsValues_RejectsWrongArityAndNonFinite()
    {
        var sink = new FakeCommandSink();
        var file = VectorControlFile.Create("color", 1, sink, () => "0 0 0", 3,
            v => new SimCommand("v1", "light.color", 0, 0) { Values = v });
        await WriteLineAsync(file, "0.1 0.2 0.3\n");
        Assert.That(sink.Last!.Values, Is.EqualTo(new[] { 0.1, 0.2, 0.3 }));

        foreach (var bad in new[] { "1 2\n", "1 2 3 4\n", "1 nan 3\n", "\n" })
        {
            var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteLineAsync(file, bad));
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL), $"'{bad.Trim()}' must be EINVAL");
        }
    }

    [Test]
    public async Task Enum_FoldsCaseToCanonical_RejectsUnknown()
    {
        var sink = new FakeCommandSink();
        var file = EnumControlFile.Create("attitude_mode", 1, sink, () => "manual",
            ["manual", "Prograde"], t => new SimCommand("v1", "vessel.attitude_mode", SimCommand.NoOrdinal, 0) { Token = t });
        await WriteLineAsync(file, "PROGRADE\n");
        Assert.That(sink.Last!.Token, Is.EqualTo("Prograde"), "input case-folds to the canonical token");

        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteLineAsync(file, "sideways\n"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public async Task Token_AcceptsNonEmpty_RejectsEmpty()
    {
        var sink = new FakeCommandSink();
        var file = TokenControlFile.Create("control_vessel", 1, sink, () => "",
            t => new SimCommand(t, "debug.control_vessel", SimCommand.NoOrdinal, 0) { Token = t });
        await WriteLineAsync(file, "vessel-7\n");
        Assert.That(sink.Last!.Token, Is.EqualTo("vessel-7"));

        // A blank line trims to empty → EINVAL (CommandFile trims before Parse).
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteLineAsync(file, "   \n"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        return condition();
    }
}
