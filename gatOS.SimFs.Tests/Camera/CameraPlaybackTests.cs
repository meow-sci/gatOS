using System.Text;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     <see cref="CameraPlayback"/> and <see cref="CameraPlaybackController"/>: the camera-flavoured
///     verbs as a thin wrapper over the <c>/sim/ctl/schedules/</c> registry, the shared-clock group, the
///     commit-time validator, and the <c>camera.shot</c>/<c>camera.finished</c> events.
/// </summary>
[TestFixture]
public sealed class CameraPlaybackTests
{
    private const string TwoShots =
        """
        { "shots": [
          { "name":"wide",  "t":0, "duration":4, "fov": [ {"t":0,"v":60}, {"t":4,"v":40} ] },
          { "name":"close", "t":4, "duration":4, "fov": [ {"t":0,"v":40}, {"t":4,"v":18} ] }
        ] }
        """;

    private CameraStore _camera = null!;
    private ScheduleStore _schedules = null!;
    private CameraPlaybackController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _camera = new CameraStore();
        _schedules = new ScheduleStore();
        _controller = new CameraPlaybackController(_camera, _schedules);
    }

    private void Upload(string name, string json)
    {
        var upload = _camera.OpenUpload(name, mustCreate: !_camera.Exists(name));
        upload.SetLength(0);
        upload.Write(0, Encoding.UTF8.GetBytes(json));
        upload.Commit();
    }

    private CommandResult Play(string line)
        => _controller.Execute(CameraCommands.ParsePlay(line)
                               ?? throw new InvalidOperationException($"unparseable play line '{line}'"));

    private CommandResult Set(string line)
        => _controller.Execute(CameraCommands.ParseSet(line)
                               ?? throw new InvalidOperationException($"unparseable set line '{line}'"));

    private CommandResult Stop()
        => _controller.Execute(new SimCommand("", CameraCommands.StopAction, SimCommand.NoOrdinal, 1));

    private static void Advance(ScheduleStore store, double ms) => store.AdvanceAll(ms, ms, ms);

    private IReadOnlyList<SimEvent> Events() => _camera.DrainEvents();

    // ---- the commit-time validator --------------------------------------------------------------------

    [Test]
    public void AMalformedTrack_FailsTheUploadRatherThanThePlay()
    {
        var upload = _camera.OpenUpload("bad", mustCreate: true);
        upload.Write(0, "{ \"shots\": [] }"u8);

        var ex = Assert.Throws<VfsErrorException>(() => upload.Commit())!;
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("shots is empty"));
            // The clunk that carried this could not report an errno, so the diagnosis also has to be
            // readable somewhere the author can find it.
            Assert.That(_controller.LastTrackError, Does.StartWith("bad:").And.Contain("shots is empty"));
        });
    }

    [Test]
    public void AMalformedTrackStaysOnDiskAndFailsPlayWithItsParseMessage()
    {
        var upload = _camera.OpenUpload("bad", mustCreate: true);
        upload.Write(0, "{ \"shots\": [] }"u8);
        Assert.Throws<VfsErrorException>(() => upload.Commit());

        var result = Play("bad");
        Assert.Multiple(() =>
        {
            Assert.That(_camera.SizeOf("bad"), Is.GreaterThan(0), "the bytes stay so `cat` shows what was written");
            Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Invalid));
            Assert.That(result.Message, Does.Contain("shots is empty"));
        });
    }

    [Test]
    public void AnEmptyCommit_IsRecordedButDoesNotThrow()
    {
        var upload = _camera.OpenUpload("blank", mustCreate: true);
        Assert.DoesNotThrow(() => upload.Commit(), "a bare truncate is not an authoring error");
        Assert.That(_controller.LastTrackError, Does.Contain("blank:"));
    }

    // ---- play --------------------------------------------------------------------------------------------

    [Test]
    public void PlayingAMissingTrack_IsENOENT()
        => Assert.That(Play("nope").Outcome, Is.EqualTo(CommandOutcome.NotFound));

    [Test]
    public void PlayingAnUncommittedTrack_IsEBUSY()
    {
        var upload = _camera.OpenUpload("flyby", mustCreate: true);
        upload.Write(0, Encoding.UTF8.GetBytes(TwoShots));
        Assert.That(Play("flyby").Outcome, Is.EqualTo(CommandOutcome.Busy));
    }

    [Test]
    public void Play_RegistersACameraTrackPlayerInTheScheduleRegistry()
    {
        Upload("flyby", TwoShots);
        Assert.That(Play("flyby").Outcome, Is.EqualTo(CommandOutcome.Ok));

        var player = _schedules.Players.Single();
        Assert.Multiple(() =>
        {
            Assert.That(player.Kind, Is.EqualTo(CameraPlayback.CameraTrackKind));
            Assert.That(player.Kind, Is.EqualTo("camera-track"), "the leaf value is part of the API");
            Assert.That(player.Id, Is.EqualTo(CameraPlaybackController.PreferredId));
            Assert.That(player.DurationMs, Is.EqualTo(8000.0));
            Assert.That(player.State, Is.EqualTo(PlaybackState.Running));
            Assert.That(player.PendingCount, Is.EqualTo(0), "a track fires no entries");
            Assert.That(player.Dropped, Is.EqualTo(0));
            Assert.That(player.LastError, Is.EqualTo("-"));
            Assert.That(player.Clock.Base, Is.EqualTo(ClockBase.Render));
            Assert.That(player.OwnsClock, Is.True);
            Assert.That(_schedules.Find(CameraPlaybackController.PreferredId), Is.SameAs(player));
        });
    }

    [Test]
    public void Play_HonoursAtRateAndLoop()
    {
        Upload("flyby", TwoShots);
        Assert.That(Play("flyby at 2 rate 2.5 loop 1").Outcome, Is.EqualTo(CommandOutcome.Ok));

        var clock = _controller.Current!.Clock;
        Assert.Multiple(() =>
        {
            Assert.That(clock.PositionMs, Is.EqualTo(2000.0));
            Assert.That(clock.Rate, Is.EqualTo(2.5));
            Assert.That(clock.Loop, Is.True);
        });
    }

    [Test]
    public void ATracksOwnLoopFlag_AppliesWhenThePlayLineIsSilent()
    {
        Upload("looper", """{ "loop": true, "shots": [ { "duration":4, "fov": [ {"t":0,"v":30} ] } ] }""");
        Play("looper");
        Assert.That(_controller.Current!.Clock.Loop, Is.True);
    }

    [Test]
    public void PlayingAgain_ReplacesTheTakeAndFreesTheRegistrySlot()
    {
        Upload("a", TwoShots);
        Upload("b", TwoShots);
        Play("a");
        Events();

        Play("b");

        Assert.Multiple(() =>
        {
            Assert.That(_schedules.Players, Has.Count.EqualTo(1));
            Assert.That(_controller.Current!.TrackName, Is.EqualTo("b"));
            Assert.That(Events().Any(e => e.Type == "camera.finished" && e.Detail.Contains("reason=replaced")),
                Is.True);
        });
    }

    [Test]
    public void APreferredIdCollision_FallsBackToAnAutoId()
    {
        Upload("flyby", TwoShots);
        _schedules.ReserveId(CameraPlaybackController.PreferredId);

        Play("flyby");
        Assert.That(_controller.Current!.Id, Does.StartWith("#"));
    }

    // ---- set / stop --------------------------------------------------------------------------------------

    [Test]
    public void SetWithNothingPlaying_IsENOENT()
        => Assert.That(Set("rate 2").Outcome, Is.EqualTo(CommandOutcome.NotFound));

    [Test]
    public void Set_DrivesTheSameClockLeavesTheScheduleVerbsDo()
    {
        Upload("flyby", TwoShots);
        Play("flyby");
        var clock = _controller.Current!.Clock;

        Assert.Multiple(() =>
        {
            Assert.That(Set("t 3").Outcome, Is.EqualTo(CommandOutcome.Ok));
            Assert.That(clock.PositionMs, Is.EqualTo(3000.0));
            Assert.That(Set("rate 0.25 paused 1 loop 1").Outcome, Is.EqualTo(CommandOutcome.Ok));
            Assert.That(clock.Rate, Is.EqualTo(0.25));
            Assert.That(clock.Paused, Is.True);
            Assert.That(clock.Loop, Is.True);
            Assert.That(_controller.Current.State, Is.EqualTo(PlaybackState.Paused));
        });
    }

    [Test]
    public void APausedPlayer_DoesNotAdvance()
    {
        Upload("flyby", TwoShots);
        Play("flyby");
        Set("paused 1");
        Advance(_schedules, 500);
        Assert.That(_controller.Current!.Clock.PositionMs, Is.EqualTo(0.0));
    }

    [Test]
    public void RateScalesTheAdvance()
    {
        Upload("flyby", TwoShots);
        Play("flyby rate 2");
        Advance(_schedules, 500);
        Assert.That(_controller.Current!.Clock.PositionMs, Is.EqualTo(1000.0));
    }

    [Test]
    public void ALoopingTrack_WrapsAndKeepsTheRemainder()
    {
        Upload("flyby", TwoShots);
        Play("flyby loop 1");
        Advance(_schedules, 8500);
        Assert.Multiple(() =>
        {
            Assert.That(_controller.Current!.Clock.PositionMs, Is.EqualTo(500.0).Within(1e-9));
            Assert.That(_controller.Current.Clock.LoopCount, Is.EqualTo(1));
            Assert.That(_controller.Current.State, Is.EqualTo(PlaybackState.Running), "a looping take never finishes");
        });
    }

    [Test]
    public void StopReleasesTheChannels_WhileCompletingHoldsThem()
    {
        Upload("flyby", TwoShots);
        Play("flyby");
        Advance(_schedules, 9000);

        Assert.Multiple(() =>
        {
            Assert.That(_controller.Current!.State, Is.EqualTo(PlaybackState.Done));
            Assert.That(_controller.TryEvaluateNow(out var held, out var channels), Is.True,
                "a completed take holds its final pose rather than snapping away");
            Assert.That(held.Fov, Is.EqualTo(18.0));
            Assert.That(channels, Is.EqualTo(CameraChannelMask.Fov));
        });

        Assert.That(Stop().Outcome, Is.EqualTo(CommandOutcome.Ok));
        Assert.Multiple(() =>
        {
            Assert.That(_controller.Current, Is.Null);
            Assert.That(_controller.TryEvaluateNow(out _, out var none), Is.False);
            Assert.That(none, Is.EqualTo(CameraChannelMask.None));
            Assert.That(_schedules.Players, Is.Empty, "stopping frees the registry slot");
        });
    }

    [Test]
    public void StopWithNothingPlaying_IsIdempotent()
        => Assert.That(Stop().Outcome, Is.EqualTo(CommandOutcome.Ok));

    [Test]
    public void Clear_DropsThePlayerAndTheParsedTrackCache()
    {
        Upload("flyby", TwoShots);
        Play("flyby");
        _controller.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(_controller.Current, Is.Null);
            Assert.That(_schedules.Players, Is.Empty);
            Assert.That(_controller.LastTrackError, Is.EqualTo("-"));
        });
    }

    // ---- evaluation seam -----------------------------------------------------------------------------------

    [Test]
    public void TryEvaluate_TracksTheClockAndDeclaresOnlyTheShotsChannels()
    {
        Upload("flyby", TwoShots);
        Play("flyby");

        Assert.That(_controller.TryEvaluate(0.0, out var start, out var channels), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(start.Fov, Is.EqualTo(60.0));
            Assert.That(channels, Is.EqualTo(CameraChannelMask.Fov));
        });

        Advance(_schedules, 4000);
        Assert.That(_controller.TryEvaluateNow(out var mid, out _), Is.True);
        Assert.That(mid.Fov, Is.EqualTo(40.0), "TryEvaluateNow must ride the player's own clock");
    }

    [Test]
    public void WithNothingPlaying_TheSeamSaysSo()
    {
        Assert.That(_controller.TryEvaluate(3.0, out var pose, out var channels), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(channels, Is.EqualTo(CameraChannelMask.None));
            Assert.That(pose, Is.EqualTo(CameraPose.Default));
        });
    }

    // ---- events ---------------------------------------------------------------------------------------------

    [Test]
    public void ShotBoundaries_EmitCameraShot()
    {
        Upload("flyby", TwoShots);
        Play("flyby");

        _controller.TryEvaluateNow(out _, out _);
        var first = Events().Single();
        Assert.That(first.Type, Is.EqualTo("camera.shot"));
        Assert.That(first.Detail, Is.EqualTo("camera track=flyby shot=0 name=wide"));

        _controller.TryEvaluateNow(out _, out _);
        Assert.That(Events(), Is.Empty, "the same shot must not re-announce every frame");

        Advance(_schedules, 4000);
        _controller.TryEvaluateNow(out _, out _);
        Assert.That(Events().Single().Detail, Is.EqualTo("camera track=flyby shot=1 name=close"));
    }

    [Test]
    public void RunningOffTheEnd_EmitsCameraFinishedOnce()
    {
        Upload("flyby", TwoShots);
        Play("flyby");
        Advance(_schedules, 9000);

        _controller.TryEvaluateNow(out _, out _);
        var finished = Events().Where(e => e.Type == "camera.finished").ToArray();
        Assert.That(finished, Has.Length.EqualTo(1));
        Assert.That(finished[0].Detail, Is.EqualTo("camera track=flyby kind=camera-track reason=complete"));

        _controller.TryEvaluateNow(out _, out _);
        Assert.That(Events().Any(e => e.Type == "camera.finished"), Is.False, "once, not every frame");
    }

    [Test]
    public void Stopping_EmitsCameraFinishedWithTheStoppedReason()
    {
        Upload("flyby", TwoShots);
        Play("flyby");
        Events();

        Stop();
        var finished = Events().Single(e => e.Type == "camera.finished");
        Assert.That(finished.Detail, Is.EqualTo("camera track=flyby kind=camera-track reason=stopped"));
    }

    [Test]
    public void ALoopWrap_ReArmsTheShotEvent()
    {
        Upload("flyby", TwoShots);
        Play("flyby loop 1");
        _controller.TryEvaluateNow(out _, out _);
        Events();

        Advance(_schedules, 8000);
        _controller.TryEvaluateNow(out _, out _);
        Assert.That(Events().Any(e => e.Type == "camera.shot" && e.Detail.Contains("shot=0")), Is.True);
    }

    // ---- shared-clock groups ------------------------------------------------------------------------------------

    /// <summary>
    ///     The whole reason the camera player joins the schedule registry rather than owning a private
    ///     clock: a dolly move and its cue schedule are <i>one take</i>, so a scrub on either moves both
    ///     — not two clocks that merely agree until the first hitch.
    /// </summary>
    [Test]
    public void ATrackAndASchedule_InOneGroup_StayAlignedAcrossAScrub()
    {
        Upload("flyby", TwoShots);
        Play("flyby group take-3");

        var id = _schedules.ReserveId("cues");
        _schedules.Submit(new Schedule(id, "take-3", ClockBase.Render, 1, false,
        [
            new ScheduleEntry(6000, "ctl/pause", new SimCommand("", "game.pause", SimCommand.NoOrdinal, 1), false),
        ]));
        _schedules.Activate();

        var camera = _controller.Current!;
        var cues = _schedules.Find("cues")!;

        Assert.Multiple(() =>
        {
            Assert.That(camera.Clock, Is.SameAs(cues.Clock), "one group is one clock instance, not two that agree");
            Assert.That(camera.OwnsClock, Is.False);
            Assert.That(cues.OwnsClock, Is.False);
        });

        Advance(_schedules, 1000);
        Assert.That(camera.Clock.PositionMs, Is.EqualTo(1000.0), "the group clock advances exactly once per tick");

        // A scrub through the SCHEDULE's transport must move the camera track with it.
        Assert.That(_schedules.Execute(
                new SimCommand("", "schedule.scrub", SimCommand.NoOrdinal, 5000) { Token = "cues" }).Outcome,
            Is.EqualTo(CommandOutcome.Ok));

        Assert.Multiple(() =>
        {
            Assert.That(camera.Clock.PositionMs, Is.EqualTo(5000.0));
            Assert.That(_controller.TryEvaluateNow(out var pose, out _), Is.True);
            Assert.That(pose.Fov, Is.EqualTo(34.5).Within(1e-9), "5 s in is 1 s into the 40→18 shot");
        });
    }

    [Test]
    public void TheScheduleTransport_DrivesACameraTrackPlayerToo()
    {
        Upload("flyby", TwoShots);
        Play("flyby");
        var id = _controller.Current!.Id;

        Assert.Multiple(() =>
        {
            Assert.That(_schedules.Execute(
                new SimCommand("", "schedule.pause", SimCommand.NoOrdinal, 1) { Token = id }).Outcome,
                Is.EqualTo(CommandOutcome.Ok));
            Assert.That(_controller.Current.Clock.Paused, Is.True);

            Assert.That(_schedules.Execute(
                new SimCommand("", "schedule.rate", SimCommand.NoOrdinal, 3) { Token = id }).Outcome,
                Is.EqualTo(CommandOutcome.Ok));
            Assert.That(_controller.Current.Clock.Rate, Is.EqualTo(3.0));

            Assert.That(_schedules.Execute(
                new SimCommand("", "schedule.remove", SimCommand.NoOrdinal, 1) { Token = id }).Outcome,
                Is.EqualTo(CommandOutcome.Ok));
            Assert.That(_schedules.Players, Is.Empty);
        });
    }

    // ---- eviction -------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Cap-pressure eviction reclaims only players that can never fire again. A camera track reports
    ///     <c>done</c> exactly when it is stopped or has run past its end, so a take in progress is
    ///     structurally un-evictable — which matters because a truncated shot would be the most
    ///     confusing failure this feature could produce.
    /// </summary>
    [Test]
    public void APlayingTrack_IsNeverEvictedUnderCapPressure()
    {
        var schedules = new ScheduleStore(new ScheduleLimits(MaxLive: 1));
        var controller = new CameraPlaybackController(_camera, schedules);
        Upload("flyby", TwoShots);
        controller.Execute(CameraCommands.ParsePlay("flyby")!);

        for (var i = 0; i < 5; i++)
        {
            schedules.Activate();
            Assert.That(schedules.Players, Has.Count.EqualTo(1), $"pass {i}");
            Assert.That(schedules.Players[0].State, Is.EqualTo(PlaybackState.Running));
        }

        // Once it really is finished the slot is fair game again — that is the point of the cap.
        schedules.AdvanceAll(9000, 9000, 9000);
        Assert.That(schedules.Players[0].State, Is.EqualTo(PlaybackState.Done));
        schedules.Activate();
        Assert.Multiple(() =>
        {
            Assert.That(schedules.Players, Is.Empty);
            Assert.That(_camera.DrainEvents().Any(e => e.Type == "schedule.evicted"), Is.False,
                "eviction events belong to the schedule store, not the camera store");
        });
    }
}
