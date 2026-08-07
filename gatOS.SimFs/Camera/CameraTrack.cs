namespace gatOS.SimFs.Camera;

/// <summary>
///     How the samples between two keys are joined (the track format's <c>"curve"</c> field:
///     <c>step | linear | catmull-rom | bezier</c>).
/// </summary>
/// <remarks>
///     The kind selects the <i>shape</i> of the segment; the per-key <see cref="EaseSpec"/> selects how
///     fast progress moves <i>along</i> that shape. They are orthogonal on purpose — "an arc that
///     decelerates into the key" is a Catmull-Rom segment with an <c>out</c> ease, not a third curve
///     type.
/// </remarks>
public enum CurveKind
{
    /// <summary>Hold the previous key's value until the next key is reached. No interpolation at all.</summary>
    Step,

    /// <summary>Straight-line interpolation (slerp for a rotation channel).</summary>
    Linear,

    /// <summary>
    ///     Centripetal Catmull-Rom through the keys (squad for a rotation channel) — the smooth default
    ///     for a hand-authored move. See <see cref="Splines.CatmullRom"/> for why centripetal is a
    ///     requirement rather than a preference.
    /// </summary>
    CatmullRom,

    /// <summary>
    ///     Cubic Bézier with explicit per-key handles — the shape a spline cannot express, e.g. a
    ///     deliberate wide swing round an obstacle. Not accepted on a rotation channel (a quaternion
    ///     handle has no meaning here; use <see cref="CatmullRom"/>, which is squad).
    /// </summary>
    Bezier,
}

/// <summary>
///     How a shot's <c>position</c> block places the camera (the track format's <c>"mode"</c> field).
///     All three resolve in the block's <see cref="FrameKind"/> about the shot's anchor, so the choice
///     is purely about how the placement is <i>authored</i>.
/// </summary>
public enum PositionMode
{
    /// <summary>Explicit XYZ keys, interpolated by the channel's <see cref="CurveKind"/>.</summary>
    Cartesian,

    /// <summary>
    ///     Spherical placement about the anchor: independent <c>radius</c> / <c>azimuth</c> /
    ///     <c>elevation</c> scalar channels — the granular twin of the <c>pose/orbit/*</c> leaves.
    ///     "Circle the ship while pushing in" is three scalar curves, not a keyframed circle.
    /// </summary>
    Orbit,

    /// <summary>
    ///     A rigid constant offset in the frame: the locked-off tripod and the bolted-on chase cam.
    ///     No keys, no interpolation — all the motion comes from the frame itself.
    /// </summary>
    Attach,
}

/// <summary>
///     One authored key of a track channel: when, what, how to leave it, and (for
///     <see cref="CurveKind.Bezier"/>) the control handles either side of it.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="Ease"/> is already resolved.</b> The parser folds the authoring rule — a
///         segment's ease comes from its <i>start</i> key, or failing that from its <i>end</i> key, or
///         failing that from the track's <c>defaults.ease</c> — into every key at parse time, so the
///         evaluator never has to look sideways. The last key's ease is therefore stored but inert
///         (there is no segment leaving it).
///     </para>
///     <para>
///         <typeparamref name="TValue"/> is constrained to a struct because every animatable value here is one
///         (<see cref="double"/>, <see cref="Vec3"/>, <see cref="Quat"/>), which is what lets the whole
///         evaluation path run without touching the heap.
///     </para>
/// </remarks>
/// <typeparam name="TValue">The animated value type.</typeparam>
/// <param name="TSeconds">The key's time, in seconds from the start of its shot.</param>
/// <param name="Value">The authored value at <paramref name="TSeconds"/>.</param>
/// <param name="Ease">The resolved ease governing the segment that <i>leaves</i> this key.</param>
/// <param name="HandleIn">
///     The Bézier control pulling <i>into</i> this key (absolute, in the channel's own units), or null.
/// </param>
/// <param name="HandleOut">
///     The Bézier control pulling <i>out of</i> this key (absolute, in the channel's own units), or null.
/// </param>
public readonly record struct TrackKey<TValue>(
    double TSeconds,
    TValue Value,
    EaseSpec Ease,
    TValue? HandleIn = default,
    TValue? HandleOut = default)
    where TValue : struct;

/// <summary>
///     One animated channel of a shot: a curve kind plus its keys, strictly increasing in time.
/// </summary>
/// <remarks>
///     A sealed class rather than a record so the key array cannot be handed out for mutation: a
///     committed track is immutable, and a shot that started against version 3 must keep playing
///     version 3 unchanged. The indexer exists so the evaluator's inner loop is an array access rather
///     than an interface dispatch.
/// </remarks>
/// <typeparam name="TValue">The animated value type.</typeparam>
public sealed class TrackChannel<TValue>
    where TValue : struct
{
    private readonly TrackKey<TValue>[] _keys;

    /// <param name="curve">How samples between the keys are joined.</param>
    /// <param name="keys">The keys, already validated as strictly time-increasing and non-empty.</param>
    public TrackChannel(CurveKind curve, TrackKey<TValue>[] keys)
    {
        Curve = curve;
        _keys = keys;
    }

    /// <summary>How samples between the keys are joined.</summary>
    public CurveKind Curve { get; }

    /// <summary>The number of keys (always ≥ 1 — an empty channel is rejected at parse).</summary>
    public int Count => _keys.Length;

    /// <summary>The key at <paramref name="index"/>.</summary>
    /// <param name="index">A zero-based key index.</param>
    public TrackKey<TValue> this[int index] => _keys[index];

    /// <summary>The keys, read-only (for tests and diagnostics; the evaluator uses the indexer).</summary>
    public IReadOnlyList<TrackKey<TValue>> Keys => _keys;
}

/// <summary>
///     A shot's <c>position</c> block: the placement mode, the frame it resolves in, and whichever of
///     the mode's channels were authored.
/// </summary>
/// <remarks>
///     One record with a <see cref="Mode"/> discriminator rather than three types, because the three
///     modes are three spellings of one channel group and the evaluator has to switch on the mode
///     anyway. Exactly the fields of the active mode are non-null; the parser enforces that, so
///     <c>"mode":"orbit"</c> carrying cartesian <c>keys</c> is EINVAL rather than a silently ignored
///     block.
/// </remarks>
/// <param name="Mode">Which placement spelling this block uses.</param>
/// <param name="Frame">The frame the placement resolves in (from the block, the shot, or the defaults).</param>
/// <param name="Keys"><see cref="PositionMode.Cartesian"/> only: the XYZ keys.</param>
/// <param name="Offset"><see cref="PositionMode.Attach"/> only: the constant offset.</param>
/// <param name="Radius"><see cref="PositionMode.Orbit"/> only: distance from the anchor, metres.</param>
/// <param name="Azimuth"><see cref="PositionMode.Orbit"/> only: bearing about the frame's Z axis, degrees.</param>
/// <param name="Elevation"><see cref="PositionMode.Orbit"/> only: angle above the frame's XY plane, degrees.</param>
public sealed record PositionSpec(
    PositionMode Mode,
    FrameKind Frame,
    TrackChannel<Vec3>? Keys,
    Vec3 Offset,
    TrackChannel<double>? Radius,
    TrackChannel<double>? Azimuth,
    TrackChannel<double>? Elevation);

/// <summary>
///     A shot's <c>aim</c> block: what to look at, where on it, in which frame, and where "up" comes
///     from — plus an optional animated roll.
/// </summary>
/// <remarks>
///     The target, offset, frame and up are <b>constant</b> for the shot on purpose: the whole point of
///     an aim channel is that the host re-resolves it against the <i>live</i> target every frame, which
///     is what makes "+0.9 m on the kittenaut's own Y axis" stay its head as it walks. Animating the
///     offset instead would be animating the wrong thing; animate the camera's position, or roll.
/// </remarks>
/// <param name="Target">What the camera looks at.</param>
/// <param name="Offset">The offset from the target, in <paramref name="Frame"/>, metres.</param>
/// <param name="Frame">The frame <paramref name="Offset"/> resolves in.</param>
/// <param name="Up">Where "up" is taken from once forward is fixed.</param>
/// <param name="Roll">An optional animated roll about the view axis, degrees.</param>
public sealed record AimSpec(
    TargetRef Target,
    Vec3 Offset,
    FrameKind Frame,
    AimUpKind Up,
    TrackChannel<double>? Roll);

/// <summary>
///     The track-level <c>defaults</c> block: values any shot or channel that omits them inherits.
/// </summary>
/// <param name="Frame">The default frame for a shot's position block.</param>
/// <param name="Anchor">The default anchor for a shot.</param>
/// <param name="Ease">The default ease for a key that names none (and whose neighbour names none either).</param>
public sealed record TrackDefaults(FrameKind? Frame, TargetRef? Anchor, EaseSpec? Ease)
{
    /// <summary>The empty defaults block — everything falls back to the format's own defaults.</summary>
    public static TrackDefaults None { get; } = new(null, null, null);
}

/// <summary>
///     One shot of a track: a time window on the track timeline, plus the channels it drives inside it.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="Channels"/> is the whole discipline of plan §4.3 in one field.</b> A shot claims
///         <i>only</i> the channels it actually animates; every other channel falls through to the live
///         overrides and then to the baseline. That is what lets a <c>timed_batch</c> pull focus while
///         this shot interpolates position — and it is why the mask is computed from what was authored,
///         never widened to "everything with a default value". Widening it would silently clobber live
///         leaf writes on channels the shot never mentions.
///     </para>
///     <para>
///         <see cref="Anchor"/> and the position block's frame are claimed only when the shot actually
///         drives a position (any mode); a shot that only animates <c>fov</c> must not seize the anchor
///         just because the track named a default one.
///     </para>
/// </remarks>
/// <param name="Name">The shot's name, as reported by <c>camera/playback</c> and the <c>camera.shot</c> event.</param>
/// <param name="TSeconds">When the shot starts, in seconds on the track timeline.</param>
/// <param name="DurationSeconds">How long the shot lasts, seconds (always &gt; 0).</param>
/// <param name="Anchor">The target frames resolve against, or <see cref="TargetRef.None"/>.</param>
/// <param name="BlendInSeconds">The eased cross-fade from the previous shot's final pose, seconds.</param>
/// <param name="Position">The position block, or null.</param>
/// <param name="Aim">The aim block, or null.</param>
/// <param name="Rotation">The explicit-orientation channel, or null. Mutually exclusive with <paramref name="Aim"/>.</param>
/// <param name="Roll">A shot-level roll channel, or null (the <c>aim</c> block may carry one instead).</param>
/// <param name="Fov">The field-of-view channel, or null.</param>
/// <param name="Time">The simulation-speed channel, or null. See <see cref="CameraChannel.TimeScale"/>.</param>
public sealed record Shot(
    string Name,
    double TSeconds,
    double DurationSeconds,
    TargetRef Anchor,
    double BlendInSeconds,
    PositionSpec? Position,
    AimSpec? Aim,
    TrackChannel<Quat>? Rotation,
    TrackChannel<double>? Roll,
    TrackChannel<double>? Fov,
    TrackChannel<double>? Time)
{
    /// <summary>
    ///     Exactly the channels this shot drives — the claim mask handed to
    ///     <see cref="CameraState.Compose"/>. See the type remarks: it is never widened.
    /// </summary>
    public CameraChannelMask Channels { get; } = Claim(Anchor, Position, Aim, Rotation, Roll, Fov, Time);

    /// <summary>The shot's end on the track timeline, seconds.</summary>
    public double EndSeconds => TSeconds + DurationSeconds;

    private static CameraChannelMask Claim(
        TargetRef anchor, PositionSpec? position, AimSpec? aim, TrackChannel<Quat>? rotation,
        TrackChannel<double>? roll, TrackChannel<double>? fov, TrackChannel<double>? time)
    {
        var mask = CameraChannelMask.None;

        if (position is not null)
        {
            // Every placement mode resolves in a frame, so the frame is always part of the claim; the
            // anchor only when the track actually named one (an ECL placement needs none).
            mask |= CameraChannelMask.Frame;
            if (anchor.HasTarget)
                mask |= CameraChannelMask.Anchor;

            switch (position.Mode)
            {
                case PositionMode.Orbit:
                    if (position.Radius is not null) mask |= CameraChannelMask.OrbitRadius;
                    if (position.Azimuth is not null) mask |= CameraChannelMask.OrbitAzimuth;
                    if (position.Elevation is not null) mask |= CameraChannelMask.OrbitElevation;
                    break;

                case PositionMode.Cartesian:
                case PositionMode.Attach:
                default:
                    mask |= CameraChannelMask.Position;
                    break;
            }
        }

        if (aim is not null)
        {
            mask |= CameraChannelMask.AimTarget | CameraChannelMask.AimOffset
                    | CameraChannelMask.AimFrame | CameraChannelMask.AimUp;
            if (aim.Roll is not null)
                mask |= CameraChannelMask.Roll;
        }

        if (rotation is not null) mask |= CameraChannelMask.Rotation;
        if (roll is not null) mask |= CameraChannelMask.Roll;
        if (fov is not null) mask |= CameraChannelMask.Fov;
        if (time is not null) mask |= CameraChannelMask.TimeScale;
        return mask;
    }
}

/// <summary>
///     A parsed, validated camera track: the whole of an uploaded <c>/sim/camera/track/&lt;name&gt;</c>
///     JSON document as an immutable object graph (plans/CAMERA_CONTROLS_PLAN.md §4.4).
/// </summary>
/// <remarks>
///     Built only by <see cref="TrackParser"/>, which is where every validation rule lives; by the time
///     a <see cref="Track"/> exists, the evaluator may assume shots are ordered and non-overlapping,
///     every channel has at least one key, key times increase strictly and lie inside their shot, and
///     nothing anywhere is non-finite.
/// </remarks>
/// <param name="Loop">The authored default for looping (a <c>camera/play … loop</c> keyword overrides it).</param>
/// <param name="Defaults">The track-level defaults the parser has already folded into the shots.</param>
/// <param name="Shots">The shots, ordered by start time and non-overlapping.</param>
public sealed record Track(bool Loop, TrackDefaults Defaults, IReadOnlyList<Shot> Shots)
{
    /// <summary>
    ///     The track's length in seconds: the end of its last shot. A leading gap (a first shot with
    ///     <c>t &gt; 0</c>) is part of the length — the timeline is absolute, exactly like a schedule's.
    /// </summary>
    public double DurationSeconds { get; } = End(Shots);

    /// <summary>The track's length in milliseconds — the unit <c>PlaybackClock</c> speaks.</summary>
    public double DurationMs => DurationSeconds * 1000.0;

    private static double End(IReadOnlyList<Shot> shots)
    {
        var end = 0.0;
        for (var i = 0; i < shots.Count; i++)
            if (shots[i].EndSeconds > end)
                end = shots[i].EndSeconds;
        return end;
    }
}
