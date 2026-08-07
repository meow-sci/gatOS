namespace gatOS.SimFs.Camera;

/// <summary>
///     A critically-damped spring that eases a camera pose toward a moving target — the thing that
///     turns a jumpy per-frame setpoint (a target vessel jittering under physics, a scrubbed track, a
///     hand-written program writing <c>pose/</c> at 5 Hz) into footage you can actually cut.
/// </summary>
/// <remarks>
///     <para>
///         The plan cites KSA's own <c>MathEx.SpringInterp</c> as the model, but nothing in
///         <c>gatOS.SimFs</c> may touch a game assembly, so this is the standard analytic
///         critically-damped step (the Game-Programming-Gems formulation Unity's <c>SmoothDamp</c>
///         also uses):
///         <code>
///         ω    = 2 / smoothTime
///         x    = ω·dt
///         exp  = 1 / (1 + x + 0.48x² + 0.235x³)      // a Padé-style approximation of e^(−x)
///         Δ    = current − target
///         temp = (v + ω·Δ)·dt
///         v    = (v − ω·temp)·exp
///         out  = target + (Δ + temp)·exp
///         </code>
///         <b>Critically damped</b> is the whole point: an under-damped spring rings (the camera
///         wobbles past the subject and back — instantly readable as "bad game camera"), an
///         over-damped one crawls. Critical damping is the fastest approach with <i>no</i> overshoot.
///     </para>
///     <para>
///         <b>Frame-rate independence.</b> <c>dt</c> enters through the exponential, not as a naive
///         <c>lerp(current, target, k)</c> — so the same <c>smoothTime</c> produces the same motion at
///         30 fps and at 240 fps. A naive lerp would make the camera lag differently on every machine,
///         which is unusable for recording.
///     </para>
///     <para>
///         The smoother is <b>stateful</b> (it carries velocity) and therefore not thread-safe: it is
///         owned and stepped by the single game-thread camera driver, exactly like the other
///         per-frame drivers. Every instance carries two independent velocities — one for position,
///         one for rotation — so one object smooths a whole pose.
///     </para>
/// </remarks>
public sealed class PoseSmoother
{
    /// <summary>
    ///     Below this separation (radians — 10⁻⁶ rad is 0.2 arcseconds, far below one pixel at any
    ///     sane FOV) the rotation counts as "already there" and the step snaps.
    ///     The guard is numerical, not cosmetic: the angle is recovered through
    ///     <c>2·acos(|dot|)</c>, which loses half its significant digits near <c>dot = 1</c>, so two
    ///     <i>bit-identical</i> rotations measure ~3 × 10⁻⁸ rad apart. Without a floor comfortably
    ///     above that noise the slerp fraction <c>1 − remaining/angle</c> becomes a ratio of two
    ///     pieces of rounding error, and the spring never reports rest.
    /// </summary>
    private const double AngleEpsilon = 1e-6;

    private Vec3 _positionVelocity;
    private double _angularVelocity;

    /// <summary>The current position-spring velocity, m/s. Exposed for diagnostics and tests.</summary>
    public Vec3 PositionVelocity => _positionVelocity;

    /// <summary>
    ///     The current rotation-spring velocity, rad/s — the rate of change of the <i>remaining</i>
    ///     angle, so it is negative while the camera is closing on its target. Exposed for
    ///     diagnostics and tests.
    /// </summary>
    public double AngularVelocity => _angularVelocity;

    /// <summary>
    ///     Clears both velocities. Call it whenever the pose teleports — a hard cut between shots, a
    ///     scrub, an ownership take — so the spring does not carry the old shot's momentum into the
    ///     new one and sail past the first frame of it.
    /// </summary>
    public void Reset()
    {
        _positionVelocity = Vec3.Zero;
        _angularVelocity = 0.0;
    }

    /// <summary>
    ///     Advances the position spring one frame and returns the smoothed position.
    /// </summary>
    /// <param name="current">Where the camera is now.</param>
    /// <param name="target">Where it is being asked to be.</param>
    /// <param name="smoothTimeSeconds">
    ///     Roughly how long the camera takes to reach the target. <c>0</c> (or negative) disables
    ///     smoothing entirely — the target passes through raw and the velocity is zeroed. That is what
    ///     writing <c>0</c> to <c>pose/smoothing</c> means, and it must be exact: a director asking for
    ///     no smoothing must get frame-exact placement, not "almost".
    /// </param>
    /// <param name="dtSeconds">The frame delta. Non-positive is treated as "no time passed" ⇒ raw target.</param>
    /// <returns>The smoothed position. Never overshoots <paramref name="target"/>; never NaN.</returns>
    public Vec3 Step(Vec3 current, Vec3 target, double smoothTimeSeconds, double dtSeconds)
    {
        if (!current.IsFinite || !target.IsFinite ||
            !double.IsFinite(smoothTimeSeconds) || !double.IsFinite(dtSeconds) ||
            smoothTimeSeconds <= 0.0 || dtSeconds <= 0.0)
        {
            _positionVelocity = Vec3.Zero;
            return target;
        }

        var omega = 2.0 / smoothTimeSeconds;
        var x = omega * dtSeconds;
        var exp = 1.0 / (1.0 + x + 0.48 * x * x + 0.235 * x * x * x);

        var change = current - target;
        var temp = (_positionVelocity + change * omega) * dtSeconds;
        var velocity = (_positionVelocity - temp * omega) * exp;
        var output = target + (change + temp) * exp;

        // Overshoot clamp. The analytic step cannot ring, but a very large dt relative to smoothTime
        // pushes the approximation past the target; when it does, snap and re-derive the velocity so
        // the next frame starts consistent.
        if (Vec3.Dot(target - current, output - target) > 0.0)
        {
            output = target;
            velocity = (output - current) / dtSeconds;
        }

        if (!output.IsFinite || !velocity.IsFinite)
        {
            _positionVelocity = Vec3.Zero;
            return target;
        }

        _positionVelocity = velocity;
        return output;
    }

    /// <summary>
    ///     Advances the rotation spring one frame and returns the smoothed rotation.
    /// </summary>
    /// <remarks>
    ///     <b>Design choice:</b> rather than smoothing in the quaternion tangent space (three coupled
    ///     springs plus a re-exponentiation, with its own drift and hemisphere bookkeeping), this runs
    ///     the <i>same</i> scalar spring on the <b>angle</b> between <paramref name="current"/> and
    ///     <paramref name="target"/> and then slerps by the fraction of that angle it consumed. The
    ///     spring state is therefore an honest angular rate in rad/s that stays meaningful frame to
    ///     frame, the interpolation fraction is clamped to <c>[0,1]</c> so overshoot is impossible by
    ///     construction, and the output is unit by construction (slerp normalises). Rotation smoothing
    ///     does not need momentum past the target — nobody wants a camera that whips past its subject
    ///     and swings back — so nothing is lost.
    /// </remarks>
    /// <param name="current">The camera's current orientation.</param>
    /// <param name="target">The orientation it is being asked to hold.</param>
    /// <param name="smoothTimeSeconds">As for the position overload; <c>0</c> is raw pass-through.</param>
    /// <param name="dtSeconds">The frame delta. Non-positive ⇒ raw target.</param>
    /// <returns>The smoothed, unit-length orientation.</returns>
    public Quat Step(Quat current, Quat target, double smoothTimeSeconds, double dtSeconds)
    {
        if (!current.IsFinite || !target.IsFinite ||
            !double.IsFinite(smoothTimeSeconds) || !double.IsFinite(dtSeconds) ||
            smoothTimeSeconds <= 0.0 || dtSeconds <= 0.0)
        {
            _angularVelocity = 0.0;
            return target.Normalized();
        }

        var a = current.Normalized();
        var b = target.Normalized();

        // Short-path: −q is the same rotation, so measure the angle to the near representative.
        var dot = Quat.Dot(a, b);
        if (dot < 0.0) dot = -dot;
        var angle = 2.0 * Math.Acos(Math.Clamp(dot, -1.0, 1.0));

        if (!double.IsFinite(angle) || angle <= AngleEpsilon)
        {
            _angularVelocity = 0.0;
            return b;
        }

        var omega = 2.0 / smoothTimeSeconds;
        var x = omega * dtSeconds;
        var exp = 1.0 / (1.0 + x + 0.48 * x * x + 0.235 * x * x * x);

        // Drive the remaining angle toward zero with the same critically-damped step.
        var change = angle;
        var temp = (_angularVelocity + omega * change) * dtSeconds;
        var velocity = (_angularVelocity - omega * temp) * exp;
        var remaining = (change + temp) * exp;

        if (remaining <= 0.0)
        {
            // Reached (or passed) the target this frame: snap and re-derive the rate.
            _angularVelocity = -angle / dtSeconds;
            return b;
        }

        if (!double.IsFinite(remaining) || !double.IsFinite(velocity))
        {
            _angularVelocity = 0.0;
            return b;
        }

        _angularVelocity = velocity;
        var s = Math.Clamp(1.0 - remaining / angle, 0.0, 1.0);
        return Splines.Slerp(a, b, s);
    }
}
