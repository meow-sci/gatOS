namespace gatOS.SimFs.Camera;

/// <summary>
///     The shape of an easing curve. The four named kinds are the <c>unscience</c> animation model
///     (<c>ksa-abstractions.lib/EasingHelper.cs</c>) carried over verbatim, because the camera track
///     format spells them exactly this way (<c>"linear" | "in" | "out" | "in-out"</c>);
///     <see cref="Bezier"/> is the extension that accepts explicit cubic handles.
/// </summary>
public enum EaseKind
{
    /// <summary>No easing — progress is the parameter itself.</summary>
    Linear = 0,

    /// <summary>Slow start, fast finish: <c>t^powerStart</c>.</summary>
    In = 1,

    /// <summary>Fast start, slow finish: <c>1 − (1−t)^powerEnd</c>.</summary>
    Out = 2,

    /// <summary>Ease in over the first half, ease out over the second.</summary>
    InOut = 3,

    /// <summary>
    ///     A CSS-style cubic Bézier defined by two handles — the form that can express anticipation
    ///     and overshoot, which no power curve can.
    /// </summary>
    Bezier = 4,
}

/// <summary>
///     An easing curve: which <see cref="EaseKind"/>, plus the parameters that kind needs. One value
///     type covers both the named/power family and the cubic-Bézier family so a track key can carry
///     "an ease" without a discriminated union.
/// </summary>
/// <remarks>
///     <para>
///         Construct through <see cref="Linear"/>, <see cref="Named"/> or <see cref="Cubic"/> — those
///         sanitise their inputs. The <c>init</c> setters exist so a parser can build a spec with
///         <c>with</c>, and <see cref="Easing.Apply"/> re-validates defensively for exactly that
///         reason: nothing downstream may ever emit NaN into a camera pose.
///     </para>
///     <para>
///         It is a <c>readonly record struct</c>, so two specs parsed from identical JSON compare
///         equal — which is what lets the evaluator's determinism tests assert on whole keys.
///     </para>
/// </remarks>
public readonly record struct EaseSpec
{
    /// <summary>The smallest accepted easing power. Below this the curve is a step in all but name.</summary>
    public const double MinPower = 0.01;

    /// <summary>The largest accepted easing power. Above this the curve is a step in all but name.</summary>
    public const double MaxPower = 16.0;

    /// <summary>The curve family.</summary>
    public EaseKind Kind { get; init; }

    /// <summary>
    ///     The exponent of the ease-<i>in</i> half (used by <see cref="EaseKind.In"/> and the first
    ///     half of <see cref="EaseKind.InOut"/>). Clamped to <c>[<see cref="MinPower"/>,
    ///     <see cref="MaxPower"/>]</c> at use.
    /// </summary>
    public double PowerStart { get; init; }

    /// <summary>
    ///     The exponent of the ease-<i>out</i> half (used by <see cref="EaseKind.Out"/> and the second
    ///     half of <see cref="EaseKind.InOut"/>).
    /// </summary>
    public double PowerEnd { get; init; }

    /// <summary>First handle's x, in <c>[0,1]</c> (Bézier only).</summary>
    public double X1 { get; init; }

    /// <summary>First handle's y — may leave <c>[0,1]</c>, which is what produces anticipation.</summary>
    public double Y1 { get; init; }

    /// <summary>Second handle's x, in <c>[0,1]</c> (Bézier only).</summary>
    public double X2 { get; init; }

    /// <summary>Second handle's y — may leave <c>[0,1]</c>, which is what produces overshoot.</summary>
    public double Y2 { get; init; }

    /// <summary>
    ///     The identity curve. Also what <c>default(EaseSpec)</c> behaves as, so an unset ease on a
    ///     track key is linear rather than undefined.
    /// </summary>
    public static EaseSpec Linear => new()
    {
        Kind = EaseKind.Linear, PowerStart = 1.0, PowerEnd = 1.0,
        X1 = 0.0, Y1 = 0.0, X2 = 1.0, Y2 = 1.0,
    };

    /// <summary>
    ///     A named power curve. The two powers are independent so a shot can, say, leave slowly and
    ///     arrive very slowly (<c>in-out</c> with <c>2</c> / <c>5</c>). Powers are clamped to
    ///     <c>[<see cref="MinPower"/>, <see cref="MaxPower"/>]</c>; a non-finite power falls back to
    ///     the 3.0 default rather than being propagated.
    /// </summary>
    /// <param name="kind">The curve family. <see cref="EaseKind.Bezier"/> here degrades to the CSS
    ///     <c>ease</c> handles, since no handles were supplied — use <see cref="Cubic"/> for that.</param>
    /// <param name="powerStart">The ease-in exponent (default 3, the <c>unscience</c> default).</param>
    /// <param name="powerEnd">The ease-out exponent (default 3).</param>
    public static EaseSpec Named(EaseKind kind, double powerStart = 3.0, double powerEnd = 3.0)
    {
        if (kind == EaseKind.Bezier) return Cubic(0.25, 0.1, 0.25, 1.0);
        return new EaseSpec
        {
            Kind = kind,
            PowerStart = SanitizePower(powerStart),
            PowerEnd = SanitizePower(powerEnd),
            X1 = 0.0, Y1 = 0.0, X2 = 1.0, Y2 = 1.0,
        };
    }

    /// <summary>
    ///     A cubic-Bézier ease with control points <c>(0,0), (x1,y1), (x2,y2), (1,1)</c> — the CSS
    ///     <c>cubic-bezier()</c> convention, which is what the track format's four-number
    ///     <c>"ease": [x1,y1,x2,y2]</c> array means.
    /// </summary>
    /// <remarks>
    ///     The <b>x</b> handles are clamped to <c>[0,1]</c>: outside that the curve is not a function
    ///     of progress and the solver has no single answer. The <b>y</b> handles are left free —
    ///     <c>y &lt; 0</c> gives anticipation (pull back before moving), <c>y &gt; 1</c> gives
    ///     overshoot (fly past and settle). Any non-finite component collapses the whole spec to
    ///     <see cref="Linear"/>; silently shooting a NaN curve is not an option.
    /// </remarks>
    /// <param name="x1">First handle x, clamped to <c>[0,1]</c>.</param>
    /// <param name="y1">First handle y, unclamped.</param>
    /// <param name="x2">Second handle x, clamped to <c>[0,1]</c>.</param>
    /// <param name="y2">Second handle y, unclamped.</param>
    public static EaseSpec Cubic(double x1, double y1, double x2, double y2)
    {
        if (!double.IsFinite(x1) || !double.IsFinite(y1) ||
            !double.IsFinite(x2) || !double.IsFinite(y2))
        {
            return Linear;
        }

        return new EaseSpec
        {
            Kind = EaseKind.Bezier,
            PowerStart = 3.0, PowerEnd = 3.0,
            X1 = Math.Clamp(x1, 0.0, 1.0), Y1 = y1,
            X2 = Math.Clamp(x2, 0.0, 1.0), Y2 = y2,
        };
    }

    /// <summary>
    ///     Parses the track format's named ease spellings — <c>"linear"</c>, <c>"in"</c>,
    ///     <c>"out"</c>, <c>"in-out"</c> — case-insensitively, with surrounding whitespace ignored.
    /// </summary>
    /// <remarks>
    ///     Names only. The Bézier form arrives as a four-number JSON array, not a token, so the
    ///     parser calls <see cref="Cubic"/> directly for it — there is no token spelling to accept
    ///     here. The resulting spec carries the default powers; a track key's <c>ease_power</c>
    ///     is applied afterwards with <see cref="Named"/>.
    /// </remarks>
    /// <param name="token">The JSON token to parse.</param>
    /// <param name="spec">The parsed spec, or <see cref="Linear"/> when the token is unrecognised.</param>
    /// <returns>True when <paramref name="token"/> named a known ease.</returns>
    public static bool TryParse(string? token, out EaseSpec spec)
    {
        spec = Linear;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var t = token.Trim();
        if (t.Equals("linear", StringComparison.OrdinalIgnoreCase)) { spec = Linear; return true; }
        if (t.Equals("in", StringComparison.OrdinalIgnoreCase)) { spec = Named(EaseKind.In); return true; }
        if (t.Equals("out", StringComparison.OrdinalIgnoreCase)) { spec = Named(EaseKind.Out); return true; }
        if (t.Equals("in-out", StringComparison.OrdinalIgnoreCase)) { spec = Named(EaseKind.InOut); return true; }
        return false;
    }

    private static double SanitizePower(double p)
        => double.IsFinite(p) ? Math.Clamp(p, MinPower, MaxPower) : 3.0;
}

/// <summary>
///     Evaluates an <see cref="EaseSpec"/>: maps linear progress <c>t ∈ [0,1]</c> to eased progress.
///     Pure, deterministic, allocation-free — the same inputs always produce bit-identical output,
///     which the camera track evaluator relies on (CAMERA_CONTROLS_PLAN §9).
/// </summary>
public static class Easing
{
    // Newton–Raphson: 8 passes is enough for double precision on a well-conditioned cubic, and the
    // bisection fallback covers the rest — so the total work is bounded and identical every call.
    private const int NewtonIterations = 8;
    private const int BisectionIterations = 24;
    private const double SolveEpsilon = 1e-12;
    private const double DerivativeEpsilon = 1e-9;

    /// <summary>
    ///     Applies <paramref name="spec"/> to linear progress <paramref name="t"/> and returns the
    ///     eased progress.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The exact-endpoint snap is load-bearing, not cosmetic.</b> <c>t ≤ 0</c> returns
    ///         exactly <c>0.0</c> and <c>t ≥ 1</c> exactly <c>1.0</c>, short-circuited before any
    ///         <see cref="Math.Pow(double,double)"/> or Bézier solve. Without it the terminal frame of
    ///         a shot evaluates to <c>0.99999999…</c> — <c>Math.Pow</c> and an iterative root solve are
    ///         not obliged to round to 1 — and a 360° orbit lands a fraction of a degree short of where
    ///         it started, so a looping shot visibly ratchets. This is the drift diagnosed in
    ///         <c>unscience/plans/done/TIMING_ANALYSIS_AND_FIX.md</c>; keep the short-circuits.
    ///     </para>
    ///     <para>
    ///         <paramref name="t"/> is <i>clamped</i>, never extrapolated: the evaluator may hand over
    ///         a slightly out-of-range value when a frame straddles a key boundary, and an
    ///         extrapolated ease would fling the camera. NaN degrades to <c>0.0</c>.
    ///     </para>
    ///     <para>
    ///         A spec with a non-finite power (only reachable by hand-building one with <c>with</c>)
    ///         degrades to <see cref="EaseKind.Linear"/>. Nothing here can return NaN.
    ///     </para>
    /// </remarks>
    /// <param name="t">Linear progress. Clamped to <c>[0,1]</c>.</param>
    /// <param name="spec">The curve to apply.</param>
    /// <returns>
    ///     Eased progress. Inside <c>[0,1]</c> for the named kinds; a Bézier with out-of-range y
    ///     handles may deliberately leave it (anticipation/overshoot).
    /// </returns>
    public static double Apply(double t, in EaseSpec spec)
    {
        // Endpoint snap + clamp, before anything that could round. See the remarks.
        if (double.IsNaN(t)) return 0.0;
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;

        switch (spec.Kind)
        {
            case EaseKind.In:
            {
                if (!double.IsFinite(spec.PowerStart)) return t;
                return Math.Pow(t, Clamp(spec.PowerStart));
            }

            case EaseKind.Out:
            {
                if (!double.IsFinite(spec.PowerEnd)) return t;
                return 1.0 - Math.Pow(1.0 - t, Clamp(spec.PowerEnd));
            }

            case EaseKind.InOut:
            {
                if (!double.IsFinite(spec.PowerStart) || !double.IsFinite(spec.PowerEnd)) return t;
                // The unscience formula, unchanged. NOTE: when PowerStart != PowerEnd this is only
                // C⁰ at t = 0.5 — the two halves meet at 0.5 but their slopes do not. That is
                // intentional (it is what "leave fast, arrive slowly" means); if a shot needs C¹
                // there, author a Bézier instead.
                return t < 0.5
                    ? Math.Pow(2.0 * t, Clamp(spec.PowerStart)) / 2.0
                    : 1.0 - Math.Pow(2.0 * (1.0 - t), Clamp(spec.PowerEnd)) / 2.0;
            }

            case EaseKind.Bezier:
                return Bezier(t, spec);

            case EaseKind.Linear:
            default:
                return t;
        }
    }

    private static double Clamp(double power) => Math.Clamp(power, EaseSpec.MinPower, EaseSpec.MaxPower);

    /// <summary>
    ///     Solves the CSS-style cubic-Bézier ease. The curve is the parametric
    ///     <c>(x(u), y(u))</c> through <c>(0,0), (X1,Y1), (X2,Y2), (1,1)</c>; <paramref name="t"/> is
    ///     an <b>x</b> value, so we invert <c>x(u) = t</c> for <c>u</c> and return <c>y(u)</c>.
    /// </summary>
    private static double Bezier(double t, in EaseSpec spec)
    {
        var x1 = double.IsFinite(spec.X1) ? Math.Clamp(spec.X1, 0.0, 1.0) : 0.0;
        var x2 = double.IsFinite(spec.X2) ? Math.Clamp(spec.X2, 0.0, 1.0) : 1.0;
        var y1 = double.IsFinite(spec.Y1) ? spec.Y1 : 0.0;
        var y2 = double.IsFinite(spec.Y2) ? spec.Y2 : 1.0;

        // Polynomial (Bernstein-expanded) coefficients: p(u) = ((a·u + b)·u + c)·u.
        var cx = 3.0 * x1;
        var bx = 3.0 * (x2 - x1) - cx;
        var ax = 1.0 - cx - bx;

        var cy = 3.0 * y1;
        var by = 3.0 * (y2 - y1) - cy;
        var ay = 1.0 - cy - by;

        var u = SolveForX(t, ax, bx, cx);
        return ((ay * u + by) * u + cy) * u;
    }

    private static double SolveForX(double t, double a, double b, double c)
    {
        // Newton–Raphson seeded at u = t. For the usual monotone handles this converges in 2–4
        // passes; the seed is exact for the linear handle set.
        var u = t;
        for (var i = 0; i < NewtonIterations; i++)
        {
            var x = ((a * u + b) * u + c) * u - t;
            if (Math.Abs(x) < SolveEpsilon) return u;

            var d = (3.0 * a * u + 2.0 * b) * u + c;
            if (!double.IsFinite(d) || Math.Abs(d) < DerivativeEpsilon) break;

            var next = u - x / d;
            if (!double.IsFinite(next)) break;
            u = next;
        }

        // Bisection fallback — used when a handle flattens the curve (near-zero derivative) or
        // Newton wanders out of the unit interval. x(u) is monotone on [0,1] because the x handles
        // are clamped there, so bisection is guaranteed to bracket the root.
        double lo = 0.0, hi = 1.0;
        u = Math.Clamp(t, 0.0, 1.0);
        for (var i = 0; i < BisectionIterations; i++)
        {
            var x = ((a * u + b) * u + c) * u;
            if (x > t) hi = u; else lo = u;
            u = (lo + hi) * 0.5;
        }

        return u;
    }
}
