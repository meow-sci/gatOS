using gatOS.Logging;

namespace gatOS.SimFs.Display;

/// <summary>One encoded Kitty frame on the output feed: a monotonic sequence and its bytes.</summary>
/// <param name="Sequence">Strictly increasing; readers track the last one they delivered.</param>
/// <param name="Bytes">The complete, self-contained Kitty frame (see <see cref="KittyEncoder"/>).</param>
public sealed record EncodedFrame(long Sequence, byte[] Bytes)
{
    /// <summary>The feed's start state: nothing produced yet.</summary>
    public static EncodedFrame None { get; } = new(0, []);
}

/// <summary>
///     The host-side hub of the screen stream (STREAM_PLAN.md §4.1): it owns the live
///     <see cref="DisplaySettings"/>, takes raw BGRA frames from the render-thread capture, encodes
///     them off that thread into Kitty graphics units, and publishes the latest to a single feed the
///     <see cref="DisplayStreamFile"/> handles fan out to every open reader (in-game purrTTY and
///     external SSH terminals alike).
/// </summary>
/// <remarks>
///     <para><b>Threading.</b> <see cref="SubmitFrame"/> runs on the render thread and does only a
///     buffer copy under a short lock (no encode, no allocation beyond a one-time/grow buffer) before
///     signalling the worker — honoring "never block the render thread". The worker thread swizzles,
///     compresses, base64s and frames; the output feed mirrors <c>SnapshotStore</c> (one volatile
///     swap + signal), so readers never lock and a slow reader simply skips to the latest frame
///     (drop-old by construction).</para>
///     <para><b>Idle by default.</b> The capture only submits while <see cref="DisplaySettings.Enabled"/>
///     is set <i>and</i> <see cref="HasReaders"/> is true, so an enabled-but-unwatched stream still
///     costs no GPU work — the surface idles until someone opens <c>/sim/display/stream</c>.</para>
/// </remarks>
public sealed class DisplaySurface : IDisposable
{
    private readonly object _inLock = new();
    private byte[] _inBuffer = [];
    private int _inWidth;
    private int _inHeight;
    private bool _inHasFrame;
    private volatile TaskCompletionSource _inSignal = NewSignal();

    private volatile EncodedFrame _current = EncodedFrame.None;
    private volatile TaskCompletionSource _outSignal = NewSignal();
    private long _sequence;

    private readonly CancellationTokenSource _stopping = new();
    private int _readers;
    private Task? _worker;

    /// <param name="settings">The live, runtime-mutable stream parameters (shared with the control files).</param>
    public DisplaySurface(DisplaySettings settings) => Settings = settings;

    /// <summary>The live stream parameters; the capture reads these every frame, control files mutate them.</summary>
    public DisplaySettings Settings { get; }

    /// <summary>Render-thread time spent capturing (blit + copy + map), recorded by the capture hook.</summary>
    public PerfStat CaptureStat { get; } = new();

    /// <summary>Worker-thread time spent encoding a frame to Kitty bytes.</summary>
    public PerfStat EncodeStat { get; } = new();

    /// <summary>Whether any reader currently has <c>/sim/display/stream</c> open.</summary>
    public bool HasReaders => Volatile.Read(ref _readers) > 0;

    /// <summary>Number of readers currently streaming (shown in the status window).</summary>
    public int ReaderCount => Volatile.Read(ref _readers);

    /// <summary>The most recently encoded frame (never null bytes; starts at <see cref="EncodedFrame.None"/>).</summary>
    public EncodedFrame Current => _current;

    /// <summary>Starts the encode worker. Idempotent.</summary>
    public void Start() => _worker ??= Task.Run(EncodeLoopAsync);

    /// <summary>
    ///     Hands a freshly captured frame to the encoder (render thread). <paramref name="bgra"/> is
    ///     copied into an internal reused buffer and the worker is signalled; an earlier undelivered
    ///     frame is overwritten (drop-old). No-op once disposed.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="bgra">Row-major top-to-bottom 32-bit BGRA, at least <c>width*height*4</c> bytes.</param>
    public void SubmitFrame(int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (_stopping.IsCancellationRequested || width <= 0 || height <= 0)
            return;
        var needed = width * height * 4;
        if (bgra.Length < needed)
            return;

        lock (_inLock)
        {
            if (_inBuffer.Length < needed)
                _inBuffer = new byte[needed];
            bgra[..needed].CopyTo(_inBuffer);
            _inWidth = width;
            _inHeight = height;
            _inHasFrame = true;
        }

        // Swap first, then complete (SnapshotStore ordering): a worker that captured the old signal
        // wakes and re-checks _inHasFrame; one already on the new signal waits for the next submit.
        Interlocked.Exchange(ref _inSignal, NewSignal()).TrySetResult();
    }

    /// <summary>Registers an open reader (gates the capture via <see cref="HasReaders"/>).</summary>
    public void RegisterReader() => Interlocked.Increment(ref _readers);

    /// <summary>Unregisters a reader closed (clunk).</summary>
    public void UnregisterReader() => Interlocked.Decrement(ref _readers);

    /// <summary>
    ///     Completes with the first encoded frame whose sequence exceeds <paramref name="afterSequence"/>
    ///     (immediately when one is already current). Mirrors <c>SnapshotStore.WaitForNextAsync</c>; a
    ///     reader that fell behind resumes at the latest frame, not the next one (drop-old).
    /// </summary>
    public async ValueTask<EncodedFrame> WaitForNextEncodedAsync(long afterSequence, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var frame = _current;
            if (frame.Sequence > afterSequence)
                return frame;

            var signal = _outSignal;
            frame = _current; // re-check after capturing the signal (publish-between-reads race)
            if (frame.Sequence > afterSequence)
                return frame;

            await signal.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_stopping.IsCancellationRequested)
            return;
        _stopping.Cancel();
        // Unpark the worker if it is waiting on an input signal.
        _inSignal.TrySetResult();
    }

    private async Task EncodeLoopAsync()
    {
        var token = _stopping.Token;
        var work = Array.Empty<byte>();
        while (!token.IsCancellationRequested)
        {
            var signal = _inSignal;
            int width, height, length;
            lock (_inLock)
            {
                if (_inHasFrame)
                {
                    length = _inWidth * _inHeight * 4;
                    if (work.Length < length)
                        work = new byte[length];
                    _inBuffer.AsSpan(0, length).CopyTo(work);
                    width = _inWidth;
                    height = _inHeight;
                    _inHasFrame = false;
                }
                else
                {
                    width = height = length = 0;
                }
            }

            if (length == 0)
            {
                try
                {
                    await signal.Task.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                // One fixed image id, deleted + re-transmitted each frame (the terminal updates a single
                // image in place — see KittyEncoder). A fresh id per frame would churn the terminal's
                // GPU image cache.
                byte[] encoded;
                using (EncodeStat.Measure())
                    encoded = KittyEncoder.EncodeFrame(width, height, work.AsSpan(0, length), Settings.Encoding);
                Publish(encoded);
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"display: frame encode failed ({ex.Message}); dropping frame.");
            }
        }
    }

    private void Publish(byte[] bytes)
    {
        _current = new EncodedFrame(Interlocked.Increment(ref _sequence), bytes);
        Interlocked.Exchange(ref _outSignal, NewSignal()).TrySetResult();
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
