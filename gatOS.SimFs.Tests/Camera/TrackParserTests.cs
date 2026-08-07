using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Camera;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     <see cref="TrackParser"/>: the accept/reject matrix, every cap, and — the point of the whole
///     class — that a rejection names the shot, channel and key that caused it. A track is authored in
///     a text editor on the host, so the message on the failed write is the only feedback loop the
///     author has.
/// </summary>
[TestFixture]
public sealed class TrackParserTests
{
    /// <summary>
    ///     plans/CAMERA_CONTROLS_PLAN.md §4.4's worked example, verbatim — comments and all. If this
    ///     ever stops parsing, the plan and the code have diverged.
    /// </summary>
    private const string PlanExample =
        """
        {
          "loop": false,
          "defaults": { "frame": "cce", "anchor": "vessel:apollo11", "ease": "in-out" },
          "shots": [
            {
              "name": "pad-rise",
              "t": 0.0, "duration": 8.0,
              "anchor": "body:earth",
              "blend_in": 0.5,                          // eased cross-fade from the previous pose

              "position": {
                "mode": "cartesian",                    // cartesian | orbit | attach
                "curve": "catmull-rom",                 // step | linear | catmull-rom | bezier
                "frame": "bodyfixed",
                "keys": [
                  { "t": 0.0, "v": [-40, 8, 12], "ease": "out", "ease_power": 3 },
                  { "t": 4.0, "v": [-18, 14,  6] },
                  { "t": 8.0, "v": [ -6, 22,  2] }
                ]
              },

              "aim": {
                "target": "vessel:apollo11",
                "offset": [0, 1.2, 0], "frame": "bodyfixed",
                "up": "world",
                "roll": { "keys": [ {"t":0,"v":0}, {"t":8,"v":-6,"ease":"in-out"} ] }
              },

              "fov":  { "keys": [ {"t":0,"v":42}, {"t":8,"v":24,"ease":"out"} ] },
              "time": { "keys": [ {"t":0,"v":1.0}, {"t":6,"v":0.15,"ease":"in-out"} ] }
            }
          ]
        }
        """;

    private static readonly CameraLimits Limits = new();

    internal static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    internal static Track Parse(string json, CameraLimits? limits = null)
        => TrackParser.Parse(Bytes(json), limits ?? Limits);

    /// <summary>Parses <paramref name="json"/> and returns the EINVAL message it must have produced.</summary>
    internal static string Reject(string json, CameraLimits? limits = null)
    {
        var ex = Assert.Throws<VfsErrorException>(() => Parse(json, limits))!;
        Assert.That(ex.Errno, Is.EqualTo(LinuxErrno.EINVAL));
        return ex.Message;
    }

    /// <summary>A minimal well-formed track around <paramref name="body"/> (one 8 s shot).</summary>
    internal static string Shot(string body) =>
        $$"""
          { "shots": [ { "duration": 8, {{body}} } ] }
          """;

    // ---- the worked example --------------------------------------------------------------------------

    [Test]
    public void ThePlanExample_RoundTripsToTheExpectedObjectGraph()
    {
        var track = Parse(PlanExample);

        Assert.Multiple(() =>
        {
            Assert.That(track.Loop, Is.False);
            Assert.That(track.Defaults.Frame, Is.EqualTo(FrameKind.Cce));
            Assert.That(track.Defaults.Anchor, Is.EqualTo(TargetRef.Vessel("apollo11")));
            Assert.That(track.Defaults.Ease, Is.EqualTo(EaseSpec.Named(EaseKind.InOut)));
            Assert.That(track.Shots, Has.Count.EqualTo(1));
            Assert.That(track.DurationSeconds, Is.EqualTo(8.0));
            Assert.That(track.DurationMs, Is.EqualTo(8000.0));
        });

        var shot = track.Shots[0];
        Assert.Multiple(() =>
        {
            Assert.That(shot.Name, Is.EqualTo("pad-rise"));
            Assert.That(shot.TSeconds, Is.EqualTo(0.0));
            Assert.That(shot.DurationSeconds, Is.EqualTo(8.0));
            Assert.That(shot.BlendInSeconds, Is.EqualTo(0.5));
            // The shot's own anchor wins over defaults.anchor.
            Assert.That(shot.Anchor, Is.EqualTo(TargetRef.Body("earth")));
        });

        var position = shot.Position!;
        Assert.Multiple(() =>
        {
            Assert.That(position.Mode, Is.EqualTo(PositionMode.Cartesian));
            Assert.That(position.Frame, Is.EqualTo(FrameKind.BodyFixed), "the block's frame beats defaults");
            Assert.That(position.Keys!.Curve, Is.EqualTo(CurveKind.CatmullRom));
            Assert.That(position.Keys.Count, Is.EqualTo(3));
            Assert.That(position.Keys[0].Value, Is.EqualTo(new Vec3(-40, 8, 12)));
            Assert.That(position.Keys[1].Value, Is.EqualTo(new Vec3(-18, 14, 6)));
            Assert.That(position.Keys[2].Value, Is.EqualTo(new Vec3(-6, 22, 2)));
            // "ease": "out" + "ease_power": 3 on the departing key.
            Assert.That(position.Keys[0].Ease, Is.EqualTo(EaseSpec.Named(EaseKind.Out, 3, 3)));
            // Neither key names an ease, so both fall back to defaults.ease.
            Assert.That(position.Keys[1].Ease, Is.EqualTo(EaseSpec.Named(EaseKind.InOut)));
            Assert.That(position.Keys[2].Ease, Is.EqualTo(EaseSpec.Named(EaseKind.InOut)));
        });

        var aim = shot.Aim!;
        Assert.Multiple(() =>
        {
            Assert.That(aim.Target, Is.EqualTo(TargetRef.Vessel("apollo11")));
            Assert.That(aim.Offset, Is.EqualTo(new Vec3(0, 1.2, 0)));
            Assert.That(aim.Frame, Is.EqualTo(FrameKind.BodyFixed));
            Assert.That(aim.Up, Is.EqualTo(AimUpKind.World));
            Assert.That(aim.Roll!.Count, Is.EqualTo(2));
            // The arriving key names the ease, so the segment leaving key 0 inherits it.
            Assert.That(aim.Roll[0].Ease, Is.EqualTo(EaseSpec.Named(EaseKind.InOut)));
            Assert.That(aim.Roll[1].Value, Is.EqualTo(-6.0));
        });

        Assert.Multiple(() =>
        {
            Assert.That(shot.Fov!.Count, Is.EqualTo(2));
            Assert.That(shot.Fov[0].Ease, Is.EqualTo(EaseSpec.Named(EaseKind.Out)),
                "the arriving key's 'out' governs the only segment");
            Assert.That(shot.Time!.Count, Is.EqualTo(2));
            Assert.That(shot.Time[1].Value, Is.EqualTo(0.15));
            Assert.That(shot.Rotation, Is.Null);
            Assert.That(shot.Roll, Is.Null, "the roll lives inside the aim block here");
        });

        // The claim mask is exactly what the shot animates — and nothing else (plan §4.3).
        Assert.That(shot.Channels, Is.EqualTo(
            CameraChannelMask.Position | CameraChannelMask.Frame | CameraChannelMask.Anchor
            | CameraChannelMask.AimTarget | CameraChannelMask.AimOffset | CameraChannelMask.AimFrame
            | CameraChannelMask.AimUp | CameraChannelMask.Roll
            | CameraChannelMask.Fov | CameraChannelMask.TimeScale));
    }

    // ---- structure ------------------------------------------------------------------------------------

    [Test]
    public void AnEmptyUpload_IsRejected()
        => Assert.That(
            Assert.Throws<VfsErrorException>(() => TrackParser.Parse([], Limits))!.Message,
            Does.Contain("empty"));

    [Test]
    public void NotJson_IsRejectedWithTheReaderDiagnosis()
        => Assert.That(Reject("{ this is not json"), Does.Contain("not valid JSON"));

    [Test]
    public void ATopLevelArray_IsRejected()
        => Assert.That(Reject("[]"), Does.Contain("top level must be a JSON object"));

    [Test]
    public void NoShots_IsRejected()
        => Assert.That(Reject("""{ "loop": true }"""), Does.Contain("no 'shots'"));

    [Test]
    public void AnEmptyShotList_IsRejected()
        => Assert.That(Reject("""{ "shots": [] }"""), Does.Contain("is empty"));

    [Test]
    public void AnUnknownRootKey_IsRejectedRatherThanIgnored()
        => Assert.That(Reject("""{ "shots": [], "lop": true }"""), Does.Contain("lop is not a known key"));

    [Test]
    public void AnUnknownShotKey_NamesTheShot()
        => Assert.That(Reject("""{ "shots": [ { "durration": 3 } ] }"""),
            Does.Contain("shots[0].durration is not a known key"));

    [Test]
    public void AnUnknownChannelName_IsRejected()
        => Assert.That(Reject(Shot(""" "zoom": { "keys": [ {"t":0,"v":1} ] } """)),
            Does.Contain("shots[0].zoom is not a known key"));

    [Test]
    public void AShotWithNoChannels_IsRejected()
        => Assert.That(Reject("""{ "shots": [ { "duration": 4 } ] }"""),
            Does.Contain("declares no channels"));

    [Test]
    public void AShotWithNoDuration_IsRejected()
        => Assert.That(Reject("""{ "shots": [ { "fov": { "keys": [{"t":0,"v":30}] } } ] }"""),
            Does.Contain("shots[0] has no 'duration'"));

    [TestCase("0", "must last a positive time")]
    [TestCase("-2", "must last a positive time")]
    public void ANonPositiveDuration_IsRejected(string duration, string expected)
        => Assert.That(Reject($$"""{ "shots": [ { "duration": {{duration}}, "fov": [{"t":0,"v":30}] } ] }"""),
            Does.Contain(expected));

    [Test]
    public void ANegativeShotStart_IsRejected()
        => Assert.That(Reject("""{ "shots": [ { "t": -1, "duration": 4, "fov": [{"t":0,"v":30}] } ] }"""),
            Does.Contain("shots[0].t is -1"));

    [Test]
    public void ANegativeBlendIn_IsRejected()
        => Assert.That(Reject(Shot(""" "blend_in": -0.5, "fov": [{"t":0,"v":30}] """)),
            Does.Contain("shots[0].blend_in is -0.5"));

    [Test]
    public void OverlappingShots_AreRejectedRatherThanResolved()
    {
        const string json =
            """
            { "shots": [
              { "t": 0, "duration": 5, "fov": [{"t":0,"v":30}] },
              { "t": 4, "duration": 5, "fov": [{"t":0,"v":40}] }
            ] }
            """;
        Assert.That(Reject(json), Does.Contain("shots[1] starts at t=4").And.Contain("must not overlap"));
    }

    [Test]
    public void OutOfOrderShots_AreRejected()
    {
        const string json =
            """
            { "shots": [
              { "t": 10, "duration": 5, "fov": [{"t":0,"v":30}] },
              { "t": 0,  "duration": 5, "fov": [{"t":0,"v":40}] }
            ] }
            """;
        Assert.That(Reject(json), Does.Contain("must be listed in time order"));
    }

    [Test]
    public void BackToBackAndGappedShots_AreAccepted()
    {
        var track = Parse(
            """
            { "shots": [
              { "t": 0,  "duration": 5, "fov": [{"t":0,"v":30}] },
              { "t": 5,  "duration": 5, "fov": [{"t":0,"v":40}] },
              { "t": 20, "duration": 5, "fov": [{"t":0,"v":50}] }
            ] }
            """);
        Assert.Multiple(() =>
        {
            Assert.That(track.Shots, Has.Count.EqualTo(3));
            Assert.That(track.DurationSeconds, Is.EqualTo(25.0), "a trailing gap still ends at the last shot");
            Assert.That(track.Shots[0].Name, Is.EqualTo("shot-0"), "an unnamed shot gets its index");
        });
    }

    // ---- keys -----------------------------------------------------------------------------------------

    [Test]
    public void AnEmptyKeyList_NamesTheChannel()
        => Assert.That(Reject(Shot(""" "fov": { "keys": [] } """)),
            Does.Contain("shots[0].fov.keys is empty"));

    [Test]
    public void UnsortedKeyTimes_NameTheOffendingKey()
        => Assert.That(Reject(Shot(""" "fov": [ {"t":0,"v":30}, {"t":4,"v":40}, {"t":2,"v":50} ] """)),
            Does.Contain("shots[0].fov[2] is at t=2, not after [1] at t=4"));

    [Test]
    public void DuplicateKeyTimes_AreRejected()
        => Assert.That(Reject(Shot(""" "fov": [ {"t":2,"v":30}, {"t":2,"v":40} ] """)),
            Does.Contain("shots[0].fov[1] is at t=2, not after [0] at t=2"));

    [Test]
    public void AKeyOutsideTheShotWindow_IsRejected()
        => Assert.That(Reject(Shot(""" "fov": [ {"t":0,"v":30}, {"t":9,"v":40} ] """)),
            Does.Contain("shots[0].fov[1] is at t=9, outside the shot's [0, 8] window"));

    [Test]
    public void ANonFiniteNumber_IsRejected()
        => Assert.That(Reject(Shot(""" "fov": [ {"t":0,"v":1e400} ] """)),
            Does.Contain("must be a number").Or.Contain("finite"));

    [Test]
    public void AKeyWithoutAValue_IsRejected()
        => Assert.That(Reject(Shot(""" "fov": [ {"t":0} ] """)), Does.Contain("shots[0].fov[0] has no 'v'"));

    [Test]
    public void AnOutOfRangeFov_NamesTheBoundsAndTheKey()
        => Assert.That(Reject(Shot(""" "fov": [ {"t":0,"v":400} ] """)),
            Does.Contain("shots[0].fov[0].v is 400").And.Contain("[1, 179]"));

    [Test]
    public void AnOutOfRangeOrbitElevation_IsRejected()
        => Assert.That(Reject(Shot(""" "position": { "mode":"orbit", "elevation": [ {"t":0,"v":120} ] } """)),
            Does.Contain("elevation[0].v is 120").And.Contain("[-90, 90]"));

    [Test]
    public void ANegativeTimeScale_IsRejected()
        => Assert.That(Reject(Shot(""" "time": [ {"t":0,"v":-1} ] """)), Does.Contain("shots[0].time[0].v is -1"));

    // ---- eases ----------------------------------------------------------------------------------------

    [Test]
    public void AnUnknownEaseToken_IsRejected()
        => Assert.That(Reject(Shot(""" "fov": [ {"t":0,"v":30,"ease":"bouncy"} ] """)),
            Does.Contain("'bouncy' is not one of linear|in|out|in-out"));

    [Test]
    public void ExplicitBezierEaseHandles_AreAccepted()
    {
        var track = Parse(Shot(""" "fov": [ {"t":0,"v":30,"ease":[0.25,-0.5,0.75,1.5]}, {"t":8,"v":60} ] """));
        var ease = track.Shots[0].Fov![0].Ease;
        Assert.Multiple(() =>
        {
            Assert.That(ease.Kind, Is.EqualTo(EaseKind.Bezier));
            Assert.That(ease.Y1, Is.EqualTo(-0.5), "the y handles stay unclamped — that is anticipation");
            Assert.That(ease.Y2, Is.EqualTo(1.5));
        });
    }

    [Test]
    public void AWrongLengthEaseArray_IsRejected()
        => Assert.That(Reject(Shot(""" "fov": [ {"t":0,"v":30,"ease":[0.25,0.1]} ] """)),
            Does.Contain("four bezier handle numbers"));

    [Test]
    public void EasePowerWithoutAnEase_IsRejected()
        => Assert.That(Reject(Shot(""" "fov": [ {"t":0,"v":30,"ease_power":2} ] """)),
            Does.Contain("has no 'ease' to apply to"));

    [Test]
    public void AnOutOfRangeEasePower_IsRejected()
        => Assert.That(Reject(Shot(""" "fov": [ {"t":0,"v":30,"ease":"in","ease_power":99} ] """)),
            Does.Contain("ease_power is 99"));

    [Test]
    public void TheTrackDefaultEase_FillsInAKeyThatNamesNone()
    {
        var track = Parse(
            """
            { "defaults": { "ease": "in" },
              "shots": [ { "duration": 4, "fov": [ {"t":0,"v":30}, {"t":4,"v":60} ] } ] }
            """);
        Assert.That(track.Shots[0].Fov![0].Ease, Is.EqualTo(EaseSpec.Named(EaseKind.In)));
    }

    // ---- position modes --------------------------------------------------------------------------------

    [Test]
    public void OrbitModeCarryingCartesianKeys_IsRejected()
        => Assert.That(Reject(Shot(""" "position": { "mode":"orbit", "keys": [ {"t":0,"v":[1,2,3]} ] } """)),
            Does.Contain("is 'orbit' but carries cartesian 'keys'"));

    [Test]
    public void CartesianModeCarryingOrbitChannels_IsRejected()
        => Assert.That(Reject(Shot(""" "position": { "mode":"cartesian", "radius": [ {"t":0,"v":5} ] } """)),
            Does.Contain("is 'cartesian' but carries orbit channels"));

    [Test]
    public void AttachModeWithoutAnOffset_IsRejected()
        => Assert.That(Reject(Shot(""" "position": { "mode":"attach" } """)),
            Does.Contain("is 'attach' but has no 'offset'"));

    [Test]
    public void OrbitModeWithNoChannels_IsRejected()
        => Assert.That(Reject(Shot(""" "position": { "mode":"orbit" } """)),
            Does.Contain("declares none of radius/azimuth/elevation"));

    [Test]
    public void TheModeIsInferredFromWhatWasAuthored()
    {
        var orbit = Parse(Shot(""" "position": { "azimuth": [ {"t":0,"v":0}, {"t":8,"v":360} ] } """));
        var attach = Parse(Shot(""" "position": { "offset": [0,3,-12], "frame": "chase" } """));
        var cartesian = Parse(Shot(""" "position": { "keys": [ {"t":0,"v":[1,2,3]} ] } """));

        Assert.Multiple(() =>
        {
            Assert.That(orbit.Shots[0].Position!.Mode, Is.EqualTo(PositionMode.Orbit));
            Assert.That(orbit.Shots[0].Channels, Is.EqualTo(
                CameraChannelMask.Frame | CameraChannelMask.OrbitAzimuth),
                "only the orbit channels that were authored are claimed");
            Assert.That(attach.Shots[0].Position!.Mode, Is.EqualTo(PositionMode.Attach));
            Assert.That(attach.Shots[0].Position!.Offset, Is.EqualTo(new Vec3(0, 3, -12)));
            Assert.That(attach.Shots[0].Position!.Frame, Is.EqualTo(FrameKind.Chase));
            Assert.That(cartesian.Shots[0].Position!.Mode, Is.EqualTo(PositionMode.Cartesian));
        });
    }

    [Test]
    public void APositionBlockWithNoAnchor_DoesNotClaimTheAnchorChannel()
    {
        var track = Parse(Shot(""" "position": { "keys": [ {"t":0,"v":[1,2,3]} ] } """));
        Assert.That(track.Shots[0].Channels, Is.EqualTo(CameraChannelMask.Position | CameraChannelMask.Frame));
    }

    [Test]
    public void AFovOnlyShot_ClaimsNeitherAnchorNorFrame()
    {
        var track = Parse(
            """
            { "defaults": { "anchor": "body:earth", "frame": "enu" },
              "shots": [ { "duration": 4, "fov": [ {"t":0,"v":30} ] } ] }
            """);
        Assert.That(track.Shots[0].Channels, Is.EqualTo(CameraChannelMask.Fov),
            "a shot must never seize a channel it does not animate — plan §4.3");
    }

    // ---- curves and handles -----------------------------------------------------------------------------

    [Test]
    public void AnUnknownCurve_IsRejected()
        => Assert.That(Reject(Shot(""" "fov": { "curve": "smooth", "keys": [ {"t":0,"v":30} ] } """)),
            Does.Contain("must be one of step|linear|catmull-rom|bezier"));

    [Test]
    public void ABezierCurveWithoutHandles_IsRejected()
        => Assert.That(Reject(Shot(
                """ "position": { "curve":"bezier", "keys": [ {"t":0,"v":[0,0,0]}, {"t":8,"v":[1,0,0]} ] } """)),
            Does.Contain("keys[0] has no 'handle_out'"));

    [Test]
    public void ABezierCurveMissingTheArrivingHandle_NamesThatKey()
        => Assert.That(Reject(Shot(
                """
                "position": { "curve":"bezier", "keys": [
                  {"t":0,"v":[0,0,0],"handle_out":[1,0,0]}, {"t":8,"v":[1,0,0]} ] }
                """)),
            Does.Contain("keys[1] has no 'handle_in'"));

    [Test]
    public void AHandleOnANonBezierCurve_IsRejected()
        => Assert.That(Reject(Shot(""" "fov": { "curve":"linear", "keys": [ {"t":0,"v":30,"handle_out":40} ] } """)),
            Does.Contain("carries a bezier handle but the curve is 'linear'"));

    [Test]
    public void ABezierOnARotationChannel_IsRejected()
        => Assert.That(Reject(Shot(""" "rotation": { "curve":"bezier", "keys": [ {"t":0,"v":[0,0,0,1]} ] } """)),
            Does.Contain("cannot be 'bezier' here"));

    [Test]
    public void ARotationChannelDefaultsToSquad()
    {
        var track = Parse(Shot(""" "rotation": [ {"t":0,"v":[0,0,0,1]} ] """));
        Assert.That(track.Shots[0].Rotation!.Curve, Is.EqualTo(CurveKind.CatmullRom));
    }

    [Test]
    public void AZeroQuaternionKey_IsRejected()
        => Assert.That(Reject(Shot(""" "rotation": [ {"t":0,"v":[0,0,0,0]} ] """)),
            Does.Contain("not a usable rotation"));

    // ---- targets and enums --------------------------------------------------------------------------------

    [Test]
    public void AMalformedTargetRef_IsRejected()
        => Assert.That(Reject(Shot(""" "aim": { "target": "ship apollo11" } """)),
            Does.Contain("shots[0].aim.target must be"));

    [Test]
    public void AnAimAtNothing_IsRejected()
        => Assert.That(Reject(Shot(""" "aim": { "target": "none" } """)), Does.Contain("is 'none'"));

    [Test]
    public void AnUnknownFrameToken_IsRejected()
        => Assert.That(Reject(Shot(""" "position": { "frame": "galactic", "keys": [{"t":0,"v":[0,0,0]}] } """)),
            Does.Contain("ecl|cce|bodyfixed|enu|lvlh|chase"));

    [Test]
    public void AnUnknownUpToken_IsRejected()
        => Assert.That(Reject(Shot(""" "aim": { "target": "body:earth", "up": "sideways" } """)),
            Does.Contain("world|target|velocity|free"));

    [Test]
    public void AnAimBlockDefaultsToBodyFixedAndWorldUp()
    {
        var aim = Parse(Shot(""" "aim": { "target": "vessel:kitten-01" } """)).Shots[0].Aim!;
        Assert.Multiple(() =>
        {
            Assert.That(aim.Frame, Is.EqualTo(FrameKind.BodyFixed));
            Assert.That(aim.Up, Is.EqualTo(AimUpKind.World));
            Assert.That(aim.Offset, Is.EqualTo(Vec3.Zero));
        });
    }

    // ---- aim vs rotation --------------------------------------------------------------------------------

    [Test]
    public void AimWinsOverRotationRatherThanFailing()
    {
        var shot = Parse(Shot(
            """
            "aim": { "target": "body:earth" },
            "rotation": [ {"t":0,"v":[0,0,0,1]} ]
            """)).Shots[0];

        Assert.Multiple(() =>
        {
            Assert.That(shot.Aim, Is.Not.Null);
            Assert.That(shot.Rotation, Is.Null, "plan §4.4: aim wins");
            Assert.That(shot.Channels.Has(CameraChannel.Rotation), Is.False);
        });
    }

    [Test]
    public void RollDeclaredTwice_IsRejected()
        => Assert.That(Reject(Shot(
                """
                "aim": { "target": "body:earth", "roll": [ {"t":0,"v":0} ] },
                "roll": [ {"t":0,"v":0} ]
                """)),
            Does.Contain("declared both at the shot level and inside 'aim'"));

    // ---- caps -------------------------------------------------------------------------------------------

    [Test]
    public void TheKeyCap_IsEnforcedPerChannelAndNamesIt()
    {
        var keys = string.Join(',', Enumerable.Range(0, 9).Select(i => $"{{\"t\":{i},\"v\":30}}"));
        var json = $$"""{ "shots": [ { "duration": 20, "fov": [{{keys}}] } ] }""";
        Assert.That(Reject(json, new CameraLimits(MaxKeys: 8)),
            Does.Contain("shots[0].fov has 9 keys, past the 8-key cap"));
    }

    [Test]
    public void TheTrackByteCap_IsEnforced()
        => Assert.That(Reject(PlanExample, new CameraLimits(MaxTrackBytes: 64)),
            Does.Contain("per-track cap"));

    [Test]
    public void TheShotCap_IsEnforced()
    {
        var shots = string.Join(',', Enumerable.Range(0, TrackParser.MaxShots + 1)
            .Select(i => $"{{\"t\":{i},\"duration\":1,\"fov\":[{{\"t\":0,\"v\":30}}]}}"));
        Assert.That(Reject($$"""{ "shots": [{{shots}}] }"""),
            Does.Contain($"shots has {TrackParser.MaxShots + 1} shots"));
    }

    // ---- TryParse ----------------------------------------------------------------------------------------

    [Test]
    public void TryParse_ReportsTheSameDiagnosisWithoutThrowing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TrackParser.TryParse(Bytes(PlanExample), Limits, out var good, out var noError), Is.True);
            Assert.That(good, Is.Not.Null);
            Assert.That(noError, Is.Null);

            Assert.That(TrackParser.TryParse(Bytes("{}"), Limits, out var bad, out var error), Is.False);
            Assert.That(bad, Is.Null);
            Assert.That(error, Does.Contain("no 'shots'"));
        });
    }
}
