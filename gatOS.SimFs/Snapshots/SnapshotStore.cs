namespace gatOS.SimFs.Snapshots;

/// <summary>
///     The single-writer snapshot exchange between the game-thread sampler (M9) and the 9p
///     server threads (OS_PLAN.md T8.1; threading rules 1–2). <see cref="Publish"/> swaps one
///     volatile reference and wakes waiters; readers never lock.
/// </summary>
public sealed class SnapshotStore
{
    private volatile SimSnapshot _current = SimSnapshot.Empty;
    private volatile TaskCompletionSource _signal = NewSignal();

    /// <summary>The latest published snapshot (never null; starts at <see cref="SimSnapshot.Empty"/>).</summary>
    public SimSnapshot Current => _current;

    /// <summary>
    ///     Publishes a snapshot. Single writer by contract (the game-thread sampler);
    ///     <paramref name="snapshot"/>.Sequence must be greater than the current one.
    /// </summary>
    public void Publish(SimSnapshot snapshot)
    {
        _current = snapshot;
        // Swap first, then complete: a waiter that captured the old signal wakes and re-reads
        // _current; one that already captured the new signal waits for the next publish.
        var completed = Interlocked.Exchange(ref _signal, NewSignal());
        completed.SetResult();
    }

    /// <summary>
    ///     Completes with the first snapshot whose sequence exceeds
    ///     <paramref name="afterSequence"/> (immediately when one is already current). Used by
    ///     the stream/events files; cancellation propagates from Tflush/clunk.
    /// </summary>
    public async ValueTask<SimSnapshot> WaitForNextAsync(long afterSequence, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = _current;
            if (snapshot.Sequence > afterSequence)
                return snapshot;

            var signal = _signal;
            // Re-check after capturing the signal: a publish between the two volatile reads
            // would otherwise be missed (it completed the previous signal, not this one).
            snapshot = _current;
            if (snapshot.Sequence > afterSequence)
                return snapshot;

            await signal.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
