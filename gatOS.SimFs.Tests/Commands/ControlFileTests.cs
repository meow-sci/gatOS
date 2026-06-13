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
}
