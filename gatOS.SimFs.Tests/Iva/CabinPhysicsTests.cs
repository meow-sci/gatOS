using gatOS.SimFs.Iva;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Iva;

/// <summary>
///     The IVA cabin forcing field (plans/IVA_MOVEMENTS.md §2). This is the whole physics model, and
///     it is pure math over plain vectors — so it is tested here, on a bare host, rather than behind
///     the KSA compile gate. Each case is one flight situation the field has to get right.
/// </summary>
[TestFixture]
public sealed class CabinPhysicsTests
{
    private const double Tol = 1e-9;

    private static readonly double3Snap Zero = new(0, 0, 0);

    /// <summary>
    ///     Coasting: no proper acceleration, no rotation ⇒ nothing acts on the object and it drifts
    ///     in a straight line. The case the whole feature exists for, and the one KSA's own solver
    ///     cannot deliver (it does not step Bepu at all for an unconstrained vessel).
    /// </summary>
    [Test]
    public void Freefall_IsWeightless()
    {
        var a = CabinPhysics.ApparentAcceleration(
            properAccel: Zero, bodyRates: Zero, angularAccel: Zero,
            offsetFromCom: new double3Snap(0.4, -0.2, 0.7), velocity: new double3Snap(0.1, 0, -0.05));

        AssertVector(a, Zero);
    }

    /// <summary>
    ///     On the pad KSA reports the normal-force reading: +1 g along the "up" body axis. The field
    ///     is its negation — 1 g "down" — so loose objects rest on the cabin floor.
    /// </summary>
    [Test]
    public void OnThePad_ObjectsFallDown()
    {
        // Gemini's IVA seats declare UpAxis Z = −1, so "up" is −Z in body axes.
        var up = new double3Snap(0, 0, -9.81);

        var a = CabinPhysics.ApparentAcceleration(
            properAccel: up, bodyRates: Zero, angularAccel: Zero,
            offsetFromCom: new double3Snap(0.1, 0.2, 0.3), velocity: Zero);

        AssertVector(a, new double3Snap(0, 0, 9.81));
        Assert.That(CabinPhysics.Length(a), Is.EqualTo(9.81).Within(Tol));
    }

    /// <summary>Under thrust the cabin accelerates forward, so unrestrained objects slam aft at −a_p.</summary>
    [Test]
    public void UnderThrust_ObjectsSlamAft()
    {
        // +X is the nose in KSA body axes; a 3 g burn.
        var a = CabinPhysics.ApparentAcceleration(
            properAccel: new double3Snap(3 * 9.81, 0, 0), bodyRates: Zero, angularAccel: Zero,
            offsetFromCom: new double3Snap(0.5, 0, 0), velocity: Zero);

        AssertVector(a, new double3Snap(-3 * 9.81, 0, 0));
    }

    /// <summary>
    ///     A steady spin about +Z throws an object at radius r outward (centrifugal, +X for an object
    ///     on the +X side) at ω²r, with no Euler term because α is zero.
    /// </summary>
    [Test]
    public void SteadySpin_IsCentrifugalOutward()
    {
        const double omega = 2.0; // rad/s about +Z
        const double r = 0.75;    // metres along +X from the CoM

        var a = CabinPhysics.ApparentAcceleration(
            properAccel: Zero, bodyRates: new double3Snap(0, 0, omega), angularAccel: Zero,
            offsetFromCom: new double3Snap(r, 0, 0), velocity: Zero);

        AssertVector(a, new double3Snap(omega * omega * r, 0, 0));
    }

    /// <summary>
    ///     Spin-up (α ≠ 0) adds the Euler term −α×r_b: an object on the +X side of a vessel angularly
    ///     accelerating about +Z lags the rotation, i.e. is pushed toward −Y.
    /// </summary>
    [Test]
    public void SpinUp_AddsEulerLag()
    {
        const double alpha = 1.5; // rad/s² about +Z
        const double r = 0.6;

        var a = CabinPhysics.ApparentAcceleration(
            properAccel: Zero, bodyRates: Zero, angularAccel: new double3Snap(0, 0, alpha),
            offsetFromCom: new double3Snap(r, 0, 0), velocity: Zero);

        // α×r_b = (0,0,α)×(r,0,0) = (0, αr, 0)  ⇒  the field is (0, −αr, 0).
        AssertVector(a, new double3Snap(0, -alpha * r, 0));
    }

    /// <summary>
    ///     An object moving radially outward in a rotating cabin is deflected against the rotation:
    ///     the Coriolis term −2 ω×v. Moving +X at v while spinning about +Z pushes it toward −Y.
    /// </summary>
    [Test]
    public void RadialMotionInASpin_IsDeflectedByCoriolis()
    {
        const double omega = 3.0;
        const double v = 0.4;

        var a = CabinPhysics.ApparentAcceleration(
            properAccel: Zero, bodyRates: new double3Snap(0, 0, omega), angularAccel: Zero,
            offsetFromCom: Zero, velocity: new double3Snap(v, 0, 0));

        // 2 ω×v = 2(0,0,ω)×(v,0,0) = (0, 2ωv, 0)  ⇒  the field is (0, −2ωv, 0).
        AssertVector(a, new double3Snap(0, -2 * omega * v, 0));
    }

    /// <summary>
    ///     An object exactly at the centre of mass feels no rotational term at all, however hard the
    ///     vessel spins — only the linear reaction. The lever-arm terms must key off r_b, not r.
    /// </summary>
    [Test]
    public void AtTheCenterOfMass_OnlyLinearReactionRemains()
    {
        var a = CabinPhysics.ApparentAcceleration(
            properAccel: new double3Snap(1, 2, 3),
            bodyRates: new double3Snap(5, -4, 2), angularAccel: new double3Snap(-1, 7, 0.5),
            offsetFromCom: Zero, velocity: Zero);

        AssertVector(a, new double3Snap(-1, -2, -3));
    }

    /// <summary>
    ///     The field superposes: computing it from all four terms at once equals summing the terms
    ///     computed in isolation. Cheap insurance against a sign or ordering slip in the combined form.
    /// </summary>
    [Test]
    public void TheTermsSuperpose()
    {
        var accel = new double3Snap(0.7, -1.3, 2.6);
        var omega = new double3Snap(0.2, 0.9, -0.4);
        var alpha = new double3Snap(-0.6, 0.15, 0.33);
        var offset = new double3Snap(0.31, -0.44, 0.62);
        var velocity = new double3Snap(-0.12, 0.05, 0.27);

        var combined = CabinPhysics.ApparentAcceleration(accel, omega, alpha, offset, velocity);
        var linear = CabinPhysics.ApparentAcceleration(accel, Zero, Zero, offset, Zero);
        var euler = CabinPhysics.ApparentAcceleration(Zero, Zero, alpha, offset, Zero);
        var coriolis = CabinPhysics.ApparentAcceleration(Zero, omega, Zero, Zero, velocity);
        var centrifugal = CabinPhysics.ApparentAcceleration(Zero, omega, Zero, offset, Zero);

        AssertVector(combined, new double3Snap(
            linear.X + euler.X + coriolis.X + centrifugal.X,
            linear.Y + euler.Y + coriolis.Y + centrifugal.Y,
            linear.Z + euler.Z + coriolis.Z + centrifugal.Z));
    }

    /// <summary>The vector helpers the field is built from (right-handed cross product).</summary>
    [Test]
    public void Cross_IsRightHanded()
    {
        AssertVector(CabinPhysics.Cross(new double3Snap(1, 0, 0), new double3Snap(0, 1, 0)),
            new double3Snap(0, 0, 1));
        AssertVector(CabinPhysics.Cross(new double3Snap(0, 1, 0), new double3Snap(0, 0, 1)),
            new double3Snap(1, 0, 0));
        AssertVector(CabinPhysics.Subtract(new double3Snap(3, 5, 7), new double3Snap(1, 2, 3)),
            new double3Snap(2, 3, 4));
        Assert.That(CabinPhysics.Length(new double3Snap(3, 4, 0)), Is.EqualTo(5).Within(Tol));
    }

    private static void AssertVector(double3Snap actual, double3Snap expected)
        => Assert.Multiple(() =>
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(Tol), "X");
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(Tol), "Y");
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(Tol), "Z");
        });
}
