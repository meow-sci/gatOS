using gatOS.SimFs.Camera;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     <see cref="TrackEvaluator"/>: shot selection, blending, every curve kind, and the two properties
///     the whole feature stands on — <b>determinism</b> (the same <c>t</c> always yields the same
///     sample) and <b>exact closure</b> (a 360° orbit lands bit-identically back where it started).
/// </summary>
[TestFixture]
public sealed class TrackEvaluatorTests
{
    private static Track Parse(string json) => TrackParserTests.Parse(json);

    private static Track Shot(string body) => Parse(TrackParserTests.Shot(body));

    // ---- easing --------------------------------------------------------------------------------------

    [Test]
    public void EasedProgress_IsMonotonicAndLandsExactlyOnTheFinalKey()
    {
        var track = Shot(""" "fov": [ {"t":0,"v":20,"ease":"in-out"}, {"t":8,"v":100} ] """);

        var previous = double.NegativeInfinity;
        for (var i = 0; i <= 400; i++)
        {
            var fov = TrackEvaluator.Sample(track, 8.0 * i / 400.0).Pose.Fov;
            Assert.That(fov, Is.GreaterThanOrEqualTo(previous - 1e-12), $"i={i}");
            previous = fov;
        }

        Assert.Multiple(() =>
        {
            // Exactly, not approximately: this is the endpoint snap that stops a looping shot ratcheting.
            Assert.That(TrackEvaluator.Sample(track, 8.0).Pose.Fov, Is.EqualTo(100.0));
            Assert.That(TrackEvaluator.Sample(track, 0.0).Pose.Fov, Is.EqualTo(20.0));
        });
    }

    [Test]
    public void ABezierEase_IsAllowedToOvershootTheKeyRange()
    {
        // y2 = 1.4 means "fly past the target and settle" — clamping that away would silently delete
        // the only ease shape a power curve cannot express.
        var track = Shot(""" "fov": [ {"t":0,"v":20,"ease":[0.2,0,0.2,1.4]}, {"t":8,"v":40} ] """);

        var peak = 0.0;
        for (var i = 0; i <= 400; i++)
            peak = Math.Max(peak, TrackEvaluator.Sample(track, 8.0 * i / 400.0).Pose.Fov);

        Assert.Multiple(() =>
        {
            Assert.That(peak, Is.GreaterThan(40.0), "the overshoot must survive to the sample");
            Assert.That(TrackEvaluator.Sample(track, 8.0).Pose.Fov, Is.EqualTo(40.0), "and still settle exactly");
        });
    }

    // ---- curves --------------------------------------------------------------------------------------

    [Test]
    public void CatmullRom_PassesExactlyThroughEveryKey()
    {
        var track = Shot(
            """
            "position": { "curve": "catmull-rom", "keys": [
              {"t":0,"v":[0,0,0]}, {"t":1,"v":[1,0,0]}, {"t":2,"v":[1.1,3,-2]}, {"t":8,"v":[100,4,9]} ] }
            """);

        Assert.Multiple(() =>
        {
            Assert.That(TrackEvaluator.Sample(track, 0).Pose.Position, Is.EqualTo(new Vec3(0, 0, 0)));
            Assert.That(TrackEvaluator.Sample(track, 1).Pose.Position, Is.EqualTo(new Vec3(1, 0, 0)));
            Assert.That(TrackEvaluator.Sample(track, 2).Pose.Position, Is.EqualTo(new Vec3(1.1, 3, -2)));
            Assert.That(TrackEvaluator.Sample(track, 8).Pose.Position, Is.EqualTo(new Vec3(100, 4, 9)));
        });
    }

    /// <summary>
    ///     The centripetal guarantee, at the evaluator level: the same lopsided key run
    ///     <c>SplinesTests</c> uses (1 → 1.1 with 100 far beyond) must not fling the camera outside the
    ///     segment it is travelling.
    /// </summary>
    [Test]
    public void CatmullRom_DoesNotOvershootOnUnevenSpacing()
    {
        var track = Shot(
            """
            "position": { "curve": "catmull-rom", "keys": [
              {"t":0,"v":[0,0,0]}, {"t":1,"v":[1,0,0]}, {"t":2,"v":[1.1,0,0]}, {"t":3,"v":[100,0,0]} ] }
            """);

        for (var i = 0; i <= 500; i++)
        {
            var x = TrackEvaluator.Sample(track, 1.0 + (i / 500.0)).Pose.Position.X;
            Assert.That(x, Is.InRange(1.0 - 1e-9, 1.1 + 1e-9), $"i={i}");
        }
    }

    [Test]
    public void Step_HoldsThePreviousKeyUntilTheNext()
    {
        var track = Shot(""" "fov": { "curve":"step", "keys": [ {"t":0,"v":20}, {"t":4,"v":80} ] } """);
        Assert.Multiple(() =>
        {
            Assert.That(TrackEvaluator.Sample(track, 3.999).Pose.Fov, Is.EqualTo(20.0));
            Assert.That(TrackEvaluator.Sample(track, 4.0).Pose.Fov, Is.EqualTo(80.0));
        });
    }

    [Test]
    public void Bezier_UsesItsHandlesAndStillLandsOnItsKeys()
    {
        var track = Shot(
            """
            "position": { "curve":"bezier", "keys": [
              {"t":0,"v":[0,0,0],"handle_out":[0,10,0]},
              {"t":8,"v":[10,0,0],"handle_in":[10,10,0]} ] }
            """);

        var mid = TrackEvaluator.Sample(track, 4.0).Pose.Position;
        Assert.Multiple(() =>
        {
            Assert.That(mid.Y, Is.GreaterThan(1.0), "the handles must bow the path off the straight line");
            Assert.That(TrackEvaluator.Sample(track, 0).Pose.Position, Is.EqualTo(new Vec3(0, 0, 0)));
            Assert.That(TrackEvaluator.Sample(track, 8).Pose.Position, Is.EqualTo(new Vec3(10, 0, 0)));
        });
    }

    // ---- rotation ------------------------------------------------------------------------------------

    private static string RotationKeys(string curve) =>
        $$"""
          "rotation": { "curve": "{{curve}}", "keys": [
            {"t":0,"v":[0,0,0,1]},
            {"t":1,"v":[0,0,0.25881904510252074,0.9659258262890683]},
            {"t":2,"v":[0,0,0.7071067811865476,0.7071067811865476]} ] }
          """;

    /// <summary>The angular speed of the rotation channel around <c>t</c>, degrees per second.</summary>
    private static double AngularSpeed(Track track, double t, double h)
    {
        var a = TrackEvaluator.Sample(track, t).Pose.Rotation;
        var b = TrackEvaluator.Sample(track, t + h).Pose.Rotation;
        var dot = Math.Abs(Quat.Dot(a.Normalized(), b.Normalized()));
        return 2.0 * Math.Acos(Math.Clamp(dot, -1.0, 1.0)) * (180.0 / Math.PI) / Math.Abs(h);
    }

    /// <summary>
    ///     Squad is C¹ across a key: the angular speed either side of the waypoint matches. Slerp is
    ///     only C⁰ and visibly flicks there — asserted below as the control, so this test cannot pass
    ///     for the wrong reason.
    /// </summary>
    [Test]
    public void Squad_IsContinuousAcrossAKeyBoundary()
    {
        var track = Shot(RotationKeys("catmull-rom"));
        const double h = 1e-4;
        var before = AngularSpeed(track, 1.0 - h, h);
        var after = AngularSpeed(track, 1.0, h);
        Assert.That(after, Is.EqualTo(before).Within(1.0),
            $"squad must not jump at a key (before={before}, after={after})");
    }

    [Test]
    public void Slerp_IsTheDiscontinuousControlForTheSquadTest()
    {
        var track = Shot(RotationKeys("linear"));
        const double h = 1e-4;
        var before = AngularSpeed(track, 1.0 - h, h);
        var after = AngularSpeed(track, 1.0, h);
        Assert.That(Math.Abs(after - before), Is.GreaterThan(10.0),
            "the two slerp segments run at 30 and 60 deg/s — if they did not, the squad test proves nothing");
    }

    [Test]
    public void ATwoKeyRotationChannel_Slerps()
    {
        var track = Shot(
            """
            "rotation": [ {"t":0,"v":[0,0,0,1]}, {"t":2,"v":[0,0,0.7071067811865476,0.7071067811865476]} ]
            """);
        // Halfway along a 90° arc is 45°, which slerp gives exactly (squad would need three keys).
        var mid = TrackEvaluator.Sample(track, 1.0).Pose.Rotation;
        Assert.That(2.0 * Math.Acos(mid.W) * (180.0 / Math.PI), Is.EqualTo(45.0).Within(1e-9));
    }

    // ---- orbit ---------------------------------------------------------------------------------------

    /// <summary>
    ///     <b>The regression guard for the incremental-drift bug.</b> A full turn is authored as the key
    ///     pair 0° → 360°, evaluated absolutely from the shot's start — never <c>azimuth += ω·dt</c>.
    ///     At <c>t == duration</c> the eased progress snaps to exactly 1.0, so the azimuth is exactly
    ///     360, and <see cref="CameraPlacement.Spherical"/> folds that back to exactly 0: the resolved
    ///     position is bit-identical to the one at <c>t == 0</c>.
    /// </summary>
    [Test]
    public void AFullOrbit_ClosesBitIdentically()
    {
        var track = Shot(
            """
            "position": { "mode":"orbit",
              "radius":    [ {"t":0,"v":120} ],
              "azimuth":   [ {"t":0,"v":0}, {"t":8,"v":360} ],
              "elevation": [ {"t":0,"v":15} ] }
            """);

        var start = TrackEvaluator.Sample(track, 0.0).Pose;
        var end = TrackEvaluator.Sample(track, 8.0).Pose;

        Assert.Multiple(() =>
        {
            Assert.That(start.OrbitAzimuth, Is.EqualTo(0.0));
            Assert.That(end.OrbitAzimuth, Is.EqualTo(360.0), "the scalar stays monotonic for smoothing");
            Assert.That(CameraPlacement.Spherical(end.OrbitRadius, end.OrbitAzimuth, end.OrbitElevation),
                Is.EqualTo(CameraPlacement.Spherical(start.OrbitRadius, start.OrbitAzimuth, start.OrbitElevation)),
                "the resolved placement must close exactly, or a looping orbit ratchets");
        });
    }

    [Test]
    public void AnEasedFullOrbit_AlsoClosesExactly()
    {
        var track = Shot(
            """
            "position": { "mode":"orbit", "radius": [ {"t":0,"v":50} ],
              "azimuth": [ {"t":0,"v":0,"ease":"in-out"}, {"t":8,"v":720} ] }
            """);
        Assert.That(CameraPlacement.Spherical(50, TrackEvaluator.Sample(track, 8).Pose.OrbitAzimuth, 0),
            Is.EqualTo(CameraPlacement.Spherical(50, 0, 0)));
    }

    [Test]
    public void SphericalPlacement_PutsTheAxesWhereTheContractSaysTheyAre()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CameraPlacement.Spherical(10, 0, 0).X, Is.EqualTo(10.0).Within(1e-12));
            Assert.That(CameraPlacement.Spherical(10, 90, 0).Y, Is.EqualTo(10.0).Within(1e-12));
            Assert.That(CameraPlacement.Spherical(10, 0, 90).Z, Is.EqualTo(10.0).Within(1e-12));
            // Past the pole the elevation clamps rather than turning the placement inside out.
            Assert.That(CameraPlacement.Spherical(10, 0, 200).Z, Is.EqualTo(10.0).Within(1e-12));
            Assert.That(CameraPlacement.Spherical(10, double.NaN, 0), Is.EqualTo(Vec3.Zero));
        });
    }

    [Test]
    public void AttachMode_IsAConstantOffsetInItsFrame()
    {
        var track = Shot(""" "position": { "mode":"attach", "offset":[0,3,-12], "frame":"chase" } """);
        Assert.Multiple(() =>
        {
            Assert.That(TrackEvaluator.Sample(track, 0).Pose.Position, Is.EqualTo(new Vec3(0, 3, -12)));
            Assert.That(TrackEvaluator.Sample(track, 5).Pose.Position, Is.EqualTo(new Vec3(0, 3, -12)));
            Assert.That(TrackEvaluator.Sample(track, 5).Pose.Frame, Is.EqualTo(FrameKind.Chase));
        });
    }

    // ---- shot selection -------------------------------------------------------------------------------

    private const string ThreeShots =
        """
        { "shots": [
          { "name":"a", "t":0,  "duration":4, "fov": [ {"t":0,"v":10}, {"t":4,"v":20} ] },
          { "name":"b", "t":4,  "duration":4, "fov": [ {"t":0,"v":50}, {"t":4,"v":60} ] },
          { "name":"c", "t":12, "duration":4, "fov": [ {"t":0,"v":90} ] }
        ] }
        """;

    [Test]
    public void ShotSelection_PicksTheShotThatOwnsT()
    {
        var track = Parse(ThreeShots);
        Assert.Multiple(() =>
        {
            Assert.That(TrackEvaluator.Sample(track, 0).ShotName, Is.EqualTo("a"));
            Assert.That(TrackEvaluator.Sample(track, 3.9).ShotName, Is.EqualTo("a"));
            Assert.That(TrackEvaluator.Sample(track, 4).ShotName, Is.EqualTo("b"), "back-to-back cuts at the seam");
            Assert.That(TrackEvaluator.Sample(track, 12).ShotName, Is.EqualTo("c"));
            Assert.That(TrackEvaluator.Sample(track, 12).ShotIndex, Is.EqualTo(2));
        });
    }

    [Test]
    public void AGapBetweenShots_HoldsThePreviousShotsFinalPose()
    {
        var track = Parse(ThreeShots);
        Assert.Multiple(() =>
        {
            Assert.That(TrackEvaluator.Sample(track, 9).Pose.Fov, Is.EqualTo(60.0), "held, not released");
            Assert.That(TrackEvaluator.Sample(track, 9).ShotName, Is.EqualTo("b"));
            Assert.That(TrackEvaluator.Sample(track, 11.999).Pose.Fov, Is.EqualTo(60.0));
        });
    }

    [Test]
    public void BeforeTheFirstShotAndPastTheLast_TheEvaluatorHolds()
    {
        var track = Parse(
            """
            { "shots": [ { "t":5, "duration":4, "fov": [ {"t":0,"v":10}, {"t":4,"v":20} ] } ] }
            """);
        Assert.Multiple(() =>
        {
            Assert.That(TrackEvaluator.Sample(track, 0).Pose.Fov, Is.EqualTo(10.0));
            Assert.That(TrackEvaluator.Sample(track, 100).Pose.Fov, Is.EqualTo(20.0));
        });
    }

    // ---- blend_in -------------------------------------------------------------------------------------

    private const string Blended =
        """
        { "shots": [
          { "name":"a", "t":0, "duration":4, "fov": [ {"t":0,"v":100} ] },
          { "name":"b", "t":4, "duration":4, "blend_in": 2,
            "fov": [ {"t":0,"v":20} ], "roll": [ {"t":0,"v":7} ] }
        ] }
        """;

    [Test]
    public void BlendIn_CrossFadesTheChannelsBothShotsDeclare()
    {
        var track = Parse(Blended);
        Assert.Multiple(() =>
        {
            Assert.That(TrackEvaluator.Sample(track, 4.0).Pose.Fov, Is.EqualTo(100.0),
                "at the seam the blend is still entirely the outgoing shot");
            Assert.That(TrackEvaluator.Sample(track, 5.0).Pose.Fov, Is.EqualTo(60.0).Within(1e-9),
                "halfway through a symmetric ease");
            Assert.That(TrackEvaluator.Sample(track, 6.0).Pose.Fov, Is.EqualTo(20.0),
                "the blend must land exactly on the incoming value");
            Assert.That(TrackEvaluator.Sample(track, 7.0).Pose.Fov, Is.EqualTo(20.0));
        });
    }

    [Test]
    public void BlendIn_TakesAChannelOnlyTheNewShotDeclaresAtFullValue()
    {
        var track = Parse(Blended);
        var sample = TrackEvaluator.Sample(track, 5.0);
        Assert.Multiple(() =>
        {
            Assert.That(sample.Pose.Roll, Is.EqualTo(7.0), "nothing to fade from");
            Assert.That(sample.ShotName, Is.EqualTo("b"), "the blend belongs to the incoming shot");
            Assert.That(sample.Channels, Is.EqualTo(CameraChannelMask.Fov | CameraChannelMask.Roll));
        });
    }

    [Test]
    public void AFirstShotWithBlendIn_StartsAtFullValue()
    {
        var track = Parse("""{ "shots": [ { "duration":4, "blend_in":2, "fov": [ {"t":0,"v":33} ] } ] }""");
        Assert.That(TrackEvaluator.Sample(track, 0).Pose.Fov, Is.EqualTo(33.0));
    }

    // ---- compositing ----------------------------------------------------------------------------------

    /// <summary>
    ///     The §4.3 precedence, end to end: a track driving position and a live override on FOV both
    ///     apply, and the track does not touch a single channel it never declared.
    /// </summary>
    [Test]
    public void TheTrackDrivesOnlyItsOwnChannels_AndALiveOverrideSurvivesBesideIt()
    {
        var track = Shot(""" "position": { "keys": [ {"t":0,"v":[1,2,3]}, {"t":8,"v":[9,9,9]} ] } """);
        var sample = TrackEvaluator.Sample(track, 0.0);

        var state = new CameraState();
        state.SetBaseline(CameraPose.Default with
        {
            Fov = 55, Roll = 12, Smoothing = 0.4, AimTarget = TargetRef.Vessel("apollo11"),
        });
        state.SetOverride(CameraChannel.Fov, 24.0);

        var composed = state.Compose(sample.Pose, sample.Channels);

        Assert.Multiple(() =>
        {
            Assert.That(sample.Channels, Is.EqualTo(CameraChannelMask.Position | CameraChannelMask.Frame),
                "the mask must name exactly what the shot animates");
            Assert.That(composed.Position, Is.EqualTo(new Vec3(1, 2, 3)), "track wins on its own channel");
            Assert.That(composed.Fov, Is.EqualTo(24.0), "the live override survives — the track never claimed FOV");
            Assert.That(composed.Roll, Is.EqualTo(12.0), "unclaimed and un-overridden falls through to the baseline");
            Assert.That(composed.Smoothing, Is.EqualTo(0.4));
            Assert.That(composed.AimTarget, Is.EqualTo(TargetRef.Vessel("apollo11")));
        });
    }

    [Test]
    public void AnUndeclaredChannel_IsNotClobberedByThePosesDefault()
    {
        // The evaluated pose carries CameraPose.Default's 60° FOV in an unclaimed field. If the mask
        // were widened to "everything", this is the assertion that would fail.
        var track = Shot(""" "roll": [ {"t":0,"v":3} ] """);
        var sample = TrackEvaluator.Sample(track, 0.0);

        var state = new CameraState();
        state.SetBaseline(CameraPose.Default with { Fov = 12 });

        Assert.Multiple(() =>
        {
            Assert.That(sample.Pose.Fov, Is.EqualTo(60.0), "the sample's unclaimed fields are just defaults");
            Assert.That(state.Compose(sample.Pose, sample.Channels).Fov, Is.EqualTo(12.0));
        });
    }

    // ---- determinism ----------------------------------------------------------------------------------

    [Test]
    public void TheSameT_AlwaysYieldsABitIdenticalSample()
    {
        var track = TrackParserTests.Parse(
            """
            { "defaults": { "ease": "in-out" },
              "shots": [
                { "name":"a", "t":0, "duration":6, "anchor":"body:earth", "blend_in":0,
                  "position": { "curve":"catmull-rom", "keys": [
                    {"t":0,"v":[0,0,0]}, {"t":2,"v":[5,1,2]}, {"t":6,"v":[9,-3,4]} ] },
                  "fov": [ {"t":0,"v":40}, {"t":6,"v":18} ],
                  "rotation": [ {"t":0,"v":[0,0,0,1]}, {"t":3,"v":[0,0,0.383,0.924]},
                                {"t":6,"v":[0,0,0.707,0.707]} ] },
                { "name":"b", "t":6, "duration":4, "blend_in":1.5,
                  "position": { "keys": [ {"t":0,"v":[20,0,0]}, {"t":4,"v":[30,0,0]} ] },
                  "fov": [ {"t":0,"v":60}, {"t":4,"v":24} ] }
              ] }
            """);

        var times = new[] { 0.0, 0.37, 2.0, 5.999, 6.0, 6.75, 7.5, 9.9, 10.0, 42.0 };
        var forward = times.Select(t => TrackEvaluator.Sample(track, t)).ToArray();
        var shuffled = times.Reverse().Select(t => TrackEvaluator.Sample(track, t)).Reverse().ToArray();
        var again = times.Select(t => TrackEvaluator.Sample(track, t)).ToArray();

        for (var i = 0; i < times.Length; i++)
        {
            Assert.That(again[i], Is.EqualTo(forward[i]), $"resampled at t={times[i]}");
            Assert.That(shuffled[i], Is.EqualTo(forward[i]), $"sampled out of order at t={times[i]}");
        }
    }

    [Test]
    public void ANaNTimestamp_DegradesToTheTrackStartRatherThanPoisoningThePose()
    {
        var track = Shot(""" "fov": [ {"t":0,"v":30}, {"t":8,"v":60} ] """);
        Assert.That(TrackEvaluator.Sample(track, double.NaN).Pose.Fov, Is.EqualTo(30.0));
    }

    // ---- allocation ------------------------------------------------------------------------------------

    /// <summary>
    ///     A shot runs at frame rate for minutes at a time, so the steady-state sample path must not
    ///     touch the heap. Budget is generous (the GC counter is approximate) but far below what even
    ///     one allocation per sample would produce.
    /// </summary>
    [Test]
    public void TheSamplePath_AllocatesNothing()
    {
        var track = Shot(
            """
            "position": { "curve":"catmull-rom", "keys": [
              {"t":0,"v":[0,0,0]}, {"t":4,"v":[5,1,2]}, {"t":8,"v":[9,-3,4]} ] },
            "fov": [ {"t":0,"v":40}, {"t":8,"v":18} ]
            """);

        TrackEvaluator.Sample(track, 1.0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sink = 0.0;
        for (var i = 0; i < 10_000; i++)
            sink += TrackEvaluator.Sample(track, 8.0 * i / 10_000.0).Pose.Fov;
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.That(sink, Is.Not.EqualTo(0.0));
        Assert.That(after - before, Is.LessThan(64), "the per-frame sample path must not allocate");
    }
}
