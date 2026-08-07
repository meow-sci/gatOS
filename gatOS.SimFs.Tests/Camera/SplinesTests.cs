using gatOS.SimFs.Camera;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     The interpolation primitives. The cases that earn their keep are the ones a hand-authored
///     camera track actually hits: unevenly spaced keys, duplicated keys, and a rotation pair that is
///     350° apart the wrong way round.
/// </summary>
[TestFixture]
public sealed class SplinesTests
{
    private const double Tol = 1e-12;

    // A deliberately lopsided key run: p1 and p2 are 0.1 m apart while p3 is 99 m beyond. Uniform
    // Catmull-Rom derives p2's tangent from |p3 − p1| / 2 ≈ 49.5 and flings the segment far outside
    // the p1..p2 hull; centripetal scales the tangent by the *local* spacing and cannot.
    private static readonly Vec3 U0 = new(0, 0, 0);
    private static readonly Vec3 U1 = new(1, 0, 0);
    private static readonly Vec3 U2 = new(1.1, 0, 0);
    private static readonly Vec3 U3 = new(100, 0, 0);

    [Test]
    public void CatmullRom_PassesExactlyThroughItsKeys()
    {
        var p0 = new Vec3(-3, 2, 1);
        var p1 = new Vec3(0, 0, 0);
        var p2 = new Vec3(10, 4, -2);
        var p3 = new Vec3(14, 9, 7);

        Assert.Multiple(() =>
        {
            Assert.That(Splines.CatmullRom(p0, p1, p2, p3, 0.0), Is.EqualTo(p1));
            Assert.That(Splines.CatmullRom(p0, p1, p2, p3, 1.0), Is.EqualTo(p2));
            Assert.That(Splines.CatmullRom(p0, p1, p2, p3, -0.5), Is.EqualTo(p1));
            Assert.That(Splines.CatmullRom(p0, p1, p2, p3, 1.5), Is.EqualTo(p2));
            Assert.That(Splines.CatmullRom(p0, p1, p2, p3, double.NaN), Is.EqualTo(p1));
        });
    }

    /// <summary>
    ///     <b>The centripetal property, asserted.</b> With wildly uneven key spacing every sample must
    ///     stay inside the <c>p1..p2</c> hull — no cusp, no excursion. This is the guarantee the plan
    ///     requires of the position curve.
    /// </summary>
    [Test]
    public void CatmullRom_Centripetal_DoesNotOvershootOnUnevenSpacing()
    {
        for (var i = 0; i <= 500; i++)
        {
            var p = Splines.CatmullRom(U0, U1, U2, U3, i / 500.0);
            Assert.Multiple(() =>
            {
                Assert.That(p.X, Is.GreaterThanOrEqualTo(U1.X - 1e-9), $"i={i}");
                Assert.That(p.X, Is.LessThanOrEqualTo(U2.X + 1e-9), $"i={i}");
            });
        }
    }

    /// <summary>
    ///     The control for the case above: the <i>uniform</i> parameterisation on the same keys blows
    ///     straight out of the hull. Without this the centripetal test could be passing for the wrong
    ///     reason (e.g. because the keys happened to be tame).
    /// </summary>
    [Test]
    public void CatmullRom_Uniform_DoesOvershootOnTheSameKeys()
    {
        var worst = 0.0;
        for (var i = 0; i <= 500; i++)
        {
            var p = Splines.CatmullRom(U0, U1, U2, U3, i / 500.0, alpha: 0.0);
            worst = Math.Max(worst, Math.Max(U1.X - p.X, p.X - U2.X));
        }

        Assert.That(worst, Is.GreaterThan(0.5), "uniform Catmull-Rom must overshoot here — if it does "
            + "not, the centripetal test above proves nothing");
    }

    /// <summary>Coincident keys are legal input from a parser; they must degrade, not divide by zero.</summary>
    [Test]
    public void CatmullRom_CoincidentKeys_DegradeToLinearWithoutNaN()
    {
        var a = new Vec3(1, 2, 3);
        var b = new Vec3(5, 6, 7);

        // p1 == p2: the segment has no extent, so every sample is the key itself.
        for (var i = 0; i <= 10; i++)
        {
            var p = Splines.CatmullRom(a, b, b, new Vec3(9, 9, 9), i / 10.0);
            Assert.That(p.IsFinite, Is.True);
            Assert.That(p, Is.EqualTo(b));
        }

        // p0 == p1 (a duplicated leading key): falls back to a straight line p1 → p2.
        var mid = Splines.CatmullRom(a, a, b, new Vec3(9, 9, 9), 0.5);
        Assert.Multiple(() =>
        {
            Assert.That(mid.IsFinite, Is.True);
            Assert.That(mid, Is.EqualTo(Vec3.Lerp(a, b, 0.5)));
        });

        // Everything coincident.
        var all = Splines.CatmullRom(a, a, a, a, 0.5);
        Assert.That(all, Is.EqualTo(a));
    }

    /// <summary>Non-finite keys must not poison the pose — the segment degrades to a straight line.</summary>
    [Test]
    public void CatmullRom_NonFiniteKeys_DoNotProduceNaN()
    {
        var poisoned = new Vec3(double.NaN, 0, 0);
        var p = Splines.CatmullRom(poisoned, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(2, 0, 0), 0.5);
        Assert.That(p.IsFinite, Is.True);
    }

    [Test]
    public void Bezier_HasExactEndpointsAndAKnownMidpoint()
    {
        var p0 = new Vec3(0, 0, 0);
        var c0 = new Vec3(0, 1, 0);
        var c1 = new Vec3(1, 1, 0);
        var p1 = new Vec3(1, 0, 0);

        var mid = Splines.Bezier(p0, c0, c1, p1, 0.5);

        Assert.Multiple(() =>
        {
            Assert.That(Splines.Bezier(p0, c0, c1, p1, 0.0), Is.EqualTo(p0));
            Assert.That(Splines.Bezier(p0, c0, c1, p1, 1.0), Is.EqualTo(p1));
            Assert.That(Splines.Bezier(p0, c0, c1, p1, -1.0), Is.EqualTo(p0));
            Assert.That(Splines.Bezier(p0, c0, c1, p1, 2.0), Is.EqualTo(p1));
            // Symmetric arch: midpoint at x = 0.5, y = 3/4 (the Bernstein weights at u = 1/2).
            Assert.That(mid.X, Is.EqualTo(0.5).Within(Tol));
            Assert.That(mid.Y, Is.EqualTo(0.75).Within(Tol));
        });
    }

    [Test]
    public void Bezier_DegenerateAllEqualPoints_IsThatPoint()
    {
        var p = new Vec3(7, -7, 7);
        for (var i = 0; i <= 10; i++)
        {
            Assert.That(Splines.Bezier(p, p, p, p, i / 10.0), Is.EqualTo(p));
        }
    }

    [Test]
    public void Slerp_HasExactEndpoints()
    {
        var a = Quat.FromAxisAngle(new Vec3(0, 0, 1), 0.3);
        var b = Quat.FromAxisAngle(new Vec3(0, 1, 0), 1.9);

        Assert.Multiple(() =>
        {
            Assert.That(Splines.Slerp(a, b, 0.0), Is.EqualTo(a.Normalized()));
            Assert.That(Splines.Slerp(a, b, -3.0), Is.EqualTo(a.Normalized()));
            Assert.That(Splines.Slerp(a, b, 1.0), Is.EqualTo(b.Normalized()));
            Assert.That(Splines.Slerp(a, b, 4.0), Is.EqualTo(b.Normalized()));
        });
    }

    [Test]
    public void Slerp_StaysUnitLengthAcrossTheSweep()
    {
        var a = Quat.FromAxisAngle(new Vec3(1, 2, 3), 0.2);
        var b = Quat.FromAxisAngle(new Vec3(-3, 1, 0.5), 2.6);

        for (var i = 0; i <= 200; i++)
        {
            Assert.That(Splines.Slerp(a, b, i / 200.0).Length, Is.EqualTo(1.0).Within(1e-12), $"i={i}");
        }
    }

    /// <summary>
    ///     The double-cover trap: a 350° pair must interpolate through 10° the short way, not sweep
    ///     the camera 350° the long way. Halfway must land at ±5°, never ±175°.
    /// </summary>
    [Test]
    public void Slerp_TakesTheShortPath()
    {
        var axis = new Vec3(0, 0, 1);
        var a = Quat.FromAxisAngle(axis, 0.0);
        var b = Quat.FromAxisAngle(axis, 350.0 * Math.PI / 180.0);

        var mid = Splines.Slerp(a, b, 0.5);
        var angle = 2.0 * Math.Acos(Math.Clamp(Math.Abs(Quat.Dot(a.Normalized(), mid)), -1.0, 1.0));

        Assert.That(angle * 180.0 / Math.PI, Is.EqualTo(5.0).Within(1e-9));
    }

    [Test]
    public void Slerp_HandlesIdenticalAndAntipodalInputs()
    {
        var q = Quat.FromAxisAngle(new Vec3(0, 1, 0), 1.1);

        for (var i = 0; i <= 10; i++)
        {
            var same = Splines.Slerp(q, q, i / 10.0);
            var flipped = Splines.Slerp(q, -q, i / 10.0);
            Assert.Multiple(() =>
            {
                Assert.That(same.Length, Is.EqualTo(1.0).Within(1e-12));
                Assert.That(flipped.Length, Is.EqualTo(1.0).Within(1e-12));
                // −q is the SAME rotation, so nothing should move in either case.
                Assert.That(Math.Abs(Quat.Dot(same, q.Normalized())), Is.EqualTo(1.0).Within(1e-9));
                Assert.That(Math.Abs(Quat.Dot(flipped, q.Normalized())), Is.EqualTo(1.0).Within(1e-9));
            });
        }
    }

    [Test]
    public void Squad_HasExactEndpoints()
    {
        var q0 = Quat.FromAxisAngle(new Vec3(0, 0, 1), 0.0);
        var q1 = Quat.FromAxisAngle(new Vec3(0, 0, 1), 1.0);
        var a = Quat.FromAxisAngle(new Vec3(0, 0, 1), 0.3);
        var b = Quat.FromAxisAngle(new Vec3(0, 0, 1), 0.7);

        Assert.Multiple(() =>
        {
            Assert.That(Splines.Squad(q0, a, b, q1, 0.0), Is.EqualTo(q0.Normalized()));
            Assert.That(Splines.Squad(q0, a, b, q1, 1.0), Is.EqualTo(q1.Normalized()));
        });
    }

    /// <summary>
    ///     The reason squad exists: across a shared key, the angular <i>rate</i> must not jump. Slerp
    ///     alone is only C⁰ there and flicks the camera at every waypoint.
    /// </summary>
    [Test]
    public void Squad_IsContinuousInRateAcrossAKey()
    {
        // Four keys with deliberately uneven angular spacing about a tilted axis.
        var axis = new Vec3(0.3, 1.0, -0.2).Normalized();
        var k0 = Quat.FromAxisAngle(axis, 0.0);
        var k1 = Quat.FromAxisAngle(axis, 0.4);
        var k2 = Quat.FromAxisAngle(axis, 1.5);
        var k3 = Quat.FromAxisAngle(axis, 2.0);

        // Segment A is k1 → k2, segment B is k2 → k3; they share k2.
        var a1 = Splines.SquadIntermediate(k0, k1, k2);
        var b1 = Splines.SquadIntermediate(k1, k2, k3);
        var a2 = b1;
        var b2 = Splines.SquadIntermediate(k2, k3, k3);

        const double h = 1e-4;
        var beforeKey = Splines.Squad(k1, a1, b1, k2, 1.0 - h);
        var atKeyFromA = Splines.Squad(k1, a1, b1, k2, 1.0);
        var atKeyFromB = Splines.Squad(k2, a2, b2, k3, 0.0);
        var afterKey = Splines.Squad(k2, a2, b2, k3, h);

        // C⁰: both segments land on the shared key exactly.
        Assert.Multiple(() =>
        {
            Assert.That(Angle(atKeyFromA, k2.Normalized()), Is.LessThan(1e-12));
            Assert.That(Angle(atKeyFromB, k2.Normalized()), Is.LessThan(1e-12));
        });

        // C¹: the one-sided angular rates match. Squad's whole purpose.
        var rateIn = Angle(beforeKey, atKeyFromA) / h;
        var rateOut = Angle(atKeyFromB, afterKey) / h;

        // The 0.1% slack is the one-sided finite difference's own truncation error at h = 1e-4, not
        // slop — the measured gap is ~1.6e-4 relative and shrinks with h.
        Assert.That(rateOut, Is.EqualTo(rateIn).Within(1e-3 * rateIn),
            $"rate discontinuity at the shared key: {rateIn} vs {rateOut}");

        // The control: plain slerp between the same keys IS discontinuous there — segment k1→k2
        // spans 1.1 rad in unit time while k2→k3 spans 0.5, so the rate more than halves at the key.
        // That is the flick squad exists to remove, and it proves the assertion above can fail.
        var slerpRateIn = Angle(Splines.Slerp(k1, k2, 1.0 - h), Splines.Slerp(k1, k2, 1.0)) / h;
        var slerpRateOut = Angle(Splines.Slerp(k2, k3, 0.0), Splines.Slerp(k2, k3, h)) / h;
        Assert.That(Math.Abs(slerpRateOut - slerpRateIn), Is.GreaterThan(0.5 * slerpRateIn));
    }

    /// <summary>Squad must stay on the unit sphere and never emit NaN, including for repeated keys.</summary>
    [Test]
    public void Squad_StaysUnitAndSurvivesRepeatedKeys()
    {
        var q = Quat.FromAxisAngle(new Vec3(1, 0, 0), 0.9);
        var control = Splines.SquadIntermediate(q, q, q);

        Assert.That(control.Length, Is.EqualTo(1.0).Within(1e-12));

        for (var i = 0; i <= 20; i++)
        {
            var s = Splines.Squad(q, control, control, q, i / 20.0);
            Assert.Multiple(() =>
            {
                Assert.That(s.IsFinite, Is.True);
                Assert.That(s.Length, Is.EqualTo(1.0).Within(1e-12));
            });
        }
    }

    [Test]
    public void ScalarLerp_ClampsAndHitsItsEndpointsExactly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Splines.Lerp(2.0, 8.0, 0.0), Is.EqualTo(2.0));
            Assert.That(Splines.Lerp(2.0, 8.0, 1.0), Is.EqualTo(8.0));
            Assert.That(Splines.Lerp(2.0, 8.0, -1.0), Is.EqualTo(2.0));
            Assert.That(Splines.Lerp(2.0, 8.0, 9.0), Is.EqualTo(8.0));
            Assert.That(Splines.Lerp(2.0, 8.0, double.NaN), Is.EqualTo(2.0));
            Assert.That(Splines.Lerp(2.0, 8.0, 0.5), Is.EqualTo(5.0).Within(Tol));
            Assert.That(Splines.Lerp(new Vec3(0, 0, 0), new Vec3(2, 4, 6), 0.5), Is.EqualTo(new Vec3(1, 2, 3)));
        });
    }

    /// <summary>Determinism (plan §9) across the whole primitive set.</summary>
    [Test]
    public void Primitives_AreBitDeterministic()
    {
        var qa = Quat.FromAxisAngle(new Vec3(1, 1, 0), 0.2);
        var qb = Quat.FromAxisAngle(new Vec3(0, 1, 1), 2.0);

        for (var i = 0; i <= 100; i++)
        {
            var t = i / 100.0;
            var expectedPoint = Splines.CatmullRom(U0, U1, U2, U3, t);
            var expectedRotation = Splines.Slerp(qa, qb, t);

            Assert.Multiple(() =>
            {
                Assert.That(Splines.CatmullRom(U0, U1, U2, U3, t), Is.EqualTo(expectedPoint));
                Assert.That(Splines.Slerp(qa, qb, t), Is.EqualTo(expectedRotation));
            });
        }
    }

    /// <summary>The unsigned angle between two unit rotations, radians.</summary>
    private static double Angle(Quat a, Quat b)
        => 2.0 * Math.Acos(Math.Clamp(Math.Abs(Quat.Dot(a.Normalized(), b.Normalized())), -1.0, 1.0));
}
