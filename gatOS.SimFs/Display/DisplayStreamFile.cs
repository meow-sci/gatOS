using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Display;

/// <summary>
///     <c>/sim/display/stream</c> — the binary Kitty-graphics frame feed (STREAM_PLAN.md §4.1), a
///     <b>blocking-event</b> synthetic file (the <see cref="EventsFile"/> model): a fresh read parks
///     until the next encoded frame, delivers it (across as many <c>count</c>-sized chunks as a frame
///     needs), then answers the kernel's continuation reads with 0 so the <c>read()</c> completes and
///     the bytes reach userspace. A guest <c>cat /sim/display/stream</c> renders frame after frame.
/// </summary>
/// <remarks>
///     Each open handle is an independent subscriber to <see cref="DisplaySurface"/>'s latest-frame
///     feed, so multiple terminals can watch at once; a reader that falls behind skips to the latest
///     frame (drop-old — a live view wants <i>current</i>, not a backlog). Marked
///     <see cref="VfsFile.IsStreaming"/> so the bulk scalar walk (HTTP/MQTT field mirror) never reads
///     it; binary-safe because the 9p Tread path carries raw bytes, and the Kitty payload is LF-free.
/// </remarks>
public sealed class DisplayStreamFile : VfsFile
{
    private readonly DisplaySurface _surface;

    /// <param name="name">The entry name (<c>stream</c>).</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="surface">The frame feed this file fans out.</param>
    public DisplayStreamFile(string name, ulong qidPath, DisplaySurface surface)
        : base(name, qidPath)
    {
        _surface = surface;
    }

    /// <inheritdoc />
    public override bool IsStreaming => true;

    /// <inheritdoc />
    // Truthful minimal claim (EventsFile rationale): a 0 size makes the kernel answer read() with
    // instant EOF and never issue a Tread; any positive claim must not exceed what a read delivers,
    // and every frame is at least one byte.
    public override long Size => 1;

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new Handle(_surface);

    private sealed class Handle : IVfsFileHandle
    {
        private readonly DisplaySurface _surface;
        private readonly SemaphoreSlim _readGate = new(1, 1);
        private readonly CancellationTokenSource _disposed = new();
        private long _lastSeq;
        private byte[] _pending = [];
        private int _pendingAt;
        private int _zerosOwed;

        internal Handle(DisplaySurface surface)
        {
            _surface = surface;
            _surface.RegisterReader();
            _lastSeq = surface.Current.Sequence; // only frames produced after the open are delivered
        }

        public long Size => 1;

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            // Offsets are ignored: delivery is strictly sequential ("zeros owed" bookkeeping per fid,
            // exactly the EventsFile model). Disposal (clunk) also unparks the read.
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
                    var frame = await _surface.WaitForNextEncodedAsync(_lastSeq, token).ConfigureAwait(false);
                    _lastSeq = frame.Sequence;
                    _pending = frame.Bytes;
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
        {
            _surface.UnregisterReader();
            _disposed.Cancel(); // cancel-only; the semaphore is left to the GC so a parked read releases safely
        }
    }
}
