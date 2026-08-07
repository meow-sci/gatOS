using gatOS.SimFs.Camera;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     The critically-damped pose spring. Two properties matter for footage — it must never overshoot
///     (an overshooting camera wobbles past its subject, which reads instantly as "bad game camera")
///     and it must behave identically at any frame rate (otherwise a recording looks different on
///     every machine). Both are asserted here.
/// </summary>
[TestFixture]
public sealed class PoseSmootherTests
{
    private const double Dt60 = 1.0 / 60.0;

    /// <summary>Approaches the target every single step and never passes it.</summary>
    [Test]
    public void Position_ConvergesMonotonicallyWithoutOvershoot()
    {
        var smoother = new PoseSmoother();
        var target = new Vec3(100, -40, 15);
        var current = Vec3.Zero;
        var previousDistance = (target - current).Length;

        for (var i = 0; i < 500; i++)
        {
            current = smoother.Step(current, target, 0.3, Dt60);
            var distance = (target - current).Length;

            Assert.That(current.IsFinite, Is.True, $"step {i}");
            Assert.That(distance, Is.LessThanOrEqualTo(previousDistance), $"step {i} moved away from the target");
            previousDistance = distance;
        }

        Assert.That(previousDistance, Is.LessThan(1e-6), "should have settled on the target");
    }

    /// <summary>
    ///     Frame-rate independence: <c>dt</c> enters through the exponential, so 0.3 s of smoothing is
    ///     0.3 s of smoothing whether the game is running at 30 fps or 240 fps.
    /// </summary>
    [Test]
    public void Position_IsFrameRateIndependent()
    {
        var target = new Vec3(100, 0, 0);

        var slow = RunFor(target, 1.0 / 30.0, 0.3, 0.3);
        var fast = RunFor(target, 1.0 / 240.0, 0.3, 0.3);

        var travel = target.Length;
        Assert.That((slow - fast).Length, Is.LessThan(0.02 * travel),
            $"30 fps landed at {slow.X}, 240 fps at {fast.X}");

        static Vec3 RunFor(Vec3 target, double dt, double smoothTime, double seconds)
        {
            var smoother = new PoseSmoother();
            var current = Vec3.Zero;
            var steps = (int)Math.Round(seconds / dt);
            for (var i = 0; i < steps; i++) current = smoother.Step(current, target, smoothTime, dt);
            return current;
        }
    }

    /// <summary>
    ///     <c>pose/smoothing 0</c> means "place the camera exactly where I said, this frame". It must
    ///     be exact, not almost.
    /// </summary>
    [Test]
    public void ZeroSmoothTime_IsRawPassThrough()
    {
        var smoother = new PoseSmoother();
        var target = new Vec3(3, 4, 5);

        Assert.Multiple(() =>
        {
            Assert.That(smoother.Step(Vec3.Zero, target, 0.0, Dt60), Is.EqualTo(target));
            Assert.That(smoother.Step(Vec3.Zero, target, -1.0, Dt60), Is.EqualTo(target));
            Assert.That(smoother.Step(Vec3.Zero, target, 0.3, 0.0), Is.EqualTo(target));
            Assert.That(smoother.Step(Vec3.Zero, target, 0.3, -0.1), Is.EqualTo(target));
            Assert.That(smoother.PositionVelocity, Is.EqualTo(Vec3.Zero));
        });
    }

    /// <summary>
    ///     A cut between shots must not carry the previous shot's momentum, or the camera sails past
    ///     the first frame of the new one. <see cref="PoseSmoother.Reset"/> is what a cut calls.
    /// </summary>
    [Test]
    public void Reset_ClearsVelocitySoTheNextStepStartsFresh()
    {
        var target = new Vec3(100, 0, 0);

        var flying = new PoseSmoother();
        var current = Vec3.Zero;
        for (var i = 0; i < 20; i++) current = flying.Step(current, target, 0.3, Dt60);
        Assert.That(flying.PositionVelocity.Length, Is.GreaterThan(1.0), "should have built up speed");

        flying.Reset();
        Assert.That(flying.PositionVelocity, Is.EqualTo(Vec3.Zero));

        // From here it must behave exactly like a brand-new smoother given the same state.
        var fresh = new PoseSmoother();
        var afterReset = flying.Step(current, target, 0.3, Dt60);
        var afterFresh = fresh.Step(current, target, 0.3, Dt60);
        Assert.That(afterReset, Is.EqualTo(afterFresh));
    }

    [Test]
    public void Position_NonFiniteInput_ReturnsTheTarget()
    {
        var smoother = new PoseSmoother();
        var target = new Vec3(1, 2, 3);

        Assert.Multiple(() =>
        {
            Assert.That(smoother.Step(new Vec3(double.NaN, 0, 0), target, 0.3, Dt60), Is.EqualTo(target));
            Assert.That(smoother.Step(Vec3.Zero, target, double.NaN, Dt60), Is.EqualTo(target));
            Assert.That(smoother.Step(Vec3.Zero, target, 0.3, double.PositiveInfinity), Is.EqualTo(target));
            Assert.That(smoother.PositionVelocity, Is.EqualTo(Vec3.Zero));
        });
    }

    /// <summary>A huge <c>dt</c> (a hitch, a loading pause) must snap, not launch the camera.</summary>
    [Test]
    public void Position_HugeTimestep_SnapsWithoutOvershoot()
    {
        var smoother = new PoseSmoother();
        var target = new Vec3(1000, 0, 0);
        var result = smoother.Step(Vec3.Zero, target, 0.05, 5.0);

        Assert.Multiple(() =>
        {
            Assert.That(result.X, Is.LessThanOrEqualTo(target.X + 1e-9));
            Assert.That(result.X, Is.GreaterThanOrEqualTo(0.0));
        });
    }

    [Test]
    public void Rotation_ConvergesMonotonicallyAndStaysUnit()
    {
        var smoother = new PoseSmoother();
        var axis = new Vec3(0.2, 1.0, -0.4);
        var target = Quat.FromAxisAngle(axis, 2.4);
        var current = Quat.Identity;
        var previousAngle = AngleBetween(current, target);

        for (var i = 0; i < 400; i++)
        {
            current = smoother.Step(current, target, 0.25, Dt60);
            var angle = AngleBetween(current, target);

            Assert.That(current.IsFinite, Is.True, $"step {i}");
            Assert.That(current.Length, Is.EqualTo(1.0).Within(1e-12), $"step {i}");
            Assert.That(angle, Is.LessThanOrEqualTo(previousAngle + 1e-12), $"step {i} rotated away from the target");
            previousAngle = angle;
        }

        Assert.That(previousAngle, Is.LessThan(1e-6), "should have settled on the target orientation");
    }

    /// <summary>
    ///     The Slerp-driven form cannot overshoot by construction: the interpolation fraction is
    ///     clamped to <c>[0,1]</c>, so the camera can reach the target orientation but never pass it.
    /// </summary>
    [Test]
    public void Rotation_NeverPassesTheTarget()
    {
        var smoother = new PoseSmoother();
        var axis = new Vec3(0, 0, 1);
        var target = Quat.FromAxisAngle(axis, 1.0);
        var current = Quat.Identity;

        for (var i = 0; i < 200; i++)
        {
            current = smoother.Step(current, target, 0.1, Dt60);
            // The rotation is confined to the Z axis; its angle must stay within [0, 1] rad.
            var angle = 2.0 * Math.Acos(Math.Clamp(Math.Abs(current.W), -1.0, 1.0));
            Assert.That(angle, Is.LessThanOrEqualTo(1.0 + 1e-9), $"step {i}");
        }
    }

    [Test]
    public void Rotation_ZeroSmoothTimeAndNonFiniteInput_ReturnTheTarget()
    {
        var smoother = new PoseSmoother();
        var target = Quat.FromAxisAngle(new Vec3(1, 0, 0), 0.8);

        Assert.Multiple(() =>
        {
            Assert.That(smoother.Step(Quat.Identity, target, 0.0, Dt60), Is.EqualTo(target.Normalized()));
            Assert.That(smoother.Step(Quat.Identity, target, 0.3, 0.0), Is.EqualTo(target.Normalized()));
            Assert.That(smoother.Step(new Quat(double.NaN, 0, 0, 1), target, 0.3, Dt60), Is.EqualTo(target.Normalized()));
            Assert.That(smoother.AngularVelocity, Is.EqualTo(0.0));
        });
    }

    /// <summary>Already there ⇒ nothing to do, and no division by a zero angle.</summary>
    [Test]
    public void Rotation_AlreadyAtTarget_IsAStableNoOp()
    {
        var smoother = new PoseSmoother();
        var q = Quat.FromAxisAngle(new Vec3(0, 1, 0), 0.5).Normalized();

        var result = smoother.Step(q, q, 0.3, Dt60);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsFinite, Is.True);
            Assert.That(AngleBetween(result, q), Is.LessThan(1e-12));
            Assert.That(smoother.AngularVelocity, Is.EqualTo(0.0));
        });
    }

    /// <summary>Position and rotation carry independent velocity state on one instance.</summary>
    [Test]
    public void PositionAndRotationVelocities_AreIndependent()
    {
        var smoother = new PoseSmoother();
        var position = Vec3.Zero;
        for (var i = 0; i < 10; i++) position = smoother.Step(position, new Vec3(50, 0, 0), 0.3, Dt60);

        var before = smoother.PositionVelocity;
        smoother.Step(Quat.Identity, Quat.FromAxisAngle(new Vec3(0, 0, 1), 1.0), 0.3, Dt60);

        Assert.Multiple(() =>
        {
            Assert.That(smoother.PositionVelocity, Is.EqualTo(before), "a rotation step must not disturb the position spring");
            Assert.That(smoother.AngularVelocity, Is.Not.EqualTo(0.0));
        });
    }

    private static double AngleBetween(Quat a, Quat b)
        => 2.0 * Math.Acos(Math.Clamp(Math.Abs(Quat.Dot(a.Normalized(), b.Normalized())), -1.0, 1.0));
}
