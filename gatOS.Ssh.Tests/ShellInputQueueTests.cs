namespace gatOS.Ssh.Tests;

/// <summary>
///     Covers the bounded-queue discipline of <see cref="ShellInputQueue"/> (T4.2): ordered
///     delivery, overflow drop with one report per episode, and fail-once on write errors.
/// </summary>
[TestFixture]
public sealed class ShellInputQueueTests
{
    [Test]
    public void Writes_AreDeliveredInOrder_OnTheWriterThread()
    {
        var delivered = new List<byte[]>();
        var threads = new List<int>();
        using var done = new CountdownEvent(3);
        using var queue = new ShellInputQueue(
            "test-input",
            chunk =>
            {
                lock (delivered)
                {
                    delivered.Add(chunk);
                    threads.Add(Environment.CurrentManagedThreadId);
                }

                done.Signal();
            },
            _ => { },
            _ => { });

        var caller = Environment.CurrentManagedThreadId;
        queue.Write("a"u8);
        queue.Write("bb"u8);
        queue.Write("ccc"u8);

        Assert.That(done.Wait(TimeSpan.FromSeconds(5)), Is.True, "writer thread must drain the queue");
        lock (delivered)
        {
            Assert.Multiple(() =>
            {
                Assert.That(delivered.Select(c => c.Length), Is.EqualTo(new[] { 1, 2, 3 }));
                Assert.That(threads, Has.All.Not.EqualTo(caller), "writes must not run on the caller's thread");
            });
        }
    }

    [Test]
    public void Overflow_DropsTheChunk_AndReportsOncePerEpisode()
    {
        var overflows = 0;
        using var firstChunkTaken = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var queue = new ShellInputQueue(
            "test-input",
            _ =>
            {
                firstChunkTaken.Set();
                gate.Wait(TimeSpan.FromSeconds(10)); // simulate a guest that stopped reading
            },
            _ => Interlocked.Increment(ref overflows),
            _ => { });

        // One chunk occupies the (blocked) writer; its bytes no longer count as pending.
        queue.Write(new byte[16]);
        Assert.That(firstChunkTaken.Wait(TimeSpan.FromSeconds(5)), Is.True);

        queue.Write(new byte[ShellInputQueue.MaxPendingBytes]); // exactly at the cap: accepted
        queue.Write(new byte[1]); // over the cap: dropped + reported
        queue.Write(new byte[1]); // same episode: dropped silently
        Assert.That(Volatile.Read(ref overflows), Is.EqualTo(1));

        gate.Set();
    }

    [Test]
    public void WriteFailure_IsReportedOnce_AndLaterChunksAreDropped()
    {
        var failures = 0;
        var written = 0;
        using var failed = new ManualResetEventSlim();
        using var queue = new ShellInputQueue(
            "test-input",
            chunk =>
            {
                if (chunk[0] == 0xFF)
                {
                    failed.Set();
                    throw new IOException("connection lost");
                }

                Interlocked.Increment(ref written);
            },
            _ => { },
            _ => Interlocked.Increment(ref failures));

        queue.Write([0xFF]);
        Assert.That(failed.Wait(TimeSpan.FromSeconds(5)), Is.True);

        queue.Write([0x01]);
        queue.Write([0x02]);
        // Dispose completes + joins the writer, so the drain-and-drop has finished by here.
        queue.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.EqualTo(1));
            Assert.That(written, Is.Zero, "chunks after a write failure must be dropped");
        });
    }

    [Test]
    public void Dispose_DrainsPendingInput_BeforeJoining()
    {
        var written = 0;
        var queue = new ShellInputQueue(
            "test-input",
            _ => Interlocked.Increment(ref written),
            _ => { },
            _ => { });

        for (var i = 0; i < 100; i++)
            queue.Write(new byte[8]);
        queue.Dispose();

        Assert.That(written, Is.EqualTo(100), "pending input is flushed on shutdown, not dropped");
    }
}
