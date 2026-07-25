using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Iva;

/// <summary>
///     The forcing field for free-floating objects inside a moving vessel's cabin — the whole of the
///     IVA-physics model, as pure math over plain vectors (plans/IVA_MOVEMENTS.md §2). It lives here,
///     outside the game-coupled half of the mod, so it can be unit-tested on a bare host: the KSA
///     integration only has to *supply* the four inputs and *apply* the result.
/// </summary>
/// <remarks>
///     <para>
///         Work in the vessel's <b>assembly frame</b> (the frame parts live in). It shares orientation
///         with the body frame and differs only by a translation, so body-frame vectors
///         (<c>Vehicle.AccelerationBody</c>, <c>BodyRates</c>, <c>AngularAccelerationBody</c>) transfer
///         unchanged and only positions shift by the centre of mass.
///     </para>
///     <para>
///         For an object at assembly-frame position <c>r</c> moving at cabin-frame velocity <c>v</c>,
///         with <c>r_b = r − CenterOfMassAsmb</c>:
///         <code>
///         a = −a_p − α×r_b − 2 ω×v − ω×(ω×r_b)
///              └──┘  └────┘  └────┘  └────────┘
///            linear  Euler  Coriolis centrifugal
///         </code>
///         <c>a_p</c> is <b>proper</b> (non-gravitational) acceleration — an accelerometer reading —
///         so gravity never appears explicitly and never has to be modelled: on the pad <c>a_p</c> is
///         +1 g "up" and the field is 1 g down; coasting in orbit <c>a_p</c> is zero and objects drift;
///         under thrust they slam aft. One formula, every flight situation.
///     </para>
///     <para>
///         Tidal terms are deliberately ignored: over a 2 m cabin they are ~10⁻⁶ m/s², six orders of
///         magnitude below the sleep threshold.
///     </para>
/// </remarks>
public static class CabinPhysics
{
    /// <summary>
    ///     The apparent (fictitious + reaction) acceleration felt by an object floating in the cabin,
    ///     in the vessel's assembly/body frame, m/s². Add it to nothing else — it is the complete
    ///     forcing term.
    /// </summary>
    /// <param name="properAccel">
    ///     The vessel's proper acceleration in body axes, m/s² (KSA <c>Vehicle.AccelerationBody</c>) —
    ///     what an accelerometer bolted to the hull reads. Zero in freefall.
    /// </param>
    /// <param name="bodyRates">The vessel's angular velocity in body axes, rad/s (<c>ω</c>).</param>
    /// <param name="angularAccel">The vessel's angular acceleration in body axes, rad/s² (<c>α</c>).</param>
    /// <param name="offsetFromCom">
    ///     The object's position relative to the vessel's centre of mass, in assembly-frame axes,
    ///     metres (<c>r_b</c>) — the lever arm the Euler and centrifugal terms act on.
    /// </param>
    /// <param name="velocity">The object's velocity <i>relative to the cabin</i>, m/s (<c>v</c>).</param>
    public static double3Snap ApparentAcceleration(
        double3Snap properAccel, double3Snap bodyRates, double3Snap angularAccel,
        double3Snap offsetFromCom, double3Snap velocity)
    {
        var euler = Cross(angularAccel, offsetFromCom);
        var coriolis = Scale(Cross(bodyRates, velocity), 2.0);
        var centrifugal = Cross(bodyRates, Cross(bodyRates, offsetFromCom));
        return new double3Snap(
            -properAccel.X - euler.X - coriolis.X - centrifugal.X,
            -properAccel.Y - euler.Y - coriolis.Y - centrifugal.Y,
            -properAccel.Z - euler.Z - coriolis.Z - centrifugal.Z);
    }

    /// <summary>The cross product <c>a × b</c>.</summary>
    public static double3Snap Cross(double3Snap a, double3Snap b)
        => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

    /// <summary>Scales a vector by <paramref name="k"/>.</summary>
    public static double3Snap Scale(double3Snap v, double k) => new(v.X * k, v.Y * k, v.Z * k);

    /// <summary>The vector difference <c>a − b</c>.</summary>
    public static double3Snap Subtract(double3Snap a, double3Snap b)
        => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>The Euclidean length of <paramref name="v"/>.</summary>
    public static double Length(double3Snap v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
}
