using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.NineP.Tests;

/// <summary>
///     GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP4: the read-into-destination path — the 9p
///     session hands file handles a slice of its pooled reply frame, so the payload is written
///     exactly once with no intermediate buffer. The host-file handle is the critical one: its
///     count-allocating overload was a fresh 512 KiB LOH array per <c>/mnt</c> Tread.
/// </summary>
[TestFixture]
public sealed class ZeroCopyReadTests
{
    [Test]
    public void Writer_ReserveAndTrim_ProduceTheExactFrame()
    {
        var writer = new NinePWriter().Begin(MessageType.Rread, 7).WriteUInt32(0);
        var payloadStart = writer.Length;
        var reserved = writer.Reserve(16);
        System.Text.Encoding.ASCII.GetBytes("hello", reserved.Span);
        writer.TrimTo(payloadStart + 5).PatchUInt32(payloadStart - 4, 5);

        var expected = new NinePWriter().Begin(MessageType.Rread, 7).WriteUInt32(5)
            .WriteBytes(System.Text.Encoding.ASCII.GetBytes("hello"));
        Assert.That(writer.Frame().ToArray(), Is.EqualTo(expected.Frame().ToArray()));
    }

    [Test]
    public async Task HostFile_ReadIntoDestination_MatchesAndDoesNotAllocate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gatos-zerocopy-{Guid.NewGuid():N}.bin");
        var content = new byte[128 * 1024];
        new Random(42).NextBytes(content);
        await File.WriteAllBytesAsync(path, content);
        try
        {
            var file = new HostFile("f", path, writable: false, new HostMountQids());
            using var handle = file.Open();
            var destination = new byte[64 * 1024];

            var first = await handle.ReadAsync(0, destination.AsMemory(), CancellationToken.None);
            Assert.That(first, Is.EqualTo(destination.Length));
            Assert.That(destination.AsSpan(0, first).SequenceEqual(content.AsSpan(0, first)), Is.True);

            for (var warm = 0; warm < 8; warm++) // JIT + any IO-layer warmup
                await handle.ReadAsync(0, destination.AsMemory(), CancellationToken.None);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 16; i++)
                await handle.ReadAsync((ulong)(i * 1024), destination.AsMemory(), CancellationToken.None);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.LessThan(16 * 1024),
                $"16 zero-copy host reads allocated {allocated} B — the old path allocated the full "
                + "read size per Tread (LOH at msize 512 KiB)");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
