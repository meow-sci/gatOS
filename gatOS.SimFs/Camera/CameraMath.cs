using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Camera;

/// <summary>
///     A double-precision 3-vector — the numeric currency of every camera-math primitive
///     (<see cref="Easing"/>, <see cref="Splines"/>, <see cref="PoseSmoother"/> and the track
///     evaluator built on them).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why double, everywhere.</b> Camera positions are metres in the ecliptic frame at
///         solar-system scale — up to ~1.5 × 10¹¹ m. A <c>float</c> has ~7 significant digits, so at
///         1 AU its representable step is ~8 km: a camera pose would quantise to kilometres and every
///         interpolated curve would stairstep. <c>System.Numerics.Vector3</c> is therefore unusable
///         here and must never appear in this folder.
///     </para>
///     <para>
///         <b>Why a new type rather than <c>double3Snap</c>.</b> The snapshot record
///         (<c>gatOS.SimFs.Snapshots.double3Snap</c>) is already double-precision and game-free, but it
///         is a bare DTO — the serialization contract the whole <c>/sim</c> read surface is projected
///         from — and carries no arithmetic. Spline and spring math is unreadable without operators, and
///         giving the DTO operators would push maths into the snapshot contract. So <see cref="Vec3"/>
///         is the *math* type and converts to/from the DTO explicitly at the seam
///         (<see cref="From(double3Snap)"/> / <see cref="ToSnapshot"/>). The conversions are
///         <c>explicit</c>, not implicit, on purpose: two implicit conversions between two record
///         structs would make <c>a == b</c> across the pair an ambiguous-operator compile error at
///         every future call site.
///     </para>
///     <para>
///         It is a <c>readonly record struct</c> so it has value equality (tests compare poses
///         directly) and never allocates.
///     </para>
/// </remarks>
/// <param name="X">The X component.</param>
/// <param name="Y">The Y component.</param>
/// <param name="Z">The Z component.</param>
public readonly record struct Vec3(double X, double Y, double Z)
{
    /// <summary>The zero vector.</summary>
    public static Vec3 Zero => new(0.0, 0.0, 0.0);

    /// <summary>The vector whose every component is 1.</summary>
    public static Vec3 One => new(1.0, 1.0, 1.0);

    /// <summary>The squared Euclidean length — the allocation- and sqrt-free comparison form.</summary>
    public double LengthSquared => X * X + Y * Y + Z * Z;

    /// <summary>The Euclidean length.</summary>
    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>
    ///     True when every component is a finite number. The camera pipeline treats a non-finite
    ///     vector as "refuse to move" rather than propagating NaN into the game's view matrix.
    /// </summary>
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    /// <summary>
    ///     The unit vector in the same direction, or <see cref="Zero"/> when the input has zero (or
    ///     non-finite) length. Returning zero rather than NaN is deliberate: a degenerate aim vector
    ///     — camera exactly on its target — must degrade to "no direction", never poison the pose.
    /// </summary>
    public Vec3 Normalized()
    {
        var lenSq = LengthSquared;
        if (!double.IsFinite(lenSq) || lenSq <= 0.0) return Zero;
        var inv = 1.0 / Math.Sqrt(lenSq);
        return new Vec3(X * inv, Y * inv, Z * inv);
    }

    /// <summary>Component-wise sum.</summary>
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Component-wise difference <c>a − b</c>.</summary>
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Negation.</summary>
    public static Vec3 operator -(Vec3 v) => new(-v.X, -v.Y, -v.Z);

    /// <summary>Scalar multiply.</summary>
    public static Vec3 operator *(Vec3 v, double k) => new(v.X * k, v.Y * k, v.Z * k);

    /// <summary>Scalar multiply (scalar on the left).</summary>
    public static Vec3 operator *(double k, Vec3 v) => new(v.X * k, v.Y * k, v.Z * k);

    /// <summary>Scalar divide. Division by zero yields infinities, as for any <c>double</c> divide.</summary>
    public static Vec3 operator /(Vec3 v, double k) => new(v.X / k, v.Y / k, v.Z / k);

    /// <summary>The dot product <c>a · b</c>.</summary>
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>The cross product <c>a × b</c>.</summary>
    public static Vec3 Cross(Vec3 a, Vec3 b)
        => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

    /// <summary>
    ///     Linear interpolation from <paramref name="a"/> to <paramref name="b"/>.
    ///     <paramref name="t"/> is clamped to <c>[0,1]</c> (the evaluator only ever samples inside a
    ///     key interval), and the endpoints are returned <i>exactly</i> — <c>a + (b−a)·1</c> is not
    ///     bit-identical to <c>b</c> in floating point, and the camera must land exactly on its keys.
    /// </summary>
    public static Vec3 Lerp(Vec3 a, Vec3 b, double t)
    {
        if (double.IsNaN(t)) return a;
        if (t <= 0.0) return a;
        if (t >= 1.0) return b;
        return new Vec3(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t);
    }

    /// <summary>Adopts a snapshot vector (the <c>/sim</c> DTO) as camera math.</summary>
    public static Vec3 From(double3Snap v) => new(v.X, v.Y, v.Z);

    /// <summary>Projects back to the snapshot DTO for publication over <c>/sim</c>.</summary>
    public double3Snap ToSnapshot() => new(X, Y, Z);

    /// <summary>Explicit conversion from the snapshot DTO — see <see cref="From(double3Snap)"/>.</summary>
    public static explicit operator Vec3(double3Snap v) => From(v);

    /// <summary>Explicit conversion to the snapshot DTO — see <see cref="ToSnapshot"/>.</summary>
    public static explicit operator double3Snap(Vec3 v) => v.ToSnapshot();
}

/// <summary>
///     A double-precision quaternion, used for camera orientation keys, slerp/squad interpolation and
///     the rotation half of <see cref="PoseSmoother"/>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Composition convention (binding for this folder):</b> <c>q1 * q2</c> is the Hamilton
///         product and means <i>"apply <c>q2</c> first, then <c>q1</c>"</i> — the same order as matrix
///         composition, and consistent with <c>Rotate(v) = q·v·q⁻¹</c>:
///         <c>(q1·q2)·v·(q1·q2)⁻¹ = q1·(q2·v·q2⁻¹)·q1⁻¹</c>. Every method here obeys it.
///     </para>
///     <para>
///         Double precision, like <see cref="Vec3"/> — not for range (a rotation is bounded) but so a
///         long chain of squad evaluations and repeated renormalisations does not accumulate visible
///         drift over a multi-minute shot.
///     </para>
/// </remarks>
/// <param name="X">The i component of the vector part.</param>
/// <param name="Y">The j component of the vector part.</param>
/// <param name="Z">The k component of the vector part.</param>
/// <param name="W">The scalar (real) part.</param>
public readonly record struct Quat(double X, double Y, double Z, double W)
{
    /// <summary>The identity rotation.</summary>
    public static Quat Identity => new(0.0, 0.0, 0.0, 1.0);

    /// <summary>The squared norm.</summary>
    public double LengthSquared => X * X + Y * Y + Z * Z + W * W;

    /// <summary>The norm. Unit for any valid rotation.</summary>
    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>True when every component is a finite number.</summary>
    public bool IsFinite
        => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) && double.IsFinite(W);

    /// <summary>The vector (imaginary) part as a <see cref="Vec3"/>.</summary>
    public Vec3 Vector => new(X, Y, Z);

    /// <summary>
    ///     The unit quaternion in the same direction, or <see cref="Identity"/> when the input is
    ///     degenerate (zero or non-finite). Identity — not NaN — is the safe degenerate rotation.
    /// </summary>
    public Quat Normalized()
    {
        var lenSq = LengthSquared;
        if (!double.IsFinite(lenSq) || lenSq <= 0.0) return Identity;
        var inv = 1.0 / Math.Sqrt(lenSq);
        return new Quat(X * inv, Y * inv, Z * inv, W * inv);
    }

    /// <summary>The conjugate — the inverse rotation <i>for a unit quaternion</i>.</summary>
    public Quat Conjugate() => new(-X, -Y, -Z, W);

    /// <summary>
    ///     The true multiplicative inverse (conjugate ÷ squared norm), so it is correct for
    ///     non-unit input too. Degenerate input yields <see cref="Identity"/>.
    /// </summary>
    public Quat Inverse()
    {
        var lenSq = LengthSquared;
        if (!double.IsFinite(lenSq) || lenSq <= 0.0) return Identity;
        var inv = 1.0 / lenSq;
        return new Quat(-X * inv, -Y * inv, -Z * inv, W * inv);
    }

    /// <summary>
    ///     The Hamilton product. <c>a * b</c> applies <paramref name="b"/> first, then
    ///     <paramref name="a"/> (see the type remarks).
    /// </summary>
    public static Quat operator *(Quat a, Quat b)
        => new(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>
    ///     Component-wise negation. <c>−q</c> is the <i>same</i> rotation as <c>q</c> (the double
    ///     cover); negating is how the interpolators pick the short way round.
    /// </summary>
    public static Quat operator -(Quat q) => new(-q.X, -q.Y, -q.Z, -q.W);

    /// <summary>
    ///     The 4-D dot product. Its sign tells you which hemisphere two rotations are in — the whole
    ///     basis of short-path slerp.
    /// </summary>
    public static double Dot(Quat a, Quat b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

    /// <summary>
    ///     The rotation of <paramref name="radians"/> about <paramref name="axis"/>, right-handed.
    ///     A zero-length axis or a non-finite angle yields <see cref="Identity"/> rather than NaN.
    /// </summary>
    public static Quat FromAxisAngle(Vec3 axis, double radians)
    {
        if (!double.IsFinite(radians)) return Identity;
        var n = axis.Normalized();
        if (n == Vec3.Zero) return Identity;
        var half = radians * 0.5;
        var s = Math.Sin(half);
        return new Quat(n.X * s, n.Y * s, n.Z * s, Math.Cos(half));
    }

    /// <summary>
    ///     Rotates <paramref name="v"/> by this rotation (<c>q·v·q⁻¹</c>).
    /// </summary>
    /// <remarks>
    ///     Uses the standard <c>t = 2·(q_xyz × v); v' = v + q_w·t + q_xyz × t</c> form, which assumes
    ///     a unit quaternion — so a quaternion that has drifted off the unit sphere is renormalised
    ///     first. The near-unit fast path skips the sqrt for the overwhelmingly common case.
    /// </remarks>
    public Vec3 Rotate(Vec3 v)
    {
        var lenSq = LengthSquared;
        var q = lenSq is > 0.999999 and < 1.000001 ? this : Normalized();
        var u = q.Vector;
        var t = Vec3.Cross(u, v) * 2.0;
        return v + t * q.W + Vec3.Cross(u, t);
    }

    /// <summary>Adopts a snapshot quaternion (the <c>/sim</c> DTO) as camera math.</summary>
    public static Quat From(QuatSnap q) => new(q.X, q.Y, q.Z, q.W);

    /// <summary>Projects back to the snapshot DTO for publication over <c>/sim</c>.</summary>
    public QuatSnap ToSnapshot() => new(X, Y, Z, W);

    /// <summary>Explicit conversion from the snapshot DTO — see <see cref="From(QuatSnap)"/>.</summary>
    public static explicit operator Quat(QuatSnap q) => From(q);

    /// <summary>Explicit conversion to the snapshot DTO — see <see cref="ToSnapshot"/>.</summary>
    public static explicit operator QuatSnap(Quat q) => q.ToSnapshot();
}
