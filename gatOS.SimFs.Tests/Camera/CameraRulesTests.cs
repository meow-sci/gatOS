using gatOS.SimFs.Camera;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     The game-free camera validation of <see cref="CameraRules"/> and the round-tripping of
///     <see cref="TargetRef"/> — the rules both the 9p parse path and (later) the game-side
///     re-validation call, because <c>POST /v1/command</c> bypasses the parse entirely.
/// </summary>
[TestFixture]
public sealed class CameraRulesTests
{
    // ---- token tables ---------------------------------------------------------------------------

    [TestCase("ecl", FrameKind.Ecl)]
    [TestCase("cce", FrameKind.Cce)]
    [TestCase("bodyfixed", FrameKind.BodyFixed)]
    [TestCase("BodyFixed", FrameKind.BodyFixed)]
    [TestCase("ENU", FrameKind.Enu)]
    [TestCase("lvlh", FrameKind.Lvlh)]
    [TestCase("chase", FrameKind.Chase)]
    public void Frames_ParseCaseInsensitively_AndRoundTrip(string token, FrameKind expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(CameraRules.TryParseFrame(token, out var frame), Is.True);
            Assert.That(frame, Is.EqualTo(expected));
            Assert.That(CameraRules.NameOf(expected), Is.EqualTo(token.ToLowerInvariant()));
            Assert.That(CameraRules.TryParseFrame(CameraRules.NameOf(expected), out var again), Is.True);
            Assert.That(again, Is.EqualTo(expected));
        });
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("body-fixed")]
    [TestCase("eci")]
    [TestCase("ecl ")]
    public void BadFrameToken_IsRejected(string? token)
        => Assert.That(CameraRules.TryParseFrame(token, out _), Is.False);

    [TestCase("world", AimUpKind.World)]
    [TestCase("TARGET", AimUpKind.Target)]
    [TestCase("velocity", AimUpKind.Velocity)]
    [TestCase("free", AimUpKind.Free)]
    public void AimUp_Parses(string token, AimUpKind expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(CameraRules.TryParseAimUp(token, out var up), Is.True);
            Assert.That(up, Is.EqualTo(expected));
            Assert.That(CameraRules.NameOf(expected), Is.EqualTo(token.ToLowerInvariant()));
        });
    }

    [TestCase("orbit", CameraModeKind.Orbit)]
    [TestCase("free", CameraModeKind.Free)]
    [TestCase("map", CameraModeKind.Map)]
    [TestCase("iva", CameraModeKind.Iva)]
    [TestCase("Fixed", CameraModeKind.Fixed)]
    public void Modes_Parse(string token, CameraModeKind expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(CameraRules.TryParseMode(token, out var mode), Is.True);
            Assert.That(mode, Is.EqualTo(expected));
            Assert.That(CameraRules.NameOf(expected), Is.EqualTo(token.ToLowerInvariant()));
        });
    }

    [Test]
    public void OutOfRangeEnum_HasNoName()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CameraRules.NameOf((FrameKind)99), Is.Null);
            Assert.That(CameraRules.NameOf((AimUpKind)99), Is.Null);
            Assert.That(CameraRules.NameOf((CameraModeKind)99), Is.Null);
        });
    }

    // ---- id charset -----------------------------------------------------------------------------

    [TestCase("apollo11", true)]
    [TestCase("a.b_c-1", true)]
    [TestCase("", false)]
    [TestCase(".", false)]
    [TestCase("..", false)]
    [TestCase("has space", false)]
    [TestCase("has/slash", false)]
    [TestCase("has:colon", false)]
    public void IsValidId_MatchesTheSimSanitizedCharset(string id, bool valid)
        => Assert.That(CameraRules.IsValidId(id), Is.EqualTo(valid));

    [Test]
    public void IsValidId_CapsAt64Chars()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CameraRules.IsValidId(new string('a', 64)), Is.True);
            Assert.That(CameraRules.IsValidId(new string('a', 65)), Is.False);
        });
    }

    // ---- TargetRef round-trip -------------------------------------------------------------------

    [Test]
    public void TargetRef_RoundTripsEveryValidSpelling()
    {
        TargetRef[] targets =
        [
            TargetRef.None,
            TargetRef.Vessel("apollo11"),
            TargetRef.Body("earth"),
            TargetRef.Part("apollo11", "1234"),
        ];

        Assert.Multiple(() =>
        {
            foreach (var target in targets)
            {
                Assert.That(TargetRef.TryParse(target.ToString(), out var parsed), Is.True, target.ToString());
                Assert.That(parsed, Is.EqualTo(target), target.ToString());
            }
        });
    }

    [TestCase("none", TargetKind.None, "", "")]
    [TestCase("NONE", TargetKind.None, "", "")]
    [TestCase("vessel:apollo11", TargetKind.Vessel, "apollo11", "")]
    [TestCase("VESSEL:apollo11", TargetKind.Vessel, "apollo11", "")]
    [TestCase("body:earth", TargetKind.Body, "earth", "")]
    [TestCase("part:apollo11/9", TargetKind.Part, "apollo11", "9")]
    public void TargetRef_ParsesTheWireSpelling(string token, TargetKind kind, string id, string instance)
    {
        Assert.Multiple(() =>
        {
            Assert.That(TargetRef.TryParse(token, out var target), Is.True);
            Assert.That(target.Kind, Is.EqualTo(kind));
            Assert.That(target.Id, Is.EqualTo(id));
            Assert.That(target.PartInstanceId, Is.EqualTo(instance));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("apollo11")]
    [TestCase("vessel:")]
    [TestCase(":apollo11")]
    [TestCase("ship:apollo11")]
    [TestCase("vessel:has space")]
    [TestCase("vessel:has/slash")]
    [TestCase("part:apollo11")]
    [TestCase("part:apollo11/")]
    [TestCase("part:/9")]
    public void TargetRef_RejectsMalformedSpellings(string? token)
    {
        Assert.Multiple(() =>
        {
            Assert.That(TargetRef.TryParse(token, out var target), Is.False, token ?? "<null>");
            Assert.That(target, Is.EqualTo(TargetRef.None), "a failed parse yields None, never a half-value");
        });
    }

    [Test]
    public void TargetRef_HasTarget_IsFalseOnlyForNone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TargetRef.None.HasTarget, Is.False);
            Assert.That(TargetRef.Vessel("x").HasTarget, Is.True);
            Assert.That(TargetRef.Body("x").HasTarget, Is.True);
            Assert.That(TargetRef.Part("x", "1").HasTarget, Is.True);
        });
    }

    // ---- scalar ranges ---------------------------------------------------------------------------

    [TestCase(1.0, true)]
    [TestCase(179.0, true)]
    [TestCase(0.999, false)]
    [TestCase(179.001, false)]
    [TestCase(double.NaN, false)]
    [TestCase(double.PositiveInfinity, false)]
    public void Fov_IsBoundedByTheConfiguredRange(double deg, bool valid)
        => Assert.That(CameraRules.IsValidFov(deg, 1, 179), Is.EqualTo(valid));

    [TestCase(-90.0, true)]
    [TestCase(90.0, true)]
    [TestCase(0.0, true)]
    [TestCase(-90.001, false)]
    [TestCase(90.001, false)]
    [TestCase(double.NaN, false)]
    public void Latitude_IsPlusMinus90(double deg, bool valid)
        => Assert.That(CameraRules.IsValidLatitude(deg), Is.EqualTo(valid));

    [TestCase(-180.0, true)]
    [TestCase(360.0, true)]
    [TestCase(279.4, true)]
    [TestCase(-180.001, false)]
    [TestCase(360.001, false)]
    [TestCase(double.NegativeInfinity, false)]
    public void Longitude_AcceptsBothConventions(double deg, bool valid)
        => Assert.That(CameraRules.IsValidLongitude(deg), Is.EqualTo(valid));

    [TestCase(0.0, 0.0)]
    [TestCase(-80.6, -80.6)]
    [TestCase(279.4, -80.6)]
    [TestCase(180.0, -180.0)]
    [TestCase(-180.0, -180.0)]
    [TestCase(360.0, 0.0)]
    public void NormalizeLongitude_FoldsIntoTheCanonicalHalfOpenRange(double input, double expected)
        => Assert.That(CameraRules.NormalizeLongitude(input), Is.EqualTo(expected).Within(1e-9));

    [TestCase(0.0, true)]
    [TestCase(45.0, true)]
    [TestCase(-0.001, false)]
    [TestCase(double.NaN, false)]
    public void Altitude_IsNonNegativeAndFinite(double metres, bool valid)
        => Assert.That(CameraRules.IsValidAltitude(metres), Is.EqualTo(valid));

    [TestCase(0.0, true)]
    [TestCase(10.0, true)]
    [TestCase(10.001, false)]
    [TestCase(-0.001, false)]
    [TestCase(double.PositiveInfinity, false)]
    public void Smoothing_IsZeroToTen(double seconds, bool valid)
        => Assert.That(CameraRules.IsValidSmoothing(seconds), Is.EqualTo(valid));

    [TestCase(0.0, false)]
    [TestCase(0.001, true)]
    [TestCase(-1.0, false)]
    [TestCase(double.NaN, false)]
    public void OrthoHeight_IsStrictlyPositive(double metres, bool valid)
        => Assert.That(CameraRules.IsValidOrthoHeight(metres), Is.EqualTo(valid));

    [TestCase(-720.0, true)]
    [TestCase(0.0, true)]
    [TestCase(double.NaN, false)]
    public void Roll_IsAnyFiniteAngle(double deg, bool valid)
        => Assert.That(CameraRules.IsValidRoll(deg), Is.EqualTo(valid));

    [Test]
    public void OrbitAndTimeScale_Ranges()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CameraRules.IsValidOrbitRadius(0), Is.True);
            Assert.That(CameraRules.IsValidOrbitRadius(-1), Is.False);
            Assert.That(CameraRules.IsValidOrbitAzimuth(-720), Is.True);
            Assert.That(CameraRules.IsValidOrbitAzimuth(double.NaN), Is.False);
            Assert.That(CameraRules.IsValidOrbitElevation(90), Is.True);
            Assert.That(CameraRules.IsValidOrbitElevation(90.001), Is.False);
            Assert.That(CameraRules.IsValidTimeScale(0), Is.True, "0 is a legal pause");
            Assert.That(CameraRules.IsValidTimeScale(-0.5), Is.False);
        });
    }

    // ---- vectors ------------------------------------------------------------------------------------

    [Test]
    public void IsFiniteVector_RejectsNullNonFiniteAndWrongArity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CameraRules.IsFiniteVector(null), Is.False);
            Assert.That(CameraRules.IsFiniteVector(new[] { 1.0, 2.0, 3.0 }), Is.True);
            Assert.That(CameraRules.IsFiniteVector(new[] { 1.0, double.NaN, 3.0 }), Is.False);
            Assert.That(CameraRules.IsFiniteVector(new[] { 1.0, 2.0 }, 3), Is.False);
            Assert.That(CameraRules.IsFiniteVector(new[] { 1.0, 2.0, 3.0 }, 3), Is.True);
        });
    }

    [Test]
    public void IsUnitQuaternionish_AcceptsNearUnit_AndRejectsDegenerate()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CameraRules.IsUnitQuaternionish(new[] { 0.0, 0.0, 0.0, 1.0 }), Is.True);
            Assert.That(CameraRules.IsUnitQuaternionish(new[] { 0.0, 0.0, 0.0, 1.4 }), Is.True, "norm 1.4 ≤ 2");
            Assert.That(CameraRules.IsUnitQuaternionish(new[] { 0.0, 0.0, 0.0, 0.0 }), Is.False,
                "a zero quaternion names no rotation");
            Assert.That(CameraRules.IsUnitQuaternionish(new[] { 0.0, 0.0, 0.0, 0.4 }), Is.False, "norm < 0.5");
            Assert.That(CameraRules.IsUnitQuaternionish(new[] { 0.0, 0.0, 0.0, 2.5 }), Is.False, "norm > 2");
            Assert.That(CameraRules.IsUnitQuaternionish(new[] { 0.0, 0.0, 1.0 }), Is.False, "wrong arity");
            Assert.That(CameraRules.IsUnitQuaternionish(new[] { 0.0, 0.0, 0.0, double.NaN }), Is.False);
        });
    }
}
