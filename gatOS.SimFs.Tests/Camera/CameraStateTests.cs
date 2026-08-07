using gatOS.SimFs.Camera;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     The §4.3 compositing model: <c>Track ?? Override ?? Baseline</c>, per channel, allocation-free.
///     This is the rule that lets a <c>timed_batch</c> pull focus while a track interpolates position,
///     so it is asserted channel by channel rather than in the aggregate.
/// </summary>
[TestFixture]
public sealed class CameraStateTests
{
    private static CameraPose Baseline => CameraPose.Default with
    {
        Position = new Vec3(1, 2, 3),
        Frame = FrameKind.Ecl,
        Anchor = TargetRef.Body("earth"),
        Fov = 60,
        Roll = 0,
        Smoothing = 0,
    };

    private static CameraState Owned()
    {
        var state = new CameraState();
        state.SetBaseline(Baseline);
        return state;
    }

    // ---- baseline -------------------------------------------------------------------------------

    [Test]
    public void NoOverridesNoTrack_ComposesTheBaselineExactly()
    {
        var state = Owned();
        Assert.That(state.Compose(null, CameraChannelMask.All), Is.EqualTo(Baseline),
            "an unclaimed channel falls all the way through — taking the camera is visually a no-op");
    }

    [Test]
    public void FreshState_FallsBackToTheNeutralPose()
        => Assert.That(new CameraState().Compose(null, CameraChannelMask.None),
            Is.EqualTo(CameraPose.Default));

    // ---- overrides ------------------------------------------------------------------------------

    [Test]
    public void OverrideBeatsBaseline_OnlyOnItsOwnChannel()
    {
        var state = Owned();
        state.SetOverride(CameraChannel.Fov, 24.0);

        var composed = state.Compose(null, CameraChannelMask.None);
        Assert.Multiple(() =>
        {
            Assert.That(composed.Fov, Is.EqualTo(24.0));
            Assert.That(composed.Position, Is.EqualTo(Baseline.Position), "untouched channels are untouched");
            Assert.That(composed.Anchor, Is.EqualTo(Baseline.Anchor));
            Assert.That(state.HasOverride(CameraChannel.Fov), Is.True);
            Assert.That(state.HasOverride(CameraChannel.Roll), Is.False);
        });
    }

    [Test]
    public void EveryPayloadShape_HasATypedSetter()
    {
        var state = Owned();
        state.SetOverride(CameraChannel.Position, new Vec3(10, 20, 30));
        state.SetOverride(CameraChannel.Frame, FrameKind.BodyFixed);
        state.SetOverride(CameraChannel.Anchor, TargetRef.Vessel("apollo11"));
        state.SetOverride(CameraChannel.Rotation, new Quat(0, 0, 1, 0));
        state.SetOverride(CameraChannel.AimTarget, TargetRef.Part("apollo11", "7"));
        state.SetOverride(CameraChannel.AimOffset, new Vec3(0, 0.9, 0));
        state.SetOverride(CameraChannel.AimFrame, FrameKind.Lvlh);
        state.SetOverride(CameraChannel.AimUp, AimUpKind.Velocity);
        state.SetOverride(CameraChannel.Roll, -6.0);
        state.SetOverride(CameraChannel.Fov, 28.0);
        state.SetOverride(CameraChannel.Ortho, true);
        state.SetOverride(CameraChannel.OrthoHeight, 500.0);
        state.SetOverride(CameraChannel.Smoothing, 0.25);
        state.SetOverride(CameraChannel.OrbitRadius, 120.0);
        state.SetOverride(CameraChannel.OrbitAzimuth, 45.0);
        state.SetOverride(CameraChannel.OrbitElevation, -10.0);
        state.SetOverride(CameraChannel.TimeScale, 0.15);

        var composed = state.Compose(null, CameraChannelMask.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Overrides, Is.EqualTo(CameraChannelMask.All), "every channel is claimable");
            Assert.That(composed.Position, Is.EqualTo(new Vec3(10, 20, 30)));
            Assert.That(composed.Frame, Is.EqualTo(FrameKind.BodyFixed));
            Assert.That(composed.Anchor, Is.EqualTo(TargetRef.Vessel("apollo11")));
            Assert.That(composed.Rotation, Is.EqualTo(new Quat(0, 0, 1, 0)));
            Assert.That(composed.AimTarget, Is.EqualTo(TargetRef.Part("apollo11", "7")));
            Assert.That(composed.AimOffset, Is.EqualTo(new Vec3(0, 0.9, 0)));
            Assert.That(composed.AimFrame, Is.EqualTo(FrameKind.Lvlh));
            Assert.That(composed.AimUp, Is.EqualTo(AimUpKind.Velocity));
            Assert.That(composed.Roll, Is.EqualTo(-6.0));
            Assert.That(composed.Fov, Is.EqualTo(28.0));
            Assert.That(composed.Ortho, Is.True);
            Assert.That(composed.OrthoHeight, Is.EqualTo(500.0));
            Assert.That(composed.Smoothing, Is.EqualTo(0.25));
            Assert.That(composed.OrbitRadius, Is.EqualTo(120.0));
            Assert.That(composed.OrbitAzimuth, Is.EqualTo(45.0));
            Assert.That(composed.OrbitElevation, Is.EqualTo(-10.0));
            Assert.That(composed.TimeScale, Is.EqualTo(0.15));
        });
    }

    [Test]
    public void PayloadShapeMismatch_Throws()
    {
        var state = Owned();
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => state.SetOverride(CameraChannel.Position, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.SetOverride(CameraChannel.Fov, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.SetOverride(CameraChannel.Roll, Vec3.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.SetOverride(CameraChannel.Fov, Quat.Identity));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.SetOverride(CameraChannel.Roll, FrameKind.Ecl));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.SetOverride(CameraChannel.Fov, AimUpKind.World));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.SetOverride(CameraChannel.Fov, TargetRef.None));
        });
    }

    // ---- geo vs Cartesian: one channel, two spellings --------------------------------------------

    [Test]
    public void Geo_ClaimsThePositionChannel_AndNormalizesLongitude()
    {
        var state = Owned();
        state.SetGeoOverride(28.573, 279.351, 45, TargetRef.Body("earth"));

        var composed = state.Compose(null, CameraChannelMask.None);
        Assert.Multiple(() =>
        {
            Assert.That(composed.PositionIsGeo, Is.True);
            Assert.That(composed.Latitude, Is.EqualTo(28.573).Within(1e-9));
            Assert.That(composed.Longitude, Is.EqualTo(-80.649).Within(1e-9), "folded into [-180, 180)");
            Assert.That(composed.Altitude, Is.EqualTo(45));
            Assert.That(composed.Anchor, Is.EqualTo(TargetRef.Body("earth")), "the body tail claims the anchor");
            Assert.That(state.HasOverride(CameraChannel.Position), Is.True);
        });
    }

    [Test]
    public void Geo_WithoutABody_LeavesTheAnchorAlone()
    {
        var state = Owned();
        state.SetGeoOverride(10, 20, 30, TargetRef.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.HasOverride(CameraChannel.Anchor), Is.False);
            Assert.That(state.Compose(null, CameraChannelMask.None).Anchor, Is.EqualTo(Baseline.Anchor));
        });
    }

    [Test]
    public void CartesianPosition_ClearsTheGeoSpelling()
    {
        var state = Owned();
        state.SetGeoOverride(10, 20, 30, TargetRef.None);
        state.SetOverride(CameraChannel.Position, new Vec3(4, 5, 6));

        var composed = state.Compose(null, CameraChannelMask.None);
        Assert.Multiple(() =>
        {
            Assert.That(composed.PositionIsGeo, Is.False, "they are two spellings of one channel");
            Assert.That(composed.Position, Is.EqualTo(new Vec3(4, 5, 6)));
        });
    }

    // ---- track layer ------------------------------------------------------------------------------

    [Test]
    public void TrackBeatsOverride_WhichBeatsBaseline()
    {
        var state = Owned();
        state.SetOverride(CameraChannel.Fov, 24.0);
        var sample = CameraPose.Default with { Fov = 12.0 };

        Assert.Multiple(() =>
        {
            Assert.That(state.Compose(sample, CameraChannelMask.Fov).Fov, Is.EqualTo(12.0),
                "the shot declares fov, so it wins");
            Assert.That(state.Compose(sample, CameraChannelMask.None).Fov, Is.EqualTo(24.0),
                "an undeclared channel falls through to the live override");
            state.ClearOverrides();
            Assert.That(state.Compose(sample, CameraChannelMask.None).Fov, Is.EqualTo(Baseline.Fov),
                "…and then to the baseline");
        });
    }

    [Test]
    public void TrackAndOverride_OnDisjointChannels_BothApply()
    {
        // The composable case that §4.1's leaf granularity exists for: a track interpolates position
        // while a timed_batch pulls focus.
        var state = Owned();
        state.SetOverride(CameraChannel.Fov, 28.0);
        var sample = CameraPose.Default with { Position = new Vec3(-40, 8, 12), Fov = 90 };

        var composed = state.Compose(sample, CameraChannelMask.Position);
        Assert.Multiple(() =>
        {
            Assert.That(composed.Position, Is.EqualTo(new Vec3(-40, 8, 12)), "from the track");
            Assert.That(composed.Fov, Is.EqualTo(28.0), "from the live override, not the sample");
            Assert.That(composed.Anchor, Is.EqualTo(Baseline.Anchor), "from the baseline");
        });
    }

    [Test]
    public void WritingAChannelAShotIsDriving_IsAcceptedButSuperseded()
    {
        var state = Owned();
        var sample = CameraPose.Default with { Roll = -6 };

        state.SetOverride(CameraChannel.Roll, 45.0);
        Assert.Multiple(() =>
        {
            Assert.That(state.HasOverride(CameraChannel.Roll), Is.True, "the write is recorded, never refused");
            Assert.That(state.Compose(sample, CameraChannelMask.Roll).Roll, Is.EqualTo(-6.0),
                "…and superseded on the next frame while the shot drives it");
            Assert.That(state.Compose(null, CameraChannelMask.All).Roll, Is.EqualTo(45.0),
                "…and reappears the moment the shot stops declaring it");
        });
    }

    [Test]
    public void ANullSample_IgnoresTheClaimMaskEntirely()
    {
        var state = Owned();
        state.SetOverride(CameraChannel.Fov, 24.0);
        Assert.That(state.Compose(null, CameraChannelMask.All).Fov, Is.EqualTo(24.0));
    }

    // ---- lifecycle -----------------------------------------------------------------------------------

    [Test]
    public void ClearOverrides_DropsWrites_ButLeavesTheTrackAndBaseline()
    {
        var state = Owned();
        state.SetOverride(CameraChannel.Fov, 24.0);
        state.SetOverride(CameraChannel.Roll, 45.0);
        state.ClearOverrides();

        var sample = CameraPose.Default with { Roll = -6 };
        var composed = state.Compose(sample, CameraChannelMask.Roll);
        Assert.Multiple(() =>
        {
            Assert.That(state.Overrides, Is.EqualTo(CameraChannelMask.None));
            Assert.That(composed.Fov, Is.EqualTo(Baseline.Fov));
            Assert.That(composed.Roll, Is.EqualTo(-6.0), "pose/reset is about your writes, not about playback");
            Assert.That(state.Baseline, Is.EqualTo(Baseline));
        });
    }

    [Test]
    public void ClearAll_AlsoDropsTheCapturedBaseline()
    {
        var state = Owned();
        state.SetOverride(CameraChannel.Fov, 24.0);
        state.ClearAll();
        Assert.Multiple(() =>
        {
            Assert.That(state.Overrides, Is.EqualTo(CameraChannelMask.None));
            Assert.That(state.Baseline, Is.EqualTo(CameraPose.Default));
            Assert.That(state.Compose(null, CameraChannelMask.None), Is.EqualTo(CameraPose.Default));
        });
    }

    // ---- masks + allocation ---------------------------------------------------------------------------

    [Test]
    public void ChannelMask_BitsMatchTheEnumOrdinals()
    {
        Assert.Multiple(() =>
        {
            foreach (CameraChannel channel in Enum.GetValues<CameraChannel>())
            {
                var mask = CameraChannels.Mask(channel);
                Assert.That((uint)mask, Is.EqualTo(1u << (int)channel), channel.ToString());
                Assert.That(mask.Has(channel), Is.True);
                Assert.That(CameraChannelMask.All.Has(channel), Is.True);
                Assert.That(CameraChannelMask.None.Has(channel), Is.False);
            }

            Assert.That(Enum.GetValues<CameraChannel>(), Has.Length.EqualTo(CameraChannels.Count));
        });
    }

    [Test]
    public void Compose_AllocatesNothing()
    {
        var state = Owned();
        state.SetOverride(CameraChannel.Fov, 24.0);
        var sample = CameraPose.Default with { Position = new Vec3(1, 1, 1) };

        // Warm up first: the JIT's own first-call allocations are not the compositor's.
        for (var i = 0; i < 100; i++)
            Consume(state.Compose(sample, CameraChannelMask.Position));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            Consume(state.Compose(sample, CameraChannelMask.Position | CameraChannelMask.Roll));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.LessThan(64),
            "the compositor runs every rendered frame; it must never touch the heap");
    }

    private static double _sink;

    private static void Consume(in CameraPose pose) => _sink = pose.Fov;
}
