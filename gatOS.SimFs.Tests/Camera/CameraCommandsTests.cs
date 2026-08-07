using gatOS.SimFs.Camera;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     The five composite camera line grammars (<c>aim</c>, <c>geo</c>, <c>position</c>,
///     <c>rotation</c>, <c>play</c>, <c>set</c>) parsed in isolation: the exact
///     <see cref="SimCommand"/> each builds, the defaults, and the null (⇒ EINVAL) boundaries.
/// </summary>
[TestFixture]
public sealed class CameraCommandsTests
{
    // ---- camera.aim ---------------------------------------------------------------------------------

    [Test]
    public void Aim_Full_BuildsTheSlotArray()
    {
        var c = CameraCommands.ParseAim("vessel:kitten-01 off 0 0.9 0 frame bodyfixed up world roll -6")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("camera.aim"));
            Assert.That(c.VesselId, Is.Empty);
            Assert.That(c.Ordinal, Is.EqualTo(SimCommand.NoOrdinal));
            Assert.That(c.Token, Is.EqualTo("vessel:kitten-01"));
            Assert.That(c.Aux, Is.Null);
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame));
            Assert.That(c.Values, Is.EqualTo(new[]
            {
                0d, 0.9, 0, (double)(int)FrameKind.BodyFixed, (double)(int)AimUpKind.World, -6, 1,
            }));
        });
    }

    [Test]
    public void Aim_BareTarget_TakesTheDocumentedDefaults()
    {
        var c = CameraCommands.ParseAim("body:earth")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Token, Is.EqualTo("body:earth"));
            Assert.That(c.Values, Is.EqualTo(new[]
            {
                0d, 0, 0, (double)(int)FrameKind.BodyFixed, (double)(int)AimUpKind.World, 0, 0,
            }), "zero offset, bodyfixed, world up, roll untouched");
        });
    }

    [Test]
    public void Aim_KeywordsAreOrderIndependent()
    {
        var a = CameraCommands.ParseAim("none up free frame lvlh off 1 2 3")!;
        var b = CameraCommands.ParseAim("none off 1 2 3 frame lvlh up free")!;
        Assert.That(a.Values, Is.EqualTo(b.Values));
    }

    [Test]
    public void Aim_RollPresenceIsSignalled()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CameraCommands.ParseAim("none roll 0")!.Values![CameraCommands.AimRollPresent],
                Is.EqualTo(1), "an explicit roll 0 is not the same as no roll at all");
            Assert.That(CameraCommands.ParseAim("none")!.Values![CameraCommands.AimRollPresent],
                Is.EqualTo(0));
        });
    }

    [TestCase("")]
    [TestCase("notatarget")]
    [TestCase("vessel:x off 1 2")]
    [TestCase("vessel:x off 1 2 nan")]
    [TestCase("vessel:x frame")]
    [TestCase("vessel:x frame nope")]
    [TestCase("vessel:x up sideways")]
    [TestCase("vessel:x roll")]
    [TestCase("vessel:x roll nan")]
    [TestCase("vessel:x wobble 3")]
    [TestCase("vessel:x frame ecl frame cce")]
    [TestCase("vessel:x off 1 2 3 off 4 5 6")]
    public void Aim_RejectsMalformedLines(string line)
        => Assert.That(CameraCommands.ParseAim(line), Is.Null);

    // ---- camera.geo ---------------------------------------------------------------------------------

    [Test]
    public void Geo_WithBody_BuildsTheTriple()
    {
        var c = CameraCommands.ParseGeo("28.573 -80.649 45 body:earth")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("camera.geo"));
            Assert.That(c.Token, Is.EqualTo("body:earth"));
            Assert.That(c.Values, Is.EqualTo(new[] { 28.573, -80.649, 45.0 }));
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [Test]
    public void Geo_WithoutBody_LeavesTheTokenEmpty_AndNormalizesLongitude()
    {
        var c = CameraCommands.ParseGeo("12.4 279.351 30")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Token, Is.Empty, "empty ⇒ use the current anchor");
            Assert.That(c.Values![CameraCommands.GeoLon], Is.EqualTo(-80.649).Within(1e-9));
        });
    }

    [TestCase("")]
    [TestCase("1 2")]
    [TestCase("1 2 3 4 5")]
    [TestCase("91 0 0")]
    [TestCase("-91 0 0")]
    [TestCase("0 361 0")]
    [TestCase("0 -181 0")]
    [TestCase("0 0 -1")]
    [TestCase("0 0 nan")]
    [TestCase("0 0 0 vessel:x")]
    [TestCase("0 0 0 earth")]
    public void Geo_RejectsMalformedLines(string line)
        => Assert.That(CameraCommands.ParseGeo(line), Is.Null);

    // ---- camera.position ------------------------------------------------------------------------------

    [Test]
    public void Position_WithFrame_CarriesTheCanonicalToken()
    {
        var c = CameraCommands.ParsePosition("-40 8 12 BodyFixed")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("camera.position"));
            Assert.That(c.Token, Is.EqualTo("bodyfixed"), "canonical casing, whatever was written");
            Assert.That(c.Values, Is.EqualTo(new[] { -40.0, 8, 12 }));
        });
    }

    [Test]
    public void Position_WithoutFrame_LeavesTheTokenEmpty()
        => Assert.That(CameraCommands.ParsePosition("1 2 3")!.Token, Is.Empty);

    [TestCase("1 2")]
    [TestCase("1 2 3 4 5")]
    [TestCase("1 2 inf")]
    [TestCase("1 2 3 eci")]
    [TestCase("")]
    public void Position_RejectsMalformedLines(string line)
        => Assert.That(CameraCommands.ParsePosition(line), Is.Null);

    // ---- camera.rotation --------------------------------------------------------------------------------

    [Test]
    public void Rotation_AcceptsANearUnitQuaternion()
    {
        var c = CameraCommands.ParseRotation("0 0 0 1")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("camera.rotation"));
            Assert.That(c.Values, Is.EqualTo(new[] { 0d, 0, 0, 1 }));
            Assert.That(c.Token, Is.Null);
        });
    }

    [TestCase("0 0 0")]
    [TestCase("0 0 0 1 1")]
    [TestCase("0 0 0 0")]
    [TestCase("0 0 0 5")]
    [TestCase("0 0 0 nan")]
    public void Rotation_RejectsMalformedOrDegenerate(string line)
        => Assert.That(CameraCommands.ParseRotation(line), Is.Null);

    // ---- camera.play ------------------------------------------------------------------------------------

    [Test]
    public void Play_Full_BuildsTheSlotArrayAndGroup()
    {
        var c = CameraCommands.ParsePlay("flyby at 2.5 rate 0.5 loop 1 group take-3")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("camera.play"));
            Assert.That(c.Token, Is.EqualTo("flyby"));
            Assert.That(c.Aux, Is.EqualTo("take-3"));
            Assert.That(c.Values, Is.EqualTo(new[] { 2.5, 0.5, 1, 1, 1, 1 }));
        });
    }

    [Test]
    public void Play_BareTrack_DefaultsRateToOneAndSignalsNothingPresent()
    {
        var c = CameraCommands.ParsePlay("flyby")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Aux, Is.Null);
            Assert.That(c.Values, Is.EqualTo(new[] { 0d, 1, 0, 0, 0, 0 }));
        });
    }

    [TestCase("")]
    [TestCase("has space extra")]
    [TestCase("bad/name")]
    [TestCase("flyby at")]
    [TestCase("flyby at -1")]
    [TestCase("flyby rate 101")]
    [TestCase("flyby rate -1")]
    [TestCase("flyby loop 2")]
    [TestCase("flyby group bad:name")]
    [TestCase("flyby at 1 at 2")]
    [TestCase("flyby wobble 1")]
    public void Play_RejectsMalformedLines(string line)
        => Assert.That(CameraCommands.ParsePlay(line), Is.Null);

    // ---- camera.set -------------------------------------------------------------------------------------

    [Test]
    public void Set_BuildsFlatKeyValuePairs()
    {
        var c = CameraCommands.ParseSet("t 4 rate 0.25 loop 1 paused 0")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("camera.set"));
            Assert.That(c.Token, Is.Null, "set has no subject — there is one camera player");
            Assert.That(c.Values, Is.EqualTo(new double[]
            {
                CameraCommands.SetT, 4,
                CameraCommands.SetRate, 0.25,
                CameraCommands.SetLoop, 1,
                CameraCommands.SetPaused, 0,
            }));
        });
    }

    [Test]
    public void Set_IsSparse()
        => Assert.That(CameraCommands.ParseSet("rate 2")!.Values,
            Is.EqualTo(new double[] { CameraCommands.SetRate, 2 }));

    [TestCase("")]
    [TestCase("rate")]
    [TestCase("rate 2 rate 3")]
    [TestCase("t -1")]
    [TestCase("paused 2")]
    [TestCase("loop yes")]
    [TestCase("wobble 1")]
    [TestCase("rate 2 extra")]
    public void Set_RejectsMalformedLines(string line)
        => Assert.That(CameraCommands.ParseSet(line), Is.Null);

    // ---- phase ------------------------------------------------------------------------------------------

    [Test]
    public void EveryCameraAction_IsFramePhase()
    {
        string[] actions =
        [
            CameraCommands.EnabledAction, CameraCommands.ReleaseAction, CameraCommands.ModeAction,
            CameraCommands.FollowAction, CameraCommands.TidalAction, CameraCommands.PositionAction,
            CameraCommands.FrameAction, CameraCommands.AnchorAction, CameraCommands.GeoAction,
            CameraCommands.OrbitRadiusAction, CameraCommands.OrbitAzimuthAction,
            CameraCommands.OrbitElevationAction, CameraCommands.RotationAction, CameraCommands.AimAction,
            CameraCommands.AimTargetAction, CameraCommands.AimOffsetAction, CameraCommands.AimFrameAction,
            CameraCommands.AimUpAction, CameraCommands.RollAction, CameraCommands.FovAction,
            CameraCommands.OrthoAction, CameraCommands.OrthoHeightAction, CameraCommands.SmoothingAction,
            CameraCommands.PoseResetAction, CameraCommands.PlayAction, CameraCommands.SetAction,
            CameraCommands.StopAction,
        ];

        Assert.Multiple(() =>
        {
            foreach (var action in actions)
            {
                Assert.That(action, Does.StartWith("camera."), action);
                Assert.That(SimCommand.PhaseFor(action), Is.EqualTo(CommandPhase.Frame),
                    $"{action} must not be in SolverActions — nothing about the camera is solver-visible");
            }
        });
    }

    // ---- read-back round trip ----------------------------------------------------------------------------

    [Test]
    public void CompositeReadBacks_ReParseThroughTheirOwnGrammar()
    {
        // AGENTS.md §7: read a leaf, write it straight back, and nothing changes.
        var pose = CameraPose.Default with
        {
            Position = new Vec3(-40.25, 8, 12),
            Frame = FrameKind.BodyFixed,
            Anchor = TargetRef.Body("earth"),
            Latitude = 28.573,
            Longitude = -80.649,
            Altitude = 45,
            AimTarget = TargetRef.Part("apollo11", "77"),
            AimOffset = new Vec3(0, 1.2, 0),
            AimFrame = FrameKind.Lvlh,
            AimUp = AimUpKind.Velocity,
            Roll = -6,
            Rotation = new Quat(0, 0, 0, 1),
        };
        var status = CameraStatus.Idle with { Pose = pose, TrackName = "flyby", TrackTMs = 2500, Rate = 0.5 };

        Assert.Multiple(() =>
        {
            var position = CameraCommands.ParsePosition(CameraFormat.Position(pose))!;
            Assert.That(position.Values, Is.EqualTo(new[] { -40.25, 8.0, 12.0 }));
            Assert.That(position.Token, Is.EqualTo("bodyfixed"));

            var geo = CameraCommands.ParseGeo(CameraFormat.Geo(pose))!;
            Assert.That(geo.Values, Is.EqualTo(new[] { 28.573, -80.649, 45.0 }));
            Assert.That(geo.Token, Is.EqualTo("body:earth"));

            var aim = CameraCommands.ParseAim(CameraFormat.Aim(pose))!;
            Assert.That(aim.Token, Is.EqualTo("part:apollo11/77"));
            Assert.That(aim.Values, Is.EqualTo(new[]
            {
                0d, 1.2, 0, (double)(int)FrameKind.Lvlh, (double)(int)AimUpKind.Velocity, -6, 1,
            }));

            Assert.That(CameraCommands.ParseRotation(Formats.Quat(pose.Rotation.ToSnapshot())), Is.Not.Null);
            Assert.That(CameraCommands.ParsePlay(CameraFormat.Play(status))!.Token, Is.EqualTo("flyby"));

            var set = CameraCommands.ParseSet(CameraFormat.Set(status))!;
            Assert.That(set.Values, Is.EqualTo(new double[]
            {
                CameraCommands.SetT, 2.5,
                CameraCommands.SetRate, 0.5,
                CameraCommands.SetLoop, 0,
                CameraCommands.SetPaused, 0,
            }));
        });
    }

    [Test]
    public void GeoReadBack_OmitsANonBodyAnchor_SoItStillReParses()
    {
        var pose = CameraPose.Default with { Anchor = TargetRef.Vessel("apollo11"), Latitude = 1, Altitude = 2 };
        var text = CameraFormat.Geo(pose);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Not.Contain("vessel:"));
            Assert.That(CameraCommands.ParseGeo(text), Is.Not.Null);
        });
    }
}
