using gatOS.SimFs.Telemetry;

namespace gatOS.SimFs.Tests.Telemetry;

/// <summary>OS_PLAN.md T9.1 (pure part): the dt-accumulating rate limiter.</summary>
[TestFixture]
public sealed class SampleClockTests
{
    [Test]
    public void FiresAtTheConfiguredRate()
    {
        var clock = new SampleClock(10); // every 100 ms
        var fired = 0;
        for (var frame = 0; frame < 100; frame++) // 100 frames × 16.7 ms ≈ 1.67 s
            if (clock.Tick(1.0 / 60))
                fired++;
        Assert.That(fired, Is.InRange(15, 17), "≈10 Hz over 1.67 s");
    }

    [Test]
    public void LongFrame_FiresOnceAndDropsMissedIntervals()
    {
        var clock = new SampleClock(10);
        Assert.Multiple(() =>
        {
            Assert.That(clock.Tick(5.0), Is.True, "a 5 s hitch still fires only this once");
            Assert.That(clock.Tick(0.01), Is.False, "no burst of catch-up samples");
        });
    }

    [Test]
    public void GarbageDt_IsIgnored()
    {
        var clock = new SampleClock(10);
        Assert.Multiple(() =>
        {
            Assert.That(clock.Tick(double.NaN), Is.False);
            Assert.That(clock.Tick(-1), Is.False);
            Assert.That(clock.Tick(double.PositiveInfinity), Is.False);
            Assert.That(clock.Tick(0.2), Is.True, "still functional after garbage");
        });
    }

    [Test]
    public void Reset_DropsTheAccumulator()
    {
        var clock = new SampleClock(10);
        clock.Tick(0.09);
        clock.Reset();
        Assert.Multiple(() =>
        {
            Assert.That(clock.Tick(0.05), Is.False, "the pre-reset 90 ms must be gone");
            Assert.That(clock.Tick(0.05), Is.True);
        });
    }

    [Test]
    public void InvalidRate_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SampleClock(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SampleClock(double.NaN));
    }
}
