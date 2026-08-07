namespace gatOS.SimFs.Camera;

/// <summary>
///     One independently-claimable camera channel. A shot claims only the channels it declares; every
///     other channel falls through to the live overrides, then to the baseline — which is what lets a
///     <c>timed_batch</c> pull focus while a track interpolates position
///     (plans/CAMERA_CONTROLS_PLAN.md §4.3).
/// </summary>
/// <remarks>
///     Each member corresponds to exactly one writable <c>/sim/camera</c> leaf (or, for
///     <see cref="Position"/>, the <c>pose/position</c>+<c>pose/geo</c> pair, which are two spellings
///     of one placement). The ordinal is the bit index in <see cref="CameraChannelMask"/>, so the
///     enum's order is part of the type's contract — append, never reorder.
/// </remarks>
public enum CameraChannel
{
    /// <summary>Where the camera is — Cartesian (<c>pose/position</c>) or geodetic (<c>pose/geo</c>).</summary>
    Position,

    /// <summary>The frame <see cref="Position"/> is expressed in (<c>pose/frame</c>).</summary>
    Frame,

    /// <summary>The target frames resolve against (<c>pose/anchor</c>).</summary>
    Anchor,

    /// <summary>An explicit ECL orientation (<c>pose/rotation</c>), used when no aim target is set.</summary>
    Rotation,

    /// <summary>What the camera looks at (<c>pose/aim_target</c>).</summary>
    AimTarget,

    /// <summary>The offset from the aim target, in <see cref="AimFrame"/> (<c>pose/aim_offset</c>).</summary>
    AimOffset,

    /// <summary>The frame <see cref="AimOffset"/> resolves in (<c>pose/aim_frame</c>).</summary>
    AimFrame,

    /// <summary>Where "up" comes from once forward is fixed (<c>pose/aim_up</c>).</summary>
    AimUp,

    /// <summary>Roll about the view axis, degrees, applied after aim (<c>pose/roll</c>).</summary>
    Roll,

    /// <summary>Vertical field of view in degrees (<c>pose/fov</c>).</summary>
    Fov,

    /// <summary>Whether the projection is orthographic (<c>pose/ortho</c>).</summary>
    Ortho,

    /// <summary>The orthographic half-height in metres (<c>pose/ortho_height</c>).</summary>
    OrthoHeight,

    /// <summary>Critically-damped follow time in seconds; 0 is raw (<c>pose/smoothing</c>).</summary>
    Smoothing,

    /// <summary>Spherical placement about the anchor: radius, metres (<c>pose/orbit/radius</c>).</summary>
    OrbitRadius,

    /// <summary>Spherical placement about the anchor: azimuth, degrees (<c>pose/orbit/azimuth</c>).</summary>
    OrbitAzimuth,

    /// <summary>Spherical placement about the anchor: elevation, degrees (<c>pose/orbit/elevation</c>).</summary>
    OrbitElevation,

    /// <summary>Simulation speed as a shot channel (the L3 <c>time</c> channel; 0 = paused).</summary>
    TimeScale,
}

/// <summary>
///     A set of <see cref="CameraChannel"/>s as bit flags — how a track sample declares which channels
///     it is driving, and how <see cref="CameraState"/> records which channels carry a live override.
/// </summary>
/// <remarks>
///     A bit set rather than a collection precisely so <see cref="CameraState.Compose"/> can run every
///     frame without allocating. Bit <c>n</c> is <see cref="CameraChannel"/> ordinal <c>n</c>.
/// </remarks>
[Flags]
public enum CameraChannelMask : uint
{
    /// <summary>No channels claimed.</summary>
    None = 0,

    /// <summary><see cref="CameraChannel.Position"/>.</summary>
    Position = 1u << 0,

    /// <summary><see cref="CameraChannel.Frame"/>.</summary>
    Frame = 1u << 1,

    /// <summary><see cref="CameraChannel.Anchor"/>.</summary>
    Anchor = 1u << 2,

    /// <summary><see cref="CameraChannel.Rotation"/>.</summary>
    Rotation = 1u << 3,

    /// <summary><see cref="CameraChannel.AimTarget"/>.</summary>
    AimTarget = 1u << 4,

    /// <summary><see cref="CameraChannel.AimOffset"/>.</summary>
    AimOffset = 1u << 5,

    /// <summary><see cref="CameraChannel.AimFrame"/>.</summary>
    AimFrame = 1u << 6,

    /// <summary><see cref="CameraChannel.AimUp"/>.</summary>
    AimUp = 1u << 7,

    /// <summary><see cref="CameraChannel.Roll"/>.</summary>
    Roll = 1u << 8,

    /// <summary><see cref="CameraChannel.Fov"/>.</summary>
    Fov = 1u << 9,

    /// <summary><see cref="CameraChannel.Ortho"/>.</summary>
    Ortho = 1u << 10,

    /// <summary><see cref="CameraChannel.OrthoHeight"/>.</summary>
    OrthoHeight = 1u << 11,

    /// <summary><see cref="CameraChannel.Smoothing"/>.</summary>
    Smoothing = 1u << 12,

    /// <summary><see cref="CameraChannel.OrbitRadius"/>.</summary>
    OrbitRadius = 1u << 13,

    /// <summary><see cref="CameraChannel.OrbitAzimuth"/>.</summary>
    OrbitAzimuth = 1u << 14,

    /// <summary><see cref="CameraChannel.OrbitElevation"/>.</summary>
    OrbitElevation = 1u << 15,

    /// <summary><see cref="CameraChannel.TimeScale"/>.</summary>
    TimeScale = 1u << 16,

    /// <summary>Every channel.</summary>
    All = (1u << 17) - 1,
}

/// <summary>Bit-twiddling helpers over <see cref="CameraChannelMask"/>.</summary>
public static class CameraChannels
{
    /// <summary>The number of distinct channels (and therefore of mask bits).</summary>
    public const int Count = 17;

    /// <summary>The single-bit mask for one channel.</summary>
    public static CameraChannelMask Mask(CameraChannel channel) => (CameraChannelMask)(1u << (int)channel);

    /// <summary>Whether <paramref name="mask"/> claims <paramref name="channel"/>.</summary>
    public static bool Has(this CameraChannelMask mask, CameraChannel channel)
        => (mask & Mask(channel)) != 0;
}

/// <summary>
///     A fully-resolved camera pose: every channel, no optionality. This is what the director applies
///     to the game camera and what the store publishes for read-back — so by the time a value reaches
///     here, "unset" is not a state that exists.
/// </summary>
/// <remarks>
///     <para>
///         <b>Position carries two spellings.</b> <see cref="PositionIsGeo"/> selects between the
///         Cartesian <see cref="Position"/> (in <see cref="Frame"/>, about <see cref="Anchor"/>) and the
///         geodetic <see cref="Latitude"/>/<see cref="Longitude"/>/<see cref="Altitude"/> triple (about
///         <see cref="Anchor"/>). They are one channel — writing either replaces the other — because
///         they answer the same question, and letting both be "live" would leave the director guessing.
///         The director resolves whichever is active and publishes <b>both</b> back, so a client can
///         read the geodetic form of a Cartesian placement and vice versa.
///     </para>
///     <para>
///         A <c>readonly record struct</c>: value equality (tests compare poses directly), no
///         allocation, and <c>with</c>-expression updates, which is how <see cref="CameraState.Compose"/>
///         builds its result without touching the heap.
///     </para>
/// </remarks>
public readonly record struct CameraPose
{
    /// <summary>Cartesian position in <see cref="Frame"/>, metres (used when <see cref="PositionIsGeo"/> is false).</summary>
    public Vec3 Position { get; init; }

    /// <summary>Whether the geodetic triple, rather than <see cref="Position"/>, is the authored placement.</summary>
    public bool PositionIsGeo { get; init; }

    /// <summary>Geodetic latitude in degrees, <c>[-90, 90]</c>.</summary>
    public double Latitude { get; init; }

    /// <summary>Geodetic longitude in degrees, canonical <c>[-180, 180)</c>.</summary>
    public double Longitude { get; init; }

    /// <summary>Altitude above the anchor's reference surface, metres.</summary>
    public double Altitude { get; init; }

    /// <summary>The frame <see cref="Position"/> is expressed in.</summary>
    public FrameKind Frame { get; init; }

    /// <summary>What the frames resolve against.</summary>
    public TargetRef Anchor { get; init; }

    /// <summary>Explicit ECL orientation, used when <see cref="AimTarget"/> names nothing.</summary>
    public Quat Rotation { get; init; }

    /// <summary>What the camera looks at; <see cref="TargetRef.None"/> ⇒ use <see cref="Rotation"/>.</summary>
    public TargetRef AimTarget { get; init; }

    /// <summary>Offset from the aim target, expressed in <see cref="AimFrame"/>, metres.</summary>
    public Vec3 AimOffset { get; init; }

    /// <summary>The frame <see cref="AimOffset"/> resolves in.</summary>
    public FrameKind AimFrame { get; init; }

    /// <summary>Where "up" is taken from once forward is fixed.</summary>
    public AimUpKind AimUp { get; init; }

    /// <summary>Roll about the view axis in degrees, applied after aim.</summary>
    public double Roll { get; init; }

    /// <summary>Vertical field of view, degrees.</summary>
    public double Fov { get; init; }

    /// <summary>Whether the projection is orthographic.</summary>
    public bool Ortho { get; init; }

    /// <summary>Orthographic half-height, metres (ignored while <see cref="Ortho"/> is false).</summary>
    public double OrthoHeight { get; init; }

    /// <summary>Critically-damped follow time in seconds; 0 = raw.</summary>
    public double Smoothing { get; init; }

    /// <summary>Spherical placement about the anchor: radius, metres.</summary>
    public double OrbitRadius { get; init; }

    /// <summary>Spherical placement about the anchor: azimuth, degrees.</summary>
    public double OrbitAzimuth { get; init; }

    /// <summary>Spherical placement about the anchor: elevation, degrees.</summary>
    public double OrbitElevation { get; init; }

    /// <summary>Simulation speed factor; 1 = normal, 0 = paused.</summary>
    public double TimeScale { get; init; }

    /// <summary>
    ///     A neutral pose: origin, ecliptic frame, no anchor, identity rotation, no aim, 60° FOV,
    ///     perspective, unsmoothed, normal time. It is what the composer falls back to before the
    ///     director has ever captured a baseline — never a value the game produced.
    /// </summary>
    public static CameraPose Default => new()
    {
        Position = Vec3.Zero,
        PositionIsGeo = false,
        Latitude = 0,
        Longitude = 0,
        Altitude = 0,
        Frame = FrameKind.Ecl,
        Anchor = TargetRef.None,
        Rotation = Quat.Identity,
        AimTarget = TargetRef.None,
        AimOffset = Vec3.Zero,
        AimFrame = FrameKind.BodyFixed,
        AimUp = AimUpKind.World,
        Roll = 0,
        Fov = 60,
        Ortho = false,
        OrthoHeight = 1000,
        Smoothing = 0,
        OrbitRadius = 0,
        OrbitAzimuth = 0,
        OrbitElevation = 0,
        TimeScale = 1,
    };
}

/// <summary>
///     The three-layer compositor of plans/CAMERA_CONTROLS_PLAN.md §4.3. Game-free, fully unit-tested,
///     and the single definition of "what the camera should look like this frame".
/// </summary>
/// <remarks>
///     <para><b>The three layers, in precedence order (last wins):</b></para>
///     <list type="number">
///         <item><b>Baseline</b> — captured from the live game camera at ownership take. Set once by the
///             director; never changes while gatOS owns the camera. It is what an unclaimed channel
///             falls back to, and it is why taking the camera is visually a no-op until something is
///             written.</item>
///         <item><b>Override</b> — every L1/L2 leaf write sets exactly one channel here. It persists
///             until <c>pose/reset</c> (<see cref="ClearOverrides"/>) or <c>camera/release</c>
///             (<see cref="ClearAll"/>).</item>
///         <item><b>Track</b> — only the channels the active shot declares, recomputed every frame by
///             the C3 evaluator and handed to <see cref="Compose"/> as a sample plus a claim mask.</item>
///     </list>
///     <para>
///         Effective value per channel = <c>Track ?? Override ?? Baseline</c>. Writing a channel a shot
///         is currently driving is <b>accepted but superseded on the next frame</b> — no error, no lock.
///         That is honest and self-explanatory; a "channel busy" errno would make an ordinary
///         <c>echo</c> fail for reasons the writer cannot see.
///     </para>
///     <para>
///         <b>Threading.</b> This object is mutated only on the game thread (via the command drain) and
///         read only on the game thread (the director). Transport threads never touch it — they read the
///         volatile status snapshot the director publishes into <c>CameraStore</c> instead. There are
///         therefore <b>no locks here on purpose</b>; adding one would only hide a rule violation.
///     </para>
/// </remarks>
public sealed class CameraState
{
    private CameraPose _baseline = CameraPose.Default;
    private CameraPose _overrides = CameraPose.Default;
    private CameraChannelMask _claimed;

    /// <summary>The captured game-camera pose every unclaimed channel falls back to.</summary>
    public CameraPose Baseline => _baseline;

    /// <summary>Which channels currently carry a live override.</summary>
    public CameraChannelMask Overrides => _claimed;

    /// <summary>Game thread: records the pose captured from the live camera at ownership take.</summary>
    /// <param name="baseline">The captured pose.</param>
    public void SetBaseline(in CameraPose baseline) => _baseline = baseline;

    /// <summary>Whether <paramref name="channel"/> currently carries a live override.</summary>
    public bool HasOverride(CameraChannel channel) => _claimed.Has(channel);

    // ---- typed override setters (one per payload shape) -------------------------------------------

    /// <summary>Sets a scalar channel's override.</summary>
    /// <param name="channel">
    ///     <see cref="CameraChannel.Roll"/>, <see cref="CameraChannel.Fov"/>,
    ///     <see cref="CameraChannel.OrthoHeight"/>, <see cref="CameraChannel.Smoothing"/>,
    ///     <see cref="CameraChannel.OrbitRadius"/>, <see cref="CameraChannel.OrbitAzimuth"/>,
    ///     <see cref="CameraChannel.OrbitElevation"/> or <see cref="CameraChannel.TimeScale"/>.
    /// </param>
    /// <param name="value">The value (already validated by <see cref="CameraRules"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException">The channel does not carry a scalar.</exception>
    public void SetOverride(CameraChannel channel, double value)
    {
        _overrides = channel switch
        {
            CameraChannel.Roll => _overrides with { Roll = value },
            CameraChannel.Fov => _overrides with { Fov = value },
            CameraChannel.OrthoHeight => _overrides with { OrthoHeight = value },
            CameraChannel.Smoothing => _overrides with { Smoothing = value },
            CameraChannel.OrbitRadius => _overrides with { OrbitRadius = value },
            CameraChannel.OrbitAzimuth => _overrides with { OrbitAzimuth = value },
            CameraChannel.OrbitElevation => _overrides with { OrbitElevation = value },
            CameraChannel.TimeScale => _overrides with { TimeScale = value },
            _ => throw Mismatch(channel, "a scalar"),
        };
        _claimed |= CameraChannels.Mask(channel);
    }

    /// <summary>Sets a boolean channel's override (<see cref="CameraChannel.Ortho"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The channel does not carry a flag.</exception>
    public void SetOverride(CameraChannel channel, bool value)
    {
        if (channel != CameraChannel.Ortho)
            throw Mismatch(channel, "a flag");
        _overrides = _overrides with { Ortho = value };
        _claimed |= CameraChannels.Mask(channel);
    }

    /// <summary>
    ///     Sets a vector channel's override: <see cref="CameraChannel.Position"/> (Cartesian — this
    ///     also clears the geodetic spelling) or <see cref="CameraChannel.AimOffset"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The channel does not carry a vector.</exception>
    public void SetOverride(CameraChannel channel, Vec3 value)
    {
        _overrides = channel switch
        {
            CameraChannel.Position => _overrides with { Position = value, PositionIsGeo = false },
            CameraChannel.AimOffset => _overrides with { AimOffset = value },
            _ => throw Mismatch(channel, "a vector"),
        };
        _claimed |= CameraChannels.Mask(channel);
    }

    /// <summary>Sets the rotation channel's override.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The channel is not <see cref="CameraChannel.Rotation"/>.</exception>
    public void SetOverride(CameraChannel channel, Quat value)
    {
        if (channel != CameraChannel.Rotation)
            throw Mismatch(channel, "a quaternion");
        _overrides = _overrides with { Rotation = value };
        _claimed |= CameraChannels.Mask(channel);
    }

    /// <summary>Sets a frame channel's override (<see cref="CameraChannel.Frame"/> or <see cref="CameraChannel.AimFrame"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The channel does not carry a frame.</exception>
    public void SetOverride(CameraChannel channel, FrameKind value)
    {
        _overrides = channel switch
        {
            CameraChannel.Frame => _overrides with { Frame = value },
            CameraChannel.AimFrame => _overrides with { AimFrame = value },
            _ => throw Mismatch(channel, "a frame"),
        };
        _claimed |= CameraChannels.Mask(channel);
    }

    /// <summary>Sets the aim-up channel's override.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The channel is not <see cref="CameraChannel.AimUp"/>.</exception>
    public void SetOverride(CameraChannel channel, AimUpKind value)
    {
        if (channel != CameraChannel.AimUp)
            throw Mismatch(channel, "an aim-up mode");
        _overrides = _overrides with { AimUp = value };
        _claimed |= CameraChannels.Mask(channel);
    }

    /// <summary>Sets a target-reference channel's override (<see cref="CameraChannel.Anchor"/> or <see cref="CameraChannel.AimTarget"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The channel does not carry a target reference.</exception>
    public void SetOverride(CameraChannel channel, TargetRef value)
    {
        _overrides = channel switch
        {
            CameraChannel.Anchor => _overrides with { Anchor = value },
            CameraChannel.AimTarget => _overrides with { AimTarget = value },
            _ => throw Mismatch(channel, "a target reference"),
        };
        _claimed |= CameraChannels.Mask(channel);
    }

    /// <summary>
    ///     Sets the <see cref="CameraChannel.Position"/> override in its geodetic spelling
    ///     (<c>pose/geo</c>): latitude/longitude/altitude about <paramref name="anchor"/>. Passing a
    ///     non-<see cref="TargetRef.None"/> anchor also claims <see cref="CameraChannel.Anchor"/> — the
    ///     optional <c>body:&lt;id&gt;</c> tail of the <c>geo</c> grammar; omit it (pass
    ///     <see cref="TargetRef.None"/>) to keep the current anchor.
    /// </summary>
    /// <param name="latitudeDeg">Latitude in degrees.</param>
    /// <param name="longitudeDeg">Longitude in degrees (normalized to <c>[-180, 180)</c>).</param>
    /// <param name="altitudeMetres">Altitude above the reference surface, metres.</param>
    /// <param name="anchor">The body to place against, or <see cref="TargetRef.None"/> to keep the current anchor.</param>
    public void SetGeoOverride(double latitudeDeg, double longitudeDeg, double altitudeMetres, TargetRef anchor)
    {
        _overrides = _overrides with
        {
            PositionIsGeo = true,
            Latitude = latitudeDeg,
            Longitude = CameraRules.NormalizeLongitude(longitudeDeg),
            Altitude = altitudeMetres,
        };
        _claimed |= CameraChannelMask.Position;
        if (!anchor.HasTarget)
            return;
        _overrides = _overrides with { Anchor = anchor };
        _claimed |= CameraChannelMask.Anchor;
    }

    // ---- lifecycle ---------------------------------------------------------------------------------

    /// <summary>
    ///     <c>pose/reset</c>: drops every live override, so every channel falls back to the baseline
    ///     (or to the active track, which is untouched — a reset is about <i>your</i> writes, not about
    ///     stopping playback).
    /// </summary>
    public void ClearOverrides()
    {
        _claimed = CameraChannelMask.None;
        _overrides = CameraPose.Default;
    }

    /// <summary>
    ///     <c>camera/release</c>: drops the overrides <i>and</i> the captured baseline, returning the
    ///     compositor to its pristine state so the next ownership take starts from a clean capture
    ///     rather than the last session's.
    /// </summary>
    public void ClearAll()
    {
        ClearOverrides();
        _baseline = CameraPose.Default;
    }

    // ---- composition --------------------------------------------------------------------------------

    /// <summary>
    ///     Composes the effective pose for this frame: per channel,
    ///     <c>Track ?? Override ?? Baseline</c>.
    /// </summary>
    /// <param name="trackSample">
    ///     The active shot's evaluated sample, or null when nothing is playing. Only the channels named
    ///     by <paramref name="trackChannels"/> are read from it — the rest of the sample is ignored, so
    ///     the evaluator may leave them at whatever default it likes.
    /// </param>
    /// <param name="trackChannels">Which channels the active shot declares. Ignored when the sample is null.</param>
    /// <returns>The fully-resolved pose to apply.</returns>
    /// <remarks>Allocates nothing: every input and the result are structs, and there is no iteration state.</remarks>
    public CameraPose Compose(in CameraPose? trackSample, CameraChannelMask trackChannels)
    {
        var track = trackSample ?? default;
        var claims = trackSample.HasValue ? trackChannels : CameraChannelMask.None;

        // Position moves as one unit (Cartesian spelling + geodetic spelling + the "which is live"
        // bit), because they are two ways of authoring the same channel.
        var position = Pick(CameraChannel.Position, claims);
        var frame = Pick(CameraChannel.Frame, claims);
        var anchor = Pick(CameraChannel.Anchor, claims);

        return new CameraPose
        {
            Position = position switch
            {
                Layer.Track => track.Position,
                Layer.Override => _overrides.Position,
                _ => _baseline.Position,
            },
            PositionIsGeo = position switch
            {
                Layer.Track => track.PositionIsGeo,
                Layer.Override => _overrides.PositionIsGeo,
                _ => _baseline.PositionIsGeo,
            },
            Latitude = position switch
            {
                Layer.Track => track.Latitude,
                Layer.Override => _overrides.Latitude,
                _ => _baseline.Latitude,
            },
            Longitude = position switch
            {
                Layer.Track => track.Longitude,
                Layer.Override => _overrides.Longitude,
                _ => _baseline.Longitude,
            },
            Altitude = position switch
            {
                Layer.Track => track.Altitude,
                Layer.Override => _overrides.Altitude,
                _ => _baseline.Altitude,
            },
            Frame = frame switch
            {
                Layer.Track => track.Frame,
                Layer.Override => _overrides.Frame,
                _ => _baseline.Frame,
            },
            Anchor = anchor switch
            {
                Layer.Track => track.Anchor,
                Layer.Override => _overrides.Anchor,
                _ => _baseline.Anchor,
            },
            Rotation = Pick(CameraChannel.Rotation, claims) switch
            {
                Layer.Track => track.Rotation,
                Layer.Override => _overrides.Rotation,
                _ => _baseline.Rotation,
            },
            AimTarget = Pick(CameraChannel.AimTarget, claims) switch
            {
                Layer.Track => track.AimTarget,
                Layer.Override => _overrides.AimTarget,
                _ => _baseline.AimTarget,
            },
            AimOffset = Pick(CameraChannel.AimOffset, claims) switch
            {
                Layer.Track => track.AimOffset,
                Layer.Override => _overrides.AimOffset,
                _ => _baseline.AimOffset,
            },
            AimFrame = Pick(CameraChannel.AimFrame, claims) switch
            {
                Layer.Track => track.AimFrame,
                Layer.Override => _overrides.AimFrame,
                _ => _baseline.AimFrame,
            },
            AimUp = Pick(CameraChannel.AimUp, claims) switch
            {
                Layer.Track => track.AimUp,
                Layer.Override => _overrides.AimUp,
                _ => _baseline.AimUp,
            },
            Roll = Pick(CameraChannel.Roll, claims) switch
            {
                Layer.Track => track.Roll,
                Layer.Override => _overrides.Roll,
                _ => _baseline.Roll,
            },
            Fov = Pick(CameraChannel.Fov, claims) switch
            {
                Layer.Track => track.Fov,
                Layer.Override => _overrides.Fov,
                _ => _baseline.Fov,
            },
            Ortho = Pick(CameraChannel.Ortho, claims) switch
            {
                Layer.Track => track.Ortho,
                Layer.Override => _overrides.Ortho,
                _ => _baseline.Ortho,
            },
            OrthoHeight = Pick(CameraChannel.OrthoHeight, claims) switch
            {
                Layer.Track => track.OrthoHeight,
                Layer.Override => _overrides.OrthoHeight,
                _ => _baseline.OrthoHeight,
            },
            Smoothing = Pick(CameraChannel.Smoothing, claims) switch
            {
                Layer.Track => track.Smoothing,
                Layer.Override => _overrides.Smoothing,
                _ => _baseline.Smoothing,
            },
            OrbitRadius = Pick(CameraChannel.OrbitRadius, claims) switch
            {
                Layer.Track => track.OrbitRadius,
                Layer.Override => _overrides.OrbitRadius,
                _ => _baseline.OrbitRadius,
            },
            OrbitAzimuth = Pick(CameraChannel.OrbitAzimuth, claims) switch
            {
                Layer.Track => track.OrbitAzimuth,
                Layer.Override => _overrides.OrbitAzimuth,
                _ => _baseline.OrbitAzimuth,
            },
            OrbitElevation = Pick(CameraChannel.OrbitElevation, claims) switch
            {
                Layer.Track => track.OrbitElevation,
                Layer.Override => _overrides.OrbitElevation,
                _ => _baseline.OrbitElevation,
            },
            TimeScale = Pick(CameraChannel.TimeScale, claims) switch
            {
                Layer.Track => track.TimeScale,
                Layer.Override => _overrides.TimeScale,
                _ => _baseline.TimeScale,
            },
        };
    }

    private Layer Pick(CameraChannel channel, CameraChannelMask trackClaims)
    {
        var bit = CameraChannels.Mask(channel);
        if ((trackClaims & bit) != 0)
            return Layer.Track;
        return (_claimed & bit) != 0 ? Layer.Override : Layer.Baseline;
    }

    private static ArgumentOutOfRangeException Mismatch(CameraChannel channel, string shape)
        => new(nameof(channel), channel, $"camera channel '{channel}' does not carry {shape}");

    private enum Layer
    {
        Baseline,
        Override,
        Track,
    }
}
