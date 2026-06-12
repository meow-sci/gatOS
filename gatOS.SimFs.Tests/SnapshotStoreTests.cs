using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests;

/// <summary>OS_PLAN.md T8.1: publish/wait ordering, concurrent waiters, cancellation.</summary>
[TestFixture]
public sealed class SnapshotStoreTests
{
    private static SimSnapshot Snap(long sequence)
        => SimSnapshot.Empty with { Sequence = sequence, UtSeconds = sequence * 0.1 };

    [Test]
    public void Current_StartsAtTheEmptySnapshot()
    {
        var store = new SnapshotStore();
        Assert.Multiple(() =>
        {
            Assert.That(store.Current, Is.SameAs(SimSnapshot.Empty));
            Assert.That(store.Current.Sequence, Is.Zero);
        });
    }

    [Test]
    public async Task WaitForNext_CompletesImmediately_WhenANewerSnapshotIsCurrent()
    {
        var store = new SnapshotStore();
        store.Publish(Snap(5));
        var result = await store.WaitForNextAsync(afterSequence: 3, CancellationToken.None);
        Assert.That(result.Sequence, Is.EqualTo(5));
    }

    [Test]
    public async Task WaitForNext_ParksUntilPublish()
    {
        var store = new SnapshotStore();
        var waiter = store.WaitForNextAsync(0, CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.That(waiter.IsCompleted, Is.False, "must park until something is published");

        store.Publish(Snap(1));
        var result = await waiter.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(result.Sequence, Is.EqualTo(1));
    }

    [Test]
    public async Task WaitForNext_SkipsToTheLatest_WhenPublishesRaceAhead()
    {
        var store = new SnapshotStore();
        store.Publish(Snap(1));
        store.Publish(Snap(2));
        store.Publish(Snap(3));
        var result = await store.WaitForNextAsync(1, CancellationToken.None);
        Assert.That(result.Sequence, Is.EqualTo(3), "intermediate snapshots may be skipped, never replayed");
    }

    [Test]
    public async Task ManyConcurrentWaiters_AllWakeOnOnePublish()
    {
        var store = new SnapshotStore();
        var waiters = Enumerable.Range(0, 64)
            .Select(_ => store.WaitForNextAsync(0, CancellationToken.None).AsTask())
            .ToArray();
        await Task.Delay(50);
        Assert.That(waiters.Any(w => w.IsCompleted), Is.False);

        store.Publish(Snap(1));
        var results = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(results.Select(r => r.Sequence), Is.All.EqualTo(1L));
    }

    [Test]
    public async Task SequentialWaits_ObserveMonotonicSequences()
    {
        var store = new SnapshotStore();
        var publisher = Task.Run(async () =>
        {
            for (var i = 1; i <= 50; i++)
            {
                store.Publish(Snap(i));
                await Task.Delay(1);
            }
        });

        long last = 0;
        while (last < 50)
        {
            var snapshot = await store.WaitForNextAsync(last, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(snapshot.Sequence, Is.GreaterThan(last));
            last = snapshot.Sequence;
        }

        await publisher;
    }

    [Test]
    public void WaitForNext_Cancellation_ThrowsPromptly()
    {
        var store = new SnapshotStore();
        using var cts = new CancellationTokenSource();
        var waiter = store.WaitForNextAsync(0, cts.Token).AsTask();
        cts.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(() => waiter.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void WaitForNext_PreCanceledToken_ThrowsImmediately()
    {
        var store = new SnapshotStore();
        store.Publish(Snap(1));
        var token = new CancellationToken(canceled: true);
        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await store.WaitForNextAsync(0, token));
    }

    [Test]
    public async Task CanceledWaiter_DoesNotDisturbOtherWaiters()
    {
        var store = new SnapshotStore();
        using var cts = new CancellationTokenSource();
        var canceled = store.WaitForNextAsync(0, cts.Token).AsTask();
        var healthy = store.WaitForNextAsync(0, CancellationToken.None).AsTask();
        cts.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => canceled);

        store.Publish(Snap(1));
        var result = await healthy.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(result.Sequence, Is.EqualTo(1));
    }
}
