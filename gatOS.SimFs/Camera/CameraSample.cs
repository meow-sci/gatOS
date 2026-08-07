namespace gatOS.SimFs.Camera;

/// <summary>
///     One evaluated frame of a camera track: the pose the active shot wants, <b>exactly</b> the
///     channels it claims, and which shot produced it.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="Channels"/> is not decoration.</b> <see cref="CameraState.Compose"/> reads only
///         the channels this mask names and falls through to the live overrides and then the baseline
///         for every other one. Widening the mask to "everything the pose struct can hold" would
///         silently clobber a live <c>pose/fov</c> write with the pose's default 60° — which is exactly
///         the composability plan §4.3 exists to provide.
///     </para>
///     <para>
///         <b>Positions and aim targets stay frame-relative.</b> The pose carries the value <i>plus</i>
///         its <see cref="FrameKind"/> and anchor <see cref="TargetRef"/>; resolving a frame to ECL
///         needs live game state and is the director's job. Nothing here has ever seen a game type.
///     </para>
/// </remarks>
/// <param name="Pose">The evaluated pose. Only the fields named by <paramref name="Channels"/> are meaningful.</param>
/// <param name="Channels">Exactly the channels the active shot animates.</param>
/// <param name="ShotIndex">The active shot's index, or <c>-1</c> when the track has no shots.</param>
/// <param name="ShotName">The active shot's name, or <c>""</c>.</param>
public readonly record struct CameraSample(
    CameraPose Pose,
    CameraChannelMask Channels,
    int ShotIndex,
    string ShotName)
{
    /// <summary>The "nothing to drive" sample.</summary>
    public static CameraSample None { get; } = new(CameraPose.Default, CameraChannelMask.None, -1, "");
}

/// <summary>
///     The game-free geometry shared between a track's <c>"mode": "orbit"</c> placement and the
///     <c>pose/orbit/*</c> leaves: spherical coordinates about an anchor, resolved into an offset in
///     the chosen frame.
/// </summary>
/// <remarks>
///     This is the <b>one</b> definition — the director must resolve the orbit channels through it
///     rather than re-deriving the trigonometry, or a track's circle and a hand-written
///     <c>echo 90 &gt; pose/orbit/azimuth</c> would put the camera in two different places.
/// </remarks>
public static class CameraPlacement
{
    private const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>
    ///     The offset of a camera placed at <paramref name="azimuthDegrees"/> /
    ///     <paramref name="elevationDegrees"/> and <paramref name="radiusMetres"/> from the anchor,
    ///     expressed in the placement frame's axes: azimuth is measured in the XY plane from +X toward
    ///     +Y, elevation above that plane toward +Z.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The azimuth is folded into <c>[0, 360)</c> before any trigonometry, and that is the
    ///         360°-closure fix.</b> A full orbit evaluates its last frame at exactly <c>360</c>
    ///         (progress snaps to exactly 1.0 — see <see cref="Easing.Apply"/>), but
    ///         <c>Math.Sin(2π)</c> is <c>−2.4e-16</c>, not <c>0</c>, so without the fold the closing
    ///         frame would land a hair off the opening one and a looping shot would visibly ratchet.
    ///         <c>360 − 360·floor(360/360)</c> is exactly <c>0</c>, so the loop closes bit-identically.
    ///     </para>
    ///     <para>
    ///         Elevation is clamped to <c>[-90, 90]</c> defensively: a Bézier ease may deliberately
    ///         overshoot its keys, and an elevation past the pole would flip the placement inside out.
    ///     </para>
    /// </remarks>
    /// <param name="radiusMetres">Distance from the anchor, metres.</param>
    /// <param name="azimuthDegrees">Bearing in the frame's XY plane, degrees.</param>
    /// <param name="elevationDegrees">Angle above the frame's XY plane, degrees.</param>
    /// <returns>The offset from the anchor, in the placement frame's axes. <see cref="Vec3.Zero"/> for non-finite input.</returns>
    public static Vec3 Spherical(double radiusMetres, double azimuthDegrees, double elevationDegrees)
    {
        if (!double.IsFinite(radiusMetres) || !double.IsFinite(azimuthDegrees)
                                           || !double.IsFinite(elevationDegrees))
        {
            return Vec3.Zero;
        }

        var azimuth = NormalizeAzimuth(azimuthDegrees) * DegreesToRadians;
        var elevation = Math.Clamp(elevationDegrees, -90.0, 90.0) * DegreesToRadians;

        var cosEl = Math.Cos(elevation);
        return new Vec3(
            radiusMetres * cosEl * Math.Cos(azimuth),
            radiusMetres * cosEl * Math.Sin(azimuth),
            radiusMetres * Math.Sin(elevation));
    }

    /// <summary>
    ///     Folds an azimuth into <c>[0, 360)</c>. Exact at the multiples of 360 — see
    ///     <see cref="Spherical"/>'s remarks for why that matters.
    /// </summary>
    /// <param name="degrees">Any finite azimuth.</param>
    public static double NormalizeAzimuth(double degrees)
    {
        if (!double.IsFinite(degrees))
            return 0.0;
        var folded = degrees - (360.0 * Math.Floor(degrees / 360.0));
        return folded is >= 0.0 and < 360.0 ? folded : 0.0;
    }
}
