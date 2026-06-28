using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The <see cref="LineControlFile"/> archetype: a whole-line STATE control parsed by a supplied
///     delegate (the escape hatch for mixed string+number argument shapes the welds controls use).
/// </summary>
[TestFixture]
public sealed class LineControlFileTests
{
    // A representative structured parser: "<id> <a> <b>" → Token=id, Values=[a,b]; null on bad shape.
    private static SimCommand? Parse(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0].Length == 0
            || !double.TryParse(parts[1], out var a) || !double.TryParse(parts[2], out var b))
            return null;
        return new SimCommand("src", "debug.thing", SimCommand.NoOrdinal, 0) { Token = parts[0], Values = [a, b] };
    }

    private static LineControlFile File(ICommandSink sink, Func<string>? read = null)
        => LineControlFile.Create("thing", 1, sink, read ?? (() => ""), Parse);

    private static async Task<uint> WriteLineAsync(VfsFile file, string text)
    {
        using var handle = file.OpenWrite();
        return await handle.WriteAsync(0, Encoding.UTF8.GetBytes(text), CancellationToken.None);
    }

    [Test]
    public void ReportsWritable() => Assert.That(File(new FakeCommandSink()).IsWritable, Is.True);

    [Test]
    public async Task Read_ReturnsValuePlusNewline()
    {
        var file = File(new FakeCommandSink(), () => "tgt 1 2");
        using var handle = file.Open();
        var bytes = await handle.ReadAsync(0, 64, CancellationToken.None);
        Assert.That(Encoding.UTF8.GetString(bytes.Span), Is.EqualTo("tgt 1 2\n"));
    }

    [Test]
    public async Task ValidLine_BuildsAndSubmits()
    {
        var sink = new FakeCommandSink();
        await WriteLineAsync(File(sink), "tgt 1.5 -2\n");
        Assert.Multiple(() =>
        {
            Assert.That(sink.Last!.Action, Is.EqualTo("debug.thing"));
            Assert.That(sink.Last!.Token, Is.EqualTo("tgt"));
            Assert.That(sink.Last!.Values, Is.EqualTo(new[] { 1.5, -2d }));
        });
    }

    [TestCase("tgt 1\n")] // too few
    [TestCase("tgt 1 2 3\n")] // too many
    [TestCase("tgt a b\n")] // non-numeric
    [TestCase("   \n")] // empty after trim
    public void BadLine_IsEinval_AndDoesNotSubmit(string line)
    {
        var sink = new FakeCommandSink();
        var ex = Assert.ThrowsAsync<VfsErrorException>(() => WriteLineAsync(File(sink), line));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(sink.Submits, Is.EqualTo(0));
        });
    }
}
