using System.Globalization;

namespace gatOS.SimFs.Camera;

/// <summary>
///     The <c>/sim</c> text projection of <see cref="CameraStatus"/> — every camera leaf's read-back,
///     in one place.
/// </summary>
/// <remarks>
///     <para>
///         It lives here rather than in <c>Formats</c> because the camera surface has an unusual amount
///         of it (a composite line per channel family) and because these renderings carry a hard
///         obligation: <b>every composite read-back must re-parse</b> through its own
///         <see cref="CameraCommands"/> grammar. That is what makes "read a leaf, write it back" a
///         no-op and lets a client resync after a restart (AGENTS.md §7). The unit tests assert the
///         round trip.
///     </para>
///     <para>
///         Numbers go through <c>Formats.Scalar</c> (invariant <c>G9</c>) so the whole <c>/sim</c>
///         surface reads the same way.
///     </para>
/// </remarks>
public static class CameraFormat
{
    /// <summary>Rendered in place of an absent name/target, so a field is never empty on the wire.</summary>
    public const string Absent = "-";

    /// <summary><c>camera/target</c>: the follow target's bare id, or <c>-</c>.</summary>
    public static string FollowId(in CameraStatus status)
        => status.Follow.HasTarget ? status.Follow.Id : Absent;

    /// <summary><c>pose/position</c>: <c>"x y z &lt;frame&gt;"</c> — re-parses as a <c>position</c> line.</summary>
    public static string Position(in CameraPose pose)
        => $"{Formats.Scalar(pose.Position.X)} {Formats.Scalar(pose.Position.Y)} "
           + $"{Formats.Scalar(pose.Position.Z)} {Frame(pose.Frame)}";

    /// <summary>
    ///     <c>pose/geo</c>: <c>"lat lon alt [body:&lt;id&gt;]"</c> — re-parses as a <c>geo</c> line. The
    ///     body tail is emitted only when the anchor <i>is</i> a body, because a <c>vessel:</c> or
    ///     <c>none</c> tail would not re-parse; the placement then means "against the current anchor",
    ///     which is exactly what a bare triple says.
    /// </summary>
    public static string Geo(in CameraPose pose)
    {
        var head = $"{Formats.Scalar(pose.Latitude)} {Formats.Scalar(pose.Longitude)} "
                   + Formats.Scalar(pose.Altitude);
        return pose.Anchor.Kind == TargetKind.Body ? head + " " + pose.Anchor : head;
    }

    /// <summary>
    ///     <c>pose/aim</c>: <c>"&lt;target&gt; off x y z frame &lt;f&gt; up &lt;u&gt; roll &lt;deg&gt;"</c>
    ///     — re-parses as an <c>aim</c> line, roll included (an explicit roll is never wrong to restate).
    /// </summary>
    public static string Aim(in CameraPose pose)
        => $"{pose.AimTarget} off {Formats.Scalar(pose.AimOffset.X)} {Formats.Scalar(pose.AimOffset.Y)} "
           + $"{Formats.Scalar(pose.AimOffset.Z)} frame {Frame(pose.AimFrame)} up {Up(pose.AimUp)} "
           + $"roll {Formats.Scalar(pose.Roll)}";

    /// <summary><c>camera/play</c>: the loaded track's name, or <c>-</c> when nothing is loaded.</summary>
    public static string Play(in CameraStatus status)
        => status.TrackName.Length > 0 ? status.TrackName : Absent;

    /// <summary>
    ///     <c>camera/set</c>: <c>"t &lt;sec&gt; rate &lt;x&gt; loop 0|1 paused 0|1"</c> — the running
    ///     player's live settings, and a valid <c>set</c> line.
    /// </summary>
    public static string Set(in CameraStatus status)
        => $"t {Formats.Scalar(status.TrackTMs / 1000.0)} rate {Formats.Scalar(status.Rate)} "
           + $"loop {(status.Loop ? 1 : 0)} paused {(status.Playback == Commands.PlaybackState.Paused ? 1 : 0)}";

    /// <summary>
    ///     <c>camera/playback</c>: <c>"&lt;state&gt; &lt;t_ms&gt; &lt;duration_ms&gt; &lt;shot&gt;
    ///     &lt;index&gt; &lt;rate&gt; &lt;loop&gt;"</c>. Times are ms with one decimal, matching
    ///     <c>ctl/schedules/&lt;id&gt;/t</c> — one player vocabulary, one formatting.
    /// </summary>
    public static string Playback(in CameraStatus status)
        => $"{status.Playback.ToString().ToLowerInvariant()} "
           + $"{status.TrackTMs.ToString("F1", CultureInfo.InvariantCulture)} "
           + $"{status.TrackDurationMs.ToString("F1", CultureInfo.InvariantCulture)} "
           + $"{(status.ShotName.Length > 0 ? status.ShotName : Absent)} "
           + $"{status.ShotIndex.ToString(CultureInfo.InvariantCulture)} "
           + $"{Formats.Scalar(status.Rate)} {(status.Loop ? 1 : 0)}";

    /// <summary>
    ///     <c>camera/status</c>: the whole camera state as one <c>key value…</c> line per field —
    ///     the "what is the camera doing right now" read that does not need eighteen <c>cat</c>s.
    /// </summary>
    public static string Status(in CameraStatus status)
    {
        var pose = status.Pose;
        return $"owned {(status.Owned ? 1 : 0)}\n"
               + $"mode {Mode(status.Mode)}\n"
               + $"follow {status.Follow}\n"
               + $"tidal {(status.Tidal ? 1 : 0)}\n"
               + $"map_scope {Formats.Scalar(status.MapScope)}\n"
               + $"anchor {pose.Anchor}\n"
               + $"frame {Frame(pose.Frame)}\n"
               + $"position {Formats.Scalar(pose.Position.X)} {Formats.Scalar(pose.Position.Y)} "
               + $"{Formats.Scalar(pose.Position.Z)}\n"
               + $"geo {Formats.Scalar(pose.Latitude)} {Formats.Scalar(pose.Longitude)} "
               + $"{Formats.Scalar(pose.Altitude)} {(pose.PositionIsGeo ? 1 : 0)}\n"
               + $"rotation {Formats.Scalar(pose.Rotation.X)} {Formats.Scalar(pose.Rotation.Y)} "
               + $"{Formats.Scalar(pose.Rotation.Z)} {Formats.Scalar(pose.Rotation.W)}\n"
               + $"applied_position_ecl {Formats.Scalar(status.AppliedPositionEcl.X)} "
               + $"{Formats.Scalar(status.AppliedPositionEcl.Y)} {Formats.Scalar(status.AppliedPositionEcl.Z)}\n"
               + $"applied_rotation {Formats.Scalar(status.AppliedRotation.X)} "
               + $"{Formats.Scalar(status.AppliedRotation.Y)} {Formats.Scalar(status.AppliedRotation.Z)} "
               + $"{Formats.Scalar(status.AppliedRotation.W)}\n"
               + $"aim {Aim(pose)}\n"
               + $"fov {Formats.Scalar(pose.Fov)}\n"
               + $"ortho {(pose.Ortho ? 1 : 0)} {Formats.Scalar(pose.OrthoHeight)}\n"
               + $"smoothing {Formats.Scalar(pose.Smoothing)}\n"
               + $"orbit {Formats.Scalar(pose.OrbitRadius)} {Formats.Scalar(pose.OrbitAzimuth)} "
               + $"{Formats.Scalar(pose.OrbitElevation)}\n"
               + $"time_scale {Formats.Scalar(pose.TimeScale)}\n";
    }

    /// <summary>
    ///     <c>camera/info</c>: one line of live usage, the caps, and the token vocabularies — enough to
    ///     write a correct <c>aim</c>/<c>position</c> line without opening the SPEC.
    /// </summary>
    public static string Info(CameraStore store)
    {
        var (tracks, bytes) = store.Usage();
        var limits = store.Limits;
        return $"enabled=1 owned={(store.Status.Owned ? 1 : 0)} "
               + $"tracks={tracks.ToString(CultureInfo.InvariantCulture)} "
               + $"tracks_max={limits.MaxTracks.ToString(CultureInfo.InvariantCulture)} "
               + $"bytes={bytes.ToString(CultureInfo.InvariantCulture)} "
               + $"bytes_max={limits.MaxTotalBytes.ToString(CultureInfo.InvariantCulture)} "
               + $"track_bytes_max={limits.MaxTrackBytes.ToString(CultureInfo.InvariantCulture)} "
               + $"keys_max={limits.MaxKeys.ToString(CultureInfo.InvariantCulture)} "
               + $"fov_min={Formats.Scalar(limits.FovMin)} fov_max={Formats.Scalar(limits.FovMax)} "
               + $"frames={string.Join(',', CameraRules.FrameTokens)} "
               + $"modes={string.Join(',', CameraRules.ModeTokens)} "
               + $"up={string.Join(',', CameraRules.AimUpTokens)}";
    }

    /// <summary>The canonical frame token (never null — an out-of-range value renders as <c>ecl</c>).</summary>
    public static string Frame(FrameKind frame) => CameraRules.NameOf(frame) ?? CameraRules.FrameTokens[0];

    /// <summary>The canonical aim-up token (never null — an out-of-range value renders as <c>world</c>).</summary>
    public static string Up(AimUpKind up) => CameraRules.NameOf(up) ?? CameraRules.AimUpTokens[0];

    /// <summary>The canonical camera-mode token (never null — an out-of-range value renders as <c>orbit</c>).</summary>
    public static string Mode(CameraModeKind mode) => CameraRules.NameOf(mode) ?? CameraRules.ModeTokens[0];
}
