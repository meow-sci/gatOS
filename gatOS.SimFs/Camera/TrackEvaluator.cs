namespace gatOS.SimFs.Camera;

/// <summary>
///     Evaluates a parsed <see cref="Track"/> at a timeline position: picks the active shot, samples
///     every channel it declares, and cross-fades the first <c>blend_in</c> seconds from the previous
///     shot's final pose (plans/CAMERA_CONTROLS_PLAN.md §4.4).
/// </summary>
/// <remarks>
///     <para>
///         <b>Deterministic and allocation-free.</b> There is no state, no time source and no
///         randomness: the same <c>t</c> always yields a bit-identical sample, sampled in any order, on
///         any thread (plan §9). Every value is a struct and every spline call is a static over structs,
///         so a 60 fps take allocates nothing after the track is parsed.
///     </para>
///     <para>
///         <b>Absolute-from-start, never incremental.</b> Every channel is evaluated from its keys at
///         the absolute time <c>t</c> — never by accumulating a per-frame delta onto a running value.
///         Incremental accumulation is the documented drift bug of
///         <c>unscience/plans/done/TIMING_ANALYSIS_AND_FIX.md</c>: a 360° orbit built from
///         <c>azimuth += ω·dt</c> lands short of where it started because the frame deltas do not sum to
///         the duration, so a looping shot visibly ratchets. Here a full turn is the key pair
///         <c>0° → 360°</c>, the terminal progress snaps to exactly <c>1.0</c>
///         (<see cref="Easing.Apply"/>), and <see cref="CameraPlacement.Spherical"/> folds
///         <c>360</c> back to exactly <c>0</c> — so the loop closes bit-identically.
///     </para>
///     <para>
///         <b>Outside a shot the evaluator holds, it does not release.</b> Before the first shot, in a
///         gap between shots, and past the last shot, it returns the nearest shot's terminal sample. A
///         gap in a shot list is a hold — releasing the camera there would snap it back to whatever the
///         overrides say and then snap again when the next shot began. Ending playback is the player's
///         decision (<c>camera/stop</c>), not the evaluator's.
///     </para>
/// </remarks>
public static class TrackEvaluator
{
    /// <summary>
    ///     Samples <paramref name="track"/> at <paramref name="tSeconds"/> on the track timeline.
    /// </summary>
    /// <param name="track">The parsed track.</param>
    /// <param name="tSeconds">The timeline position, seconds. Outside the track it holds (see the remarks).</param>
    /// <returns>The evaluated pose, the channels it claims, and the shot that produced it.</returns>
    public static CameraSample Sample(Track track, double tSeconds)
    {
        ArgumentNullException.ThrowIfNull(track);
        var shots = track.Shots;
        if (shots.Count == 0)
            return CameraSample.None;

        var t = double.IsNaN(tSeconds) ? 0.0 : tSeconds;
        var index = FindShot(shots, t);
        var shot = shots[index];
        var local = Math.Clamp(t - shot.TSeconds, 0.0, shot.DurationSeconds);
        var sample = SampleShot(shot, index, local);

        if (index == 0 || shot.BlendInSeconds <= 0 || local >= shot.BlendInSeconds)
            return sample;

        // The cross-fade source is the previous shot at its own end — the pose the camera was actually
        // holding a moment ago, including through a gap. A first shot has nothing to blend from and
        // therefore starts at full value; softening that edge is the director's PoseSmoother, which can
        // see the live composed pose that the evaluator deliberately cannot.
        var previous = shots[index - 1];
        var from = SampleShot(previous, index - 1, previous.DurationSeconds);
        var progress = Easing.Apply(local / shot.BlendInSeconds, BlendEase(track));
        return Blend(from, sample, progress);
    }

    /// <summary>
    ///     The default cross-fade shape. A blend with a linear edge reads as two hard cuts joined by a
    ///     slide, so the fallback is a symmetric ease; a track that wants otherwise says so in
    ///     <c>defaults.ease</c>.
    /// </summary>
    private static EaseSpec BlendEase(Track track)
        => track.Defaults.Ease ?? EaseSpec.Named(EaseKind.InOut);

    /// <summary>The index of the last shot that has started by <paramref name="t"/> (0 before the first).</summary>
    private static int FindShot(IReadOnlyList<Shot> shots, double t)
    {
        if (t <= shots[0].TSeconds)
            return 0;

        int lo = 0, hi = shots.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (shots[mid].TSeconds <= t)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    // ---- one shot ------------------------------------------------------------------------------------

    private static CameraSample SampleShot(Shot shot, int index, double local)
    {
        var pose = CameraPose.Default;

        if (shot.Position is { } position)
        {
            pose = pose with { Frame = position.Frame };
            if (shot.Anchor.HasTarget)
                pose = pose with { Anchor = shot.Anchor };

            switch (position.Mode)
            {
                case PositionMode.Orbit:
                    // Absolute from the keys, never `+=` — see the type remarks.
                    if (position.Radius is { } radius)
                        pose = pose with { OrbitRadius = EvalScalar(radius, local) };
                    if (position.Azimuth is { } azimuth)
                        pose = pose with { OrbitAzimuth = EvalScalar(azimuth, local) };
                    if (position.Elevation is { } elevation)
                        pose = pose with { OrbitElevation = EvalScalar(elevation, local) };
                    break;

                case PositionMode.Attach:
                    pose = pose with { Position = position.Offset, PositionIsGeo = false };
                    break;

                case PositionMode.Cartesian:
                default:
                    pose = pose with { Position = EvalVec(position.Keys!, local), PositionIsGeo = false };
                    break;
            }
        }

        if (shot.Aim is { } aim)
        {
            pose = pose with
            {
                AimTarget = aim.Target,
                AimOffset = aim.Offset,
                AimFrame = aim.Frame,
                AimUp = aim.Up,
            };
            if (aim.Roll is { } aimRoll)
                pose = pose with { Roll = EvalScalar(aimRoll, local) };
        }

        if (shot.Rotation is { } rotation)
            pose = pose with { Rotation = EvalQuat(rotation, local) };
        if (shot.Roll is { } roll)
            pose = pose with { Roll = EvalScalar(roll, local) };
        if (shot.Fov is { } fov)
            pose = pose with { Fov = EvalScalar(fov, local) };
        if (shot.Time is { } time)
            pose = pose with { TimeScale = EvalScalar(time, local) };

        return new CameraSample(pose, shot.Channels, index, shot.Name);
    }

    // ---- blending ------------------------------------------------------------------------------------

    /// <summary>
    ///     Cross-fades <paramref name="from"/> into <paramref name="to"/> over the channels
    ///     <b>both</b> shots declare. A channel only the new shot drives has nothing to fade from and is
    ///     taken at full value; a channel only the old shot drove is released, because the mask is the
    ///     new shot's. Discrete channels (frame, anchor, aim target, aim frame, up, ortho) cut rather
    ///     than fade — there is no halfway between two frames.
    /// </summary>
    private static CameraSample Blend(in CameraSample from, in CameraSample to, double progress)
    {
        var both = from.Channels & to.Channels;
        if (both == CameraChannelMask.None)
            return to;

        var pose = to.Pose;
        var a = from.Pose;

        if (both.Has(CameraChannel.Position))
            pose = pose with { Position = LerpUnclamped(a.Position, pose.Position, progress) };
        if (both.Has(CameraChannel.Rotation))
            pose = pose with { Rotation = Splines.Slerp(a.Rotation, pose.Rotation, progress) };
        if (both.Has(CameraChannel.AimOffset))
            pose = pose with { AimOffset = LerpUnclamped(a.AimOffset, pose.AimOffset, progress) };
        if (both.Has(CameraChannel.Roll))
            pose = pose with { Roll = LerpUnclamped(a.Roll, pose.Roll, progress) };
        if (both.Has(CameraChannel.Fov))
            pose = pose with { Fov = LerpUnclamped(a.Fov, pose.Fov, progress) };
        if (both.Has(CameraChannel.OrbitRadius))
            pose = pose with { OrbitRadius = LerpUnclamped(a.OrbitRadius, pose.OrbitRadius, progress) };
        if (both.Has(CameraChannel.OrbitAzimuth))
            pose = pose with { OrbitAzimuth = LerpUnclamped(a.OrbitAzimuth, pose.OrbitAzimuth, progress) };
        if (both.Has(CameraChannel.OrbitElevation))
            pose = pose with { OrbitElevation = LerpUnclamped(a.OrbitElevation, pose.OrbitElevation, progress) };
        if (both.Has(CameraChannel.TimeScale))
            pose = pose with { TimeScale = LerpUnclamped(a.TimeScale, pose.TimeScale, progress) };

        return to with { Pose = pose };
    }

    // ---- channel evaluation --------------------------------------------------------------------------

    private static double EvalScalar(TrackChannel<double> channel, double t)
    {
        if (!Segment(channel, t, out var i, out var u))
            return channel[i].Value;

        var k0 = channel[i];
        var k1 = channel[i + 1];
        var e = Easing.Apply(u, k0.Ease);

        switch (channel.Curve)
        {
            case CurveKind.Step:
                return k0.Value;

            case CurveKind.CatmullRom:
            {
                var last = channel.Count - 1;
                return Splines.CatmullRom(
                    Lift(channel[Math.Max(i - 1, 0)].Value), Lift(k0.Value),
                    Lift(k1.Value), Lift(channel[Math.Min(i + 2, last)].Value), e).X;
            }

            case CurveKind.Bezier:
                return Splines.Bezier(
                    Lift(k0.Value), Lift(k0.HandleOut ?? k0.Value),
                    Lift(k1.HandleIn ?? k1.Value), Lift(k1.Value), e).X;

            case CurveKind.Linear:
            default:
                return LerpUnclamped(k0.Value, k1.Value, e);
        }
    }

    private static Vec3 EvalVec(TrackChannel<Vec3> channel, double t)
    {
        if (!Segment(channel, t, out var i, out var u))
            return channel[i].Value;

        var k0 = channel[i];
        var k1 = channel[i + 1];
        var e = Easing.Apply(u, k0.Ease);

        switch (channel.Curve)
        {
            case CurveKind.Step:
                return k0.Value;

            case CurveKind.CatmullRom:
            {
                // The terminal keys are repeated as their own missing neighbours, which is what makes
                // the first and last segments land exactly on their end keys.
                var last = channel.Count - 1;
                return Splines.CatmullRom(
                    channel[Math.Max(i - 1, 0)].Value, k0.Value,
                    k1.Value, channel[Math.Min(i + 2, last)].Value, e);
            }

            case CurveKind.Bezier:
                return Splines.Bezier(
                    k0.Value, k0.HandleOut ?? k0.Value, k1.HandleIn ?? k1.Value, k1.Value, e);

            case CurveKind.Linear:
            default:
                return LerpUnclamped(k0.Value, k1.Value, e);
        }
    }

    private static Quat EvalQuat(TrackChannel<Quat> channel, double t)
    {
        if (!Segment(channel, t, out var i, out var u))
            return channel[i].Value;

        var k0 = channel[i];
        var k1 = channel[i + 1];
        if (channel.Curve == CurveKind.Step)
            return k0.Value;

        var e = Easing.Apply(u, k0.Ease);

        // Slerp is only C⁰ across keys — the angular velocity jumps at every waypoint, which reads as a
        // flick. Squad needs three keys to have a tangent to match, so a two-key channel (and an
        // explicit "curve": "linear") slerps.
        if (channel.Curve == CurveKind.Linear || channel.Count < 3)
            return Splines.Slerp(k0.Value, k1.Value, e);

        var lastIndex = channel.Count - 1;
        var previous = channel[Math.Max(i - 1, 0)].Value;
        var next = channel[Math.Min(i + 2, lastIndex)].Value;
        var controlA = Splines.SquadIntermediate(previous, k0.Value, k1.Value);
        var controlB = Splines.SquadIntermediate(k0.Value, k1.Value, next);
        return Splines.Squad(k0.Value, controlA, controlB, k1.Value, e);
    }

    /// <summary>
    ///     Locates the segment containing <paramref name="t"/>. Returns false when <paramref name="t"/>
    ///     is at or outside an end key, with <paramref name="index"/> set to that key — the hold case.
    /// </summary>
    private static bool Segment<TValue>(TrackChannel<TValue> channel, double t, out int index, out double u)
        where TValue : struct
    {
        var last = channel.Count - 1;
        u = 0.0;

        if (t <= channel[0].TSeconds)
        {
            index = 0;
            return false;
        }

        if (t >= channel[last].TSeconds)
        {
            index = last;
            return false;
        }

        int lo = 0, hi = last;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (channel[mid].TSeconds <= t)
                lo = mid;
            else
                hi = mid - 1;
        }

        index = lo;
        var span = channel[index + 1].TSeconds - channel[index].TSeconds;
        u = span > 0 ? (t - channel[index].TSeconds) / span : 0.0;
        return true;
    }

    /// <summary>Lifts a scalar into the X axis so the Vec3 spline primitives serve scalar channels too.</summary>
    /// <remarks>
    ///     A 1-D centripetal Catmull-Rom is exactly the 3-D one on <c>(v, 0, 0)</c> — the knot spacing
    ///     <c>|Δp|^0.5</c> reduces to <c>|Δv|^0.5</c> — so this reuses the landed, tested primitive
    ///     rather than growing a second implementation that could disagree with it.
    /// </remarks>
    private static Vec3 Lift(double value) => new(value, 0.0, 0.0);

    /// <summary>
    ///     Linear interpolation that snaps the exact endpoints but does <b>not</b> clamp in between.
    /// </summary>
    /// <remarks>
    ///     The clamp is deliberately absent so a cubic-Bézier ease's out-of-range y handles do what they
    ///     exist for: <c>y &lt; 0</c> pulls back before moving (anticipation) and <c>y &gt; 1</c> flies
    ///     past and settles (overshoot). <see cref="Splines.Lerp(double,double,double)"/> clamps, which
    ///     would flatten both effects to a hold at the key. The endpoint snap is kept — an eased
    ///     progress of exactly <c>0</c> or <c>1</c> must return the key bit-identically, which is what
    ///     makes a loop close. The spline curves (Catmull-Rom, Bézier) keep the landed clamped
    ///     behaviour: extrapolating a spline past its hull really would fling the camera.
    /// </remarks>
    private static double LerpUnclamped(double a, double b, double e)
    {
        if (e == 0.0) return a;
        if (e == 1.0) return b;
        return a + ((b - a) * e);
    }

    /// <inheritdoc cref="LerpUnclamped(double,double,double)"/>
    private static Vec3 LerpUnclamped(Vec3 a, Vec3 b, double e)
    {
        if (e == 0.0) return a;
        if (e == 1.0) return b;
        return new Vec3(
            a.X + ((b.X - a.X) * e),
            a.Y + ((b.Y - a.Y) * e),
            a.Z + ((b.Z - a.Z) * e));
    }
}
