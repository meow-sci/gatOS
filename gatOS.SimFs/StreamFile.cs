using gatOS.NineP.Vfs;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs;

/// <summary>
///     A per-vessel NDJSON telemetry stream — the <b>growing-log</b> synthetic-file model
///     (OS_PLAN.md T8.3; spike/NOTES.md T1.2 rule 3): reads never block, a read at the
///     frontier returns 0 bytes, and size = bytes produced so far. That is exactly what
///     <c>tail -f</c> needs — its 1 Hz stat-poll sees size grow and paces the follow reads —
///     and a plain <c>cat</c> prints the current line and exits (sample-now semantics).
/// </summary>
/// <remarks>
///     Each open handle owns an append-only buffer seeded with the current snapshot's line
///     (so the first stat never reports 0 — a 0 size would suppress reads entirely, spike
///     rule 1) and a pump task appending one line per published snapshot until the handle is
///     disposed (clunk).
/// </remarks>
public sealed class StreamFile : VfsFile
{
    /// <summary>Per-handle buffer cap; on overflow whole lines are dropped from the front.</summary>
    public const int BufferCap = 256 * 1024;

    private readonly SnapshotStore _store;
    private readonly string _vesselId;

    /// <param name="name">The entry name (<c>stream</c>).</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="store">The snapshot source.</param>
    /// <param name="vesselId">The vessel whose telemetry this stream carries.</param>
    public StreamFile(string name, ulong qidPath, SnapshotStore store, string vesselId)
        : base(name, qidPath)
    {
        _store = store;
        _vesselId = vesselId;
    }

    /// <inheritdoc />
    public override bool IsStreaming => true;

    /// <inheritdoc />
    public override long Size
    {
        get
        {
            // Truthful unopened size: what a fresh open would seed its buffer with.
            var snapshot = _store.Current;
            var vessel = FindVessel(snapshot, _vesselId);
            return vessel is null ? 0 : Formats.StreamLine(snapshot, vessel).Length;
        }
    }

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new Handle(_store, _vesselId);

    private static VesselSnapshot? FindVessel(SimSnapshot snapshot, string vesselId)
    {
        foreach (var vessel in snapshot.Vessels)
            if (vessel.Id == vesselId)
                return vessel;
        return null;
    }

    private sealed class Handle : IVfsFileHandle
    {
        private readonly SnapshotStore _store;
        private readonly string _vesselId;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();

        private byte[] _buffer = new byte[4096];
        private long _bufferStart; // absolute offset of _buffer[0]
        private int _bufferLength;

        internal Handle(SnapshotStore store, string vesselId)
        {
            _store = store;
            _vesselId = vesselId;

            // Seed with the current snapshot so the first Tgetattr reports a non-zero size.
            var snapshot = store.Current;
            var lastSeq = snapshot.Sequence;
            if (FindVessel(snapshot, vesselId) is { } vessel)
                Append(Formats.StreamLine(snapshot, vessel));
            _ = Task.Run(() => PumpAsync(lastSeq));
        }

        public long Size
        {
            get
            {
                lock (_lock)
                {
                    return _bufferStart + _bufferLength;
                }
            }
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            lock (_lock)
            {
                var produced = _bufferStart + _bufferLength;
                if ((long)offset >= produced)
                    return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); // frontier: 0, never block

                // A reader that fell behind a trimmed buffer resumes at the oldest retained
                // byte (cat/tail never rewind; documented in OS_PLAN.md T8.3).
                var effective = Math.Max((long)offset, _bufferStart);
                var available = (int)(produced - effective);
                var length = (int)Math.Min(count, (uint)available);
                var copy = new byte[length];
                _buffer.AsSpan((int)(effective - _bufferStart), length).CopyTo(copy);
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(copy);
            }
        }

        public void Dispose()
            // Cancel only — disposing the CTS would race the pump's next Token access; a
            // timer-less CTS needs no deterministic disposal.
            => _cts.Cancel();

        private async Task PumpAsync(long lastSeq)
        {
            try
            {
                while (true)
                {
                    var snapshot = await _store.WaitForNextAsync(lastSeq, _cts.Token).ConfigureAwait(false);
                    lastSeq = snapshot.Sequence;
                    if (FindVessel(snapshot, _vesselId) is { } vessel)
                        Append(Formats.StreamLine(snapshot, vessel));
                }
            }
            catch (OperationCanceledException)
            {
                // Handle disposed (clunk).
            }
        }

        private void Append(byte[] line)
        {
            lock (_lock)
            {
                if (_bufferLength + line.Length > BufferCap)
                    TrimWholeLines(needed: line.Length);
                EnsureCapacity(_bufferLength + line.Length);
                line.CopyTo(_buffer.AsSpan(_bufferLength));
                _bufferLength += line.Length;
            }
        }

        /// <summary>Drops whole lines from the front and notes the gap (OS_PLAN.md T8.3).</summary>
        private void TrimWholeLines(int needed)
        {
            var notice = Formats.DroppedNoticeLine();
            var mustFree = needed + notice.Length;
            var span = _buffer.AsSpan(0, _bufferLength);
            var drop = 0;
            while (drop < mustFree && drop < _bufferLength)
            {
                var newline = span[drop..].IndexOf((byte)'\n');
                if (newline < 0)
                {
                    drop = _bufferLength; // no line boundary left: drop everything
                    break;
                }

                drop += newline + 1;
            }

            span[drop..].CopyTo(_buffer);
            _bufferStart += drop;
            _bufferLength -= drop;

            notice.CopyTo(_buffer.AsSpan(_bufferLength));
            _bufferLength += notice.Length;
        }

        private void EnsureCapacity(int required)
        {
            if (_buffer.Length >= required)
                return;
            var grown = Math.Max(_buffer.Length * 2, required);
            Array.Resize(ref _buffer, Math.Min(Math.Max(grown, 4096), Math.Max(BufferCap + 4096, required)));
        }
    }
}
