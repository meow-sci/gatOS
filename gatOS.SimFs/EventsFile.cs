using gatOS.NineP.Vfs;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs;

/// <summary>
///     <c>/sim/events</c> — the <b>blocking-event</b> synthetic-file model (OS_PLAN.md T8.3;
///     spike/NOTES.md T1.2 rule 3): a fresh read parks until the next event, delivers the
///     NDJSON line(s), then answers the kernel's two continuation reads with 0 bytes so the
///     read() syscall completes and the line reaches userspace immediately. <c>cat</c> prints
///     one line per event forever; Ctrl-C lands as Tflush on the parked read.
/// </summary>
/// <remarks>
///     The reported size is 1, not 0: a 0 size makes the kernel answer read() with instant
///     EOF without ever issuing a Tread (spike rule 1), while any positive claim must not
///     exceed what a read actually delivers — and every event line is at least one byte.
///     ("One line" in the spike was a fixed 16-byte line; with variable-length JSON lines,
///     1 is the only always-truthful claim. Verified against the real guest in the T8.4
///     in-VM end-to-end test.)
/// </remarks>
public sealed class EventsFile : VfsFile
{
    private readonly SnapshotStore _store;

    /// <param name="name">The entry name (<c>events</c>).</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="store">The snapshot source.</param>
    public EventsFile(string name, ulong qidPath, SnapshotStore store)
        : base(name, qidPath)
    {
        _store = store;
    }

    /// <inheritdoc />
    public override bool IsStreaming => true;

    /// <inheritdoc />
    public override long Size => 1;

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new Handle(_store);

    private sealed class Handle : IVfsFileHandle
    {
        private readonly SnapshotStore _store;
        private readonly SemaphoreSlim _readGate = new(1, 1);
        private readonly CancellationTokenSource _disposed = new();
        private long _lastSeq;
        private byte[] _pending = [];
        private int _pendingAt;
        private int _zerosOwed;

        internal Handle(SnapshotStore store)
        {
            _store = store;
            _lastSeq = store.Current.Sequence; // only events after the open are delivered
        }

        public long Size => 1;

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            // Offsets are ignored: the kernel's offsets grow monotonically while this file
            // has no positional content — delivery is strictly sequential ("zeros owed"
            // bookkeeping per fid, exactly the model the spike proved). Disposal (clunk)
            // also unparks the read, so a clunked fid can never strand a waiter.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
            var token = linked.Token;
            await _readGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_zerosOwed > 0)
                {
                    _zerosOwed--;
                    return ReadOnlyMemory<byte>.Empty;
                }

                while (_pendingAt >= _pending.Length)
                {
                    var snapshot = await _store.WaitForNextAsync(_lastSeq, token).ConfigureAwait(false);
                    _lastSeq = snapshot.Sequence;
                    if (snapshot.NewEvents.Count == 0)
                        continue;
                    using var lines = new MemoryStream();
                    foreach (var simEvent in snapshot.NewEvents)
                        lines.Write(Formats.EventLine(simEvent));
                    _pending = lines.ToArray();
                    _pendingAt = 0;
                }

                var length = (int)Math.Min(count, (uint)(_pending.Length - _pendingAt));
                var chunk = _pending.AsMemory(_pendingAt, length);
                _pendingAt += length;
                if (_pendingAt >= _pending.Length)
                    _zerosOwed = 2; // complete the syscall: two 0-byte Rreads (spike rule 2)
                return chunk;
            }
            finally
            {
                _readGate.Release();
            }
        }

        public void Dispose()
            // Cancel-only (see StreamFile.Handle.Dispose); the semaphore is left to the GC so
            // a still-parked read can release it safely.
            => _disposed.Cancel();
    }
}
