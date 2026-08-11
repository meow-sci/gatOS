using gatOS.SimFs.Camera;

namespace gatOS.SimFs.Tests.Camera;

[TestFixture]
public sealed class AnchoredPositionSmootherTests
{
    private const double Dt = 1.0 / 60.0;

    [Test]
    public void MovingAnchorTranslation_PassesThroughWithoutFollowingLag()
    {
        var smoother = new AnchoredPositionSmoother();
        var anchor = TargetRef.Vessel("Hunter");
        var origin = new Vec3(1e11, 2e11, -3e10);
        var offset = new Vec3(5, 0, 2);
        var world = origin + offset;

        // Seed at the requested offset, then move the vessel 120 m every render frame. Even with a
        // long smoothing time, unchanged relative placement must remain exact instead of trailing.
        world = smoother.Step(world, origin, offset, anchor, relative: true, 0.8, Dt);
        for (var i = 0; i < 120; i++)
        {
            origin += new Vec3(120, -30, 4);
            world = smoother.Step(world, origin, offset, anchor, relative: true, 0.8, Dt);
            Assert.That((world - (origin + offset)).Length, Is.LessThan(1e-8), $"frame {i}");
        }
    }

    [Test]
    public void OffsetChange_IsSmoothedWhileAnchorTranslationRemainsExact()
    {
        var smoother = new AnchoredPositionSmoother();
        var anchor = TargetRef.Vessel("Hunter");
        var origin = new Vec3(1000, 2000, 3000);
        var world = origin + new Vec3(5, 0, 0);

        world = smoother.Step(world, origin, new Vec3(5, 0, 0), anchor, true, 0.4, Dt);
        origin += new Vec3(100, 0, 0);
        var result = smoother.Step(world, origin, new Vec3(20, 0, 0), anchor, true, 0.4, Dt);
        var resultOffset = result - origin;

        Assert.Multiple(() =>
        {
            Assert.That(resultOffset.X, Is.GreaterThan(5));
            Assert.That(resultOffset.X, Is.LessThan(20));
            Assert.That(result.Y, Is.EqualTo(origin.Y).Within(1e-12));
            Assert.That(result.Z, Is.EqualTo(origin.Z).Within(1e-12));
        });
    }

    [Test]
    public void ChangingAnchor_SeedsFromCurrentWorldPositionWithoutAJump()
    {
        var smoother = new AnchoredPositionSmoother();
        var hunter = TargetRef.Vessel("Hunter");
        var gemini = TargetRef.Vessel("Gemini7");
        var firstOrigin = new Vec3(100, 0, 0);
        var world = firstOrigin + new Vec3(5, 0, 0);
        world = smoother.Step(world, firstOrigin, new Vec3(5, 0, 0), hunter, true, 0.5, Dt);

        var secondOrigin = new Vec3(1000, 0, 0);
        var result = smoother.Step(world, secondOrigin, new Vec3(10, 0, 0), gemini, true, 0.5, Dt);

        Assert.That((result - world).Length, Is.LessThan(5),
            "an anchor cut with smoothing should begin from the visible camera, not teleport to the new origin");
    }

    [Test]
    public void ZeroSmoothing_IsExactForRelativeAndAbsolutePlacement()
    {
        var smoother = new AnchoredPositionSmoother();
        var origin = new Vec3(1e11, -2e11, 3e11);
        var offset = new Vec3(20, -4, 8);

        var relative = smoother.Step(Vec3.Zero, origin, offset, TargetRef.Vessel("Hunter"), true, 0, Dt);
        var absoluteTarget = new Vec3(-40, 8, 12);
        var absolute = smoother.Step(relative, Vec3.Zero, absoluteTarget, TargetRef.None, false, 0, Dt);

        Assert.Multiple(() =>
        {
            Assert.That(relative, Is.EqualTo(origin + offset));
            Assert.That(absolute, Is.EqualTo(absoluteTarget));
        });
    }
}
