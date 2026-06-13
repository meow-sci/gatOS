using System.Globalization;
using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs;

/// <summary>
///     <c>/sim/time/alarm</c> — the sim-time wake alarm (KSA_GAME_INTEGRATION_PLAN §2.1), the
///     <c>/dev/rtc</c> analogue for a fast-forwarding spacecraft. Write a target sim time (UT, in
///     seconds) to arm it; a subsequent <c>read</c> parks until the published sim time reaches the
///     target, then returns the reached UT. This is the correct way to "sleep 5 sim seconds" or
///     "wake at periapsis − 60 s" regardless of time-warp — never <c>sleep(5)</c> in the guest,
///     whose wall clock is unrelated to sim time.
/// </summary>
/// <remarks>
///     Combines the two spike-proven synthetic-file models: a line-buffered write (like a control
///     file) that sets the volatile target, and a blocking read (like <see cref="EventsFile"/>)
///     that completes the <c>read()</c> syscall with two trailing 0-byte Rreads. The target is
///     re-captured per read open, so each <c>cat</c> waits for the latest armed target. No command
///     pipeline is involved — the alarm is pure snapshot-time bookkeeping.
/// </remarks>
public sealed class AlarmFile : VfsFile
{
    private readonly SnapshotStore _store;
    private double _targetUt; // volatile by the lock-free single-double convention (set on write, read on open)

    /// <param name="name">The entry name (<c>alarm</c>).</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="store">The snapshot source whose <c>UtSeconds</c> the read waits on.</param>
    public AlarmFile(string name, ulong qidPath, SnapshotStore store)
        : base(name, qidPath)
    {
        _store = store;
    }

    /// <inheritdoc />
    public override bool IsWritable => true;

    /// <inheritdoc />
    public override bool IsStreaming => true; // blocking read — a scalar walk must skip it

    /// <summary>Blocking-read file: 1 is the only always-truthful size claim (see <see cref="EventsFile"/>).</summary>
    public override long Size => 1;

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new ReadHandle(_store, Volatile.Read(ref _targetUt));

    /// <inheritdoc />
    public override IVfsWritableFileHandle OpenWrite() => new WriteHandle(this);

    private void Arm(double targetUt) => Volatile.Write(ref _targetUt, targetUt);

    private sealed class ReadHandle(SnapshotStore store, double targetUt) : IVfsFileHandle
    {
        private readonly CancellationTokenSource _disposed = new();
        private byte[] _pending = [];
        private int _pendingAt;
        private int _zerosOwed;
        private bool _fired;

        public long Size => 1;

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
            var token = linked.Token;

            if (_zerosOwed > 0)
            {
                _zerosOwed--;
                return ReadOnlyMemory<byte>.Empty;
            }

            if (!_fired)
            {
                var reached = await WaitForTargetAsync(token).ConfigureAwait(false);
                _pending = Encoding.UTF8.GetBytes(Formats.Scalar(reached) + "\n");
                _pendingAt = 0;
                _fired = true;
            }

            if (_pendingAt >= _pending.Length)
                return ReadOnlyMemory<byte>.Empty;

            var length = (int)Math.Min(count, (uint)(_pending.Length - _pendingAt));
            var chunk = _pending.AsMemory(_pendingAt, length);
            _pendingAt += length;
            if (_pendingAt >= _pending.Length)
                _zerosOwed = 2; // complete the syscall (spike rule 2)
            return chunk;
        }

        private async ValueTask<double> WaitForTargetAsync(CancellationToken token)
        {
            var snapshot = store.Current;
            while (snapshot.UtSeconds < targetUt)
                snapshot = await store.WaitForNextAsync(snapshot.Sequence, token).ConfigureAwait(false);
            return snapshot.UtSeconds;
        }

        public void Dispose() => _disposed.Cancel();
    }

    private sealed class WriteHandle(AlarmFile file) : IVfsWritableFileHandle
    {
        private readonly List<byte> _buffer = [];
        private bool _armed;

        public ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!_armed)
            {
                _buffer.AddRange(data.Span);
                var newline = _buffer.IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    _armed = true;
                    Arm(Encoding.UTF8.GetString(_buffer.GetRange(0, newline).ToArray()));
                }
            }

            return ValueTask.FromResult((uint)data.Length);
        }

        public void Dispose()
        {
            if (_armed || _buffer.Count == 0)
                return;
            _armed = true;
            Arm(Encoding.UTF8.GetString(_buffer.ToArray()));
        }

        private void Arm(string text)
        {
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ut)
                || !double.IsFinite(ut))
                throw new VfsErrorException(LinuxErrno.EINVAL, $"alarm: '{text.Trim()}' is not a sim time");
            file.Arm(ut);
        }
    }
}
