using System.Buffers;
using System.Diagnostics;
using System.Text;
using gatOS.Logging;

namespace gatOS.SimFs.Display;

/// <summary>
///     One encoded Kitty frame on the output feed: a monotonic sequence and its bytes. Frame buffers
///     are pooled (PERF_IMPROVEMENT_PLAN.md P2) and shared by reference across every open reader, so
///     lifetime is reference-counted: the publisher holds one reference for the current-frame slot,
///     and each reader that obtained the frame via
///     <see cref="DisplaySurface.WaitForNextEncodedAsync"/> holds one until it calls
///     <see cref="Release"/>. When the count reaches zero the buffer returns to the pool. Unpooled
///     frames (<see cref="None"/>, debug text lines, tests) are immortal — retain/release no-op.
/// </summary>
public sealed class EncodedFrame
{
    private readonly ArrayPool<byte>? _pool;
    private readonly byte[] _bytes; // may be longer than Length when pooled
    private int _refs;

    internal EncodedFrame(long sequence, byte[] bytes, int length, ArrayPool<byte>? pool)
    {
        Sequence = sequence;
        Length = length;
        _bytes = bytes;
        _pool = pool;
        _refs = 1; // the publisher's current-frame reference
    }

    /// <summary>The feed's start state: nothing produced yet.</summary>
    public static EncodedFrame None { get; } = new(0, [], 0, null);

    /// <summary>Strictly increasing; readers track the last one they delivered.</summary>
    public long Sequence { get; }

    /// <summary>The frame's byte count (the backing buffer may be larger — it is pooled).</summary>
    public int Length { get; }

    /// <summary>The complete, self-contained Kitty frame bytes (see <see cref="KittyEncoder"/>).</summary>
    public ReadOnlyMemory<byte> Memory => _bytes.AsMemory(0, Length);

    /// <summary>
    ///     Takes a reference if the frame is still live; <c>false</c> means the publisher already
    ///     replaced and released it (its buffer may be back in the pool) — re-read the current frame.
    /// </summary>
    internal bool TryRetain()
    {
        if (_pool is null)
            return true;
        while (true)
        {
            var refs = Volatile.Read(ref _refs);
            if (refs == 0)
                return false;
            if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs)
                return true;
        }
    }

    /// <summary>Drops a reference; the last one returns the pooled buffer.</summary>
    internal void Release()
    {
        if (_pool is null)
            return;
        if (Interlocked.Decrement(ref _refs) == 0)
            _pool.Return(_bytes);
    }
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
///     <para><b>Demand-paced (PERF_IMPROVEMENT_PLAN.md P3).</b> When readers exist but none is parked
///     awaiting the next frame (all still draining the previous one — the saturated-transport case),
///     the worker drops the captured frame without encoding it: drop-old semantics would discard it
///     anyway, so the encode rate self-paces to actual consumption (see <see cref="EncodeSkips"/>).</para>
/// </remarks>
public sealed class DisplaySurface : IDisposable
{
    /// <summary>
    ///     Published frame buffers — pooled and reference-counted via <see cref="EncodedFrame"/> so
    ///     the steady-state encode→publish→read cycle allocates nothing. Private (not
    ///     <see cref="ArrayPool{T}.Shared"/>) because frames exceed the shared pool's 1 MiB bucket cap.
    /// </summary>
    private static readonly ArrayPool<byte> FrameBuffers = ArrayPool<byte>.Create(1 << 26, 8);

    private readonly object _inLock = new();
    private byte[] _inBuffer = [];
    private int _inWidth;
    private int _inHeight;
    private bool _inHasFrame;
    private volatile TaskCompletionSource _inSignal = NewSignal();

    private volatile EncodedFrame _current = EncodedFrame.None;
    private volatile TaskCompletionSource _outSignal = NewSignal();
    private long _sequence;

    // Keyframe cadence (PERF_IMPROVEMENT_PLAN.md P0.3): a keyframe (a=T, transmit+display)
    // [re]creates the placement; steady-state frames are a=t replaces (no placement churn — a
    // kitty display step allocates a terminal-side pin every time, and ghostty leaks it on
    // placement overwrite). Keyframe on: the first frame, a new reader (it has no placement
    // yet), a size/encoding change, and at least once per KeyframeInterval so a consumer that
    // lost its placement (terminal reset) or attached out-of-band recovers within ~1 s.
    private static readonly long KeyframeIntervalTicks = Stopwatch.Frequency;
    private int _forceKeyframe = 1; // first frame always displays
    private long _lastKeyframeTs;   // worker-thread only
    private int _kfWidth;           // worker-thread only: geometry/encoding the last keyframe carried
    private int _kfHeight;
    private DisplayEncoding _kfEncoding;

    private readonly CancellationTokenSource _stopping = new();
    private int _readers;
    private int _parkedWaiters; // readers currently awaiting the next frame (demand pacing, P3)
    private long _encodeSkips;
    private long _staticSkips;
    private Task? _worker;

    // Static-frame suppression (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP6): the previous
    // encoded frame's raw pixels, kept for an exact compare. Identical consecutive frames are
    // coalesced — no encode, no wire, no guest/terminal work — with the ~1 s keyframe cadence as
    // the heartbeat (late joiners and lost placements still recover within a keyframe interval).
    // Worker-thread only.
    private byte[] _previousPixels = [];
    private int _previousWidth;
    private int _previousHeight;

    private volatile string? _pngDumpDirectory;
    private long _lastPngWriteTs; // worker-thread only (the 1 Hz dump throttle)

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

    /// <summary>
    ///     Captured frames dropped without encoding because every reader was still draining the
    ///     previous frame (demand pacing, PERF_IMPROVEMENT_PLAN.md P3) — the encoder self-paces to
    ///     actual consumption when the transport saturates. Shown in the status window.
    /// </summary>
    public long EncodeSkips => Interlocked.Read(ref _encodeSkips);

    /// <summary>
    ///     Captured frames dropped because their pixels were byte-identical to the previous frame
    ///     (static-frame suppression, GP6): a still scene costs no encode and no wire beyond the
    ///     ~1 s keyframe heartbeat. Shown in the status window.
    /// </summary>
    public long StaticSkips => Interlocked.Read(ref _staticSkips);

    /// <summary>
    ///     The most recently encoded frame (starts at <see cref="EncodedFrame.None"/>). Reading its
    ///     <see cref="EncodedFrame.Sequence"/>/<see cref="EncodedFrame.Length"/> is always safe;
    ///     consuming its <b>bytes</b> requires the retention
    ///     <see cref="WaitForNextEncodedAsync"/> provides (the pooled buffer may otherwise be
    ///     recycled mid-read).
    /// </summary>
    public EncodedFrame Current => _current;

    /// <summary>
    ///     Tier-1/2 debug mode (STREAM_PLAN.md "Debugging the encoded stream"): when set, the encode
    ///     worker <b>does not publish Kitty bytes</b> — instead, at most once per second, it writes the
    ///     frame into this directory <b>twice</b>: <c>screencap-&lt;ISO 8601 UTC&gt;.png</c> (ground-truth
    ///     pixels, via <see cref="PngEncoder"/>) and <c>screencap-&lt;same stamp&gt;.kitty</c> (the exact
    ///     Kitty unit the live path would publish, via <see cref="KittyEncoder"/> at the current
    ///     <see cref="DisplaySettings.Encoding"/>). The pair is the tier-2 artifact: decode the
    ///     <c>.kitty</c> offline and diff against the sibling PNG (<c>KittyDumpPairTests</c>, gated on
    ///     <c>GATOS_KITTY_DUMP</c>), and a validated <c>.kitty</c> is the vendorable purrTTY test asset.
    ///     A one-line ASCII note per pair is published on the stream feed so an attached reader sees
    ///     progress (read it with small buffers, e.g. <c>dd bs=64</c> — see the class remarks on read
    ///     granularity). Capture gating is unchanged — frames only flow while
    ///     <see cref="DisplaySettings.Enabled"/> is set and a reader has the stream open. Null (the
    ///     default) restores normal Kitty encoding.
    /// </summary>
    public string? PngDumpDirectory
    {
        get => _pngDumpDirectory;
        set => _pngDumpDirectory = value;
    }

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

    /// <summary>
    ///     Registers an open reader (gates the capture via <see cref="HasReaders"/>). The next
    ///     encoded frame keyframes (<c>a=T</c>) so the newcomer gets a placement immediately
    ///     instead of waiting out the periodic keyframe interval.
    /// </summary>
    public void RegisterReader()
    {
        Interlocked.Exchange(ref _forceKeyframe, 1);
        Interlocked.Increment(ref _readers);
    }

    /// <summary>Unregisters a reader closed (clunk).</summary>
    public void UnregisterReader() => Interlocked.Decrement(ref _readers);

    /// <summary>
    ///     Completes with the first encoded frame whose sequence exceeds <paramref name="afterSequence"/>
    ///     (immediately when one is already current). Mirrors <c>SnapshotStore.WaitForNextAsync</c>; a
    ///     reader that fell behind resumes at the latest frame, not the next one (drop-old). The
    ///     returned frame is <b>retained for the caller</b> — call <see cref="EncodedFrame.Release"/>
    ///     once its bytes are no longer needed so the pooled buffer can be reused.
    /// </summary>
    public async ValueTask<EncodedFrame> WaitForNextEncodedAsync(long afterSequence, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // TryRetain fails only when the publisher already replaced (and released) the frame we
            // read — a newer one exists, so looping re-reads it. Progress is guaranteed.
            var frame = _current;
            if (frame.Sequence > afterSequence && frame.TryRetain())
                return frame;

            var signal = _outSignal;
            frame = _current; // re-check after capturing the signal (publish-between-reads race)
            if (frame.Sequence > afterSequence && frame.TryRetain())
                return frame;

            // Parked-waiter accounting drives the demand pacing (P3): the encode worker skips
            // frames while no reader is waiting for one.
            Interlocked.Increment(ref _parkedWaiters);
            try
            {
                await signal.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _parkedWaiters);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_stopping.IsCancellationRequested)
            return;
        _stopping.Cancel();
        // Unpark the worker if it is waiting on an input signal. The current frame's pooled buffer
        // is deliberately NOT released here: a concurrent in-flight Publish could double-release,
        // and the cost of skipping it is one un-returned (GC-reclaimed) buffer per surface lifetime.
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
                    // Pointer-swap the input buffer out instead of copying it (P2): the render
                    // thread's next SubmitFrame regrows the (possibly smaller) swapped-in array as
                    // needed. Saves a full frame memcpy per encode.
                    (work, _inBuffer) = (_inBuffer, work);
                    width = _inWidth;
                    height = _inHeight;
                    length = width * height * 4;
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
                if (_pngDumpDirectory is { } pngDir)
                {
                    DumpFramePair(pngDir, width, height, work.AsSpan(0, length));
                    continue;
                }

                // Demand pacing (P3): encoding a frame no reader is waiting for is pure waste under
                // saturation — drop-old means it would be skipped anyway. Readers mid-drain re-park
                // when they finish and pick up the NEXT capture (≤ one capture interval of added
                // latency). The bootstrap frame (sequence 0 → 1) always encodes so the feed starts
                // even before the first read parks, and the dump/debug branch above is exempt.
                if (_current.Sequence > 0 && HasReaders && Volatile.Read(ref _parkedWaiters) == 0)
                {
                    Interlocked.Increment(ref _encodeSkips);
                    continue;
                }

                var encoding = Settings.Encoding;

                // Static-frame suppression (GP6): a frame byte-identical to the previous one is
                // coalesced — the terminal keeps showing the stored image, so a still scene costs
                // no encode/wire/guest/parse work at all. Peeked (not consumed) keyframe conditions
                // exempt the ~1 s heartbeat, a pending reader-forced keyframe, and an encoding
                // change, so late joiners and lost placements still recover within a keyframe.
                // Exact equality (SIMD SequenceEqual), so there is no hash-collision caveat.
                var keyframeDue = Volatile.Read(ref _forceKeyframe) == 1
                                  || width != _kfWidth || height != _kfHeight || encoding != _kfEncoding
                                  || Stopwatch.GetTimestamp() - _lastKeyframeTs >= KeyframeIntervalTicks;
                if (!keyframeDue && _current.Sequence > 0
                    && width == _previousWidth && height == _previousHeight
                    && _previousPixels.AsSpan(0, length).SequenceEqual(work.AsSpan(0, length)))
                {
                    Interlocked.Increment(ref _staticSkips);
                    continue;
                }

                // Remember this frame for the next compare (only frames that reach the encoder —
                // demand-paced drops above never update the reference, which can only produce a
                // compare-unequal, never a false suppression).
                if (_previousPixels.Length < length)
                    _previousPixels = new byte[length];
                work.AsSpan(0, length).CopyTo(_previousPixels);
                _previousWidth = width;
                _previousHeight = height;

                // One fixed image id, re-transmitted each frame with no delete (the terminal replaces
                // the image in place — see KittyEncoder). Keyframe (a=T) only when a placement must be
                // [re]created; steady state is a=t replace frames (no per-frame placement churn).
                var display = Interlocked.Exchange(ref _forceKeyframe, 0) == 1
                              || width != _kfWidth || height != _kfHeight || encoding != _kfEncoding
                              || Stopwatch.GetTimestamp() - _lastKeyframeTs >= KeyframeIntervalTicks;

                // Encode straight into a pooled frame buffer (zero steady-state allocation; the
                // buffer returns to the pool when the publisher and every reader release the frame).
                var buffer = FrameBuffers.Rent(KittyEncoder.GetMaxEncodedLength(width, height, encoding));
                int encodedLength;
                try
                {
                    using (EncodeStat.Measure())
                        encodedLength = KittyEncoder.EncodeFrame(
                            width, height, work.AsSpan(0, length), encoding, display, KittyEncoder.VideoImageId, buffer);
                }
                catch
                {
                    FrameBuffers.Return(buffer);
                    throw;
                }

                if (display)
                {
                    _lastKeyframeTs = Stopwatch.GetTimestamp();
                    _kfWidth = width;
                    _kfHeight = height;
                    _kfEncoding = encoding;
                }

                Publish(buffer, encodedLength, FrameBuffers);
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"display: frame encode failed ({ex.Message}); dropping frame.");
            }
        }
    }

    /// <summary>
    ///     The tier-1/2 debug sink (see <see cref="PngDumpDirectory"/>): at most once per second, write
    ///     the frame as a PNG <b>and</b> as the exact live-path Kitty unit, sharing an ISO 8601 UTC
    ///     basic-format timestamp (colon-free, so Windows-safe), and publish a plain-text progress line
    ///     on the stream feed in place of the Kitty bytes. Frames inside the 1 s window are dropped,
    ///     decoupling the dump rate from the capture cadence.
    /// </summary>
    private void DumpFramePair(string dir, int width, int height, ReadOnlySpan<byte> bgra)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastPngWriteTs != 0 && now - _lastPngWriteTs < Stopwatch.Frequency)
            return;
        _lastPngWriteTs = now;

        byte[] png;
        byte[] kitty;
        using (EncodeStat.Measure())
        {
            png = PngEncoder.EncodeBgra(width, height, bgra);
            kitty = KittyEncoder.EncodeFrame(width, height, bgra, Settings.Encoding);
        }

        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'.'fff'Z'");
        File.WriteAllBytes(Path.Combine(dir, $"screencap-{stamp}.png"), png);
        File.WriteAllBytes(Path.Combine(dir, $"screencap-{stamp}.kitty"), kitty);
        var line = Encoding.ASCII.GetBytes(
            $"wrote screencap-{stamp}.{{png,kitty}} ({width}x{height}, png {png.Length} B, kitty {kitty.Length} B)\r\n");
        Publish(line, line.Length, pool: null); // debug text line — unpooled, immortal
    }

    private void Publish(byte[] bytes, int length, ArrayPool<byte>? pool)
    {
        var frame = new EncodedFrame(Interlocked.Increment(ref _sequence), bytes, length, pool);
        var previous = _current;
        _current = frame;
        Interlocked.Exchange(ref _outSignal, NewSignal()).TrySetResult();
        previous.Release(); // the current-frame slot's reference to the replaced frame
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
