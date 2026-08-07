using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The one timeline primitive (plans/CAMERA_CONTROLS_PLAN.md §3.4): clock-base selection, rate
///     scaling, pause, loop wrapping, scrubbing, and the never-shrinking duration that makes a shared
///     group clock span its longest member.
/// </summary>
[TestFixture]
public sealed class PlaybackClockTests
{
    private static PlaybackClock Started(ClockBase clockBase, double durationMs = 0)
    {
        var clock = new PlaybackClock(clockBase) { DurationMs = durationMs };
        clock.Start();
        return clock;
    }

    [Test]
    public void EachBase_ConsumesOnlyItsOwnDelta()
    {
        var render = Started(ClockBase.Render);
        var wall = Started(ClockBase.Wall);
        var ut = Started(ClockBase.Ut);

        foreach (var clock in new[] { render, wall, ut })
            clock.Advance(16, 250, 1000);

        Assert.Multiple(() =>
        {
            Assert.That(render.PositionMs, Is.EqualTo(16));
            Assert.That(wall.PositionMs, Is.EqualTo(250));
            Assert.That(ut.PositionMs, Is.EqualTo(1000));
        });
    }

    [Test]
    public void NotStarted_DoesNotAdvance()
    {
        var clock = new PlaybackClock(ClockBase.Render);
        clock.Advance(16, 16, 16);
        Assert.Multiple(() =>
        {
            Assert.That(clock.Started, Is.False);
            Assert.That(clock.PositionMs, Is.EqualTo(0));
        });
    }

    [Test]
    public void Paused_IsANoOpAdvance()
    {
        var clock = Started(ClockBase.Render);
        clock.Advance(16, 16, 16);
        clock.Paused = true;
        clock.Advance(16, 16, 16);
        Assert.That(clock.PositionMs, Is.EqualTo(16));

        clock.Paused = false;
        clock.Advance(16, 16, 16);
        Assert.That(clock.PositionMs, Is.EqualTo(32));
    }

    [Test]
    public void Rate_ScalesTheDelta_AndIsClamped()
    {
        var clock = Started(ClockBase.Render);
        clock.Rate = 2;
        clock.Advance(10, 0, 0);
        Assert.That(clock.PositionMs, Is.EqualTo(20));

        Assert.Multiple(() =>
        {
            Assert.That(new PlaybackClock(ClockBase.Render) { Rate = -5 }.Rate, Is.EqualTo(PlaybackClock.MinRate));
            Assert.That(new PlaybackClock(ClockBase.Render) { Rate = 1e9 }.Rate, Is.EqualTo(PlaybackClock.MaxRate));
            Assert.That(new PlaybackClock(ClockBase.Render) { Rate = double.NaN }.Rate, Is.EqualTo(1));
        });
    }

    [Test]
    public void RateZero_FreezesWithoutBeingClampedAway()
    {
        var clock = Started(ClockBase.Render);
        clock.Rate = 0;
        clock.Advance(100, 100, 100);
        Assert.Multiple(() =>
        {
            Assert.That(clock.Rate, Is.EqualTo(0), "0 is a legal rate meaning 'frozen'");
            Assert.That(clock.PositionMs, Is.EqualTo(0));
            Assert.That(clock.Paused, Is.False, "frozen is not paused");
        });
    }

    [Test]
    public void NoLoop_ClampsAtDuration()
    {
        var clock = Started(ClockBase.Render, 100);
        clock.Advance(250, 0, 0);
        Assert.That(clock.PositionMs, Is.EqualTo(100));
    }

    [Test]
    public void Loop_WrapsKeepingTheRemainder_AndCountsWraps()
    {
        var clock = Started(ClockBase.Render, 100);
        clock.Loop = true;

        clock.Advance(120, 0, 0);
        Assert.Multiple(() =>
        {
            Assert.That(clock.PositionMs, Is.EqualTo(20).Within(1e-9), "the remainder carries over — no drift");
            Assert.That(clock.LoopCount, Is.EqualTo(1));
        });

        // A single huge delta (a hitch) wraps as many times as it spans.
        clock.Advance(330, 0, 0);
        Assert.Multiple(() =>
        {
            Assert.That(clock.PositionMs, Is.EqualTo(50).Within(1e-9));
            Assert.That(clock.LoopCount, Is.EqualTo(4));
        });
    }

    [Test]
    public void Scrub_BumpsGeneration_AndClampsAtZero()
    {
        var clock = Started(ClockBase.Render, 100);
        var before = clock.ScrubGeneration;

        clock.Scrub(60);
        Assert.Multiple(() =>
        {
            Assert.That(clock.PositionMs, Is.EqualTo(60));
            Assert.That(clock.ScrubGeneration, Is.EqualTo(before + 1));
        });

        clock.Scrub(-10);
        Assert.That(clock.PositionMs, Is.EqualTo(0));

        // Seeking past the end parks there rather than snapping back — a preview loop may overrun.
        clock.Scrub(500);
        Assert.That(clock.PositionMs, Is.EqualTo(500));

        var generation = clock.ScrubGeneration;
        clock.Scrub(double.NaN);
        Assert.Multiple(() =>
        {
            Assert.That(clock.PositionMs, Is.EqualTo(500), "a non-finite seek is ignored");
            Assert.That(clock.ScrubGeneration, Is.EqualTo(generation));
        });
    }

    [Test]
    public void Duration_TakesTheMax_AndNeverShrinks()
    {
        var clock = new PlaybackClock(ClockBase.Render) { DurationMs = 500 };
        clock.DurationMs = 200;
        Assert.That(clock.DurationMs, Is.EqualTo(500), "a shorter group member must not truncate the take");

        clock.DurationMs = 900;
        Assert.That(clock.DurationMs, Is.EqualTo(900));

        clock.DurationMs = double.PositiveInfinity;
        Assert.That(clock.DurationMs, Is.EqualTo(900), "non-finite is ignored");
    }

    [Test]
    public void SharedInstance_IsWhatMakesAGroupOneTake()
    {
        // A "group" is not a mechanism — it is several players holding the same clock.
        var shared = Started(ClockBase.Render, 100);
        var memberA = shared;
        var memberB = shared;

        memberA.Paused = true;
        Assert.That(memberB.Paused, Is.True);

        memberB.Scrub(40);
        Assert.That(memberA.PositionMs, Is.EqualTo(40));
    }
}
