namespace gatOS.SimFs.Camera;

/// <summary>
///     The interpolation primitives the camera track evaluator is built from: centripetal
///     Catmull-Rom and cubic Bézier for position, slerp and squad for orientation.
/// </summary>
/// <remarks>
///     <para>
///         Everything here is pure, deterministic and allocation-free — no time source, no
///         <c>Random</c>, no state — so the same track sampled at the same <c>t</c> always produces
///         bit-identical output (CAMERA_CONTROLS_PLAN §9). It is game-free by construction and lives
///         beside <c>Iva/CabinPhysics.cs</c> for the same reason: the maths is unit-testable on a bare
///         host, and the KSA half only has to <i>apply</i> the result.
///     </para>
///     <para>
///         All of it is double precision — see <see cref="Vec3"/>'s remarks for why <c>float</c>
///         cannot express a camera position at solar-system scale.
///     </para>
/// </remarks>
public static class Splines
{
    /// <summary>
    ///     Below this a knot interval counts as degenerate and the pyramidal evaluation would divide
    ///     by ~zero. Knot spacing is <c>|Δp|^alpha</c>, so at solar-system scale it is ~10⁵ and at
    ///     cabin scale ~10⁻³; 10⁻¹² is far below any spacing a real track produces and only fires on
    ///     genuinely coincident keys.
    /// </summary>
    private const double KnotEpsilon = 1e-12;

    /// <summary>Above this |dot| two quaternions are parallel enough that slerp's sin(θ) is unusable.</summary>
    private const double SlerpLinearThreshold = 0.9995;

    /// <summary>Below this a quaternion's vector part is treated as zero by <c>Log</c>/<c>Exp</c>.</summary>
    private const double LogEpsilon = 1e-12;

    /// <summary>
    ///     Evaluates a Catmull-Rom spline segment between <paramref name="p1"/> and
    ///     <paramref name="p2"/>, using <paramref name="p0"/> and <paramref name="p3"/> as the
    ///     neighbouring keys that set the tangents.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Centripetal by default (<c>alpha = 0.5</c>), and that is a requirement, not a
    ///         preference.</b> The uniform parameterisation (<c>alpha = 0</c>) ignores how far apart
    ///         the keys are, so a track whose keys are unevenly spaced in space — which every hand-authored
    ///         camera move is — produces tangents scaled to the wrong distance: the curve cusps at
    ///         tight key pairs and swings wildly outside the key hull between distant ones. The camera
    ///         visibly lurches. The centripetal parameterisation is provably free of cusps and
    ///         self-intersections and never leaves the local hull, which is exactly the guarantee a
    ///         director needs. Pass <c>alpha = 0</c> only to reproduce the uniform behaviour (the unit
    ///         tests use it to prove the centripetal property is real).
    ///     </para>
    ///     <para>
    ///         Implemented as the Barry–Goldman pyramid over the knot sequence
    ///         <c>t₀ = 0, tᵢ₊₁ = tᵢ + |pᵢ₊₁ − pᵢ|^alpha</c>. When any knot interval collapses —
    ///         duplicated keys, which a parser must tolerate rather than reject — the pyramid would
    ///         divide by zero, so the segment degrades to a straight
    ///         <see cref="Vec3.Lerp"/> from <paramref name="p1"/> to <paramref name="p2"/>.
    ///     </para>
    /// </remarks>
    /// <param name="p0">The key before the segment (tangent support only).</param>
    /// <param name="p1">The segment start — returned exactly at <c>t = 0</c>.</param>
    /// <param name="p2">The segment end — returned exactly at <c>t = 1</c>.</param>
    /// <param name="p3">The key after the segment (tangent support only).</param>
    /// <param name="t">Progress along the segment, clamped to <c>[0,1]</c>.</param>
    /// <param name="alpha">
    ///     The knot parameterisation exponent: <c>0.5</c> centripetal (default), <c>0</c> uniform,
    ///     <c>1</c> chordal.
    /// </param>
    /// <returns>The interpolated point. Never NaN for finite inputs.</returns>
    public static Vec3 CatmullRom(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, double t, double alpha = 0.5)
    {
        // Endpoint snap, for the same reason Easing.Apply snaps: the camera must land exactly on its
        // keys, and the pyramidal form does not round to them.
        if (double.IsNaN(t)) return p1;
        if (t <= 0.0) return p1;
        if (t >= 1.0) return p2;
        if (!p0.IsFinite || !p1.IsFinite || !p2.IsFinite || !p3.IsFinite) return Vec3.Lerp(p1, p2, t);

        var a = double.IsFinite(alpha) ? Math.Clamp(alpha, 0.0, 1.0) : 0.5;

        var t0 = 0.0;
        var t1 = t0 + Knot(p1 - p0, a);
        var t2 = t1 + Knot(p2 - p1, a);
        var t3 = t2 + Knot(p3 - p2, a);

        var d01 = t1 - t0;
        var d12 = t2 - t1;
        var d23 = t3 - t2;
        var d02 = t2 - t0;
        var d13 = t3 - t1;

        if (d01 < KnotEpsilon || d12 < KnotEpsilon || d23 < KnotEpsilon ||
            d02 < KnotEpsilon || d13 < KnotEpsilon)
        {
            return Vec3.Lerp(p1, p2, t);
        }

        var tt = t1 + d12 * t;

        var a1 = p0 * ((t1 - tt) / d01) + p1 * ((tt - t0) / d01);
        var a2 = p1 * ((t2 - tt) / d12) + p2 * ((tt - t1) / d12);
        var a3 = p2 * ((t3 - tt) / d23) + p3 * ((tt - t2) / d23);

        var b1 = a1 * ((t2 - tt) / d02) + a2 * ((tt - t0) / d02);
        var b2 = a2 * ((t3 - tt) / d13) + a3 * ((tt - t1) / d13);

        var c = b1 * ((t2 - tt) / d12) + b2 * ((tt - t1) / d12);
        return c.IsFinite ? c : Vec3.Lerp(p1, p2, t);
    }

    private static double Knot(Vec3 delta, double alpha)
    {
        var len = delta.Length;
        if (!double.IsFinite(len)) return 0.0;
        // Math.Pow(0, 0) == 1, so alpha = 0 yields the uniform knot sequence even for coincident
        // keys — which is precisely why the uniform form never trips the degeneracy guard and
        // happily overshoots instead.
        var k = Math.Pow(len, alpha);
        return double.IsFinite(k) ? k : 0.0;
    }

    /// <summary>
    ///     A cubic Bézier in space: <c>p0 → p1</c> shaped by the two control points
    ///     <paramref name="c0"/> and <paramref name="c1"/>. De Casteljau's algorithm, which is the
    ///     numerically stable evaluation.
    /// </summary>
    /// <param name="p0">The start point — returned exactly at <c>t = 0</c>.</param>
    /// <param name="c0">The control point pulling out of <paramref name="p0"/>.</param>
    /// <param name="c1">The control point pulling into <paramref name="p1"/>.</param>
    /// <param name="p1">The end point — returned exactly at <c>t = 1</c>.</param>
    /// <param name="t">Progress, clamped to <c>[0,1]</c>.</param>
    public static Vec3 Bezier(Vec3 p0, Vec3 c0, Vec3 c1, Vec3 p1, double t)
    {
        if (double.IsNaN(t)) return p0;
        if (t <= 0.0) return p0;
        if (t >= 1.0) return p1;

        var a0 = Vec3.Lerp(p0, c0, t);
        var a1 = Vec3.Lerp(c0, c1, t);
        var a2 = Vec3.Lerp(c1, p1, t);

        var b0 = Vec3.Lerp(a0, a1, t);
        var b1 = Vec3.Lerp(a1, a2, t);

        return Vec3.Lerp(b0, b1, t);
    }

    /// <summary>
    ///     Spherical linear interpolation between two rotations — constant angular velocity along the
    ///     shortest arc.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Inputs are normalised first (a quaternion that has drifted off the unit sphere would
    ///         otherwise scale the result), and <paramref name="b"/> is negated when
    ///         <c>Dot(a,b) &lt; 0</c>. That negation is what makes the camera take the short way round:
    ///         <c>−q</c> is the same rotation as <c>q</c>, but naively interpolating to the far
    ///         representative would spin the camera the long way — 350° instead of 10°.
    ///     </para>
    ///     <para>
    ///         When the two rotations are nearly parallel (<c>|dot| &gt; 0.9995</c>) <c>sin θ</c>
    ///         approaches zero and the slerp weights lose all precision, so it falls back to a
    ///         normalised lerp — which differs from slerp by less than the difference is measurable at
    ///         that separation.
    ///     </para>
    ///     <para>Output is always unit length; the endpoints are the normalised inputs, exactly.</para>
    /// </remarks>
    /// <param name="a">The start rotation — returned (normalised) at <c>t = 0</c>.</param>
    /// <param name="b">The end rotation — returned (normalised, short-path) at <c>t = 1</c>.</param>
    /// <param name="t">Progress, clamped to <c>[0,1]</c>.</param>
    public static Quat Slerp(Quat a, Quat b, double t)
    {
        var qa = a.Normalized();
        var qb = b.Normalized();

        if (double.IsNaN(t)) return qa;
        if (t <= 0.0) return qa;

        var dot = Quat.Dot(qa, qb);
        if (dot < 0.0)
        {
            qb = -qb;
            dot = -dot;
        }

        if (t >= 1.0) return qb;

        if (dot > SlerpLinearThreshold)
        {
            // Nearly parallel: lerp + renormalise. Also the only sane answer for a == b.
            var lerped = new Quat(
                qa.X + (qb.X - qa.X) * t,
                qa.Y + (qb.Y - qa.Y) * t,
                qa.Z + (qb.Z - qa.Z) * t,
                qa.W + (qb.W - qa.W) * t);
            return lerped.Normalized();
        }

        // dot is in [-1, 1] by construction, but clamp anyway: Acos(1 + 1e-16) is NaN.
        var theta = Math.Acos(Math.Clamp(dot, -1.0, 1.0));
        var sinTheta = Math.Sin(theta);
        if (Math.Abs(sinTheta) < LogEpsilon) return qa;

        var wa = Math.Sin((1.0 - t) * theta) / sinTheta;
        var wb = Math.Sin(t * theta) / sinTheta;

        return new Quat(
            qa.X * wa + qb.X * wb,
            qa.Y * wa + qb.Y * wb,
            qa.Z * wa + qb.Z * wb,
            qa.W * wa + qb.W * wb).Normalized();
    }

    /// <summary>
    ///     Spherical <i>cubic</i> interpolation (Shoemake's squad):
    ///     <c>Slerp(Slerp(q0,q1,t), Slerp(a,b,t), 2t(1−t))</c>.
    /// </summary>
    /// <remarks>
    ///     Slerp between orientation keys is only C⁰ — the angular velocity jumps at every key, which
    ///     reads on screen as a flick at each waypoint. Squad is C¹ across keys when the control
    ///     quaternions come from <see cref="SquadIntermediate"/>, so a multi-key orientation track
    ///     flows. The endpoints are exact: <c>t = 0</c> gives <paramref name="q0"/> and <c>t = 1</c>
    ///     gives <paramref name="q1"/> (both normalised), because the blend weight <c>2t(1−t)</c> is
    ///     zero at both ends.
    /// </remarks>
    /// <param name="q0">The segment start key.</param>
    /// <param name="a">The control quaternion just after <paramref name="q0"/>.</param>
    /// <param name="b">The control quaternion just before <paramref name="q1"/>.</param>
    /// <param name="q1">The segment end key.</param>
    /// <param name="t">Progress, clamped to <c>[0,1]</c>.</param>
    public static Quat Squad(Quat q0, Quat a, Quat b, Quat q1, double t)
    {
        if (double.IsNaN(t)) return q0.Normalized();
        if (t <= 0.0) return q0.Normalized();
        if (t >= 1.0) return q1.Normalized();

        var c = Slerp(q0, q1, t);
        var d = Slerp(a, b, t);
        return Slerp(c, d, 2.0 * t * (1.0 - t));
    }

    /// <summary>
    ///     Builds the squad control quaternion for key <paramref name="q"/> from its neighbours:
    ///     <c>q · exp(−(log(q⁻¹·qNext) + log(q⁻¹·qPrev)) / 4)</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is what makes squad C¹: the control point encodes the average of the incoming and
    ///         outgoing arcs, so the tangent matches on both sides of the key. For a segment
    ///         <c>kᵢ → kᵢ₊₁</c> pass <c>a = SquadIntermediate(kᵢ₋₁, kᵢ, kᵢ₊₁)</c> and
    ///         <c>b = SquadIntermediate(kᵢ, kᵢ₊₁, kᵢ₊₂)</c>; at the ends of a track, repeat the
    ///         terminal key as its own missing neighbour.
    ///     </para>
    ///     <para>
    ///         Each relative rotation is forced into the positive-<c>W</c> hemisphere before its
    ///         logarithm, so the control point describes the short arc — the same double-cover trap
    ///         <see cref="Slerp"/> handles.
    ///     </para>
    /// </remarks>
    /// <param name="qPrev">The key before <paramref name="q"/>.</param>
    /// <param name="q">The key the control quaternion belongs to.</param>
    /// <param name="qNext">The key after <paramref name="q"/>.</param>
    public static Quat SquadIntermediate(Quat qPrev, Quat q, Quat qNext)
    {
        var qq = q.Normalized();
        var inv = qq.Conjugate(); // unit ⇒ conjugate is the inverse
        var toNext = ShortPath(inv * qNext.Normalized());
        var toPrev = ShortPath(inv * qPrev.Normalized());

        var logNext = Log(toNext);
        var logPrev = Log(toPrev);
        var sum = new Quat(
            (logNext.X + logPrev.X) * -0.25,
            (logNext.Y + logPrev.Y) * -0.25,
            (logNext.Z + logPrev.Z) * -0.25,
            0.0);

        return (qq * Exp(sum)).Normalized();
    }

    /// <summary>Linear interpolation between two vectors; <paramref name="t"/> clamped, endpoints exact.</summary>
    /// <param name="a">The start point.</param>
    /// <param name="b">The end point.</param>
    /// <param name="t">Progress, clamped to <c>[0,1]</c>.</param>
    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => Vec3.Lerp(a, b, t);

    /// <summary>Linear interpolation between two scalars; <paramref name="t"/> clamped, endpoints exact.</summary>
    /// <param name="a">The start value.</param>
    /// <param name="b">The end value.</param>
    /// <param name="t">Progress, clamped to <c>[0,1]</c>.</param>
    public static double Lerp(double a, double b, double t)
    {
        if (double.IsNaN(t)) return a;
        if (t <= 0.0) return a;
        if (t >= 1.0) return b;
        return a + (b - a) * t;
    }

    /// <summary>Picks the representative of a rotation that lies in the positive-<c>W</c> hemisphere.</summary>
    private static Quat ShortPath(Quat q) => q.W < 0.0 ? -q : q;

    /// <summary>
    ///     The quaternion logarithm of a unit quaternion: a pure quaternion (<c>W = 0</c>) whose
    ///     vector part is the rotation's axis scaled by half its angle. A near-identity input logs to
    ///     ~zero rather than NaN — the <c>θ / sin θ</c> factor is indeterminate at zero.
    /// </summary>
    private static Quat Log(Quat q)
    {
        var v = q.Vector;
        var s = v.Length;
        if (!double.IsFinite(s) || s < LogEpsilon) return new Quat(0.0, 0.0, 0.0, 0.0);

        var theta = Math.Atan2(s, q.W);
        var f = theta / s;
        return new Quat(v.X * f, v.Y * f, v.Z * f, 0.0);
    }

    /// <summary>
    ///     The quaternion exponential of a pure quaternion — the inverse of <see cref="Log"/>. A
    ///     zero input exponentiates to the identity rather than dividing by zero.
    /// </summary>
    private static Quat Exp(Quat q)
    {
        var v = q.Vector;
        var theta = v.Length;
        if (!double.IsFinite(theta) || theta < LogEpsilon) return Quat.Identity;

        var s = Math.Sin(theta) / theta;
        return new Quat(v.X * s, v.Y * s, v.Z * s, Math.Cos(theta));
    }
}
