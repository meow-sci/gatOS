using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Display;

/// <summary>
///     <c>/sim/display/stream</c> — the binary Kitty-graphics frame feed (STREAM_PLAN.md §4.1), a
///     <b>continuous stream</b> synthetic file: like reading an endless pipe (<c>cat /dev/urandom</c>),
///     a read parks until the next encoded frame and delivers its bytes, and it <b>never returns a
///     0-byte read</b> — so a single <c>cat /sim/display/stream</c> renders frame after frame forever
///     (Ctrl-C lands as a Tflush on the parked read). This is deliberately not the <see cref="EventsFile"/>
///     blocking-event model (which owes the kernel two 0-byte reads per event and so makes <c>cat</c>
///     hit EOF and exit) — a video feed wants uninterrupted flow.
/// </summary>
/// <remarks>
///     <para>Because a 0-byte read inside the claimed <c>i_size</c> becomes userspace ENODATA on ≥6.11
///     kernels (spike/NOTES.md rule 1), and a never-blocking frontier would let the read complete with
///     EOF, the file reports a very large <see cref="Size"/> and the handle simply blocks for the next
///     frame instead of ever returning empty. The large size never causes over-reads: under
///     <c>cache=none</c> the kernel issues one demand Tread per <c>read()</c> bounded by the user
///     buffer, and we always answer with frame bytes (or park).</para>
///     <para>Each open handle is an independent subscriber to <see cref="DisplaySurface"/>'s
///     latest-frame feed, so multiple terminals can watch at once; a reader that falls behind skips to
///     the latest frame (drop-old — a live view wants <i>current</i>, not a backlog). Marked
///     <see cref="VfsFile.IsStreaming"/> so the bulk scalar walk (HTTP/MQTT field mirror) never reads
///     it; binary-safe because the 9p Tread path carries raw bytes, and the Kitty payload is LF-free.</para>
/// </remarks>
public sealed class DisplayStreamFile : VfsFile
{
    // A large, always-ahead-of-offset i_size so the kernel never reports EOF on this endless stream.
    // Not a real length (the feed is unbounded); the handle never returns a 0-byte read inside it, so
    // the "0 inside i_size ⇒ ENODATA" hazard (spike rule 1) cannot trigger.
    private const long StreamSize = long.MaxValue;

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
    public override long Size => StreamSize;

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

        internal Handle(DisplaySurface surface)
        {
            _surface = surface;
            _surface.RegisterReader();
            _lastSeq = surface.Current.Sequence; // only frames produced after the open are delivered
        }

        public long Size => StreamSize;

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            // Offsets are ignored: delivery is strictly sequential. The read blocks for the next frame
            // and never returns empty, so cat streams forever (Tflush/clunk unpark it via the token).
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
            var token = linked.Token;
            await _readGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
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
                return chunk; // always > 0 — never EOF
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
