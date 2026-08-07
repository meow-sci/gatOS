using gatOS.SimFs.Camera;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     The double-precision vector/quaternion pair the whole camera stack is built on. The cases that
///     matter here are the degenerate ones: every one of them must degrade to a defined value rather
///     than a NaN, because a NaN reaching the game's view matrix blanks the screen.
/// </summary>
[TestFixture]
public sealed class CameraMathTests
{
    private const double Tol = 1e-12;

    [Test]
    public void Vec3_Arithmetic_IsComponentWise()
    {
        var a = new Vec3(1, 2, 3);
        var b = new Vec3(10, 20, 30);

        Assert.Multiple(() =>
        {
            Assert.That(a + b, Is.EqualTo(new Vec3(11, 22, 33)));
            Assert.That(b - a, Is.EqualTo(new Vec3(9, 18, 27)));
            Assert.That(-a, Is.EqualTo(new Vec3(-1, -2, -3)));
            Assert.That(a * 2.0, Is.EqualTo(new Vec3(2, 4, 6)));
            Assert.That(2.0 * a, Is.EqualTo(new Vec3(2, 4, 6)));
            Assert.That(b / 10.0, Is.EqualTo(new Vec3(1, 2, 3)));
        });
    }

    [Test]
    public void Vec3_DotCrossLength_MatchTextbook()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Vec3.Dot(new Vec3(1, 2, 3), new Vec3(4, -5, 6)), Is.EqualTo(12.0).Within(Tol));
            Assert.That(Vec3.Cross(new Vec3(1, 0, 0), new Vec3(0, 1, 0)), Is.EqualTo(new Vec3(0, 0, 1)));
            Assert.That(new Vec3(3, 4, 0).Length, Is.EqualTo(5.0).Within(Tol));
            Assert.That(new Vec3(3, 4, 0).LengthSquared, Is.EqualTo(25.0).Within(Tol));
        });
    }

    /// <summary>
    ///     Solar-system scale is the reason this type exists. At 1 AU a <c>float</c>'s representable
    ///     step is ~8 km — a camera pose would quantise to kilometres. A <c>double</c>'s step there is
    ///     ~30 µm, so a 1 mm nudge survives to well within a tenth of a millimetre.
    /// </summary>
    [Test]
    public void Vec3_HoldsAstronomicalScaleToSubMillimetre()
    {
        const double au = 1.495978707e11;
        var v = new Vec3(au, 0, 0);
        var nudged = v + new Vec3(0.001, 0, 0);

        Assert.That(nudged.X - v.X, Is.EqualTo(0.001).Within(1e-4));
        Assert.That(nudged, Is.Not.EqualTo(v));

        // The same nudge in single precision is invisible — the point of the type decision.
        Assert.That((float)au + 0.001f, Is.EqualTo((float)au));
    }

    /// <summary>A zero-length aim vector must produce "no direction", not NaN.</summary>
    [Test]
    public void Vec3_NormalizedOfZero_IsZeroNotNaN()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Vec3.Zero.Normalized(), Is.EqualTo(Vec3.Zero));
            Assert.That(new Vec3(double.NaN, 0, 0).Normalized(), Is.EqualTo(Vec3.Zero));
            Assert.That(new Vec3(0, 3, 4).Normalized().Length, Is.EqualTo(1.0).Within(Tol));
        });
    }

    [Test]
    public void Vec3_IsFinite_DetectsPoison()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new Vec3(1, 2, 3).IsFinite, Is.True);
            Assert.That(new Vec3(1, double.NaN, 3).IsFinite, Is.False);
            Assert.That(new Vec3(double.PositiveInfinity, 0, 0).IsFinite, Is.False);
        });
    }

    /// <summary>Endpoint exactness — the same reason easing snaps; keys must be hit, not approached.</summary>
    [Test]
    public void Vec3_Lerp_HasExactClampedEndpoints()
    {
        var a = new Vec3(-1e11, 7, 3);
        var b = new Vec3(1e11, -7, 9);

        Assert.Multiple(() =>
        {
            Assert.That(Vec3.Lerp(a, b, 0.0), Is.EqualTo(a));
            Assert.That(Vec3.Lerp(a, b, 1.0), Is.EqualTo(b));
            Assert.That(Vec3.Lerp(a, b, -5.0), Is.EqualTo(a));
            Assert.That(Vec3.Lerp(a, b, 5.0), Is.EqualTo(b));
            Assert.That(Vec3.Lerp(a, b, double.NaN), Is.EqualTo(a));
            Assert.That(Vec3.Lerp(a, b, 0.5).X, Is.EqualTo(0.0).Within(1e-3));
        });
    }

    [Test]
    public void Vec3_ConvertsToAndFromTheSnapshotDto()
    {
        var snap = new double3Snap(1.5, -2.5, 3.5);
        var v = Vec3.From(snap);

        Assert.Multiple(() =>
        {
            Assert.That(v, Is.EqualTo(new Vec3(1.5, -2.5, 3.5)));
            Assert.That(v.ToSnapshot(), Is.EqualTo(snap));
            Assert.That((Vec3)snap, Is.EqualTo(v));
            Assert.That((double3Snap)v, Is.EqualTo(snap));
        });
    }

    [Test]
    public void Quat_Identity_RotatesNothing()
    {
        var v = new Vec3(1, 2, 3);
        Assert.That(Quat.Identity.Rotate(v), Is.EqualTo(v));
    }

    /// <summary>A quarter turn about +Z takes +X to +Y — the sanity check for handedness.</summary>
    [Test]
    public void Quat_FromAxisAngle_RotatesRightHanded()
    {
        var q = Quat.FromAxisAngle(new Vec3(0, 0, 1), Math.PI / 2.0);
        var r = q.Rotate(new Vec3(1, 0, 0));

        Assert.Multiple(() =>
        {
            Assert.That(r.X, Is.EqualTo(0.0).Within(1e-12));
            Assert.That(r.Y, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(r.Z, Is.EqualTo(0.0).Within(1e-12));
        });
    }

    /// <summary>
    ///     The composition convention, asserted rather than assumed: <c>a * b</c> means "apply b, then
    ///     a". A yaw composed with a pitch gives different answers in the two orders, so this pins it.
    /// </summary>
    [Test]
    public void Quat_Multiply_AppliesRightOperandFirst()
    {
        var yaw = Quat.FromAxisAngle(new Vec3(0, 0, 1), Math.PI / 2.0);   // +X → +Y
        var pitch = Quat.FromAxisAngle(new Vec3(0, 1, 0), Math.PI / 2.0); // +X → −Z

        var v = new Vec3(1, 0, 0);

        // yaw * pitch  ==  pitch first, then yaw.
        var composed = (yaw * pitch).Rotate(v);
        var sequential = yaw.Rotate(pitch.Rotate(v));

        Assert.Multiple(() =>
        {
            Assert.That(composed.X, Is.EqualTo(sequential.X).Within(1e-12));
            Assert.That(composed.Y, Is.EqualTo(sequential.Y).Within(1e-12));
            Assert.That(composed.Z, Is.EqualTo(sequential.Z).Within(1e-12));
            // ... and it is NOT the other order.
            var other = (pitch * yaw).Rotate(v);
            Assert.That(Vec3.Dot(composed - other, composed - other), Is.GreaterThan(0.1));
        });
    }

    [Test]
    public void Quat_InverseAndConjugate_UndoTheRotation()
    {
        var q = Quat.FromAxisAngle(new Vec3(1, 2, 3), 0.7);
        var v = new Vec3(4, -5, 6);

        var round = q.Inverse().Rotate(q.Rotate(v));
        Assert.Multiple(() =>
        {
            Assert.That(round.X, Is.EqualTo(v.X).Within(1e-12));
            Assert.That(round.Y, Is.EqualTo(v.Y).Within(1e-12));
            Assert.That(round.Z, Is.EqualTo(v.Z).Within(1e-12));
            // For a unit quaternion conjugate == inverse.
            Assert.That(Quat.Dot(q.Conjugate(), q.Inverse()), Is.EqualTo(1.0).Within(1e-12));
        });
    }

    [Test]
    public void Quat_DegenerateInputs_DegradeToIdentity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new Quat(0, 0, 0, 0).Normalized(), Is.EqualTo(Quat.Identity));
            Assert.That(new Quat(double.NaN, 0, 0, 1).Normalized(), Is.EqualTo(Quat.Identity));
            Assert.That(new Quat(0, 0, 0, 0).Inverse(), Is.EqualTo(Quat.Identity));
            Assert.That(Quat.FromAxisAngle(Vec3.Zero, 1.0), Is.EqualTo(Quat.Identity));
            Assert.That(Quat.FromAxisAngle(new Vec3(0, 0, 1), double.NaN), Is.EqualTo(Quat.Identity));
        });
    }

    [Test]
    public void Quat_ConvertsToAndFromTheSnapshotDto()
    {
        var snap = new QuatSnap(0.1, 0.2, 0.3, 0.9);
        var q = Quat.From(snap);

        Assert.Multiple(() =>
        {
            Assert.That(q, Is.EqualTo(new Quat(0.1, 0.2, 0.3, 0.9)));
            Assert.That(q.ToSnapshot(), Is.EqualTo(snap));
            Assert.That((Quat)snap, Is.EqualTo(q));
            Assert.That((QuatSnap)q, Is.EqualTo(snap));
        });
    }

    /// <summary>Value equality is what lets the evaluator's determinism tests compare whole poses.</summary>
    [Test]
    public void RecordStructs_HaveValueEquality()
    {
        var v = new Vec3(1, 2, 3);
        var q = new Quat(1, 2, 3, 4);

        Assert.Multiple(() =>
        {
            Assert.That(v, Is.EqualTo(new Vec3(1.0, 2.0, 3.0)));
            Assert.That(q, Is.EqualTo(new Quat(1.0, 2.0, 3.0, 4.0)));
            Assert.That(v, Is.Not.EqualTo(new Vec3(1, 2, 3.0000001)));
        });
    }
}
