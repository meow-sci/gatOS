using gatOS.SimFs.Display;

namespace gatOS.SimFs.Tests.Display;

/// <summary>
///     STREAM_PLAN.md §4.1: the surface takes BGRA frames off the render thread, encodes them on a
///     worker, and publishes the latest to a drop-old feed.
/// </summary>
[TestFixture]
public sealed class DisplaySurfaceTests
{
    private DisplaySurface _surface = null!;

    [SetUp]
    public void SetUp()
    {
        _surface = new DisplaySurface(new DisplaySettings(enabled: true, fps: 30, width: 4, height: 4));
        _surface.Start();
    }

    [TearDown]
    public void TearDown() => _surface.Dispose();

    [Test]
    public async Task SubmitFrame_ProducesAnEncodedFrameOnTheFeed()
    {
        _surface.SubmitFrame(4, 4, Solid(4, 4));
        var frame = await _surface.WaitForNextEncodedAsync(0, Cancel(5)).AsTask();

        Assert.That(frame.Sequence, Is.GreaterThan(0));
        Assert.That(frame.Length, Is.GreaterThan(0));
        // It is a real Kitty unit (starts with the save-cursor wrapper).
        Assert.That(frame.Memory.Span[0], Is.EqualTo((byte)0x1b));
    }

    [Test]
    public async Task ReaderCount_TracksRegistration()
    {
        Assert.That(_surface.HasReaders, Is.False);
        _surface.RegisterReader();
        _surface.RegisterReader();
        Assert.That(_surface.ReaderCount, Is.EqualTo(2));
        _surface.UnregisterReader();
        Assert.That(_surface.HasReaders, Is.True);
        _surface.UnregisterReader();
        Assert.That(_surface.HasReaders, Is.False);
        await Task.CompletedTask;
    }

    [Test]
    public async Task SlowReader_SkipsToTheLatestFrame_DropOld()
    {
        // Drive several frames through; a reader starting at seq 0 should land on the latest, not seq 1.
        _surface.SubmitFrame(4, 4, Solid(4, 4));
        var first = await _surface.WaitForNextEncodedAsync(0, Cancel(5)).AsTask();

        for (var i = 0; i < 5; i++)
        {
            // Varying content — identical consecutive frames are coalesced (GP6).
            _surface.SubmitFrame(4, 4, Solid(4, 4, (byte)(i + 1)));
            // Let the worker advance the sequence.
            await WaitForSequenceBeyondAsync(first.Sequence + i, 5);
        }

        var latest = await _surface.WaitForNextEncodedAsync(first.Sequence, Cancel(5)).AsTask();
        Assert.That(latest.Sequence, Is.GreaterThan(first.Sequence));
        Assert.That(latest.Sequence, Is.EqualTo(_surface.Current.Sequence));
    }

    [Test]
    public async Task Feed_KeyframesFirst_ThenReplaces_AndKeyframesForANewReader()
    {
        // First frame must display (a=T — nothing has a placement yet).
        _surface.SubmitFrame(4, 4, Solid(4, 4));
        var first = await _surface.WaitForNextEncodedAsync(0, Cancel(5)).AsTask();
        Assert.That(KittyStrict.ValidateFrame(first.Memory.ToArray()).Display, Is.True,
            "the first frame must be a keyframe (creates the placement)");

        // Steady state replaces in place (a=t) — no per-frame placement churn. (Different pixels —
        // an identical frame would be coalesced, GP6.)
        _surface.SubmitFrame(4, 4, Solid(4, 4, 200));
        var second = await _surface.WaitForNextEncodedAsync(first.Sequence, Cancel(5)).AsTask();
        Assert.That(KittyStrict.ValidateFrame(second.Memory.ToArray()).Display, Is.False,
            "steady-state frames must be a=t replaces");

        // A new reader has no placement — the very next frame must keyframe for it. (Park the
        // wait before submitting: with a reader registered, demand pacing skips the encode of a
        // frame nobody is waiting on.)
        _surface.RegisterReader();
        try
        {
            var third = _surface.WaitForNextEncodedAsync(second.Sequence, Cancel(5)).AsTask();
            _surface.SubmitFrame(4, 4, Solid(4, 4, 50));
            Assert.That(KittyStrict.ValidateFrame((await third).Memory.ToArray()).Display, Is.True,
                "a new reader must get a keyframe immediately");
        }
        finally
        {
            _surface.UnregisterReader();
        }
    }

    [Test]
    public async Task Encode_IsDemandPaced_SkipsFramesNoReaderIsWaitingFor()
    {
        _surface.RegisterReader(); // a reader exists but is NOT parked on the feed
        try
        {
            // The bootstrap frame always encodes so the feed starts.
            _surface.SubmitFrame(4, 4, Solid(4, 4));
            var first = await _surface.WaitForNextEncodedAsync(0, Cancel(5)).AsTask();

            // Nobody is waiting now — these captures must be dropped without encoding.
            _surface.SubmitFrame(4, 4, Solid(4, 4));
            _surface.SubmitFrame(4, 4, Solid(4, 4));
            await WaitForAsync(() => _surface.EncodeSkips >= 1, 5);
            Assert.That(_surface.Current.Sequence, Is.EqualTo(first.Sequence),
                "a frame no reader awaits must not be encoded (drop-old would discard it anyway)");

            // A parked reader resumes the flow on the next (changed — GP6) capture.
            var next = _surface.WaitForNextEncodedAsync(first.Sequence, Cancel(5)).AsTask(); // parks
            _surface.SubmitFrame(4, 4, Solid(4, 4, 7));
            Assert.That((await next).Sequence, Is.GreaterThan(first.Sequence));
        }
        finally
        {
            _surface.UnregisterReader();
        }
    }

    [Test]
    public async Task Feed_KeyframesWhenTheGeometryChanges()
    {
        _surface.SubmitFrame(4, 4, Solid(4, 4));
        var first = await _surface.WaitForNextEncodedAsync(0, Cancel(5)).AsTask();

        // A size change re-transmits with new s/v — the placement must be re-established.
        _surface.SubmitFrame(6, 4, Solid(6, 4));
        var resized = await _surface.WaitForNextEncodedAsync(first.Sequence, Cancel(5)).AsTask();
        var decoded = KittyStrict.ValidateFrame(resized.Memory.ToArray());
        Assert.That((decoded.Width, decoded.Height), Is.EqualTo((6, 4)));
        Assert.That(decoded.Display, Is.True, "a geometry change must keyframe");
    }

    [Test]
    public async Task IdenticalFrames_AreCoalesced_UntilSomethingChanges()
    {
        // GP6 static-frame suppression: a byte-identical capture publishes nothing (the terminal
        // keeps showing the stored image; the ~1 s keyframe cadence remains the heartbeat).
        _surface.SubmitFrame(4, 4, Solid(4, 4));
        var first = await _surface.WaitForNextEncodedAsync(0, Cancel(5)).AsTask();

        var parked = _surface.WaitForNextEncodedAsync(first.Sequence, Cancel(10)).AsTask();
        _surface.SubmitFrame(4, 4, Solid(4, 4)); // identical — must be coalesced
        await WaitForAsync(() => _surface.StaticSkips >= 1, 5);
        Assert.That(_surface.StaticSkips, Is.GreaterThanOrEqualTo(1));
        Assert.That(_surface.Current.Sequence, Is.EqualTo(first.Sequence),
            "an identical frame must not advance the feed");

        _surface.SubmitFrame(4, 4, Solid(4, 4, 99)); // changed — flows immediately
        Assert.That((await parked).Sequence, Is.GreaterThan(first.Sequence));
    }

    [Test]
    public void Dispose_StopsTheWorker_Idempotently()
    {
        _surface.Dispose();
        Assert.DoesNotThrow(() => _surface.Dispose());
        // Submitting after dispose is a no-op (no throw, no feed advance).
        var seq = _surface.Current.Sequence;
        _surface.SubmitFrame(4, 4, Solid(4, 4));
        Assert.That(_surface.Current.Sequence, Is.EqualTo(seq));
    }

    private async Task WaitForSequenceBeyondAsync(long sequence, int seconds)
    {
        var deadline = TimeSpan.FromSeconds(seconds);
        var spun = TimeSpan.Zero;
        while (_surface.Current.Sequence <= sequence && spun < deadline)
        {
            await Task.Delay(10);
            spun += TimeSpan.FromMilliseconds(10);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int seconds)
    {
        var deadline = TimeSpan.FromSeconds(seconds);
        var spun = TimeSpan.Zero;
        while (!condition() && spun < deadline)
        {
            await Task.Delay(10);
            spun += TimeSpan.FromMilliseconds(10);
        }
    }

    private static byte[] Solid(int w, int h, byte value = 128)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, value);
        return px;
    }

    private static CancellationToken Cancel(int seconds)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}
